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

        var slots = await Task.WhenAll(Enumerable.Range(0, 64).Select(
            _ => ledger.AcquireAsync(identity, TestContext.Current.CancellationToken).AsTask()));

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
}
