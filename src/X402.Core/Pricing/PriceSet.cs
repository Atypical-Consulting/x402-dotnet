using System.Collections;

namespace X402.Pricing;

/// <summary>
/// The prices a resource is offered at, one per accepted asset.
/// </summary>
/// <remarks>
/// The order is significant: it becomes the order of the <c>accepts</c> array in the payment
/// demand, which is the preference the server announces to payers.
/// </remarks>
public readonly record struct PriceSet : IReadOnlyList<Price>
{
    private readonly Price[] prices;

    /// <summary>Builds a price set from one price per accepted asset.</summary>
    /// <exception cref="ArgumentException">
    /// The set is empty, prices the same asset twice, or mixes networks.
    /// </exception>
    public PriceSet(IReadOnlyList<Price> prices)
    {
        ArgumentNullException.ThrowIfNull(prices);

        if (prices.Count == 0)
        {
            throw new ArgumentException("A price set needs at least one price.", nameof(prices));
        }

        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var price in prices)
        {
            if (!addresses.Add(price.Asset.Address))
            {
                throw new ArgumentException(
                    $"{price.Asset.Symbol} ({price.Asset.Address}) is priced more than once. " +
                    "Each accepted asset takes exactly one price.", nameof(prices));
            }
        }

        var network = prices[0].Asset.Network;
        foreach (var price in prices)
        {
            if (price.Asset.Network != network)
            {
                throw new ArgumentException(
                    $"A price set cannot mix networks: found '{network}' and " +
                    $"'{price.Asset.Network}'. A server offers requirements on one network.",
                    nameof(prices));
            }
        }

        this.prices = [.. prices];
    }

    /// <summary>The number of priced assets.</summary>
    public int Count => prices?.Length ?? 0;

    /// <summary>The price at the given position, in the server's order of preference.</summary>
    public Price this[int index] => prices[index];

    /// <summary>Wraps a single price.</summary>
    public static implicit operator PriceSet(Price price) => new([price]);

    /// <summary>Wraps one price per accepted asset.</summary>
    public static implicit operator PriceSet(Price[] prices) => new(prices);

    /// <inheritdoc />
    public IEnumerator<Price> GetEnumerator() => ((IEnumerable<Price>)(prices ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
