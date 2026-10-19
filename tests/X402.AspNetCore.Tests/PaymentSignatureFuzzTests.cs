using System.Net;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using X402.Assets;
using X402.Json;
using X402.PropertyTesting;
using X402.Protocol;
using X402.Transport;

namespace X402.AspNetCore.Tests;

/// <summary>
/// Every byte of <c>PAYMENT-SIGNATURE</c> comes from an untrusted payer. 200 deterministic hostile
/// headers per <see cref="FuzzClass"/> go through the real middleware on the test host.
/// </summary>
public sealed class PaymentSignatureFuzzTests
{
    public static TheoryData<FuzzClass> Classes => new(Enum.GetValues<FuzzClass>());

    [Theory]
    [MemberData(nameof(Classes))]
    public async Task Hostile_payment_signature_always_yields_402(FuzzClass fuzzClass)
    {
        await using var server = await PaidServerFixture.StartAsync();

        var outcomes = await FuzzAsync(server, fuzzClass, ValidPayloadJson);

        outcomes.Where(o => o.Status != HttpStatusCode.PaymentRequired)
            .Select(o => $"{(int)o.Status} for {o.Header}")
            .ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Classes))]
    public async Task Hostile_payment_signature_logs_no_unhandled_exception(FuzzClass fuzzClass)
    {
        await using var server = await PaidServerFixture.StartAsync();
        // A genuine signed payment as well: its mutations get past matching and the nonce and
        // validity checks into verification and settlement. Some stay valid (dropping the optional
        // resource, say) and are rightly served, so this test asserts on errors, not on status.
        var genuine = JsonSerializer.Serialize(
            await server.SignFor("/premium", KnownAssets.EurcBaseSepolia), X402Json.Options);

        await FuzzAsync(server, fuzzClass, Gen.OneOf(ValidPayloadJson, Gen.Constant(genuine)));

        server.LoggedErrors.Append(server.LastServerError).Where(e => e.Length > 0).ShouldBeEmpty();
    }

    [Fact]
    public async Task Nonce_in_another_hex_case_replays_the_same_settlement()
    {
        await using var server = await PaidServerFixture.StartAsync();
        var payload = await server.SignFor("/premium", KnownAssets.EurcBaseSepolia);
        var exact = payload.AsExactEvm();
        var flipped = payload.WithExactEvm(exact with
        {
            Authorization = exact.Authorization with { Nonce = FlipHexCase(exact.Authorization.Nonce) },
        });

        (await server.SendAsync("/premium", payload)).EnsureSuccessStatusCode();
        (await server.SendAsync("/premium", flipped)).EnsureSuccessStatusCode();

        server.Facilitator.SettleCallCount.ShouldBe(1);
    }

    private static string FlipHexCase(string hex) =>
        "0x" + new string(hex[2..].Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());

    // Valid payloads that do not pay this server (random asset, payee, amount, signature): every
    // mutation of them must still be refused with a demand, never crash.
    private static readonly Gen<string> ValidPayloadJson =
        X402Arbitraries.PaymentPayload.Select(p => JsonSerializer.Serialize(p, X402Json.Options));

    private static async Task<List<(string Header, HttpStatusCode Status)>> FuzzAsync(
        PaidServerFixture server, FuzzClass fuzzClass, Gen<string> validJson)
    {
        var outcomes = new List<(string, HttpStatusCode)>();
        foreach (var header in HeaderFuzz.Sample(fuzzClass, validJson))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/premium");
            request.Headers.TryAddWithoutValidation(X402Headers.PaymentSignature, header);
            using var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);
            outcomes.Add((header, response.StatusCode));
        }

        return outcomes;
    }
}
