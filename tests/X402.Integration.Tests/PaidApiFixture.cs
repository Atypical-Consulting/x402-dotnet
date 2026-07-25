using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.DependencyInjection;
using X402.AspNetCore.Gate;
using X402.Assets;
using X402.Client;
using X402.Client.Signing;
using X402.Client.Spending;
using X402.Networks;
using X402.Pricing;
using X402.TestKit;
using X402.Transport;

namespace X402.Integration.Tests;

/// <summary>
/// A real paying agent (<see cref="X402.Client"/>) calling a real paid API
/// (<see cref="X402.AspNetCore"/>), both hosted in-process, in front of the real
/// <see cref="HttpFacilitatorClient"/> pointed at <see cref="FakeFacilitator"/> — the only
/// simulated half of the chain, and it verifies EIP-3009 signatures for real.
/// </summary>
/// <remarks>
/// <para>
/// The server side is hosted on a <see cref="TestServer"/> exactly the way
/// <c>X402.AspNetCore.Tests.PaidServerFixture</c> already does: the real
/// <see cref="X402ApplicationBuilderExtensions.UseX402"/> middleware, the real imperative gate,
/// and the two named facilitator <see cref="HttpClient"/>s ("x402-verify"/"x402-settle") pointed
/// at <see cref="FakeFacilitator.CreateHandler"/> in-process.
/// </para>
/// <para>
/// The client side is built the same way a real application would build it, just without
/// <c>IHttpClientFactory</c> in between: a real <see cref="X402PaymentHandler"/>, carrying a real
/// <see cref="PrivateKeyPaymentSigner"/> and a real <see cref="InMemorySpendTracker"/>, is
/// installed as a <see cref="DelegatingHandler"/> whose <see cref="DelegatingHandler.InnerHandler"/>
/// is the very same <see cref="TestServer"/>'s own message handler
/// (<see cref="TestServer.CreateHandler"/>) — the same in-process transport
/// <see cref="TestServer.GetTestClient"/> uses elsewhere in this suite. A thin recording handler
/// sits between the two so <see cref="LastPaymentSignature"/> can observe exactly the bytes that
/// were sent on the wire, including the one that never gets returned to the caller (the initial,
/// unpaid attempt that provokes the 402).
/// </para>
/// <para>Routes mounted: <c>/premium</c> (EURC 0.010 then USDC 0.011, priced via the route table)
/// and <c>POST /analyze</c> (0.001 EURC/USDC per requested token, priced from inside the handler
/// through <see cref="IX402PaymentGate"/>).</para>
/// </remarks>
public sealed class PaidApiFixture : IAsyncDisposable
{
    private readonly IHost host;
    private readonly ContentServedCounter servedContent;
    private readonly LastSignatureHolder lastSignature;

    private PaidApiFixture(
        IHost host, FakeFacilitator facilitator, HttpClient agent, HttpClient rawClient,
        ContentServedCounter servedContent, LastSignatureHolder lastSignature)
    {
        this.host = host;
        Facilitator = facilitator;
        Agent = agent;
        RawClient = rawClient;
        this.servedContent = servedContent;
        this.lastSignature = lastSignature;
    }

    /// <summary>
    /// The paying agent: a real <see cref="HttpClient"/> with <see cref="X402PaymentHandler"/> in
    /// front of the API's own <see cref="TestServer"/> transport. Every call through this client
    /// pays for a 402 on its own, exactly as it would against a real server.
    /// </summary>
    public HttpClient Agent { get; }

    /// <summary>
    /// A plain <see cref="HttpClient"/> bound to the same <see cref="TestServer"/>, carrying no
    /// payment handler — for a test that needs to send a hand-built request, such as replaying a
    /// captured <c>PAYMENT-SIGNATURE</c> header the way a malicious client would.
    /// </summary>
    public HttpClient RawClient { get; }

    /// <summary>The in-process facilitator every request is verified and settled against.</summary>
    public FakeFacilitator Facilitator { get; }

