using Nethereum.Signer;
using Nethereum.Signer.EIP712;
using X402.Assets;
using X402.Protocol;

namespace X402.Client.Signing;

/// <summary>Signs with a private key held in memory.</summary>
public sealed class PrivateKeyPaymentSigner : IPaymentSigner
{
    private readonly EthECKey key;
    private readonly Eip712TypedDataSigner signer = new();

    /// <summary>Creates a signer from a hex private key, with or without a <c>0x</c> prefix.</summary>
    /// <exception cref="ArgumentException">The key is not a valid secp256k1 private key.</exception>
    public PrivateKeyPaymentSigner(string privateKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKey);

        // EthECKey accepts and silently zero-extends hex strings shorter than 32 bytes instead of
        // rejecting them, so an under-length key must be caught here, before construction.
        var body = privateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? privateKey[2..]
            : privateKey;
        if (body.Length != 64 || !body.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException(
                "The value is not a valid secp256k1 private key: expected 32 bytes (64 hex characters).",
                nameof(privateKey));
        }

        try
        {
            key = new EthECKey(privateKey);
        }
        catch (Exception exception)
        {
            throw new ArgumentException(
                "The value is not a valid secp256k1 private key.", nameof(privateKey), exception);
        }

        Address = key.GetPublicAddress();
    }

    /// <inheritdoc />
    public string Address { get; }

    /// <inheritdoc />
    public ValueTask<ExactEvmPayload> SignAsync(
        PaymentRequirements requirements, Eip3009Authorization authorization,
        AssetDescriptor asset, CancellationToken cancellationToken = default)
    {
        var typedData = Eip3009TypedData.Build(requirements, authorization, asset);
        var signature = signer.SignTypedDataV4(typedData, key);

        return ValueTask.FromResult(new ExactEvmPayload
        {
            Signature = signature,
            Authorization = authorization,
        });
    }
}
