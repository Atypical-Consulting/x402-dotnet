using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using X402.PropertyTesting;
using X402.Protocol;
using X402.Transport;

namespace X402.Core.Tests;

// Fixed seed: the suite is deterministic in CI. A failure prints its own seed for replay.
public sealed class CodecPropertyTests
{
    private static readonly string[] ProtocolNumerics = ["amount", "value", "validAfter", "validBefore", "nonce"];

    [Property(MaxTest = 500, Replay = "(26,1)")]
    public Property PaymentRequired_round_trips_through_the_codec() =>
        Prop.ForAll(X402Arbitraries.PaymentRequired.ToArbitrary(), RoundTrips);

    [Property(MaxTest = 500, Replay = "(26,3)")]
    public Property PaymentPayload_round_trips_through_the_codec() =>
        Prop.ForAll(X402Arbitraries.PaymentPayload.ToArbitrary(), RoundTrips);

    [Property(MaxTest = 500, Replay = "(26,5)")]
    public Property SettleResponse_round_trips_through_the_codec() =>
        Prop.ForAll(X402Arbitraries.SettleResponse.ToArbitrary(), RoundTrips);

    [Property(MaxTest = 500, Replay = "(26,7)")]
    public Property Protocol_numerics_are_always_json_strings() =>
        Prop.ForAll(
            Gen.OneOf(
                X402Arbitraries.PaymentRequired.Select(X402Codec.Encode),
                X402Arbitraries.PaymentPayload.Select(X402Codec.Encode),
                X402Arbitraries.SettleResponse.Select(X402Codec.Encode)).ToArbitrary(),
            encoded =>
            {
                using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
                NonStringNumerics(document.RootElement).ShouldBeEmpty();
            });

    private static bool RoundTrips<T>(T original) where T : class
    {
        var encoded = X402Codec.Encode(original);
        return X402Codec.TryDecode<T>(encoded, out var decoded, out _)
               && X402Codec.Encode(decoded) == encoded;
    }

    private static IEnumerable<string> NonStringNumerics(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p =>
            ProtocolNumerics.Contains(p.Name) && p.Value.ValueKind != JsonValueKind.String
                ? [$"{p.Name}: {p.Value.ValueKind}"]
                : NonStringNumerics(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(NonStringNumerics),
        _ => [],
    };
}
