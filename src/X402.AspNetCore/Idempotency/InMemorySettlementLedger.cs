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

        if (existing.ExpiresAt <= now)
        {
            // Entrée périmée : on tente de la remplacer, en perdant la course si un autre le fait.
            if (entries.TryUpdate(identity, fresh, existing))
            {
                return ValueTask.FromResult(new SettlementSlot(SettlementSlotState.Acquired, null));
            }

            existing = entries.GetOrAdd(identity, fresh);
            if (ReferenceEquals(existing, fresh))
            {
                return ValueTask.FromResult(new SettlementSlot(SettlementSlotState.Acquired, null));
            }
        }

        return ValueTask.FromResult(existing.Response is { } response
            ? new SettlementSlot(SettlementSlotState.AlreadySettled, response)
            : new SettlementSlot(SettlementSlotState.InFlight, null));
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(
        PaymentIdentity identity, SettleResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        entries[identity] = new Entry(time.GetUtcNow() + retention, response);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask AbandonAsync(PaymentIdentity identity, CancellationToken cancellationToken = default)
    {
        entries.TryRemove(identity, out _);
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
