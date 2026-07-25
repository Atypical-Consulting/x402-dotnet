using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.DependencyInjection;
using X402.AspNetCore.Facilitator;
using X402.Assets;
using X402.Client.Signing;
using X402.Json;
using X402.Networks;
using X402.Pricing;
using X402.Protocol;
using X402.TestKit;

/// <summary>
/// Answers a question the x402 protocol itself cannot: does this facilitator actually settle a
/// given asset? <c>GET /supported</c> only ever lists scheme-and-network pairs — see ADR 0002 —
/// so the only way to know is to attempt a real settlement and see what comes back. This probe
/// does exactly that, for the smallest amount the asset can represent: one atomic unit.
/// </summary>
internal static class Probe
{
    public static async Task RunAsync(string privateKey, bool useFake)
    {
        var network = KnownNetworks.BaseSepolia;
        var facilitatorUrl = useFake
            ? new Uri("http://localhost/") // never dialled: the fake below replaces the transport.
            : new Uri(Environment.GetEnvironmentVariable("X402_FACILITATOR_URL") ?? "https://x402.org/facilitator");

        var signer = new PrivateKeyPaymentSigner(privateKey);

        var services = new ServiceCollection();

        // AddX402 builds an IFacilitatorClient from nothing more than a facilitator URL — the
        // rest of X402Options (PayTo, Assets, ...) exists only because start-up validation
        // requires a complete, well-formed configuration. This probe pays itself, so PayTo is its
        // own address: no separate merchant account is needed to find out whether an asset settles.
        services.AddX402(options =>
        {
            options.PayTo = signer.Address;
            options.Network = network;
            options.FacilitatorUrl = facilitatorUrl;
            options.Assets.Add(new AssetConfiguration { Symbol = "EURC" });
            options.Assets.Add(new AssetConfiguration { Symbol = "USDC" });
        });

        FakeFacilitator? fake = null;
        if (useFake)
        {
            fake = new FakeFacilitator();
            services.AddHttpClient("x402-verify").ConfigurePrimaryHttpMessageHandler(fake.CreateHandler);
            services.AddHttpClient("x402-settle").ConfigurePrimaryHttpMessageHandler(fake.CreateHandler);
        }

        await using var provider = services.BuildServiceProvider();
        var facilitator = provider.GetRequiredService<IFacilitatorClient>();

        Console.WriteLine(useFake
            ? $"Probing the in-process fake facilitator on {network} (--fake: always settles; " +
              "proves the mechanics, not real asset support)"
            : $"Probing {facilitatorUrl} on {network}");
        Console.WriteLine();

        var supported = await facilitator.GetSupportedAsync();
        Console.WriteLine("/supported advertises (scheme × network pairs only — never assets):");
        foreach (var kind in supported.Kinds)
        {
            Console.WriteLine($"  {kind.Scheme}  {kind.Network}");
        }

        Console.WriteLine();
        Console.WriteLine("Nothing above says which assets settle. Trying each configured asset for");
        Console.WriteLine("real, one atomic unit at a time:");
        Console.WriteLine();

        var settled = new List<string>();
        foreach (var asset in KnownAssets.ForNetwork(network))
        {
            var (outcome, detail) = await ProbeAssetAsync(facilitator, signer, asset);
            var displayAmount = 1m / (decimal)Math.Pow(10, asset.Decimals);
            Console.WriteLine(
                $"  {asset.Symbol,-5} {displayAmount.ToString("0." + new string('0', asset.Decimals), CultureInfo.InvariantCulture)}  ->  {outcome,-8} {detail}");

            if (outcome == "settled")
            {
                settled.Add(asset.Symbol);
            }
        }

        Console.WriteLine();
        Console.WriteLine(settled.Count > 0
            ? $"This facilitator settles {string.Join(" and ", settled)}. Nothing in the x402 protocol advertises which"
            : "This facilitator settled none of the configured assets. Nothing in the x402 protocol advertises which");
        Console.WriteLine("assets a facilitator handles, so this probe is the only way to find out — see");
        Console.WriteLine("docs/adr/0002 and the README.");

        if (fake is not null)
        {
            await fake.DisposeAsync();
        }
    }

    /// <summary>
    /// Signs and attempts to settle a real authorization for one atomic unit of
    /// <paramref name="asset"/>, paid from <paramref name="signer"/> to itself. Verify runs first,
    /// exactly as a real payment pipeline would: a rejection there (no funds, unsupported asset)
    /// is reported without ever attempting the settle call that follows it.
    /// </summary>
    private static async Task<(string Outcome, string Detail)> ProbeAssetAsync(
        IFacilitatorClient facilitator, PrivateKeyPaymentSigner signer, AssetDescriptor asset)
    {
        var requirements = OneAtomicUnitRequirement(asset, payTo: signer.Address);
        var now = DateTimeOffset.UtcNow;
        var authorization = new Eip3009Authorization
        {
            From = signer.Address,
            To = requirements.PayTo,
            Value = requirements.Amount,
            ValidAfter = now.AddSeconds(-60).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ValidBefore = now.AddSeconds(requirements.MaxTimeoutSeconds)
                .ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            Nonce = "0x" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
        };

        var exact = await signer.SignAsync(requirements, authorization, asset);
        var payload = new PaymentPayload
        {
            Resource = new ResourceInfo { Url = "urn:x402-dotnet:probe" },
            Accepted = requirements,
            Payload = JsonSerializer.SerializeToElement(exact, X402Json.Options),
        };

        try
        {
            var verified = await facilitator.VerifyAsync(payload, requirements);
            if (!verified.IsValid)
            {
                return ("refused", verified.InvalidReason ?? "unknown");
            }

            var settled = await facilitator.SettleAsync(payload, requirements);
            return settled.Success
                ? ("settled", settled.Transaction)
                : ("refused", settled.ErrorReason ?? "unknown");
        }
        catch (FacilitatorException exception)
        {
            return ("error", exception.Message);
        }
    }

    private static PaymentRequirements OneAtomicUnitRequirement(AssetDescriptor asset, string payTo) => new()
    {
        Scheme = "exact",
        Network = asset.Network,
        Amount = Price.Atomic(asset, "1").AtomicAmount,
        Asset = asset.Address,
        PayTo = payTo,
        MaxTimeoutSeconds = 60,
        Extra = JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { ["name"] = asset.Eip712Name, ["version"] = asset.Eip712Version },
            X402Json.Options),
    };
}
