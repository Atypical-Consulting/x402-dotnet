using System.Security.Cryptography;
using X402.Assets;
using X402.Client.Signing;
using X402.Json;
using X402.Networks;
using X402.Protocol;

namespace X402.TestKit;

/// <summary>Builders for valid protocol objects, so tests state only what they are about.</summary>
public static class TestData
{
    /// <summary>A throw-away private key. Holds nothing, on any network, ever.</summary>
    public const string PayerPrivateKey =
        "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";

    /// <summary>Address derived from <see cref="PayerPrivateKey"/>.</summary>
    public static string PayerAddress { get; } =
        new Nethereum.Signer.EthECKey(PayerPrivateKey).GetPublicAddress();

    /// <summary>A payee address used across the suite.</summary>
    public const string PayeeAddress = "0x209693Bc6afc0C5328bA36FaF03C514EF312287C";

    /// <summary>Requirements for 0.01 EURC on Base Sepolia.</summary>
    public static PaymentRequirements EurcSepoliaRequirements(string amount = "10000") =>
        RequirementsFor(KnownAssets.EurcBaseSepolia, amount);

    /// <summary>Requirements for a given asset and atomic amount.</summary>
    public static PaymentRequirements RequirementsFor(AssetDescriptor asset, string amount) => new()
    {
        Scheme = "exact",
        Network = asset.Network,
        Amount = amount,
        Asset = asset.Address,
        PayTo = PayeeAddress,
        MaxTimeoutSeconds = 60,
        // Dictionary<string, string> is registered on X402JsonContext (see X402Json.cs) precisely
        // for this ad-hoc `extra` shape, so this serializes through the same options as every other
        // protocol object.
        Extra = System.Text.Json.JsonSerializer.SerializeToElement(
            new Dictionary<string, string>
            {
                ["name"] = asset.Eip712Name,
                ["version"] = asset.Eip712Version,
            }, X402Json.Options),
    };

    /// <summary>Builds a facilitator request carrying a genuinely signed authorization.</summary>
    public static async Task<FacilitatorRequest> SignedRequestAsync(
        PaymentRequirements requirements,
        DateTimeOffset? validAfter = null,
        DateTimeOffset? validBefore = null)
    {
        var payload = await SignedPayloadAsync(requirements, validAfter, validBefore);
        return new FacilitatorRequest
        {
            PaymentPayload = payload,
            PaymentRequirements = requirements,
        };
    }

    /// <summary>Builds a payment payload carrying a genuinely signed authorization.</summary>
    public static async Task<PaymentPayload> SignedPayloadAsync(
        PaymentRequirements requirements,
        DateTimeOffset? validAfter = null,
        DateTimeOffset? validBefore = null)
    {
        var asset = KnownAssets.ForNetwork(requirements.Network)
            .Single(a => string.Equals(a.Address, requirements.Asset,
                StringComparison.OrdinalIgnoreCase));

        var now = DateTimeOffset.UtcNow;
        var authorization = new Eip3009Authorization
        {
            From = PayerAddress,
            To = requirements.PayTo,
            Value = requirements.Amount,
            ValidAfter = (validAfter ?? now.AddSeconds(-60)).ToUnixTimeSeconds().ToString(),
            ValidBefore = (validBefore ?? now.AddSeconds(requirements.MaxTimeoutSeconds))
                .ToUnixTimeSeconds().ToString(),
            Nonce = "0x" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
        };

        var signer = new PrivateKeyPaymentSigner(PayerPrivateKey);
        var exact = await signer.SignAsync(requirements, authorization, asset);

        return new PaymentPayload
        {
            Resource = new ResourceInfo { Url = "https://api.example.com/premium" },
            Accepted = requirements,
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(exact, X402Json.Options),
        };
    }

    /// <summary>Flips one byte of a hex signature, so it recovers to a different address.</summary>
    public static string FlipOneByte(string hexSignature)
    {
        var chars = hexSignature.ToCharArray();
        // Position 10 : dans le corps de la signature, jamais dans le préfixe 0x ni l'octet v.
        chars[10] = chars[10] == 'a' ? 'b' : 'a';
        return new string(chars);
    }
}
