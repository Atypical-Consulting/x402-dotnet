using X402.Assets;
using X402.Protocol;

namespace X402.Client.Signing;

/// <summary>
/// Signs EIP-3009 authorizations. The default implementation holds a private key in memory;
/// substitute one that talks to an HSM or a KMS when that is not acceptable.
/// </summary>
public interface IPaymentSigner
{
    /// <summary>The address that will appear as the payer.</summary>
    string Address { get; }

    /// <summary>Signs an authorization for the given requirement.</summary>
    ValueTask<ExactEvmPayload> SignAsync(
        PaymentRequirements requirements,
        Eip3009Authorization authorization,
        AssetDescriptor asset,
        CancellationToken cancellationToken = default);
}
