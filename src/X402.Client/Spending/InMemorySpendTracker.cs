using System.Globalization;
using X402.Assets;

namespace X402.Client.Spending;

/// <summary>
/// In-process <see cref="ISpendTracker"/>. Running totals live only for the lifetime of this
/// instance and are not persisted; a new process starts every session budget back at zero.
/// </summary>
public sealed class InMemorySpendTracker : ISpendTracker
{
    private readonly X402ClientOptions options;
    private readonly Lock gate = new();

    // Keyed by AssetIdentity.Key (network + contract address), not by symbol: two catalogued
    // assets can share a symbol (EURC on Base Sepolia and EURC on Base mainnet) and must not share
    // a running total.
    private readonly Dictionary<string, decimal> spentByAsset = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a tracker that enforces the limits declared in <paramref name="options"/>.</summary>
    public InMemorySpendTracker(X402ClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.options = options;
    }

    /// <inheritdoc />
    public void EnsureWithinLimits(AssetDescriptor asset, decimal amount)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        lock (gate)
        {
            Check(asset, amount);
        }
    }

    /// <inheritdoc />
    public void Record(AssetDescriptor asset, decimal amount)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        lock (gate)
        {
            Add(asset, amount);
        }
    }

    /// <inheritdoc />
    public void EnsureWithinLimitsAndRecord(AssetDescriptor asset, decimal amount)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        lock (gate)
        {
            Check(asset, amount);
            Add(asset, amount);
        }
    }

    /// <inheritdoc />
    public void Release(AssetDescriptor asset, decimal amount)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        lock (gate)
        {
            var key = AssetIdentity.Key(asset);
            var remaining = spentByAsset.TryGetValue(key, out var current)
                ? current - amount
                : -amount;
            spentByAsset[key] = remaining < 0m ? 0m : remaining;
        }
    }

    // Must run inside `gate`.
    private void Check(AssetDescriptor asset, decimal amount)
    {
        if (!options.TryGetLimits(asset, out var limits))
        {
            throw new SpendingLimitExceededException(
                $"{asset.Symbol} on {asset.Network} has no spending limit declared; refusing to " +
                $"pay {Format(amount)} {asset.Symbol}.");
        }

        if (amount > limits.PerRequest)
        {
            throw new SpendingLimitExceededException(
                $"{asset.Symbol} on {asset.Network} request of {Format(amount)} exceeds the " +
                $"per-request limit of {Format(limits.PerRequest)} {asset.Symbol}.");
        }

        var key = AssetIdentity.Key(asset);
        var alreadySpent = spentByAsset.TryGetValue(key, out var current) ? current : 0m;
        var projected = alreadySpent + amount;
        if (projected > limits.PerSession)
        {
            throw new SpendingLimitExceededException(
                $"{asset.Symbol} on {asset.Network} request of {Format(amount)} would bring " +
                $"session spend to {Format(projected)}, exceeding the per-session limit of " +
                $"{Format(limits.PerSession)} {asset.Symbol}.");
        }
    }

    // Must run inside `gate`.
    private void Add(AssetDescriptor asset, decimal amount)
    {
        var key = AssetIdentity.Key(asset);
        spentByAsset[key] = (spentByAsset.TryGetValue(key, out var current) ? current : 0m) + amount;
    }

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
