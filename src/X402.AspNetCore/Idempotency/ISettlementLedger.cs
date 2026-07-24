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
    ValueTask<SettlementSlot> AcquireAsync(
        PaymentIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>Memorises the settlement outcome of an acquired authorization.</summary>
    ValueTask CompleteAsync(
        PaymentIdentity identity, SettleResponse response, CancellationToken cancellationToken = default);

    /// <summary>Releases an acquired authorization that was never settled.</summary>
    ValueTask AbandonAsync(
        PaymentIdentity identity, CancellationToken cancellationToken = default);
}
