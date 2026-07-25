using Microsoft.Extensions.Options;
using X402.Networks;

namespace X402.Client;

/// <summary>
/// Fails the host at start-up rather than the first paying request — the client-side counterpart
/// of <c>X402.AspNetCore.Configuration.X402OptionsValidator</c> on the server.
/// </summary>
/// <remarks>
/// Without this, misconfigurations all surfaced only once a payment was actually attempted:
/// a v1 network shorthand in <see cref="X402ClientOptions.AllowedNetworks"/> silently filtered
/// every offered requirement out, leaving the agent to report that nothing was acceptable; no
/// <see cref="X402ClientOptions.SetLimits(X402.Assets.AssetDescriptor, decimal, decimal)"/> call at all
/// is a valid configuration, so every payment was then refused for want of a declared limit; a
/// per-session limit set below its per-request counterpart made every payment above the per-session
/// figure impossible even though the per-request figure said it should be allowed; a zero limit
/// refused every payment for that asset exactly as an undeclared one does; and a negative
/// <see cref="X402ClientOptions.MaxBufferedRequestBytes"/> failed inside the paying handler, on the
/// first request that actually carried a body, with a message naming a raw byte count and nothing
/// about which setting caused it.
/// </remarks>
internal sealed class X402ClientOptionsValidator : IValidateOptions<X402ClientOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, X402ClientOptions options)
    {
        var failures = new List<string>();

        ValidateAllowedNetworks(options, failures);
        ValidateLimits(options, failures);

        if (options.MaxBufferedRequestBytes < 0)
        {
            failures.Add(
                $"X402ClientOptions.MaxBufferedRequestBytes is {options.MaxBufferedRequestBytes}; " +
                "it cannot be negative. A request carrying a body would fail inside " +
                "X402PaymentHandler the first time it tried to buffer one for replay.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateAllowedNetworks(X402ClientOptions options, List<string> failures)
    {
        foreach (var network in options.AllowedNetworks)
        {
            if (!Caip2Network.TryParse(network, out _))
            {
                failures.Add(
                    $"X402ClientOptions.AllowedNetworks contains '{network}', which is not a CAIP-2 " +
                    $"identifier. Use e.g. '{KnownNetworks.BaseSepolia}'. x402 v2 does not accept the " +
                    "v1 short names such as 'base-sepolia' — an entry like that silently filters " +
                    "every offered requirement out, and the agent reports that nothing was acceptable.");
            }
        }
    }

    private static void ValidateLimits(X402ClientOptions options, List<string> failures)
    {
        if (options.Limits.Count == 0)
        {
            failures.Add(
                "X402ClientOptions declares no spending limits at all. An asset with no declared " +
                "limit is never paid, so every payment would be refused. Call SetLimits for at " +
                "least one asset this agent is willing to pay with.");
            return;
        }

        foreach (var (key, limit) in options.Limits)
        {
            var (network, asset) = SplitKey(key);

            // Non-negativity on its own is not re-checked here: both SetLimits overloads are the
            // only way a value ever reaches this dictionary, and both already call
            // ArgumentOutOfRangeException.ThrowIfNegative on PerRequest and PerSession before
            // storing anything — a negative entry cannot exist to find. Re-asserting exactly that
            // here would be an unreachable branch no test could honestly exercise through the
            // public API, which is exactly the kind of promise this project does not want to make
            // silently.
            //
            // Positivity is a different question, and genuinely reachable: ThrowIfNegative admits
            // zero. SetLimits(asset, 0m, 0m) stores validly, and InMemorySpendTracker.Check then
            // refuses every non-zero payment for that asset ("amount > limits.PerRequest" holds for
            // any priced request against a zero limit) — the identical "every payment refused"
            // outcome the empty-dictionary check above exists to catch, just scoped to one asset
            // instead of the whole configuration. The <= 0 test below also happens to cover a
            // negative value, should one ever reach this dictionary by some future path that
            // forgets SetLimits's own guard — without needing a second, separately-unreachable
            // branch to say so.
            if (limit.PerRequest <= 0m || limit.PerSession <= 0m)
            {
                failures.Add(
                    $"X402ClientOptions declares a non-positive limit for asset '{asset}' on " +
                    $"network '{network}' (PerRequest={limit.PerRequest}, PerSession=" +
                    $"{limit.PerSession}). A zero limit refuses every payment for this asset just " +
                    "as surely as no limit at all — both PerRequest and PerSession must be " +
                    "greater than zero.");
                continue;
            }

            if (limit.PerSession < limit.PerRequest)
            {
                failures.Add(
                    $"X402ClientOptions declares PerSession ({limit.PerSession}) below PerRequest " +
                    $"({limit.PerRequest}) for asset '{asset}' on network '{network}'. A single " +
                    "request within the per-request limit would still be refused by the tighter " +
                    "per-session one. Raise PerSession to at least PerRequest, or lower PerRequest.");
            }
        }
    }

    /// <summary>Splits an <c>AssetIdentity.Key</c> ("{Network}|{Address}") back into its two parts for a message.</summary>
    private static (string Network, string Asset) SplitKey(string key)
    {
        var separator = key.IndexOf('|');
        return separator < 0 ? (key, "") : (key[..separator], key[(separator + 1)..]);
    }
}
