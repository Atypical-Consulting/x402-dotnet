using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.Engine;
using X402.AspNetCore.Idempotency;

namespace X402.AspNetCore.Middleware;

/// <summary>
/// Prices declared routes, and carries the outbound half of the flow — settlement and the
/// settlement header — for both the route table and the imperative gate.
/// </summary>
/// <remarks>
/// This shape mirrors <c>PaidServerFixture.RunPaidAsync</c>, task 10's reviewed reference
/// implementation of the same outbound half, rather than reinventing it: restoring the original
/// <see cref="IHttpResponseBodyFeature"/> in a <c>finally</c> that runs on every path, consulting
/// <see cref="BufferingResponseBodyFeature.Poisoned"/> in straight-line code rather than only in a
/// catch clause, abandoning the authorization only when settlement provably never happened, and
/// checking <see cref="PaymentAttempt.ConflictReason"/> before <see cref="PaymentAttempt.Result"/>.
/// </remarks>
internal sealed class X402Middleware(
    RequestDelegate next,
    X402PaymentProcessor processor,
    ISettlementLedger ledger,
    IOptions<X402Options> options,
    IReadOnlyList<X402Route> routes)
{
    /// <summary>Runs the middleware for one request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var route = routes.FirstOrDefault(r => r.Matches(context.Request.Path));

        // The marker is always set: the imperative gate uses it to tell the outbound half of the
        // pipeline that a payment was opened late, even on a request no route here matched.
        var feature = new X402RequestFeature();
        context.Features.Set(feature);

        if (route is null)
        {
            await next(context);
            EnsureLateAttemptIsBuffered(feature);
            return;
        }

        var attempt = await processor.AuthorizeAsync(
            context, route.Prices, route.Overrides, context.RequestAborted);

        if (!attempt.CanContinue)
        {
            await WriteRefusalAsync(context, attempt);
            return;
        }

        feature.Attempt = attempt;
        await RunAndSettleAsync(context, feature, attempt);
    }

    private async Task RunAndSettleAsync(
        HttpContext context, X402RequestFeature feature, PaymentAttempt attempt)
    {
        var original = context.Features.Get<IHttpResponseBodyFeature>()!;
        var buffering = new BufferingResponseBodyFeature(
            original, options.Value.MaxBufferedResponseBytes,
            ct => processor.SettleAsync(context, attempt, ct));

        feature.Buffer = buffering;
        context.Features.Set<IHttpResponseBodyFeature>(buffering);

        try
        {
            await next(context);
        }
        catch (BufferingSettlementFailedException)
        {
            // Settlement was attempted — and failed — exactly when the buffer crossed the cap,
            // before anything reached the real network: still refuse cleanly.
            buffering.Discard();
            context.Features.Set(original);
            await new PaymentRequiredResult(attempt.Demand!).ExecuteAsync(context);
            return;
        }
        catch
        {
            // Abandon only when settlement provably never happened. It did, and the ledger already
            // has its outcome on record, when the buffer overflowed successfully (funds moved) or
            // was poisoned by a failed cap-crossing settlement that this handler's own broad catch
            // swallowed before it reached us. The ledger tolerates a mistaken Abandon defensively —
            // it refuses to erase a completed entry — but that is a backstop, not a licence to call
            // it when the correct answer is already known here.
            if (!buffering.Overflowed && !buffering.Poisoned)
            {
                await ledger.AbandonAsync(attempt.Identity, context.RequestAborted);
            }

            buffering.Discard();
            context.Features.Set(original);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }

            return;
        }
        finally
        {
            // Always restore, whichever path above ran — leaking a buffering feature onto a later
            // request would be a serious, hard-to-diagnose bug.
            context.Features.Set(original);
        }

        if (buffering.Poisoned)
        {
            // BufferingSettlementFailedException was thrown at the cap and swallowed somewhere
            // inside the handler (a broad catch around writes, tolerating e.g. a client disconnect,
            // is a common streaming pattern) instead of propagating to the catch above. Consult the
            // flag directly rather than trust that every handler propagates it: settlement was
            // attempted and failed, so falling through to SettleAsync below would settle — and
            // charge — a second time for the same authorization.
            buffering.Discard();
            await new PaymentRequiredResult(attempt.Demand!).ExecuteAsync(context);
            return;
        }

        if (buffering.Overflowed)
        {
            // Settlement already happened when the cap was crossed.
            await buffering.CompleteAsync();
            return;
        }

        var settled = await processor.SettleAsync(context, attempt, context.RequestAborted);
        if (settled)
        {
            await buffering.FlushBufferAsync(context.RequestAborted);
            return;
        }

        // Settlement failed within the cap: the buffered content is discarded, never delivered.
        buffering.Discard();
        await new PaymentRequiredResult(attempt.Demand!).ExecuteAsync(context);
    }

    private static async Task WriteRefusalAsync(HttpContext context, PaymentAttempt attempt)
    {
        if (attempt.ConflictReason is { } conflict)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsync(conflict, context.RequestAborted);
            return;
        }

        await attempt.Result!.ExecuteAsync(context);
    }

    private static void EnsureLateAttemptIsBuffered(X402RequestFeature feature)
    {
        // Nothing to settle here: the imperative gate installs its own buffering and settles on
        // its own outbound path. This hook exists only to catch a bug — a payment opened without
        // buffering ever being installed, which would silently skip settlement.
        if (feature.Attempt is not null && feature.Buffer is null)
        {
            throw new InvalidOperationException(
                "A payment gate was opened but no buffering was installed. This is a bug in " +
                "X402.AspNetCore; please report it.");
        }
    }
}
