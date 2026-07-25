namespace X402.Licensing;

/// <summary>
/// Decides whether an optional feature is available. The default implementation allows
/// everything; a commercial package may substitute its own without any change to this library.
/// </summary>
/// <remarks>
/// <para>
/// Implementations shipped here perform no network call, read no licence file and emit no
/// telemetry. A test asserts that this assembly references no networking type at all.
/// </para>
/// <para>
/// <b>No code in this library currently calls <see cref="IsEnabled"/>.</b> It is registered in DI
/// (as <see cref="AllowAllFeatureGate"/> by default) and every feature this library ships,
/// including <see cref="X402Features.DynamicPricing"/>, runs ungated regardless of what a
/// substituted implementation returns. This interface exists purely as an extension point for a
/// future commercial layer to build on — wiring it into the pricing or billing paths themselves is
/// a deliberate, separate decision, not an oversight, and has not been made yet.
/// </para>
/// </remarks>
public interface IFeatureGate
{
    /// <summary>Whether the named feature is available.</summary>
    /// <param name="feature">A feature key, see <see cref="X402Features"/>.</param>
    bool IsEnabled(string feature);
}

/// <summary>The default gate: every feature is available.</summary>
public sealed class AllowAllFeatureGate : IFeatureGate
{
    /// <inheritdoc />
    public bool IsEnabled(string feature) => true;
}

/// <summary>Feature keys understood by this library.</summary>
public static class X402Features
{
    /// <summary>Pricing computed per request rather than per route.</summary>
    public const string DynamicPricing = "x402.dynamic-pricing";

    /// <summary>Persisted billing ledger beyond the default logger sink.</summary>
    public const string BillingLedger = "x402.billing-ledger";

    /// <summary>Free-call quotas backed by a persistent store.</summary>
    public const string PersistedQuota = "x402.persisted-quota";
}
