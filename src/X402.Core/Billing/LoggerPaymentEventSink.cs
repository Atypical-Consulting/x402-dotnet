using Microsoft.Extensions.Logging;

namespace X402.Billing;

/// <summary>The default sink: writes each payment event through <see cref="ILogger"/>.</summary>
public sealed partial class LoggerPaymentEventSink : IPaymentEventSink
{
    private readonly ILogger<LoggerPaymentEventSink> logger;

    /// <summary>Creates a sink writing to the given logger.</summary>
    public LoggerPaymentEventSink(ILogger<LoggerPaymentEventSink> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    /// <inheritdoc />
    public ValueTask RecordAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentEvent);

        if (paymentEvent.Status is PaymentEventStatus.VerificationFailed
            or PaymentEventStatus.SettlementFailed)
        {
            PaymentFailed(logger, paymentEvent.Status, paymentEvent.Resource, paymentEvent.Amount,
                paymentEvent.Asset, paymentEvent.Network, paymentEvent.Payer,
                paymentEvent.FailureReason);
        }
        else
        {
            PaymentRecorded(logger, paymentEvent.Status, paymentEvent.Resource, paymentEvent.Amount,
                paymentEvent.Asset, paymentEvent.Network, paymentEvent.Payer,
                paymentEvent.Transaction);
        }

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 4020, Level = LogLevel.Information,
        Message = "x402 {Status}: {Resource} {Amount} of {Asset} on {Network} (payer {Payer}, tx {Transaction})")]
    private static partial void PaymentRecorded(ILogger logger, PaymentEventStatus status,
        string resource, string amount, string asset, string network, string? payer, string? transaction);

    [LoggerMessage(EventId = 4021, Level = LogLevel.Warning,
        Message = "x402 {Status}: {Resource} {Amount} of {Asset} on {Network} (payer {Payer}) — {FailureReason}")]
    private static partial void PaymentFailed(ILogger logger, PaymentEventStatus status,
        string resource, string amount, string asset, string network, string? payer, string? failureReason);
}
