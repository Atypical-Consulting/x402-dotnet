using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using X402.AspNetCore.Engine;
using X402.AspNetCore.Idempotency;
using X402.AspNetCore.Middleware;
using X402.Assets;
using X402.Protocol;
using X402.TestKit;

namespace X402.AspNetCore.Tests;

/// <summary>
/// Pins <see cref="X402Middleware"/>'s routing between the endpoint, the refusal path and
/// settlement. The states a real request rarely reaches — a payment opened with no buffering, a
/// buffer that overflowed before the endpoint threw — are set up directly on
/// <see cref="X402RequestFeature"/> from inside <c>next</c>; the rest go through
/// <see cref="PaidServerFixture"/>.
/// </summary>
public sealed class X402MiddlewareTests
{
    [Fact]
    public async Task An_unpriced_request_that_never_opens_a_payment_finishes_nothing()
    {
        // No route, no gate: there is no payment to settle, so the processor is never reached
        // (it is null here, and would throw if it were).
        var middleware = Create(_ => Task.CompletedTask, new CapturingLogger());

        await Should.NotThrowAsync(() => middleware.InvokeAsync(new DefaultHttpContext()));
    }

    [Fact]
    public async Task A_payment_opened_without_buffering_fails_loudly()
    {
        var middleware = Create(
            context =>
            {
                context.Features.Get<X402RequestFeature>()!.Attempt = Accepted();
                return Task.CompletedTask;
            },
            new CapturingLogger());

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(new DefaultHttpContext()));

        exception.Message.ShouldBe(
            "A payment gate was opened but no buffering was installed. This is a bug in " +
            "X402.AspNetCore; please report it.");
    }

    [Fact]
    public async Task A_refusal_whose_buffer_overflowed_still_answers_with_the_refusal()
    {
        // An endpoint ignored a gate refusal, then let the cap-crossing failure propagate instead
        // of swallowing it.
        var context = new DefaultHttpContext();
        var middleware = Create(
            ctx =>
            {
                var feature = ctx.Features.Get<X402RequestFeature>()!;
                feature.Refusal = PaymentAttempt.Refused(new PaymentRequiredResult(Demand()));
                feature.Buffer = Buffering(ctx, settles: false);
                throw new BufferingSettlementFailedException();
            },
            new CapturingLogger());

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status402PaymentRequired);
    }

    [Fact]
    public async Task An_endpoint_throwing_before_settlement_logs_the_abandon()
    {
        var logger = new CapturingLogger();
        var middleware = Create(
            ctx =>
            {
                var feature = ctx.Features.Get<X402RequestFeature>()!;
                feature.Attempt = Accepted();
                feature.Buffer = Buffering(ctx, settles: true);
                throw new InvalidOperationException("endpoint fault");
            },
            logger);

        await Should.ThrowAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(new DefaultHttpContext()));

        logger.Messages.ShouldContain(message => message.Contains("abandoned: the endpoint threw"));
    }

    [Fact]
    public async Task An_endpoint_throwing_after_the_buffer_settled_is_not_abandoned()
    {
        // Funds already moved when the buffer crossed the cap: abandoning the authorization now
        // would let the payer reuse it.
        var logger = new CapturingLogger();
        var middleware = Create(
            async ctx =>
            {
                var feature = ctx.Features.Get<X402RequestFeature>()!;
                feature.Attempt = Accepted();
                feature.Buffer = Buffering(ctx, settles: true);
                await feature.Buffer.Stream.WriteAsync(new byte[2]);
                throw new InvalidOperationException("endpoint fault");
            },
            logger);

        await Should.ThrowAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(new DefaultHttpContext()));

        logger.Messages.ShouldContain(message => message.Contains("not abandoned"));
    }

    [Fact]
    public async Task A_propagated_capacity_failure_writes_the_refusal_exactly_once()
    {
        await using var server = await PaidServerFixture.StartAsync(
            configure: options => options.MaxBufferedResponseBytes = 1024);
        server.Facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;

        var response = await server.PayAsync("/large-with-content-length", KnownAssets.EurcBaseSepolia);

        // Finishing the payment again after the refusal would either append a second body or throw
        // on the already-started response's read-only headers.
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        (body, server.LastServerError).ShouldBe(("{}", ""));
    }

    [Fact]
    public async Task A_conflict_is_answered_without_a_server_error()
    {
        await using var server = await PaidServerFixture.StartAsync();
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        var authorization = payload.AsExactEvm().Authorization;
        await server.Ledger.AcquireAsync(
            new PaymentIdentity(payload.Accepted.Network, payload.Accepted.Asset, authorization.Nonce),
            TestContext.Current.CancellationToken);

        var response = await server.SendAsync("/premium", payload);

        (response.StatusCode, server.LastServerError).ShouldBe((HttpStatusCode.Conflict, ""));
    }

    [Fact]
    public async Task A_free_route_is_answered_without_a_server_error()
    {
        await using var server = await PaidServerFixture.StartAsync();

        await server.Client.GetAsync("/free", TestContext.Current.CancellationToken);

        server.LastServerError.ShouldBeEmpty();
    }

    // The processor is only used for a route match, which the route table here never produces.
    private static X402Middleware Create(RequestDelegate next, CapturingLogger logger) =>
        new(next, processor: null!, new InMemorySettlementLedger(), [], logger);

    private static BufferingResponseBodyFeature Buffering(HttpContext context, bool settles) =>
        new(context.Features.Get<IHttpResponseBodyFeature>()!, 1, _ => Task.FromResult(settles));

    private static PaymentRequired Demand() => new()
    {
        Resource = new ResourceInfo { Url = "https://api.example/premium" },
        Accepts = [],
    };

    private static PaymentAttempt Accepted() => PaymentAttempt.Accepted(
        payload: null!, requirements: null!, KnownAssets.EurcBaseSepolia, payer: null,
        new PaymentIdentity("eip155:84532", KnownAssets.EurcBaseSepolia.Address, "0x01"),
        new ResourceInfo { Url = "https://api.example/premium" }, []);

    private sealed class CapturingLogger : ILogger<X402Middleware>
    {
        private readonly List<string> messages = [];

        public IReadOnlyList<string> Messages => messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
    }
}
