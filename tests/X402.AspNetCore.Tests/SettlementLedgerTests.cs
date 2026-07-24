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
        // L'endpoint a levé avant règlement : l'autorisation est encore valable en chaîne,
        // le client doit pouvoir réessayer.
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

        // Passé validBefore, l'autorisation est refusée en chaîne : garder l'entrée ne
        // protégerait plus rien et ferait croître la mémoire sans fin.
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
    public async Task Completing_a_nonce_that_was_never_acquired_throws()
    {
        var ledger = new InMemorySettlementLedger();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await ledger.CompleteAsync(Identity(), Settled(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Completing_an_already_settled_nonce_throws()
    {
        var ledger = new InMemorySettlementLedger();
        await ledger.AcquireAsync(Identity(), TestContext.Current.CancellationToken);
        await ledger.CompleteAsync(Identity(), Settled(), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await ledger.CompleteAsync(Identity(), Settled(), TestContext.Current.CancellationToken));
    }
}
