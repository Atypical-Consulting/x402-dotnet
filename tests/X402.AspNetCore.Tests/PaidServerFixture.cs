using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.DependencyInjection;
using X402.AspNetCore.Engine;
using X402.AspNetCore.Idempotency;
using X402.Assets;
using X402.Billing;
using X402.Networks;
using X402.Pricing;
using X402.Protocol;
using X402.TestKit;
using X402.Transport;

namespace X402.AspNetCore.Tests;

/// <summary>
/// A real x402-accepting server on an in-memory <see cref="TestServer"/>, so
/// <c>PaymentProcessorTests</c> and <c>BufferingTests</c> verify observable HTTP behavior rather
/// than internal calls.
/// </summary>
/// <remarks>
/// <c>UseX402</c> and the route-pricing middleware do not exist yet — they are task 11. Until then
/// this fixture wires <see cref="X402PaymentProcessor"/> (internal, reached via
/// <c>InternalsVisibleTo</c>, the same access <see cref="Facilitator.HttpFacilitatorClient"/>
/// already uses) into a hand-built pipeline that plays the same role
/// <c>X402Middleware.RunAndSettleAsync</c> will: authorize, buffer, settle, restore the original
/// response feature. Routes mounted: <c>/free</c> (unpriced), <c>/premium</c> and <c>/boom</c>
/// (EURC 0.010 then USDC 0.011), <c>/large</c> (same prices, streams 64 KiB).
/// </remarks>
public sealed class PaidServerFixture : IAsyncDisposable
{
    private readonly IHost host;
    private readonly RecordingPaymentEventSink sink;
    private readonly CapturingLoggerProvider logs;
    private readonly RequestCounter bufferedRequests;

    private PaidServerFixture(
        IHost host, FakeFacilitator facilitator, RecordingPaymentEventSink sink,
        CapturingLoggerProvider logs, RequestCounter bufferedRequests)
    {
        this.host = host;
        Facilitator = facilitator;
        this.sink = sink;
        this.logs = logs;
        this.bufferedRequests = bufferedRequests;
        Client = host.GetTestClient();
    }

    /// <summary>An <see cref="HttpClient"/> bound to the hosted server.</summary>
    public HttpClient Client { get; }

    /// <summary>The in-process facilitator every request is verified and settled against.</summary>
    public FakeFacilitator Facilitator { get; }

    /// <summary>Every payment event recorded so far.</summary>
    public IReadOnlyList<PaymentEvent> Events => sink.Events;

    /// <summary>Error-level log messages captured so far, formatted category-first.</summary>
    public IReadOnlyList<string> LoggedErrors => logs.Errors;

    /// <summary>How many requests had response buffering installed.</summary>
    public int BufferedRequestCount => bufferedRequests.Count;

