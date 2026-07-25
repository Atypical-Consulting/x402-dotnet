using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using X402.Protocol;

namespace X402.AspNetCore.Idempotency;

/// <summary>
/// The default ledger: a concurrent dictionary with time-bounded entries.
/// </summary>
/// <remarks>
/// Two separate durations govern entry lifetime, and neither tracks the authorization's own
/// on-chain validity window. <c>retention</c> is how long a completed settlement is remembered, so
/// that an authorization presented again gets the memorised response instead of settling a second
/// time. <c>leaseTimeout</c> is how long an acquired-but-not-yet-completed authorization is
/// tolerated before it is assumed abandoned and reclaimed — a leak backstop for a caller that
/// crashes between acquiring and completing, not a bound on how long settlement itself may take.
/// Conflating the two is exactly the assumption that once let a slow settlement's still-live lease
/// be pruned as if it were an expired completed record; see <see cref="ISettlementLedger.AcquireAsync"/>
/// and <see cref="ISettlementLedger.CompleteAsync"/> for what that costs when it happens anyway.
/// </remarks>
public sealed partial class InMemorySettlementLedger : ISettlementLedger
{
    private readonly ConcurrentDictionary<PaymentIdentity, Entry> entries = new();
    private readonly TimeSpan retention;
    private readonly TimeSpan leaseTimeout;
    private readonly TimeProvider time;
    private readonly ILogger<InMemorySettlementLedger> logger;

    /// <summary>Creates a ledger retaining entries for the given durations.</summary>
    /// <param name="retention">How long a completed settlement is remembered. Defaults to 10 minutes.</param>
    /// <param name="leaseTimeout">
    /// How long an acquired-but-not-yet-completed authorization is held before being treated as
    /// abandoned and reclaimed — a backstop against a caller that acquires and never completes or
    /// abandons (typically a crash). Defaults to one hour, deliberately far larger than any plausible
    /// endpoint-plus-settlement round trip so this should never fire in normal operation.
    /// </param>
    /// <param name="timeProvider">Clock, for tests.</param>
    /// <param name="logger">Where sequencing anomalies are logged. Defaults to a no-op logger.</param>
    public InMemorySettlementLedger(
        TimeSpan? retention = null,
        TimeSpan? leaseTimeout = null,
        TimeProvider? timeProvider = null,
        ILogger<InMemorySettlementLedger>? logger = null)
    {
        this.retention = retention ?? TimeSpan.FromMinutes(10);
        this.leaseTimeout = leaseTimeout ?? TimeSpan.FromHours(1);
        time = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<InMemorySettlementLedger>.Instance;
    }

    /// <inheritdoc />
    public ValueTask<SettlementSlot> AcquireAsync(
        PaymentIdentity identity, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        Prune(now);

        var fresh = new Entry(now + leaseTimeout, null);
        var existing = entries.GetOrAdd(identity, fresh);

        if (ReferenceEquals(existing, fresh))
        {
            return ValueTask.FromResult(new SettlementSlot(SettlementSlotState.Acquired, null));
        }

        // No separate "expired but not yet pruned" case to handle here: Prune, just above, removes
        // every entry whose ExpiresAt <= now using this same now. For `existing` to reach this point
        // already expired, another caller would have to have inserted it after our Prune ran — but
        // every entry a caller inserts is stamped with its own now plus a strictly positive duration
        // (lease timeout or retention), and real time only moves forward, so that stamp can never
        // already be <= a now captured before the insert happened. `existing` is therefore always
        // either in flight or already settled here, never stale.
        return ValueTask.FromResult(existing.Response is { } response
            ? new SettlementSlot(SettlementSlotState.AlreadySettled, response)
            : new SettlementSlot(SettlementSlotState.InFlight, null));
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(
        PaymentIdentity identity, SettleResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        var completed = new Entry(time.GetUtcNow() + retention, response);

        while (true)
        {
            if (!entries.TryGetValue(identity, out var existing))
            {
                // Absent: never acquired, or — far more likely in practice — the lease was reclaimed
                // by Prune's abandoned-lease backstop while this settlement was still in flight (see
                // AcquireAsync's remarks). Either way the settlement already happened on-chain, and
                // refusing to record it is what would create a double payment, not recording it too
                // eagerly. Insert rather than reject; a missing entry here is not proof of a bug.
                if (entries.TryAdd(identity, completed))
                {
                    CompletedWithoutAcquisition(
                        logger, identity.Network, identity.Asset, identity.Nonce, response.Transaction);
                    return ValueTask.CompletedTask;
                }

                continue; // Someone else raced in between; re-read and resolve against the new state.
            }

            if (existing.Response is null)
            {
                // Normal path: still in flight, hand off via compare-and-swap.
                if (entries.TryUpdate(identity, completed, existing))
                {
                    return ValueTask.CompletedTask;
                }

                continue; // Lost a race (e.g. against Abandon or another Complete); re-read and retry.
            }

            if (existing.Response.Equals(response))
            {
                // Duplicate completion, same outcome (e.g. a retried settle that reached the
                // facilitator twice): harmless, nothing left to record.
                //
                // Known gap: SettleResponse's record-generated Equals is structural for every field
                // except Extensions (IReadOnlyDictionary<string, ProtocolExtension>?), which
                // EqualityComparer<T>.Default compares by reference, not content. Two separately
                // deserialised responses carrying identical extension data therefore compare unequal
                // whenever Extensions is populated, and fall through to the conflicting-outcome branch
                // below. The consequence is a spurious warning, not incorrect state: that branch keeps
                // the first response either way, so no settlement is lost or duplicated. No facilitator
                // in this codebase populates Extensions yet, which is why this hasn't surfaced. A
                // structural comparer would close it but is a real change, not a doc fix — left for a
                // future task.
                return ValueTask.CompletedTask;
            }

            // Two different outcomes recorded for the same authorization. Keep the first — it is the
            // one that actually happened on-chain first — and never overwrite it; this is a genuine
            // anomaly, not something a well-behaved retry can trigger on its own, so it is surfaced.
            ConflictingCompletion(logger, identity.Network, identity.Asset, identity.Nonce,
                existing.Response.Transaction, response.Transaction);
            return ValueTask.CompletedTask;
        }
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

    [LoggerMessage(EventId = 4030, Level = LogLevel.Warning,
        Message = "Settlement completed for {Network}/{Asset}/{Nonce} without a held lease " +
                  "(transaction {Transaction}) — most likely the lease timeout reclaimed it while " +
                  "settlement was still in flight.")]
    private static partial void CompletedWithoutAcquisition(
        ILogger logger, string network, string asset, string nonce, string transaction);

    [LoggerMessage(EventId = 4031, Level = LogLevel.Warning,
        Message = "Settlement for {Network}/{Asset}/{Nonce} was already completed with transaction " +
                  "{ExistingTransaction}; ignoring a conflicting completion carrying transaction " +
                  "{NewTransaction}.")]
    private static partial void ConflictingCompletion(ILogger logger, string network, string asset,
        string nonce, string existingTransaction, string newTransaction);

    private sealed record Entry(DateTimeOffset ExpiresAt, SettleResponse? Response);
}
