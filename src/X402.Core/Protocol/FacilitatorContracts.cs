using System.Text.Json;
using System.Text.Json.Serialization;

namespace X402.Protocol;

/// <summary>Body of a facilitator <c>/verify</c> or <c>/settle</c> call.</summary>
public sealed record FacilitatorRequest
{
    /// <summary>Protocol version identifier. Always 2.</summary>
    [JsonPropertyName("x402Version")]
    public int X402Version { get; init; } = 2;

    /// <summary>The payer's proof of payment.</summary>
    [JsonPropertyName("paymentPayload")]
    public required PaymentPayload PaymentPayload { get; init; }

    /// <summary>The requirement to verify against, as constructed by the resource server.</summary>
    [JsonPropertyName("paymentRequirements")]
    public required PaymentRequirements PaymentRequirements { get; init; }
}

/// <summary>Outcome of a facilitator <c>/verify</c> call. No funds move.</summary>
public sealed record VerifyResponse
{
    /// <summary>Whether the authorization is valid.</summary>
    [JsonPropertyName("isValid")]
    public required bool IsValid { get; init; }

    /// <summary>Why the authorization was rejected. See <see cref="X402ErrorReason"/>.</summary>
    [JsonPropertyName("invalidReason")]
    public string? InvalidReason { get; init; }

    /// <summary>Address recovered from the signature.</summary>
    [JsonPropertyName("payer")]
    public string? Payer { get; init; }

    /// <summary>Scheme-specific additional data.</summary>
    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}

/// <summary>Outcome of a facilitator <c>/settle</c> call. Funds have moved, or failed to.</summary>
public sealed record SettleResponse
{
    /// <summary>Whether settlement succeeded.</summary>
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    /// <summary>Why settlement failed. See <see cref="X402ErrorReason"/>.</summary>
    [JsonPropertyName("errorReason")]
    public string? ErrorReason { get; init; }

    /// <summary>Address of the payer.</summary>
    [JsonPropertyName("payer")]
    public string? Payer { get; init; }

    /// <summary>Transaction hash, or an empty string when settlement failed.</summary>
    [JsonPropertyName("transaction")]
    public required string Transaction { get; init; }

    /// <summary>Network the transaction was broadcast to, in CAIP-2 form.</summary>
    [JsonPropertyName("network")]
    public required string Network { get; init; }

    /// <summary>Amount actually settled, in atomic units.</summary>
    [JsonPropertyName("amount")]
    public string? Amount { get; init; }

    /// <summary>Protocol extension data.</summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, ProtocolExtension>? Extensions { get; init; }
}

/// <summary>What a facilitator advertises at <c>/supported</c>.</summary>
public sealed record SupportedResponse
{
    /// <summary>Scheme and network pairs the facilitator handles. Note that assets are not listed.</summary>
    [JsonPropertyName("kinds")]
    public required IReadOnlyList<SupportedKind> Kinds { get; init; }

    /// <summary>Extension identifiers the facilitator implements.</summary>
    [JsonPropertyName("extensions")]
    public required IReadOnlyList<string> Extensions { get; init; }

    /// <summary>Public signer addresses, keyed by CAIP-2 pattern such as <c>eip155:*</c>.</summary>
    [JsonPropertyName("signers")]
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Signers { get; init; }
}

/// <summary>One scheme and network pair supported by a facilitator.</summary>
public sealed record SupportedKind
{
    /// <summary>Protocol version supported.</summary>
    [JsonPropertyName("x402Version")]
    public int X402Version { get; init; } = 2;

    /// <summary>Payment scheme identifier.</summary>
    [JsonPropertyName("scheme")]
    public required string Scheme { get; init; }

    /// <summary>Network identifier in CAIP-2 form.</summary>
    [JsonPropertyName("network")]
    public required string Network { get; init; }

    /// <summary>Additional scheme-specific configuration.</summary>
    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}
