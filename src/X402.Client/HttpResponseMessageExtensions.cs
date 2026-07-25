using X402.Protocol;

namespace X402.Client;

/// <summary>Extension members for reading data that <see cref="X402PaymentHandler"/> attaches to a response.</summary>
public static class HttpResponseMessageExtensions
{
    /// <summary>
    /// The key <see cref="X402PaymentHandler"/> stores the settlement receipt under.
    /// <see cref="HttpResponseMessage"/> has no <c>Options</c> bag of its own, so the receipt is
    /// carried on <see cref="HttpResponseMessage.RequestMessage"/>'s <see cref="HttpRequestMessage.Options"/>
    /// instead — the handler always makes sure that request is the one it actually sent.
    /// </summary>
    internal static readonly HttpRequestOptionsKey<SettleResponse> ReceiptKey = new("X402.PaymentReceipt");

    /// <summary>
    /// The settlement receipt for a request that <see cref="X402PaymentHandler"/> paid for on the
    /// caller's behalf, or <c>null</c> when the request never received a 402, or the server did
    /// not return a decodable <c>PAYMENT-RESPONSE</c> alongside its success.
    /// </summary>
    public static SettleResponse? GetPaymentReceipt(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.RequestMessage is { } request && request.Options.TryGetValue(ReceiptKey, out var receipt)
            ? receipt
            : null;
    }
}
