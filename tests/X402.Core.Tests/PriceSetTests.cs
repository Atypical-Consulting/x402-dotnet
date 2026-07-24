using X402.Assets;
using X402.Pricing;

namespace X402.Core.Tests;

public sealed class PriceSetTests
{
    [Fact]
    public void A_single_price_converts_implicitly()
    {
        PriceSet set = Price.For(KnownAssets.EurcBaseSepolia, 0.01m);

        set.Count.ShouldBe(1);
        set[0].Asset.Symbol.ShouldBe("EURC");
    }

    [Fact]
    public void An_array_converts_implicitly_and_keeps_its_order()
    {
        // L'ordre est une promesse commerciale : il devient l'ordre du tableau accepts.
        PriceSet set = new[]
        {
            Price.For(KnownAssets.EurcBaseSepolia, 0.010m),
            Price.For(KnownAssets.UsdcBaseSepolia, 0.011m),
        };

        set.Select(p => p.Asset.Symbol).ShouldBe(["EURC", "USDC"]);
    }

    [Fact]
    public void An_empty_set_is_rejected()
    {
        Should.Throw<ArgumentException>(() => new PriceSet([]));
    }

    [Fact]
    public void The_same_asset_cannot_be_priced_twice()
    {
        Should.Throw<ArgumentException>(() => new PriceSet([
            Price.For(KnownAssets.EurcBaseSepolia, 0.010m),
            Price.For(KnownAssets.EurcBaseSepolia, 0.020m),
        ]));
    }

    [Fact]
    public void Assets_from_different_networks_cannot_be_mixed()
    {
        // Une réponse 402 annonce des exigences ; les mélanger entre réseaux produirait
        // une offre que le serveur ne sait pas honorer.
        Should.Throw<ArgumentException>(() => new PriceSet([
            Price.For(KnownAssets.EurcBaseSepolia, 0.010m),
            Price.For(KnownAssets.UsdcBaseMainnet, 0.011m),
        ]));
    }

    [Fact]
    public void Default_constructed_price_set_is_empty_and_indexing_it_throws()
    {
        var empty = default(PriceSet);

        empty.Count.ShouldBe(0);
        empty.ShouldBeEmpty();

        Should.Throw<IndexOutOfRangeException>(() => empty[0]);
    }
}
