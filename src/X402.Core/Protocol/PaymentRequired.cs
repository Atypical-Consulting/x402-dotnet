using System.Text.Json;
using System.Text.Json.Serialization;

namespace X402.Protocol;

/// <summary>
/// The payment demand a resource server returns when an unpaid request reaches a priced resource.
/// Carried base64-encoded in the <c>PAYMENT-REQUIRED</c> header over HTTP.
/// </summary>
public sealed record PaymentRequired
{
    /// <summary>Protocol version identifier. Always 2 — this library does not speak v1.</summary>
    [JsonPropertyName("x402Version")]
    public int X402Version { get; init; } = 2;

    /// <summary>Human-readable explanation of why payment is required, or why one was refused.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Description of the protected resource.</summary>
    [JsonPropertyName("resource")]
    public required ResourceInfo Resource { get; init; }

    /// <summary>Acceptable ways to pay, in the server's order of preference.</summary>
    [JsonPropertyName("accepts")]
    public required IReadOnlyList<PaymentRequirements> Accepts { get; init; }

    /// <summary>Protocol extensions advertised by the server, keyed by extension identifier.</summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, ProtocolExtension>? Extensions { get; init; }
}

/// <summary>Metadata describing a resource behind a paywall.</summary>
public sealed record ResourceInfo
{
    /// <summary>URL of the protected resource.</summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>Human-readable description of the resource.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>MIME type of the expected response.</summary>
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }

    /// <summary>Name of the hosting service. Printable ASCII, 32 characters at most.</summary>
    [JsonPropertyName("serviceName")]
    public string? ServiceName { get; init; }

    /// <summary>Discovery tags. Five entries at most, each printable ASCII and 32 characters at most.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Absolute URL of a service icon. 2048 characters at most.</summary>
    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; init; }
}

/// <summary>A protocol extension entry: opaque data plus the schema that describes it.</summary>
public sealed record ProtocolExtension
{
    /// <summary>Extension-specific data.</summary>
    [JsonPropertyName("info")]
    public required JsonElement Info { get; init; }

    /// <summary>JSON Schema describing the shape of <see cref="Info"/>.</summary>
    [JsonPropertyName("schema")]
    public required JsonElement Schema { get; init; }
}
