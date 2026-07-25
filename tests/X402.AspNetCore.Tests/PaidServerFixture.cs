using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.DependencyInjection;
using X402.AspNetCore.Engine;
using X402.AspNetCore.Gate;
using X402.AspNetCore.Idempotency;
using X402.AspNetCore.Middleware;
using X402.Assets;
using X402.Billing;
using X402.Networks;
using X402.Pricing;
using X402.Protocol;
using X402.TestKit;
using X402.Transport;

namespace X402.AspNetCore.Tests;

/// <summary>
/// A real x402-accepting server on an in-memory <see cref="TestServer"/>, wired through the real
/// <see cref="X402ApplicationBuilderExtensions.UseX402"/> middleware, so
/// <c>PaymentProcessorTests</c>, <c>BufferingTests</c> and <c>RouteMappingTests</c> verify
/// observable HTTP behavior rather than internal calls.
/// </summary>
/// <remarks>
/// Routes mounted by default, when <c>routes</c> is not supplied to <see cref="StartAsync"/>:
/// <c>/free</c> (unpriced), <c>/premium</c> and <c>/boom</c> (EURC 0.010 then USDC 0.011),
/// <c>/large</c> (same prices, streams 64 KiB), <c>/large-swallowing</c> (same as
/// <c>/large</c>, but swallows whatever each write throws), and
/// <c>/analyze-ignoring-refusal</c> (deliberately buggy: ignores a gate refusal and serves content
/// anyway, for <c>PaymentGateTests</c>). Supplying <c>routes</c> replaces this default set
/// entirely, for tests that need their own route table (see <c>RouteMappingTests</c>). Any path
/// not otherwise mapped answers 200 OK, so a route declared but never paid for still has
/// something to reach.
/// </remarks>
public sealed class PaidServerFixture : IAsyncDisposable
{
    private readonly IHost host;
    private readonly RecordingPaymentEventSink sink;
    private readonly CapturingLoggerProvider logs;
    private readonly RequestCounter bufferedRequests;
    private readonly LastErrorHolder lastServerError;

    private PaidServerFixture(
        IHost host, FakeFacilitator facilitator, RecordingPaymentEventSink sink,
        CapturingLoggerProvider logs, RequestCounter bufferedRequests, LastErrorHolder lastServerError)
    {
        this.host = host;
        Facilitator = facilitator;
        this.sink = sink;
        this.logs = logs;
        this.bufferedRequests = bufferedRequests;
        this.lastServerError = lastServerError;
        Client = host.GetTestClient();
        Ledger = host.Services.GetRequiredService<ISettlementLedger>();
    }

    /// <summary>An <see cref="HttpClient"/> bound to the hosted server.</summary>
    public HttpClient Client { get; }

    /// <summary>The in-process facilitator every request is verified and settled against.</summary>
    public FakeFacilitator Facilitator { get; }

    /// <summary>
    /// The same ledger the engine settles through — resolved from the hosted app's own container,
    /// not a separate instance. Lets a test seed an in-flight lease directly (see
    /// <c>An_authorization_being_settled_elsewhere_gets_409</c>) without needing a genuine race.
    /// </summary>
    public ISettlementLedger Ledger { get; }

    /// <summary>Every payment event recorded so far.</summary>
    public IReadOnlyList<PaymentEvent> Events => sink.Events;

    /// <summary>Error-level log messages captured so far, formatted category-first.</summary>
    public IReadOnlyList<string> LoggedErrors => logs.Errors;

    /// <summary>How many requests had response buffering installed.</summary>
    public int BufferedRequestCount => bufferedRequests.Count;

    /// <summary>
    /// The message of the last unhandled exception a request produced, captured by a top-level
    /// middleware that stands in for a real app's own exception handling (see
    /// <c>Opening_a_gate_without_UseX402_fails_loudly</c>). Empty until one occurs.
    /// </summary>
    public string LastServerError => lastServerError.Value ?? "";

