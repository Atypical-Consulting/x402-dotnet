using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.Facilitator;
using X402.AspNetCore.Idempotency;
using X402.Billing;
using X402.Licensing;

namespace X402.AspNetCore.DependencyInjection;

/// <summary>Lets a consumer replace the pluggable parts of the payment pipeline.</summary>
public interface IX402Builder
{
    /// <summary>The underlying service collection.</summary>
    IServiceCollection Services { get; }
}

internal sealed class X402Builder(IServiceCollection services) : IX402Builder
{
    public IServiceCollection Services { get; } = services;
}

/// <summary>Registration of the x402 server pipeline.</summary>
public static class X402ServiceCollectionExtensions
{
    /// <summary>Registers x402 payment acceptance, bound to a configuration section.</summary>
    public static IX402Builder AddX402(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        services.AddOptions<X402Options>()
            .Bind(section)
            .ValidateOnStart();

        return AddCore(services);
    }

    /// <summary>Registers x402 payment acceptance, configured in code.</summary>
    public static IX402Builder AddX402(this IServiceCollection services, Action<X402Options> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<X402Options>()
            .Configure(configure)
            .ValidateOnStart();

        return AddCore(services);
    }

    private static IX402Builder AddCore(IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<X402Options>, X402OptionsValidator>());

        services.TryAddSingleton<IResolvedAssets, ResolvedAssets>();

        // TryAdd: a commercial package substitutes these implementations without changing the core.
        services.TryAddSingleton<IFeatureGate, AllowAllFeatureGate>();
        services.TryAddSingleton<IPaymentEventSink, LoggerPaymentEventSink>();
        services.TryAddSingleton<ISettlementLedger>(provider => new InMemorySettlementLedger(
            logger: provider.GetRequiredService<ILogger<InMemorySettlementLedger>>()));

        AddFacilitatorClients(services);

        return new X402Builder(services);
    }

    // Two named HttpClients, not one shared policy that inspects the request URI: verify and
    // settle are retried under different rules (see HttpFacilitatorClient), and HttpFacilitatorClient
    // itself builds the resilience pipeline per call, sized to the payment's own MaxTimeoutSeconds.
    // These registrations only need to give each named client its transport: a base address that
    // survives a facilitator URL with a path segment (see EnsureTrailingSlash), and an infinite
    // HttpClient.Timeout so HttpFacilitatorClient's own per-attempt timeout is what actually governs.
    private static void AddFacilitatorClients(IServiceCollection services)
    {
        void ConfigureClient(HttpClient client, IServiceProvider provider)
        {
            var options = provider.GetRequiredService<IOptions<X402Options>>().Value;
            client.BaseAddress = HttpFacilitatorClient.EnsureTrailingSlash(options.FacilitatorUrl!);
            client.Timeout = Timeout.InfiniteTimeSpan;
        }

        services.AddHttpClient("x402-verify", (provider, client) => ConfigureClient(client, provider));
        services.AddHttpClient("x402-settle", (provider, client) => ConfigureClient(client, provider));

        services.TryAddSingleton<IFacilitatorClient, HttpFacilitatorClient>();
    }
}
