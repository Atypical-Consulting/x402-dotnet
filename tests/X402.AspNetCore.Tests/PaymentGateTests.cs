using System.Net;
using System.Net.Http.Json;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.Idempotency;
using X402.Assets;
using X402.Protocol;
using X402.TestKit;
using X402.Transport;

namespace X402.AspNetCore.Tests;

public sealed class PaymentGateTests
{
    [Fact]
    public async Task An_unpaid_call_to_a_dynamically_priced_endpoint_gets_402()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var response = await server.Client.PostAsJsonAsync(
            "/analyze", new { Tokens = 100 }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        response.Headers.Contains(X402Headers.PaymentRequired).ShouldBeTrue();
    }

    [Fact]
    public async Task The_price_is_computed_from_the_request()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var cheap = server.DecodeDemand(await server.Client.PostAsJsonAsync(
            "/analyze", new { Tokens = 10 }, TestContext.Current.CancellationToken));
        var dear = server.DecodeDemand(await server.Client.PostAsJsonAsync(
            "/analyze", new { Tokens = 1000 }, TestContext.Current.CancellationToken));

        // 0.001 EURC per token: 10 tokens → 0.010 EURC, 1000 tokens → 1.000 EURC.
        cheap.Accepts[0].Amount.ShouldBe("10000");
        dear.Accepts[0].Amount.ShouldBe("1000000");
    }

    [Fact]
    public async Task The_price_can_depend_on_the_body_size()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var small = server.DecodeDemand(await server.Client.PostAsync("/by-size",
            new StringContent(new string('x', 100)), TestContext.Current.CancellationToken));
        var large = server.DecodeDemand(await server.Client.PostAsync("/by-size",
            new StringContent(new string('x', 10_000)), TestContext.Current.CancellationToken));

        long.Parse(large.Accepts[0].Amount).ShouldBeGreaterThan(long.Parse(small.Accepts[0].Amount));
    }

    [Fact]
    public async Task A_paid_dynamic_call_reaches_the_endpoint_and_settles()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var response = await server.PayDynamicAsync("/analyze", new { Tokens = 100 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains(X402Headers.PaymentResponse).ShouldBeTrue();
    }

    [Fact]
    public async Task The_gate_reports_which_asset_the_payer_chose()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var response = await server.PayDynamicAsync(
            "/analyze", new { Tokens = 100 }, KnownAssets.UsdcBaseSepolia);

        // /analyze returns SettledAsset in its body, so the test can observe it.
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("USDC");
    }

    [Fact]
    public async Task The_same_result_object_works_from_a_controller_and_a_minimal_endpoint()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var minimal = await server.Client.PostAsJsonAsync(
            "/analyze", new { Tokens = 1 }, TestContext.Current.CancellationToken);
        var mvc = await server.Client.PostAsJsonAsync(
            "/mvc/analyze", new { Tokens = 1 }, TestContext.Current.CancellationToken);

        minimal.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        mvc.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        mvc.Headers.Contains(X402Headers.PaymentRequired).ShouldBeTrue();
    }

    [Fact]
    public async Task A_gate_driven_authorization_being_settled_elsewhere_gets_a_409_with_the_reason()
    {
        // No genuine race needed: the ledger is resolvable from the same container the engine
        // settles through, and the identity AuthorizeAsync computes is deterministic from the
        // payload. Seed an in-flight lease for it directly, then present the same authorization —
        // mirrors PaymentProcessorTests.An_authorization_being_settled_elsewhere_gets_409, the
        // route-driven equivalent of this test.
        //
        // This also exercises PaymentGateResult.Result being non-null on a Conflict: /analyze does
        // `await result.Result!.ExecuteAsync(context)` with no special case for a conflict, so a
        // null Result here would surface as a 500 (an unhandled NullReferenceException), not a 409.
        await using var server = await PaidServerFixture.StartAsync();

        var demand = server.DecodeDemand(await server.Client.PostAsJsonAsync(
            "/analyze", new { Tokens = 100 }, TestContext.Current.CancellationToken));
        var requirement = demand.Accepts.Single(
            a => EvmAddress.AreEqual(a.Asset, KnownAssets.EurcBaseSepolia.Address));
        var payload = await TestData.SignedPayloadAsync(requirement);

        var authorization = payload.AsExactEvm().Authorization;
        var identity = new PaymentIdentity(
            payload.Accepted.Network, payload.Accepted.Asset, authorization.Nonce);
        await server.Ledger.AcquireAsync(identity, TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/analyze")
        {
            Content = JsonContent.Create(new { Tokens = 100 }),
        };
        request.Headers.TryAddWithoutValidation(X402Headers.PaymentSignature, X402Codec.Encode(payload));
        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("settl");
    }

    [Fact]
    public async Task Opening_a_gate_without_UseX402_fails_loudly()
    {
        // Without the outbound leg, the content would be delivered without ever being settled.
        await using var server = await PaidServerFixture.StartAsync(withMiddleware: false);

        var response = await server.Client.PostAsJsonAsync(
            "/analyze", new { Tokens = 1 }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        server.LastServerError.ShouldContain("UseX402");
    }

    [Fact]
    public async Task An_endpoint_that_ignores_a_refusal_does_not_get_to_keep_the_content()
    {
        // /analyze-ignoring-refusal discards PaymentGateResult.CanContinue and writes success
        // content regardless — exactly the bug this test exists to catch. No payment is attached,
        // so the gate refuses; the endpoint ignores that and serves the content anyway.
        await using var server = await PaidServerFixture.StartAsync();

        var response = await server.Client.GetAsync(
            "/analyze-ignoring-refusal", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Withheld: the pipeline overwrites the ignored 200 with the refusal it should have been.
        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        body.ShouldNotContain("content that was never paid for");

        // And it must never be a SILENT free ride, even on a request where withholding somehow
        // could not happen: the loud log is the unconditional half of this guarantee.
        server.LoggedErrors.ShouldContain(
            message => message.Contains("ignored PaymentGateResult.CanContinue"));
    }
}
