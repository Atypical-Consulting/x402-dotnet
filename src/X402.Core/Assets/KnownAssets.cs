using System.Diagnostics.CodeAnalysis;
using X402.Networks;

namespace X402.Assets;

/// <summary>
/// Ready-made descriptors for the stablecoins this library ships profiles for. The catalogue is a
/// convenience, not an allow-list: <see cref="AssetDescriptor"/> is public and any EIP-3009 token
/// can be described by hand.
/// </summary>
/// <remarks>
/// Every value below was read on-chain on 2026-07-24 (<c>name()</c>, <c>version()</c>,
/// <c>decimals()</c>, <c>authorizationState()</c>), not copied from documentation. Re-read them
/// on-chain before changing any of them.
/// </remarks>
public static class KnownAssets
{
    /// <summary>EURC on Base Sepolia.</summary>
    public static AssetDescriptor EurcBaseSepolia { get; } = new()
    {
        Network = KnownNetworks.BaseSepolia,
        Address = "0x808456652fdb597867f38412077A9182bf77359F",
        Symbol = "EURC",
        Decimals = 6,
        Eip712Name = "EURC",
        Eip712Version = "2",
    };

    /// <summary>EURC on Base mainnet.</summary>
    public static AssetDescriptor EurcBaseMainnet { get; } = new()
    {
        Network = KnownNetworks.BaseMainnet,
        Address = "0x60a3E35Cc302bFA44Cb288Bc5a4F316Fdb1adb42",
        Symbol = "EURC",
        Decimals = 6,
        Eip712Name = "EURC",
        Eip712Version = "2",
    };

    /// <summary>USDC on Base Sepolia. Note the domain name differs from mainnet.</summary>
    public static AssetDescriptor UsdcBaseSepolia { get; } = new()
    {
        Network = KnownNetworks.BaseSepolia,
        Address = "0x036CbD53842c5426634e7929541eC2318f3dCF7e",
        Symbol = "USDC",
        Decimals = 6,
        Eip712Name = "USDC",
        Eip712Version = "2",
    };

    /// <summary>USDC on Base mainnet. Its EIP-712 domain name is "USD Coin", not "USDC".</summary>
    public static AssetDescriptor UsdcBaseMainnet { get; } = new()
    {
        Network = KnownNetworks.BaseMainnet,
        Address = "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913",
        Symbol = "USDC",
        Decimals = 6,
        Eip712Name = "USD Coin",
        Eip712Version = "2",
    };

    // The euro comes first: it is the default this library wants operators to reach for.
    private static readonly Dictionary<string, AssetDescriptor[]> ByNetwork = new()
    {
        [KnownNetworks.BaseSepolia] = [EurcBaseSepolia, UsdcBaseSepolia],
        [KnownNetworks.BaseMainnet] = [EurcBaseMainnet, UsdcBaseMainnet],
    };

    /// <summary>Looks up a catalogued asset by network and ticker symbol.</summary>
    public static bool TryGet(string network, string symbol, [NotNullWhen(true)] out AssetDescriptor? asset)
    {
        asset = null;
        if (!ByNetwork.TryGetValue(network, out var assets))
        {
            return false;
        }

        asset = assets.FirstOrDefault(
            a => string.Equals(a.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        return asset is not null;
    }

    /// <summary>Every catalogued asset for a network, euro-denominated first. Empty if unknown.</summary>
    public static IReadOnlyList<AssetDescriptor> ForNetwork(string network) =>
        ByNetwork.TryGetValue(network, out var assets) ? assets : [];
}
