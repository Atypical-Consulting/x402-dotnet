using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using X402.Protocol;
using X402.Transport;

namespace X402.AspNetCore.Engine;

/// <summary>
/// A 402 response carrying a payment demand. Implements both result abstractions, so the same
/// object is returned from a minimal endpoint and from an MVC controller without the caller
/// having to know which world it is in.
/// </summary>
public sealed class PaymentRequiredResult : IResult, IActionResult
{
    private readonly SettleResponse? settlement;

    internal PaymentRequiredResult(PaymentRequired demand, SettleResponse? settlement = null)
    {
        Demand = demand;
        this.settlement = settlement;
    }

    /// <summary>The demand this response carries.</summary>
    public PaymentRequired Demand { get; }

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext) => WriteAsync(httpContext);

    /// <inheritdoc />
    public Task ExecuteResultAsync(ActionContext context) => WriteAsync(context.HttpContext);

    private async Task WriteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        httpContext.Response.Headers[X402Headers.PaymentRequired] = X402Codec.Encode(Demand);

        if (settlement is not null)
        {
            httpContext.Response.Headers[X402Headers.PaymentResponse] = X402Codec.Encode(settlement);
        }

        httpContext.Response.ContentType = "application/json";
        // The v2 HTTP spec is explicit: all protocol information lives in the headers.
        await httpContext.Response.WriteAsync("{}");
    }
}
