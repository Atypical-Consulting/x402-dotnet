using Microsoft.Extensions.DependencyInjection;
using X402.AspNetCore.Facilitator;
using X402.Protocol;
using X402.TestKit;

namespace X402.AspNetCore.Tests;

public sealed class FacilitatorClientTests : IAsyncLifetime
{
    private FakeFacilitator facilitator = null!;

    public ValueTask InitializeAsync()
    {
        facilitator = new FakeFacilitator();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await facilitator.DisposeAsync();

    // HttpFacilitatorClient resolves two named HttpClients from IHttpClientFactory — "x402-verify"
    // and "x402-settle" — instead of one shared policy that inspects the request URI to tell them
    // apart. Both point at the same fake facilitator here; only the resilience behavior applied by
    // HttpFacilitatorClient around each name differs.
    private IFacilitatorClient Client()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        foreach (var name in new[] { "x402-verify", "x402-settle" })
        {
            services.AddHttpClient(name, client =>
                {
                    client.BaseAddress = new Uri("http://localhost");
                    client.Timeout = Timeout.InfiniteTimeSpan;
                })
                .ConfigurePrimaryHttpMessageHandler(() => facilitator.CreateHandler());
        }

        services.AddSingleton<IFacilitatorClient, HttpFacilitatorClient>();

        return services.BuildServiceProvider().GetRequiredService<IFacilitatorClient>();
    }

    [Fact]
    public async Task GetSupportedAsync_reads_the_facilitator_capabilities()
    {
        var supported = await Client().GetSupportedAsync(TestContext.Current.CancellationToken);

        supported.Kinds.ShouldContain(k => k.Scheme == "exact" && k.Network == "eip155:84532");
    }

    [Fact]
    public async Task VerifyAsync_sends_the_specification_request_shape()
    {
        var requirements = TestData.EurcSepoliaRequirements();
        var payload = await TestData.SignedPayloadAsync(requirements);

        var verify = await Client().VerifyAsync(
            payload, requirements, TestContext.Current.CancellationToken);

        verify.IsValid.ShouldBeTrue(verify.InvalidReason);
        var lastRequestBody = facilitator.LastRequestBody.ShouldNotBeNull();
        lastRequestBody.ShouldContain("\"x402Version\":2");
        lastRequestBody.ShouldContain("\"paymentPayload\"");
        lastRequestBody.ShouldContain("\"paymentRequirements\"");
    }

    [Fact]
    public async Task VerifyAsync_reports_a_rejection_without_throwing()
    {
        facilitator.Scenario = FakeFacilitatorScenario.InsufficientFunds;
        var requirements = TestData.EurcSepoliaRequirements();
        var payload = await TestData.SignedPayloadAsync(requirements);

        var verify = await Client().VerifyAsync(
            payload, requirements, TestContext.Current.CancellationToken);

        // A rejection is a response, not a fault: it must not surface as an exception.
        verify.IsValid.ShouldBeFalse();
        verify.InvalidReason.ShouldBe(X402ErrorReason.InsufficientFunds);
    }

    [Fact]
    public async Task VerifyAsync_is_retried_when_the_transport_fails()
    {
        facilitator.FailNextCalls(2, FakeFacilitatorScenario.NetworkFailure);
        var requirements = TestData.EurcSepoliaRequirements();
        var payload = await TestData.SignedPayloadAsync(requirements);

        var verify = await Client().VerifyAsync(
            payload, requirements, TestContext.Current.CancellationToken);

        // verify has no side effect: replaying it on a transport failure is safe and desirable.
        verify.IsValid.ShouldBeTrue(verify.InvalidReason);
        facilitator.VerifyCallCount.ShouldBe(3);
    }

    [Fact]
    public async Task SettleAsync_is_never_retried_after_a_server_error()
    {
        // A 5xx means the facilitator answered: it may already have broadcast the transaction.
        // Replaying would risk a double settlement, which the registry alone would not catch.
        facilitator.FailNextCalls(1, FakeFacilitatorScenario.ServerError);
        var requirements = TestData.EurcSepoliaRequirements();
        var payload = await TestData.SignedPayloadAsync(requirements);

        await Should.ThrowAsync<FacilitatorException>(() => Client().SettleAsync(
            payload, requirements, TestContext.Current.CancellationToken));

        facilitator.SettleCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task SettleAsync_is_retried_when_no_response_was_received()
    {
        facilitator.FailNextCalls(1, FakeFacilitatorScenario.NetworkFailure);
        var requirements = TestData.EurcSepoliaRequirements();
        var payload = await TestData.SignedPayloadAsync(requirements);

        var settle = await Client().SettleAsync(
            payload, requirements, TestContext.Current.CancellationToken);

        settle.Success.ShouldBeTrue(settle.ErrorReason);
        facilitator.SettleCallCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_timeout_surfaces_as_a_facilitator_exception()
    {
        facilitator.Scenario = FakeFacilitatorScenario.Timeout;
        facilitator.TimeoutDelay = TimeSpan.FromSeconds(10);
        var requirements = TestData.EurcSepoliaRequirements() with { MaxTimeoutSeconds = 1 };
        var payload = await TestData.SignedPayloadAsync(requirements);

        var exception = await Should.ThrowAsync<FacilitatorException>(() => Client().VerifyAsync(
            payload, requirements, TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("facilitator");
    }

    [Fact]
    public void EnsureTrailingSlash_keeps_the_base_address_path_segment()
    {
        // Without this, a facilitator configured at "https://host/facilitator" (no trailing slash)
        // would resolve "verify" to "https://host/verify" per Uri's relative-resolution rules,
        // silently dropping the "facilitator" segment — a bug invisible in a test using a bare host.
        var baseAddress = new Uri("https://host.example/facilitator");

        var resolved = new Uri(HttpFacilitatorClient.EnsureTrailingSlash(baseAddress), "verify");

        resolved.ShouldBe(new Uri("https://host.example/facilitator/verify"));
    }
}
