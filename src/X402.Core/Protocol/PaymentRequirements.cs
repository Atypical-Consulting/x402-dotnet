using System.Text.Json;
using System.Text.Json.Serialization;

namespace X402.Protocol;

/// <summary>
/// One acceptable way to pay for a resource: a scheme, a network, an asset, an amount and a payee.
/// </summary>
public sealed record PaymentRequirements
{
    /// <summary>Payment scheme identifier, for example <c>exact</c>.</summary>
    [JsonPropertyName("scheme")]
    public required string Scheme { get; init; }

    /// <summary>Network identifier in CAIP-2 form, for example <c>eip155:8453</c>.</summary>
    [JsonPropertyName("network")]
    public required string Network { get; init; }

    /// <summary>Amount due, in the asset's atomic units. A string, never a JSON number.</summary>
    [JsonPropertyName("amount")]
    public required string Amount { get; init; }

    /// <summary>Token contract address, or an ISO 4217 code for fiat rails.</summary>
    [JsonPropertyName("asset")]
    public required string Asset { get; init; }

    /// <summary>Address the funds must reach.</summary>
    [JsonPropertyName("payTo")]
    public required string PayTo { get; init; }

    /// <summary>How long the payer has to complete the payment, in seconds.</summary>
    [JsonPropertyName("maxTimeoutSeconds")]
    public required int MaxTimeoutSeconds { get; init; }

    /// <summary>
    /// Scheme-specific data. For <c>exact</c> on EVM this carries the token's EIP-712 domain
    /// (<c>name</c>, <c>version</c>) and optionally <c>assetTransferMethod</c>.
    /// </summary>
    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}
