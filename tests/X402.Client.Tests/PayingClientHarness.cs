using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using X402.Assets;
using X402.Client.Signing;
using X402.Client.Spending;
using X402.Json;
using X402.Protocol;
using X402.Transport;

namespace X402.Client.Tests;

/// <summary>
/// Wires an <see cref="X402PaymentHandler"/> to a scripted "server" for tests: a bare
/// <see cref="HttpMessageHandler"/> that plays 402-then-200 (or always-402), while recording every
/// request that reaches it — body, <c>PAYMENT-SIGNATURE</c> header and decoded payload — plus how
/// many times the signer was actually asked to sign.
/// </summary>
public sealed class PayingClientHarness : IDisposable
{
    private const string PayeeAddress = "0x209693Bc6afc0C5328bA36FaF03C514EF312287C";
    private const string PayerPrivateKey =
        "0x43da92af0b6c7af92b11f5ceb276329989499043c18c9dab3446903c84ac904a";
    private const string ResourceUrl = "https://api.test/premium";

    private readonly CountingPaymentSigner signer;
    private readonly RecordingHandler recordingHandler;

    private PayingClientHarness(
        X402ClientOptions options, Func<HttpRequestMessage, RecordedRequest, Task<HttpResponseMessage>> respond)
    {
        signer = new CountingPaymentSigner(new PrivateKeyPaymentSigner(PayerPrivateKey));
        recordingHandler = new RecordingHandler(respond);

        var handler = new X402PaymentHandler(options, signer, new InMemorySpendTracker(options))
        {
            InnerHandler = recordingHandler,
        };

        Client = new HttpClient(handler);
    }

    /// <summary>The client under test: every call goes through <see cref="X402PaymentHandler"/>.</summary>
    public HttpClient Client { get; }

    /// <summary>How many requests actually reached the "server".</summary>
    public int RequestCount => recordingHandler.Requests.Count;

    /// <summary>How many times the wrapped <see cref="IPaymentSigner"/> was asked to sign.</summary>
    public int SignatureCount => signer.Count;

    /// <summary>The <c>PAYMENT-SIGNATURE</c> header of the most recent request, if any.</summary>
    public string? LastPaymentHeader => recordingHandler.Requests.Count > 0
        ? recordingHandler.Requests[^1].PaymentHeader
        : null;

    /// <summary>The body of the most recent request that reached the "server". Empty for a body-less request.</summary>
    public string LastRequestBody => recordingHandler.Requests.Count > 0
        ? recordingHandler.Requests[^1].Body
        : string.Empty;

    /// <summary>The body of every request that reached the "server", in order. Empty for a body-less request.</summary>
    public IReadOnlyList<string> RequestBodies => [.. recordingHandler.Requests.Select(r => r.Body)];

    /// <summary>The token contract address of the most recently paid asset.</summary>
    public string? PaidAsset => recordingHandler.Requests
        .LastOrDefault(r => r.Payload is not null)?.Payload?.Accepted.Asset;

    /// <summary>The authorization decoded from the most recent paid request.</summary>
    public Eip3009Authorization? LastAuthorization => recordingHandler.Requests
        .LastOrDefault(r => r.Payload is not null)?.Payload?.AsExactEvm().Authorization;

    /// <summary>Every nonce this harness has seen across every payment made against it so far.</summary>
    public IReadOnlyList<string> Nonces => [.. recordingHandler.Requests
        .Where(r => r.Payload is not null)
        .Select(r => r.Payload!.AsExactEvm().Authorization.Nonce)];