    /// <summary>
    /// How many times <c>/premium</c>'s content was actually delivered to a caller — that is, the
    /// request finished with <c>200 OK</c>, not withheld behind a refused or failed settlement.
    /// Counted after the whole x402 pipeline (including settlement) has decided the final status
    /// code, so a settlement failure that discards an already-buffered response is correctly
    /// counted as never served.
    /// </summary>
    public int ServedContentCount => servedContent.Count;

    /// <summary>
    /// The <c>PAYMENT-SIGNATURE</c> header value most recently sent on the wire by
    /// <see cref="Agent"/> — including a payment that was itself refused. <c>null</c> until a
    /// request carrying one has actually been sent.
    /// </summary>
    public string? LastPaymentSignature => lastSignature.Value;

    /// <summary>Starts the API, its facilitator, and an agent configured to pay it.</summary>
    /// <param name="configureAgent">
    /// When omitted, the agent gets generous default limits in both EURC and USDC (enough to pay
    /// <c>/premium</c> and <c>/analyze</c> either way) and no asset preference, so the server's own
    /// euro-first order governs. When supplied, it replaces those defaults entirely — the agent
    /// starts with no declared limits beyond what this callback sets, so a test can make one asset
    /// genuinely unpayable (see <c>An_agent_that_only_holds_dollars_still_pays_a_euro_first_server</c>).
    /// </param>
    /// <param name="configureServer">Further overrides applied after the server's defaults.</param>
    public static Task<PaidApiFixture> StartAsync(
        Action<X402ClientOptions>? configureAgent = null,
        Action<X402Options>? configureServer = null)
    {
        var facilitator = new FakeFacilitator();
        var servedContent = new ContentServedCounter();

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

                    services.AddX402(options =>
                    {
                        options.PayTo = TestData.PayeeAddress;
                        options.Network = KnownNetworks.BaseSepolia;
                        // No path segment: FakeFacilitator mounts /verify, /settle and /supported
                        // at the root.
                        options.FacilitatorUrl = new Uri("https://x402.example/");
                        options.Assets.Add(new AssetConfiguration { Symbol = "EURC" });
                        options.Assets.Add(new AssetConfiguration { Symbol = "USDC" });
                        configureServer?.Invoke(options);
                    });

                    // Route the two named facilitator clients AddX402 already configured at the
                    // fake facilitator in-process — the same pattern PaidServerFixture uses.
                    services.AddHttpClient("x402-verify")
                        .ConfigurePrimaryHttpMessageHandler(() => facilitator.CreateHandler());
                    services.AddHttpClient("x402-settle")
                        .ConfigurePrimaryHttpMessageHandler(() => facilitator.CreateHandler());
                })
                .Configure(app =>
                {
                    // Counts content actually delivered, not merely produced: the x402 pipeline
                    // buffers the response and can still replace 200 with 402 after the endpoint
                    // returns (a failed settlement), so this must run after the whole pipeline —
                    // UseX402 included — has decided the final status code.
                    app.Use(async (context, next) =>
                    {
                        await next(context);
                        if (context.Request.Path == "/premium" &&
                            context.Response.StatusCode == StatusCodes.Status200OK)
                        {
                            servedContent.Increment();
                        }
                    });

                    // Only /premium is priced through the route table. POST /analyze is priced
                    // from inside its own handler, through IX402PaymentGate — see AnalyzeAsync —
                    // so the dynamic-pricing scenario exercises a real client against the
                    // imperative gate, not the route table.
                    app.UseX402(routeBuilder => routeBuilder.Map("/premium", premiumPrices));

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/premium", () => Results.Text("premium content"));
                        endpoints.MapPost("/analyze", AnalyzeAsync);
                    });
                }))
            .Start();

        var testServer = host.GetTestServer();
        var rawClient = host.GetTestClient();

        var agentOptions = new X402ClientOptions();
        agentOptions.AllowedNetworks.Add(KnownNetworks.BaseSepolia);

        if (configureAgent is null)
        {
            agentOptions.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 1m, perSession: 10m);
            agentOptions.SetLimits(KnownAssets.UsdcBaseSepolia, perRequest: 1m, perSession: 10m);
        }
        else
        {
            configureAgent(agentOptions);
        }

        var signer = new PrivateKeyPaymentSigner(TestData.PayerPrivateKey);
        var spendTracker = new InMemorySpendTracker(agentOptions);
        var lastSignature = new LastSignatureHolder();

        // The fiddly part: X402PaymentHandler is a DelegatingHandler and needs somewhere real to
        // send to. TestServer.CreateHandler() is that "somewhere real" — the same in-process
        // transport TestServer.GetTestClient() itself uses — so it becomes X402PaymentHandler's
        // InnerHandler, with a thin recording handler between the two so LastPaymentSignature can
        // observe every request that actually reached the wire, not just the one the caller sees
        // returned.
        var paymentHandler = new X402PaymentHandler(agentOptions, signer, spendTracker)
        {
            InnerHandler = new SignatureCapturingHandler(lastSignature)
            {
                InnerHandler = testServer.CreateHandler(),
            },
        };

        var agent = new HttpClient(paymentHandler) { BaseAddress = testServer.BaseAddress };

        return Task.FromResult(new PaidApiFixture(
            host, facilitator, agent, rawClient, servedContent, lastSignature));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Agent.Dispose();
        RawClient.Dispose();
        await host.StopAsync();
        host.Dispose();
        await Facilitator.DisposeAsync();
    }

    /// <summary>
    /// Prices itself from the request body through <see cref="IX402PaymentGate"/> — 0.001 EURC
    /// (and USDC) per token — the same shape as
    /// <c>X402.AspNetCore.Tests.PaidServerFixture.AnalyzeAsync</c>, kept as its own copy here so
    /// this project asserts on it without depending on another test assembly's internals.
    /// </summary>
    private static async Task AnalyzeAsync(HttpContext context, IX402PaymentGate gate)
    {
        var request = await context.Request.ReadFromJsonAsync<AnalyzeRequest>(context.RequestAborted)
            ?? new AnalyzeRequest(0);

        var prices = new PriceSet([
            Price.For(KnownAssets.EurcBaseSepolia, 0.001m * request.Tokens),
            Price.For(KnownAssets.UsdcBaseSepolia, 0.001m * request.Tokens),
        ]);

        var result = await gate.RequireAsync(prices, cancellationToken: context.RequestAborted);

        if (!result.CanContinue)
        {
            await result.Result!.ExecuteAsync(context);
            return;
        }

        await context.Response.WriteAsync(
            $"analyzed {request.Tokens} tokens, settled in {result.SettledAsset?.Symbol}",
            context.RequestAborted);
    }

    private sealed record AnalyzeRequest(int Tokens);

    private sealed class ContentServedCounter
    {
        private int count;

        public int Count => count;

        public void Increment() => Interlocked.Increment(ref count);
    }

    /// <summary>A single mutable slot for <see cref="LastPaymentSignature"/> — plain reference
    /// assignment is atomic, so no lock is needed for a value only ever read after the request
    /// that wrote it has completed.</summary>
    private sealed class LastSignatureHolder
    {
        public volatile string? Value;
    }

    /// <summary>
    /// Records the <c>PAYMENT-SIGNATURE</c> header of every request that reaches the wire, then
    /// passes it straight through. Placed as <see cref="X402PaymentHandler"/>'s
    /// <see cref="DelegatingHandler.InnerHandler"/>, so it sees both the initial, unpaid attempt
    /// (no header) and the single paid replay (header present) — never anything after
    /// <see cref="X402PaymentHandler"/> has already decided to stop.
    /// </summary>
    private sealed class SignatureCapturingHandler(LastSignatureHolder holder) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues(X402Headers.PaymentSignature, out var values))
            {
                holder.Value = values.FirstOrDefault();
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
