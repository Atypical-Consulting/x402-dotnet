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
    /// <returns>
    /// What the gate decided; see <see cref="PaymentGateResult"/>. Its <c>Result</c> is non-null
    /// exactly when <c>CanContinue</c> is false, so <c>if (!result.CanContinue) return
    /// result.Result;</c> is always correct — including when another request is settling the same
    /// authorization right now.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Called outside an HTTP request, called without <c>UseX402()</c> in the pipeline, or called
    /// after the response has already started.
    /// </exception>
    /// <remarks>
    /// <b>Ignoring a refusal is a hazard, not a suggestion — but not a profitable one.</b> A refusal
    /// is buffered exactly like an accepted payment, so if the caller does not check
    /// <c>CanContinue</c> and return <c>Result</c> — writing a success response regardless — nothing
    /// it writes reaches the real transport before the pipeline gets a chance to look. Once the
    /// handler returns, it discards that response, delivers the refusal that should have been
    /// returned in its place, and logs the substitution at <c>LogLevel.Error</c>. This is a backstop
    /// for a bug, not a licence to skip the check: still verify <c>CanContinue</c> before writing
    /// anything, every time.
    /// </remarks>
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
