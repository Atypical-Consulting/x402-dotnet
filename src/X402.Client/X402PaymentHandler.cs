using System.Globalization;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Nethereum.Util;
using X402.Assets;
using X402.Client.Signing;
using X402.Client.Spending;
using X402.Json;
using X402.Protocol;
using X402.Transport;

namespace X402.Client;

/// <summary>
/// A <see cref="DelegatingHandler"/> that turns a 402 response into a paid one: it sees the
/// demand, picks an affordable asset, signs an EIP-3009 authorization, and replays the request
/// exactly once. Callers see only the final response — no payment-specific code of their own.
/// </summary>
/// <remarks>
/// Two orderings here are safety properties, not implementation detail:
/// <list type="bullet">
/// <item>
/// The spending limit is checked, via <see cref="ISpendTracker.EnsureWithinLimitsAndRecord"/>,
/// before anything is signed. Signing first and checking after would leave a valid, spendable
/// authorization in memory — and in any log that captured it — even when the demand exceeds what
/// this agent is willing to pay.
/// </item>
/// <item>
/// The outgoing request's content is buffered before the first send. An <see cref="HttpContent"/>
/// is not necessarily replayable — a <see cref="HttpContent.LoadIntoBufferAsync()"/> call is what
/// makes a second send of the same content possible — so skipping this step would replay a POST
/// with an empty body.
/// </item>
/// </list>
/// This client is deliberately general: it is not restricted to servers built with
/// <c>X402.AspNetCore</c>. A third-party or non-compliant server is not bound by this library's
/// own invariant that one demand never spans two networks, so requirements are filtered by
/// <see cref="X402ClientOptions.AllowedNetworks"/> before the agent's asset preference is applied
/// — applying a symbol preference first, against a multi-network offer, could resolve it against
/// the wrong chain's contract.
/// </remarks>
public sealed class X402PaymentHandler : DelegatingHandler
{
    private readonly X402ClientOptions options;
    private readonly IPaymentSigner signer;
    private readonly ISpendTracker spendTracker;

    /// <summary>Creates a handler that pays for 402 responses on behalf of <paramref name="signer"/>.</summary>
    public X402PaymentHandler(X402ClientOptions options, IPaymentSigner signer, ISpendTracker spendTracker)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(spendTracker);

