using System.Collections.Concurrent;
using X402.Protocol;

namespace X402.AspNetCore.Idempotency;

/// <summary>
/// The default ledger: a concurrent dictionary with time-bounded entries.
/// </summary>
/// <remarks>
/// Entries live for the authorization's own validity window plus a margin. Past that point the
/// authorization is refused on-chain anyway, so keeping the entry would bound nothing and grow
/// memory without end.
/// </remarks>
public sealed class InMemorySettlementLedger : ISettlementLedger
{
    private readonly ConcurrentDictionary<PaymentIdentity, Entry> entries = new();
    private readonly TimeSpan retention;
    private readonly TimeProvider time;

    /// <summary>Creates a ledger retaining entries for the given duration.</summary>
    /// <param name="retention">How long a settled authorization is remembered. Defaults to 10 minutes.</param>
    /// <param name="timeProvider">Clock, for tests.</param>
    public InMemorySettlementLedger(TimeSpan? retention = null, TimeProvider? timeProvider = null)
    {
        this.retention = retention ?? TimeSpan.FromMinutes(10);
        time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ValueTask<SettlementSlot> AcquireAsync(
        PaymentIdentity identity, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        Prune(now);

        var fresh = new Entry(now + retention, null);
        var existing = entries.GetOrAdd(identity, fresh);

        if (ReferenceEquals(existing, fresh))
        {
            return ValueTask.FromResult(new SettlementSlot(SettlementSlotState.Acquired, null));
        }

        // No separate "expired but not yet pruned" case to handle here: Prune, just above, removes
        // every entry whose ExpiresAt <= now before GetOrAdd runs, using this same `now`. For
        // `existing` to reach this point already expired, another caller would have to insert it in
        // the gap between Prune's scan and this GetOrAdd — a gap with no yield point, so no thread
        // interleaving lands there in practice. (This stops being true if Prune is ever throttled to
        // run less than once per acquisition — see the class remarks on that known limitation.)
        return ValueTask.FromResult(existing.Response is { } response
            ? new SettlementSlot(SettlementSlotState.AlreadySettled, response)
            : new SettlementSlot(SettlementSlotState.InFlight, null));
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The identity was not acquired first, or was already completed.
    /// </exception>
    public ValueTask CompleteAsync(
        PaymentIdentity identity, SettleResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        var completed = new Entry(time.GetUtcNow() + retention, response);

        // Only settle an entry that is currently in flight (acquired, not yet completed), and only
        // the specific entry read a moment ago: this is a caller-sequencing contract, not something
        // to paper over. Completing without acquiring, or completing twice, is a bug in the caller
        // and must fail loudly rather than fabricate or silently overwrite a settlement record.
        if (entries.TryGetValue(identity, out var existing)
            && existing.Response is null
            && entries.TryUpdate(identity, completed, existing))
        {
            return ValueTask.CompletedTask;
        }

        throw new InvalidOperationException(
            $"Cannot complete settlement for {identity}: it was not acquired, or is already settled.");
    }

    /// <inheritdoc />
    public ValueTask AbandonAsync(PaymentIdentity identity, CancellationToken cancellationToken = default)
    {
        // Same defensive shape as Prune: remove only the specific entry read a moment ago, and only
        // while it is still in flight (no memorised response). A completed entry must never be
        // erased by an abandon that races — or is mistakenly called — after CompleteAsync.
        if (entries.TryGetValue(identity, out var existing) && existing.Response is null)
        {
            entries.TryRemove(new(identity, existing));
        }

        return ValueTask.CompletedTask;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, entry) in entries)
        {
            if (entry.ExpiresAt <= now)
            {
                entries.TryRemove(new(key, entry));
            }
        }
    }

    private sealed record Entry(DateTimeOffset ExpiresAt, SettleResponse? Response);
}
