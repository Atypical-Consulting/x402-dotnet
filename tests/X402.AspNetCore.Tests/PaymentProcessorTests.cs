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
    [InlineData(PaymentEventStatus.Settled)]
    public async Task Every_terminal_branch_records_a_payment_event(PaymentEventStatus expected)
    {
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
    public async Task A_throwing_event_sink_does_not_fail_an_unpaid_request()
    {
        await using var throwing = await PaidServerFixture.StartAsync(sinkThrows: true);

        var response = await throwing.Client.GetAsync("/premium", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        throwing.LoggedErrors.ShouldContain(message => message.Contains("payment event sink threw"));
    }
}
