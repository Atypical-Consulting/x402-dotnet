using X402.Assets;

namespace X402.Client.Spending;

/// <summary>
/// Enforces per-asset spending limits before a payment is signed, and tracks how much of each
/// asset's session budget has already been committed.
/// </summary>
/// <remarks>
/// Limits and running totals are held per asset and never aggregated: spending euros never draws
/// down the dollar budget, and vice versa. An asset with no declared limit is never payable.
/// </remarks>
public interface ISpendTracker
{
    /// <summary>
    /// Throws if paying <paramref name="amount"/> of <paramref name="asset"/> would exceed its
    /// per-request limit, or would bring the asset's session total past its per-session limit.
    /// Does not record the amount; call <see cref="Record"/> once the payment is actually
    /// committed, or call <see cref="EnsureWithinLimitsAndRecord"/> to do both atomically.
    /// </summary>
    /// <exception cref="SpendingLimitExceededException">
    /// <paramref name="asset"/> has no declared limit, or <paramref name="amount"/> breaches one.
    /// </exception>
    void EnsureWithinLimits(AssetDescriptor asset, decimal amount);

    /// <summary>
    /// Adds <paramref name="amount"/> to the session total already spent on <paramref name="asset"/>,
    /// without checking it against any limit.
    /// </summary>
    void Record(AssetDescriptor asset, decimal amount);

    /// <summary>
    /// Atomically performs <see cref="EnsureWithinLimits"/> followed by <see cref="Record"/>, so
    /// that two concurrent callers can never both pass a check that only one of them can actually
    /// afford. This is the member the paying handler calls.
    /// </summary>
    /// <exception cref="SpendingLimitExceededException">
    /// <paramref name="asset"/> has no declared limit, or <paramref name="amount"/> breaches one.
    /// Nothing is recorded when this is thrown.
    /// </exception>
    void EnsureWithinLimitsAndRecord(AssetDescriptor asset, decimal amount);

    /// <summary>
    /// Returns a previously recorded <paramref name="amount"/> to the session budget for
    /// <paramref name="asset"/> — for example when a payment is refused after the amount was
    /// already reserved with <see cref="EnsureWithinLimitsAndRecord"/>. Without this, a rejected
    /// payment would permanently consume session budget.
    /// </summary>
    void Release(AssetDescriptor asset, decimal amount);
}
