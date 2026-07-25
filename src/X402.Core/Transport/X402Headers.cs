namespace X402.Transport;

/// <summary>The HTTP headers the x402 v2 transport uses. All three carry base64-encoded JSON.</summary>
public static class X402Headers
{
    /// <summary>Server to client: the payment demand.</summary>
    public const string PaymentRequired = "PAYMENT-REQUIRED";

    /// <summary>Client to server: the proof of payment.</summary>
    public const string PaymentSignature = "PAYMENT-SIGNATURE";

    /// <summary>Server to client: the settlement outcome.</summary>
    public const string PaymentResponse = "PAYMENT-RESPONSE";
}
