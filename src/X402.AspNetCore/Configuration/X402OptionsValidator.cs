using Microsoft.Extensions.Options;
using X402.Assets;
using X402.Networks;

namespace X402.AspNetCore.Configuration;

/// <summary>Fails the host at start-up rather than the first payment.</summary>
internal sealed class X402OptionsValidator : IValidateOptions<X402Options>
{
    public ValidateOptionsResult Validate(string? name, X402Options options)
    {
        var failures = new List<string>();

        ValidatePayee(options, failures);
        ValidateNetwork(options, failures);
        ValidateAssets(options, failures);
        ValidateFacilitator(options, failures);

        if (options.MaxTimeoutSeconds is < 1 or > 3600)
        {
            failures.Add(
                $"X402:MaxTimeoutSeconds is {options.MaxTimeoutSeconds}; it must be between 1 and 3600.");
        }

        if (options.MaxBufferedResponseBytes < 0)
        {
            failures.Add(
                $"X402:MaxBufferedResponseBytes is {options.MaxBufferedResponseBytes}; it cannot be negative.");
        }

        if (options.ServiceName is { Length: > 32 })
        {
            failures.Add("X402:ServiceName is longer than the 32 characters the specification allows.");
        }

        if (options.ServiceName is { } serviceName && !IsPrintableAscii(serviceName))
        {
            failures.Add(
                $"X402:ServiceName is '{serviceName}', which is not printable ASCII (0x20-0x7E) as " +
                "the specification requires. Remove the non-ASCII or control characters.");
        }

        if (options.IconUrl is { } iconUrl && !IsAbsoluteHttpUrl(iconUrl))
        {
            failures.Add(
                $"X402:IconUrl is '{iconUrl}', which is not an absolute http(s) URL as the " +
                "specification requires. Use a full URL such as 'https://example.com/icon.png'.");
        }

        if (options.Tags.Count > 5)
        {
            failures.Add("X402:Tags holds more than the 5 entries the specification allows.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsPrintableAscii(string value) =>
        value.All(c => c is >= (char)0x20 and <= (char)0x7E);

    /// <summary>
    /// True only for a well-formed, absolute http or https URL. Plain <c>Uri.TryCreate(value,
    /// UriKind.Absolute, out _)</c> is not enough: on this platform it also accepts a bare
    /// filesystem-style path such as <c>/icon.png</c>, silently treating it as a <c>file://</c>
    /// URI — exactly the kind of relative-looking mistake this check exists to catch.
    /// </summary>
    private static bool IsAbsoluteHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static void ValidatePayee(X402Options options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.PayTo))
        {
            failures.Add("X402:PayTo is required — without a payee address there is nowhere to pay.");
            return;
        }

        if (!EvmAddress.IsValid(options.PayTo))
        {
            failures.Add(
                $"X402:PayTo is '{options.PayTo}', which is not a 20-byte hex address prefixed with 0x.");
            return;
        }

        if (!EvmAddress.IsChecksumValid(options.PayTo))
        {
            failures.Add(
                $"X402:PayTo is '{options.PayTo}', whose EIP-55 checksum does not match. " +
                $"Did you mean '{EvmAddress.ToChecksum(options.PayTo)}'? " +
                "A mistyped payee address sends the money nowhere recoverable.");
        }
    }

    private static void ValidateNetwork(X402Options options, List<string> failures)
    {
        if (!Caip2Network.TryParse(options.Network, out _))
        {
            failures.Add(
                $"X402:Network is '{options.Network}', which is not a CAIP-2 identifier. " +
                $"Use '{KnownNetworks.BaseSepolia}' or '{KnownNetworks.BaseMainnet}'. " +
                "x402 v2 does not accept the v1 short names such as 'base-sepolia'.");
        }
    }

    private static void ValidateAssets(X402Options options, List<string> failures)
    {
        if (options.Assets.Count == 0)
        {
            failures.Add(
                "X402:Assets is empty — declare at least one accepted asset, for example " +
                "[{ \"Symbol\": \"EURC\" }].");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in options.Assets)
        {
            var resolvedAddress = ResolveForValidation(asset, options.Network, failures);
            if (resolvedAddress is null)
            {
                continue;
            }

            if (!seen.Add(resolvedAddress))
            {
                failures.Add(
                    $"X402:Assets declares {resolvedAddress} more than once. " +
                    "Each accepted asset appears exactly once.");
            }
        }
    }

    private static string? ResolveForValidation(
        AssetConfiguration asset, string network, List<string> failures)
    {
        if (asset.Address is null)
        {
            if (string.IsNullOrWhiteSpace(asset.Symbol))
            {
                failures.Add(
                    "X402:Assets holds an entry with neither Symbol nor Address. " +
                    "Declare a catalogued symbol, or describe the token in full.");
                return null;
            }

            if (!KnownAssets.TryGet(network, asset.Symbol, out var catalogued))
            {
                var available = KnownAssets.ForNetwork(network).Select(a => a.Symbol).ToArray();
                failures.Add(
                    $"X402:Assets declares symbol '{asset.Symbol}', which is not catalogued for " +
                    $"network '{network}'. Available: {(available.Length == 0
                        ? "none — describe the token in full"
                        : string.Join(", ", available))}.");
                return null;
            }

            return catalogued.Address;
        }

        if (!EvmAddress.IsValid(asset.Address))
        {
            failures.Add($"X402:Assets declares address '{asset.Address}', which is not a valid EVM address.");
            return null;
        }

        var missing = new List<string>();
        if (asset.Decimals is null)
        {
            missing.Add("Decimals");
        }

        if (string.IsNullOrWhiteSpace(asset.Eip712Name))
        {
            missing.Add("Eip712Name");
        }

        if (string.IsNullOrWhiteSpace(asset.Eip712Version))
        {
            missing.Add("Eip712Version");
        }

        if (missing.Count > 0)
        {
            failures.Add(
                $"X402:Assets describes {asset.Address} but omits {string.Join(", ", missing)}. " +
                "A token described by address needs its decimals and its EIP-712 domain — read " +
                "them from the contract's decimals(), name() and version() functions. A wrong " +
                "domain produces signatures that recover to the wrong address.");
            return null;
        }

        return asset.Address;
    }

    private static void ValidateFacilitator(X402Options options, List<string> failures)
    {
        if (options.FacilitatorUrl is null)
        {
            failures.Add(
                "X402:FacilitatorUrl is required — verification and settlement are delegated to it.");
            return;
        }

        if (!options.FacilitatorUrl.IsAbsoluteUri)
        {
            failures.Add($"X402:FacilitatorUrl is '{options.FacilitatorUrl}', which is not absolute.");
            return;
        }

        var isLoopback = options.FacilitatorUrl.IsLoopback;
        if (options.FacilitatorUrl.Scheme != Uri.UriSchemeHttps && !isLoopback)
        {
            failures.Add(
                $"X402:FacilitatorUrl is '{options.FacilitatorUrl}'; it must use https. " +
                "Plain http is tolerated on loopback only, so a fake facilitator can be used in tests.");
        }
    }
}
