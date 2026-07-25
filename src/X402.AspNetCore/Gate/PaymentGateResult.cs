using X402.AspNetCore.Engine;
using X402.Assets;

namespace X402.AspNetCore.Gate;

/// <summary>What <see cref="IX402PaymentGate.RequireAsync"/> decided.</summary>
public sealed class PaymentGateResult
{
    internal PaymentGateResult(PaymentAttempt attempt)
    {
        CanContinue = attempt.CanContinue;
        Result = attempt.Result;
        SettledAsset = attempt.SettledAsset;
        Payer = attempt.Payer;
        FailureReason = attempt.FailureReason;
    }

    /// <summary>Whether the handler may proceed.</summary>
    public bool CanContinue { get; }

    /// <summary>
    /// The 402 to return when the handler may not proceed. Implements both <c>IResult</c> and
    /// <c>IActionResult</c>, so the same object works from a minimal endpoint and from an MVC
    /// controller. Null when the request was refused because another request is settling the same
    /// authorization right now — the caller should answer 409 instead, using
    /// <see cref="FailureReason"/>.
    /// </summary>
    public PaymentRequiredResult? Result { get; }

    /// <summary>The asset the payer chose, once payment is accepted.</summary>
    public AssetDescriptor? SettledAsset { get; }

    /// <summary>The payer's address, once payment is accepted.</summary>
    public string? Payer { get; }

    /// <summary>Why payment was refused, when it was.</summary>
    public string? FailureReason { get; }
}
