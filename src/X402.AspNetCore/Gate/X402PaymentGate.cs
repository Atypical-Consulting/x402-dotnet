using Microsoft.AspNetCore.Http;
using X402.AspNetCore.Engine;
using X402.AspNetCore.Middleware;
using X402.Pricing;

namespace X402.AspNetCore.Gate;

/// <summary>
/// Opens a payment mid-request. Authorization and the outbound half both run through
/// <see cref="X402PaymentProcessor"/> — the same processor a route match drives from
/// <see cref="X402Middleware"/> — so nothing here re-implements settlement or response buffering;
/// see <see cref="X402PaymentProcessor.OpenBuffering"/>.
/// </summary>
internal sealed class X402PaymentGate(
    IHttpContextAccessor accessor, X402PaymentProcessor processor) : IX402PaymentGate
{
    /// <inheritdoc />
    public async ValueTask<PaymentGateResult> RequireAsync(
        PriceSet prices, PaymentGateOptions? gateOptions = null,
        CancellationToken cancellationToken = default)
    {
        var context = accessor.HttpContext
            ?? throw new InvalidOperationException(
                "IX402PaymentGate was used outside of an HTTP request.");

        var feature = context.Features.Get<X402RequestFeature>()
            ?? throw new InvalidOperationException(
                "IX402PaymentGate requires UseX402() in the pipeline. Without it settlement " +
                "would never run and paid content would be delivered for free. Add " +
                "app.UseX402(); before the endpoints.");

        if (context.Response.HasStarted)
        {
            throw new InvalidOperationException(
                "IX402PaymentGate was called after the response started. Demand payment before " +
                "writing anything.");
        }

        var overrides = new ResourceInfoOverrides
        {
            Description = gateOptions?.Description,
            MimeType = gateOptions?.MimeType,
        };

        var attempt = await processor.AuthorizeAsync(context, prices, overrides, cancellationToken);

        if (attempt.CanContinue)
        {
            // The same buffering installation a route match gets from X402Middleware: one
            // implementation, in X402PaymentProcessor.OpenBuffering, used by both. The middleware's
            // outbound half (X402PaymentProcessor.FinishAsync, driven from
            // X402Middleware.RunProtectedAsync) finishes the job once this handler returns, reading
            // this same feature.
            processor.OpenBuffering(context, feature, attempt);
        }
        else
        {
            // Buffered too, even though there is nothing to settle: this is what lets
            // X402Middleware.RunProtectedAsync reliably withhold a response from a caller that
            // ignores PaymentGateResult.CanContinue and writes one anyway — see the hazard
            // documented there and on IX402PaymentGate.RequireAsync. Without this, such a write
            // would reach the real transport directly, and the response would already have started
            // by the time anything downstream could intervene.
            processor.OpenRefusalBuffering(context, feature, attempt);
        }

        return new PaymentGateResult(attempt);
    }
}
