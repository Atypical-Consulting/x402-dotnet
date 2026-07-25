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
        harness.SignatureCount.ShouldBe(1); // EURC était rejeté avant toute signature, jamais signé
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
        // Sans rebufferisation, le rejeu partirait avec un corps vide — le bug le plus courant de
        // ce genre de handler. Le contenu est un StreamContent sur un flux non-recherchable à
        // lecture unique, pas un JsonContent : ce dernier se re-sérialise depuis l'objet à chaque
        // envoi et rejouerait correctement même sans aucune bufferisation, ce qui ne prouverait
        // rien sur le handler.
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
        // Documente que le chemin courant (PostAsJsonAsync) fonctionne de bout en bout. Ce test ne
        // prouve rien sur la bufferisation elle-même : JsonContent se re-sérialise depuis l'objet
        // à chaque envoi, donc il rejouerait correctement même si le handler ne bufferisait rien —
        // voir A_post_body_is_replayed_intact pour le test qui exerce réellement l'étape 1.
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

        harness.RequestCount.ShouldBe(2);   // un seul rejeu
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
