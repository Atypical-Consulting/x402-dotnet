using X402.Assets;

namespace X402.Client;

/// <summary>How this agent is willing to pay.</summary>
/// <remarks>
/// Limits are held per asset and never aggregated: a single cap spanning euros and dollars would
/// imply an exchange rate this library does not have. An asset with no declared limit is never
/// paid.
/// </remarks>
public sealed class X402ClientOptions
{
    private readonly Dictionary<string, (decimal PerRequest, decimal PerSession)> limits =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> preferences = [];

    /// <summary>Networks this agent will pay on. Empty means any network.</summary>
    public IList<string> AllowedNetworks { get; } = [];

    /// <summary>How much of a request body is buffered so the request can be replayed.</summary>
    public long MaxBufferedRequestBytes { get; set; } = 1024 * 1024;

    /// <summary>Asset symbols in the agent's order of preference.</summary>
    public IReadOnlyList<string> Preferences => preferences;

    /// <summary>Declares a preference for an asset, ahead of previously declared ones.</summary>
    public X402ClientOptions Prefer(AssetDescriptor asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return Prefer(asset.Symbol);
    }

    /// <summary>Declares a preference for an asset symbol.</summary>
    public X402ClientOptions Prefer(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (!preferences.Contains(symbol, StringComparer.OrdinalIgnoreCase))
        {
            preferences.Add(symbol);
        }

        return this;
    }

    /// <summary>Sets the spending limits for an asset, in its display units.</summary>
    /// <param name="symbol">Asset ticker symbol, for example <c>EURC</c>.</param>
    /// <param name="perRequest">Most this agent will pay for a single request.</param>
    /// <param name="perSession">Most this agent will pay in total, for this asset.</param>
    public X402ClientOptions SetLimits(string symbol, decimal perRequest, decimal perSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentOutOfRangeException.ThrowIfNegative(perRequest);
        ArgumentOutOfRangeException.ThrowIfNegative(perSession);

        limits[symbol] = (perRequest, perSession);
        return this;
    }

    /// <summary>Reads the limits declared for an asset.</summary>
    public bool TryGetLimits(string symbol, out (decimal PerRequest, decimal PerSession) declared) =>
        limits.TryGetValue(symbol, out declared);
}
