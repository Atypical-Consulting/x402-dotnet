using X402.Pricing;

namespace X402.AspNetCore.Gate;

/// <summary>
/// Demands payment from inside a handler, at a price computed from the request.
/// </summary>
/// <remarks>
/// Requires <c>UseX402()</c> in the pipeline: that middleware carries settlement and the
/// settlement header on the way out. Opening a gate without it throws rather than delivering
/// content that was never settled.
/// </remarks>
public interface IX402PaymentGate
{
    /// <summary>Demands payment at the given prices before the handler continues.</summary>
    /// <param name="prices">One price per accepted asset, computed by the caller for this request.</param>
    /// <param name="options">Per-call overrides for the demand advertised to the payer. Optional.</param>
    /// <param name="cancellationToken">Propagated to authorization against the facilitator.</param>
    /// <returns>What the gate decided; see <see cref="PaymentGateResult"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Called outside an HTTP request, called without <c>UseX402()</c> in the pipeline, or called
    /// after the response has already started.
    /// </exception>
    ValueTask<PaymentGateResult> RequireAsync(
        PriceSet prices,
        PaymentGateOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Per-call overrides for a payment demand opened through <see cref="IX402PaymentGate"/>.</summary>
public sealed class PaymentGateOptions
{
    /// <summary>Description advertised in the demand.</summary>
    public string? Description { get; set; }

    /// <summary>MIME type advertised in the demand.</summary>
    public string? MimeType { get; set; }
}
