using X402.Protocol;

namespace X402.Client;

/// <summary>Base type for failures raised by the paying handler.</summary>
public class X402Exception : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    public X402Exception(string message) : base(message) { }

    /// <summary>Creates an exception with a message and a cause.</summary>
    public X402Exception(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The demanded amount exceeds a configured limit. Raised before anything is signed, so no
/// usable authorization ever leaves the process.
/// </summary>
public sealed class SpendingLimitExceededException(string message) : X402Exception(message);

/// <summary>No requirement in the demand is one this client is willing and able to pay.</summary>
public sealed class NoAcceptablePaymentException(string message) : X402Exception(message);

/// <summary>
/// The server refused the payment, or demanded payment again after being paid. Carries whatever
/// the server's own <see cref="Protocol.PaymentRequired"/> said about why, when it could be
/// decoded — an agent paying a third-party API cannot read that server's logs, so this is the
/// only place the actionable reason (an insufficient balance, an unsupported asset, an expired
/// authorization, and so on) reaches code that can act on it.
/// </summary>
public sealed class PaymentRejectedException : X402Exception
{
    /// <summary>Creates an exception with a message and no decoded refusal to attach.</summary>
    public PaymentRejectedException(string message) : base(message) { }

    /// <summary>
    /// Creates an exception carrying the payment demand the server returned when it refused, so a
    /// caller can inspect exactly which requirements were refused and why rather than only read a
    /// message.
    /// </summary>
    public PaymentRejectedException(string message, PaymentRequired paymentRequired) : base(message)
    {
        ArgumentNullException.ThrowIfNull(paymentRequired);

        PaymentRequired = paymentRequired;
        Reason = paymentRequired.Error;
    }

    /// <summary>
    /// The server's stated reason for the refusal (<see cref="Protocol.PaymentRequired.Error"/>),
    /// when the server's response carried a decodable <c>PAYMENT-REQUIRED</c> header and gave one.
    /// Null when no reason was given, or the response could not be decoded at all — in which case
    /// the exception's own message is the only information available.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// The payment demand the server returned when it refused, when its <c>PAYMENT-REQUIRED</c>
    /// header could be decoded. Exposes the full set of requirements the server refused, not just
    /// <see cref="Reason"/> — a caller can inspect what was actually on offer. Null when the
    /// response carried no such header, or it could not be decoded.
    /// </summary>
    public PaymentRequired? PaymentRequired { get; }
}
