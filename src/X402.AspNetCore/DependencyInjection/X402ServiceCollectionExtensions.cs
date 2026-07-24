using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using X402.AspNetCore.Configuration;
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

        return new X402Builder(services);
    }
}
