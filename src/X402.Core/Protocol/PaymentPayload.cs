using System.Text.Json;
using System.Text.Json.Serialization;

namespace X402.Protocol;

/// <summary>
/// A payer's proof of payment. Carried base64-encoded in the <c>PAYMENT-SIGNATURE</c> header
/// over HTTP, and in <c>_meta["x402/payment"]</c> over MCP.
/// </summary>
public sealed record PaymentPayload
{
    /// <summary>Protocol version identifier. Always 2.</summary>
    [JsonPropertyName("x402Version")]
    public int X402Version { get; init; } = 2;

    /// <summary>The resource being paid for, echoed from the demand.</summary>
    [JsonPropertyName("resource")]
    public ResourceInfo? Resource { get; init; }

    /// <summary>
    /// The requirement the payer claims to satisfy. This is caller-supplied data: a server must
    /// use it only to select which of its own offered requirements to verify against, never as
    /// the requirement it sends to the facilitator.
    /// </summary>
    [JsonPropertyName("accepted")]
    public required PaymentRequirements Accepted { get; init; }

    /// <summary>Scheme-specific payment data. See <see cref="ExactEvmPayload"/> for <c>exact</c> on EVM.</summary>
    [JsonPropertyName("payload")]
    public required JsonElement Payload { get; init; }

    /// <summary>Protocol extension data echoed back by the payer.</summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, ProtocolExtension>? Extensions { get; init; }
}

/// <summary>The <c>payload</c> of the <c>exact</c> scheme on EVM networks.</summary>
public sealed record ExactEvmPayload
{
    /// <summary>65-byte EIP-712 signature of the authorization, hex-encoded with a <c>0x</c> prefix.</summary>
    [JsonPropertyName("signature")]
    public required string Signature { get; init; }

    /// <summary>The EIP-3009 authorization that was signed.</summary>
    [JsonPropertyName("authorization")]
    public required Eip3009Authorization Authorization { get; init; }
}

/// <summary>Parameters of an EIP-3009 <c>transferWithAuthorization</c> call.</summary>
public sealed record Eip3009Authorization
{
    /// <summary>Payer's address.</summary>
    [JsonPropertyName("from")]
    public required string From { get; init; }

    /// <summary>Payee's address.</summary>
    [JsonPropertyName("to")]
    public required string To { get; init; }

    /// <summary>Amount in atomic units. A string, never a JSON number.</summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    /// <summary>Unix timestamp, as a string, before which the authorization is not yet valid.</summary>
    [JsonPropertyName("validAfter")]
    public required string ValidAfter { get; init; }

    /// <summary>Unix timestamp, as a string, after which the authorization has expired.</summary>
    [JsonPropertyName("validBefore")]
    public required string ValidBefore { get; init; }

    /// <summary>32-byte replay nonce, hex-encoded with a <c>0x</c> prefix.</summary>
    [JsonPropertyName("nonce")]
    public required string Nonce { get; init; }
}

/// <summary>Typed access to the <c>exact</c> EVM scheme payload.</summary>
public static class ExactEvmPayloadExtensions
{
    /// <summary>Reads the payload as an <c>exact</c> EVM payment.</summary>
    /// <exception cref="System.Text.Json.JsonException">The payload is not a valid exact/EVM payload.</exception>
    public static ExactEvmPayload AsExactEvm(this PaymentPayload payload) =>
        payload.Payload.Deserialize<ExactEvmPayload>(Json.X402Json.Options)
        ?? throw new JsonException("The payment payload is not a valid exact/EVM payload.");

    /// <summary>Returns a copy of the payload carrying the given <c>exact</c> EVM payment.</summary>
    public static PaymentPayload WithExactEvm(this PaymentPayload payload, ExactEvmPayload exact) =>
        payload with
        {
            Payload = JsonSerializer.SerializeToElement(exact, Json.X402Json.Options),
        };
}
