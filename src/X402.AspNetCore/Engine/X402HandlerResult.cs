using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace X402.AspNetCore.Engine;

/// <summary>
/// A result the payment pipeline can hand back to the caller — from a route refusal, from the
/// imperative gate, or from the middleware's outbound half — that works from a minimal endpoint and
/// from an MVC controller without the caller having to know which world it is in.
/// </summary>
/// <remarks>
/// Implements <see cref="IActionResult.ExecuteResultAsync"/> once, in terms of
/// <see cref="ExecuteAsync"/>, so a new result type only ever has one method to write.
/// <see cref="PaymentRequiredResult"/> (402) and <see cref="PaymentConflictResult"/> (409) are the
/// two implementations.
/// </remarks>
public abstract class X402HandlerResult : IResult, IActionResult
{
    /// <inheritdoc cref="IResult.ExecuteAsync" />
    public abstract Task ExecuteAsync(HttpContext httpContext);

    /// <inheritdoc />
    Task IActionResult.ExecuteResultAsync(ActionContext context) => ExecuteAsync(context.HttpContext);
}
