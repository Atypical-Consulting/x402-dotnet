using System.Globalization;
using System.Numerics;
using X402.Assets;

namespace X402.Pricing;

/// <summary>
/// An amount due, bound to the asset it is denominated in.
/// </summary>
/// <remarks>
/// A price is never a bare number. Charging 0.010 EURC and 0.011 USDC for the same resource is a
/// commercial decision an operator writes down; this library performs no currency conversion and
/// offers no API to request one.
/// </remarks>
public readonly record struct Price
{
    private Price(AssetDescriptor asset, string atomicAmount)
    {
        Asset = asset;
        AtomicAmount = atomicAmount;
    }

    /// <summary>The asset this price is denominated in.</summary>
    public AssetDescriptor Asset { get; }

    /// <summary>The amount, in the asset's atomic units, as the protocol carries it.</summary>
    public string AtomicAmount { get; }

    /// <summary>
    /// Builds a price from an amount in the asset's display units — euros for EURC, dollars for USDC.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The amount is negative, or needs more decimals than the asset has. Rounding here would bill
    /// an amount nobody wrote, so it is refused instead.
    /// </exception>
    public static Price For(AssetDescriptor asset, decimal displayAmount)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (displayAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(displayAmount), displayAmount,
                "A price cannot be negative.");
        }

        var scale = (decimal)BigInteger.Pow(10, asset.Decimals);
        var scaled = displayAmount * scale;

        if (scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentOutOfRangeException(nameof(displayAmount), displayAmount,
                $"{asset.Symbol} has {asset.Decimals} decimals, so {displayAmount} cannot be " +
                "represented exactly in atomic units. Rounding is refused — pick a representable " +
                "amount, or use Price.Atomic to state the atomic amount yourself.");
        }

        return new Price(asset, ((BigInteger)scaled).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Builds a price from an amount already expressed in the asset's atomic units.</summary>
    /// <exception cref="ArgumentException">The amount is not a non-negative decimal integer.</exception>
    public static Price Atomic(AssetDescriptor asset, string atomicAmount)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrEmpty(atomicAmount);

        if (!atomicAmount.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                $"'{atomicAmount}' is not an atomic amount. Expected a non-negative decimal " +
                "integer with no sign, separator or prefix.", nameof(atomicAmount));
        }

        return new Price(asset, atomicAmount);
    }
}