    /// <summary>Starts the server. Assets default to EURC and USDC on Base Sepolia.</summary>
    /// <param name="configure">Further overrides applied after the fixture's defaults.</param>
    /// <param name="sinkThrows">
    /// When true, the event sink throws on every <see cref="IPaymentEventSink.RecordAsync"/> call —
    /// exercises the guarantee that billing never fails a payment already settled on-chain.
    /// </param>
    public static Task<PaidServerFixture> StartAsync(
        Action<X402Options>? configure = null, bool sinkThrows = false)
    {
        var facilitator = new FakeFacilitator();
        var sink = new RecordingPaymentEventSink(sinkThrows);
        var logs = new CapturingLoggerProvider();
        var bufferedRequests = new RequestCounter();

        var premiumPrices = new PriceSet([
            Price.For(KnownAssets.EurcBaseSepolia, 0.010m),
            Price.For(KnownAssets.UsdcBaseSepolia, 0.011m),
        ]);

        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRoutingCore();
                    services.AddLogging(builder => builder.AddProvider(logs));

                    // Registered before AddX402: AddCore's registration of the default sink is a
                    // TryAdd, so this one wins.
                    services.AddSingleton<IPaymentEventSink>(sink);

                    services.AddX402(options =>
                    {
                        options.PayTo = TestData.PayeeAddress;
                        options.Network = KnownNetworks.BaseSepolia;
                        // No path segment: FakeFacilitator mounts /verify, /settle and /supported
                        // at the root, exactly as FacilitatorClientTests already relies on.
                        options.FacilitatorUrl = new Uri("https://x402.example/");
                        options.Assets.Add(new AssetConfiguration { Symbol = "EURC" });
                        options.Assets.Add(new AssetConfiguration { Symbol = "USDC" });
                        configure?.Invoke(options);
                    });

                    // Route the two named facilitator clients AddX402 already configured (base
                    // address, infinite HttpClient.Timeout) at the fake facilitator in-process.
                    services.AddHttpClient("x402-verify")
                        .ConfigurePrimaryHttpMessageHandler(() => facilitator.CreateHandler());
                    services.AddHttpClient("x402-settle")
                        .ConfigurePrimaryHttpMessageHandler(() => facilitator.CreateHandler());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/free", () => Results.Text("free content"));

                        MapPriced(endpoints, "/premium", premiumPrices, bufferedRequests,
                            context => context.Response.WriteAsync("premium content", context.RequestAborted));

                        MapPriced(endpoints, "/boom", premiumPrices, bufferedRequests,
                            _ => throw new InvalidOperationException("boom"));

                        MapPriced(endpoints, "/large", premiumPrices, bufferedRequests, WriteLargeBodyAsync);
                    });
                }))
            .Start();

        return Task.FromResult(new PaidServerFixture(host, facilitator, sink, logs, bufferedRequests));
    }

    /// <summary>Decodes the payment demand carried by a 402 response.</summary>
    public PaymentRequired DecodeDemand(HttpResponseMessage response)
    {
        var header = response.Headers.GetValues(X402Headers.PaymentRequired).Single();
        if (!X402Codec.TryDecode<PaymentRequired>(header, out var demand, out var error))
        {
            throw new InvalidOperationException($"could not decode a payment demand: {error}");
        }

        return demand!;
    }

    /// <summary>Fetches the demand, signs a genuine payment in <paramref name="asset"/>, and sends it.</summary>
    public async Task<HttpResponseMessage> PayAsync(string path, AssetDescriptor asset) =>
        await SendAsync(path, await SignFor(path, asset));

    /// <summary>Fetches the demand and signs a genuine payment in <paramref name="asset"/>, without sending it.</summary>
    public async Task<PaymentPayload> SignFor(string path, AssetDescriptor asset)
    {
        var demand = DecodeDemand(await Client.GetAsync(path, CancellationToken.None));
        var requirement = demand.Accepts.Single(a => EvmAddress.AreEqual(a.Asset, asset.Address));
        return await TestData.SignedPayloadAsync(requirement);
    }

    /// <summary>Sends a request to <paramref name="path"/> carrying <paramref name="payload"/> as proof of payment.</summary>
    public async Task<HttpResponseMessage> SendAsync(string path, PaymentPayload payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(X402Headers.PaymentSignature, X402Codec.Encode(payload));
        return await Client.SendAsync(request, CancellationToken.None);
    }

    /// <summary>
    /// Signs a genuine payment for an asset on a different network — one this server never
    /// offers — and sends it. The engine must refuse to match it locally.
    /// </summary>
    public async Task<HttpResponseMessage> PayWithForeignAssetAsync(string path)
    {
        var requirements = TestData.RequirementsFor(KnownAssets.EurcBaseMainnet, "10000");
        var payload = await TestData.SignedPayloadAsync(requirements);
        return await SendAsync(path, payload);
    }

    /// <summary>
    /// Signs a payment for the real EURC requirement with the amount changed to
    /// <paramref name="amount"/> — a genuine signature over the attacker's own (wrong) terms —
    /// then sends it. <c>accepted</c> still names the real scheme/network/asset, so the engine
    /// matches the server's real (unmodified) requirement and sends that to the facilitator; the
    /// signed authorization no longer matches it.
    /// </summary>
    public async Task<HttpResponseMessage> PayWithTamperedAmountAsync(string path, string amount)
    {
        var demand = DecodeDemand(await Client.GetAsync(path, CancellationToken.None));
        var real = demand.Accepts[0];
        var payload = await TestData.SignedPayloadAsync(real with { Amount = amount });
        return await SendAsync(path, payload);
    }

    /// <summary>Same as <see cref="PayWithTamperedAmountAsync"/>, tampering the payee instead.</summary>
    public async Task<HttpResponseMessage> PayWithTamperedPayeeAsync(string path, string payee)
    {
        var demand = DecodeDemand(await Client.GetAsync(path, CancellationToken.None));
        var real = demand.Accepts[0];
        var payload = await TestData.SignedPayloadAsync(real with { PayTo = payee });
        return await SendAsync(path, payload);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await host.StopAsync();
        host.Dispose();
        await Facilitator.DisposeAsync();
    }

    private static async Task WriteLargeBodyAsync(HttpContext context)
    {
        var chunk = new byte[4096];
        Array.Fill(chunk, (byte)'x');
        for (var i = 0; i < 16; i++)
        {
            await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
        }
    }

    private static void MapPriced(
        IEndpointRouteBuilder endpoints, string pattern, PriceSet prices, RequestCounter counter,
        Func<HttpContext, Task> handler) =>
        endpoints.MapGet(pattern, context => RunPaidAsync(context, prices, handler, counter));

    /// <summary>
    /// Stands in for <c>X402Middleware.RunAndSettleAsync</c> (task 11): authorize, install
    /// buffering, run the endpoint, settle, and always restore the original response feature.
    /// </summary>
    private static async Task RunPaidAsync(
        HttpContext context, PriceSet prices, Func<HttpContext, Task> handler, RequestCounter counter)
    {
        var processor = context.RequestServices.GetRequiredService<X402PaymentProcessor>();
        var ledger = context.RequestServices.GetRequiredService<ISettlementLedger>();
        var options = context.RequestServices.GetRequiredService<IOptions<X402Options>>();

        var attempt = await processor.AuthorizeAsync(context, prices, null, context.RequestAborted);

        if (!attempt.CanContinue)
        {
            if (attempt.ConflictReason is { } conflict)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsync(conflict, context.RequestAborted);
                return;
            }

            await attempt.Result!.ExecuteAsync(context);
            return;
        }

        var original = context.Features.Get<IHttpResponseBodyFeature>()!;
        var buffering = new BufferingResponseBodyFeature(
            original, options.Value.MaxBufferedResponseBytes,
            ct => processor.SettleAsync(context, attempt, ct));
        counter.Increment();
        context.Features.Set<IHttpResponseBodyFeature>(buffering);

        try
        {
            await handler(context);
        }
        catch (BufferingSettlementFailedException)
        {
            // Settlement was attempted — and failed — exactly when the buffer crossed the cap,
            // before anything reached the real network: still refuse cleanly.
            buffering.Discard();
            context.Features.Set(original);
            await new PaymentRequiredResult(attempt.Demand!).ExecuteAsync(context);
            return;
        }
        catch
        {
            // The endpoint threw before settlement was ever attempted: the authorization was never
            // used, so release it for a retry rather than leave it stuck as in-flight.
            buffering.Discard();
            context.Features.Set(original);
            await ledger.AbandonAsync(attempt.Identity, context.RequestAborted);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }

            return;
        }
        finally
        {
            // Always restore, whichever path above ran — leaking a buffering feature onto a later
            // request would be a serious, hard-to-diagnose bug.
            context.Features.Set(original);
        }

        if (buffering.Overflowed)
        {
            // Settlement already happened when the cap was crossed.
            await buffering.CompleteAsync();
            return;
        }

        var settled = await processor.SettleAsync(context, attempt, context.RequestAborted);
        if (settled)
        {
            await buffering.FlushBufferAsync(context.RequestAborted);
            return;
        }

        // Settlement failed within the cap: the buffered content is discarded, never delivered.
        buffering.Discard();
        await new PaymentRequiredResult(attempt.Demand!).ExecuteAsync(context);
    }

    private sealed class RequestCounter
    {
        private int count;

        public int Count => count;

        public void Increment() => Interlocked.Increment(ref count);
    }

    private sealed class RecordingPaymentEventSink(bool throwOnRecord) : IPaymentEventSink
    {
        private readonly object gate = new();
        private readonly List<PaymentEvent> events = [];

        public IReadOnlyList<PaymentEvent> Events
        {
            get
            {
                lock (gate)
                {
                    return [.. events];
                }
            }
        }

        public ValueTask RecordAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default)
        {
            if (throwOnRecord)
            {
                throw new InvalidOperationException("this event sink is configured to fail for this test");
            }

            lock (gate)
            {
                events.Add(paymentEvent);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly object gate = new();
        private readonly List<string> errors = [];

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (gate)
                {
                    return [.. errors];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose() { }

        private void Record(string categoryName, string message)
        {
            lock (gate)
            {
                errors.Add($"{categoryName}: {message}");
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Error)
                {
                    return;
                }

                owner.Record(categoryName, formatter(state, exception));
            }
        }
    }
}
