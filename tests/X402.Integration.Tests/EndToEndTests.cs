using System.Net;
using System.Net.Http.Json;
using X402.Assets;
using X402.Client;
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
        // 250 jetons à 0.001 EURC : 0.250 EURC.
        fixture.Facilitator.LastRequestBody!.ShouldContain("\"amount\":\"250000\"");
    }

    [Fact]
    public async Task A_verification_failure_surfaces_to_the_agent_without_content()
    {
        await using var fixture = await PaidApiFixture.StartAsync();
        fixture.Facilitator.Scenario = FakeFacilitatorScenario.InsufficientFunds;

        await Should.ThrowAsync<PaymentRejectedException>(
            () => fixture.Agent.GetAsync("/premium", TestContext.Current.CancellationToken));
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

        // Rejeu manuel de la MÊME autorisation, comme le ferait un client malveillant.
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
                // aucune limite EURC déclarée : l'euro est donc impayable pour cet agent
            });

        var response = await fixture.Agent.GetAsync("/premium",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Facilitator.LastRequestBody!.ShouldContain(KnownAssets.UsdcBaseSepolia.Address);
    }

    [Fact]
    public async Task A_usdc_only_facilitator_degrades_the_offer_instead_of_breaking_it()
    {
        // §2.1.6 : la bibliothèque supporte l'EURC, le facilitateur choisi peut ne pas le régler.
        await using var fixture = await PaidApiFixture.StartAsync();
        fixture.Facilitator.Scenario = FakeFacilitatorScenario.UnsupportedAsset;
        fixture.Facilitator.SupportedAssets = [KnownAssets.UsdcBaseSepolia.Address];

        // L'agent tente l'euro, se fait refuser, et n'a pas de second essai automatique :
        // la conséquence est visible, ce qui est le comportement voulu.
        await Should.ThrowAsync<PaymentRejectedException>(
            () => fixture.Agent.GetAsync("/premium", TestContext.Current.CancellationToken));
    }
}
