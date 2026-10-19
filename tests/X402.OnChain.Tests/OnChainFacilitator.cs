using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethereum.ABI.FunctionEncoding;
using Nethereum.JsonRpc.Client;
using X402.Assets;
using X402.Json;
using X402.Networks;
using X402.Protocol;

namespace X402.OnChain.Tests;

/// <summary>
/// A test-only x402 facilitator that settles for real on the Anvil fork: <c>/verify</c>
/// simulates <c>transferWithAuthorization</c> with <c>eth_call</c>, <c>/settle</c> mines it and
/// reports the receipt. It holds a key (Anvil dev account #0) — a facilitator is a separate party
/// from the server, so ADR 0003 is untouched; this never ships.
/// </summary>
public sealed class OnChainFacilitator : IAsyncDisposable
{
    private readonly AnvilFork chain;
    private readonly IHost host;
    private int settleCallCount;

    public OnChainFacilitator(AnvilFork chain)
    {
        this.chain = chain;
        host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddRoutingCore())
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/supported", () => Results.Json(new SupportedResponse
                        {
                            Kinds = [new SupportedKind { Scheme = "exact", Network = KnownNetworks.BaseSepolia }],
                            Extensions = [],
                            Signers = new Dictionary<string, IReadOnlyList<string>> { ["eip155:*"] = [chain.FacilitatorAddress] },
                        }, X402Json.Options));
                        // Delegate cast: see FakeFacilitator (ASP0016).
                        endpoints.MapPost("/verify", (Delegate)HandleVerifyAsync);
                        endpoints.MapPost("/settle", (Delegate)HandleSettleAsync);
                    });
                }))
            .Start();
    }

    /// <summary>Runs inside <c>/settle</c>, before the transaction is sent: the test's lever on chain state.</summary>
    public Func<Task>? BeforeSettle { get; set; }

    /// <summary>The hash of the last transaction <c>/settle</c> mined, whatever its status.</summary>
    public string? LastSettleTransaction { get; private set; }

    public int SettleCallCount => settleCallCount;

    public HttpMessageHandler CreateHandler() => host.GetTestServer().CreateHandler();

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
    }

    private async Task<IResult> HandleVerifyAsync(HttpContext context)
    {
        var request = await ReadAsync(context);
        var payer = request.PaymentPayload.AsExactEvm().Authorization.From;

        try
        {
            await chain.SimulateAuthorizationAsync(AssetOf(request), request.PaymentPayload);
            return Results.Json(new VerifyResponse { IsValid = true, Payer = payer }, X402Json.Options);
        }
        catch (Exception e) when (e is RpcResponseException or SmartContractRevertException)
        {
            return Results.Json(new VerifyResponse { IsValid = false, InvalidReason = e.Message, Payer = payer }, X402Json.Options);
        }
    }

    private async Task<IResult> HandleSettleAsync(HttpContext context)
    {
        Interlocked.Increment(ref settleCallCount);
        var request = await ReadAsync(context);
        var payer = request.PaymentPayload.AsExactEvm().Authorization.From;

        await (BeforeSettle?.Invoke() ?? Task.CompletedTask);

        var receipt = await chain.SubmitAuthorizationAsync(AssetOf(request), request.PaymentPayload);
        LastSettleTransaction = receipt.TransactionHash;
        var mined = receipt.Status.Value == 1;

        return Results.Json(new SettleResponse
        {
            Success = mined,
            ErrorReason = mined ? null : X402ErrorReason.InvalidTransactionState,
            Transaction = receipt.TransactionHash,
            Network = request.PaymentRequirements.Network,
            Amount = mined ? request.PaymentRequirements.Amount : null,
            Payer = payer,
        }, X402Json.Options);
    }

    private static async Task<FacilitatorRequest> ReadAsync(HttpContext context) =>
        await context.Request.ReadFromJsonAsync<FacilitatorRequest>(X402Json.Options)
        ?? throw new InvalidOperationException("The facilitator received an empty request body.");

    private static AssetDescriptor AssetOf(FacilitatorRequest request) =>
        KnownAssets.ForNetwork(request.PaymentRequirements.Network)
            .Single(a => a.Address.Equals(request.PaymentRequirements.Asset, StringComparison.OrdinalIgnoreCase));
}
