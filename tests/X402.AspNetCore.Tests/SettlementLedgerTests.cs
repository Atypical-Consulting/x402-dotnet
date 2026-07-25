using Microsoft.Extensions.Logging;
using X402.AspNetCore.Idempotency;
using X402.Protocol;

namespace X402.AspNetCore.Tests;

public sealed class SettlementLedgerTests
{
    private static PaymentIdentity Identity(string nonce = "0xabc") =>
        new("eip155:84532", "0x808456652fdb597867f38412077A9182bf77359F", nonce);

    private static SettleResponse Settled() => new()
    {
        Success = true,
        Transaction = "0xdeadbeef",
        Network = "eip155:84532",
    };

    [Fact]
    public async Task A_fresh_nonce_is_acquired()
    {
        var ledger = new InMemorySettlementLedger();

        var slot = await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);

        slot.State.ShouldBe(SettlementSlotState.Acquired);
        slot.Existing.ShouldBeNull();
    }

    [Fact]
    public async Task A_settled_nonce_returns_the_memorised_response_without_settling_again()
    {
        var ledger = new InMemorySettlementLedger();
        await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        await ledger.CompleteAsync(Identity(), Settled(), TestContext.Current.CancellationToken);

        var slot = await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);

        slot.State.ShouldBe(SettlementSlotState.AlreadySettled);
        slot.Existing!.Transaction.ShouldBe("0xdeadbeef");
    }

    [Fact]
    public async Task A_nonce_being_settled_reports_in_flight()
    {
        var ledger = new InMemorySettlementLedger();
        await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);

        var slot = await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);

        slot.State.ShouldBe(SettlementSlotState.InFlight);
    }

    [Fact]
    public async Task An_abandoned_nonce_can_be_acquired_again()
    {
        // The endpoint threw before settlement: the authorization is still valid on-chain,
        // the client must be able to retry.
        var ledger = new InMemorySettlementLedger();
        await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        await ledger.AbandonAsync(Identity(), TestContext.Current.CancellationToken);

        var slot = await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);

        slot.State.ShouldBe(SettlementSlotState.Acquired);
    }

    [Fact]
    public async Task The_same_nonce_on_another_network_is_a_different_payment()
    {
        var ledger = new InMemorySettlementLedger();
        await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);

        var other = new PaymentIdentity(
            "eip155:8453", "0x808456652fdb597867f38412077A9182bf77359F", "0xabc");
        var slot = await ledger.AcquireAsync(other, TestContext.Current.CancellationToken);

        slot.State.ShouldBe(SettlementSlotState.Acquired);
    }

    [Fact]
    public async Task Concurrent_acquisitions_of_one_nonce_yield_exactly_one_winner()
    {
        var ledger = new InMemorySettlementLedger();
        var identity = Identity();
        const int concurrency = 64;

        // AcquireAsync never awaits anything, so `Task.WhenAll(source.Select(x => AcquireAsync(x)))`
        // would enumerate the whole source — running every call synchronously, one after another, on
        // this thread — before Task.WhenAll ever has anything to wait on. That "concurrency" test
        // would pass even against the broken factory-overload GetOrAdd the brief warns about, because
        // nothing would ever actually contend. Task.Run forces each acquisition onto its own
        // thread-pool thread, and the barrier holds all of them back until every one of the 64 is
        // actually running, so they hit AcquireAsync at (as close as the runtime allows to) the same
        // instant instead of merely at different times on different threads.
        ThreadPool.GetMinThreads(out var minWorker, out var minCompletionPort);
        ThreadPool.SetMinThreads(Math.Max(minWorker, concurrency), minCompletionPort);

        using var barrier = new Barrier(concurrency);

        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(async () =>
            {
                barrier.SignalAndWait(TestContext.Current.CancellationToken);
                return await ledger.AcquireAsync(identity, TestContext.Current.CancellationToken);
            }))
            .ToArray();

        var slots = await Task.WhenAll(tasks);

        slots.Count(s => s.State == SettlementSlotState.Acquired).ShouldBe(1);
        slots.Count(s => s.State == SettlementSlotState.InFlight).ShouldBe(63);
    }

    [Fact]
    public async Task An_entry_expires_once_its_authorization_can_no_longer_be_valid()
    {
        var ledger = new InMemorySettlementLedger(TimeSpan.FromMilliseconds(50));
        await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        await ledger.CompleteAsync(Identity(), Settled(), TestContext.Current.CancellationToken);

        await Task.Delay(150, TestContext.Current.CancellationToken);

        // Past validBefore, the authorization is refused on-chain: keeping the entry would no
        // longer protect anything and would grow memory without bound.
        var slot = await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        slot.State.ShouldBe(SettlementSlotState.Acquired);
    }

    [Fact]
    public async Task Abandoning_a_completed_nonce_leaves_the_memorised_settlement_intact()
    {
        // A consumer wraps settle, then CompleteAsync, then a subsequent step (e.g. writing the
        // response header) in one try/catch, calling AbandonAsync in the catch. If that later step
        // throws after CompleteAsync already recorded a real on-chain settlement, AbandonAsync must
        // not wipe it out — otherwise the next presentation of the authorization would settle again.
        var ledger = new InMemorySettlementLedger();
        await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        await ledger.CompleteAsync(Identity(), Settled(), TestContext.Current.CancellationToken);

        await ledger.AbandonAsync(Identity(), TestContext.Current.CancellationToken);

        var slot = await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        slot.State.ShouldBe(SettlementSlotState.AlreadySettled);
        slot.Existing!.Transaction.ShouldBe("0xdeadbeef");
    }

    [Fact]
    public async Task Prune_leaves_an_in_flight_entry_alone_well_past_retention()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var ledger = new InMemorySettlementLedger(
            retention: TimeSpan.FromMilliseconds(1), timeProvider: time);
        await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);

        // Retention governs settled records, not in-flight leases: an in-flight entry must survive
        // well past it, protected instead by the much longer (default one hour) lease timeout.
        time.Advance(TimeSpan.FromMinutes(5));

        var slot = await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        slot.State.ShouldBe(SettlementSlotState.InFlight);
    }

    [Fact]
    public async Task Completing_after_the_lease_was_pruned_still_persists_the_settlement()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var logger = new RecordingLogger();
        var ledger = new InMemorySettlementLedger(
            leaseTimeout: TimeSpan.FromMilliseconds(1), timeProvider: time, logger: logger);
        await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(5)); // well past the (deliberately shortened) lease timeout

        // Some unrelated request's AcquireAsync runs Prune as a side effect, reclaiming our
        // now-abandoned-looking lease while we are still mid-settlement — the scenario this fix
        // round exists to close.
        var other = new PaymentIdentity("eip155:8453", "0xdead", "0xother");
        await ledger.AcquireAsync(other, TestContext.Current.CancellationToken);

        // Our own settlement finishes anyway; it must be recorded, not discarded, and the missing
        // lease must not be treated as a caller bug.
        await ledger.CompleteAsync(Identity(), Settled(), TestContext.Current.CancellationToken);

        var slot = await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        slot.State.ShouldBe(SettlementSlotState.AlreadySettled);
        slot.Existing!.Transaction.ShouldBe("0xdeadbeef");
        logger.Levels.ShouldContain(LogLevel.Warning);
    }

    [Fact]
    public async Task Completing_twice_with_the_same_outcome_is_a_no_op()
    {
        var ledger = new InMemorySettlementLedger();
        await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        await ledger.CompleteAsync(Identity(), Settled(), TestContext.Current.CancellationToken);

        // A retried settle that reaches the facilitator a second time and gets the identical
        // response back must not be treated as a conflict.
        await ledger.CompleteAsync(Identity(), Settled(), TestContext.Current.CancellationToken);

        var slot = await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        slot.State.ShouldBe(SettlementSlotState.AlreadySettled);
        slot.Existing!.Transaction.ShouldBe("0xdeadbeef");
    }

    [Fact]
    public async Task Completing_twice_with_a_different_outcome_keeps_the_first()
    {
        var logger = new RecordingLogger();
        var ledger = new InMemorySettlementLedger(logger: logger);
        await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        await ledger.CompleteAsync(Identity(), Settled(), TestContext.Current.CancellationToken);

        var conflicting = Settled() with { Transaction = "0xconflict" };
        await ledger.CompleteAsync(Identity(), conflicting, TestContext.Current.CancellationToken);

        var slot = await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        slot.State.ShouldBe(SettlementSlotState.AlreadySettled);
        slot.Existing!.Transaction.ShouldBe("0xdeadbeef");
        logger.Levels.ShouldContain(LogLevel.Warning);
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class RecordingLogger : ILogger<InMemorySettlementLedger>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}
