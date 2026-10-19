---
title: Protecting endpoints
description: Price routes with UseX402, or price a request from inside its handler with IX402PaymentGate.
---

# Protecting endpoints

There are two ways to demand payment, and both run through the same pipeline. A **route table** prices a URL pattern before the endpoint runs. The **imperative gate** prices a request from inside its handler, once the handler knows what the request costs. Either way, `UseX402` must be in the pipeline: it is the middleware that settles the payment and writes the `PAYMENT-RESPONSE` header on the way out.

## Register the pipeline

<xref:X402.AspNetCore.DependencyInjection.X402ServiceCollectionExtensions.AddX402*> binds <xref:X402.AspNetCore.Configuration.X402Options> from a configuration section, or from code:

```csharp
using X402.AspNetCore.Configuration;
using X402.AspNetCore.DependencyInjection;
using X402.Networks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddX402(options =>
{
    options.PayTo = "0x209693Bc6afc0C5328bA36FaF03C514EF312287C";
    options.Network = KnownNetworks.BaseSepolia;
    options.FacilitatorUrl = new Uri("https://x402.org/facilitator");
    options.Assets.Add(new AssetConfiguration { Symbol = "EURC" });
    options.Assets.Add(new AssetConfiguration { Symbol = "USDC" });
    options.ServiceName = "Weather";
    options.MaxTimeoutSeconds = 120;
});
```

The options are validated when the host starts. It refuses to start on any of these:

- `PayTo` is empty, not an EVM address, or fails its EIP-55 checksum.
- `Network` is not a CAIP-2 identifier (a v1 shorthand such as `base-sepolia` is refused).
- `Assets` is empty, names an uncatalogued symbol without describing it in full, or lists one contract twice.
- `FacilitatorUrl` is missing, relative, or plain `http` anywhere but loopback.
- `MaxTimeoutSeconds` is outside 1 to 3600, `ServiceName` is longer than 32 characters or not printable ASCII, `IconUrl` is not an absolute http(s) URL, or there are more than five `Tags`.

## Price routes

<xref:X402.AspNetCore.DependencyInjection.X402ApplicationBuilderExtensions.UseX402*> takes a route table. Each entry is a route template and a <xref:X402.Pricing.PriceSet>, one price per accepted asset:

```csharp
using X402.AspNetCore.DependencyInjection;
using X402.Assets;
using X402.Pricing;

var detailed = new PriceSet(
[
    Price.For(KnownAssets.EurcBaseSepolia, 0.010m),
    Price.For(KnownAssets.UsdcBaseSepolia, 0.011m),
]);

app.UseX402(routes => routes
    .Map("/weather/detailed", detailed,
        describe: info => info.Description = "Hourly detail: wind, humidity and precipitation chance.")
    .Map("/reports/{id}", Price.For(KnownAssets.EurcBaseSepolia, 0.05m)));

app.MapGet("/weather/detailed", () => Results.Ok(new { WindKph = 14 }));
app.MapGet("/reports/{id}", (string id) => Results.Ok(new { id }));
```

- **Patterns match like ASP.NET Core routing.** A parameter matches exactly one path segment, so `/reports/{id}` never matches `/reports/1/raw` as a prefix.
- **The first matching entry wins**, even when a later one would also match.
- **Order inside a `PriceSet` is the order payers are offered.** Put EURC first, as above, to offer the euro first. A single `Price` converts to a `PriceSet` implicitly.
- **Every price is checked at start-up.** A route priced in an asset, or on a network, that `X402Options` does not accept throws <xref:System.InvalidOperationException> from `UseX402`.

### Writing a price

<xref:X402.Pricing.Price.For*> takes an amount in display units: euros for EURC, dollars for USDC. It refuses an amount that needs more decimals than the asset has, rather than rounding it into a bill nobody wrote. <xref:X402.Pricing.Price.Atomic*> takes the amount already in atomic units, as a string of digits:

```csharp
var oneCent = Price.For(KnownAssets.EurcBaseSepolia, 0.01m);        // AtomicAmount "10000"
var perByte = Price.Atomic(KnownAssets.EurcBaseSepolia, "2048");    // 2048 atomic units
```