    /// <summary>Builds a harness around a hand-written response function — for scenarios with no payment involved.</summary>
    public static PayingClientHarness Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);

        return new PayingClientHarness(new X402ClientOptions(), (request, _) => Task.FromResult(respond(request)));
    }

    /// <summary>
    /// Builds a harness whose "server" demands payment for <paramref name="asset"/>, then accepts
    /// whatever <see cref="X402PaymentHandler"/> replays with a valid <c>PAYMENT-SIGNATURE</c>.
    /// </summary>
    public static PayingClientHarness CreatePaywall(
        AssetDescriptor asset, Action<X402ClientOptions>? configure = null) =>
        CreatePaywall([asset], alwaysPaywall: false, configure);

    /// <summary>
    /// Builds a harness whose "server" offers two assets, in the given order, then accepts
    /// whichever one <see cref="X402PaymentHandler"/> replays with a valid <c>PAYMENT-SIGNATURE</c>.
    /// </summary>
    public static PayingClientHarness CreatePaywall(
        AssetDescriptor firstAsset, AssetDescriptor secondAsset, Action<X402ClientOptions>? configure = null) =>
        CreatePaywall([firstAsset, secondAsset], alwaysPaywall: false, configure);

    /// <summary>
    /// Builds a harness whose "server" demands payment for <paramref name="asset"/> and keeps
    /// demanding it again even after being paid — so a second reply never settles. When
    /// <paramref name="rejectionReason"/> is given, the second (rejecting) demand carries it as
    /// <see cref="PaymentRequired.Error"/> — the same field <c>PaymentRejectedException.Reason</c>
    /// is meant to surface. When <paramref name="malformedSecondRejection"/> is true, the second
    /// 402's <c>PAYMENT-REQUIRED</c> header is not a valid encoded demand at all — exercises the
    /// branch where <see cref="X402PaymentHandler"/>'s own decode of that header fails, distinct
    /// from a demand that decodes but simply carries no <see cref="PaymentRequired.Error"/>.
    /// </summary>
    public static PayingClientHarness CreateAlwaysPaywalled(
        AssetDescriptor asset, string? rejectionReason = null, bool malformedSecondRejection = false) =>
        CreatePaywall([asset], alwaysPaywall: true, configure: null, rejectionReason, malformedSecondRejection);

    private static PayingClientHarness CreatePaywall(
        IReadOnlyList<AssetDescriptor> assets, bool alwaysPaywall, Action<X402ClientOptions>? configure,
        string? rejectionReason = null, bool malformedSecondRejection = false)
    {
        var options = new X402ClientOptions();
        foreach (var asset in assets)
        {
            // Generous defaults so a test that only cares about some other behaviour does not
            // also have to think about limits; a test that does calls SetLimits again below, and
            // the later call wins because SetLimits is keyed by asset identity, not accumulated.
            options.SetLimits(asset, perRequest: 10m, perSession: 100m);
        }

        configure?.Invoke(options);

        return new PayingClientHarness(options, (request, recorded) =>
            RespondToPaywalled(
                request, recorded, assets, alwaysPaywall, rejectionReason, malformedSecondRejection));
    }

    private static Task<HttpResponseMessage> RespondToPaywalled(
        HttpRequestMessage request, RecordedRequest recorded, IReadOnlyList<AssetDescriptor> assets,
        bool alwaysPaywall, string? rejectionReason, bool malformedSecondRejection)
    {
        if (!alwaysPaywall && recorded.Payload is not null)
        {
            return Task.FromResult(Settle(recorded.Payload, recorded.Body));
        }

        // A rejection reason (or a malformed header) only makes sense once a payment was actually
        // presented and refused — the very first 402 (no payload yet) is an ordinary demand, never
        // a refusal.
        var isRejection = alwaysPaywall && recorded.Payload is not null;
        var response = new HttpResponseMessage(HttpStatusCode.PaymentRequired);

        if (isRejection && malformedSecondRejection)
        {
            // Not valid base64, so X402Codec.TryDecode<PaymentRequired> must fail cleanly rather
            // than throw — same contract the first 402's own decode (step 3) already relies on.
            response.Headers.Add(X402Headers.PaymentRequired, "not a decodable payment-required header");
            return Task.FromResult(response);
        }

        var error = isRejection ? rejectionReason : null;
        response.Headers.Add(X402Headers.PaymentRequired, X402Codec.Encode(BuildDemand(assets, error)));
        return Task.FromResult(response);
    }

    private static HttpResponseMessage Settle(PaymentPayload payload, string requestBody)
    {
        var exact = payload.AsExactEvm();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(requestBody),
        };

        var settlement = new SettleResponse
        {
            Success = true,
            Payer = exact.Authorization.From,
            Transaction = "0x" + Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(exact.Authorization.Nonce))).ToLowerInvariant(),
            Network = payload.Accepted.Network,
            Amount = payload.Accepted.Amount,
        };

        response.Headers.Add(X402Headers.PaymentResponse, X402Codec.Encode(settlement));
        return response;
    }

    private static PaymentRequired BuildDemand(IReadOnlyList<AssetDescriptor> assets, string? error = null) => new()
    {
        Error = error,
        Resource = new ResourceInfo { Url = ResourceUrl },
        Accepts = [.. assets.Select(BuildRequirements)],
    };

    private static PaymentRequirements BuildRequirements(AssetDescriptor asset) => new()
    {
        Scheme = "exact",
        Network = asset.Network,
        // 0.01 in display units, well inside the generous default limits above.
        Amount = ((BigIntegerScale(asset.Decimals)) / 100).ToString(),
        Asset = asset.Address,
        PayTo = PayeeAddress,
        MaxTimeoutSeconds = 60,
        Extra = JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { ["name"] = asset.Eip712Name, ["version"] = asset.Eip712Version },
            X402Json.Options),
    };

    private static System.Numerics.BigInteger BigIntegerScale(int decimals) =>
        System.Numerics.BigInteger.Pow(10, decimals);

    /// <inheritdoc />
    public void Dispose()
    {
        Client.Dispose();
    }
}