        this.options = options;
        this.signer = signer;
        this.spendTracker = spendTracker;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Buffer the content before the first send. Without this, a POST replayed at step 8
        // would go out with an empty body — the request stream can only be read once otherwise.
        if (request.Content is not null)
        {
            await request.Content
                .LoadIntoBufferAsync(options.MaxBufferedRequestBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        // 2. Send. Anything but 402 is returned untouched — most requests never involve payment.
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PaymentRequired)
        {
            return response;
        }

        var paymentRequiredHeader = response.Headers.TryGetValues(X402Headers.PaymentRequired, out var headerValues)
            ? headerValues.FirstOrDefault()
            : null;
        response.Dispose();

        // 3. Decode PAYMENT-REQUIRED. A 402 with no usable demand cannot be paid.
        if (!X402Codec.TryDecode<PaymentRequired>(paymentRequiredHeader, out var decoded, out var decodeError))
        {
            throw new PaymentRejectedException(
                $"The server returned 402 without a usable payment demand: {decodeError}.");
        }

        var required = decoded!;

        // 4 & 5. Filter to what is actually payable, then order by agent preference, server order
        // as the tiebreaker.
        var candidates = SelectCandidates(required);
        if (candidates.Count == 0)
        {
            throw new NoAcceptablePaymentException(
                $"None of the {required.Accepts.Count} payment requirement(s) offered for " +
                $"'{required.Resource.Url}' are payable: each was rejected for an unsupported " +
                "scheme, a network outside AllowedNetworks, an asset outside the KnownAssets " +
                "catalogue, or a malformed amount.");
        }

        // 6. Reserve funds for the first candidate that fits within its limits. Nothing is signed
        // until a reservation succeeds, so a demand this agent will not pay never gets that far.
        var chosen = default((PaymentRequirements Requirement, AssetDescriptor Asset, decimal Amount)?);
        SpendingLimitExceededException? lastLimitException = null;
        foreach (var candidate in candidates)
        {
            try
            {
                spendTracker.EnsureWithinLimitsAndRecord(candidate.Asset, candidate.Amount);
                chosen = candidate;
                break;
            }
            catch (SpendingLimitExceededException exception)
            {
                lastLimitException = exception;
            }
        }

        if (chosen is null)
        {
            // Every acceptable asset was over its limit: relay the last reason. Nothing was
            // signed, and nothing below this point ever runs.
            throw lastLimitException!;
        }

        var (requirement, asset, amount) = chosen.Value;
        HttpRequestMessage replay;
        HttpResponseMessage replayResponse;
        try
        {
            // 7. Build the authorization. validAfter sits 60s in the past so a payer's clock
            // running slightly ahead of the facilitator's does not make the authorization
            // "not yet valid"; validBefore follows the server's own stated timeout.
            var now = DateTimeOffset.UtcNow;
            var authorization = new Eip3009Authorization
            {
                From = signer.Address,
                To = requirement.PayTo,
                Value = requirement.Amount,
                ValidAfter = now.AddSeconds(-60).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ValidBefore = now.AddSeconds(requirement.MaxTimeoutSeconds)
                    .ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                Nonce = "0x" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            };

            // 8. Sign, encode PAYMENT-SIGNATURE, and replay — exactly once.
            var exact = await signer.SignAsync(requirement, authorization, asset, cancellationToken)
                .ConfigureAwait(false);
            var paymentPayload = new PaymentPayload
            {
                Resource = required.Resource,
                Accepted = requirement,
                Payload = JsonSerializer.SerializeToElement(exact, X402Json.Options),
            };

            replay = Clone(request);
            replay.Headers.Add(X402Headers.PaymentSignature, X402Codec.Encode(paymentPayload));

            replayResponse = await base.SendAsync(replay, cancellationToken).ConfigureAwait(false);

            // 9. A second 402 means the payment was refused; do not loop. The second PAYMENT-REQUIRED
            // is decoded here — before disposal, the same way the first one was at step 3 — so the
            // server's actual reason (an insufficient balance, an unsupported asset, an expired
            // authorization, and so on) reaches the caller instead of being thrown away: an agent
            // paying a third-party API has no way to read that server's own logs.
            if (replayResponse.StatusCode == HttpStatusCode.PaymentRequired)
            {
                var rejectionHeader = replayResponse.Headers
                    .TryGetValues(X402Headers.PaymentRequired, out var rejectionValues)
                        ? rejectionValues.FirstOrDefault()
                        : null;
                replayResponse.Dispose();

                var decodedRejection = X402Codec.TryDecode<PaymentRequired>(
                    rejectionHeader, out var rejection, out _)
                        ? rejection
                        : null;

                var reasonClause = decodedRejection?.Error is { Length: > 0 } reason
                    ? $": {reason}"
                    : " (the server gave no reason)";

                var message =
                    $"The server demanded payment again for '{required.Resource.Url}' after this " +
                    $"client paid for it{reasonClause}. Refusing to retry a second time.";

                throw decodedRejection is not null
                    ? new PaymentRejectedException(message, decodedRejection)
                    : new PaymentRejectedException(message);
            }
        }
        catch
        {
            // The reservation made at step 6 must not outlive a payment that never completed —
            // otherwise a rejected or failed payment would permanently consume session budget.
            // Scoped to end here, not around receipt exposure below: by the time this try block
            // exits normally, the replay is known to have succeeded, and nothing past this point
            // should ever release a reservation for a payment that actually settled.
            spendTracker.Release(asset, amount);
            throw;
        }

        // 10. Expose the settlement receipt on the response, via the replay request's Options bag
        // — HttpResponseMessage carries no Options of its own.
        replayResponse.RequestMessage ??= replay;
        if (replayResponse.Headers.TryGetValues(X402Headers.PaymentResponse, out var receiptValues) &&
            X402Codec.TryDecode<SettleResponse>(receiptValues.FirstOrDefault(), out var receipt, out _))
        {
            replayResponse.RequestMessage.Options.Set(HttpResponseMessageExtensions.ReceiptKey, receipt!);
        }

        return replayResponse;
    }

