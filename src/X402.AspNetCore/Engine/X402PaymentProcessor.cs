using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
/// The one place a payment is decided, and the one place its outbound half — installing response
/// buffering, settling, then deciding to deliver or withhold — runs. Route pricing
/// (<see cref="Middleware.X402Middleware"/>) and the imperative gate
/// (<c>IX402PaymentGate.RequireAsync</c>) both drive this; there is deliberately no second
/// implementation of either half of the flow.
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
            // The payer is unknown before verification: nothing client-supplied goes in its place.
            await RecordAsync(PaymentEventStatus.VerificationFailed, offered[0], resource, null,
                reason, cancellationToken);
            return PaymentAttempt.Refused(Demand(resource, offered, reason));
        }

        var (requirements, asset) = matched.Value;

        // 2b. The ledger key must be canonical: a bytes32 nonce written in another hex case is the
        //     same authorization, and must not claim a second slot (and run the endpoint again).
        if (!TryReadCanonicalNonce(payload!, out var nonce))
        {
            await RecordAsync(PaymentEventStatus.VerificationFailed, requirements, resource, null,
                X402ErrorReason.InvalidPayload, cancellationToken);
            return PaymentAttempt.Refused(Demand(resource, offered, X402ErrorReason.InvalidPayload));
        }

        // 2c. The ledger remembers a settlement for max(10 min, MaxTimeoutSeconds): an authorization
        //     valid for longer could be presented again once it is forgotten and run the endpoint
        //     twice. Refuse it here (30 s of clock skew allowed) so that retention is a guarantee.
        if (!long.TryParse(payload!.AsExactEvm().Authorization.ValidBefore, out var validBefore)
            || validBefore > time.GetUtcNow().ToUnixTimeSeconds() + requirements.MaxTimeoutSeconds + 30)
        {
            await RecordAsync(PaymentEventStatus.VerificationFailed, requirements, resource, null,
                X402ErrorReason.InvalidPayload, cancellationToken);
            return PaymentAttempt.Refused(Demand(resource, offered, X402ErrorReason.InvalidPayload));
        }

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
        var identity = new PaymentIdentity(requirements.Network, requirements.Asset, nonce);
        var slot = await ledger.AcquireAsync(identity, cancellationToken);

        switch (slot.State)
        {
            case SettlementSlotState.InFlight:
                return PaymentAttempt.Conflict("this authorization is being settled by another request");

            case SettlementSlotState.AlreadySettled:
                // A settled authorization re-delivers only what it paid for: the EIP-3009
                // signature does not cover the resource, so without this check one payment would
                // unlock every route priced the same. A failed settlement is refused outright,
                // without running the endpoint (and its side effects) again.
                var memorised = slot.Existing!;
                var refusal =
                    !memorised.Success ? memorised.ErrorReason ?? X402ErrorReason.UnexpectedSettleError
                    : !string.Equals(slot.Resource, LedgerResource(context, resource), StringComparison.Ordinal)
                        ? "this authorization already paid for a different resource"
                    : null;
                if (refusal is not null)
                {
                    await RecordAsync(PaymentEventStatus.VerificationFailed, requirements, resource,
                        verify.Payer, refusal, cancellationToken);
                    return PaymentAttempt.Refused(Demand(resource, offered, refusal));
                }

                return PaymentAttempt.AlreadySettled(
                    payload!, requirements, asset, verify.Payer, identity, memorised, resource, offered);

            default:
                return PaymentAttempt.Accepted(
                    payload!, requirements, asset, verify.Payer, identity, resource, offered);
        }
    }

    /// <summary>
    /// Reads the exact/EVM nonce and canonicalises it (0x plus 64 lowercase hex digits). False for a
    /// payload that is not an exact/EVM payload at all, or whose nonce is not a bytes32.
    /// </summary>
    private static bool TryReadCanonicalNonce(PaymentPayload payload, out string nonce)
    {
        // Stryker disable once String : the placeholder is only read when this returns true, after it is overwritten
        nonce = "";
        string raw;
        try
        {
            raw = payload.AsExactEvm().Authorization.Nonce;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        if (raw.Length != 66 || !raw.StartsWith("0x", StringComparison.Ordinal)
            || raw.AsSpan(2).ContainsAnyExcept("0123456789abcdefABCDEF"))
        {
            return false;
        }

        nonce = raw.ToLowerInvariant();
        return true;
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
            // Released for a retry rather than left in flight. Not proof settlement did not
            // happen: a timeout or a connection reset after the request went out is ambiguous, and
            // the facilitator may have broadcast anyway. A retry then fails on the used nonce, so
            // this can withhold content that was in fact paid for — never deliver content unpaid.
            attempt.SettlementError = X402ErrorReason.UnexpectedSettleError;
            await ledger.AbandonAsync(attempt.Identity, cancellationToken);
            await RecordAsync(PaymentEventStatus.SettlementFailed, attempt.Requirements!,
                attempt.Demand?.Resource, attempt.Payer, X402ErrorReason.UnexpectedSettleError,
                cancellationToken);
            return false;
        }

        // CompleteAsync never throws: it persists whatever it is handed, inserting the entry if it
        // was pruned in the meantime. No defensive try/catch belongs here.
        await ledger.CompleteAsync(
            attempt.Identity, settle, LedgerResource(context, attempt.Demand!.Resource), cancellationToken);
        context.Response.Headers[X402Headers.PaymentResponse] = X402Codec.Encode(settle);
        if (!settle.Success)
        {
            attempt.SettlementError = settle.ErrorReason ?? X402ErrorReason.UnexpectedSettleError;
        }

        await RecordAsync(
            settle.Success ? PaymentEventStatus.Settled : PaymentEventStatus.SettlementFailed,
            attempt.Requirements!, attempt.Demand?.Resource, settle.Payer ?? attempt.Payer,
            settle.Success ? null : settle.ErrorReason, cancellationToken,
            settle.Transaction);

        return settle.Success;
    }

    /// <summary>
    /// Installs response buffering for an accepted attempt and records it on
    /// <paramref name="feature"/> — the single place this happens, called both when a route prices
    /// the request ahead of time (<see cref="Middleware.X402Middleware"/>) and when
    /// <c>IX402PaymentGate.RequireAsync</c> opens one late, from inside the handler. Whichever side
    /// called this, <see cref="FinishAsync"/> reads the same <paramref name="feature"/> once the
    /// handler returns, without needing to know which one it was.
    /// </summary>
    internal BufferingResponseBodyFeature OpenBuffering(
        HttpContext context, X402RequestFeature feature, PaymentAttempt attempt)
    {
        // Settlement never runs on the client's connection token: a disconnect mid-settle would
        // otherwise leave the authorization neither completed nor abandoned in the ledger.
        var buffering = InstallBuffering(context, feature, async _ =>
        {
            // Same rule as FinishAsync: an error response is never charged, even past the cap.
            // True lets it stream through uncharged.
            if (context.Response.StatusCode >= 400 && attempt.MemorisedSettlement is null)
            {
                await ledger.AbandonAsync(attempt.Identity, CancellationToken.None);
                return true;
            }

            return await SettleAsync(context, attempt, CancellationToken.None);
        });
        feature.Attempt = attempt;
        return buffering;
    }

    /// <summary>
    /// Installs response buffering for a <em>refused</em> imperative-gate attempt — the same
    /// mechanism <see cref="OpenBuffering"/> installs for an accepted one, and for the same reason:
    /// without it, an endpoint that ignores <c>PaymentGateResult.CanContinue</c> and writes a
    /// response anyway would reach the real transport directly, and nothing downstream could tell —
    /// let alone withhold it — once the response has started. Deliberately does not set
    /// <paramref name="feature"/>'s <see cref="X402RequestFeature.Attempt"/>: that stays null, which
    /// is what the rest of this class and <see cref="Middleware.X402Middleware"/> rely on to mean
    /// "no payment was accepted, nothing to settle." <see cref="Middleware.X402Middleware"/> reads
    /// <see cref="X402RequestFeature.Refusal"/> and this same buffer once the handler returns, to
    /// decide whether to release what was written or withhold it.
    /// </summary>
    internal BufferingResponseBodyFeature OpenRefusalBuffering(
        HttpContext context, X402RequestFeature feature, PaymentAttempt refusal)
    {
        // No settlement is possible for a refusal, so there is nothing a cap-crossing write could
        // hand off to — any overflow is simply unrecoverable here, same as a failed settlement
        // would be for an accepted attempt.
        var buffering = InstallBuffering(context, feature, _ => Task.FromResult(false));
        feature.Refusal = refusal;
        return buffering;
    }

    private BufferingResponseBodyFeature InstallBuffering(
        HttpContext context, X402RequestFeature feature, Func<CancellationToken, Task<bool>> onOverflowAsync)
    {
        var original = context.Features.Get<IHttpResponseBodyFeature>()!;
        var buffering = new BufferingResponseBodyFeature(
            original, settings.MaxBufferedResponseBytes, onOverflowAsync);

        feature.Buffer = buffering;
        feature.OriginalBody = original;
        context.Features.Set<IHttpResponseBodyFeature>(buffering);

        return buffering;
    }

    /// <summary>
    /// The outbound half of the flow, once the endpoint has returned without throwing: decides
    /// whether the buffered response may be delivered. Called once per request, from
    /// <see cref="Middleware.X402Middleware"/>, for an attempt opened either by a route match or by
    /// the imperative gate — this is the single implementation of that decision, so the two paths
    /// cannot diverge.
    /// </summary>
    internal async Task FinishAsync(
        HttpContext context, PaymentAttempt attempt, BufferingResponseBodyFeature buffering)
    {
        // Bytes still held by a PipeWriter the endpoint never flushed are not in the buffer yet.
        // Push them in first, so the decision below sees the whole response — and so
        // FlushBufferAsync cannot cross the cap, and settle a second time, after settling here.
        try
        {
            await buffering.FlushWriterAsync(CancellationToken.None);
        }
        catch (BufferingSettlementFailedException)
        {
            // The flush crossed the cap and that settlement failed: Poisoned, handled below.
        }

        if (buffering.Poisoned)
        {
            // BufferingSettlementFailedException was thrown at the cap and swallowed somewhere
            // inside the handler (a broad catch around writes, tolerating e.g. a client disconnect,
            // is a common streaming pattern) instead of propagating out to the middleware. Consult
            // the flag directly rather than trust that every handler propagates it: settlement was
            // attempted and failed, so falling through to SettleAsync below would settle — and
            // charge — a second time for the same authorization.
            // Stryker disable once Statement : redundant today, BufferingResponseBodyFeature.CheckCapacityAsync discards the buffer as it poisons it
            buffering.Discard();
            // Not Headers.Clear(): SettleAsync is the buffer's own overflow hook (OpenBuffering
            // wires it as onOverflowAsync), so reaching this branch means it already ran at the cap
            // and returned false — and unless it caught a FacilitatorException it set
            // PAYMENT-RESPONSE on the way, the payer's only evidence of what settlement reported.
            // Same reason as the settlement-failed branch below; unlike the refusal path, where no
            // settlement is possible at all and clearing is safe. What does need resetting is
            // Content-Length: the endpoint may already have set it for the body it never got to
            // finish writing, and left as-is that stale value survives onto the 2-byte {}
            // PaymentRequiredResult writes below, where a real transport (Kestrel, unlike
            // TestServer) aborts the response trying to satisfy it.
            context.Response.ContentLength = null;
            await new PaymentRequiredResult(attempt.SettlementFailedDemand).ExecuteAsync(context);
            return;
        }

        if (buffering.Overflowed)
        {
            // Settlement already happened when the cap was crossed.
            // Stryker disable once Statement : the host completes the response itself once the pipeline returns
            await buffering.CompleteAsync();
            return;
        }

        if (context.Response.StatusCode >= 400)
        {
            // The endpoint answered with an error, not with what was paid for: deliver the error
            // and do not charge for it. The authorization stays usable for a retry.
            if (attempt.MemorisedSettlement is null)
            {
                await ledger.AbandonAsync(attempt.Identity, CancellationToken.None);
            }

            await buffering.FlushBufferAsync(context.RequestAborted);
            return;
        }

        if (context.RequestAborted.IsCancellationRequested && attempt.MemorisedSettlement is null)
        {
            // The client is already gone: nothing could be delivered, so do not charge for it.
            // Settlement that has started still runs to completion (CancellationToken.None).
            await ledger.AbandonAsync(attempt.Identity, CancellationToken.None);
            // Stryker disable once Statement : the client is gone and nothing flushes this buffer; Discard only frees it early
            buffering.Discard();
            return;
        }

        var settled = await SettleAsync(context, attempt, CancellationToken.None);
        if (settled)
        {
            await buffering.FlushBufferAsync(context.RequestAborted);
            return;
        }

        // Settlement failed within the cap: the buffered content is discarded, never delivered.
        // Stryker disable once Statement : nothing flushes this buffer on the failure path; Discard only frees it early
        buffering.Discard();
        // Same stale-Content-Length hazard as the Poisoned branch above — see its comment. Not
        // Headers.Clear() here: when the facilitator responded with a failure rather than being
        // unreachable, SettleAsync already set PAYMENT-RESPONSE on this same context
        // (context.Response.Headers[X402Headers.PaymentResponse], above) — the payer's only
        // evidence that settlement was attempted and what it reported — and clearing the whole
        // header collection would destroy it. (When SettleAsync instead caught a
        // FacilitatorException, no such header was ever set — but the fix stays scoped to
        // Content-Length regardless, since a blanket Headers.Clear() here would also strip
        // anything outer middleware, CORS or otherwise, wrote directly onto this response.)
        context.Response.ContentLength = null;
        await new PaymentRequiredResult(attempt.SettlementFailedDemand).ExecuteAsync(context);
    }

    /// <summary>
    /// What a settled authorization is bound to in the ledger: the resource URL plus the query
    /// string, so a replay with other query parameters is not the same paid request. The request
    /// body is not part of it.
    /// </summary>
    private static string LedgerResource(HttpContext context, ResourceInfo resource) =>
        resource.Url + context.Request.QueryString;

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
        // The token's EIP-712 domain: without it the payer cannot sign correctly. Dictionary<string,
        // string> is registered on X402JsonContext (see X402Json.cs) precisely for this ad-hoc
        // shape, so this goes through the same source-generated options as every other protocol object.
        Extra = System.Text.Json.JsonSerializer.SerializeToElement(
            new Dictionary<string, string>
            {
                ["name"] = price.Asset.Eip712Name,
                ["version"] = price.Asset.Eip712Version,
            }, X402.Json.X402Json.Options),
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
                // Stryker disable once String : PaymentAttempt.Accepted and AlreadySettled, the only attempts that settle, always set Demand
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
