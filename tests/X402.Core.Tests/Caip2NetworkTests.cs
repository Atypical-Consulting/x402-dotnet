using X402.Networks;

namespace X402.Core.Tests;

public sealed class Caip2NetworkTests
{
    [Theory]
    [InlineData("eip155:8453", "eip155", "8453")]
    [InlineData("eip155:84532", "eip155", "84532")]
    [InlineData("solana:EtWTRABZaYq6iMfeYKouRu166VU2xqa1", "solana", "EtWTRABZaYq6iMfeYKouRu166VU2xqa1")]
    public void Parse_splits_namespace_from_reference(string value, string ns, string reference)
    {
        var network = Caip2Network.Parse(value);

        network.Namespace.ShouldBe(ns);
        network.Reference.ShouldBe(reference);
        network.ToString().ShouldBe(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("eip155")]
    [InlineData("eip155:")]
    [InlineData(":8453")]
    [InlineData("eip155:8453:extra")]
    [InlineData("base-sepolia")]   // identifiant v1, explicitement non supporté
    [InlineData("eip155:-8453")]   // negative chain id, rejected
    public void TryParse_rejects_malformed_identifiers(string value)
    {
        Caip2Network.TryParse(value, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParse_rejects_null()
    {
        Caip2Network.TryParse(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void ChainId_reads_the_reference_of_an_evm_network()
    {
        Caip2Network.Parse(KnownNetworks.BaseSepolia).ChainId.ShouldBe(84532L);
        Caip2Network.Parse(KnownNetworks.BaseMainnet).ChainId.ShouldBe(8453L);
    }

    [Fact]
    public void ChainId_throws_for_a_non_evm_network()
    {
        var solana = Caip2Network.Parse("solana:EtWTRABZaYq6iMfeYKouRu166VU2xqa1");

        solana.IsEvm.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => solana.ChainId);
    }

    [Fact]
    public void ChainId_throws_for_an_evm_network_with_invalid_reference()
    {
        // Direct construction bypasses TryParse validation.
        var invalid = new Caip2Network("eip155", "banana");

        invalid.IsEvm.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => invalid.ChainId);
    }
}
