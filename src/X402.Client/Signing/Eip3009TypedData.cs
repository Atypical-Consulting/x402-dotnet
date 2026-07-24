using System.Globalization;
using System.Numerics;
using Nethereum.ABI.EIP712;
using X402.Assets;
using X402.Networks;
using X402.Protocol;

namespace X402.Client.Signing;

/// <summary>
/// Builds the EIP-712 typed data for an EIP-3009 <c>transferWithAuthorization</c>, exactly as the
/// token contract will reconstruct it when the facilitator settles.
/// </summary>
/// <remarks>
/// The domain comes from the locally resolved <see cref="AssetDescriptor"/>, never from the
/// requirement's <c>extra</c> field. <c>extra</c> arrives over the network: honouring it would let
/// a server choose the domain a payer signs under.
/// </remarks>
public static class Eip3009TypedData
{
    /// <summary>Builds the typed data to sign.</summary>
    public static TypedData<Domain> Build(
        PaymentRequirements requirements, Eip3009Authorization authorization, AssetDescriptor asset)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(asset);

        var network = Caip2Network.Parse(asset.Network);
        if (!network.IsEvm)
        {
            throw new NotSupportedException(
                $"The exact scheme with EIP-3009 applies to EVM networks; '{asset.Network}' is not one.");
        }

        return new TypedData<Domain>
        {
            Domain = new Domain
            {
                Name = asset.Eip712Name,
                Version = asset.Eip712Version,
                ChainId = new BigInteger(network.ChainId),
                VerifyingContract = asset.Address,
            },
            Types = new Dictionary<string, MemberDescription[]>
            {
                ["EIP712Domain"] =
                [
                    new MemberDescription { Name = "name", Type = "string" },
                    new MemberDescription { Name = "version", Type = "string" },
                    new MemberDescription { Name = "chainId", Type = "uint256" },
                    new MemberDescription { Name = "verifyingContract", Type = "address" },
                ],
                ["TransferWithAuthorization"] =
                [
                    new MemberDescription { Name = "from", Type = "address" },
                    new MemberDescription { Name = "to", Type = "address" },
                    new MemberDescription { Name = "value", Type = "uint256" },
                    new MemberDescription { Name = "validAfter", Type = "uint256" },
                    new MemberDescription { Name = "validBefore", Type = "uint256" },
                    new MemberDescription { Name = "nonce", Type = "bytes32" },
                ],
            },
            PrimaryType = "TransferWithAuthorization",
            Message =
            [
                new MemberValue { TypeName = "address", Value = authorization.From },
                new MemberValue { TypeName = "address", Value = authorization.To },
                new MemberValue { TypeName = "uint256", Value = ParseAmount(authorization.Value) },
                new MemberValue { TypeName = "uint256", Value = ParseAmount(authorization.ValidAfter) },
                new MemberValue { TypeName = "uint256", Value = ParseAmount(authorization.ValidBefore) },
                new MemberValue { TypeName = "bytes32", Value = ParseNonce(authorization.Nonce) },
            ],
        };
    }

    private static BigInteger ParseAmount(string value) =>
        BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new FormatException(
                $"'{value}' is not a non-negative integer. The protocol carries these as strings, " +
                "but their content must still be an integer.");

    private static byte[] ParseNonce(string nonce)
    {
        var body = nonce.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? nonce[2..] : nonce;

        if (body.Length != 64)
        {
            throw new FormatException(
                $"The nonce must be 32 bytes; '{nonce}' decodes to {body.Length / 2}.");
        }

        return Convert.FromHexString(body);
    }
}
