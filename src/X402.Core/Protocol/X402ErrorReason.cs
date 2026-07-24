namespace X402.Protocol;

/// <summary>
/// The error codes defined by the x402 specification. A facilitator may return a code absent from
/// this list; such codes are propagated verbatim rather than normalised or hidden.
/// </summary>
public static class X402ErrorReason
{
    /// <summary>The payer does not hold enough of the asset.</summary>
    public const string InsufficientFunds = "insufficient_funds";

    /// <summary>The authorization is not valid yet.</summary>
    public const string InvalidExactEvmPayloadAuthorizationValidAfter =
        "invalid_exact_evm_payload_authorization_valid_after";

    /// <summary>The authorization has expired.</summary>
    public const string InvalidExactEvmPayloadAuthorizationValidBefore =
        "invalid_exact_evm_payload_authorization_valid_before";

    /// <summary>The authorised amount does not match the required amount.</summary>
    public const string InvalidExactEvmPayloadAuthorizationValueMismatch =
        "invalid_exact_evm_payload_authorization_value_mismatch";

    /// <summary>The signature is invalid or was not produced by the stated payer.</summary>
    public const string InvalidExactEvmPayloadSignature = "invalid_exact_evm_payload_signature";

    /// <summary>The recipient does not match the payment requirements.</summary>
    public const string InvalidExactEvmPayloadRecipientMismatch =
        "invalid_exact_evm_payload_recipient_mismatch";

    /// <summary>The network is not supported.</summary>
    public const string InvalidNetwork = "invalid_network";

    /// <summary>The payment payload is malformed.</summary>
    public const string InvalidPayload = "invalid_payload";

    /// <summary>The payment requirements are malformed.</summary>
    public const string InvalidPaymentRequirements = "invalid_payment_requirements";

    /// <summary>The scheme is not supported.</summary>
    public const string InvalidScheme = "invalid_scheme";

    /// <summary>The facilitator does not implement the scheme.</summary>
    public const string UnsupportedScheme = "unsupported_scheme";

    /// <summary>The protocol version is not supported.</summary>
    public const string InvalidX402Version = "invalid_x402_version";

    /// <summary>The blockchain transaction failed or was rejected.</summary>
    public const string InvalidTransactionState = "invalid_transaction_state";

    /// <summary>An unexpected error occurred during verification.</summary>
    public const string UnexpectedVerifyError = "unexpected_verify_error";

    /// <summary>An unexpected error occurred during settlement.</summary>
    public const string UnexpectedSettleError = "unexpected_settle_error";

    /// <summary>Every code defined by the specification.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        InsufficientFunds,
        InvalidExactEvmPayloadAuthorizationValidAfter,
        InvalidExactEvmPayloadAuthorizationValidBefore,
        InvalidExactEvmPayloadAuthorizationValueMismatch,
        InvalidExactEvmPayloadSignature,
        InvalidExactEvmPayloadRecipientMismatch,
        InvalidNetwork,
        InvalidPayload,
        InvalidPaymentRequirements,
        InvalidScheme,
        UnsupportedScheme,
        InvalidX402Version,
        InvalidTransactionState,
        UnexpectedVerifyError,
        UnexpectedSettleError,
    };
}
