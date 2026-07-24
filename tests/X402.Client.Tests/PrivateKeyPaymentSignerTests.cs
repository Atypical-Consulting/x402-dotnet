using Nethereum.Signer;
using Nethereum.Signer.EIP712;
using Nethereum.Util;
using X402.Assets;
using X402.Client.Signing;
using X402.Protocol;

namespace X402.Client.Tests;

public sealed class PrivateKeyPaymentSignerTests
{
    private const string PrivateKey =
        "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";

    [Fact]
    public void Address_matches_the_key()
    {
        var signer = new PrivateKeyPaymentSigner(PrivateKey);

        signer.Address.ShouldBe(new EthECKey(PrivateKey).GetPublicAddress());
    }

    [Fact]
    public async Task A_signature_recovers_to_the_signer_address()
    {
        var asset = KnownAssets.EurcBaseSepolia;
        var requirements = new PaymentRequirements
        {
            Scheme = "exact", Network = asset.Network, Amount = "10000",
            Asset = asset.Address, PayTo = "0x209693Bc6afc0C5328bA36FaF03C514EF312287C",
            MaxTimeoutSeconds = 60,
        };
        var signer = new PrivateKeyPaymentSigner(PrivateKey);
        var authorization = new Eip3009Authorization
        {
            From = signer.Address, To = requirements.PayTo, Value = requirements.Amount,
            ValidAfter = "1740672089", ValidBefore = "1740672154",
            Nonce = "0xf3746613c2d920b5fdabc0856f2aeb2d4f88ee6037b8cc5d04a71a4462f13480",
        };

        var payload = await signer.SignAsync(requirements, authorization, asset,
            TestContext.Current.CancellationToken);

        payload.Signature.ShouldStartWith("0x");
        payload.Signature.Length.ShouldBe(132);   // 0x + 65 octets
        var recovered = new Eip712TypedDataSigner().RecoverFromSignatureV4(
            Eip3009TypedData.Build(requirements, authorization, asset), payload.Signature);
        recovered.IsTheSameAddress(signer.Address).ShouldBeTrue();
    }

    [Fact]
    public async Task Signing_the_same_authorization_twice_gives_the_same_signature()
    {
        // ECDSA déterministe (RFC 6979) : utile pour reproduire un incident.
        var asset = KnownAssets.EurcBaseSepolia;
        var requirements = new PaymentRequirements
        {
            Scheme = "exact", Network = asset.Network, Amount = "10000",
            Asset = asset.Address, PayTo = "0x209693Bc6afc0C5328bA36FaF03C514EF312287C",
            MaxTimeoutSeconds = 60,
        };
        var signer = new PrivateKeyPaymentSigner(PrivateKey);
        var authorization = new Eip3009Authorization
        {
            From = signer.Address, To = requirements.PayTo, Value = "10000",
            ValidAfter = "1", ValidBefore = "2",
            Nonce = "0x" + new string('0', 64),
        };

        var first = await signer.SignAsync(requirements, authorization, asset,
            TestContext.Current.CancellationToken);
        var second = await signer.SignAsync(requirements, authorization, asset,
            TestContext.Current.CancellationToken);

        second.Signature.ShouldBe(first.Signature);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("0x1234")]
    public void A_malformed_private_key_is_rejected_at_construction(string key)
    {
        Should.Throw<ArgumentException>(() => new PrivateKeyPaymentSigner(key));
    }
}
