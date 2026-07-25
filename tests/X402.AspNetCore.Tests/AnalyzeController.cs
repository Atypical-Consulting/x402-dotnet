using Microsoft.AspNetCore.Mvc;
using X402.AspNetCore.Gate;

namespace X402.AspNetCore.Tests;

/// <summary>
/// The MVC twin of <c>PaidServerFixture</c>'s minimal <c>/analyze</c> endpoint — same
/// <see cref="IX402PaymentGate"/>, same pricing, same body — mounted at <c>/mvc/analyze</c> so
/// <c>PaymentGateTests.The_same_result_object_works_from_a_controller_and_a_minimal_endpoint</c>
/// genuinely exercises the MVC result pipeline (<see cref="IActionResult"/> via
/// <c>ExecuteResultAsync</c>) rather than a minimal endpoint wearing a controller's clothes.
/// </summary>
[ApiController]
[Route("mvc")]
public sealed class AnalyzeController(IX402PaymentGate gate) : ControllerBase
{
    /// <summary>Prices and settles the same way as POST /analyze, through the MVC action pipeline.</summary>
    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze(
        [FromBody] AnalyzeRequest request, CancellationToken cancellationToken)
    {
        var result = await gate.RequireAsync(
            DynamicPricing.ForTokens(request.Tokens), cancellationToken: cancellationToken);

        if (!result.CanContinue)
        {
            return result.Result!;
        }

        return Ok($"analyzed {request.Tokens} tokens, settled in {result.SettledAsset?.Symbol}");
    }
}
