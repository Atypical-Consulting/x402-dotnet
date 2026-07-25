using System.Text.Json;
using System.Text.Json.Nodes;
using X402.Core.Tests.Vectors;
using X402.Json;
using X402.Protocol;

namespace X402.Core.Tests;

public sealed class SpecVectorRoundTripTests
{
    private static readonly Dictionary<string, Type> Types = new()
    {
        ["PaymentRequired"]     = typeof(PaymentRequired),
        ["PaymentPayload"]      = typeof(PaymentPayload),
        ["PaymentRequirements"] = typeof(PaymentRequirements),
        ["VerifyResponse"]      = typeof(VerifyResponse),
        ["SettleResponse"]      = typeof(SettleResponse),
        ["SupportedResponse"]   = typeof(SupportedResponse),
    };

    public static TheoryData<string, int, string> Vectors()
    {
        var data = new TheoryData<string, int, string>();
        foreach (var vector in SpecVectorSource.All)
        {
            data.Add(vector.File, vector.Index, vector.Kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Specification_example_round_trips_without_loss(string file, int index, string kind)
    {
        var vector = SpecVectorSource.All.Single(
            v => v.File == file && v.Index == index && v.Kind == kind);

        var type = Types[kind];
        var value = JsonSerializer.Deserialize(vector.Json.ToJsonString(), type, X402Json.Options);
        value.ShouldNotBeNull();

        var round = JsonNode.Parse(JsonSerializer.Serialize(value, type, X402Json.Options))!;

        // DeepEquals is type-sensitive: a string re-emitted as a number fails here, which is
        // exactly the interoperability break we are guarding against (§2.1.3).
        JsonNode.DeepEquals(vector.Json, round).ShouldBeTrue(
            $"{vector} did not round-trip.\nexpected: {vector.Json.ToJsonString()}\nactual:   {round.ToJsonString()}");
    }

    [Fact]
    public void Specification_yields_vectors_for_every_protocol_type()
    {
        // Guards the extractor itself: a classification regression would silently empty the suite.
        foreach (var kind in Types.Keys)
        {
            SpecVectorSource.OfKind(kind).ShouldNotBeEmpty($"no vector extracted for {kind}");
        }
    }
}
