using X402.Protocol;

namespace X402.AspNetCore.Facilitator;

/// <summary>
/// Talks to the facilitator that verifies authorizations and broadcasts settlements.
/// </summary>
/// <remarks>
/// A rejection is a response, not a fault: <see cref="VerifyAsync"/> returns
/// <c>isValid: false</c> rather than throwing. Only transport failures, timeouts and unusable
/// responses raise <see cref="FacilitatorException"/>.
/// </remarks>
public interface IFacilitatorClient
{
    /// <summary>Verifies an authorization without moving any funds.</summary>
    Task<VerifyResponse> VerifyAsync(
        PaymentPayload payload, PaymentRequirements requirements,
        CancellationToken cancellationToken = default);

    /// <summary>Broadcasts the settlement of a verified authorization.</summary>
    Task<SettleResponse> SettleAsync(
        PaymentPayload payload, PaymentRequirements requirements,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the schemes and networks the facilitator supports.</summary>
    /// <remarks>
    /// The response lists scheme and network pairs but never the assets the facilitator settles.
    /// No start-up check can therefore confirm that a given token is accepted.
    /// </remarks>
    Task<SupportedResponse> GetSupportedAsync(CancellationToken cancellationToken = default);
}

/// <summary>The facilitator could not be reached, or answered unusably.</summary>
public sealed class FacilitatorException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    public FacilitatorException(string message) : base(message) { }

    /// <summary>Creates an exception with a message and a cause.</summary>
    public FacilitatorException(string message, Exception innerException)
        : base(message, innerException) { }
}
