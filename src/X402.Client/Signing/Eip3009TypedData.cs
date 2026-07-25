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
/// a server choose the domain a payer signs under. The <c>asset</c> parameter of <see cref="Build"/>
/// is cross-checked against its <c>requirements</c> parameter for exactly that reason: the
/// descriptor is what decides the signed domain, so a caller must not be able to pass one that
/// names a different network or contract than the requirement it is meant to satisfy.
/// </remarks>
public static class Eip3009TypedData
{
    /// <summary>Builds the typed data to sign.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="asset"/> names a different network or contract address than
    /// <paramref name="requirements"/>.
    /// </exception>
    public static TypedData<Domain> Build(
        PaymentRequirements requirements, Eip3009Authorization authorization, AssetDescriptor asset)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(asset);

        // Structural cross-check, not merely a null check: this is what keeps a custom
        // IPaymentSigner from being able to sign for a token other than the one being paid for —
        // see the type-level remarks. Every reader of this parameter list is entitled to assume
        // requirements and asset were already verified to match; make that true here rather than
        // relying on the one caller that happens to get it right today.
        if (!string.Equals(asset.Network, requirements.Network, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The asset's network ('{asset.Network}') does not match the requirement's " +
                $"network ('{requirements.Network}'). Signing under the wrong network would " +
                "produce an authorization valid on a different chain than the one being paid for.",
                nameof(asset));
        }

        if (!string.Equals(asset.Address, requirements.Asset, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The asset's contract address ('{asset.Address}') does not match the " +
                $"requirement's asset ('{requirements.Asset}'). Signing for the wrong token would " +
                "authorize a transfer of a token other than the one the payer intended to spend.",
                nameof(asset));
        }

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
