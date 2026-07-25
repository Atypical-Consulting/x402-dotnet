using Microsoft.AspNetCore.Http.Features;

namespace X402.AspNetCore.Engine;

/// <summary>
/// Marker carried on <see cref="Microsoft.AspNetCore.Http.HttpContext.Features"/> for the
/// lifetime of one request, so the outbound half of the pipeline can find what the inbound half
/// decided.
/// </summary>
/// <remarks>
/// <see cref="Attempt"/> is set once a payment is authorized, whether by route pricing or by the
/// imperative gate (<c>IX402PaymentGate.RequireAsync</c>). <see cref="Buffer"/> and
/// <see cref="OriginalBody"/> are set at the same time, by
/// <see cref="X402PaymentProcessor.OpenBuffering"/> — the single place buffering is installed,
/// whichever side opened the payment. A request that opens a payment but never installs buffering
/// is a bug in this library, not in a consumer.
/// </remarks>
internal sealed class X402RequestFeature
{
    /// <summary>The authorization decided for this request, once one has been made.</summary>
    public PaymentAttempt? Attempt { get; set; }

    /// <summary>The response buffering installed for this request, once one has been made.</summary>
    public BufferingResponseBodyFeature? Buffer { get; set; }

    /// <summary>
    /// The <see cref="IHttpResponseBodyFeature"/> that was active before <see cref="Buffer"/> was
    /// installed, so the outbound half can restore it once the request is done — even when
    /// buffering was installed deep inside the endpoint, by the imperative gate, rather than by the
    /// middleware itself.
    /// </summary>
    public IHttpResponseBodyFeature? OriginalBody { get; set; }
}
