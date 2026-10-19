using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using X402.Assets;
using X402.Pricing;

namespace X402.AspNetCore.Configuration;

/// <summary>The accepted assets, resolved once at start-up.</summary>
public interface IResolvedAssets
{
    /// <summary>Accepted assets, in the configured order of preference.</summary>
    IReadOnlyList<AssetDescriptor> All { get; }

    /// <summary>Finds an accepted asset by contract address, ignoring case.</summary>
    bool TryGetByAddress(string? address, [NotNullWhen(true)] out AssetDescriptor? asset);
}

internal static class ResolvedAssetsExtensions
{
    /// <summary>
    /// Throws when <paramref name="prices"/> names an asset this server does not accept: such a
    /// price would be advertised, then every correctly signed payment for it refused.
    /// </summary>
    /// <param name="assets">The accepted assets.</param>
    /// <param name="prices">The prices to check.</param>
    /// <param name="pricedBy">What carries the prices, for the message — e.g. <c>Route '/x'</c>.</param>
    public static void EnsureAccepted(this IResolvedAssets assets, PriceSet prices, string pricedBy)
    {
        foreach (var price in prices)
        {
            if (!assets.TryGetByAddress(price.Asset.Address, out _))
            {
                throw new InvalidOperationException(
                    $"{pricedBy} is priced in {price.Asset.Symbol} ({price.Asset.Address}) on " +
                    $"{price.Asset.Network}, which this server does not accept. Add it to " +
                    "X402:Assets, or price it in an accepted asset: " +
                    $"{string.Join(", ", assets.All.Select(a => a.Symbol))}.");
            }
        }
    }
}

internal sealed class ResolvedAssets : IResolvedAssets
{
    public ResolvedAssets(IOptions<X402Options> options)
    {
        // Validation has already run: every entry is resolvable.
        All = [.. options.Value.Assets.Select(a => Resolve(a, options.Value.Network))];
    }

    public IReadOnlyList<AssetDescriptor> All { get; }

    public bool TryGetByAddress(string? address, [NotNullWhen(true)] out AssetDescriptor? asset)
    {
        asset = All.FirstOrDefault(a => EvmAddress.AreEqual(a.Address, address));
        return asset is not null;
    }

    internal static AssetDescriptor Resolve(AssetConfiguration configuration, string network)
    {
        if (configuration.Address is null
            && configuration.Symbol is { } symbol
            && KnownAssets.TryGet(network, symbol, out var catalogued))
        {
            return catalogued;
        }

        return new AssetDescriptor
        {
            Network = network,
            Address = configuration.Address!,
            Symbol = configuration.Symbol ?? configuration.Address!,
            Decimals = configuration.Decimals!.Value,
            Eip712Name = configuration.Eip712Name!,
            Eip712Version = configuration.Eip712Version!,
        };
    }
}
