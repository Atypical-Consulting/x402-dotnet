using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using X402.AspNetCore.Engine;
using X402.AspNetCore.Idempotency;

namespace X402.AspNetCore.Middleware;

/// <summary>
/// Prices declared routes, and carries the outbound half of the flow — settlement and the
/// settlement header — for both the route table and the imperative gate.
/// </summary>
/// <remarks>
/// The outbound half itself — installing response buffering, settling, then deciding to deliver or
/// withhold — lives in <see cref="X402PaymentProcessor.OpenBuffering"/> and
/// <see cref="X402PaymentProcessor.FinishAsync"/>, the single implementation both a route match
/// here and <c>IX402PaymentGate.RequireAsync</c> drive. What stays here is specific to owning
/// <c>next</c>: restoring the original <see cref="Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature"/>
/// in a <c>finally</c> that runs on every path, including when the endpoint throws; abandoning the
/// authorization only when settlement provably never happened; and checking
/// <see cref="PaymentAttempt.ConflictReason"/> before <see cref="PaymentAttempt.Result"/> is
/// dereferenced.
/// </remarks>
internal sealed partial class X402Middleware(
    RequestDelegate next,
    X402PaymentProcessor processor,
    ISettlementLedger ledger,
    IReadOnlyList<X402Route> routes,
    ILogger<X402Middleware> logger)
{
    /// <summary>Runs the middleware for one request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var route = routes.FirstOrDefault(r => r.Matches(context.Request.Path));

        // The marker is always set: IX402PaymentGate.RequireAsync uses its presence to tell
        // whether UseX402 ran ahead of it, and — on a request no route here matched — to carry a
        // payment it opens late from inside the endpoint.
        var feature = new X402RequestFeature();
        context.Features.Set(feature);

        if (route is not null)
        {
            var attempt = await processor.AuthorizeAsync(
                context, route.Prices, route.Overrides, context.RequestAborted);

            if (!attempt.CanContinue)
            {
                await WriteRefusalAsync(context, attempt);
                return;
            }

            processor.OpenBuffering(context, feature, attempt);
        }

        // No route matched: run the request as-is. If the endpoint uses the imperative gate, it
        // installs its own buffering on `feature` — through the very same
        // X402PaymentProcessor.OpenBuffering — before it writes anything.
        await RunProtectedAsync(context, feature);
    }

    /// <summary>
    /// Runs the rest of the pipeline, then finishes whatever payment ended up open on
    /// <paramref name="feature"/> by the time it returns — installed above for a route match, or
    /// installed by the gate from inside <c>next</c>-invoked code either way.
    /// </summary>
    private async Task RunProtectedAsync(HttpContext context, X402RequestFeature feature)
    {
        try
        {
            await next(context);
        }
        catch (BufferingSettlementFailedException)
        {
            // Settlement was attempted — and failed — exactly when the buffer crossed the cap,
            // before anything reached the real network: still refuse cleanly. This exception type
            // can only be thrown by the buffering feature itself, so buffering must be open here —
            // for an accepted attempt (feature.Attempt set) or for a refused one buffered
            // defensively by OpenRefusalBuffering (feature.Refusal set instead; see the hazard
            // documented on IX402PaymentGate.RequireAsync).
            var buffering = feature.Buffer!;
            buffering.Discard();
            RestoreOriginalBody(context, feature);
            if (feature.Attempt is { } attempt)
            {
                await new PaymentRequiredResult(attempt.Demand!).ExecuteAsync(context);
            }
            else
            {
                await WriteRefusalAsync(context, feature.Refusal!);
            }

            return;
        }
        catch (Exception exception) when (feature.Attempt is not null)
        {
            // Only handled when a payment was actually open for this request — otherwise the
            // filter is false and the exception propagates untouched, exactly as it would without
            // this middleware in the pipeline at all.
            //
            // Abandon only when settlement provably never happened. It did, and the ledger already
            // has its outcome on record, when the buffer overflowed successfully (funds moved) or
            // was poisoned by a failed cap-crossing settlement that this handler's own broad catch
            // swallowed before it reached us. The ledger tolerates a mistaken Abandon defensively —
            // it refuses to erase a completed entry — but that is a backstop, not a licence to call
            // it when the correct answer is already known here.
            var buffering = feature.Buffer!;
            var identity = feature.Attempt.Identity;
            if (!buffering.Overflowed && !buffering.Poisoned)
            {
                await ledger.AbandonAsync(identity, context.RequestAborted);
                EndpointThrewAuthorizationAbandoned(
                    logger, exception, identity.Network, identity.Asset, identity.Nonce);
            }
            else
            {
                EndpointThrewAfterSettlement(
                    logger, exception, identity.Network, identity.Asset, identity.Nonce);
            }

            // Discard() and restoring the original response body feature both guarantee nothing was
            // delivered; buffering also guarantees the response never started. Rethrowing is
            // therefore safe, and necessary: swallowing the exception here — as this handler used
            // to — hides it from UseExceptionHandler, the developer exception page, and any logging
            // middleware the host configured, leaving an operator debugging their own endpoint with
            // a bare 500 and no stack trace anywhere in the process.
            buffering.Discard();
            RestoreOriginalBody(context, feature);
            throw;
        }
        finally
        {
            // Always restore, whichever path above ran — leaking a buffering feature onto a later
            // request would be a serious, hard-to-diagnose bug. A no-op for a request that never
            // opened a payment (feature.OriginalBody is then null).
            RestoreOriginalBody(context, feature);
        }

        if (feature.Attempt is null)
        {
            // No accepted payment was opened for this request. Either nothing priced it at all (no
            // route matched and the gate was never called — feature.Refusal is then also null and
            // FinishRefusalAsync is a no-op), or the imperative gate refused, in which case
            // OpenRefusalBuffering already captured whatever the endpoint wrote next.
            await FinishRefusalAsync(context, feature);
            return;
        }

        EnsureLateAttemptIsBuffered(feature);

        // FinishAsync consults Buffer.Poisoned directly, in plain straight-line code reached only
        // when `next` returned without throwing — not only inside the catch above — because an
        // endpoint that wraps its own writes in a broad catch can swallow
        // BufferingSettlementFailedException and return normally. Falling through to SettleAsync
        // there would settle — and charge — a second time for the same authorization.
        await processor.FinishAsync(context, feature.Attempt, feature.Buffer!);
    }

    private static void RestoreOriginalBody(HttpContext context, X402RequestFeature feature)
    {
        if (feature.OriginalBody is { } original)
        {
            context.Features.Set(original);
        }
    }

    private static async Task WriteRefusalAsync(HttpContext context, PaymentAttempt attempt)
    {
        if (attempt.ConflictReason is { } conflict)
        {
            // Checked before Result is dereferenced below: a Conflict attempt leaves Result null.
            // PaymentConflictResult is the same type IX402PaymentGate.RequireAsync hands a caller
            // for this outcome (see PaymentGateResult.Result) — one implementation of what a 409
            // looks like, not two.
            await new PaymentConflictResult(conflict).ExecuteAsync(context);
            return;
        }

        await attempt.Result!.ExecuteAsync(context);
    }

    private static void EnsureLateAttemptIsBuffered(X402RequestFeature feature)
    {
        // Nothing to settle here beyond what FinishAsync already does: this hook exists only to
        // catch a bug — a payment opened without buffering ever being installed, which would
        // silently skip settlement.
        if (feature.Attempt is not null && feature.Buffer is null)
        {
            throw new InvalidOperationException(
                "A payment gate was opened but no buffering was installed. This is a bug in " +
                "X402.AspNetCore; please report it.");
        }
    }

    /// <summary>
    /// Decides what happens to whatever a refused gate call's endpoint wrote, once it returns
    /// without throwing. <see cref="Gate.X402PaymentGate.RequireAsync"/> buffers a refusal exactly
    /// like an accepted attempt (<see cref="X402PaymentProcessor.OpenRefusalBuffering"/>), so — same
    /// guarantee <see cref="X402PaymentProcessor.FinishAsync"/> relies on for a real payment —
    /// nothing the endpoint wrote has reached the real transport yet, whichever branch below is
    /// taken.
    /// </summary>
    /// <remarks>
    /// A well-behaved endpoint returned <c>PaymentGateResult.Result</c> unchanged: its status is
    /// never 2xx, so what it wrote — normally the 402/409 body itself — is simply released. An
    /// endpoint that ignored <c>CanContinue</c> and wrote a success response anyway never gets to
    /// keep it: the buffered bytes are discarded and replaced with the refusal that should have been
    /// returned, and the substitution is logged at <c>LogLevel.Error</c> — an endpoint bug that would
    /// have served paid content for free must never be silent, even though this pipeline caught it.
    /// </remarks>
    private async Task FinishRefusalAsync(HttpContext context, X402RequestFeature feature)
    {
        if (feature.Refusal is not { } refusal || feature.Buffer is not { } buffering)
        {
            // Nothing priced this request through the gate at all: no route matched, and the
            // endpoint never called IX402PaymentGate.RequireAsync either.
            return;
        }

        if (context.Response.StatusCode is >= 200 and < 300)
        {
            RefusalIgnored(
                logger, context.Request.Path.Value ?? "", context.Response.StatusCode,
                refusal.FailureReason ?? "");

            buffering.Discard();
            context.Response.Headers.Clear();
            await WriteRefusalAsync(context, refusal);
            return;
        }

        await buffering.FlushBufferAsync(context.RequestAborted);
    }

    [LoggerMessage(EventId = 4040, Level = LogLevel.Error,
        Message = "x402 payment for {Network}/{Asset}/{Nonce} abandoned: the endpoint threw before " +
                  "settlement. Rethrowing so the host's own error handling sees it.")]
    private static partial void EndpointThrewAuthorizationAbandoned(
        ILogger logger, Exception exception, string network, string asset, string nonce);

    [LoggerMessage(EventId = 4041, Level = LogLevel.Error,
        Message = "x402 payment for {Network}/{Asset}/{Nonce} was already settled or poisoned when " +
                  "the endpoint threw; not abandoned. Rethrowing so the host's own error handling " +
                  "sees it.")]
    private static partial void EndpointThrewAfterSettlement(
        ILogger logger, Exception exception, string network, string asset, string nonce);

    [LoggerMessage(EventId = 4042, Level = LogLevel.Error,
        Message = "x402: {Path} returned {StatusCode} after IX402PaymentGate.RequireAsync refused " +
                  "payment ({Reason}) — the endpoint ignored PaymentGateResult.CanContinue and " +
                  "served paid content for free. Withholding the response where still possible.")]
    private static partial void RefusalIgnored(
        ILogger logger, string path, int statusCode, string reason);
}