/// <summary>What <see cref="RecordingHandler"/> captured about one request.</summary>
internal sealed record RecordedRequest(string Body, string? PaymentHeader, PaymentPayload? Payload);

/// <summary>
/// The terminal handler of the harness's pipeline: records every request that reaches it — reading
/// and storing its body before the response is produced, since the request (and its content) may
/// be disposed once the caller has moved on.
/// </summary>
internal sealed class RecordingHandler(Func<HttpRequestMessage, RecordedRequest, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    private readonly List<RecordedRequest> requests = [];

    public IReadOnlyList<RecordedRequest> Requests => requests;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(request.Content, cancellationToken);

        var paymentHeader = request.Headers.TryGetValues(X402Headers.PaymentSignature, out var values)
            ? values.FirstOrDefault()
            : null;

        PaymentPayload? payload = null;
        if (paymentHeader is not null &&
            X402Codec.TryDecode<PaymentPayload>(paymentHeader, out var decoded, out _))
        {
            payload = decoded;
        }

        var recorded = new RecordedRequest(body, paymentHeader, payload);
        requests.Add(recorded);

        return await respond(request, recorded);
    }

    /// <summary>
    /// Reads a request's body without masking whether it was actually replayable.
    /// <see cref="HttpContent.ReadAsStringAsync(CancellationToken)"/> buffers its content as a side
    /// effect — internally, it is implemented on top of <c>LoadIntoBufferAsync</c> — which would
    /// silently make even an unbuffered, single-read stream appear replayable on a second capture,
    /// masking the exact defect <c>A_post_body_is_replayed_intact</c> exists to catch.
    /// <see cref="HttpContent.CopyToAsync(Stream, CancellationToken)"/> has no such side effect: on
    /// content nobody has buffered, it drains the underlying source exactly once, so capturing the
    /// body this way tells the truth about what the wire actually saw.
    /// </summary>
    private static async Task<string> ReadBodyAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return string.Empty;
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>Wraps a real <see cref="IPaymentSigner"/> and counts how often it is actually asked to sign.</summary>
internal sealed class CountingPaymentSigner(IPaymentSigner inner) : IPaymentSigner
{
    private int count;

    public int Count => count;

    public string Address => inner.Address;

    public ValueTask<ExactEvmPayload> SignAsync(
        PaymentRequirements requirements, Eip3009Authorization authorization, AssetDescriptor asset,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref count);
        return inner.SignAsync(requirements, authorization, asset, cancellationToken);
    }
}

/// <summary>
/// A forward-only, non-seekable stream that can be drained exactly once — like a genuine
/// network or request-body stream, and unlike a <see cref="MemoryStream"/> or a
/// <c>JsonContent</c>'s backing object, neither of which would ever expose a buffering defect: a
/// <see cref="MemoryStream"/> is trivially re-readable, and <c>JsonContent</c> re-serialises its
/// backing object fresh on every send. Wrapped in a <see cref="StreamContent"/>, a second attempt
/// to send this content without first buffering it throws
/// <see cref="InvalidOperationException"/> ("The stream was already consumed"); reading it a
/// second time never silently produces a correct-but-coincidental body.
/// </summary>
internal sealed class SingleReadStream(byte[] data) : Stream
{
    private int position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = data.Length - position;
        var toCopy = Math.Min(remaining, count);
        Array.Copy(data, position, buffer, offset, toCopy);
        position += toCopy;
        return toCopy;
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
