using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using X402.Client.Signing;
using X402.Client.Spending;

namespace X402.Client.DependencyInjection;

/// <summary>Registration of the x402 paying agent.</summary>
public static class X402ClientExtensions
{
    /// <summary>
    /// Registers a configured <see cref="X402ClientOptions"/>, an <see cref="ISpendTracker"/> that
    /// enforces it for the lifetime of the process, and <see cref="X402PaymentHandler"/> — ready to
    /// be attached to an <see cref="HttpClient"/> with <see cref="AddX402Payment"/>.
    /// </summary>
    /// <remarks>
    /// This does not register an <see cref="IPaymentSigner"/>: only the consuming application
    /// knows where its key material lives, so it must register one itself — typically a
    /// <see cref="PrivateKeyPaymentSigner"/>, or a custom implementation backed by an HSM or KMS.
    /// <see cref="X402PaymentHandler"/> resolution fails at the point it is needed if none is
    /// registered.
    /// </remarks>
    public static IServiceCollection AddX402Client(
        this IServiceCollection services, Action<X402ClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new X402ClientOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<ISpendTracker, InMemorySpendTracker>();

        // Transient, not singleton: IHttpClientFactory rotates and disposes message handlers, and
        // a DelegatingHandler registered via AddHttpMessageHandler must not outlive that rotation.
        services.TryAddTransient<X402PaymentHandler>();

        return services;
    }

    /// <summary>Adds <see cref="X402PaymentHandler"/> to this <see cref="HttpClient"/>'s message handler pipeline.</summary>
    public static IHttpClientBuilder AddX402Payment(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddHttpMessageHandler<X402PaymentHandler>();
    }
}
