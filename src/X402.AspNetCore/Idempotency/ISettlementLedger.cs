using X402.Protocol;

namespace X402.AspNetCore.Idempotency;

/// <summary>Identifies one payment authorization, uniquely and across deployments.</summary>
/// <param name="Network">CAIP-2 network identifier.</param>
/// <param name="Asset">Token contract address.</param>
/// <param name="Nonce">The EIP-3009 nonce, unique per authorization.</param>
public readonly record struct PaymentIdentity(string Network, string Asset, string Nonce);

/// <summary>What the ledger knows about an authorization.</summary>
public enum SettlementSlotState
{
    /// <summary>The caller holds the right to settle this authorization.</summary>
    Acquired,

    /// <summary>Another caller is settling this authorization right now.</summary>
    InFlight,

    /// <summary>This authorization has already been settled; see the memorised response.</summary>
    AlreadySettled,
}

/// <summary>The outcome of an acquisition attempt.</summary>
/// <param name="State">What the ledger knows.</param>
/// <param name="Existing">The memorised settlement, when the state is <c>AlreadySettled</c>.</param>
public readonly record struct SettlementSlot(SettlementSlotState State, SettleResponse? Existing);

/// <summary>
/// Guarantees that one authorization settles at most once, whoever presents it and however often.
/// </summary>
/// <remarks>
/// This is what makes settlement idempotent — not the retry policy. It protects against a client
/// replaying an authorization deliberately, and against a settlement retried after an ambiguous
/// transport failure. The default implementation is in-memory; substitute a distributed one for a
/// multi-instance deployment.
/// </remarks>
public interface ISettlementLedger
{
    /// <summary>Claims the right to settle an authorization.</summary>
    /// <remarks>
    /// The lease this grants is bounded by a lease timeout — a separate window from the retention
    /// used to remember a completed settlement, deliberately much longer. It exists purely as a
    /// backstop against a caller that acquires and then never completes or abandons (most plausibly
    /// a crash), and its default is sized far larger than any plausible endpoint-plus-settlement
    /// round trip so it should never fire in normal operation. If it ever does, the entry is
    /// reclaimed and a second caller can acquire the same identity — including concurrently with the
    /// first caller, who is still settling; the ledger cannot prevent that once the lease is gone.
    /// When the original caller eventually calls <see cref="CompleteAsync"/>, that call still
    /// persists the settlement it carries — a missing entry there does not mean the caller did
    /// anything wrong — but by then a duplicate settlement may already be in flight or complete.
    /// Keep the lease timeout comfortably larger than the slowest realistic settlement so this
    /// backstop stays a backstop and not a normal-path timeout.
    /// </remarks>
    ValueTask<SettlementSlot> AcquireAsync(
        PaymentIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>Memorises the settlement outcome of an acquired authorization.</summary>
    /// <remarks>
    /// Always persists the given outcome: a settlement that already happened on-chain must never be
    /// discarded, so this never throws. If the identity is not currently held as in-flight — never
    /// acquired, or its lease was reclaimed by the backstop described on <see cref="AcquireAsync"/> —
    /// the outcome is recorded anyway and the situation is logged, not rejected. Completing an
    /// identity that already carries a different memorised outcome keeps the first recorded outcome
    /// and logs the conflict; completing it again with the same outcome is a harmless no-op.
    /// </remarks>
    ValueTask CompleteAsync(
        PaymentIdentity identity, SettleResponse response, CancellationToken cancellationToken = default);

    /// <summary>Releases an acquired authorization that was never settled.</summary>
    /// <remarks>
    /// A no-op if the identity was already completed: abandon must never erase a memorised
    /// settlement, so a caller that races a completed <see cref="CompleteAsync"/> against its own
    /// cleanup (for example, aborting after a downstream failure that happened after settlement)
    /// cannot make an already-settled authorization look available again.
    /// </remarks>
    ValueTask AbandonAsync(
        PaymentIdentity identity, CancellationToken cancellationToken = default);
}
