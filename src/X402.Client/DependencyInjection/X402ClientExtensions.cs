using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using X402.Client.Signing;
using X402.Client.Spending;

namespace X402.Client.DependencyInjection;

/// <summary>Registration of the x402 paying agent.</summary>
public static class X402ClientExtensions
{
    /// <summary>
    /// Registers a configured, validated <see cref="X402ClientOptions"/>, an
    /// <see cref="ISpendTracker"/> that enforces it for the lifetime of the process, and
    /// <see cref="X402PaymentHandler"/> — ready to be attached to an <see cref="HttpClient"/> with
    /// <see cref="AddX402Payment"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="configure"/> is run through the options system (<c>IValidateOptions</c> plus
    /// <c>ValidateOnStart</c>, the same mechanism <c>X402.AspNetCore</c>'s own <c>X402Options</c>
    /// uses), so a v1 network shorthand in <see cref="X402ClientOptions.AllowedNetworks"/>, no
    /// declared spending limit at all, or a per-session limit set below its per-request counterpart
    /// fails as soon as something resolves <see cref="X402ClientOptions"/> — under a real
    /// <c>IHost</c>, that is host start-up, before any request is dispatched; resolution still
    /// happens once, at the first <c>IHttpClientFactory.CreateClient</c> call for a client carrying
    /// <see cref="X402PaymentHandler"/>, for a bare <see cref="IServiceProvider"/> with no host to
    /// start. Either way it is well before the first paying request, where these three
    /// misconfigurations used to surface instead.
    /// </para>
    /// <para>
    /// This does not register an <see cref="IPaymentSigner"/>: only the consuming application
    /// knows where its key material lives, so it must register one itself — typically a
    /// <see cref="PrivateKeyPaymentSigner"/>, or a custom implementation backed by an HSM or KMS.
    /// That registration is deliberately <em>not</em> validated the same way: this library has no
    /// way to know which named <see cref="HttpClient"/>(s) <see cref="AddX402Payment"/> will be
    /// attached to, since <see cref="IHttpClientFactory"/> builds a named client's handler pipeline
    /// lazily on its first use — there is nothing for a start-up check to eagerly resolve. A
    /// missing <see cref="IPaymentSigner"/> instead fails the first time that pipeline is built,
    /// with an <see cref="InvalidOperationException"/> naming exactly what is missing and how to
    /// register it, rather than the dependency injection container's own generic "unable to
    /// resolve" message.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddX402Client(
        this IServiceCollection services, Action<X402ClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<X402ClientOptions>()
            .Configure(configure)
            .ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<X402ClientOptions>, X402ClientOptionsValidator>());

        // X402ClientOptions itself stays directly resolvable — X402PaymentHandler and
        // InMemorySpendTracker take it as a plain constructor parameter, not
        // IOptions<X402ClientOptions>, so neither type nor any existing caller of either changes.
        // Resolving through IOptions<X402ClientOptions>.Value (rather than a second Configure call
        // building a second instance) means this is the very same, validated instance the options
        // system built.
        services.TryAddSingleton(
            provider => provider.GetRequiredService<IOptions<X402ClientOptions>>().Value);

        services.TryAddSingleton<ISpendTracker, InMemorySpendTracker>();

        // A factory instead of TryAddTransient<X402PaymentHandler>(): see this method's remarks for
        // why a missing IPaymentSigner cannot be caught at start-up the way the rest of this
        // configuration now is. This is the legibility half of that decision — a named,
        // actionable X402 failure in place of the container's own generic "Unable to resolve
        // service for type 'IPaymentSigner' while attempting to activate 'X402PaymentHandler'".
        // Transient, not singleton, like the registration it replaces: IHttpClientFactory rotates
        // and disposes message handlers, and a DelegatingHandler added via AddHttpMessageHandler
        // must not outlive that rotation.
        services.TryAddTransient(provider =>
        {
            // Options resolved first, so a misconfiguration reported by X402ClientOptionsValidator
            // — an OptionsValidationException naming the setting — is what a caller sees when both
            // are wrong, not the signer message below masking it.
            var options = provider.GetRequiredService<X402ClientOptions>();
            var signer = provider.GetService<IPaymentSigner>() ?? throw new InvalidOperationException(
                "X402.Client: no IPaymentSigner is registered. AddX402Client does not register " +
                "one itself — only the consuming application knows where its key material lives. " +
                "Register one before the first paid request, for example: " +
                "services.AddSingleton<IPaymentSigner>(new PrivateKeyPaymentSigner(myPrivateKey)).");

            return new X402PaymentHandler(options, signer, provider.GetRequiredService<ISpendTracker>());
        });

        return services;
    }

    /// <summary>Adds <see cref="X402PaymentHandler"/> to this <see cref="HttpClient"/>'s message handler pipeline.</summary>
    public static IHttpClientBuilder AddX402Payment(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddHttpMessageHandler<X402PaymentHandler>();
    }
}
