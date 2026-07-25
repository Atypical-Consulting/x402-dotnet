using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using X402.Pricing;

namespace X402.AspNetCore.Middleware;

/// <summary>
/// One route pricing declaration: a template to match, the prices it is offered at, and the
/// resource description advertised to payers.
/// </summary>
/// <param name="Matcher">Matches a request path against <paramref name="Pattern"/>.</param>
/// <param name="Pattern">The route template this declaration was built from, for diagnostics.</param>
/// <param name="Prices">One price per accepted asset, in the order announced to payers.</param>
/// <param name="Overrides">Resource description advertised in the demand for this route.</param>
internal sealed record X402Route(
    TemplateMatcher Matcher, string Pattern, PriceSet Prices, ResourceInfoOverrides Overrides)
{
    /// <summary>Whether this route's template matches the given request path.</summary>
    /// <remarks>
    /// Matched through <see cref="TemplateMatcher"/> — real route-template semantics (one segment
    /// per parameter, no accidental prefix match) rather than a string comparison. The matched
    /// route values themselves are not needed: pricing does not read them.
    /// </remarks>
    public bool Matches(PathString path) => Matcher.TryMatch(path, new RouteValueDictionary());
}
