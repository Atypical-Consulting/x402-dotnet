using X402.Assets;

namespace X402.Client;

/// <summary>How this agent is willing to pay.</summary>
/// <remarks>
/// Limits are held per asset identity — network plus contract address, not ticker symbol — and
/// never aggregated: a single cap spanning euros and dollars would imply an exchange rate this
/// library does not have (see ADR 0002, multi-asset settlement). Keying by symbol alone would
/// also conflate, say, EURC on a testnet with EURC on mainnet: an operator wants a generous limit
/// for the former's play money and a tight one for the latter's real money, and a shared counter
/// makes that impossible. An asset with no declared limit is never paid.
/// </remarks>
public sealed class X402ClientOptions
{
    private readonly Dictionary<string, (decimal PerRequest, decimal PerSession)> limits =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> preferences = [];

    /// <summary>Networks this agent will pay on. Empty means any network.</summary>
    public IList<string> AllowedNetworks { get; } = [];

    /// <summary>How much of a request body is buffered so the request can be replayed.</summary>
    /// <remarks>
    /// A request whose declared <c>Content-Length</c> exceeds this is sent unbuffered: it works
    /// normally against an endpoint that does not ask for payment, and a 402 for it is refused with
    /// <see cref="PaymentRejectedException"/> since its body cannot be replayed. A body of unknown
    /// length is buffered, and fails with <see cref="HttpRequestException"/> if it exceeds this.
    /// </remarks>
    public long MaxBufferedRequestBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// The longest authorization validity this agent signs, in seconds. A requirement whose
    /// <c>maxTimeoutSeconds</c> is above this (or below 1) is not paid. Defaults to 3600, the
    /// most an <c>X402.AspNetCore</c> server can ask for.
    /// </summary>
    /// <remarks>
    /// A signed authorization stays spendable until its <c>validBefore</c>, which follows the
    /// server's <c>maxTimeoutSeconds</c>. Without this bound a server could ask for one valid for
    /// decades.
    /// </remarks>
    public int MaxAuthorizationValiditySeconds { get; set; } = 3600;

    /// <summary>Asset symbols in the agent's order of preference.</summary>
    public IReadOnlyList<string> Preferences => preferences;

    /// <summary>
    /// Declares a preference for an asset, ranked after previously declared ones: the first
    /// declared preference wins.
    /// </summary>
    public X402ClientOptions Prefer(AssetDescriptor asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return Prefer(asset.Symbol);
    }

    /// <summary>
    /// Declares a preference for an asset symbol, ranked after previously declared ones: the first
    /// declared preference wins.
    /// </summary>
    public X402ClientOptions Prefer(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (!preferences.Contains(symbol, StringComparer.OrdinalIgnoreCase))
        {
            preferences.Add(symbol);
        }

        return this;
    }

    /// <summary>
    /// Sets the spending limits for an asset, in its display units, keyed by the asset's network
    /// and contract address — never by symbol alone, since two catalogued assets can share one.
    /// </summary>
    /// <param name="asset">
    /// The asset to limit. <see cref="X402PaymentHandler"/> only pays assets in
    /// <see cref="KnownAssets"/> — it never signs against an EIP-712 domain it has not catalogued —
    /// so a limit for any other asset is never used.
    /// </param>
    /// <param name="perRequest">Most this agent will pay for a single request.</param>
    /// <param name="perSession">Most this agent will pay in total, for this asset.</param>
    public X402ClientOptions SetLimits(AssetDescriptor asset, decimal perRequest, decimal perSession)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentOutOfRangeException.ThrowIfNegative(perRequest);
        ArgumentOutOfRangeException.ThrowIfNegative(perSession);

        limits[AssetIdentity.Key(asset)] = (perRequest, perSession);
        return this;
    }

    /// <summary>
    /// Sets the spending limits for a catalogued asset, looked up by network and ticker symbol.
    /// A network is required alongside the symbol so that, for example, EURC on a testnet and
    /// EURC on mainnet cannot collide into a single shared limit.
    /// </summary>
    /// <param name="network">CAIP-2 network identifier, for example <c>KnownNetworks.BaseSepolia</c>.</param>
    /// <param name="symbol">Asset ticker symbol, for example <c>EURC</c>.</param>
    /// <param name="perRequest">Most this agent will pay for a single request.</param>
    /// <param name="perSession">Most this agent will pay in total, for this asset.</param>
    /// <exception cref="ArgumentException">
    /// No <see cref="KnownAssets"/> entry matches <paramref name="network"/> and
    /// <paramref name="symbol"/>. <see cref="X402PaymentHandler"/> only pays catalogued assets.
    /// </exception>
    public X402ClientOptions SetLimits(string network, string symbol, decimal perRequest, decimal perSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(network);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        if (!KnownAssets.TryGet(network, symbol, out var asset))
        {
            throw new ArgumentException(
                $"No catalogued asset matches '{symbol}' on '{network}'. X402PaymentHandler only " +
                "pays assets listed in KnownAssets.",
                nameof(symbol));
        }

        return SetLimits(asset, perRequest, perSession);
    }

    /// <summary>Reads the limits declared for an asset's network and contract address.</summary>
    public bool TryGetLimits(AssetDescriptor asset, out (decimal PerRequest, decimal PerSession) declared)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return limits.TryGetValue(AssetIdentity.Key(asset), out declared);
    }

    /// <summary>
    /// Every limit declared so far, keyed by <see cref="AssetIdentity.Key"/> (network plus contract
    /// address). Internal: only <see cref="X402ClientOptionsValidator"/> needs the whole set at
    /// once — a public consumer asks <see cref="TryGetLimits"/> for one asset at a time.
    /// </summary>
    internal IReadOnlyDictionary<string, (decimal PerRequest, decimal PerSession)> Limits => limits;
}