    /// <summary>
    /// Filters <paramref name="required"/>'s offered requirements down to what this client can
    /// actually pay, then orders the survivors by <see cref="X402ClientOptions.Preferences"/>,
    /// falling back to the server's own order.
    /// </summary>
    private List<(PaymentRequirements Requirement, AssetDescriptor Asset, decimal Amount)> SelectCandidates(
        PaymentRequired required)
    {
        var filtered = new List<(PaymentRequirements Requirement, AssetDescriptor Asset, decimal Amount, int Index)>();

        for (var index = 0; index < required.Accepts.Count; index++)
        {
            var requirement = required.Accepts[index];

            if (!string.Equals(requirement.Scheme, "exact", StringComparison.Ordinal))
            {
                continue;
            }

            // Filter by allowed network before asset preference is ever applied: a server this
            // library did not build can offer a multi-network `accepts` array, and resolving a
            // symbol preference against that first could sign for the wrong chain's contract.
            if (options.AllowedNetworks.Count > 0 && !options.AllowedNetworks.Contains(requirement.Network))
            {
                continue;
            }

            // Resolution is by network + contract address, from KnownAssets, never by trusting
            // the server's `extra` field: an uncatalogued asset has no locally-known EIP-712
            // domain, so signing for it would mean guessing that domain from server-supplied data.
            var asset = ResolveAsset(requirement);
            if (asset is null)
            {
                continue;
            }

            if (!TryGetDisplayAmount(requirement.Amount, asset.Decimals, out var amount))
            {
                continue;
            }

            filtered.Add((requirement, asset, amount, index));
        }

        return filtered
            .OrderBy(candidate => PreferenceRank(candidate.Asset.Symbol))
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => (candidate.Requirement, candidate.Asset, candidate.Amount))
            .ToList();
    }

    /// <summary>The candidate's rank in <see cref="X402ClientOptions.Preferences"/>, least first.</summary>
    private int PreferenceRank(string symbol)
    {
        for (var i = 0; i < options.Preferences.Count; i++)
        {
            if (string.Equals(options.Preferences[i], symbol, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static AssetDescriptor? ResolveAsset(PaymentRequirements requirement)
    {
        foreach (var candidate in KnownAssets.ForNetwork(requirement.Network))
        {
            if (candidate.Address.IsTheSameAddress(requirement.Asset))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Converts an atomic-unit amount to the asset's display units, for limit checks.</summary>
    private static bool TryGetDisplayAmount(string atomicAmount, int decimals, out decimal amount)
    {
        amount = 0m;

        if (!BigInteger.TryParse(atomicAmount, NumberStyles.None, CultureInfo.InvariantCulture, out var atomic))
        {
            return false;
        }

        try
        {
            amount = (decimal)atomic / (decimal)BigInteger.Pow(10, decimals);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds a fresh, unsent request equivalent to <paramref name="request"/>. A request that has
    /// already been sent cannot be sent again, so the replay at step 8 needs its own instance —
    /// carrying the same method, URI, version, headers, options and buffered content.
    /// </summary>
    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            // Already buffered at step 1: the same HttpContent instance can be sent a second time.
            Content = request.Content,
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            clone.Options.TryAdd(option.Key, option.Value);
        }

        return clone;
    }
}
