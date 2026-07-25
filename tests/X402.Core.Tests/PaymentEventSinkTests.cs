using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using X402.Billing;

namespace X402.Core.Tests;

public sealed class PaymentEventSinkTests
{
    private static PaymentEvent Sample(PaymentEventStatus status) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        Resource = "https://api.example.com/premium",
        Amount = "10000",
        Asset = "0x808456652fdb597867f38412077A9182bf77359F",
        Network = "eip155:84532",
        Status = status,
        Payer = "0x857b06519E91e3A54538791bDbb0E22373e36b66",
    };

    [Theory]
    [InlineData(PaymentEventStatus.PaymentRequired)]
    [InlineData(PaymentEventStatus.VerificationFailed)]
    [InlineData(PaymentEventStatus.Verified)]
    [InlineData(PaymentEventStatus.SettlementFailed)]
    [InlineData(PaymentEventStatus.Settled)]
    public async Task LoggerPaymentEventSink_writes_one_entry_per_status(PaymentEventStatus status)
    {
        var logger = new RecordingLogger();
        var sink = new LoggerPaymentEventSink(logger);

        await sink.RecordAsync(Sample(status), TestContext.Current.CancellationToken);

        logger.Entries.Count.ShouldBe(1);
        logger.Entries[0].ShouldContain(status.ToString());
    }

    [Fact]
    public async Task Failures_are_logged_at_warning_level()
    {
        var logger = new RecordingLogger();
        var sink = new LoggerPaymentEventSink(logger);

        await sink.RecordAsync(Sample(PaymentEventStatus.SettlementFailed) with
        {
            FailureReason = "insufficient_funds",
        }, TestContext.Current.CancellationToken);

        logger.Levels[0].ShouldBe(LogLevel.Warning);
    }

    private sealed class RecordingLogger : ILogger<LoggerPaymentEventSink>
    {
        public List<string> Entries { get; } = [];
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Entries.Add(formatter(state, exception));
        }
    }
}
