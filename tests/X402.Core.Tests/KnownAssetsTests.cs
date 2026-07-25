using X402.Assets;
using X402.Networks;

namespace X402.Core.Tests;

public sealed class KnownAssetsTests
{
    // Values read on-chain on 2026-07-24 via eth_call against the public Base and Base Sepolia
    // RPCs (name(), version(), decimals(), authorizationState()). See spec §2.1.7.
    // NEVER correct this table from documentation: re-read it on-chain.
    [Theory]
    [InlineData("EURC", "eip155:84532", "0x808456652fdb597867f38412077A9182bf77359F", "EURC", "2", 6)]
    [InlineData("EURC", "eip155:8453", "0x60a3E35Cc302bFA44Cb288Bc5a4F316Fdb1adb42", "EURC", "2", 6)]
    [InlineData("USDC", "eip155:84532", "0x036CbD53842c5426634e7929541eC2318f3dCF7e", "USDC", "2", 6)]
    [InlineData("USDC", "eip155:8453", "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913", "USD Coin", "2", 6)]
    public void Catalogue_matches_the_on_chain_values(
        string symbol, string network, string address, string name, string version, int decimals)
    {
        KnownAssets.TryGet(network, symbol, out var asset).ShouldBeTrue();

        asset.Address.ShouldBe(address);
        asset.Eip712Name.ShouldBe(name);
        asset.Eip712Version.ShouldBe(version);
        asset.Decimals.ShouldBe(decimals);
        asset.Network.ShouldBe(network);
    }

    [Fact]
    public void Usdc_changes_its_eip712_name_between_networks_but_eurc_does_not()
    {
        // The asymmetry that costs money. Spelled out here so that a well-intentioned
        // "harmonization" of the catalogue breaks this test instead of breaking mainnet payments.
        KnownAssets.UsdcBaseSepolia.Eip712Name.ShouldBe("USDC");
        KnownAssets.UsdcBaseMainnet.Eip712Name.ShouldBe("USD Coin");

        KnownAssets.EurcBaseSepolia.Eip712Name.ShouldBe("EURC");
        KnownAssets.EurcBaseMainnet.Eip712Name.ShouldBe("EURC");
    }

    [Fact]
    public void TryGet_is_case_insensitive_on_the_symbol()
    {
        KnownAssets.TryGet(KnownNetworks.BaseSepolia, "eurc", out var asset).ShouldBeTrue();
        asset.Symbol.ShouldBe("EURC");
    }

    [Fact]
    public void TryGet_fails_for_an_unknown_symbol_rather_than_guessing()
    {
        KnownAssets.TryGet(KnownNetworks.BaseSepolia, "DAI", out _).ShouldBeFalse();
    }

    [Fact]
    public void ForNetwork_lists_the_euro_asset_first()
    {
        // The catalogue's order is the default offered to operators: the euro comes first (§3.1).
        KnownAssets.ForNetwork(KnownNetworks.BaseSepolia)
            .Select(a => a.Symbol)
            .ShouldBe(["EURC", "USDC"]);
    }

    [Fact]
    public void ForNetwork_is_empty_for_an_unknown_network()
    {
        KnownAssets.ForNetwork("eip155:1").ShouldBeEmpty();
    }
}
