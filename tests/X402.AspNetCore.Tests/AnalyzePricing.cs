using X402.Assets;
using X402.Pricing;

namespace X402.AspNetCore.Tests;

/// <summary>Body for POST /analyze and POST /mvc/analyze — one type so both price identically.</summary>
/// <remarks>Public: <see cref="AnalyzeController.Analyze"/> is a public MVC action, and its
/// parameter type cannot be less accessible than the action itself.</remarks>
public sealed record AnalyzeRequest(int Tokens);

/// <summary>
/// Pricing shared between the minimal <c>/analyze</c>/<c>/by-size</c> endpoints
/// (<see cref="PaidServerFixture"/>) and the MVC <see cref="AnalyzeController"/>, so
/// <c>PaymentGateTests</c> can assert on exact amounts regardless of which world served the request.
/// </summary>
internal static class DynamicPricing
{
    /// <summary>0.001 EURC and 0.001 USDC per token — both have 6 decimals, so the atomic amounts match.</summary>
    public static PriceSet ForTokens(int tokens) => new([
        Price.For(KnownAssets.EurcBaseSepolia, 0.001m * tokens),
        Price.For(KnownAssets.UsdcBaseSepolia, 0.001m * tokens),
    ]);

    /// <summary>One atomic EURC unit per byte, so a larger body always costs strictly more.</summary>
    public static PriceSet ForBodySize(long bytes) =>
        Price.Atomic(KnownAssets.EurcBaseSepolia, bytes.ToString());
}
