using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using X402.Assets;
using X402.Client.DependencyInjection;
using X402.Networks;

namespace X402.Client.Tests;

/// <summary>
/// Exercises <see cref="X402ClientOptionsValidator"/> the way it actually runs in production:
/// through <see cref="X402ClientExtensions.AddX402Client"/> and the options system, never by
/// instantiating the (internal) validator directly — mirrors
/// <c>X402.AspNetCore.Tests.OptionsValidationTests</c>, the server-side equivalent.
/// </summary>
public sealed class X402ClientOptionsValidationTests
{
    private static void Validate(Action<X402ClientOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddX402Client(configure);

        using var provider = services.BuildServiceProvider();
        // Forces validation, the way ValidateOnStart would when a real IHost starts (see
        // AddX402Client's own remarks for why this is also where a bare ServiceProvider — no host
        // to start — first sees it: the same instant something resolves X402ClientOptions).
        _ = provider.GetRequiredService<IOptions<X402ClientOptions>>().Value;
    }

    private static void SeedOneValidLimit(X402ClientOptions options) =>
        options.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 1m, perSession: 10m);

    [Fact]
    public void A_complete_configuration_validates()
    {
        Should.NotThrow(() => Validate(o =>
        {
            o.AllowedNetworks.Add(KnownNetworks.BaseSepolia);
            SeedOneValidLimit(o);
        }));
    }

    [Fact]
    public void An_empty_allowed_networks_list_validates_meaning_any_network()
    {
        Should.NotThrow(() => Validate(SeedOneValidLimit));
    }

    [Fact]
    public void A_v1_network_shorthand_in_allowed_networks_fails_at_startup()
    {
        // "base-sepolia" would otherwise silently filter every offered requirement out, and the
        // agent would report that nothing was acceptable — no clue that the network name is why.
        var exception = Should.Throw<OptionsValidationException>(() => Validate(o =>
        {
            SeedOneValidLimit(o);
            o.AllowedNetworks.Add("base-sepolia");
        }));

        exception.Message.ShouldContain("AllowedNetworks");
        exception.Message.ShouldContain("CAIP-2");
        exception.Message.ShouldContain("base-sepolia");
    }

    [Fact]
    public void No_declared_spending_limits_at_all_fails_at_startup()
    {
        // A valid-looking configuration that never calls SetLimits used to be accepted silently,
        // and then every payment was refused for want of a declared limit.
        var exception = Should.Throw<OptionsValidationException>(
            () => Validate(o => o.AllowedNetworks.Add(KnownNetworks.BaseSepolia)));

        exception.Message.ShouldContain("SetLimits");
    }

    [Fact]
    public void A_per_session_limit_below_the_per_request_limit_fails_at_startup()
    {
        // Nothing in SetLimits itself stops this: the first payment within the (higher)
        // per-request figure would still be refused by the (lower) per-session one.
        var exception = Should.Throw<OptionsValidationException>(() => Validate(o =>
            o.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 10m, perSession: 1m)));

        exception.Message.ShouldContain("PerSession");
        exception.Message.ShouldContain("PerRequest");
    }

    [Fact]
    public void A_per_session_limit_equal_to_the_per_request_limit_validates()
    {
        Should.NotThrow(() => Validate(o =>
            o.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 5m, perSession: 5m)));
    }

    [Fact]
    public void A_zero_limit_fails_at_startup_just_like_no_limit_at_all()
    {
        // SetLimits(asset, 0m, 0m) passes SetLimits's own ArgumentOutOfRangeException.ThrowIfNegative
        // guard (zero is not negative) and stores validly — then InMemorySpendTracker refuses every
        // non-zero payment for that asset, the same outcome the empty-dictionary check exists to
        // catch, just for one asset instead of the whole configuration.
        var exception = Should.Throw<OptionsValidationException>(() => Validate(o =>
            o.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 0m, perSession: 0m)));

        exception.Message.ShouldContain(KnownAssets.EurcBaseSepolia.Address);
        exception.Message.ShouldContain("PerRequest");
        exception.Message.ShouldContain("PerSession");
    }

    [Fact]
    public void A_zero_per_request_limit_alone_fails_at_startup()
    {
        var exception = Should.Throw<OptionsValidationException>(() => Validate(o =>
            o.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 0m, perSession: 10m)));

        exception.Message.ShouldContain(KnownAssets.EurcBaseSepolia.Address);
    }

    [Fact]
    public void A_tiny_but_positive_limit_validates()
    {
        Should.NotThrow(() => Validate(o =>
            o.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 0.000001m, perSession: 0.000001m)));
    }

    [Fact]
    public void Multiple_assets_each_with_a_valid_limit_all_validate()
    {
        Should.NotThrow(() => Validate(o =>
        {
            o.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 1m, perSession: 10m);
            o.SetLimits(KnownAssets.UsdcBaseSepolia, perRequest: 1m, perSession: 10m);
        }));
    }

    [Fact]
    public void One_bad_limit_among_several_good_ones_still_fails_at_startup_and_names_that_asset()
    {
        var exception = Should.Throw<OptionsValidationException>(() => Validate(o =>
        {
            o.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 1m, perSession: 10m);
            o.SetLimits(KnownAssets.UsdcBaseSepolia, perRequest: 10m, perSession: 1m);
        }));

        // Named by network and contract address (AssetIdentity.Key), not by ticker symbol — the
        // same identity the limits themselves are keyed by, so it points at exactly the SetLimits
        // call to fix.
        exception.Message.ShouldContain(KnownAssets.UsdcBaseSepolia.Address);
        exception.Message.ShouldContain(KnownAssets.UsdcBaseSepolia.Network);
    }

    [Fact]
    public void A_negative_max_buffered_request_bytes_fails_at_startup()
    {
        // Confirmed to otherwise fail deep inside X402PaymentHandler on the first request that
        // actually carries a body ("Cannot write more bytes to the buffer than the configured
        // maximum buffer size: -1") rather than at start-up — the same shape every other check in
        // this validator exists to move earlier.
        var exception = Should.Throw<OptionsValidationException>(() => Validate(o =>
        {
            SeedOneValidLimit(o);
            o.MaxBufferedRequestBytes = -1;
        }));

        exception.Message.ShouldContain("MaxBufferedRequestBytes");
    }

    [Fact]
    public void A_zero_max_buffered_request_bytes_validates()
    {
        // Zero is a legitimate (if impractical) choice — mirrors the server's own
        // MaxBufferedResponseBytes, which also only rejects strictly negative values.
        Should.NotThrow(() => Validate(o =>
        {
            SeedOneValidLimit(o);
            o.MaxBufferedRequestBytes = 0;
        }));
    }

    [Fact]
    public void The_default_max_buffered_request_bytes_validates()
    {
        Should.NotThrow(() => Validate(SeedOneValidLimit));
    }
}
