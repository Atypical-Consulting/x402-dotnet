using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using X402.Assets;
using X402.Client.DependencyInjection;
using X402.Client.Signing;
using X402.Networks;

namespace X402.Client.Tests;

/// <summary>
/// Covers <see cref="X402ClientExtensions.AddX402Client"/>'s registration shape itself: that
/// <see cref="X402ClientOptions"/> stays directly resolvable for existing consumers of
/// <see cref="X402PaymentHandler"/>/<see cref="Spending.InMemorySpendTracker"/> even though it now
/// goes through the options system, and that a missing <see cref="IPaymentSigner"/> fails with a
/// named, actionable message instead of the container's own generic one. Validation of the
/// options' own content lives in <see cref="X402ClientOptionsValidationTests"/>.
/// </summary>
public sealed class X402ClientExtensionsTests
{
    // A throwaway key — same shape TestData/samples use elsewhere in this repository, never
    // funded, only exercised for its shape (a valid IPaymentSigner registration).
    private const string SomePrivateKey =
        "0x43da92af0b6c7af92b11f5ceb276329989499043c18c9dab3446903c84ac904a";

    private static ServiceCollection ValidlyConfiguredServices()
    {
        var services = new ServiceCollection();
        services.AddX402Client(options =>
        {
            options.AllowedNetworks.Add(KnownNetworks.BaseSepolia);
            options.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 1m, perSession: 10m);
        });
        return services;
    }

    [Fact]
    public void A_missing_payment_signer_fails_legibly_rather_than_with_the_containers_generic_message()
    {
        using var provider = ValidlyConfiguredServices().BuildServiceProvider();

        // No IPaymentSigner registered: resolving the handler is exactly what IHttpClientFactory
        // does lazily, on the first CreateClient call for a named client carrying it.
        var exception = Should.Throw<InvalidOperationException>(
            () => provider.GetRequiredService<X402PaymentHandler>());

        exception.Message.ShouldContain("IPaymentSigner");
        exception.Message.ShouldContain("AddX402Client");
        exception.Message.ShouldContain("AddSingleton");
    }

    [Fact]
    public void A_registered_payment_signer_resolves_the_handler_successfully()
    {
        var services = ValidlyConfiguredServices();
        services.AddSingleton<IPaymentSigner>(new PrivateKeyPaymentSigner(SomePrivateKey));
        using var provider = services.BuildServiceProvider();

        Should.NotThrow(() => provider.GetRequiredService<X402PaymentHandler>());
    }

    [Fact]
    public void X402ClientOptions_stays_directly_resolvable_for_existing_consumers()
    {
        // X402PaymentHandler and InMemorySpendTracker both take X402ClientOptions as a plain
        // constructor parameter, not IOptions<X402ClientOptions>: bringing AddX402Client into the
        // options system must not be a breaking change for either.
        var services = ValidlyConfiguredServices();
        services.AddSingleton<IPaymentSigner>(new PrivateKeyPaymentSigner(SomePrivateKey));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<X402ClientOptions>();

        options.AllowedNetworks.ShouldContain(KnownNetworks.BaseSepolia);
        // The same singleton every time — and so the same instance the handler itself resolves.
        provider.GetRequiredService<X402ClientOptions>().ShouldBeSameAs(options);
    }

    [Fact]
    public void An_invalid_configuration_fails_before_a_missing_signer_would_even_be_reached()
    {
        var services = new ServiceCollection();
        // Neither a CAIP-2-valid AllowedNetworks entry nor any SetLimits call — invalid on its own
        // terms, and still no IPaymentSigner registered either.
        services.AddX402Client(options => options.AllowedNetworks.Add("base-sepolia"));

        using var provider = services.BuildServiceProvider();

        Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<X402PaymentHandler>());
    }

    [Fact]
    public void The_full_registration_wires_a_working_named_http_client()
    {
        // The shape samples/PayingAgent/Program.cs actually uses: AddHttpClient(...).AddX402Payment(),
        // then IHttpClientFactory.CreateClient — the point at which a real app first builds the
        // handler pipeline.
        var services = ValidlyConfiguredServices();
        services.AddSingleton<IPaymentSigner>(new PrivateKeyPaymentSigner(SomePrivateKey));
        services.AddHttpClient("paid-api", c => c.BaseAddress = new Uri("https://api.test/"))
            .AddX402Payment();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        Should.NotThrow(() => factory.CreateClient("paid-api"));
    }
}
