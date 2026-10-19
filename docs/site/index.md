---
title: Documentation
description: Guides and API reference for x402-dotnet, the .NET implementation of the x402 v2 HTTP payment protocol.
---

# Documentation

x402-dotnet implements [x402 v2](https://x402.org) for .NET. A server demands payment with HTTP 402, a client signs an EIP-3009 `transferWithAuthorization`, a facilitator verifies and settles it, and the request replays and succeeds. EURC and USDC are both first-class settlement assets, and the euro is offered first.

## Start here

| Guide | Read it when |
| --- | --- |
| [Getting started](guides/getting-started.md) | You want a priced endpoint and a client that pays for it. |
| [Protecting endpoints](guides/protecting-endpoints.md) | You are pricing routes with `UseX402`, or pricing inside a handler with `IX402PaymentGate`. |
| [The paying client](guides/paying-client.md) | You are calling a paid API from .NET with `AddX402Payment`. |
| [Assets and networks](guides/assets-and-networks.md) | You need `KnownAssets`, a CAIP-2 identifier, or a token outside the catalogue. |
| [Facilitators](guides/facilitators.md) | You are choosing a facilitator, or calling `IFacilitatorClient` yourself. |
| [Idempotency and settlement](guides/settlement.md) | You need to know exactly when a payment settles, and what a replay gets. |
| [Testing with FakeFacilitator](guides/testing.md) | You are writing tests against the pipeline. |
| [Security model](guides/security.md) | You are reviewing where keys live and what the server can do. |
| [Limits of what’s proven](guides/limits.md) | Before you write “it works” anywhere. |
| [Architecture decisions](../adr/README.md) | You want the reasoning behind a choice that departs from the obvious reading. |

## Three packages, one version

| Package | Namespace | Role |
| --- | --- | --- |
| `Atypical.X402.AspNetCore` | `X402.AspNetCore` | Accept payments: middleware, the imperative gate, the facilitator client, the idempotency ledger. |
| `Atypical.X402.Client` | `X402.Client` | Pay for requests: EIP-3009 signing, the paying `DelegatingHandler`, per-asset spending limits. |
| `Atypical.X402.Core` | `X402` | The wire format: protocol types, transport codec, asset and network catalogues. |

The package ids carry the `Atypical.` prefix and the namespaces do not ([ADR 0004](../adr/0004-prefix-the-package-ids-with-atypical.md)). The [API reference](api/index.md) is generated from the same XML documentation the packages ship for IntelliSense.
