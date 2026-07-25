using X402.Assets;
using X402.Client;
using X402.Client.Spending;

namespace X402.Client.Tests;

public sealed class SpendTrackerTests
{
    private static X402ClientOptions Options()
    {
        var options = new X402ClientOptions();
        options.SetLimits("EURC", perRequest: 0.05m, perSession: 0.20m);
        options.SetLimits("USDC", perRequest: 0.06m, perSession: 0.24m);
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
        // Deux compteurs distincts. Un compteur unique supposerait un taux de change
        // que cette bibliothèque n'a pas et n'ira pas chercher.
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
        // Un agent qui découvre un actif inattendu dans accepts ne doit pas pouvoir le payer.
        var tracker = new InMemorySpendTracker(new X402ClientOptions());

        var exception = Should.Throw<SpendingLimitExceededException>(
            () => tracker.EnsureWithinLimits(KnownAssets.EurcBaseSepolia, 0.000001m));

        exception.Message.ShouldContain("no spending limit");
    }

    [Fact]
    public void Parallel_requests_never_cross_the_session_limit_together()
    {
        var options = new X402ClientOptions();
        options.SetLimits("EURC", perRequest: 0.01m, perSession: 0.10m);
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
        // Le paiement a été refusé après réservation : le budget doit revenir.
        var options = new X402ClientOptions();
        options.SetLimits("EURC", perRequest: 0.05m, perSession: 0.05m);
        var tracker = new InMemorySpendTracker(options);

        tracker.EnsureWithinLimitsAndRecord(KnownAssets.EurcBaseSepolia, 0.05m);
        tracker.Release(KnownAssets.EurcBaseSepolia, 0.05m);

        Should.NotThrow(() => tracker.EnsureWithinLimits(KnownAssets.EurcBaseSepolia, 0.05m));
    }
}
