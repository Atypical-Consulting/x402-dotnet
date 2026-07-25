using X402.AspNetCore.Idempotency;
using X402.Assets;
using X402.Protocol;

namespace X402.AspNetCore.Engine;

/// <summary>
/// The outcome of an authorization decision: either a ready-to-return refusal, or what settlement
/// needs to finish the job.
/// </summary>
public sealed class PaymentAttempt
{
    private PaymentAttempt() { }

    /// <summary>Whether the request may proceed to the protected endpoint.</summary>
    public bool CanContinue { get; private init; }

    /// <summary>The 402 to return when the request may not proceed. Null for a conflict (409).</summary>
    public PaymentRequiredResult? Result { get; private init; }

    /// <summary>The asset actually used to pay, once accepted.</summary>
    public AssetDescriptor? SettledAsset { get; private init; }

    /// <summary>The payer's address, once known.</summary>
    public string? Payer { get; private init; }

    /// <summary>Why the payment was refused, when it was.</summary>
    public string? FailureReason { get; private init; }

    /// <summary>
    /// Set instead of <see cref="Result"/> when another request is settling the same
    /// authorization right now. The caller should answer 409, not 402.
    /// </summary>
    public string? ConflictReason { get; private init; }

    // --- Internal state needed only by X402PaymentProcessor.SettleAsync and by the outbound
    // half of the pipeline. Never meant to be read by a consumer of this library. ---

    /// <summary>The proof of payment that was verified, for a request that may proceed.</summary>
    internal PaymentPayload? Payload { get; private init; }

    /// <summary>The server's own requirement the payload was verified against.</summary>
    internal PaymentRequirements? Requirements { get; private init; }

    /// <summary>The ledger identity of the authorization, for a request that may proceed.</summary>
    internal PaymentIdentity Identity { get; private init; }

    /// <summary>
    /// The settlement already on record for this authorization, when it was presented again.
    /// <see cref="X402PaymentProcessor.SettleAsync"/> replays this instead of settling twice.
    /// </summary>
    internal SettleResponse? MemorisedSettlement { get; private init; }

    /// <summary>
    /// The demand this attempt would refuse with, kept so a settlement that fails after the
    /// endpoint has already produced content can still fall back to a 402 instead of delivering
    /// what was never paid for.
    /// </summary>
    internal PaymentRequired? Demand { get; private init; }

    /// <summary>Builds the refusal returned when no usable proof of payment reaches the engine.</summary>
    internal static PaymentAttempt Refused(PaymentRequiredResult result) => new()
    {
        CanContinue = false,
        Result = result,
        FailureReason = result.Demand.Error,
    };

    /// <summary>Builds the refusal returned when another request is settling the same authorization.</summary>
    internal static PaymentAttempt Conflict(string reason) => new()
    {
        CanContinue = false,
        ConflictReason = reason,
        FailureReason = reason,
    };

    /// <summary>Builds the outcome for an authorization the ledger already knows the settlement of.</summary>
    internal static PaymentAttempt AlreadySettled(
        PaymentPayload payload, PaymentRequirements requirements, AssetDescriptor asset,
        string? payer, PaymentIdentity identity, SettleResponse memorised,
        ResourceInfo resource, IReadOnlyList<PaymentRequirements> offered) => new()
    {
        CanContinue = true,
        SettledAsset = asset,
        Payer = payer,
        Payload = payload,
        Requirements = requirements,
        Identity = identity,
        MemorisedSettlement = memorised,
        Demand = new PaymentRequired { Resource = resource, Accepts = offered },
    };

    /// <summary>Builds the outcome for a freshly verified authorization awaiting settlement.</summary>
    internal static PaymentAttempt Accepted(
        PaymentPayload payload, PaymentRequirements requirements, AssetDescriptor asset,
        string? payer, PaymentIdentity identity,
        ResourceInfo resource, IReadOnlyList<PaymentRequirements> offered) => new()
    {
        CanContinue = true,
        SettledAsset = asset,
        Payer = payer,
        Payload = payload,
        Requirements = requirements,
        Identity = identity,
        Demand = new PaymentRequired { Resource = resource, Accepts = offered },
    };
}
