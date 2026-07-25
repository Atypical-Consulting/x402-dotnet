using System.Net;
using X402.Assets;
using X402.Pricing;

namespace X402.AspNetCore.Tests;

public sealed class RouteMappingTests
{
    [Theory]
    [InlineData("/premium/42", HttpStatusCode.PaymentRequired)]
    [InlineData("/premium/abc", HttpStatusCode.PaymentRequired)]
    [InlineData("/premium", HttpStatusCode.OK)]          // the pattern requires one segment
    [InlineData("/premium/42/deep", HttpStatusCode.OK)]  // one segment only, not a prefix
    public async Task A_route_pattern_matches_exactly_its_template(
        string path, HttpStatusCode expected)
    {
        await using var server = await PaidServerFixture.StartAsync(routes: routes =>
            routes.Map("/premium/{id}", Price.For(KnownAssets.EurcBaseSepolia, 0.01m)));

        var response = await server.Client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task A_route_can_describe_its_resource()
    {
        await using var server = await PaidServerFixture.StartAsync(routes: routes =>
            routes.Map("/reports", Price.For(KnownAssets.EurcBaseSepolia, 0.25m),
                r => r.Description = "Rapport mensuel"));

        var demand = server.DecodeDemand(
            await server.Client.GetAsync("/reports", TestContext.Current.CancellationToken));

        demand.Resource.Description.ShouldBe("Rapport mensuel");
    }

    [Fact]
    public async Task Mapping_a_price_for_an_asset_the_server_does_not_accept_fails_at_startup()
    {
        // A route priced in USDC while only EURC is configured would only surface at the first
        // payment. Refuse it at start-up instead.
        var start = async () => await PaidServerFixture.StartAsync(
            configure: options =>
            {
                options.Assets.Clear();
                options.Assets.Add(new Configuration.AssetConfiguration { Symbol = "EURC" });
            },
            routes: routes =>
                routes.Map("/premium", Price.For(KnownAssets.UsdcBaseSepolia, 0.01m)));

        var exception = await Should.ThrowAsync<InvalidOperationException>(start);
        exception.Message.ShouldContain("USDC");
    }

    [Fact]
    public async Task Mapping_a_price_on_another_network_fails_at_startup()
    {
        var start = async () => await PaidServerFixture.StartAsync(routes: routes =>
            routes.Map("/premium", Price.For(KnownAssets.EurcBaseMainnet, 0.01m)));

        await Should.ThrowAsync<InvalidOperationException>(start);
    }

    [Fact]
    public async Task Routes_are_evaluated_in_declaration_order()
    {
        await using var server = await PaidServerFixture.StartAsync(routes: routes => routes
            .Map("/api/free", Price.For(KnownAssets.EurcBaseSepolia, 0.001m))
            .Map("/api/{**rest}", Price.For(KnownAssets.EurcBaseSepolia, 0.999m)));

        var demand = server.DecodeDemand(
            await server.Client.GetAsync("/api/free", TestContext.Current.CancellationToken));

        demand.Accepts[0].Amount.ShouldBe("1000");   // the first declaration wins
    }
}
