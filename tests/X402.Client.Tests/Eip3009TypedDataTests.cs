using System.Numerics;
using System.Text;
using Nethereum.ABI;
using Nethereum.ABI.EIP712;
using Nethereum.Signer.EIP712;
using Nethereum.Util;
using X402.Assets;
using X402.Client.Signing;
using X402.Protocol;

namespace X402.Client.Tests;

public sealed class Eip3009TypedDataTests
{
    private static readonly Sha3Keccack Keccak = Sha3Keccack.Current;

    private static PaymentRequirements Requirements(AssetDescriptor asset) => new()
    {
        Scheme = "exact",
        Network = asset.Network,
        Amount = "10000",
        Asset = asset.Address,
        PayTo = "0x209693Bc6afc0C5328bA36FaF03C514EF312287C",
        MaxTimeoutSeconds = 60,
    };

    private static Eip3009Authorization Authorization() => new()
    {
        From = "0x857b06519E91e3A54538791bDbb0E22373e36b66",
        To = "0x209693Bc6afc0C5328bA36FaF03C514EF312287C",
        Value = "10000",
        ValidAfter = "1740672089",
        ValidBefore = "1740672154",
        Nonce = "0xf3746613c2d920b5fdabc0856f2aeb2d4f88ee6037b8cc5d04a71a4462f13480",
    };

    [Theory]
    [InlineData("EURC-sepolia")]
    [InlineData("EURC-mainnet")]
    [InlineData("USDC-sepolia")]
    [InlineData("USDC-mainnet")]
    public void The_encoded_digest_matches_an_independently_computed_one(string assetKey)
    {
        var asset = assetKey switch
        {
            "EURC-sepolia" => KnownAssets.EurcBaseSepolia,
            "EURC-mainnet" => KnownAssets.EurcBaseMainnet,
            "USDC-sepolia" => KnownAssets.UsdcBaseSepolia,
            _ => KnownAssets.UsdcBaseMainnet,
        };

        var requirements = Requirements(asset);
        var authorization = Authorization();

        var fromLibrary = Eip712TypedDataEncoder.Current.EncodeAndHashTypedData(
            Eip3009TypedData.Build(requirements, authorization, asset));

        var fromScratch = ComputeDigestByHand(asset, authorization);

        Convert.ToHexString(fromLibrary).ShouldBe(Convert.ToHexString(fromScratch));
    }

    /// <summary>
    /// EIP-712 digest computed straight from the specification: keccak256(0x1901 ‖ domainSeparator
    /// ‖ structHash). Deliberately shares no code with Eip3009TypedData, so a mistake there cannot
    /// hide behind a matching mistake here.
    /// </summary>
    private static byte[] ComputeDigestByHand(AssetDescriptor asset, Eip3009Authorization a)
    {
        var abi = new ABIEncode();

        var domainTypeHash = Keccak.CalculateHash(Encoding.UTF8.GetBytes(
            "EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"));

        var domainSeparator = Keccak.CalculateHash(abi.GetABIEncoded(
            new ABIValue("bytes32", domainTypeHash),
            new ABIValue("bytes32", Keccak.CalculateHash(Encoding.UTF8.GetBytes(asset.Eip712Name))),
            new ABIValue("bytes32", Keccak.CalculateHash(Encoding.UTF8.GetBytes(asset.Eip712Version))),
            new ABIValue("uint256", BigInteger.Parse(asset.Network.Split(':')[1])),
            new ABIValue("address", asset.Address)));

        var structTypeHash = Keccak.CalculateHash(Encoding.UTF8.GetBytes(
            "TransferWithAuthorization(address from,address to,uint256 value," +
            "uint256 validAfter,uint256 validBefore,bytes32 nonce)"));

        var structHash = Keccak.CalculateHash(abi.GetABIEncoded(
            new ABIValue("bytes32", structTypeHash),
            new ABIValue("address", a.From),
            new ABIValue("address", a.To),
            new ABIValue("uint256", BigInteger.Parse(a.Value)),
            new ABIValue("uint256", BigInteger.Parse(a.ValidAfter)),
            new ABIValue("uint256", BigInteger.Parse(a.ValidBefore)),
            new ABIValue("bytes32", Convert.FromHexString(a.Nonce[2..]))));

        return Keccak.CalculateHash([.. new byte[] { 0x19, 0x01 }, .. domainSeparator, .. structHash]);
    }

    [Fact]
    public void The_domain_is_taken_from_the_asset_not_from_the_requirement_extra()
    {
        // extra vient du serveur, donc du réseau. L'actif résolu localement fait foi :
        // sinon un serveur malveillant ferait signer sous un domaine de son choix.
        var asset = KnownAssets.UsdcBaseMainnet;
        var requirements = Requirements(asset) with
        {
            Extra = System.Text.Json.JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["name"] = "Attacker", ["version"] = "9" }),
        };

        var typedData = Eip3009TypedData.Build(requirements, Authorization(), asset);

        typedData.Domain.Name.ShouldBe("USD Coin");
        typedData.Domain.Version.ShouldBe("2");
    }

    [Fact]
    public void The_chain_id_comes_from_the_caip2_reference()
    {
        var typedData = Eip3009TypedData.Build(
            Requirements(KnownAssets.EurcBaseSepolia), Authorization(), KnownAssets.EurcBaseSepolia);

        typedData.Domain.ChainId.ShouldBe(new BigInteger(84532));
        typedData.Domain.VerifyingContract.ShouldBe(KnownAssets.EurcBaseSepolia.Address);
    }
}
