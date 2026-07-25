using X402.AspNetCore.Engine;
using X402.Assets;

namespace X402.AspNetCore.Gate;

/// <summary>What <see cref="IX402PaymentGate.RequireAsync"/> decided.</summary>
public sealed class PaymentGateResult
{
    internal PaymentGateResult(PaymentAttempt attempt)
    {
        CanContinue = attempt.CanContinue;
        Result = BuildResult(attempt);
        SettledAsset = attempt.SettledAsset;
        Payer = attempt.Payer;
        FailureReason = attempt.FailureReason;
        ConflictReason = attempt.ConflictReason;
    }

    /// <summary>Whether the handler may proceed.</summary>
    public bool CanContinue { get; }

    /// <summary>
    /// The response to return when the handler may not proceed. Non-null exactly when
    /// <see cref="CanContinue"/> is false: a <see cref="PaymentRequiredResult"/> (402) for an
    /// ordinary refusal, or a <see cref="PaymentConflictResult"/> (409) when another request is
    /// settling the same authorization right now. Both implement <c>IResult</c> and
    /// <c>IActionResult</c>, so <c>return result.Result;</c> is always correct from a minimal
    /// endpoint or an MVC controller — a conflict is never a special case the caller has to
    /// remember.
    /// </summary>
    /// <remarks>
    /// A caller that ignores this — proceeds to write a response instead of returning
    /// <see cref="Result"/> when <see cref="CanContinue"/> is false — has a bug. It is not a
    /// profitable one: the response is buffered the same way a real payment's is, so the pipeline
    /// discards whatever was written and delivers this refusal in its place once the handler
    /// returns, logging the substitution at <c>LogLevel.Error</c>. Do not rely on this backstop —
    /// check <see cref="CanContinue"/> before writing anything.
    /// </remarks>
    public X402HandlerResult? Result { get; }

    /// <summary>The asset the payer chose, once payment is accepted.</summary>
    public AssetDescriptor? SettledAsset { get; }

    /// <summary>The payer's address, once payment is accepted.</summary>
    public string? Payer { get; }

    /// <summary>Why payment was refused, when it was.</summary>
    public string? FailureReason { get; }

    /// <summary>
    /// Set when the request was refused because another request is settling the same authorization
    /// right now — the same text <see cref="Result"/> already carries as a
    /// <see cref="PaymentConflictResult"/>. Exposed separately so a caller can log it or vary its
    /// own behaviour without inspecting the result object.
    /// </summary>
    public string? ConflictReason { get; }

    private static X402HandlerResult? BuildResult(PaymentAttempt attempt)
    {
        if (attempt.CanContinue)
        {
            return null;
        }

        // ConflictReason is checked before Result is dereferenced: a Conflict attempt leaves
        // Result null, exactly as X402Middleware.WriteRefusalAsync already relies on.
        return attempt.ConflictReason is { } conflict
            ? new PaymentConflictResult(conflict)
            : attempt.Result!;
    }
}
