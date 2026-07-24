using System.Buffers;
using System.Text;
using System.Text.Json;
using X402.Json;

namespace X402.Transport;

/// <summary>
/// Encodes and decodes protocol objects for header transport: base64 of UTF-8 JSON.
/// </summary>
public static class X402Codec
{
    /// <summary>The only protocol version this library speaks.</summary>
    public const int SupportedVersion = 2;

    /// <summary>Maximum allowed header size in bytes. Headers exceeding this are rejected as implausibly large.</summary>
    private const int MaxHeaderSizeBytes = 10 * 1024 * 1024; // 10 MB

    /// <summary>Encodes a protocol object for a transport header.</summary>
    public static string Encode<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, typeof(T), X402Json.Options);
        return Convert.ToBase64String(json);
    }

    /// <summary>
    /// Decodes a protocol object from a transport header. Never throws: the input comes from the
    /// network, so every malformed shape is reported through <paramref name="error"/> instead.
    /// Headers larger than 10 MB are rejected as implausibly large.
    /// </summary>
    /// <param name="header">The raw header value.</param>
    /// <param name="value">The decoded object, or <c>null</c> on failure.</param>
    /// <param name="error">A human-readable reason, or <c>null</c> on success.</param>
    /// <returns><c>true</c> when decoding succeeded.</returns>
    public static bool TryDecode<T>(string? header, out T? value, out string? error)
        where T : class
    {
        value = null;
        error = null;

        if (string.IsNullOrWhiteSpace(header))
        {
            error = "the header is absent or empty";
            return false;
        }

        if (header.Length > MaxHeaderSizeBytes)
        {
            error = $"the header exceeds the maximum size of {MaxHeaderSizeBytes} bytes";
            return false;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(
            ((header.Length * 3) / 4) + 4);
        try
        {
            if (!Convert.TryFromBase64String(header, buffer, out var written))
            {
                error = "the header is not valid base64";
                return false;
            }

            try
            {
                value = JsonSerializer.Deserialize<T>(
                    buffer.AsSpan(0, written), X402Json.Options);
            }
            catch (JsonException exception)
            {
                error = $"the header does not contain a valid {typeof(T).Name}: {exception.Message}";
                return false;
            }
            catch (NotSupportedException)
            {
                error = $"{typeof(T).Name} is not a registered x402 protocol type; " +
                        $"add [JsonSerializable(typeof({typeof(T).Name}))] to X402JsonContext";
                return false;
            }

            if (value is null)
            {
                error = $"the header decoded to a null {typeof(T).Name}";
                return false;
            }

            if (VersionOf(value) is { } version && version != SupportedVersion)
            {
                error = $"unsupported x402 protocol version {version}; this library implements " +
                        $"version {SupportedVersion} only";
                value = null;
                return false;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int? VersionOf(object value) => value switch
    {
        Protocol.PaymentRequired p => p.X402Version,
        Protocol.PaymentPayload p => p.X402Version,
        _ => null,
    };
}
