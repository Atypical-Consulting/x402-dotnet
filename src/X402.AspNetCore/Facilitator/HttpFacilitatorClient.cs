using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using X402.Json;
using X402.Protocol;

namespace X402.AspNetCore.Facilitator;

/// <summary>The default <see cref="IFacilitatorClient"/>, over HTTP.</summary>
/// <remarks>
/// <para>
/// <c>verify</c> and <c>settle</c> are retried under different rules, because they differ in one
/// decisive way. <c>verify</c> has no side effect, so replaying it on a transport failure, an
/// attempt timeout or a 5xx is safe and desirable. <c>settle</c> moves money: if the facilitator
/// answered at all, even with a 500, it may already have broadcast the transaction, so it is
/// retried ONLY when no response was received at all (a transport failure or an attempt timeout),
/// never on a received 5xx.
/// </para>
/// <para>
/// The two behaviors are kept apart structurally, not by inspecting the request URI: they are two
/// named <see cref="HttpClient"/> instances resolved from <see cref="IHttpClientFactory"/> —
/// <c>"x402-verify"</c> and <c>"x402-settle"</c> — each executed through its own resilience
/// pipeline. The per-attempt timeout is derived from
/// <see cref="PaymentRequirements.MaxTimeoutSeconds"/> rather than a static, app-wide value: that
/// field already says how long the payer's authorization is good for, and a facilitator round trip
/// that outlives it is pointless.
/// </para>
/// </remarks>
internal sealed class HttpFacilitatorClient(IHttpClientFactory httpClientFactory) : IFacilitatorClient
{
    private const string VerifyClientName = "x402-verify";
    private const string SettleClientName = "x402-settle";

    // Total attempts, not retries: verify tolerates two retries (three attempts total); settle
    // tolerates only one (two attempts total), because a retried settle can double-broadcast.
    private const int VerifyMaxRetryAttempts = 2;
    private const int SettleMaxRetryAttempts = 1;

    private static readonly TimeSpan MaxAttemptTimeout = TimeSpan.FromSeconds(30);

    public Task<VerifyResponse> VerifyAsync(
        PaymentPayload payload, PaymentRequirements requirements, CancellationToken cancellationToken)
        => PostAsync<VerifyResponse>(
            VerifyClientName, "verify", VerifyMaxRetryAttempts, ShouldRetryVerify,
            payload, requirements, cancellationToken);

    public Task<SettleResponse> SettleAsync(
        PaymentPayload payload, PaymentRequirements requirements, CancellationToken cancellationToken)
        => PostAsync<SettleResponse>(
            SettleClientName, "settle", SettleMaxRetryAttempts, ShouldRetrySettle,
            payload, requirements, cancellationToken);

