using Microsoft.Extensions.Options;
using X402.Networks;

namespace X402.Client;

/// <summary>
/// Fails the host at start-up rather than the first paying request — the client-side counterpart
/// of <c>X402.AspNetCore.Configuration.X402OptionsValidator</c> on the server.
/// </summary>
/// <remarks>
/// Without this, three misconfigurations all surfaced only once a payment was actually attempted:
/// a v1 network shorthand in <see cref="X402ClientOptions.AllowedNetworks"/> silently filtered
/// every offered requirement out, leaving the agent to report that nothing was acceptable; no
/// <see cref="X402ClientOptions.SetLimits(X402.Assets.AssetDescriptor, decimal, decimal)"/> call at all
/// is a valid configuration, so every payment was then refused for want of a declared limit; and a
/// per-session limit set below its per-request counterpart made every payment above the per-session
/// figure impossible even though the per-request figure said it should be allowed.
/// </remarks>
internal sealed class X402ClientOptionsValidator : IValidateOptions<X402ClientOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, X402ClientOptions options)
    {
        var failures = new List<string>();

        ValidateAllowedNetworks(options, failures);
        ValidateLimits(options, failures);

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

            // Non-negativity is not re-checked here: both SetLimits overloads are the only way a
            // value ever reaches this dictionary, and both already call
            // ArgumentOutOfRangeException.ThrowIfNegative on PerRequest and PerSession before
            // storing anything — a negative entry cannot exist to find. Re-asserting it here would
            // be an unreachable branch no test could honestly exercise through the public API,
            // which is exactly the kind of promise this project does not want to make silently.
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
