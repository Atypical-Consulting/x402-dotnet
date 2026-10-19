---
title: Getting started
description: Price an ASP.NET Core endpoint in EURC and pay for it from a .NET client.
---

# Getting started

Five minutes from an empty folder to a priced endpoint, plus a client that pays for it. You need the .NET 10 SDK. You do not need a wallet for the first run.

## 1. Create the API and add the server package

```bash
dotnet new webapi -n PaidApi && cd PaidApi
dotnet add package Atypical.X402.AspNetCore
```

## 2. Say who gets paid, on which network, in which assets

`appsettings.json`:

```json
{
  "X402": {
    "PayTo": "0x209693Bc6afc0C5328bA36FaF03C514EF312287C",
    "Network": "eip155:84532",
    "Assets": [{ "Symbol": "EURC" }, { "Symbol": "USDC" }],
    "FacilitatorUrl": "https://x402.org/facilitator"
  }
}
```

- `PayTo` is the address the funds reach. It must carry a valid EIP-55 checksum.
- `Network` is a CAIP-2 identifier. `eip155:84532` is Base Sepolia; see [Assets and networks](assets-and-networks.md).
- `Assets` lists what this server accepts, in the order payers are offered them. Catalogued symbols resolve against <xref:X402.Assets.KnownAssets> for the configured network.
- `FacilitatorUrl` must be `https`, except on loopback.

Every one of these is validated when the host starts, so a typo fails `dotnet run`, not the first paying request.

## 3. Register the pipeline and price a route

`Program.cs`:

```csharp
using X402.AspNetCore.DependencyInjection;
using X402.Assets;
using X402.Pricing;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddX402(builder.Configuration.GetSection("X402"));
var app = builder.Build();

app.UseX402(routes => routes.Map(
    "/weather", new PriceSet([Price.For(KnownAssets.EurcBaseSepolia, 0.01m)])));

app.MapGet("/weather", () => Results.Ok(new { City = "Brussels", Temp = 19 }));
app.Run();
```

`UseX402` must come before the endpoints it protects: it is the middleware that settles the payment on the way out. Pricing a route in an asset the server has not configured throws <xref:System.InvalidOperationException> from `UseX402`, before the host accepts a request.

Call `/weather` with no payment and you get `402 Payment Required`, with a `PAYMENT-REQUIRED` header listing what the route accepts.

## 4. See it settle without a wallet

The repository's `samples/PaidApi` can run against an in-process fake facilitator that verifies signatures for real and settles anything well-formed, with no network call. It lives in the unpublished `X402.TestKit` project, so it needs a clone of the repository:

```bash
git clone https://github.com/Atypical-Consulting/x402-dotnet && cd x402-dotnet
UseFakeFacilitator=true dotnet run --project samples/PaidApi &
dotnet run --project samples/PayingAgent
```

[`samples/README.md`](https://github.com/Atypical-Consulting/x402-dotnet/blob/main/samples/README.md#running-fully-offline) shows the real, unedited console output of that run. What the fake facilitator proves, and what it cannot, is in [Testing with FakeFacilitator](testing.md).

## 5. Pay for it from a .NET client

```bash
dotnet add package Atypical.X402.Client
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using X402.Assets;
using X402.Client;
using X402.Client.DependencyInjection;
using X402.Client.Signing;
using X402.Networks;

var myPrivateKey = Environment.GetEnvironmentVariable("X402_PRIVATE_KEY")
    ?? throw new InvalidOperationException("Set X402_PRIVATE_KEY before running.");

var services = new ServiceCollection();
services.AddX402Client(options =>
{
    options.AllowedNetworks.Add(KnownNetworks.BaseSepolia);
    options.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 1m, perSession: 10m);
});
services.AddSingleton<IPaymentSigner>(new PrivateKeyPaymentSigner(myPrivateKey));
// Use the URL your API prints on start-up (see its Properties/launchSettings.json).
services.AddHttpClient("PaidApi", c => c.BaseAddress = new Uri("http://localhost:5000"))
    .AddX402Payment();

var http = services.BuildServiceProvider()
    .GetRequiredService<IHttpClientFactory>().CreateClient("PaidApi");

try
{
    var response = await http.GetAsync("/weather"); // 402 -> sign -> replay -> 200, if the wallet can pay
}
catch (PaymentRejectedException ex)
{
    // A fresh, unfunded wallet lands here: the facilitator refuses settlement.
    Console.WriteLine(ex.Reason);
}
```

The signer is the only place a key exists. `AddX402Client` validates its options the moment something resolves them; a missing `IPaymentSigner` fails the first time the client's handler pipeline is built, with a message naming what to register.

> [!NOTE]
> A real facilitator on Base Sepolia refuses an empty wallet, typically with `invalid_exact_evm_insufficient_balance`. That string is passed through verbatim in `PaymentRejectedException.Reason`. Fund the payer with testnet EURC from [faucet.circle.com](https://faucet.circle.com/) to see a real settlement.

## Where next

- Price per request, from inside a handler: [Protecting endpoints](protecting-endpoints.md).
- Choose between EURC and USDC on the client, and cap what it spends: [The paying client](paying-client.md).
- Before production, check that your facilitator actually settles your asset: [Facilitators](facilitators.md).
