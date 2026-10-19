---
title: The paying client
description: Attach X402PaymentHandler to an HttpClient, cap spending per asset, and handle refusals.
---

# The paying client

`Atypical.X402.Client` turns a 402 into a paid response inside an ordinary `HttpClient`. <xref:X402.Client.X402PaymentHandler> is a `DelegatingHandler`: it reads the demand, picks an asset it may pay, signs an EIP-3009 authorization, and replays the request once. Your calling code contains no payment logic.

## Register and attach

```csharp
using Microsoft.Extensions.DependencyInjection;
using X402.Assets;
using X402.Client.DependencyInjection;
using X402.Client.Signing;
using X402.Networks;

var services = new ServiceCollection();
services.AddX402Client(options =>
{
    options.AllowedNetworks.Add(KnownNetworks.BaseSepolia);
    options.Prefer(KnownAssets.EurcBaseSepolia);
    options.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 0.05m, perSession: 2m);
    options.SetLimits(KnownAssets.UsdcBaseSepolia, perRequest: 0.05m, perSession: 1m);
});
services.AddSingleton<IPaymentSigner>(new PrivateKeyPaymentSigner(
    Environment.GetEnvironmentVariable("X402_PRIVATE_KEY")!));

services.AddHttpClient("Weather", c => c.BaseAddress = new Uri("https://api.example.com"))
    .AddX402Payment();
```

<xref:X402.Client.DependencyInjection.X402ClientExtensions.AddX402Client*> registers the validated <xref:X402.Client.X402ClientOptions>, an in-memory <xref:X402.Client.Spending.ISpendTracker> that enforces them for the life of the process, and the handler. <xref:X402.Client.DependencyInjection.X402ClientExtensions.AddX402Payment*> attaches the handler to one named or typed client. Clients without it never pay.

`AddX402Client` does not register an <xref:X402.Client.Signing.IPaymentSigner>; only your application knows where its key lives. Register <xref:X402.Client.Signing.PrivateKeyPaymentSigner>, or your own implementation backed by an HSM or a KMS.

## Spending limits, per asset

Limits are held per asset identity, meaning network plus contract address, and never aggregated:

- **EURC and USDC never share a cap.** A single cap across both would imply an exchange rate this library does not have ([ADR 0002](../../adr/0002-multi-asset-with-eurc-as-a-first-class-citizen.md)).
- **The same symbol on two networks never shares a cap.** EURC on Base Sepolia and EURC on Base mainnet are different contracts, so you can be generous with play money and strict with real money.
- **An asset with no declared limit is never paid.**

`SetLimits` takes amounts in display units. `perRequest` caps a single payment, `perSession` caps the running total for that asset. There is also an overload keyed by network and symbol, which throws <xref:System.ArgumentException> when <xref:X402.Assets.KnownAssets> has no such entry:

```csharp
options.SetLimits(KnownNetworks.BaseMainnet, "EURC", perRequest: 0.01m, perSession: 0.50m);
```

The limit is checked and reserved **before anything is signed**, so a demand above your limit never produces a usable authorization. When the preferred asset is over its limit, the handler tries the next acceptable one. When every one is, it throws <xref:X402.Client.SpendingLimitExceededException>.

Once a signed authorization has been handed to the transport, its amount stays counted against the session total even if the request then fails, times out or is refused: the server may still settle it. Only a failure before sending, signing for example, returns the budget.

## Which asset gets paid

From the server's `accepts` list, the handler keeps only requirements it can actually pay:

1. the `exact` scheme;
2. a network in `AllowedNetworks` (an empty list allows any network);
3. an asset in <xref:X402.Assets.KnownAssets>, matched by network and contract address (the client never signs against an EIP-712 domain taken from server-supplied data);
4. a well-formed amount;
5. a `maxTimeoutSeconds` between 1 and `MaxAuthorizationValiditySeconds` (3600 by default).

The survivors are ordered by your <xref:X402.Client.X402ClientOptions.Prefer*> calls (the first declared preference wins) and then by the server's own order. Without any `Prefer` call, the server's order decides, and an `X402.AspNetCore` server configured as in these guides offers EURC first. When nothing survives, the handler throws <xref:X402.Client.NoAcceptablePaymentException>.

## Request bodies

To replay a request with a payment attached, its body must be buffered. `MaxBufferedRequestBytes` (1 MiB by default) caps that buffer:

- a request whose declared `Content-Length` exceeds it is sent unbuffered: it works normally against a free endpoint, and a 402 for it is refused with <xref:X402.Client.PaymentRejectedException>, because the body cannot be replayed. Nothing is signed.
- a body of unknown length is buffered, and fails with <xref:System.Net.Http.HttpRequestException> if it outgrows the cap.

```csharp
services.AddX402Client(options =>
{
    options.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 0.10m, perSession: 5m);
    options.MaxBufferedRequestBytes = 8 * 1024 * 1024;
    options.MaxAuthorizationValiditySeconds = 300;
});
```

`MaxAuthorizationValiditySeconds` bounds how long a signed authorization stays spendable. The server chooses `maxTimeoutSeconds`; without this bound it could ask for an authorization valid for decades.

## Start-up validation

The options are validated when something first resolves them: at host start-up under a real `IHost`, or at the first `CreateClient` call for a client carrying the handler otherwise. These fail validation:

- a v1 network shorthand in `AllowedNetworks`;
- no `SetLimits` call at all;
- a zero limit, or a per-session limit below its per-request counterpart;
- a negative `MaxBufferedRequestBytes`, or a `MaxAuthorizationValiditySeconds` below 1.

## Receipts and refusals

```csharp
using X402.Client;

var http = services.BuildServiceProvider()
    .GetRequiredService<IHttpClientFactory>().CreateClient("Weather");

try
{
    var response = await http.GetAsync("/weather/detailed");
    var receipt = response.GetPaymentReceipt(); // null when the route was free
    Console.WriteLine(receipt?.Transaction);
}
catch (PaymentRejectedException ex)
{
    // The server refused the payment, or demanded it again after being paid.
    Console.WriteLine(ex.Reason);                  // e.g. "invalid_exact_evm_insufficient_balance"
    Console.WriteLine(ex.PaymentRequired?.Error);  // the full refused demand, when it decoded
}
catch (X402Exception ex)
{
    // SpendingLimitExceededException or NoAcceptablePaymentException: nothing was signed.
    Console.WriteLine(ex.Message);
}
```

<xref:X402.Client.HttpResponseMessageExtensions.GetPaymentReceipt*> returns the settlement the server reported in `PAYMENT-RESPONSE`. A second 402 after paying is never retried: the handler throws <xref:X402.Client.PaymentRejectedException> carrying the server's stated reason, because an agent paying a third-party API cannot read that server's logs.

The handler works against any x402 v2 server, not only one built with `X402.AspNetCore`.
