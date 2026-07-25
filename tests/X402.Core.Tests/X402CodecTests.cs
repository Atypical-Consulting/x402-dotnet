using System.Text;
using X402.Core.Tests.Vectors;
using X402.Protocol;
using X402.Transport;

namespace X402.Core.Tests;

public sealed class X402CodecTests
{
    [Fact]
    public void Header_names_match_the_v2_transport_specification()
    {
        // v1 used X-PAYMENT. This test guards against a regression to the old name.
        X402Headers.PaymentRequired.ShouldBe("PAYMENT-REQUIRED");
        X402Headers.PaymentSignature.ShouldBe("PAYMENT-SIGNATURE");
        X402Headers.PaymentResponse.ShouldBe("PAYMENT-RESPONSE");
    }

    [Fact]
    public void Encode_then_decode_returns_an_equal_value()
    {
        var vector = SpecVectorSource.OfKind("PaymentRequired").First();
        var original = System.Text.Json.JsonSerializer.Deserialize<PaymentRequired>(
            vector.Json.ToJsonString(), Json.X402Json.Options)!;

        var encoded = X402Codec.Encode(original);
        X402Codec.TryDecode<PaymentRequired>(encoded, out var decoded, out var error).ShouldBeTrue();

        error.ShouldBeNull();

        // Compare via JSON round-trip to avoid issues with collection equality
        var originalJson = System.Text.Json.JsonSerializer.Serialize(original, Json.X402Json.Options);
        var decodedJson = System.Text.Json.JsonSerializer.Serialize(decoded, Json.X402Json.Options);
        decodedJson.ShouldBe(originalJson);
    }

    [Fact]
    public void Encode_produces_standard_base64_of_utf8_json()
    {
        var vector = SpecVectorSource.OfKind("SettleResponse").First();
        var value = System.Text.Json.JsonSerializer.Deserialize<SettleResponse>(
            vector.Json.ToJsonString(), Json.X402Json.Options)!;

        var encoded = X402Codec.Encode(value);

        // Decodable by any standard base64 decoder, and the result is UTF-8 JSON.
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        json.ShouldStartWith("{");
        json.ShouldContain("\"success\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!!")]
    [InlineData("YWJj")]                        // valid base64, invalid JSON
    [InlineData("eyJmb28iOiJiYXIifQ==")]        // valid JSON, but the wrong shape
    public void TryDecode_never_throws_on_hostile_input(string? header)
    {
        // These values come from the network, but TryDecode already covers all of them by
        // construction (invalid base64, invalid JSON, unexpected shape). An exception escaping
        // here would be a regression in its own error handling — a development mistake to fix
        // in TryDecode, not a denial-of-service vector: reaching this path requires breaking
        // the code, not sending a particular request.
        var ok = X402Codec.TryDecode<PaymentRequired>(header, out var value, out var error);

        ok.ShouldBeFalse();
        value.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryDecode_reports_a_version_mismatch_explicitly()
    {
        var v1 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"x402Version":1,"resource":{"url":"https://x"},"accepts":[]}"""));

        X402Codec.TryDecode<PaymentRequired>(v1, out _, out var error).ShouldBeFalse();
        error!.ShouldContain("version");
    }

    [Fact]
    public void Error_reasons_cover_the_specification_codes()
    {
        X402ErrorReason.InsufficientFunds.ShouldBe("insufficient_funds");
        X402ErrorReason.InvalidNetwork.ShouldBe("invalid_network");
        X402ErrorReason.InvalidScheme.ShouldBe("invalid_scheme");
        X402ErrorReason.InvalidExactEvmPayloadSignature
            .ShouldBe("invalid_exact_evm_payload_signature");
        X402ErrorReason.All.Count.ShouldBe(15);
    }

    [Fact]
    public void TryDecode_rejects_unregistered_types_without_throwing()
    {
        // AssetDescriptor is not registered in X402JsonContext, so deserialization would normally
        // raise NotSupportedException. TryDecode must catch this and return false with a
        // developer-actionable error message.
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"foo":"bar"}"""));

        X402Codec.TryDecode<X402.Assets.AssetDescriptor>(b64, out var value, out var error)
            .ShouldBeFalse();

        value.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
        error.ShouldContain("AssetDescriptor");
        error.ShouldContain("JsonSerializable");
    }

    [Fact]
    public void TryDecode_rejects_implausibly_large_headers()
    {
        // A header larger than 10 MB is implausibly large for any x402 protocol object and
        // suggests a malformed or hostile input. It should be rejected before attempting to decode.
        var implausiblyLarge = new string('A', 11 * 1024 * 1024); // 11 MB of base64 chars

        X402Codec.TryDecode<PaymentRequired>(implausiblyLarge, out var value, out var error)
            .ShouldBeFalse();

        value.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
        error.ShouldContain("exceeds");
    }
}
