using System.Net;
using System.Net.Http.Json;
using X402.Assets;
using X402.Client;
using X402.Protocol;
using X402.TestKit;

namespace X402.Integration.Tests;

/// <summary>
/// A real agent (<see cref="X402.Client"/>) calling a real API (<see cref="X402.AspNetCore"/>) in
/// front of a simulated facilitator (<see cref="X402.TestKit"/>), with no shortcuts on either
/// side. See <see cref="PaidApiFixture"/> for how the two message-handler chains are composed.
/// </summary>
public sealed class EndToEndTests
{
    [Fact]
    public async Task An_agent_pays_in_euros_and_gets_the_content()
    {
        await using var fixture = await PaidApiFixture.StartAsync();

        var response = await fixture.Agent.GetAsync("/premium",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.GetPaymentReceipt()!.Success.ShouldBeTrue();
        fixture.Facilitator.SettleCallCount.ShouldBe(1);
        fixture.Facilitator.HasDoubleSettled.ShouldBeFalse();
    }

    [Fact]
    public async Task An_agent_pays_a_dynamically_priced_endpoint()
    {
        await using var fixture = await PaidApiFixture.StartAsync();

        var response = await fixture.Agent.PostAsJsonAsync("/analyze",
            new { Tokens = 250 }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // 250 tokens at 0.001 EURC: 0.250 EURC.
        fixture.Facilitator.LastRequestBody!.ShouldContain("\"amount\":\"250000\"");
    }

    [Fact]
    public async Task A_verification_failure_surfaces_to_the_agent_without_content()
    {
        await using var fixture = await PaidApiFixture.StartAsync();
        fixture.Facilitator.Scenario = FakeFacilitatorScenario.InsufficientFunds;

        var exception = await Should.ThrowAsync<PaymentRejectedException>(
            () => fixture.Agent.GetAsync("/premium", TestContext.Current.CancellationToken));

        // Genuinely round-tripped, not asserted against a hand-built harness: X402.AspNetCore
        // itself put this reason on the second PAYMENT-REQUIRED, and X402PaymentHandler decoded
        // that real header rather than a test double's. See PaymentHandlerTests for the unit-level
        // coverage of every shape (a reason given, none given, an undecodable header).
        exception.Reason.ShouldBe(X402ErrorReason.InsufficientFunds);
        exception.Message.ShouldContain(X402ErrorReason.InsufficientFunds);
        exception.PaymentRequired.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_settlement_failure_withholds_the_content()
    {
        await using var fixture = await PaidApiFixture.StartAsync();
        fixture.Facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;

        await Should.ThrowAsync<PaymentRejectedException>(
            () => fixture.Agent.GetAsync("/premium", TestContext.Current.CancellationToken));

        fixture.ServedContentCount.ShouldBe(0);
    }

    [Fact]
    public async Task Replaying_one_authorization_settles_exactly_once()
    {
        await using var fixture = await PaidApiFixture.StartAsync();

        var paid = await fixture.Agent.GetAsync("/premium", TestContext.Current.CancellationToken);
        var header = fixture.LastPaymentSignature!;

        // Manual replay of the SAME authorization, as a malicious client would.
        using var replay = new HttpRequestMessage(HttpMethod.Get, "/premium");
        replay.Headers.Add(Transport.X402Headers.PaymentSignature, header);
        var replayed = await fixture.RawClient.SendAsync(replay,
            TestContext.Current.CancellationToken);

        paid.StatusCode.ShouldBe(HttpStatusCode.OK);
        replayed.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Facilitator.SettleCallCount.ShouldBe(1);
        fixture.Facilitator.HasDoubleSettled.ShouldBeFalse();
    }

    [Fact]
    public async Task An_agent_that_only_holds_dollars_still_pays_a_euro_first_server()
    {
        await using var fixture = await PaidApiFixture.StartAsync(
            configureAgent: o =>
            {
                o.SetLimits(KnownAssets.UsdcBaseSepolia, perRequest: 1m, perSession: 10m);
                // no EURC limit declared: the euro is therefore unpayable for this agent
            });

        var response = await fixture.Agent.GetAsync("/premium",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Facilitator.LastRequestBody!.ShouldContain(KnownAssets.UsdcBaseSepolia.Address);
    }

    [Fact]
    public async Task A_facilitator_refusing_an_asset_surfaces_a_clean_non_looping_refusal()
    {
        // §2.1.6: this library supports EURC, the chosen facilitator may not settle it.
        // This does NOT demonstrate an automatic degradation of the offer to a payable asset:
        // IFacilitatorClient.GetSupportedAsync exists but has no caller in src/ (dead code as
        // of today), so nothing consults the facilitator's capabilities to adapt the `accepts`
        // advertised to the client. Adapting the offer would require calling GetSupportedAsync
        // somewhere in the server pipeline, which does not exist. This test only proves that
        // the facilitator's refusal surfaces to the agent as a clean, non-looping refusal —
        // the same structural path as the refused-verification scenario, reached by a
        // different lever (the asset rather than the funds).
        await using var fixture = await PaidApiFixture.StartAsync();
        fixture.Facilitator.Scenario = FakeFacilitatorScenario.UnsupportedAsset;
        fixture.Facilitator.SupportedAssets = [KnownAssets.UsdcBaseSepolia.Address];

        // The agent tries the euro, gets refused, and has no automatic second attempt:
        // the consequence is visible, which is the intended behaviour.
        await Should.ThrowAsync<PaymentRejectedException>(
            () => fixture.Agent.GetAsync("/premium", TestContext.Current.CancellationToken));
    }
}
