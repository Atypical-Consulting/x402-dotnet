using System.Collections.Concurrent;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using X402.Assets;
using X402.Client.Spending;
using X402.PropertyTesting;

namespace X402.Client.Tests;

public sealed class SpendTrackerPropertyTests
{
    private static readonly AssetDescriptor[] Catalogue =
    [
        KnownAssets.EurcBaseSepolia, KnownAssets.UsdcBaseSepolia,
        KnownAssets.EurcBaseMainnet, KnownAssets.UsdcBaseMainnet,
    ];

    // Whole cents, so every total is exact in decimal.
    private static Gen<decimal> Money(int minCents, int maxCents) =>
        Gen.Choose(minCents, maxCents).Select(c => c / 100m);

    private sealed record Scenario((AssetDescriptor Asset, decimal Cap)[] Budgets, (AssetDescriptor Asset, decimal Amount)[] Spends);

    private static readonly Gen<Scenario> Scenarios =
        from count in Gen.Choose(1, Catalogue.Length)
        from shuffled in Gen.Shuffle(Catalogue)
        from caps in Money(0, 500).ArrayOf(count)
        let budgets = shuffled.Take(count).Zip(caps).ToArray()
        from spendCount in Gen.Choose(50, 500)
        from spends in (
            from budget in Gen.Elements(budgets)
            from amount in Money(0, 100)
            select (budget.First, amount)).ArrayOf(spendCount)
        select new Scenario(budgets, spends);

    [Property(MaxTest = 200, Replay = "(26,17)")]
    public Property Concurrent_spends_never_exceed_any_assets_session_cap() =>
        Prop.ForAll(Scenarios.ToArbitrary(), scenario =>
        {
            var options = new X402ClientOptions();
            foreach (var (asset, cap) in scenario.Budgets)
            {
                options.SetLimits(asset, perRequest: cap, perSession: cap);
            }

            var tracker = new InMemorySpendTracker(options);
            var accepted = new ConcurrentBag<(AssetDescriptor Asset, decimal Amount)>();
            Parallel.ForEach(scenario.Spends, spend =>
            {
                try
                {
                    tracker.EnsureWithinLimitsAndRecord(spend.Asset, spend.Amount);
                    accepted.Add(spend);
                }
                catch (SpendingLimitExceededException)
                {
                    // Refused: the cap held.
                }
            });

            return scenario.Budgets.All(
                b => accepted.Where(s => s.Asset == b.Asset).Sum(s => s.Amount) <= b.Cap);
        });

    [Property(MaxTest = 200, Replay = "(26,19)")]
    public Property Exhausting_one_asset_leaves_another_assets_cap_untouched() =>
        Prop.ForAll(
            (from shuffled in Gen.Shuffle(Catalogue)
             from capA in Money(1, 500)
             from capB in Money(1, 500)
             select (A: shuffled[0], B: shuffled[1], CapA: capA, CapB: capB)).ToArbitrary(),
            s =>
            {
                var options = new X402ClientOptions()
                    .SetLimits(s.A, s.CapA, s.CapA)
                    .SetLimits(s.B, s.CapB, s.CapB);
                var tracker = new InMemorySpendTracker(options);

                tracker.EnsureWithinLimitsAndRecord(s.A, s.CapA);

                return Record.Exception(() => tracker.EnsureWithinLimitsAndRecord(s.B, s.CapB)) is null;
            });

    [Property(MaxTest = 200, Replay = "(26,21)")]
    public Property Asset_address_case_does_not_split_the_session_budget() =>
        Prop.ForAll(
            (from asset in X402Arbitraries.Asset
             from cap in Money(1, 500)
             select (Asset: asset, Cap: cap)).ToArbitrary(),
            s =>
            {
                var tracker = new InMemorySpendTracker(new X402ClientOptions().SetLimits(s.Asset, s.Cap, s.Cap));
                tracker.EnsureWithinLimitsAndRecord(s.Asset with { Address = s.Asset.Address.ToUpperInvariant() }, s.Cap);

                return Record.Exception(() => tracker.EnsureWithinLimitsAndRecord(
                    s.Asset with { Address = s.Asset.Address.ToLowerInvariant() }, 0.01m)) is SpendingLimitExceededException;
            });
}
