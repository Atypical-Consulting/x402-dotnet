using System.Net;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using X402.Assets;
using X402.Json;
using X402.PropertyTesting;
using X402.Protocol;
using X402.Transport;

namespace X402.Client.Tests;

/// <summary>
/// Every byte of <c>PAYMENT-REQUIRED</c> comes from a server the agent does not trust. 200
/// deterministic hostile headers per <see cref="FuzzClass"/> go through the real
/// <see cref="X402PaymentHandler"/>; the only exceptions allowed out are the payment-domain ones.
/// </summary>
public sealed class PaymentRequiredFuzzTests
{
    public static TheoryData<FuzzClass> Classes => new(Enum.GetValues<FuzzClass>());

    private static readonly Type[] PaymentExceptions =
    [
        typeof(PaymentRejectedException),
        typeof(NoAcceptablePaymentException),
        // A hostile demand can name an amount that parses but exceeds the declared limit.
        typeof(SpendingLimitExceededException),
    ];

    private static readonly PaymentRequired PayableDemand = new()
    {
        Resource = new ResourceInfo { Url = "https://api.test/premium" },
        Accepts =
        [
            new PaymentRequirements
            {
                Scheme = "exact",
                Network = KnownAssets.EurcBaseSepolia.Network,
                Amount = "10000",
                Asset = KnownAssets.EurcBaseSepolia.Address,
                PayTo = "0x209693Bc6afc0C5328bA36FaF03C514EF312287C",
                MaxTimeoutSeconds = 60,
            },
        ],
    };

    // Random demands, most of which this client will not pay, plus one it will: mutations of the
    // payable one get past candidate selection into limits, signing and the replay.
    private static readonly Gen<string> DemandJson = Gen.OneOf(
        X402Arbitraries.PaymentRequired.Select(d => JsonSerializer.Serialize(d, X402Json.Options)),
        Gen.Constant(JsonSerializer.Serialize(PayableDemand, X402Json.Options)));

    [Theory]
    [MemberData(nameof(Classes))]
    public async Task Hostile_payment_required_only_raises_payment_exceptions(FuzzClass fuzzClass)
    {
        var foreign = new List<string>();
        foreach (var header in HeaderFuzz.Sample(fuzzClass, DemandJson))
        {
            using var harness = PayingClientHarness.Create(
                request => request.Headers.Contains(X402Headers.PaymentSignature)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    : PaymentRequired(header),
                WithLimits);

            if (await ForeignExceptionAsync(harness) is { } exception)
            {
                foreign.Add($"{exception.GetType().Name} for {header}: {exception.Message}");
            }
        }

        foreign.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Classes))]
    public async Task Hostile_rejection_on_replay_only_raises_payment_exceptions(FuzzClass fuzzClass)
    {
        var foreign = new List<string>();
        foreach (var header in HeaderFuzz.Sample(fuzzClass, DemandJson))
        {
            using var harness = PayingClientHarness.Create(
                request => request.Headers.Contains(X402Headers.PaymentSignature)
                    ? PaymentRequired(header)
                    : PaymentRequired(X402Codec.Encode(PayableDemand)),
                WithLimits);

            if (await ForeignExceptionAsync(harness) is { } exception)
            {
                foreign.Add($"{exception.GetType().Name} for {header}: {exception.Message}");
            }
        }

        foreign.ShouldBeEmpty();
    }

    private static void WithLimits(X402ClientOptions options)
    {
        foreach (var asset in X402Arbitraries.Assets)
        {
            options.SetLimits(asset, perRequest: 10m, perSession: 10m);
        }
    }

    private static HttpResponseMessage PaymentRequired(string header)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PaymentRequired);
        response.Headers.TryAddWithoutValidation(X402Headers.PaymentRequired, header);
        return response;
    }

    private static async Task<Exception?> ForeignExceptionAsync(PayingClientHarness harness)
    {
        try
        {
            using var _ = await harness.Client.GetAsync("https://api.test/premium", TestContext.Current.CancellationToken);
            return null;
        }
        catch (Exception exception) when (!PaymentExceptions.Contains(exception.GetType()))
        {
            return exception;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
