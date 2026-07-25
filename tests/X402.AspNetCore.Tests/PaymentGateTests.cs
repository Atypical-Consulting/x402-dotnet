using System.Net;
using System.Net.Http.Json;
using X402.Assets;
using X402.Transport;

namespace X402.AspNetCore.Tests;

public sealed class PaymentGateTests
{
    [Fact]
    public async Task An_unpaid_call_to_a_dynamically_priced_endpoint_gets_402()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var response = await server.Client.PostAsJsonAsync(
            "/analyze", new { Tokens = 100 }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        response.Headers.Contains(X402Headers.PaymentRequired).ShouldBeTrue();
    }

    [Fact]
    public async Task The_price_is_computed_from_the_request()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var cheap = server.DecodeDemand(await server.Client.PostAsJsonAsync(
            "/analyze", new { Tokens = 10 }, TestContext.Current.CancellationToken));
        var dear = server.DecodeDemand(await server.Client.PostAsJsonAsync(
            "/analyze", new { Tokens = 1000 }, TestContext.Current.CancellationToken));

        // 0.001 EURC par jeton : 10 jetons → 0.010 EURC, 1000 jetons → 1.000 EURC.
        cheap.Accepts[0].Amount.ShouldBe("10000");
        dear.Accepts[0].Amount.ShouldBe("1000000");
    }

    [Fact]
    public async Task The_price_can_depend_on_the_body_size()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var small = server.DecodeDemand(await server.Client.PostAsync("/by-size",
            new StringContent(new string('x', 100)), TestContext.Current.CancellationToken));
        var large = server.DecodeDemand(await server.Client.PostAsync("/by-size",
            new StringContent(new string('x', 10_000)), TestContext.Current.CancellationToken));

        long.Parse(large.Accepts[0].Amount).ShouldBeGreaterThan(long.Parse(small.Accepts[0].Amount));
    }

    [Fact]
    public async Task A_paid_dynamic_call_reaches_the_endpoint_and_settles()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var response = await server.PayDynamicAsync("/analyze", new { Tokens = 100 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains(X402Headers.PaymentResponse).ShouldBeTrue();
    }

    [Fact]
    public async Task The_gate_reports_which_asset_the_payer_chose()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var response = await server.PayDynamicAsync(
            "/analyze", new { Tokens = 100 }, KnownAssets.UsdcBaseSepolia);

        // /analyze renvoie SettledAsset dans son corps, pour que le test puisse l'observer.
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("USDC");
    }

    [Fact]
    public async Task The_same_result_object_works_from_a_controller_and_a_minimal_endpoint()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var minimal = await server.Client.PostAsJsonAsync(
            "/analyze", new { Tokens = 1 }, TestContext.Current.CancellationToken);
        var mvc = await server.Client.PostAsJsonAsync(
            "/mvc/analyze", new { Tokens = 1 }, TestContext.Current.CancellationToken);

        minimal.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        mvc.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        mvc.Headers.Contains(X402Headers.PaymentRequired).ShouldBeTrue();
    }

    [Fact]
    public async Task Opening_a_gate_without_UseX402_fails_loudly()
    {
        // Sans le trajet sortant, le contenu serait livré sans jamais être réglé.
        await using var server = await PaidServerFixture.StartAsync(withMiddleware: false);

        var response = await server.Client.PostAsJsonAsync(
            "/analyze", new { Tokens = 1 }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        server.LastServerError.ShouldContain("UseX402");
    }
}