    // Single bounded attempt, no retry: nothing in src/ calls this yet, so there is no case in
    // hand to size a retry policy against, and shipping one untested would be machinery nobody
    // asked for. Whoever adds the first real caller should choose (and test) its own resilience.
    public async Task<SupportedResponse> GetSupportedAsync(CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient(VerifyClientName);

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(MaxAttemptTimeout);

            var response = await httpClient.GetAsync("supported", timeoutSource.Token);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<SupportedResponse>(
                       X402Json.Options, cancellationToken)
                   ?? throw new FacilitatorException(
                       "The facilitator returned an empty body for /supported.");
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            throw new FacilitatorException(
                $"The facilitator at {httpClient.BaseAddress} could not be reached for /supported.",
                exception);
        }
    }

    private async Task<TResponse> PostAsync<TResponse>(
        string clientName, string path, int maxRetryAttempts,
        Func<RetryPredicateArguments<HttpResponseMessage>, ValueTask<bool>> shouldHandle,
        PaymentPayload payload, PaymentRequirements requirements, CancellationToken cancellationToken)
        where TResponse : class
    {
        var httpClient = httpClientFactory.CreateClient(clientName);
        var pipeline = BuildPipeline(maxRetryAttempts, shouldHandle, AttemptTimeout(requirements));

        var request = new FacilitatorRequest
        {
            PaymentPayload = payload,
            PaymentRequirements = requirements,
        };

        HttpResponseMessage response;
        try
        {
            response = await pipeline.ExecuteAsync(
                token => SendAsync(httpClient, path, request, token), cancellationToken);
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            throw new FacilitatorException(
                $"The facilitator at {httpClient.BaseAddress} could not be reached for /{path}.",
                exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new FacilitatorException(
                $"The facilitator answered /{path} with {(int)response.StatusCode} " +
                $"{response.ReasonPhrase}.");
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<TResponse>(
                       X402Json.Options, cancellationToken)
                   ?? throw new FacilitatorException(
                       $"The facilitator returned an empty body for /{path}.");
        }
        catch (JsonException exception)
        {
            throw new FacilitatorException(
                $"The facilitator returned a body for /{path} that is not a valid " +
                $"{typeof(TResponse).Name}.", exception);
        }
    }

    private static async ValueTask<HttpResponseMessage> SendAsync(
        HttpClient httpClient, string path, FacilitatorRequest request, CancellationToken cancellationToken)
    {
        // A fresh HttpRequestMessage per call: Polly re-invokes this delegate once per attempt, and
        // an HttpRequestMessage cannot be sent more than once.
        using var content = JsonContent.Create(request, options: X402Json.Options);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        return await httpClient.SendAsync(httpRequest, cancellationToken);
    }

    /// <summary>
    /// Builds a resilience pipeline for one call. Retry is added before timeout so that timeout is
    /// the inner strategy: each retry attempt re-enters it and gets a fresh deadline, instead of the
    /// whole retried operation sharing a single budget.
    /// </summary>
    /// <remarks>
    /// This pipeline is built fresh on every call — deliberately, because the attempt timeout is
    /// derived from <see cref="PaymentRequirements.MaxTimeoutSeconds"/>, which is only known per
    /// call, not at DI-registration time. Retry and timeout hold no cross-call state, so rebuilding
    /// them per call costs nothing today.
    /// <para>
    /// That stops being true for any STATEFUL strategy. Adding <c>.AddCircuitBreaker(...)</c>,
    /// a rate limiter or a hedging strategy here will compile and pass every existing test, but
    /// each one will start from an empty history on every single call, so a circuit breaker built
    /// this way can never accumulate enough failures to open — it silently never trips, which is
    /// worse than not having one, because everyone assumes it is there. If a later task needs a
    /// stateful strategy, it belongs on a single pipeline registered once per named client via
    /// <c>AddResilienceHandler</c>, with the per-call attempt timeout carried through
    /// <c>HttpRequestMessage.Options</c> (or a <c>ResilienceContext</c> property) and read back by
    /// a <c>TimeoutGenerator</c> — not bolted onto this per-call builder.
    /// </para>
    /// </remarks>
    private static ResiliencePipeline<HttpResponseMessage> BuildPipeline(
        int maxRetryAttempts,
        Func<RetryPredicateArguments<HttpResponseMessage>, ValueTask<bool>> shouldHandle,
        TimeSpan attemptTimeout)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();

        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = maxRetryAttempts,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(200),
            ShouldHandle = shouldHandle,
        });
        builder.AddTimeout(attemptTimeout);

        return builder.Build();
    }

    // verify has no side effect: retry on a transport failure, an attempt timeout (no response at
    // all) or a 5xx (a response, but not one that means the request was actually processed).
    private static ValueTask<bool> ShouldRetryVerify(RetryPredicateArguments<HttpResponseMessage> arguments)
    {
        if (arguments.Outcome.Exception is not null)
        {
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult((int)arguments.Outcome.Result!.StatusCode >= 500);
    }

    // settle moves money: retry ONLY when no response was received at all. A received 5xx may mean
    // the facilitator already broadcast the transaction before failing, so it is never retried.
    private static ValueTask<bool> ShouldRetrySettle(RetryPredicateArguments<HttpResponseMessage> arguments) =>
        ValueTask.FromResult(arguments.Outcome.Exception is not null);

    private static TimeSpan AttemptTimeout(PaymentRequirements requirements) =>
        TimeSpan.FromSeconds(Math.Clamp(
            requirements.MaxTimeoutSeconds, 1, (int)MaxAttemptTimeout.TotalSeconds));

    // A caller's own cancellation must propagate as-is, not be mistaken for a facilitator fault:
    // this is true only when the ambient token is untouched, so a transport failure or an
    // attempt-timeout (whose OperationCanceledException carries an unrelated, already-cancelled
    // token) is still classified as a fault.
    private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        exception switch
        {
            HttpRequestException or TimeoutRejectedException => true,
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            _ => false,
        };

    /// <summary>
    /// Appends a trailing slash when missing. Without it, resolving a relative path such as
    /// <c>verify</c> against a base address that has its own path segment (for example
    /// <c>https://host/facilitator</c>) drops that last segment per <see cref="Uri"/>'s
    /// relative-resolution rules, silently resolving to <c>https://host/verify</c>.
    /// </summary>
    internal static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
