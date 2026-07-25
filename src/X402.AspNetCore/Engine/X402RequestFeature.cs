namespace X402.AspNetCore.Engine;

/// <summary>
/// Marker carried on <see cref="Microsoft.AspNetCore.Http.HttpContext.Features"/> for the
/// lifetime of one request, so the outbound half of the pipeline can find what the inbound half
/// decided.
/// </summary>
/// <remarks>
/// <see cref="Attempt"/> is set once a payment is authorized, whether by route pricing or by the
/// imperative gate. <see cref="Buffer"/> is set once the corresponding response buffering has been
/// installed. The two are set together; a request that opens a payment but never installs
/// buffering is a bug in this library, not in a consumer.
/// </remarks>
internal sealed class X402RequestFeature
{
    /// <summary>The authorization decided for this request, once one has been made.</summary>
    public PaymentAttempt? Attempt { get; set; }

    /// <summary>The response buffering installed for this request, once one has been made.</summary>
    public BufferingResponseBodyFeature? Buffer { get; set; }
}
