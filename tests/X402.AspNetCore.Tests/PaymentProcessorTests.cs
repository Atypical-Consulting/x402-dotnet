using System.Net;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.Idempotency;
using X402.Assets;
using X402.Billing;
using X402.Protocol;
using X402.TestKit;
using X402.Transport;

namespace X402.AspNetCore.Tests;

public sealed class PaymentProcessorTests : IAsyncLifetime
{
    private PaidServerFixture server = null!;

    public async ValueTask InitializeAsync() => server = await PaidServerFixture.StartAsync();

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    [Fact]
    public async Task An_unpaid_request_gets_402_with_a_decodable_demand()
    {
        var response = await server.Client.GetAsync("/premium", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        var header = response.Headers.GetValues(X402Headers.PaymentRequired).Single();
        X402Codec.TryDecode<PaymentRequired>(header, out var demand, out var error).ShouldBeTrue(error);

        demand!.X402Version.ShouldBe(2);
        demand.Resource.Url.ShouldEndWith("/premium");
        demand.Accepts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_demand_announces_the_euro_first()
    {
        var response = await server.Client.GetAsync("/premium", TestContext.Current.CancellationToken);
        var demand = server.DecodeDemand(response);

        // The PriceSet order is the announced order: a commercial promise.
        demand.Accepts[0].Asset.ShouldBe(KnownAssets.EurcBaseSepolia.Address);
        demand.Accepts[0].Amount.ShouldBe("10000");
        demand.Accepts[1].Asset.ShouldBe(KnownAssets.UsdcBaseSepolia.Address);
        demand.Accepts[1].Amount.ShouldBe("11000");
    }

    [Fact]
    public async Task The_demand_carries_the_eip712_domain_of_each_asset()
    {
        var demand = server.DecodeDemand(
            await server.Client.GetAsync("/premium", TestContext.Current.CancellationToken));

        foreach (var requirement in demand.Accepts)
        {
            // Resolved from KnownAssets, not hard-coded: this asserts the EXACT domain the
            // catalogue declares for this asset, not merely "some non-empty name" — a regression
            // that emits the wrong domain (e.g. "USDC" where mainnet's own contract requires "USD
            // Coin") would make the resulting 402 unpayable by any third-party client that reads
            // `extra`, even though this server, our own client and the fake facilitator never
            // consult it themselves (see PaymentProcessorTests remarks below).
            var asset = KnownAssets.ForNetwork(requirement.Network)
                .Single(a => EvmAddress.AreEqual(a.Address, requirement.Asset));

            requirement.Extra.ShouldNotBeNull();
            var extra = requirement.Extra!.Value;
            extra.GetProperty("name").GetString().ShouldBe(asset.Eip712Name);
            extra.GetProperty("version").GetString().ShouldBe(asset.Eip712Version);
        }
    }

    [Fact]
    public async Task The_402_body_is_an_empty_object()
    {
        var response = await server.Client.GetAsync("/premium", TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Trim().ShouldBe("{}");
    }

    [Fact]
    public async Task A_valid_payment_in_euros_reaches_the_endpoint()
    {
        var response = await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("premium content");
    }

    [Fact]
    public async Task A_valid_payment_in_dollars_also_reaches_the_endpoint()
    {
        var response = await server.PayAsync("/premium", KnownAssets.UsdcBaseSepolia);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_settled_response_carries_the_settlement_header()
    {
        var response = await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        var header = response.Headers.GetValues(X402Headers.PaymentResponse).Single();
        X402Codec.TryDecode<SettleResponse>(header, out var settle, out _).ShouldBeTrue();
        settle!.Success.ShouldBeTrue();
        settle.Transaction.ShouldStartWith("0x");
    }

    [Fact]
    public async Task An_invalid_payment_gets_402_with_the_reason_in_the_demand()
    {
        server.Facilitator.Scenario = FakeFacilitatorScenario.InsufficientFunds;

        var response = await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        server.DecodeDemand(response).Error!.ShouldContain(X402ErrorReason.InsufficientFunds);
    }

    [Fact]
    public async Task A_payment_naming_an_asset_the_server_does_not_offer_never_reaches_the_facilitator()
    {
        // The match fails locally: no need to bother the facilitator, and above all it must never
        // be sent a requirement the server never issued.
        var response = await server.PayWithForeignAssetAsync("/premium");

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        server.Facilitator.VerifyCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_requirement_sent_to_the_facilitator_is_the_server_s_own()
    {
        // The payer genuinely signs a reduced amount, but `accepted` still names the real
        // scheme/network/asset: the engine matches the server's real (unmodified) requirement and
        // sends THAT to the facilitator, so the signed authorization no longer matches it.
        var response = await server.PayWithTamperedAmountAsync("/premium", "1");

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        server.Facilitator.LastRequestBody!.ShouldContain("\"amount\":\"10000\"");
    }

    [Fact]
    public async Task A_tampered_payee_is_refused()
    {
        var response = await server.PayWithTamperedPayeeAsync(
            "/premium", "0x0000000000000000000000000000000000000001");

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
    }

    [Fact]
    public async Task A_failed_settlement_withholds_the_content()
    {
        server.Facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;

        var response = await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var text = System.Text.Encoding.UTF8.GetString(body);
        text.ShouldNotContain("premium content");
        response.Headers.Contains(X402Headers.PaymentResponse).ShouldBeTrue();

        // /premium's own success shape (Results.Text("premium content")) declares
        // Content-Length: 15 up front. FinishAsync's settlement-failed branch discards that body
        // and writes the 2-byte {} PaymentRequiredResult instead: TestServer tolerates a stale
        // Content-Length surviving that rewrite, but a real transport (Kestrel) aborts the response
        // trying to satisfy it — so assert the header itself, not just status and body, or this
        // test would stay green with the bug present.
        var declaredLength = response.Content.Headers.ContentLength;
        (declaredLength is null || declaredLength == body.Length).ShouldBeTrue(
            $"Content-Length was {declaredLength}, but the refusal body is {body.Length} bytes.");
    }

    [Fact]
    public async Task The_same_authorization_presented_twice_settles_once()
    {
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);

        var first = await server.SendAsync("/premium", payload);
        var second = await server.SendAsync("/premium", payload);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        server.Facilitator.HasDoubleSettled.ShouldBeFalse();
        server.Facilitator.SettleCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task An_authorization_being_settled_elsewhere_gets_409()
    {
        // No genuine race needed: the ledger is resolvable from the same container the engine
        // settles through, and the identity AuthorizeAsync computes is deterministic from the
        // payload. Seed an in-flight lease for it directly, then present the same authorization.
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        var authorization = payload.AsExactEvm().Authorization;
        var identity = new PaymentIdentity(
            payload.Accepted.Network, payload.Accepted.Asset, authorization.Nonce);
        await server.Ledger.AcquireAsync(identity, TestContext.Current.CancellationToken);

        var response = await server.SendAsync("/premium", payload);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_free_route_is_untouched()
    {
        var response = await server.Client.GetAsync("/free", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains(X402Headers.PaymentRequired).ShouldBeFalse();
    }

    [Theory]
    [InlineData(PaymentEventStatus.PaymentRequired)]
    [InlineData(PaymentEventStatus.Verified)]
    [InlineData(PaymentEventStatus.Settled)]
    [InlineData(PaymentEventStatus.VerificationFailed)]
    [InlineData(PaymentEventStatus.SettlementFailed)]
    public async Task Every_terminal_branch_records_a_payment_event(PaymentEventStatus expected)
    {
        server.Facilitator.Scenario = expected switch
        {
            PaymentEventStatus.VerificationFailed => FakeFacilitatorScenario.InsufficientFunds,
            PaymentEventStatus.SettlementFailed => FakeFacilitatorScenario.SettleFailure,
            _ => FakeFacilitatorScenario.Valid,
        };

        if (expected == PaymentEventStatus.PaymentRequired)
        {
            await server.Client.GetAsync("/premium", TestContext.Current.CancellationToken);
        }
        else
        {
            await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);
        }

        server.Events.ShouldContain(e => e.Status == expected);
    }

    [Fact]
    public async Task A_settled_event_carries_the_transaction_hash()
    {
        await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        server.Events.Single(e => e.Status == PaymentEventStatus.Settled)
            .Transaction.ShouldStartWith("0x");
    }

    [Fact]
    public async Task A_mismatched_payment_records_no_client_supplied_payer()
    {
        await server.PayWithForeignAssetAsync("/premium");

        server.Events.Single(e => e.Status == PaymentEventStatus.VerificationFailed).Payer.ShouldBeNull();
    }

    [Fact]
    public async Task A_settlement_failure_tells_the_payer_why()
    {
        server.Facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;

        var response = await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        server.DecodeDemand(response).Error.ShouldBe(X402ErrorReason.InvalidTransactionState);
    }

    [Fact]
    public async Task An_error_response_from_a_paid_route_is_not_charged()
    {
        var response = await server.PayAsync("/not-found", KnownAssets.EurcBaseSepolia);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        server.Facilitator.SettleCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_settled_authorization_does_not_unlock_another_route_at_the_same_price()
    {
        // /large carries exactly /premium's prices. The EIP-3009 signature does not cover the
        // resource, so only the ledger can stop one payment from opening every such route.
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        (await server.SendAsync("/premium", payload)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var replay = await server.SendAsync("/large", payload);

        replay.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
    }

    [Fact]
    public async Task A_settled_authorization_does_not_unlock_the_same_route_with_another_query()
    {
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        (await server.SendAsync("/premium?id=1", payload)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var replay = await server.SendAsync("/premium?id=2", payload);

        replay.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
    }

    [Fact]
    public async Task An_authorization_valid_beyond_MaxTimeoutSeconds_is_refused_before_verification()
    {
        var demand = server.DecodeDemand(
            await server.Client.GetAsync("/premium", TestContext.Current.CancellationToken));
        var payload = await TestData.SignedPayloadAsync(
            demand.Accepts[0], validBefore: DateTimeOffset.UtcNow.AddDays(1));

        await server.SendAsync("/premium", payload);

        server.Facilitator.VerifyCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_client_gone_before_settlement_is_not_charged()
    {
        var payload = await server.SignFor("/slow", KnownAssets.EurcBaseSepolia);
        using (var abandon = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/slow");
            request.Headers.TryAddWithoutValidation(X402Headers.PaymentSignature, X402Codec.Encode(payload));
            await Should.ThrowAsync<OperationCanceledException>(
                () => server.Client.SendAsync(request, abandon.Token));
        }

        // BufferedRequestCount moves once the whole pipeline, FinishAsync included, has returned.
        for (var i = 0; i < 100 && server.BufferedRequestCount == 0; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        server.Facilitator.SettleCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_settlement_presented_again_is_refused_without_running_the_endpoint()
    {
        server.Facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        await server.SendAsync("/premium", payload);

        var replay = await server.SendAsync("/premium", payload);

        replay.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        server.BufferedRequestCount.ShouldBe(1); // only the first presentation reached the endpoint
    }

    [Fact]
    public async Task A_nonce_written_in_another_hex_case_is_the_same_authorization()
    {
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        await server.SendAsync("/premium", payload);
        var exact = payload.AsExactEvm();
        var shouted = payload.WithExactEvm(exact with
        {
            Authorization = exact.Authorization with
            {
                Nonce = "0x" + exact.Authorization.Nonce[2..].ToUpperInvariant(),
            },
        });

        await server.SendAsync("/premium", shouted);

        server.Facilitator.SettleCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_nonce_that_is_not_a_bytes32_is_refused_before_verification()
    {
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        var exact = payload.AsExactEvm();
        var malformed = payload.WithExactEvm(exact with
        {
            Authorization = exact.Authorization with { Nonce = "0x1234" },
        });

        var response = await server.SendAsync("/premium", malformed);

        server.DecodeDemand(response).Error.ShouldBe(X402ErrorReason.InvalidPayload);
        server.Facilitator.VerifyCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("""{"x402Version":2,"accepted":null,"payload":{}}""")]
    [InlineData("""{"x402Version":2,"accepted":{"scheme":"exact","network":"eip155:84532","amount":"10000","asset":"0x808456652fdb597867f38412077A9182bf77359F","payTo":"0x209693Bc6afc0C5328bA36FaF03C514EF312287C","maxTimeoutSeconds":60},"payload":42}""")]
    [InlineData("""{"x402Version":2,"accepted":{"scheme":"exact","network":"eip155:84532","amount":"10000","asset":"0x808456652fdb597867f38412077A9182bf77359F","payTo":"0x209693Bc6afc0C5328bA36FaF03C514EF312287C","maxTimeoutSeconds":60},"payload":{"signature":"0x00","authorization":null}}""")]
    public async Task A_structurally_broken_payment_gets_402_without_reaching_the_facilitator(string json)
    {
        // Unauthenticated input: a null where the protocol requires a value must be a refusal,
        // not a NullReferenceException turned into a 500 and an error log entry.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/premium");
        request.Headers.TryAddWithoutValidation(
            X402Headers.PaymentSignature, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)));

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        server.Facilitator.VerifyCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_settlement_that_cannot_reach_the_facilitator_releases_the_authorization_for_retry()
    {
        // settle is attempted twice (one retry) on a transport failure; both fail here.
        server.Facilitator.FailNextSettleCalls(2, FakeFacilitatorScenario.NetworkFailure);
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        var first = await server.SendAsync("/premium", payload);
        server.DecodeDemand(first).Error.ShouldBe(X402ErrorReason.UnexpectedSettleError);

        var retry = await server.SendAsync("/premium", payload);

        retry.StatusCode.ShouldBe(HttpStatusCode.OK); // not 409: the slot was abandoned
    }

    [Fact]
    public async Task An_unreachable_facilitator_during_verify_gets_402_and_never_settles()
    {
        // verify is attempted three times (two retries) on a transport failure.
        server.Facilitator.FailNextCalls(3, FakeFacilitatorScenario.NetworkFailure);

        var response = await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        server.DecodeDemand(response).Error.ShouldBe(X402ErrorReason.UnexpectedVerifyError);
        server.Facilitator.SettleCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_client_disconnecting_mid_settlement_does_not_strand_the_authorization()
    {
        // The first settle attempt hangs past its 1s attempt timeout; the client gives up first.
        // Settlement must carry on regardless — its retry succeeds — and be recorded, rather than
        // being cancelled with the connection and left in flight (every retry a 409) for an hour.
        await using var slow = await PaidServerFixture.StartAsync(options => options.MaxTimeoutSeconds = 1);
        slow.Facilitator.TimeoutDelay = TimeSpan.FromSeconds(5);
        slow.Facilitator.FailNextSettleCalls(1, FakeFacilitatorScenario.Timeout);
        var demand = slow.DecodeDemand(
            await slow.Client.GetAsync("/premium", TestContext.Current.CancellationToken));
        var payload = await TestData.SignedPayloadAsync(
            demand.Accepts[0], validBefore: DateTimeOffset.UtcNow.AddSeconds(30));

        using (var abandon = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/premium");
            request.Headers.TryAddWithoutValidation(X402Headers.PaymentSignature, X402Codec.Encode(payload));
            await Should.ThrowAsync<OperationCanceledException>(
                () => slow.Client.SendAsync(request, abandon.Token));
        }

        for (var i = 0; i < 100 && !slow.Events.Any(e => e.Status == PaymentEventStatus.Settled); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        var retry = await slow.SendAsync("/premium", payload);

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_verification_failure_is_recorded()
    {
        server.Facilitator.Scenario = FakeFacilitatorScenario.InsufficientFunds;

        await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        var recorded = server.Events.Single(e => e.Status == PaymentEventStatus.VerificationFailed);
        recorded.FailureReason.ShouldBe(X402ErrorReason.InsufficientFunds);
        recorded.Asset.ShouldBe(KnownAssets.EurcBaseSepolia.Address);
    }

    [Fact]
    public async Task A_settlement_failure_is_recorded()
    {
        server.Facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;

        await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        server.Events.ShouldContain(e => e.Status == PaymentEventStatus.SettlementFailed);
    }

    [Fact]
    public async Task A_recorded_event_names_the_resource_url()
    {
        // Corrects the brief's own RecordAsync sketch, which wrote Resource = requirements.PayTo —
        // the payee address, not the resource that was paid for.
        await server.Client.GetAsync("/premium", TestContext.Current.CancellationToken);

        var recorded = server.Events.Single(e => e.Status == PaymentEventStatus.PaymentRequired);
        recorded.Resource.ShouldEndWith("/premium");
        recorded.Resource.ShouldNotBe(KnownAssets.EurcBaseSepolia.Address);
    }

    [Fact]
    public async Task A_throwing_event_sink_does_not_fail_a_settled_payment()
    {
        await using var throwing = await PaidServerFixture.StartAsync(sinkThrows: true);

        var response = await throwing.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        // Billing must never undo a payment that already settled on-chain.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        throwing.LoggedErrors.ShouldContain(message => message.Contains("payment event sink threw"));
    }

    [Fact]
    public async Task A_malformed_nonce_is_recorded_as_an_invalid_payload()
    {
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        var exact = payload.AsExactEvm();
        await server.SendAsync("/premium", payload.WithExactEvm(exact with
        {
            Authorization = exact.Authorization with { Nonce = "0x1234" },
        }));

        server.Events.Single(e => e.Status == PaymentEventStatus.VerificationFailed)
            .FailureReason.ShouldBe(X402ErrorReason.InvalidPayload);
    }

    [Fact]
    public async Task A_nonce_without_the_0x_prefix_is_refused_before_verification()
    {
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        var exact = payload.AsExactEvm();
        await server.SendAsync("/premium", payload.WithExactEvm(exact with
        {
            Authorization = exact.Authorization with { Nonce = "00" + exact.Authorization.Nonce[2..] },
        }));

        server.Facilitator.VerifyCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task An_authorization_valid_for_exactly_MaxTimeoutSeconds_plus_the_skew_is_verified()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await using var pinned = await PaidServerFixture.StartAsync(time: new FixedTimeProvider(now));
        var requirement = pinned.DecodeDemand(
            await pinned.Client.GetAsync("/premium", TestContext.Current.CancellationToken)).Accepts[0];
        var payload = await TestData.SignedPayloadAsync(
            requirement, validBefore: now.AddSeconds(requirement.MaxTimeoutSeconds + 30));

        await pinned.SendAsync("/premium", payload);

        pinned.Facilitator.VerifyCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task An_authorization_valid_beyond_MaxTimeoutSeconds_is_recorded_as_an_invalid_payload()
    {
        var demand = server.DecodeDemand(
            await server.Client.GetAsync("/premium", TestContext.Current.CancellationToken));
        var payload = await TestData.SignedPayloadAsync(
            demand.Accepts[0], validBefore: DateTimeOffset.UtcNow.AddDays(1));

        await server.SendAsync("/premium", payload);

        server.Events.Single(e => e.Status == PaymentEventStatus.VerificationFailed)
            .FailureReason.ShouldBe(X402ErrorReason.InvalidPayload);
    }

    [Fact]
    public async Task An_unreachable_facilitator_during_verify_is_logged()
    {
        server.Facilitator.FailNextCalls(3, FakeFacilitatorScenario.NetworkFailure);

        await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        server.LoggedErrors.ShouldContain(m => m.Contains("x402 verification could not reach the facilitator"));
    }

    [Fact]
    public async Task An_unreachable_facilitator_during_verify_is_recorded()
    {
        server.Facilitator.FailNextCalls(3, FakeFacilitatorScenario.NetworkFailure);

        await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        server.Events.Single(e => e.Status == PaymentEventStatus.VerificationFailed)
            .FailureReason.ShouldBe(X402ErrorReason.UnexpectedVerifyError);
    }

    [Fact]
    public async Task A_failed_settlement_presented_again_repeats_the_settlement_error()
    {
        server.Facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        await server.SendAsync("/premium", payload);

        var replay = await server.SendAsync("/premium", payload);

        server.DecodeDemand(replay).Error.ShouldBe(X402ErrorReason.InvalidTransactionState);
    }

    [Fact]
    public async Task A_settled_authorization_replayed_on_another_route_says_why()
    {
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        await server.SendAsync("/premium", payload);

        var replay = await server.SendAsync("/large", payload);

        server.DecodeDemand(replay).Error.ShouldBe("this authorization already paid for a different resource");
    }

    [Fact]
    public async Task A_settled_authorization_replayed_on_another_route_is_recorded()
    {
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        await server.SendAsync("/premium", payload);

        await server.SendAsync("/large", payload);

        server.Events.Single(e => e.Status == PaymentEventStatus.VerificationFailed)
            .FailureReason.ShouldBe("this authorization already paid for a different resource");
    }

    [Fact]
    public async Task A_settlement_that_cannot_reach_the_facilitator_is_logged()
    {
        server.Facilitator.FailNextSettleCalls(2, FakeFacilitatorScenario.NetworkFailure);

        await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        server.LoggedErrors.ShouldContain(m => m.Contains("x402 settlement could not reach the facilitator"));
    }

    [Fact]
    public async Task A_settlement_that_cannot_reach_the_facilitator_is_recorded()
    {
        server.Facilitator.FailNextSettleCalls(2, FakeFacilitatorScenario.NetworkFailure);

        await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        server.Events.Single(e => e.Status == PaymentEventStatus.SettlementFailed)
            .FailureReason.ShouldBe(X402ErrorReason.UnexpectedSettleError);
    }

    [Fact]
    public async Task A_settled_event_names_the_payer_settlement_reported()
    {
        const string settledFor = "0x00000000000000000000000000000000000000aa";
        server.Facilitator.RewriteSettlePayer = _ => settledFor;

        await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        server.Events.Single(e => e.Status == PaymentEventStatus.Settled).Payer.ShouldBe(settledFor);
    }

    [Fact]
    public async Task A_settled_event_falls_back_to_the_verified_payer()
    {
        server.Facilitator.RewriteSettlePayer = _ => null;

        await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        server.Events.Single(e => e.Status == PaymentEventStatus.Settled).Payer.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_settlement_failure_event_carries_the_facilitator_s_reason()
    {
        server.Facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;

        await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        server.Events.Single(e => e.Status == PaymentEventStatus.SettlementFailed)
            .FailureReason.ShouldBe(X402ErrorReason.InvalidTransactionState);
    }

    [Fact]
    public async Task A_400_from_a_paid_route_is_not_charged()
    {
        await server.PayAsync("/bad-request", KnownAssets.EurcBaseSepolia);

        server.Facilitator.SettleCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task An_error_response_from_a_paid_route_is_delivered()
    {
        var response = await server.PayAsync("/bad-request", KnownAssets.EurcBaseSepolia);

        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("bad request body");
    }

    [Fact]
    public async Task An_error_response_from_a_paid_route_leaves_the_authorization_usable()
    {
        var payload = await server.SignFor("/not-found", KnownAssets.EurcBaseSepolia);
        await server.SendAsync("/not-found", payload);

        var retry = await server.SendAsync("/not-found", payload);

        retry.StatusCode.ShouldBe(HttpStatusCode.NotFound); // not 409: the slot was abandoned
    }

    [Fact]
    public async Task A_400_beyond_the_cap_is_not_charged()
    {
        await using var capped = await PaidServerFixture.StartAsync(
            configure: options => options.MaxBufferedResponseBytes = 1024);

        await capped.PayAsync("/large-bad-request", KnownAssets.EurcBaseSepolia);

        capped.Facilitator.SettleCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task An_error_response_beyond_the_cap_leaves_the_authorization_usable()
    {
        await using var capped = await PaidServerFixture.StartAsync(
            configure: options => options.MaxBufferedResponseBytes = 1024);
        var payload = await capped.SignFor("/large-error", KnownAssets.EurcBaseSepolia);
        await capped.SendAsync("/large-error", payload);

        var retry = await capped.SendAsync("/large-error", payload);

        retry.StatusCode.ShouldBe(HttpStatusCode.InternalServerError); // not 409
    }

    [Fact]
    public async Task A_client_gone_before_settlement_leaves_the_authorization_usable()
    {
        var payload = await server.SignFor("/slow", KnownAssets.EurcBaseSepolia);
        await LeaveSlowRequestAsync(payload).ShouldThrowAsync<OperationCanceledException>();
        for (var i = 0; i < 100 && server.BufferedRequestCount == 0; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        // Reaching /slow again (and leaving again) rather than an immediate 409 proves the first
        // presentation released its ledger slot.
        await LeaveSlowRequestAsync(payload).ShouldThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_paid_response_completes_without_a_server_error()
    {
        await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        server.LastServerError.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_demand_omits_tags_when_none_are_configured()
    {
        var demand = server.DecodeDemand(
            await server.Client.GetAsync("/premium", TestContext.Current.CancellationToken));

        demand.Resource.Tags.ShouldBeNull();
    }

    [Fact]
    public async Task The_demand_carries_the_configured_tags()
    {
        await using var tagged = await PaidServerFixture.StartAsync(options => options.Tags.Add("ai"));

        var demand = tagged.DecodeDemand(
            await tagged.Client.GetAsync("/premium", TestContext.Current.CancellationToken));

        demand.Resource.Tags.ShouldBe(["ai"]);
    }

    [Fact]
    public async Task The_demand_offers_the_exact_scheme()
    {
        var demand = server.DecodeDemand(
            await server.Client.GetAsync("/premium", TestContext.Current.CancellationToken));

        demand.Accepts.ShouldAllBe(a => a.Scheme == "exact");
    }

    [Fact]
    public async Task A_payment_naming_another_scheme_never_reaches_the_facilitator()
    {
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);

        await server.SendAsync("/premium", payload with { Accepted = payload.Accepted with { Scheme = "upto" } });

        server.Facilitator.VerifyCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_payment_naming_another_network_for_the_same_asset_never_reaches_the_facilitator()
    {
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);

        await server.SendAsync(
            "/premium", payload with { Accepted = payload.Accepted with { Network = "eip155:8453" } });

        server.Facilitator.VerifyCallCount.ShouldBe(0);
    }

    private async Task LeaveSlowRequestAsync(PaymentPayload payload)
    {
        using var abandon = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/slow");
        request.Headers.TryAddWithoutValidation(X402Headers.PaymentSignature, X402Codec.Encode(payload));
        await server.Client.SendAsync(request, abandon.Token);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task A_throwing_event_sink_does_not_fail_an_unpaid_request()
    {
        await using var throwing = await PaidServerFixture.StartAsync(sinkThrows: true);

        var response = await throwing.Client.GetAsync("/premium", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        throwing.LoggedErrors.ShouldContain(message => message.Contains("payment event sink threw"));
    }
}
