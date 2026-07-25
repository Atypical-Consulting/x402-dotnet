using X402.Assets;
using X402.Client;
using X402.Client.Spending;
using X402.Networks;

namespace X402.Client.Tests;

public sealed class SpendTrackerTests
{
    private static X402ClientOptions Options()
    {
        var options = new X402ClientOptions();
        options.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 0.05m, perSession: 0.20m);
        options.SetLimits(KnownAssets.UsdcBaseSepolia, perRequest: 0.06m, perSession: 0.24m);
        return options;
    }

    [Fact]
    public void An_amount_within_limits_is_allowed()
    {
        var tracker = new InMemorySpendTracker(Options());

        Should.NotThrow(() => tracker.EnsureWithinLimits(KnownAssets.EurcBaseSepolia, 0.01m));
    }

    [Fact]
    public void An_amount_over_the_per_request_limit_is_refused()
    {
        var tracker = new InMemorySpendTracker(Options());

        var exception = Should.Throw<SpendingLimitExceededException>(
            () => tracker.EnsureWithinLimits(KnownAssets.EurcBaseSepolia, 0.06m));

        exception.Message.ShouldContain("EURC");
        exception.Message.ShouldContain("0.05");
    }

    [Fact]
    public void The_session_limit_accumulates()
    {
        var tracker = new InMemorySpendTracker(Options());

        for (var i = 0; i < 4; i++)
        {
            tracker.EnsureWithinLimits(KnownAssets.EurcBaseSepolia, 0.05m);
            tracker.Record(KnownAssets.EurcBaseSepolia, 0.05m);
        }

        Should.Throw<SpendingLimitExceededException>(
            () => tracker.EnsureWithinLimits(KnownAssets.EurcBaseSepolia, 0.01m));
    }

    [Fact]
    public void Spending_euros_does_not_consume_the_dollar_budget()
    {
        // Two separate counters. A single counter would assume an exchange rate
        // this library does not have and will not go looking for.
        var tracker = new InMemorySpendTracker(Options());

        for (var i = 0; i < 4; i++)
        {
            tracker.Record(KnownAssets.EurcBaseSepolia, 0.05m);
        }

        Should.NotThrow(() => tracker.EnsureWithinLimits(KnownAssets.UsdcBaseSepolia, 0.06m));
    }

    [Fact]
    public void An_asset_without_a_declared_limit_is_refused()
    {
        // An agent that discovers an unexpected asset in accepts must not be able to pay it.
        var tracker = new InMemorySpendTracker(new X402ClientOptions());

        var exception = Should.Throw<SpendingLimitExceededException>(
            () => tracker.EnsureWithinLimits(KnownAssets.EurcBaseSepolia, 0.000001m));

        exception.Message.ShouldContain("no spending limit");
    }

    [Fact]
    public void Parallel_requests_never_cross_the_session_limit_together()
    {
        var options = new X402ClientOptions();
        options.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 0.01m, perSession: 0.10m);
        var tracker = new InMemorySpendTracker(options);

        var accepted = 0;
        Parallel.For(0, 200, _ =>
        {
            try
            {
                tracker.EnsureWithinLimitsAndRecord(KnownAssets.EurcBaseSepolia, 0.01m);
                Interlocked.Increment(ref accepted);
            }
            catch (SpendingLimitExceededException) { }
        });

        accepted.ShouldBe(10);
    }

    [Fact]
    public void A_refund_releases_session_budget()
    {
        // The payment was refused after reservation: the budget must be returned.
        var options = new X402ClientOptions();
        options.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 0.05m, perSession: 0.05m);
        var tracker = new InMemorySpendTracker(options);

        tracker.EnsureWithinLimitsAndRecord(KnownAssets.EurcBaseSepolia, 0.05m);
        tracker.Release(KnownAssets.EurcBaseSepolia, 0.05m);

        Should.NotThrow(() => tracker.EnsureWithinLimits(KnownAssets.EurcBaseSepolia, 0.05m));
    }

    [Fact]
    public void The_same_symbol_on_two_networks_never_shares_a_limit_or_a_budget()
    {
        // EURC exists on both Base Sepolia and Base mainnet with the same ticker symbol but a
        // different contract address. An operator wants a generous limit for the testnet's play
        // money and a tight one for mainnet's real money — keying by symbol alone would silently
        // merge the two into a single shared limit and a single shared running total.
        var options = new X402ClientOptions();
        options.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 1.00m, perSession: 1.00m);
        options.SetLimits(KnownAssets.EurcBaseMainnet, perRequest: 0.05m, perSession: 0.05m);
        var tracker = new InMemorySpendTracker(options);

        // Spend most of Sepolia's generous session budget.
        tracker.EnsureWithinLimitsAndRecord(KnownAssets.EurcBaseSepolia, 0.90m);

        // Mainnet's own tight limit is untouched by the Sepolia spend, and was not overwritten by
        // the later SetLimits call for the same symbol.
        Should.NotThrow(() => tracker.EnsureWithinLimits(KnownAssets.EurcBaseMainnet, 0.05m));
        Should.Throw<SpendingLimitExceededException>(
            () => tracker.EnsureWithinLimits(KnownAssets.EurcBaseMainnet, 0.06m));

        // Sepolia keeps its own generous limit too — it was not overwritten by the mainnet call.
        Should.NotThrow(() => tracker.EnsureWithinLimits(KnownAssets.EurcBaseSepolia, 0.10m));
    }

    [Fact]
    public void The_string_overload_resolves_a_catalogued_asset_by_network_and_symbol()
    {
        var options = new X402ClientOptions();
        options.SetLimits(KnownNetworks.BaseSepolia, "EURC", perRequest: 0.05m, perSession: 0.05m);
        var tracker = new InMemorySpendTracker(options);

        Should.NotThrow(() => tracker.EnsureWithinLimits(KnownAssets.EurcBaseSepolia, 0.05m));
        Should.Throw<SpendingLimitExceededException>(
            () => tracker.EnsureWithinLimits(KnownAssets.EurcBaseSepolia, 0.06m));
    }

    [Fact]
    public void The_string_overload_rejects_an_uncatalogued_network_and_symbol_pair()
    {
        var options = new X402ClientOptions();

        Should.Throw<ArgumentException>(
            () => options.SetLimits(KnownNetworks.BaseSepolia, "DAI", perRequest: 0.05m, perSession: 0.05m));
    }
}
