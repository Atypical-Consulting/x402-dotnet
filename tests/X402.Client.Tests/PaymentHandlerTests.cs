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
        // Un agent à court d'euros doit pouvoir payer en dollars plutôt qu'échouer.
        using var harness = PayingClientHarness.CreatePaywall(
            KnownAssets.EurcBaseSepolia, KnownAssets.UsdcBaseSepolia,
            configure: o =>
            {
                // Le symbole seul ne suffit pas à identifier un actif (voir AssetIdentity) :
                // les plafonds se posent par AssetDescriptor, donc par réseau + adresse.
                o.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 0.000001m, perSession: 0.000001m);
                o.SetLimits(KnownAssets.UsdcBaseSepolia, perRequest: 1m, perSession: 1m);
            });

        var response = await harness.Client.GetAsync("https://api.test/premium",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        harness.PaidAsset.ShouldBe(KnownAssets.UsdcBaseSepolia.Address);
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

        harness.RequestCount.ShouldBe(1);     // aucune requête de rejeu
        harness.SignatureCount.ShouldBe(0);   // rien n'a été signé
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
        // Sans rebufferisation, le rejeu partirait avec un corps vide — le bug le plus
        // courant de ce genre de handler.
        using var harness = PayingClientHarness.CreatePaywall(KnownAssets.EurcBaseSepolia);

        await harness.Client.PostAsJsonAsync("https://api.test/premium",
            new { Query = "latest market data" }, TestContext.Current.CancellationToken);

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

        harness.RequestCount.ShouldBe(2);   // un seul rejeu
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

        // validAfter dans le passé : sans marge, une horloge serveur légèrement en retard
        // rejetterait l'autorisation pour "pas encore valide".
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
}
