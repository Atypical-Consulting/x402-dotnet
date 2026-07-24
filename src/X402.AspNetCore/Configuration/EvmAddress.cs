using X402.Cryptography;

namespace X402.AspNetCore.Configuration;

/// <summary>Validation of EVM addresses, including the EIP-55 checksum.</summary>
public static class EvmAddress
{
    /// <summary>Whether the value is a well-formed 20-byte hex address with a <c>0x</c> prefix.</summary>
    public static bool IsValid(string? address) =>
        address is { Length: 42 }
        && address.StartsWith("0x", StringComparison.Ordinal)
        && address.AsSpan(2).ToString().All(char.IsAsciiHexDigit);

    /// <summary>
    /// Whether the address carries a correct EIP-55 checksum. An all-lowercase or all-uppercase
    /// address carries no checksum, so it passes.
    /// </summary>
    public static bool IsChecksumValid(string address)
    {
        if (!IsValid(address))
        {
            return false;
        }

        var body = address.AsSpan(2);
        var hasLower = false;
        var hasUpper = false;
        foreach (var c in body)
        {
            if (char.IsAsciiLetterLower(c))
            {
                hasLower = true;
            }

            if (char.IsAsciiLetterUpper(c))
            {
                hasUpper = true;
            }
        }

        if (!hasLower || !hasUpper)
        {
            return true; // No mixed case: nothing to verify.
        }

        return string.Equals(address, ToChecksum(address), StringComparison.Ordinal);
    }

    /// <summary>Rewrites an address with its EIP-55 checksum casing.</summary>
    public static string ToChecksum(string address)
    {
        var lower = address.AsSpan(2).ToString().ToLowerInvariant();
        var hash = Convert.ToHexString(
            Keccak256.ComputeHash(System.Text.Encoding.ASCII.GetBytes(lower))).ToLowerInvariant();

        var result = new char[42];
        result[0] = '0';
        result[1] = 'x';
        for (var i = 0; i < 40; i++)
        {
            var c = lower[i];
            result[i + 2] = char.IsAsciiLetter(c) && Convert.ToInt32(hash[i].ToString(), 16) >= 8
                ? char.ToUpperInvariant(c)
                : c;
        }

        return new string(result);
    }

    /// <summary>Compares two addresses, ignoring checksum casing.</summary>
    public static bool AreEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
