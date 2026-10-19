using System.Net;
using System.Net.Http.Json;
using X402.Assets;
using X402.Client;
using X402.Protocol;

namespace X402.Client.Tests;

public sealed class PaymentHandlerTests
{
    [Fact]
    public async Task A_non_402_response_passes_through_untouched()
    {
        using var harness = PayingClientHarness.Create(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var response = await harness.Client.GetAsync("https://api.test/free",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        harness.RequestCount.ShouldBe(1);
        harness.SignatureCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_402_is_paid_and_the_request_replayed_once()
    {
        using var harness = PayingClientHarness.CreatePaywall(KnownAssets.EurcBaseSepolia);

        var response = await harness.Client.GetAsync("https://api.test/premium",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        harness.RequestCount.ShouldBe(2);
        harness.LastPaymentHeader.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_agent_preference_wins_over_the_server_order()
    {
        using var harness = PayingClientHarness.CreatePaywall(
            KnownAssets.EurcBaseSepolia, KnownAssets.UsdcBaseSepolia,
            configure: o => o.Prefer(KnownAssets.UsdcBaseSepolia));

        await harness.Client.GetAsync("https://api.test/premium",
            TestContext.Current.CancellationToken);

        harness.PaidAsset.ShouldBe(KnownAssets.UsdcBaseSepolia.Address);
    }

    [Fact]
    public async Task Without_a_preference_the_server_order_is_honoured()
    {
        using var harness = PayingClientHarness.CreatePaywall(
            KnownAssets.EurcBaseSepolia, KnownAssets.UsdcBaseSepolia);

        await harness.Client.GetAsync("https://api.test/premium",
            TestContext.Current.CancellationToken);

        harness.PaidAsset.ShouldBe(KnownAssets.EurcBaseSepolia.Address);
    }

    [Fact]
    public async Task An_asset_over_its_limit_falls_back_to_another_within_limits()
    {
        // An agent short on euros must be able to pay in dollars rather than fail.
        using var harness = PayingClientHarness.CreatePaywall(
            KnownAssets.EurcBaseSepolia, KnownAssets.UsdcBaseSepolia,
            configure: o =>
            {
                // The symbol alone isn't enough to identify an asset (see AssetIdentity):
                // limits are set per AssetDescriptor, i.e. per network + address.
                o.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 0.000001m, perSession: 0.000001m);
                o.SetLimits(KnownAssets.UsdcBaseSepolia, perRequest: 1m, perSession: 1m);
            });

        var response = await harness.Client.GetAsync("https://api.test/premium",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        harness.PaidAsset.ShouldBe(KnownAssets.UsdcBaseSepolia.Address);
        harness.SignatureCount.ShouldBe(1); // EURC was rejected before any signing, never signed
    }

    [Fact]
    public async Task When_every_asset_is_over_its_limit_nothing_is_signed_and_nothing_is_replayed()
    {
        using var harness = PayingClientHarness.CreatePaywall(
            KnownAssets.EurcBaseSepolia,
            configure: o => o.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 0.000001m, perSession: 0.000001m));

        await Should.ThrowAsync<SpendingLimitExceededException>(
            () => harness.Client.GetAsync("https://api.test/premium",
                TestContext.Current.CancellationToken));

        harness.RequestCount.ShouldBe(1);     // no replay request
        harness.SignatureCount.ShouldBe(0);   // nothing was signed
    }

    [Fact]
    public async Task An_unacceptable_network_is_refused_without_replay()
    {
        using var harness = PayingClientHarness.CreatePaywall(
            KnownAssets.EurcBaseMainnet,
            configure: o => o.AllowedNetworks.Add(Networks.KnownNetworks.BaseSepolia));

        await Should.ThrowAsync<NoAcceptablePaymentException>(
            () => harness.Client.GetAsync("https://api.test/premium",
                TestContext.Current.CancellationToken));

        harness.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_post_body_is_replayed_intact()
    {
        // Without re-buffering, the replay would go out with an empty body — the most common
        // bug in this kind of handler. The content is a StreamContent over a non-seekable,
        // single-read stream, not a JsonContent: the latter re-serializes from the object on
        // every send and would replay correctly even with no buffering at all, which would
        // prove nothing about the handler.
        using var harness = PayingClientHarness.CreatePaywall(KnownAssets.EurcBaseSepolia);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.test/premium")
        {
            Content = new StreamContent(new SingleReadStream("latest market data"u8.ToArray())),
        };

        await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);

        harness.LastRequestBody.ShouldContain("latest market data");
        harness.RequestBodies.Count.ShouldBe(2);
        harness.RequestBodies[0].ShouldBe(harness.RequestBodies[1]);
    }

    [Fact]
    public async Task Posting_via_the_common_JsonContent_convenience_path_also_succeeds()
    {
        // Documents that the common path (PostAsJsonAsync) works end to end. This test proves
        // nothing about buffering itself: JsonContent re-serializes from the object on every
        // send, so it would replay correctly even if the handler buffered nothing — see
        // A_post_body_is_replayed_intact for the test that actually exercises step 1.
        using var harness = PayingClientHarness.CreatePaywall(KnownAssets.EurcBaseSepolia);

        var response = await harness.Client.PostAsJsonAsync("https://api.test/premium",
            new { Query = "latest market data" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        harness.LastRequestBody.ShouldContain("latest market data");
        harness.RequestBodies.Count.ShouldBe(2);
        harness.RequestBodies[0].ShouldBe(harness.RequestBodies[1]);
    }

    [Fact]
    public async Task A_second_402_after_paying_raises_rather_than_looping()
    {
        using var harness = PayingClientHarness.CreateAlwaysPaywalled(KnownAssets.EurcBaseSepolia);

        await Should.ThrowAsync<PaymentRejectedException>(
            () => harness.Client.GetAsync("https://api.test/premium",
                TestContext.Current.CancellationToken));

        harness.RequestCount.ShouldBe(2);   // a single replay
    }

    [Fact]
    public async Task A_second_402_carries_the_servers_reason_on_the_exception()
    {
        // The actionable string a real facilitator returns — e.g.
        // invalid_exact_evm_insufficient_balance — otherwise exists only in the server's own log,
        // which an agent paying a third-party API cannot read. Decoding the second PAYMENT-REQUIRED
        // and exposing it here is what samples/README.md's "one exception with a message you can
        // act on" claim depends on.
        using var harness = PayingClientHarness.CreateAlwaysPaywalled(
            KnownAssets.EurcBaseSepolia, rejectionReason: "invalid_exact_evm_insufficient_balance");

        var exception = await Should.ThrowAsync<PaymentRejectedException>(
            () => harness.Client.GetAsync("https://api.test/premium",
                TestContext.Current.CancellationToken));

        exception.Reason.ShouldBe("invalid_exact_evm_insufficient_balance");
        exception.Message.ShouldContain("invalid_exact_evm_insufficient_balance");
        exception.PaymentRequired.ShouldNotBeNull();
        exception.PaymentRequired.Accepts.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_second_402_with_no_stated_reason_still_says_so_rather_than_stay_silent()
    {
        using var harness = PayingClientHarness.CreateAlwaysPaywalled(KnownAssets.EurcBaseSepolia);

        var exception = await Should.ThrowAsync<PaymentRejectedException>(
            () => harness.Client.GetAsync("https://api.test/premium",
                TestContext.Current.CancellationToken));

        exception.Reason.ShouldBeNull();
        exception.Message.ShouldContain("the server gave no reason");
        exception.PaymentRequired.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_second_402_with_an_undecodable_header_still_raises_without_crashing()
    {
        // Distinct from A_second_402_with_no_stated_reason_still_says_so_rather_than_stay_silent:
        // there the header decodes fine and simply carries no Error; here the header itself is not
        // a valid encoded PaymentRequired at all — the false arm of X402PaymentHandler's own
        // decodedRejection ternary, which PaymentRejectedException's XML docs on Reason and
        // PaymentRequired both promise stays null in exactly this case.
        using var harness = PayingClientHarness.CreateAlwaysPaywalled(
            KnownAssets.EurcBaseSepolia, malformedSecondRejection: true);

        var exception = await Should.ThrowAsync<PaymentRejectedException>(
            () => harness.Client.GetAsync("https://api.test/premium",
                TestContext.Current.CancellationToken));

        exception.Reason.ShouldBeNull();
        exception.PaymentRequired.ShouldBeNull();
        exception.Message.ShouldContain("the server gave no reason");
    }

    [Fact]
    public async Task The_settlement_receipt_is_exposed_on_the_response()
    {
        using var harness = PayingClientHarness.CreatePaywall(KnownAssets.EurcBaseSepolia);

        var response = await harness.Client.GetAsync("https://api.test/premium",
            TestContext.Current.CancellationToken);

        var receipt = response.GetPaymentReceipt();
        receipt.ShouldNotBeNull();
        receipt.Success.ShouldBeTrue();
        receipt.Transaction.ShouldStartWith("0x");
    }

    [Fact]
    public async Task The_authorization_validity_window_tolerates_clock_skew()
    {
        using var harness = PayingClientHarness.CreatePaywall(KnownAssets.EurcBaseSepolia);

        await harness.Client.GetAsync("https://api.test/premium",
            TestContext.Current.CancellationToken);

        var authorization = harness.LastAuthorization!;
        var validAfter = long.Parse(authorization.ValidAfter);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // validAfter in the past: without slack, a server clock running slightly behind
        // would reject the authorization as "not yet valid".
        validAfter.ShouldBeLessThan(now);
        long.Parse(authorization.ValidBefore).ShouldBeGreaterThan(now);
    }

    [Fact]
    public async Task Each_payment_uses_a_fresh_nonce()
    {
        using var harness = PayingClientHarness.CreatePaywall(KnownAssets.EurcBaseSepolia);

        await harness.Client.GetAsync("https://api.test/a", TestContext.Current.CancellationToken);
        await harness.Client.GetAsync("https://api.test/b", TestContext.Current.CancellationToken);

        harness.Nonces.Distinct().Count().ShouldBe(2);
    }

    // Every paywall here charges 0.01; a per-session limit of 0.01 therefore allows one payment.
    private static void OnePaymentPerSession(X402ClientOptions options) =>
        options.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 0.01m, perSession: 0.01m);

    [Fact]
    public async Task A_transport_failure_after_the_replay_was_sent_keeps_the_reservation()
    {
        // The server already holds a spendable authorization: crediting the budget back would let
        // a server that drops the connection collect one authorization per retry.
        using var harness = PayingClientHarness.CreateFlakyPaywall(
            KnownAssets.EurcBaseSepolia, OnePaymentPerSession);
        await Should.ThrowAsync<HttpRequestException>(
            () => harness.Client.GetAsync("https://api.test/premium", TestContext.Current.CancellationToken));

        await Should.ThrowAsync<SpendingLimitExceededException>(
            () => harness.Client.GetAsync("https://api.test/premium", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_second_402_keeps_the_reservation()
    {
        using var harness = PayingClientHarness.CreateAlwaysPaywalled(
            KnownAssets.EurcBaseSepolia, configure: OnePaymentPerSession);
        await Should.ThrowAsync<PaymentRejectedException>(
            () => harness.Client.GetAsync("https://api.test/premium", TestContext.Current.CancellationToken));

        await Should.ThrowAsync<SpendingLimitExceededException>(
            () => harness.Client.GetAsync("https://api.test/premium", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_signing_failure_returns_its_reservation_to_the_session_budget()
    {
        // Nothing left the process: the budget must not be consumed by a payment never made.
        using var harness = PayingClientHarness.CreatePaywall(
            KnownAssets.EurcBaseSepolia, OnePaymentPerSession);
        harness.FailNextSignature = true;
        await Should.ThrowAsync<InvalidOperationException>(
            () => harness.Client.GetAsync("https://api.test/premium", TestContext.Current.CancellationToken));

        var response = await harness.Client.GetAsync(
            "https://api.test/premium", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_requirement_valid_longer_than_the_client_allows_is_not_paid()
    {
        // The harness's requirements ask for 60 seconds.
        using var harness = PayingClientHarness.CreatePaywall(
            KnownAssets.EurcBaseSepolia, o => o.MaxAuthorizationValiditySeconds = 30);

        await Should.ThrowAsync<NoAcceptablePaymentException>(
            () => harness.Client.GetAsync("https://api.test/premium", TestContext.Current.CancellationToken));

        harness.SignatureCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_body_larger_than_the_buffer_cap_still_reaches_a_free_endpoint()
    {
        using var harness = PayingClientHarness.Create(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var response = await harness.Client.PostAsync(
            "https://api.test/upload", new ByteArrayContent(new byte[2 * 1024 * 1024]),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_402_for_a_body_too_large_to_replay_is_refused_without_signing()
    {
        using var harness = PayingClientHarness.CreatePaywall(
            KnownAssets.EurcBaseSepolia, o => o.MaxBufferedRequestBytes = 16);

        await Should.ThrowAsync<PaymentRejectedException>(
            () => harness.Client.PostAsync(
                "https://api.test/premium", new ByteArrayContent(new byte[1024]),
                TestContext.Current.CancellationToken));

        harness.SignatureCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("""{"x402Version":2,"resource":{"url":"https://api.test/x"},"accepts":[null]}""")]
    [InlineData("""{"x402Version":2,"resource":{"url":"https://api.test/x"},"accepts":[{"scheme":"exact","network":"eip155:84532","amount":"10000","asset":"808456652fdb597867f38412077A9182bf77359F","payTo":"0x209693Bc6afc0C5328bA36FaF03C514EF312287C","maxTimeoutSeconds":60}]}""")]
    public async Task A_malformed_offer_is_reported_as_unpayable(string demandJson)
    {
        // A null element, or an asset address without its 0x prefix (which signing would reject),
        // must surface as the documented exception — not a NullReferenceException or ArgumentException.
        using var harness = PayingClientHarness.Create(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PaymentRequired);
            response.Headers.Add(
                Transport.X402Headers.PaymentRequired,
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(demandJson)));
            return response;
        });

        await Should.ThrowAsync<NoAcceptablePaymentException>(
            () => harness.Client.GetAsync("https://api.test/x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_demand_with_null_accepts_is_rejected_as_unusable()
    {
        using var harness = PayingClientHarness.Create(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PaymentRequired);
            response.Headers.Add(
                Transport.X402Headers.PaymentRequired,
                Convert.ToBase64String("""{"x402Version":2,"resource":{"url":"https://api.test/x"},"accepts":null}"""u8.ToArray()));
            return response;
        });

        await Should.ThrowAsync<PaymentRejectedException>(
            () => harness.Client.GetAsync("https://api.test/x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Earlier_preferences_outrank_later_ones()
    {
        using var harness = PayingClientHarness.CreatePaywall(
            KnownAssets.UsdcBaseSepolia, KnownAssets.EurcBaseSepolia,
            configure: o => o.Prefer(KnownAssets.EurcBaseSepolia).Prefer(KnownAssets.UsdcBaseSepolia));

        await harness.Client.GetAsync("https://api.test/premium", TestContext.Current.CancellationToken);

        harness.PaidAsset.ShouldBe(KnownAssets.EurcBaseSepolia.Address);
    }
}
