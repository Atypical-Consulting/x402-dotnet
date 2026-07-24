using X402.AspNetCore.Configuration;

namespace X402.AspNetCore.Tests;

public sealed class EvmAddressTests
{
    [Theory]
    [InlineData("0x209693Bc6afc0C5328bA36FaF03C514EF312287C")]
    [InlineData("0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913")]
    [InlineData("0x60a3E35Cc302bFA44Cb288Bc5a4F316Fdb1adb42")]
    public void Checksummed_addresses_are_accepted(string address)
    {
        EvmAddress.IsValid(address).ShouldBeTrue();
        EvmAddress.IsChecksumValid(address).ShouldBeTrue();
    }

    [Fact]
    public void All_lowercase_is_a_valid_address_without_a_checksum()
    {
        const string lower = "0x209693bc6afc0c5328ba36faf03c514ef312287c";

        EvmAddress.IsValid(lower).ShouldBeTrue();
        EvmAddress.IsChecksumValid(lower).ShouldBeTrue(); // no mixed case: nothing to verify
    }

    [Fact]
    public void A_mixed_case_address_with_a_wrong_checksum_is_rejected()
    {
        // A single character changes case: exactly what a copy-paste mangles.
        const string wrong = "0x209693bC6afc0C5328bA36FaF03C514EF312287C";

        EvmAddress.IsValid(wrong).ShouldBeTrue();
        EvmAddress.IsChecksumValid(wrong).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("209693Bc6afc0C5328bA36FaF03C514EF312287C")]        // no prefix
    [InlineData("0x209693Bc6afc0C5328bA36FaF03C514EF312287")]       // 39 characters
    [InlineData("0x209693Bc6afc0C5328bA36FaF03C514EF312287CD")]     // 41 characters
    [InlineData("0xZZ9693Bc6afc0C5328bA36FaF03C514EF312287C")]      // not hexadecimal
    public void Malformed_values_are_rejected(string address)
    {
        EvmAddress.IsValid(address).ShouldBeFalse();
    }

    [Fact]
    public void AreEqual_ignores_case()
    {
        EvmAddress.AreEqual(
            "0x209693Bc6afc0C5328bA36FaF03C514EF312287C",
            "0x209693bc6afc0c5328ba36faf03c514ef312287c").ShouldBeTrue();
    }
}
