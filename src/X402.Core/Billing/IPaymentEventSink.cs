namespace X402.Billing;

/// <summary>
/// Receives one event per payment outcome, successful or not. Substituting an implementation is
/// how a persistent billing ledger is added without changing this library.
/// </summary>
/// <remarks>
/// The engine invokes this on every terminal branch, including verification and settlement
/// failures. An exception thrown here is logged and swallowed: billing must never fail a payment
/// that has already settled on-chain.
/// </remarks>
public interface IPaymentEventSink
{
    /// <summary>Records a payment event.</summary>
    ValueTask RecordAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default);
}

/// <summary>What happened to a payment.</summary>
public enum PaymentEventStatus
{
    /// <summary>A demand was issued; no payment was presented.</summary>
    PaymentRequired,

    /// <summary>A payment was presented and the facilitator rejected it.</summary>
    VerificationFailed,

    /// <summary>A payment was verified; settlement has not been attempted yet.</summary>
    Verified,

    /// <summary>Settlement was attempted and failed.</summary>
    SettlementFailed,

    /// <summary>Settlement succeeded; funds moved.</summary>
    Settled,
}

/// <summary>A single billable moment in the life of a request.</summary>
public sealed record PaymentEvent
{
    /// <summary>When the event occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The resource that was paid for.</summary>
    public required string Resource { get; init; }

    /// <summary>Amount, in the asset's atomic units.</summary>
    public required string Amount { get; init; }

    /// <summary>Token contract address of the asset.</summary>
    public required string Asset { get; init; }

    /// <summary>Network identifier in CAIP-2 form.</summary>
    public required string Network { get; init; }

    /// <summary>What happened.</summary>
    public required PaymentEventStatus Status { get; init; }

    /// <summary>Transaction hash, when settlement produced one.</summary>
    public string? Transaction { get; init; }

    /// <summary>Payer's address, when known.</summary>
    public string? Payer { get; init; }

    /// <summary>Why the payment failed, when it did.</summary>
    public string? FailureReason { get; init; }
}
