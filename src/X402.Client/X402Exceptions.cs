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

/// <summary>The server refused the payment, or demanded payment again after being paid.</summary>
public sealed class PaymentRejectedException(string message) : X402Exception(message);
