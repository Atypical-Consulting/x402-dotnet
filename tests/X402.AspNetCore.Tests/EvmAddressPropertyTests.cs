using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using X402.AspNetCore.Configuration;
using X402.PropertyTesting;

namespace X402.AspNetCore.Tests;

public sealed class EvmAddressPropertyTests
{
    private static readonly Arbitrary<string> Addresses = X402Arbitraries.Address.ToArbitrary();

    [Property(MaxTest = 300, Replay = "(26,9)")]
    public Property ToChecksum_is_idempotent() =>
        Prop.ForAll(Addresses, a => EvmAddress.ToChecksum(EvmAddress.ToChecksum(a)) == EvmAddress.ToChecksum(a));

    [Property(MaxTest = 300, Replay = "(26,11)")]
    public Property ToChecksum_output_is_checksum_valid() =>
        Prop.ForAll(Addresses, a => EvmAddress.IsChecksumValid(EvmAddress.ToChecksum(a)));

    [Property(MaxTest = 300, Replay = "(26,13)")]
    public Property ToChecksum_ignores_hex_case() =>
        Prop.ForAll(Addresses, a => EvmAddress.ToChecksum("0x" + a[2..].ToUpperInvariant()) == EvmAddress.ToChecksum(a));

    [Property(MaxTest = 300, Replay = "(26,15)")]
    public Property AreEqual_ignores_hex_case() =>
        Prop.ForAll(Addresses, a => EvmAddress.AreEqual(a, "0x" + a[2..].ToUpperInvariant()));
}
