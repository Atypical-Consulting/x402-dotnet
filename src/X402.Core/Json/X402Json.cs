using System.Text.Json;
using System.Text.Json.Serialization;
using X402.Protocol;

namespace X402.Json;

/// <summary>
/// The single serialisation configuration for every x402 protocol object. Using anything else
/// risks emitting a shape the rest of the ecosystem will not accept.
/// </summary>
public static class X402Json
{
    /// <summary>Serializer options wired to the source-generated context. Trim- and AOT-safe.</summary>
    public static JsonSerializerOptions Options { get; } = new(X402JsonContext.Default.Options);
}

/// <summary>Source-generated serialisation context for the x402 protocol types.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PaymentRequired))]
[JsonSerializable(typeof(PaymentRequirements))]
[JsonSerializable(typeof(PaymentPayload))]
[JsonSerializable(typeof(ExactEvmPayload))]
[JsonSerializable(typeof(Eip3009Authorization))]
[JsonSerializable(typeof(FacilitatorRequest))]
[JsonSerializable(typeof(VerifyResponse))]
[JsonSerializable(typeof(SettleResponse))]
[JsonSerializable(typeof(SupportedResponse))]
[JsonSerializable(typeof(SupportedKind))]
public sealed partial class X402JsonContext : JsonSerializerContext;
