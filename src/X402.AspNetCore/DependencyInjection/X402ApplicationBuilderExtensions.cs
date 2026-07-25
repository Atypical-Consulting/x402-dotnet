using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.Middleware;

namespace X402.AspNetCore.DependencyInjection;

/// <summary>Installs the x402 payment pipeline.</summary>
public static class X402ApplicationBuilderExtensions
{
    /// <summary>
    /// Adds x402 payment handling. Required for both route pricing and the imperative gate: this
    /// middleware carries settlement and the settlement header on the way out.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <param name="configure">Declares which routes are priced, and at what.</param>
    /// <returns><paramref name="app"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A route is priced in an asset, or on a network, this server does not accept. Failing here,
    /// at start-up, is deliberate: the alternative is discovering the mistake at the first payment
    /// against that route.
    /// </exception>
    public static IApplicationBuilder UseX402(
        this IApplicationBuilder app, Action<X402RouteBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var builder = new X402RouteBuilder();
        configure?.Invoke(builder);
        var routes = builder.Build();

        var assets = app.ApplicationServices.GetRequiredService<IResolvedAssets>();

        foreach (var route in routes)
        {
            foreach (var price in route.Prices)
            {
                if (!assets.TryGetByAddress(price.Asset.Address, out _))
                {
                    throw new InvalidOperationException(
                        $"Route '{route.Pattern}' is priced in {price.Asset.Symbol} " +
                        $"({price.Asset.Address}) on {price.Asset.Network}, which this server " +
                        "does not accept. Add it to X402:Assets, or price the route in an " +
                        $"accepted asset: {string.Join(", ", assets.All.Select(a => a.Symbol))}.");
                }
            }
        }

        return app.UseMiddleware<X402Middleware>(routes);
    }
}
