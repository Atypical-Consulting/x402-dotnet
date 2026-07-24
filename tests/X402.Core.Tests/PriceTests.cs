using X402.Assets;
using X402.Pricing;

namespace X402.Core.Tests;

public sealed class PriceTests
{
    [Theory]
    [InlineData("0.01", "10000")]
    [InlineData("0.001", "1000")]
    [InlineData("1", "1000000")]
    [InlineData("0.000001", "1")]
    [InlineData("0", "0")]
    public void For_converts_display_units_to_atomic_units(string display, string atomic)
    {
        var price = Price.For(KnownAssets.EurcBaseSepolia, decimal.Parse(display,
            System.Globalization.CultureInfo.InvariantCulture));

        price.AtomicAmount.ShouldBe(atomic);
        price.Asset.ShouldBe(KnownAssets.EurcBaseSepolia);
    }

    [Fact]
    public void For_refuses_to_round_away_someone_s_money()
    {
        // 7 décimales sur un actif qui en a 6 : arrondir serait facturer un montant que
        // l'opérateur n'a pas écrit. On refuse.
        var tooPrecise = 0.0000001m;

        var exception = Should.Throw<ArgumentOutOfRangeException>(
            () => Price.For(KnownAssets.EurcBaseSepolia, tooPrecise));

        exception.Message.ShouldContain("6");
    }

    [Fact]
    public void For_refuses_a_negative_amount()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Price.For(KnownAssets.EurcBaseSepolia, -0.01m));
    }

    [Fact]
    public void Atomic_takes_the_amount_verbatim()
    {
        var price = Price.Atomic(KnownAssets.UsdcBaseMainnet, "123456789");

        price.AtomicAmount.ShouldBe("123456789");
        price.Asset.ShouldBe(KnownAssets.UsdcBaseMainnet);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("0x10")]
    [InlineData("abc")]
    public void Atomic_rejects_anything_that_is_not_a_non_negative_integer(string amount)
    {
        Should.Throw<ArgumentException>(() => Price.Atomic(KnownAssets.UsdcBaseMainnet, amount));
    }

    [Fact]
    public void There_is_no_conversion_between_assets()
    {
        // D9 : verrouille l'absence de toute API de change sur la surface publique.
        var names = typeof(Price).GetMethods()
            .Concat(typeof(PriceSet).GetMethods())
            .Where(m => m.IsPublic)
            .Select(m => m.Name)
            .ToList();

        names.ShouldNotContain(n => n.Contains("Convert", StringComparison.OrdinalIgnoreCase));
        names.ShouldNotContain(n => n.Contains("Exchange", StringComparison.OrdinalIgnoreCase));
        names.ShouldNotContain(n => n.Contains("Rate", StringComparison.OrdinalIgnoreCase));
    }
}
