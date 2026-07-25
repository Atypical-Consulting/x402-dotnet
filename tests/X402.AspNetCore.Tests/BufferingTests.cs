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
    public async Task A_poisoned_buffer_resets_a_stale_content_length_before_rewriting_the_refusal()
    {
        // /large-swallowing-with-content-length declares Content-Length up front (the way
        // Results.Text/Results.Json would) before writing past the cap and swallowing the resulting
        // exception — reaching X402PaymentProcessor.FinishAsync's own Poisoned branch, exactly like
        // A_swallowed_capacity_exception_still_settles_at_most_once above. TestServer tolerates a
        // stale Content-Length surviving the rewrite to the 2-byte {} refusal body; real Kestrel
        // aborts the response trying to satisfy it — so assert the header itself.
        await using var server = await PaidServerFixture.StartAsync(
            configure: options => options.MaxBufferedResponseBytes = 1024);
        server.Facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;

        var response = await server.PayAsync(
            "/large-swallowing-with-content-length", KnownAssets.EurcBaseSepolia);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var declaredLength = response.Content.Headers.ContentLength;
        (declaredLength is null || declaredLength == body.Length).ShouldBeTrue(
            $"Content-Length was {declaredLength}, but the refusal body is {body.Length} bytes.");
    }

    [Fact]
    public async Task A_propagated_capacity_exception_resets_a_stale_content_length_too()
    {
        // /large-with-content-length does not swallow BufferingSettlementFailedException, so it
        // propagates out of next(context) and is handled by X402Middleware's own
        // catch (BufferingSettlementFailedException) block — the M1 site distinct from FinishAsync's
        // Poisoned branch above, sharing the same stale-Content-Length hazard.
        await using var server = await PaidServerFixture.StartAsync(
            configure: options => options.MaxBufferedResponseBytes = 1024);
        server.Facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;

        var response = await server.PayAsync("/large-with-content-length", KnownAssets.EurcBaseSepolia);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var declaredLength = response.Content.Headers.ContentLength;
        (declaredLength is null || declaredLength == body.Length).ShouldBeTrue(
            $"Content-Length was {declaredLength}, but the refusal body is {body.Length} bytes.");
    }

    [Fact]
    public async Task An_ignored_refusal_that_also_overflows_the_buffer_still_gets_a_clean_refusal()
    {
        // /analyze-ignoring-refusal-overflowing stacks two independent endpoint bugs: it ignores
        // PaymentGateResult.CanContinue (like PaymentGateTests's ignored-refusal case), then writes
        // past MaxBufferedResponseBytes inside its own broad catch, which also sets a non-2xx status
        // (503) before returning normally — the exact shape that requires
        // X402Middleware.FinishRefusalAsync to consult BufferingResponseBodyFeature.Poisoned before
        // StatusCode: without it, this request would come back as a bare 503 with an empty body and
        // nothing logged (FlushBufferAsync finding the buffer already discarded and returning
        // silently).
        await using var server = await PaidServerFixture.StartAsync(
            configure: options => options.MaxBufferedResponseBytes = 1024);

        var response = await server.Client.GetAsync(
            "/analyze-ignoring-refusal-overflowing", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Withheld and rewritten as the refusal that should have been returned, not the endpoint's
        // own 503 with an empty body.
        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        response.Headers.Contains(X402Headers.PaymentRequired).ShouldBeTrue();
        body.ShouldNotBeEmpty();

        // And it must never be a SILENT failure: both endpoint bugs are named in the log.
        server.LoggedErrors.ShouldContain(
            message => message.Contains("ignored PaymentGateResult.CanContinue")
                && message.Contains("wrote past the buffered response cap"));
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

    [Fact]
    public async Task An_endpoint_that_throws_is_seen_by_the_hosts_own_error_handling()
    {
        // PaidServerFixture's own outer middleware stands in for a real app's own
        // UseExceptionHandler/logging (see its remarks): LastServerError only ever gets set by
        // that middleware catching a genuine exception propagating out of the whole pipeline. If
        // X402Middleware swallowed /boom's InvalidOperationException instead of rethrowing it —
        // as it used to — this middleware would never see it, and LastServerError would stay empty
        // even though the response is still a 500.
        await using var server = await PaidServerFixture.StartAsync();
        var payload = await server.SignFor("/boom", KnownAssets.EurcBaseSepolia);

        var response = await server.SendAsync("/boom", payload);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        server.LastServerError.ShouldBe("boom");
    }
}
