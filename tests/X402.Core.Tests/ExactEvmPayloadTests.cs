using System.Text.Json;
using X402.Core.Tests.Vectors;
using X402.Json;
using X402.Protocol;

namespace X402.Core.Tests;

public sealed class ExactEvmPayloadTests
{
    [Fact]
    public void AsExactEvm_reads_the_authorization_from_a_specification_vector()
    {
        var vector = SpecVectorSource.OfKind("PaymentPayload").First();
        var payload = JsonSerializer.Deserialize<PaymentPayload>(
            vector.Json.ToJsonString(), X402Json.Options)!;

        var exact = payload.AsExactEvm();

        exact.Signature.ShouldStartWith("0x");
        exact.Authorization.Value.ShouldBe("10000");
        exact.Authorization.Nonce.ShouldStartWith("0x");
    }

    [Fact]
    public void WithExactEvm_round_trips_through_AsExactEvm()
    {
        var vector = SpecVectorSource.OfKind("PaymentPayload").First();
        var payload = JsonSerializer.Deserialize<PaymentPayload>(
            vector.Json.ToJsonString(), X402Json.Options)!;
        var original = payload.AsExactEvm();

        var rebuilt = payload.WithExactEvm(original).AsExactEvm();

        rebuilt.ShouldBe(original);
    }
}
