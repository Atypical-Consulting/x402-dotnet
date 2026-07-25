using X402.Assets;

namespace X402.Client;

/// <summary>
/// Computes the key that identifies an asset for spending limits and tracked spend: its network
/// plus its contract address, not its ticker symbol.
/// </summary>
/// <remarks>
/// Two catalogued assets can share a symbol — EURC on Base Sepolia and EURC on Base mainnet, for
/// instance, per <see cref="KnownAssets"/> — while being different contracts an operator would
/// price very differently: a generous limit for play money on a testnet, a tight one for real
/// money on mainnet. Keying by symbol alone would silently merge those two limits, and the spend
/// against them, into one. That is exactly what the per-asset guarantee recorded in ADR 0002
/// (multi-asset settlement) exists to prevent.
/// </remarks>
internal static class AssetIdentity
{
    /// <summary>The dictionary key identifying <paramref name="asset"/>.</summary>
    public static string Key(AssetDescriptor asset) => $"{asset.Network}|{asset.Address}";
}
