using Microsoft.AspNetCore.Http;

namespace X402.AspNetCore.Engine;

/// <summary>
/// A 409 returned when another request is already settling the same authorization. Exists so a
/// refusal always has a non-null result to hand back — the imperative gate's
/// <c>PaymentGateResult.Result</c> is a <see cref="PaymentConflictResult"/> rather than null on this
/// outcome, and <see cref="Middleware.X402Middleware"/> writes the same 409 through this type for a
/// route match, so there is one implementation of what a 409 looks like.
/// </summary>
public sealed class PaymentConflictResult : X402HandlerResult
{
    internal PaymentConflictResult(string reason)
    {
        Reason = reason;
    }

    /// <summary>Why the request could not proceed. Written verbatim as the response body.</summary>
    public string Reason { get; }

    /// <inheritdoc />
    public override async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        await httpContext.Response.WriteAsync(Reason, httpContext.RequestAborted);
    }
}
