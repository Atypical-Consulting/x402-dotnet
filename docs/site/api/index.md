---
title: API reference
description: Every public type in Atypical.X402.AspNetCore, Atypical.X402.Client and Atypical.X402.Core, generated from their XML documentation.
---

# API reference

Generated from the XML documentation that ships inside each package, so this is the same text IntelliSense shows. The package ids carry the `Atypical.` prefix; the namespaces do not.

## Atypical.X402.AspNetCore

Accept payments in ASP.NET Core. Start with <xref:X402.AspNetCore.DependencyInjection.X402ServiceCollectionExtensions.AddX402*>, <xref:X402.AspNetCore.DependencyInjection.X402ApplicationBuilderExtensions.UseX402*> and <xref:X402.AspNetCore.Gate.IX402PaymentGate>.

| Namespace | Contents |
| --- | --- |
| <xref:X402.AspNetCore.DependencyInjection> | `AddX402`, `UseX402`: registering and installing the pipeline. |
| <xref:X402.AspNetCore.Configuration> | `X402Options` and the asset configuration bound from `X402`. |
| <xref:X402.AspNetCore.Middleware> | The route table and the resource description advertised in a demand. |
| <xref:X402.AspNetCore.Gate> | The imperative gate, for prices computed from the request. |
| <xref:X402.AspNetCore.Engine> | The results the pipeline hands back: 402 and 409. |
| <xref:X402.AspNetCore.Facilitator> | `IFacilitatorClient`: verify, settle, supported. |
| <xref:X402.AspNetCore.Idempotency> | The settlement ledger that settles each authorization at most once. |

## Atypical.X402.Client

Pay for requests from any .NET client. Start with <xref:X402.Client.DependencyInjection.X402ClientExtensions.AddX402Client*> and <xref:X402.Client.X402ClientOptions>.

| Namespace | Contents |
| --- | --- |
| <xref:X402.Client> | The paying handler, its options, its exceptions, the receipt accessor. |
| <xref:X402.Client.DependencyInjection> | `AddX402Client`, `AddX402Payment`. |
| <xref:X402.Client.Signing> | `IPaymentSigner`, the in-memory signer, EIP-3009 typed data. |
| <xref:X402.Client.Spending> | Per-asset spending limits and the tracker that enforces them. |

## Atypical.X402.Core

The wire format, with no ASP.NET Core, signing, network or telemetry dependency.

| Namespace | Contents |
| --- | --- |
| <xref:X402.Assets> | `AssetDescriptor` and the verified `KnownAssets` catalogue. |
| <xref:X402.Networks> | CAIP-2 identifiers and `KnownNetworks`. |
| <xref:X402.Pricing> | `Price` and `PriceSet`, always per asset. |
| <xref:X402.Protocol> | The x402 v2 payloads, requirements, facilitator contracts and error reasons. |
| <xref:X402.Transport> | Header names and the base64 JSON codec. |
| <xref:X402.Json> | The serializer options the codec uses. |
| <xref:X402.Billing> | Payment events, and the sink that records them. |
| <xref:X402.Licensing> | Feature gates, an extension point for add-on packages. |
| <xref:X402.Cryptography> | Keccak-256. |
