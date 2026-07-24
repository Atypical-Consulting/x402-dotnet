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

    /// <summary>An <see cref="HttpClient"/> bound to this facilitator.</summary>
    public HttpClient CreateClient() => host.GetTestClient();

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
        if (Scenario == FakeFacilitatorScenario.NetworkFailure)
        {
            context.Abort();
            return Results.Empty;
        }

        if (Scenario == FakeFacilitatorScenario.Timeout)
        {
            await Task.Delay(TimeoutDelay, context.RequestAborted);
        }

        var request = await ReadRequestAsync(context);
        var reason = Validate(request);

        return Results.Json(
            reason is null
                ? new VerifyResponse { IsValid = true, Payer = PayerOf(request) }
                : new VerifyResponse { IsValid = false, InvalidReason = reason, Payer = PayerOf(request) },
            X402Json.Options);
    }

    private async Task<IResult> HandleSettleAsync(HttpContext context)
    {
        if (Scenario == FakeFacilitatorScenario.NetworkFailure)
        {
            context.Abort();
            return Results.Empty;
        }

        if (Scenario == FakeFacilitatorScenario.Timeout)
        {
            await Task.Delay(TimeoutDelay, context.RequestAborted);
        }

        var request = await ReadRequestAsync(context);
        var authorization = request.PaymentPayload.AsExactEvm().Authorization;
        settledNonces.Add(authorization.Nonce);

        var reason = Validate(request);
        if (reason is not null || Scenario == FakeFacilitatorScenario.SettleFailure)
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
            // Hash déterministe dérivé du nonce : reproductible, donc assertable.
            Transaction = "0x" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(authorization.Nonce))).ToLowerInvariant(),
            Network = request.PaymentRequirements.Network,
            Amount = request.PaymentRequirements.Amount,
        }, X402Json.Options);
    }

    private static async Task<FacilitatorRequest> ReadRequestAsync(HttpContext context) =>
        await context.Request.ReadFromJsonAsync<FacilitatorRequest>(X402Json.Options)
        ?? throw new InvalidOperationException("The facilitator received an empty request body.");

    private static string? PayerOf(FacilitatorRequest request)
    {
        try { return request.PaymentPayload.AsExactEvm().Authorization.From; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private string? Validate(FacilitatorRequest request)
    {
        if (Scenario == FakeFacilitatorScenario.InsufficientFunds)
        {
            return X402ErrorReason.InsufficientFunds;
        }

        var requirements = request.PaymentRequirements;

        if (Scenario == FakeFacilitatorScenario.UnsupportedAsset
            && !SupportedAssets.Contains(requirements.Asset, StringComparer.OrdinalIgnoreCase))
        {
            return X402ErrorReason.InvalidPaymentRequirements;
        }

        ExactEvmPayload exact;
        try { exact = request.PaymentPayload.AsExactEvm(); }
        catch (System.Text.Json.JsonException) { return X402ErrorReason.InvalidPayload; }

        var authorization = exact.Authorization;

        // 1. Signature : reconstruire le TypedData exactement comme le payeur l'a signé.
        var asset = AssetFor(requirements);
        var typedData = Eip3009TypedData.Build(requirements, authorization, asset);

        string recovered;
        try { recovered = new Eip712TypedDataSigner().RecoverFromSignatureV4(typedData, exact.Signature); }
        catch (Exception) { return X402ErrorReason.InvalidExactEvmPayloadSignature; }

        if (!recovered.IsTheSameAddress(authorization.From))
        {
            return X402ErrorReason.InvalidExactEvmPayloadSignature;
        }

        // 2. Fenêtre de validité.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (long.Parse(authorization.ValidAfter) > now)
        {
            return X402ErrorReason.InvalidExactEvmPayloadAuthorizationValidAfter;
        }

        if (long.Parse(authorization.ValidBefore) < now)
        {
            return X402ErrorReason.InvalidExactEvmPayloadAuthorizationValidBefore;
        }

        // 3. Montant exact.
        if (authorization.Value != requirements.Amount)
        {
            return X402ErrorReason.InvalidExactEvmPayloadAuthorizationValueMismatch;
        }

        // 4. Bénéficiaire.
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
