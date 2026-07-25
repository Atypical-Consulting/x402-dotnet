using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethereum.Signer.EIP712;
using Nethereum.Util;
using X402.Assets;
using X402.Client.Signing;
using X402.Json;
using X402.Networks;
using X402.Protocol;

namespace X402.TestKit;

/// <summary>
/// An in-process x402 facilitator for tests. Signatures are verified for real, so a test that
/// passes here proves the client's EIP-712 construction is correct, not merely well-plumbed.
/// </summary>
public sealed class FakeFacilitator : IAsyncDisposable
{
    private readonly IHost host;
    private readonly ConcurrentBag<string> settledNonces = [];
    private readonly object scenarioGate = new();
    private int verifyCallCount;
    private int settleCallCount;
    private int pendingFailureCalls;
    private FakeFacilitatorScenario pendingFailureScenario;
    private volatile string? lastRequestBody;

    /// <summary>Starts the facilitator on an in-memory transport.</summary>
    public FakeFacilitator()
    {
        host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddRoutingCore())
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/supported", () => Results.Json(Supported(), X402Json.Options));
                        // Cast to Delegate, not RequestDelegate: HandleVerifyAsync/HandleSettleAsync
                        // return Task<IResult>, which converts to RequestDelegate's plain Task by
                        // covariance. Left un-cast, MapPost binds that overload and silently discards
                        // the IResult instead of writing it to the response (ASP0016).
                        endpoints.MapPost("/verify", (Delegate)HandleVerifyAsync);
                        endpoints.MapPost("/settle", (Delegate)HandleSettleAsync);
                    });
                }))
            .Start();
    }

    /// <summary>How the facilitator behaves. Mutable between calls.</summary>
    public FakeFacilitatorScenario Scenario { get; set; } = FakeFacilitatorScenario.Valid;

    /// <summary>Asset addresses accepted when <see cref="Scenario"/> is <c>UnsupportedAsset</c>.</summary>
    public IReadOnlyList<string> SupportedAssets { get; set; } = [];

    /// <summary>Delay applied when <see cref="Scenario"/> is <c>Timeout</c>.</summary>
    public TimeSpan TimeoutDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Every nonce presented to <c>/settle</c>, duplicates included.</summary>
    public IReadOnlyList<string> SettledNonces => [.. settledNonces];

    /// <summary>Whether the same nonce was settled more than once.</summary>
    public bool HasDoubleSettled =>
        settledNonces.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);

    /// <summary>How many times <c>/verify</c> has been called.</summary>
    public int VerifyCallCount => verifyCallCount;

    /// <summary>How many times <c>/settle</c> has been called.</summary>
    public int SettleCallCount => settleCallCount;

    /// <summary>
    /// The raw JSON body of the most recent request that reached <c>/verify</c> or <c>/settle</c>,
    /// so a test can assert the wire shape rather than only the deserialized result. Unset by a
    /// forced-scenario call that returns before the body is read (<c>NetworkFailure</c>,
    /// <c>ServerError</c>, <c>Timeout</c>).
    /// </summary>
    public string? LastRequestBody => lastRequestBody;

    /// <summary>An <see cref="HttpClient"/> bound to this facilitator.</summary>
    public HttpClient CreateClient() => host.GetTestClient();

    /// <summary>
    /// The <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>'s message handler, for use as an
    /// <see cref="HttpClient"/>'s primary handler — the way a real <c>IHttpClientFactory</c>-built
    /// client would be wired against this facilitator in a test.
    /// </summary>
    public HttpMessageHandler CreateHandler() => host.GetTestServer().CreateHandler();

    /// <summary>
    /// Makes the next <paramref name="count"/> calls to <c>/verify</c> or <c>/settle</c> — combined,
    /// not per endpoint — behave as <paramref name="scenario"/>, then reverts to <see cref="Scenario"/>.
    /// </summary>
    public void FailNextCalls(int count, FakeFacilitatorScenario scenario)
    {
        lock (scenarioGate)
        {
            pendingFailureCalls = count;
            pendingFailureScenario = scenario;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
    }

    private static SupportedResponse Supported() => new()
    {
        Kinds =
        [
            new SupportedKind { Scheme = "exact", Network = KnownNetworks.BaseSepolia },
            new SupportedKind { Scheme = "exact", Network = KnownNetworks.BaseMainnet },
        ],
        Extensions = [],
        Signers = new Dictionary<string, IReadOnlyList<string>>
        {
            ["eip155:*"] = ["0x1234567890abcdef1234567890abcdef12345678"],
        },
    };

    private async Task<IResult> HandleVerifyAsync(HttpContext context)
    {
        Interlocked.Increment(ref verifyCallCount);
        var scenario = ConsumeScenario();

        if (scenario == FakeFacilitatorScenario.NetworkFailure)
        {
            context.Abort();
            return Results.Empty;
        }

        if (scenario == FakeFacilitatorScenario.ServerError)
        {
            return Results.StatusCode(500);
        }

        if (scenario == FakeFacilitatorScenario.Timeout)
        {
            await Task.Delay(TimeoutDelay, context.RequestAborted);
        }

        var request = await ReadRequestAsync(context);
        var reason = Validate(request, scenario);

        return Results.Json(
            reason is null
                ? new VerifyResponse { IsValid = true, Payer = PayerOf(request) }
                : new VerifyResponse { IsValid = false, InvalidReason = reason, Payer = PayerOf(request) },
            X402Json.Options);
    }

    private async Task<IResult> HandleSettleAsync(HttpContext context)
    {
        Interlocked.Increment(ref settleCallCount);
        var scenario = ConsumeScenario();

        if (scenario == FakeFacilitatorScenario.NetworkFailure)
        {
            context.Abort();
            return Results.Empty;
        }

        if (scenario == FakeFacilitatorScenario.ServerError)
        {
            return Results.StatusCode(500);
        }

        if (scenario == FakeFacilitatorScenario.Timeout)
        {
            await Task.Delay(TimeoutDelay, context.RequestAborted);
        }

        var request = await ReadRequestAsync(context);
        var authorization = request.PaymentPayload.AsExactEvm().Authorization;
        settledNonces.Add(authorization.Nonce);

        var reason = Validate(request, scenario);
        if (reason is not null || scenario == FakeFacilitatorScenario.SettleFailure)
        {
            return Results.Json(new SettleResponse
            {
                Success = false,
                ErrorReason = reason ?? X402ErrorReason.InvalidTransactionState,
                Payer = PayerOf(request),
                Transaction = "",
                Network = request.PaymentRequirements.Network,
            }, X402Json.Options);
        }

        return Results.Json(new SettleResponse
        {
            Success = true,
            Payer = PayerOf(request),
            // Deterministic hash derived from the nonce: reproducible, hence assertable.
            Transaction = "0x" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(authorization.Nonce))).ToLowerInvariant(),
            Network = request.PaymentRequirements.Network,
            Amount = request.PaymentRequirements.Amount,
        }, X402Json.Options);
    }

    /// <summary>Reads and records the raw body, then decodes it: <see cref="LastRequestBody"/> must
    /// reflect exactly what was on the wire, not a round trip through the decoded object.</summary>
    private async Task<FacilitatorRequest> ReadRequestAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        lastRequestBody = body;

        return System.Text.Json.JsonSerializer.Deserialize<FacilitatorRequest>(body, X402Json.Options)
            ?? throw new InvalidOperationException("The facilitator received an empty request body.");
    }

    /// <summary>Consumes one pending forced-scenario call if <see cref="FailNextCalls"/> armed one,
    /// otherwise falls back to <see cref="Scenario"/>.</summary>
    private FakeFacilitatorScenario ConsumeScenario()
    {
        lock (scenarioGate)
        {
            if (pendingFailureCalls > 0)
            {
                pendingFailureCalls--;
                return pendingFailureScenario;
            }
        }

        return Scenario;
    }

    private static string? PayerOf(FacilitatorRequest request)
    {
        try { return request.PaymentPayload.AsExactEvm().Authorization.From; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private string? Validate(FacilitatorRequest request, FakeFacilitatorScenario scenario)
    {
        // These forced-scenario overrides run before signature verification, which inverts the
        // order of the scheme document's phase 2 (signature, then validity window, then amount,
        // then recipient). That is fine — InsufficientFunds/UnsupportedAsset are harness overrides
        // for tests, not specification steps — but it means a test combining a forced scenario with
        // a tampered signature gets the forced error code back, not InvalidExactEvmPayloadSignature.
        if (scenario == FakeFacilitatorScenario.InsufficientFunds)
        {
            return X402ErrorReason.InsufficientFunds;
        }

        var requirements = request.PaymentRequirements;

        if (scenario == FakeFacilitatorScenario.UnsupportedAsset
            && !SupportedAssets.Contains(requirements.Asset, StringComparer.OrdinalIgnoreCase))
        {
            return X402ErrorReason.InvalidPaymentRequirements;
        }

        ExactEvmPayload exact;
        try { exact = request.PaymentPayload.AsExactEvm(); }
        catch (System.Text.Json.JsonException) { return X402ErrorReason.InvalidPayload; }

        var authorization = exact.Authorization;

        // 1. Signature: reconstruct the TypedData exactly as the payer signed it.
        var asset = AssetFor(requirements);
        var typedData = Eip3009TypedData.Build(requirements, authorization, asset);

        string recovered;
        try { recovered = new Eip712TypedDataSigner().RecoverFromSignatureV4(typedData, exact.Signature); }
        catch (Exception) { return X402ErrorReason.InvalidExactEvmPayloadSignature; }

        if (!recovered.IsTheSameAddress(authorization.From))
        {
            return X402ErrorReason.InvalidExactEvmPayloadSignature;
        }

        // 2. Validity window.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (long.Parse(authorization.ValidAfter) > now)
        {
            return X402ErrorReason.InvalidExactEvmPayloadAuthorizationValidAfter;
        }

        if (long.Parse(authorization.ValidBefore) < now)
        {
            return X402ErrorReason.InvalidExactEvmPayloadAuthorizationValidBefore;
        }

        // 3. Exact amount.
        if (authorization.Value != requirements.Amount)
        {
            return X402ErrorReason.InvalidExactEvmPayloadAuthorizationValueMismatch;
        }

        // 4. Recipient.
        if (!authorization.To.IsTheSameAddress(requirements.PayTo))
        {
            return X402ErrorReason.InvalidExactEvmPayloadRecipientMismatch;
        }

        return null;
    }

    private static AssetDescriptor AssetFor(PaymentRequirements requirements)
    {
        foreach (var candidate in KnownAssets.ForNetwork(requirements.Network))
        {
            if (candidate.Address.IsTheSameAddress(requirements.Asset))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"The fake facilitator has no profile for asset {requirements.Asset} on " +
            $"{requirements.Network}. Add it to KnownAssets or describe it in the test.");
    }
}
