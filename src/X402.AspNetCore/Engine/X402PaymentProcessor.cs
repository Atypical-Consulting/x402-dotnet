using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.Facilitator;
using X402.AspNetCore.Idempotency;
using X402.AspNetCore.Middleware;
using X402.Assets;
using X402.Billing;
using X402.Pricing;
using X402.Protocol;
using X402.Transport;

namespace X402.AspNetCore.Engine;

/// <summary>
/// The one place a payment is decided. Route pricing and the imperative gate both drive this;
/// there is deliberately no second implementation of the flow.
/// </summary>
internal sealed class X402PaymentProcessor(
    IOptions<X402Options> options,
    IResolvedAssets assets,
    IFacilitatorClient facilitator,
    ISettlementLedger ledger,
    IPaymentEventSink events,
    TimeProvider time,
    ILogger<X402PaymentProcessor> logger)
{
    private readonly X402Options settings = options.Value;

    /// <summary>
    /// Decides whether the request may proceed. On refusal the returned attempt carries a
    /// ready-to-return 402 (or a 409 for a settlement already in flight); on acceptance it carries
    /// what settlement will need.
    /// </summary>
    public async Task<PaymentAttempt> AuthorizeAsync(
        HttpContext context, PriceSet prices, ResourceInfoOverrides? overrides,
        CancellationToken cancellationToken)
    {
        var resource = BuildResourceInfo(context, overrides);
        var offered = prices.Select(p => BuildRequirements(p, resource)).ToList();

        // 1. Read the proof. Absent or unreadable: demand payment.
        var header = context.Request.Headers[X402Headers.PaymentSignature].ToString();
        if (!X402Codec.TryDecode<PaymentPayload>(header, out var payload, out var decodeError))
        {
            await RecordAsync(PaymentEventStatus.PaymentRequired, offered[0], resource, null,
                decodeError, cancellationToken);
            return PaymentAttempt.Refused(Demand(resource, offered, decodeError));
        }

        // 2. Match the proof against ONE of OUR requirements. The `accepted` field comes from the
        //    client: it is used only to choose, never to define what gets verified.
        var matched = Match(offered, payload!);
        if (matched is null)
        {
            const string reason =
                "the payment names a scheme, network or asset this resource does not accept";
            await RecordAsync(PaymentEventStatus.VerificationFailed, offered[0], resource,
                payload!.Accepted.Asset, reason, cancellationToken);
            return PaymentAttempt.Refused(Demand(resource, offered, reason));
        }

        var (requirements, asset) = matched.Value;

        // 3. Have OUR requirement verified.
        VerifyResponse verify;
        try
        {
            verify = await facilitator.VerifyAsync(payload!, requirements, cancellationToken);
        }
        catch (FacilitatorException exception)
        {
            logger.LogError(exception, "x402 verification could not reach the facilitator");
            await RecordAsync(PaymentEventStatus.VerificationFailed, requirements, resource, null,
                X402ErrorReason.UnexpectedVerifyError, cancellationToken);
            return PaymentAttempt.Refused(
                Demand(resource, offered, X402ErrorReason.UnexpectedVerifyError));
        }

        if (!verify.IsValid)
        {
            await RecordAsync(PaymentEventStatus.VerificationFailed, requirements, resource,
                verify.Payer, verify.InvalidReason, cancellationToken);
            return PaymentAttempt.Refused(Demand(resource, offered, verify.InvalidReason));
        }

        await RecordAsync(PaymentEventStatus.Verified, requirements, resource, verify.Payer, null,
            cancellationToken);

        // 4. Idempotency guard, before any execution.
        var authorization = payload!.AsExactEvm().Authorization;
        var identity = new PaymentIdentity(requirements.Network, requirements.Asset, authorization.Nonce);
        var slot = await ledger.AcquireAsync(identity, cancellationToken);

        return slot.State switch
        {
            SettlementSlotState.AlreadySettled => PaymentAttempt.AlreadySettled(
                payload!, requirements, asset, verify.Payer, identity, slot.Existing!, resource, offered),
            SettlementSlotState.InFlight => PaymentAttempt.Conflict(
                "this authorization is being settled by another request"),
            _ => PaymentAttempt.Accepted(payload!, requirements, asset, verify.Payer, identity, resource, offered),
        };
    }

    /// <summary>Settles an accepted attempt and writes the settlement header.</summary>
    public async Task<bool> SettleAsync(
        HttpContext context, PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        // An authorization already settled never goes back to the facilitator: replay the response.
        if (attempt.MemorisedSettlement is { } memorised)
        {
            context.Response.Headers[X402Headers.PaymentResponse] = X402Codec.Encode(memorised);
            return memorised.Success;
        }

        SettleResponse settle;
        try
        {
            settle = await facilitator.SettleAsync(
                attempt.Payload!, attempt.Requirements!, cancellationToken);
        }
        catch (FacilitatorException exception)
        {
            logger.LogError(exception, "x402 settlement could not reach the facilitator");
            // Settlement provably did not happen: the authorization is still valid on-chain, so
            // release it for a retry rather than leaving it stuck as in-flight forever.
            await ledger.AbandonAsync(attempt.Identity, cancellationToken);
            await RecordAsync(PaymentEventStatus.SettlementFailed, attempt.Requirements!,
                attempt.Demand?.Resource, attempt.Payer, X402ErrorReason.UnexpectedSettleError,
                cancellationToken);
            return false;
        }

        // CompleteAsync never throws: it persists whatever it is handed, inserting the entry if it
        // was pruned in the meantime. No defensive try/catch belongs here.
        await ledger.CompleteAsync(attempt.Identity, settle, cancellationToken);
        context.Response.Headers[X402Headers.PaymentResponse] = X402Codec.Encode(settle);

        await RecordAsync(
            settle.Success ? PaymentEventStatus.Settled : PaymentEventStatus.SettlementFailed,
            attempt.Requirements!, attempt.Demand?.Resource, settle.Payer ?? attempt.Payer,
            settle.Success ? null : settle.ErrorReason, cancellationToken,
            settle.Transaction);

        return settle.Success;
    }

    private ResourceInfo BuildResourceInfo(HttpContext context, ResourceInfoOverrides? overrides)
    {
        var request = context.Request;
        return new ResourceInfo
        {
            Url = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}",
            Description = overrides?.Description,
            MimeType = overrides?.MimeType,
            ServiceName = settings.ServiceName,
            Tags = settings.Tags.Count > 0 ? [.. settings.Tags] : null,
            IconUrl = settings.IconUrl,
        };
    }

    private PaymentRequirements BuildRequirements(Price price, ResourceInfo resource) => new()
    {
        Scheme = "exact",
        Network = price.Asset.Network,
        Amount = price.AtomicAmount,
        Asset = price.Asset.Address,
        PayTo = settings.PayTo,
        MaxTimeoutSeconds = settings.MaxTimeoutSeconds,
        // The token's EIP-712 domain: without it the payer cannot sign correctly. A plain
        // Dictionary<string, string> is not a root type of X402JsonContext (that source-generated
        // context only covers the protocol's own record types), so serializing it through
        // X402Json.Options throws NotSupportedException at run time — the same gap TestData hit
        // building requirements for tests. Use the reflection-based default options for this one
        // ad-hoc shape instead.
        Extra = System.Text.Json.JsonSerializer.SerializeToElement(
            new Dictionary<string, string>
            {
                ["name"] = price.Asset.Eip712Name,
                ["version"] = price.Asset.Eip712Version,
            }, System.Text.Json.JsonSerializerOptions.Default),
    };

    private (PaymentRequirements Requirements, AssetDescriptor Asset)? Match(
        IReadOnlyList<PaymentRequirements> offered, PaymentPayload payload)
    {
        foreach (var requirement in offered)
        {
            if (!string.Equals(requirement.Scheme, payload.Accepted.Scheme, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(requirement.Network, payload.Accepted.Network, StringComparison.Ordinal))
            {
                continue;
            }

            if (!EvmAddress.AreEqual(requirement.Asset, payload.Accepted.Asset))
            {
                continue;
            }

            if (assets.TryGetByAddress(requirement.Asset, out var asset))
            {
                return (requirement, asset);
            }
        }

        return null;
    }

    private static PaymentRequiredResult Demand(
        ResourceInfo resource, IReadOnlyList<PaymentRequirements> offered, string? error) =>
        new(new PaymentRequired { Error = error, Resource = resource, Accepts = offered });

    /// <summary>
    /// Records one payment event. Never lets a sink failure fail the request: billing must never
    /// undo a payment that has already settled on-chain (see <see cref="IPaymentEventSink"/>).
    /// </summary>
    private async Task RecordAsync(
        PaymentEventStatus status, PaymentRequirements requirements, ResourceInfo? resource,
        string? payer, string? failureReason, CancellationToken cancellationToken,
        string? transaction = null)
    {
        try
        {
            await events.RecordAsync(new PaymentEvent
            {
                Timestamp = time.GetUtcNow(),
                // The resource is the URL that was paid for, not the payee address: PayTo already
                // has its own field below.
                Resource = resource?.Url ?? "",
                Amount = requirements.Amount,
                Asset = requirements.Asset,
                Network = requirements.Network,
                Status = status,
                Payer = payer,
                FailureReason = failureReason,
                Transaction = transaction,
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            // Billing must never fail a payment that has already settled on-chain.
            logger.LogError(exception, "x402 payment event sink threw; the payment is unaffected");
        }
    }
}
