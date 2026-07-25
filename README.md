![x402-dotnet](.github/banner.png)

# x402-dotnet

> **Accept and pay x402 v2 HTTP payments in .NET — EURC and USDC as equal, first-class settlement assets, with a server that never touches a signing key.**

<!-- Rangée 1 — Identité -->
[![Atypical-Consulting - x402-dotnet](https://img.shields.io/static/v1?label=Atypical-Consulting&message=x402-dotnet&color=blue&logo=github)](https://github.com/Atypical-Consulting/x402-dotnet)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-yellow.svg)](LICENSE)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![stars](https://img.shields.io/github/stars/Atypical-Consulting/x402-dotnet?style=social)](https://github.com/Atypical-Consulting/x402-dotnet/stargazers)
[![forks](https://img.shields.io/github/forks/Atypical-Consulting/x402-dotnet?style=social)](https://github.com/Atypical-Consulting/x402-dotnet/network/members)

<!-- Rangée 2 — Activité -->
[![tag](https://img.shields.io/github/tag/Atypical-Consulting/x402-dotnet?include_prereleases=&sort=semver&color=blue)](https://github.com/Atypical-Consulting/x402-dotnet/releases/)
[![issues](https://img.shields.io/github/issues/Atypical-Consulting/x402-dotnet)](https://github.com/Atypical-Consulting/x402-dotnet/issues)
[![pull requests](https://img.shields.io/github/issues-pr/Atypical-Consulting/x402-dotnet)](https://github.com/Atypical-Consulting/x402-dotnet/pulls)
[![last commit](https://img.shields.io/github/last-commit/Atypical-Consulting/x402-dotnet)](https://github.com/Atypical-Consulting/x402-dotnet/commits/main)

<!-- Rangée 3 — Qualité -->
[![CI](https://github.com/Atypical-Consulting/x402-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/Atypical-Consulting/x402-dotnet/actions/workflows/ci.yml)

<!-- Rangée 4 — Distribution (trois paquets, une version partagée) -->
[![NuGet X402.Core](https://img.shields.io/nuget/v/X402.Core?logo=nuget&label=X402.Core&color=004880)](https://www.nuget.org/packages/X402.Core/)
[![NuGet X402.AspNetCore](https://img.shields.io/nuget/v/X402.AspNetCore?logo=nuget&label=X402.AspNetCore&color=004880)](https://www.nuget.org/packages/X402.AspNetCore/)
[![NuGet X402.Client](https://img.shields.io/nuget/v/X402.Client?logo=nuget&label=X402.Client&color=004880)](https://www.nuget.org/packages/X402.Client/)

<!-- Rangée 5 — Docs (pas de site hébergé ; les ADR font office de documentation d'architecture) -->
[![Docs: ADRs](https://img.shields.io/badge/docs-architecture_decisions-3245b8)](docs/adr)

## Table of Contents

- [Why x402-dotnet?](#why-x402-dotnet)
- [Install](#install)
- [Quick Start](#quick-start)
- [Features](#features)
- [Usage](#usage)
- [API](#api)
- [Limits of what's proven here](#limits-of-whats-proven-here)
- [Tech Stack](#tech-stack)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)

## Why x402-dotnet?

**The problem.** [x402](https://x402.org) turns HTTP 402 into a working payment protocol: a server
demands payment, a client signs an EIP-3009 authorization, a facilitator verifies and settles it,
the request replays and succeeds. Every reference SDK — TypeScript, Python, Go — ships a
default-asset map covering twenty chains, one stablecoin each, all dollar-denominated. No euro
asset appears anywhere in any of them. A .NET service that wants to charge in euros, or a .NET
agent that wants to pay in euros, has had no reference implementation and no first-class euro
asset to reach for.

**The solution.** x402-dotnet settles EURC end to end — signing, verification, settlement,
per-asset spending limits — on equal footing with USDC, euro offered first
([ADR 0002](docs/adr/0002-multi-asset-with-eurc-as-a-first-class-citizen.md)). The server side,
`X402.AspNetCore`, is structurally non-custodial: it references no signing library, and
`X402Options` exposes no key, secret or mnemonic property to fill in
([ADR 0003](docs/adr/0003-the-server-never-holds-a-signing-key.md)). Payments move directly from
payer to `PayTo`; the operator never holds funds or key material.

**What this library does not claim.** Two things it would be easy to overstate, so this README
says them plainly instead:

- **EURC support is this library's property. Whether a *given* facilitator settles EURC is that
  facilitator's property, and the x402 wire protocol has no way to ask it directly** —
  `GET /supported` (`IFacilitatorClient.GetSupportedAsync`) returns scheme-and-network pairs only,
  never the tokens behind them. `PayingAgent --probe` (in [`samples/`](samples/)) answers the
  question empirically: it attempts a real one-atomic-unit settlement per configured asset against
  a facilitator of your choosing and reports what actually happened. Run it against whichever
  facilitator you're evaluating before you rely on it in production — this library supporting
  EURC is not evidence that your facilitator will settle EURC.
- **MiCA compliance belongs to the asset's issuer, never to this library.** EURC is issued by
  Circle France SAS, licensed as an électronic money institution by the ACPR (register number
  17788). x402-dotnet issues nothing, holds nothing and custodies nothing — it moves a signed
  authorization from a payer to a facilitator. Any reading of this project as "MiCA-compliant" is
  a misreading; see [ADR 0002](docs/adr/0002-multi-asset-with-eurc-as-a-first-class-citizen.md)
  for the record.

## Install

```bash
dotnet add package X402.AspNetCore   # accept payments in an ASP.NET Core app
dotnet add package X402.Client       # pay for HTTP requests from any .NET client
```

`X402.Core` (protocol types, transport codec) is a transitive dependency of both and is published
separately for tooling that only needs the wire format.

## Quick Start

Five minutes, no wallet required for the first run — the fake facilitator in step 4 settles
anything well-formed without touching a network.

**1. Create the API and add the server package.**

```bash
dotnet new webapi -n PaidApi && cd PaidApi
dotnet add package X402.AspNetCore
```

**2. Configure who gets paid, on which network, in which assets** (`appsettings.json`):

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

**3. Register the pipeline and price a route** (`Program.cs`):

```csharp
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

**4. Run it offline, with no facilitator and no wallet**, to see the whole pipeline settle for
real without leaving your machine — see [`samples/README.md`](samples/README.md#running-fully-offline)
for the `UseFakeFacilitator` switch and its unedited console output.

**5. Pay for it from a .NET client:**

```csharp
using Microsoft.Extensions.DependencyInjection;
using X402.Assets;
using X402.Client;
using X402.Client.DependencyInjection;
using X402.Client.Signing;
using X402.Networks;

var services = new ServiceCollection();
services.AddX402Client(options =>
{
    options.AllowedNetworks.Add(KnownNetworks.BaseSepolia);
    options.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 1m, perSession: 10m);
});
services.AddSingleton<IPaymentSigner>(new PrivateKeyPaymentSigner(myPrivateKey));
services.AddHttpClient("PaidApi", c => c.BaseAddress = new Uri("http://localhost:5000"))
    .AddX402Payment();

var http = services.BuildServiceProvider()
    .GetRequiredService<IHttpClientFactory>().CreateClient("PaidApi");
var response = await http.GetAsync("/weather"); // 402 -> sign -> replay -> 200, handled for you
```

The two runnable projects this is distilled from — `samples/PaidApi` and `samples/PayingAgent` —
include the facilitator probe, dynamic per-request pricing, and real console output captured from
actual runs. Start there for anything beyond the shape above: [`samples/README.md`](samples/README.md).

## Features

- **EURC and USDC as equals, euro first** — `KnownAssets` ships verified on-chain profiles for
  both, on both Base networks; a resource can be priced in either, or both at once, and the payer
  settles in whichever it holds.
- **Structurally non-custodial** — `X402.AspNetCore` has no signing dependency and `X402Options`
  has no key property; there is nothing an operator could misconfigure into custody.
- **Fails at start-up, not at the first payment** — pricing a route in an asset the server hasn't
  configured throws `InvalidOperationException` from `UseX402` before the host ever accepts a
  request, not on the first payer's 402.
- **Per-asset spending limits on the client** — `X402ClientOptions` keys limits by network and
  contract address, never by ticker symbol alone or aggregated across currencies, so EURC and USDC
  (and the same symbol on two networks) never share a cap.
- **A facilitator simulator that signs for real** — `X402.TestKit`'s `FakeFacilitator` performs
  genuine EIP-712 signature recovery and validity-window/amount/recipient checks; see
  [Limits of what's proven here](#limits-of-whats-proven-here) for what it still can't catch.
- **The facilitator capability probe** — `PayingAgent --probe` answers, empirically, the one
  question the x402 wire protocol cannot: does *this* facilitator actually settle *this* asset.

## Usage

**Dynamic pricing from inside a handler**, when the price depends on the request (see
`samples/PaidApi/Program.cs`):

```csharp
static async Task AnalyzeAsync(HttpContext context, IX402PaymentGate gate)
{
    var prices = new PriceSet([Price.Atomic(KnownAssets.EurcBaseSepolia, bytesAsString)]);
    var result = await gate.RequireAsync(prices, cancellationToken: context.RequestAborted);
    if (!result.CanContinue)
    {
        await result.Result!.ExecuteAsync(context);
        return;
    }
    // result.SettledAsset is set; the payment already happened.
}
```

**Reading the receipt on the client side**, and handling the one exception a well-formed refusal
raises:

```csharp
try
{
    var response = await http.GetAsync("/weather/detailed");
    var receipt = response.GetPaymentReceipt(); // null when the route was free
}
catch (PaymentRejectedException ex)
{
    // The facilitator refused settlement — e.g. an empty testnet wallet.
    // See samples/README.md for the real, unedited failure this looks like.
}
```

**Probing a facilitator before you commit to it:**

```bash
export X402_FACILITATOR_URL=https://your-candidate-facilitator.example
dotnet run --project samples/PayingAgent -- --probe
```

## API

Full XML documentation ships inside every package (`GenerateDocumentationFile` is on repo-wide),
so IntelliSense and IDE hover carry the same text as the source. The surface a typical consumer
touches:

| Package | Type | What it's for |
| --- | --- | --- |
| `X402.AspNetCore` | `AddX402`, `UseX402` | Register and install the server payment pipeline |
| `X402.AspNetCore` | `X402Options` | Payee, network, accepted assets, facilitator — bound and validated at start-up |
| `X402.AspNetCore` | `IX402PaymentGate` | Imperative gate for handlers whose price isn't known until the request is read |
| `X402.AspNetCore` | `IFacilitatorClient` | `VerifyAsync`, `SettleAsync`, `GetSupportedAsync` — the facilitator wire calls |
| `X402.Client` | `AddX402Client`, `AddX402Payment` | Register and attach the paying `DelegatingHandler` to an `HttpClient` |
| `X402.Client` | `X402ClientOptions` | Allowed networks, asset preferences, per-asset spending limits |
| `X402.Client` | `IPaymentSigner`, `PrivateKeyPaymentSigner` | Where signing happens — bring your own for an HSM or KMS |
| `X402.Client` | `HttpResponseMessageExtensions.GetPaymentReceipt` | Reads the settlement receipt off a response |
| `X402.Core` | `KnownAssets`, `KnownNetworks` | Verified EURC/USDC profiles and CAIP-2 network identifiers for Base |
| `X402.Core` | `Price`, `PriceSet` | Declaring what a route or handler costs, per asset |
| `X402.Core` | `X402ErrorReason` | Documented protocol error codes — a facilitator may still return one that isn't in this list, passed through verbatim rather than normalized |

## Limits of what's proven here

Stated plainly, because a library aiming to be a reference implementation earns that by naming
its own edges:

- **No published conformance vector suite exists.** The x402 Foundation does not publish a
  cross-language test-vector corpus. This project's vectors are extracted mechanically from the
  JSON examples embedded in the specification documents themselves, vendored at commit
  [`90688e52`](https://github.com/x402-foundation/x402/commit/90688e52e58ae9185f2860988bd2c46d2801ceda)
  with provenance recorded in
  [`tests/X402.Core.Tests/vectors/SOURCE.md`](tests/X402.Core.Tests/vectors/SOURCE.md), and
  re-synchronizable with [`scripts/sync-vectors.sh`](scripts/sync-vectors.sh). Treat them as the
  most authoritative fixed data available, not as an official suite — because none exists.
- **No test in this repository reaches an on-chain failure mode.** `X402.TestKit`'s
  `FakeFacilitator` performs real EIP-712 signature recovery and real validity-window, amount and
  recipient checks — but it has no ledger; its settlement transaction hash is derived from the
  nonce. Insufficient on-chain balance, a reverted `transferWithAuthorization`, mempool races, or a
  facilitator that reports success on a transfer that later fails are all outside what any test
  here can catch. `samples/README.md` shows what the *real* facilitator does instead — refuses an
  empty wallet with `invalid_exact_evm_insufficient_balance`, a code that isn't even one of
  `X402ErrorReason`'s documented constants, propagated verbatim rather than guessed at.
- **There is no automatic adaptation to a facilitator's capabilities.** `GetSupportedAsync` exists
  as public surface on `IFacilitatorClient` for library users to call — but nothing inside
  `X402.AspNetCore` or `X402.Client` consults it to adjust which assets are offered or accepted.
  That decision is left to the operator, typically informed by running `--probe` once against the
  chosen facilitator.

## Tech Stack

- **.NET 10.0**, `TreatWarningsAsErrors` on, nullable reference types on
- **ASP.NET Core** — the server-side payment middleware and imperative gate
- **`Nethereum.Signer.EIP712`** — client-side EIP-712 typed-data signing for `transferWithAuthorization`
- **`Microsoft.Extensions.Http.Resilience`** — retry/timeout policy around facilitator calls
- **`System.Text.Json`** with source-generated contexts for the wire codec
- **xUnit v3 + Shouldly** — 245 tests across five test projects, none of them network-dependent

## Roadmap

- [ ] Base mainnet hardening beyond the Base Sepolia path exercised so far
- [ ] Coverage for x402 schemes beyond `exact` — facilitators already advertise `upto` and
      `batch-settlement` (see `PayingAgent --probe` output in
      [`samples/README.md`](samples/README.md#the-facilitator-probe))
- [ ] Optional, opt-in facilitator-capability adaptation building on `GetSupportedAsync`, without
      changing the default of "the operator decides" (see
      [Limits of what's proven here](#limits-of-whats-proven-here))

See the [open issues](https://github.com/Atypical-Consulting/x402-dotnet/issues) for the complete,
current list.

## Contributing

Contributions are welcome — see [`CONTRIBUTING.md`](CONTRIBUTING.md) for the build, test and
architecture-decision workflow, and [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md) for how we expect
to treat each other. Commit messages and PR titles follow
[Conventional Commits](https://www.conventionalcommits.org/) — `release-please` derives every
package's version from them, so a non-conforming title is rejected before merge.

1. Fork → branch (`git checkout -b feat/my-feature`)
2. Commit (`git commit -m 'feat: ...'`)
3. Push + Pull Request

Security issues: see [`SECURITY.md`](SECURITY.md) — please don't open a public issue for those.

## License

Distributed under the Apache-2.0 license. See [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).

---

© 2026 Atypical Consulting / Philippe Matray
