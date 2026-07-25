using System.Net;
using X402.Assets;
using X402.TestKit;
using X402.Transport;

namespace X402.AspNetCore.Tests;

public sealed class BufferingTests
{
    [Fact]
    public async Task A_small_paid_response_is_buffered_until_settlement_succeeds()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var response = await server.PayAsync("/premium", KnownAssets.EurcBaseSepolia);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains(X402Headers.PaymentResponse).ShouldBeTrue();
    }

    [Fact]
    public async Task A_response_beyond_the_cap_settles_first_and_then_streams()
    {
        await using var server = await PaidServerFixture.StartAsync(
            configure: options => options.MaxBufferedResponseBytes = 1024);

        // /large writes 64 KiB, well beyond the cap.
        var response = await server.PayAsync("/large", KnownAssets.EurcBaseSepolia);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Length.ShouldBe(64 * 1024);
        // Settlement happened BEFORE the body went out: the header is present.
        response.Headers.Contains(X402Headers.PaymentResponse).ShouldBeTrue();
        server.Facilitator.SettleCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_settlement_failing_exactly_at_the_cap_is_still_refused_before_anything_streams()
    {
        // The write that first crosses the cap is what forces settlement, and it fails before that
        // write — or any before it — reaches the real network: nothing has left the process, so the
        // whole response can still be refused. The trade-off of D5 is narrower than "a failed
        // settlement can no longer withhold content" — see BufferingResponseBodyFeature's remarks.
        await using var server = await PaidServerFixture.StartAsync(
            configure: options => options.MaxBufferedResponseBytes = 1024);
        server.Facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;

        var response = await server.PayAsync("/large", KnownAssets.EurcBaseSepolia);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
    }

    [Fact]
    public async Task A_swallowed_capacity_exception_still_settles_at_most_once()
    {
        // /large-swallowing wraps every write in a broad catch and returns normally instead of
        // letting BufferingSettlementFailedException propagate — a real streaming endpoint
        // tolerating a client disconnect this way is a common pattern. The pipeline must still
        // notice (via BufferingResponseBodyFeature.Poisoned) rather than fall through to its own
        // SettleAsync call and settle — and charge — the same authorization a second time.
        await using var server = await PaidServerFixture.StartAsync(
            configure: options => options.MaxBufferedResponseBytes = 1024);
        server.Facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;

        var response = await server.PayAsync("/large-swallowing", KnownAssets.EurcBaseSepolia);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        server.Facilitator.SettleCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_free_route_is_never_buffered()
    {
        await using var server = await PaidServerFixture.StartAsync();

        var response = await server.Client.GetAsync("/free", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        server.BufferedRequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task An_endpoint_that_throws_abandons_the_authorization()
    {
        // The authorization is still valid on-chain: the client must be able to retry it.
        await using var server = await PaidServerFixture.StartAsync();
        var payload = await server.SignFor("/boom", KnownAssets.EurcBaseSepolia);

        var first = await server.SendAsync("/boom", payload);
        first.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        server.Facilitator.SettleCallCount.ShouldBe(0);

        // The same authorization is reusable on a route that works.
        var second = await server.SendAsync("/premium", payload);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
