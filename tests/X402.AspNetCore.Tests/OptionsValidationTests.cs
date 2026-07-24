using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.DependencyInjection;
using X402.Networks;

namespace X402.AspNetCore.Tests;

public sealed class OptionsValidationTests
{
    private static X402Options Valid()
    {
        var options = new X402Options
        {
            PayTo = "0x209693Bc6afc0C5328bA36FaF03C514EF312287C",
            Network = KnownNetworks.BaseSepolia,
            FacilitatorUrl = new Uri("https://x402.org/facilitator"),
        };
        options.Assets.Add(new AssetConfiguration { Symbol = "EURC" });
        return options;
    }

    private static void Validate(Action<X402Options> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddX402(options =>
        {
            var valid = Valid();
            options.PayTo = valid.PayTo;
            options.Network = valid.Network;
            options.FacilitatorUrl = valid.FacilitatorUrl;
            foreach (var asset in valid.Assets)
            {
                options.Assets.Add(asset);
            }

            configure(options);
        });

        using var provider = services.BuildServiceProvider();
        // Forces validation, the way ValidateOnStart would when the host starts.
        _ = provider.GetRequiredService<IOptions<X402Options>>().Value;
    }

    [Fact]
    public void A_complete_configuration_validates()
    {
        Should.NotThrow(() => Validate(_ => { }));
    }

    [Fact]
    public void An_absent_payee_fails_at_startup()
    {
        var exception = Should.Throw<OptionsValidationException>(
            () => Validate(o => o.PayTo = ""));

        exception.Message.ShouldContain("PayTo");
    }

    [Fact]
    public void A_payee_with_a_broken_checksum_fails_at_startup()
    {
        var exception = Should.Throw<OptionsValidationException>(
            () => Validate(o => o.PayTo = "0x209693bC6afc0C5328bA36FaF03C514EF312287C"));

        exception.Message.ShouldContain("checksum");
    }

    [Fact]
    public void A_v1_network_name_fails_at_startup()
    {
        // "base-sepolia" is a v1 identifier. Letting it through would produce 402s nobody knows
        // how to pay.
        var exception = Should.Throw<OptionsValidationException>(
            () => Validate(o => o.Network = "base-sepolia"));

        exception.Message.ShouldContain("CAIP-2");
    }

    [Fact]
    public void An_empty_asset_list_fails_at_startup()
    {
        var exception = Should.Throw<OptionsValidationException>(
            () => Validate(o => o.Assets.Clear()));

        exception.Message.ShouldContain("Assets");
    }

    [Fact]
    public void An_unknown_symbol_fails_at_startup_rather_than_being_guessed()
    {
        var exception = Should.Throw<OptionsValidationException>(() => Validate(o =>
        {
            o.Assets.Clear();
            o.Assets.Add(new AssetConfiguration { Symbol = "DAI" });
        }));

        exception.Message.ShouldContain("DAI");
        exception.Message.ShouldContain("EURC");   // the message says what IS available
    }

    [Fact]
    public void A_partially_described_asset_fails_at_startup()
    {
        var exception = Should.Throw<OptionsValidationException>(() => Validate(o =>
        {
            o.Assets.Clear();
            o.Assets.Add(new AssetConfiguration
            {
                Address = "0x808456652fdb597867f38412077A9182bf77359F",
                Decimals = 6,
                // Eip712Name and Eip712Version missing
            });
        }));

        exception.Message.ShouldContain("Eip712Name");
    }

    [Fact]
    public void A_fully_described_asset_outside_the_catalogue_validates()
    {
        Should.NotThrow(() => Validate(o =>
        {
            o.Assets.Clear();
            o.Assets.Add(new AssetConfiguration
            {
                Symbol = "TEST",
                Address = "0x1111111111111111111111111111111111111111",
                Decimals = 18,
                Eip712Name = "Test Token",
                Eip712Version = "1",
            });
        }));
    }

    [Fact]
    public void The_same_asset_twice_fails_at_startup()
    {
        var exception = Should.Throw<OptionsValidationException>(() => Validate(o =>
        {
            o.Assets.Add(new AssetConfiguration { Symbol = "EURC" });
        }));

        exception.Message.ShouldContain("more than once");
    }

    [Fact]
    public void An_absent_facilitator_url_fails_at_startup()
    {
        var exception = Should.Throw<OptionsValidationException>(
            () => Validate(o => o.FacilitatorUrl = null));

        exception.Message.ShouldContain("FacilitatorUrl");
    }

    [Fact]
    public void A_plaintext_facilitator_url_fails_at_startup()
    {
        var exception = Should.Throw<OptionsValidationException>(
            () => Validate(o => o.FacilitatorUrl = new Uri("http://facilitator.example.com")));

        exception.Message.ShouldContain("https");
    }

    [Fact]
    public void A_plaintext_facilitator_on_localhost_is_allowed_for_tests()
    {
        Should.NotThrow(() => Validate(
            o => o.FacilitatorUrl = new Uri("http://localhost:5000")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void An_out_of_range_timeout_fails_at_startup(int seconds)
    {
        Should.Throw<OptionsValidationException>(
            () => Validate(o => o.MaxTimeoutSeconds = seconds));
    }

    [Fact]
    public void Options_expose_no_private_key_property()
    {
        // The non-custodial constraint is structural: it must stay unbreakable by accident. This
        // test fails if someone adds a signing secret.
        var suspicious = typeof(X402Options).GetProperties()
            .Select(p => p.Name)
            .Where(n => n.Contains("Key", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Mnemonic", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Wallet", StringComparison.OrdinalIgnoreCase))
            .ToList();

        suspicious.ShouldBeEmpty();
    }

    [Fact]
    public void Resolved_assets_keep_the_declared_order_and_carry_the_right_eip712_domain()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddX402(options =>
        {
            options.PayTo = "0x209693Bc6afc0C5328bA36FaF03C514EF312287C";
            options.Network = KnownNetworks.BaseMainnet;
            options.FacilitatorUrl = new Uri("https://facilitator.example.com");
            options.Assets.Add(new AssetConfiguration { Symbol = "EURC" });
            options.Assets.Add(new AssetConfiguration { Symbol = "USDC" });
        });

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IResolvedAssets>();

        resolved.All.Select(a => a.Symbol).ShouldBe(["EURC", "USDC"]);
        // On mainnet, USDC signs under "USD Coin" — resolution must know that.
        resolved.All[1].Eip712Name.ShouldBe("USD Coin");
    }
}