A `PriceSet` refuses to be empty, to price one asset twice, or to mix networks. There is no conversion between assets: charging 0.010 EURC and 0.011 USDC for the same resource is a decision you write down, not one this library computes.

## Price inside a handler

When the price depends on the request (its body size, a query, a lookup), inject <xref:X402.AspNetCore.Gate.IX402PaymentGate> and call `RequireAsync` with prices computed for this request:

```csharp
using X402.AspNetCore.Gate;
using X402.Assets;
using X402.Pricing;

app.MapPost("/analyze", AnalyzeAsync);

static async Task AnalyzeAsync(HttpContext context, IX402PaymentGate gate)
{
    using var reader = new StreamReader(context.Request.Body);
    var document = await reader.ReadToEndAsync(context.RequestAborted);
    var bytes = System.Text.Encoding.UTF8.GetByteCount(document).ToString();

    var prices = new PriceSet(
    [
        Price.Atomic(KnownAssets.EurcBaseSepolia, bytes),
        Price.Atomic(KnownAssets.UsdcBaseSepolia, bytes),
    ]);

    var result = await gate.RequireAsync(prices, cancellationToken: context.RequestAborted);
    if (!result.CanContinue)
    {
        await result.Result!.ExecuteAsync(context);
        return;
    }

    await context.Response.WriteAsJsonAsync(
        new { Bytes = bytes, SettledIn = result.SettledAsset!.Symbol }, context.RequestAborted);
}
```

<xref:X402.AspNetCore.Gate.PaymentGateResult.Result> is non-null exactly when `CanContinue` is false. It is a 402 for an ordinary refusal, or a 409 when another request is settling the same authorization at that moment. Both implement `IResult` and `IActionResult`, so from a minimal endpoint that returns a result, or from an MVC controller, `return result.Result;` is always correct:

```csharp
app.MapGet("/quote/{symbol}", async Task<IResult> (string symbol, IX402PaymentGate gate) =>
{
    var result = await gate.RequireAsync(Price.For(KnownAssets.EurcBaseSepolia, 0.002m));
    if (!result.CanContinue)
    {
        return result.Result!;
    }

    return Results.Ok(new { symbol, Price = 101.25m });
});
```

`RequireAsync` throws <xref:System.InvalidOperationException> when it is called outside a request, without `UseX402` in the pipeline, after the response has started, or on a request that is already priced by a route or an earlier call. A request is priced once.

> [!IMPORTANT]
> `CanContinue` means the payment is **verified, not yet settled**. Settlement runs after the handler returns, and if it fails the buffered response is replaced by a 402. Do not do irreversible work that must be paid for first inside the handler. See [Idempotency and settlement](settlement.md).

Always check `CanContinue` before writing anything. If a handler ignores a refusal and writes a success response anyway, the pipeline discards it, delivers the refusal in its place, and logs the substitution at `Error`. That backstop exists to contain a bug, not to replace the check.

## Large responses

A paid response is buffered so that a failed settlement can withhold it. `X402Options.MaxBufferedResponseBytes` (1 MiB by default) caps that buffer. A response that outgrows it is settled first and then streamed, and from that point a failed settlement can no longer withhold the content.

## Recording payments

Each stage of a payment (demanded, verification failed, verified, settlement failed, settled) is reported to an <xref:X402.Billing.IPaymentEventSink>. The default, <xref:X402.Billing.LoggerPaymentEventSink>, writes one log line per event. Register your own implementation to feed a billing ledger:

```csharp
using X402.Billing;

builder.Services.AddX402(builder.Configuration.GetSection("X402"));
builder.Services.AddSingleton<IPaymentEventSink, BillingSink>();

sealed class BillingSink : IPaymentEventSink
{
    public ValueTask RecordAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"{paymentEvent.Status} {paymentEvent.Amount} {paymentEvent.Asset} {paymentEvent.Transaction}");
        return ValueTask.CompletedTask;
    }
}
```

The same pattern replaces the settlement ledger; see [Idempotency and settlement](settlement.md#multiple-instances).