    /// <summary>Starts the server. Assets default to EURC and USDC on Base Sepolia.</summary>
    /// <param name="configure">Further overrides applied after the fixture's defaults.</param>
    /// <param name="sinkThrows">
    /// When true, the event sink throws on every <see cref="IPaymentEventSink.RecordAsync"/> call —
    /// exercises the guarantee that billing never fails a payment already settled on-chain.
    /// </param>
    /// <param name="routes">
    /// Declares the priced route table via <see cref="X402ApplicationBuilderExtensions.UseX402"/>.
    /// When omitted, the fixture's own default route table applies (see the type-level remarks);
    /// when supplied, it replaces that default entirely, so a test can price exactly the routes it
    /// needs.
    /// </param>
    /// <param name="withMiddleware">
    /// When false, <see cref="X402ApplicationBuilderExtensions.UseX402"/> is never called — the
    /// gate and its DI registrations still exist, but there is no outbound half to settle through.
    /// Exercises <c>IX402PaymentGate.RequireAsync</c> failing loudly instead of delivering unpaid
    /// content.
    /// </param>
    public static Task<PaidServerFixture> StartAsync(
        Action<X402Options>? configure = null, bool sinkThrows = false,
        Action<X402RouteBuilder>? routes = null, bool withMiddleware = true)
    {
        var facilitator = new FakeFacilitator();
        var sink = new RecordingPaymentEventSink(sinkThrows);
        var logs = new CapturingLoggerProvider();
        var bufferedRequests = new RequestCounter();
        var lastServerError = new LastErrorHolder();

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
                    services.AddControllers();
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
                    // Stands in for a real app's own exception handling (UseExceptionHandler and
                    // the like), which this fixture otherwise has none of: without it, an unhandled
                    // exception — such as IX402PaymentGate.RequireAsync throwing when UseX402 was
                    // never called — would either propagate out of the TestServer call or be
                    // handled by ASP.NET Core's own default diagnostics, neither of which exposes
                    // the exception's message the way LastServerError needs to.
                    app.Use(async (context, next) =>
                    {
                        try
                        {
                            await next(context);
                        }
                        catch (Exception exception)
                        {
                            lastServerError.Value = exception.Message;
                            if (!context.Response.HasStarted)
                            {
                                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                            }
                        }
                    });

                    // Counts, for BufferedRequestCount, every request for which UseX402 installed
                    // response buffering. The marker X402Middleware leaves on X402RequestFeature
                    // outlives the request, even once settlement has already run, so it can still
                    // be read here after the rest of the pipeline has returned.
                    app.Use(async (context, next) =>
                    {
                        await next(context);
                        if (context.Features.Get<X402RequestFeature>()?.Buffer is not null)
                        {
                            bufferedRequests.Increment();
                        }
                    });

                    if (withMiddleware)
                    {
                        app.UseX402(routeBuilder =>
                        {
                            if (routes is not null)
                            {
                                routes(routeBuilder);
                                return;
                            }

                            routeBuilder
                                .Map("/premium", premiumPrices)
                                .Map("/boom", premiumPrices)
                                .Map("/large", premiumPrices)
                                .Map("/large-swallowing", premiumPrices);
                        });
                    }

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();

                        endpoints.MapGet("/free", () => Results.Text("free content"));
                        endpoints.MapGet("/premium", () => Results.Text("premium content"));
                        endpoints.MapGet("/boom", ThrowBoomAsync);
                        endpoints.MapGet("/large", WriteLargeBodyAsync);
                        endpoints.MapGet("/large-swallowing", WriteLargeBodySwallowingExceptionsAsync);
                        endpoints.MapPost("/analyze", AnalyzeAsync);
                        endpoints.MapPost("/by-size", BySizeAsync);
                        endpoints.MapGet("/analyze-ignoring-refusal", IgnoringRefusalAsync);

                        // A test that declares its own route table (see RouteMappingTests) can
                        // reach paths never mapped above; anything unmatched still needs to answer
                        // with something other than a 404 once payment is not the reason it stopped.
                        endpoints.MapFallback(() => Results.Text("fallback content"));
                    });
                }))
            .Start();

        return Task.FromResult(new PaidServerFixture(
            host, facilitator, sink, logs, bufferedRequests, lastServerError));
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
    /// Pays a dynamically priced endpoint: posts <paramref name="body"/> once to read the demand it
    /// produces, signs a genuine payment for <paramref name="asset"/> (EURC by default) against
    /// whichever requirement in that demand names it, then posts <paramref name="body"/> again with
    /// the payment attached. The two requests share nothing server-side — the gate recomputes the
    /// price fresh both times — so this only works when <paramref name="body"/> prices the same on
    /// both calls, which every dynamically priced test route here does.
    /// </summary>
    public async Task<HttpResponseMessage> PayDynamicAsync<TBody>(
        string path, TBody body, AssetDescriptor? asset = null)
    {
        asset ??= KnownAssets.EurcBaseSepolia;

        var demand = DecodeDemand(await Client.PostAsJsonAsync(path, body, CancellationToken.None));
        var requirement = demand.Accepts.Single(a => EvmAddress.AreEqual(a.Asset, asset.Address));
        var payload = await TestData.SignedPayloadAsync(requirement);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
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

    /// <summary>
    /// Same as <see cref="WriteLargeBodyAsync"/>, except every write is wrapped in a broad catch
    /// that swallows whatever it throws and returns normally — a real streaming endpoint tolerating
    /// a client disconnect this way is a common pattern, and it must not be able to hide a failed
    /// cap-crossing settlement from the pipeline (see <see cref="BufferingResponseBodyFeature.Poisoned"/>).
    /// </summary>
    private static async Task WriteLargeBodySwallowingExceptionsAsync(HttpContext context)
    {
        var chunk = new byte[4096];
        Array.Fill(chunk, (byte)'x');
        for (var i = 0; i < 16; i++)
        {
            try
            {
                await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    private static Task ThrowBoomAsync(HttpContext _) =>
        throw new InvalidOperationException("boom");

    /// <summary>
    /// A minimal endpoint pricing itself from the request body through <see cref="IX402PaymentGate"/>
    /// — 0.001 EURC (and USDC) per token — exercised by <c>PaymentGateTests</c> alongside
    /// <see cref="AnalyzeController"/>, which prices the identical way through the MVC pipeline.
    /// </summary>
    private static async Task AnalyzeAsync(HttpContext context, IX402PaymentGate gate)
    {
        var request = await context.Request.ReadFromJsonAsync<AnalyzeRequest>(context.RequestAborted)
            ?? new AnalyzeRequest(0);

        var result = await gate.RequireAsync(
            DynamicPricing.ForTokens(request.Tokens), cancellationToken: context.RequestAborted);

        if (!result.CanContinue)
        {
            await result.Result!.ExecuteAsync(context);
            return;
        }

        await context.Response.WriteAsync(
            $"analyzed {request.Tokens} tokens, settled in {result.SettledAsset?.Symbol}",
            context.RequestAborted);
    }

    /// <summary>
    /// A minimal endpoint pricing itself from the request's declared size — larger bodies cost
    /// more — without ever reading the body itself, so <c>PaymentGateTests</c> can post arbitrary
    /// content and observe only the demand.
    /// </summary>
    private static async Task BySizeAsync(HttpContext context, IX402PaymentGate gate)
    {
        var size = Math.Max(context.Request.ContentLength ?? 0, 1);
        var result = await gate.RequireAsync(
            DynamicPricing.ForBodySize(size), cancellationToken: context.RequestAborted);

        if (!result.CanContinue)
        {
            await result.Result!.ExecuteAsync(context);
            return;
        }

        await context.Response.WriteAsync("ok", context.RequestAborted);
    }

    /// <summary>
    /// Deliberately buggy: discards <c>PaymentGateResult.CanContinue</c> and serves content
    /// unconditionally, exactly the shape <c>PaymentGateTests</c> uses to prove the pipeline's own
    /// guard against an endpoint that ignores a refusal (see the hazard documented on
    /// <see cref="IX402PaymentGate.RequireAsync"/>).
    /// </summary>
    private static async Task IgnoringRefusalAsync(HttpContext context, IX402PaymentGate gate)
    {
        _ = await gate.RequireAsync(
            DynamicPricing.ForTokens(1), cancellationToken: context.RequestAborted);

        await context.Response.WriteAsync(
            "content that was never paid for", context.RequestAborted);
    }

    private sealed class RequestCounter
    {
        private int count;

        public int Count => count;

        public void Increment() => Interlocked.Increment(ref count);
    }

    /// <summary>A single mutable slot for <see cref="LastServerError"/> — plain reference
    /// assignment is atomic, so no lock is needed for a value only ever read after the request that
    /// wrote it has completed.</summary>
    private sealed class LastErrorHolder
    {
        public volatile string? Value;
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
