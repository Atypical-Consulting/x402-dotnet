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
    /// The lease this grants is bounded by the same retention window the ledger uses to remember a
    /// completed settlement — the default in-memory implementation does not track the two
    /// separately. If the work performed between acquiring and completing an authorization (typically
    /// broadcasting the settlement on-chain) ever runs longer than that window, the entry can expire
    /// and be reclaimed by a second caller while the first is still settling. Size retention with
    /// that shared duty in mind, not solely as a settled-record lifetime.
    /// </remarks>
    ValueTask<SettlementSlot> AcquireAsync(
        PaymentIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>Memorises the settlement outcome of an acquired authorization.</summary>
    /// <exception cref="InvalidOperationException">
    /// The identity was not acquired first, or was already completed.
    /// </exception>
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
