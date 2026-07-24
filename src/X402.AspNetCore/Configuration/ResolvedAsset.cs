using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using X402.Assets;

namespace X402.AspNetCore.Configuration;

/// <summary>The accepted assets, resolved once at start-up.</summary>
public interface IResolvedAssets
{
    /// <summary>Accepted assets, in the configured order of preference.</summary>
    IReadOnlyList<AssetDescriptor> All { get; }

    /// <summary>Finds an accepted asset by contract address, ignoring case.</summary>
    bool TryGetByAddress(string? address, [NotNullWhen(true)] out AssetDescriptor? asset);
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
