using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace X402.Networks;

/// <summary>
/// A CAIP-2 network identifier, in <c>namespace:reference</c> form. x402 v2 identifies every
/// network this way; the v1 shorthands such as <c>base-sepolia</c> are not accepted.
/// </summary>
/// <param name="Namespace">The chain namespace, for example <c>eip155</c>.</param>
/// <param name="Reference">The chain reference, for example <c>8453</c>.</param>
public readonly record struct Caip2Network(string Namespace, string Reference)
{
    private const string EvmNamespace = "eip155";

    /// <summary>Whether this identifier denotes an EVM chain.</summary>
    public bool IsEvm => Namespace == EvmNamespace;

    /// <summary>The EVM chain identifier.</summary>
    /// <exception cref="InvalidOperationException">The network is not an EVM chain.</exception>
    public long ChainId => IsEvm
        ? long.Parse(Reference, CultureInfo.InvariantCulture)
        : throw new InvalidOperationException(
            $"'{this}' is not an EVM network, so it has no chain id.");

    /// <summary>Parses a CAIP-2 identifier.</summary>
    /// <exception cref="FormatException">The value is not a well-formed CAIP-2 identifier.</exception>
    public static Caip2Network Parse(string value) =>
        TryParse(value, out var network)
            ? network
            : throw new FormatException(
                $"'{value}' is not a CAIP-2 network identifier. Expected 'namespace:reference', " +
                "for example 'eip155:8453'. x402 v2 does not accept the v1 short names.");

    /// <summary>Attempts to parse a CAIP-2 identifier.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out Caip2Network network)
    {
        network = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        var ns = value[..separator];
        var reference = value[(separator + 1)..];
        if (reference.Contains(':'))
        {
            return false;
        }

        // CAIP-2: namespace is [-a-z0-9]{3,8}, reference is [-_a-zA-Z0-9]{1,32}.
        if (ns.Length is < 3 or > 8)
        {
            return false;
        }

        if (!ns.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            return false;
        }

        if (reference.Length > 32)
        {
            return false;
        }

        if (!reference.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return false;
        }

        // An EVM reference must be a chain id.
        if (ns == EvmNamespace && !long.TryParse(reference, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        network = new Caip2Network(ns, reference);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Namespace}:{Reference}";
}
