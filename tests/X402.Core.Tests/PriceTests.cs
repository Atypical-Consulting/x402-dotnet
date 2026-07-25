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
        // 7 decimals on an asset that has 6: rounding would charge an amount the
        // operator never wrote. We refuse.
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
    public void For_refuses_amounts_that_overflow_decimal_multiplication()
    {
        // Most real ERC-20s use 18 decimals. At 18 decimals, overflow occurs around 79.2 billion.
        var highDecimalAsset = new X402.Assets.AssetDescriptor
        {
            Network = X402.Networks.KnownNetworks.BaseSepolia,
            Address = "0x0000000000000000000000000000000000000001",
            Symbol = "TEST",
            Decimals = 18,
            Eip712Name = "Test",
            Eip712Version = "1",
        };

        var exception = Should.Throw<ArgumentOutOfRangeException>(
            () => Price.For(highDecimalAsset, 100_000_000_000m));

        exception.Message.ShouldContain("TEST");
        exception.Message.ShouldContain("18");
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
        // D9: locks in the absence of any exchange API on the public surface.
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
