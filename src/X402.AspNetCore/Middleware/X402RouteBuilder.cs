using Microsoft.AspNetCore.Routing.Template;
using X402.Pricing;

namespace X402.AspNetCore.Middleware;

/// <summary>Declares which routes are priced, and at what.</summary>
/// <remarks>
/// Built through <see cref="DependencyInjection.X402ApplicationBuilderExtensions.UseX402"/>, not
/// constructed directly.
/// </remarks>
public sealed class X402RouteBuilder
{
    private readonly List<X402Route> routes = [];

    /// <summary>Prices a route.</summary>
    /// <param name="pattern">
    /// A route template, for example <c>/premium/{id}</c>. Matched with the same semantics as
    /// ASP.NET Core routing: a parameter matches exactly one path segment, so this never matches
    /// as a prefix.
    /// </param>
    /// <param name="prices">One price per accepted asset, in the order announced to payers.</param>
    /// <param name="describe">Optional description advertised in the demand for this route.</param>
    /// <returns>This builder, so calls to <see cref="Map"/> can be chained.</returns>
    /// <remarks>
    /// Routes are matched in declaration order: the first pattern that matches a request wins,
    /// even when a later one also would.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is null, empty, or blank.</exception>
    public X402RouteBuilder Map(
        string pattern, PriceSet prices, Action<ResourceInfoOverrides>? describe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var overrides = new ResourceInfoOverrides();
        describe?.Invoke(overrides);

        routes.Add(new X402Route(
            new TemplateMatcher(TemplateParser.Parse(pattern.TrimStart('/')), []),
            pattern, prices, overrides));

        return this;
    }

    /// <summary>Builds the immutable route table declared so far.</summary>
    internal IReadOnlyList<X402Route> Build() => routes;
}
