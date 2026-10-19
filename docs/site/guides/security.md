---
title: Security model
description: Where keys live, what the server can and cannot do, and the guarantees each side enforces.
---

# Security model

x402-dotnet is non-custodial by construction. The server holds nothing and signs nothing; the only key in the system belongs to the payer. That is enforced by what the code cannot express, not by a line in the documentation.

## The server never holds a signing key

- `Atypical.X402.AspNetCore` references **no signing library**.
- <xref:X402.AspNetCore.Configuration.X402Options> exposes **no key, secret or mnemonic property**, so no operator can configure custody by accident. A test asserts that none appears.
- Payments move directly from the payer to the configured `PayTo` address. The operator never holds funds or key material.
- Gas sponsoring, batching and custody are facilitator concerns.

This is recorded in [ADR 0003](../../adr/0003-the-server-never-holds-a-signing-key.md). Adding a signing dependency to the server package would supersede that record; it must not happen quietly.

## The payer’s key stays behind one interface

On the client, <xref:X402.Client.Signing.IPaymentSigner> is the only place a key is used. <xref:X402.Client.Signing.PrivateKeyPaymentSigner> holds one in memory; when that is not acceptable, implement `IPaymentSigner` over an HSM or a KMS. `AddX402Client` deliberately registers no signer: only your application knows where its key lives.

The paying handler limits what a hostile or careless server can extract from that key:

- **Limits before signatures.** The spending limit is checked and reserved before anything is signed, so a demand above the limit never produces a usable authorization, in memory or in a log.
- **No domain from the wire.** The client only pays assets in <xref:X402.Assets.KnownAssets>, matched by network and contract address. It never signs against an EIP-712 domain taken from server-supplied data.
- **Bounded validity.** `MaxAuthorizationValiditySeconds` (3600 by default) caps how long a signed authorization stays spendable, whatever `maxTimeoutSeconds` the server asks for.
- **Allowed networks first.** Requirements are filtered by `AllowedNetworks` before any asset preference is applied, so a multi-network offer cannot steer a symbol preference onto the wrong chain’s contract.
- **One replay, no loop.** A second 402 after paying is never retried.
- **Spent stays spent.** Once an authorization has left the process it stays counted against the session budget, so a flaky or hostile server cannot collect one authorization per retry while the budget never moves.

## What the server guarantees

- **Content is never delivered unpaid.** A paid response is buffered until settlement succeeds; a failed or unknown settlement withholds it (up to `MaxBufferedResponseBytes`, see [Idempotency and settlement](settlement.md)).
- **One authorization settles at most once.** The settlement ledger refuses a concurrent second settlement with 409 and replays a completed one without calling the facilitator.
- **One authorization unlocks one resource.** The EIP-3009 signature does not cover the URL, so the server binds a settled authorization to the resource it paid for and refuses it anywhere else.
- **Misconfiguration fails at start-up.** An invalid or non-checksummed `PayTo`, a v1 network name, a non-https facilitator, or a route priced in an asset the server does not accept stops the host before it serves a request.

## What it does not guarantee

- **An authorization presented again re-delivers its resource.** Within the ledger’s retention, anyone who sees a `PAYMENT-SIGNATURE` header can replay it against the same URL and get the content again, without a second charge. Treat that header as a bearer credential for that one resource.
- **An unknown settlement outcome is treated as failed**, so a payer can, rarely, pay for content they did not receive. Nothing here queries the chain.
- **Compliance is not this library’s property.** EURC is issued by Circle France SAS, licensed as an electronic money institution by the ACPR (register number 17788). x402-dotnet issues nothing, holds nothing and custodies nothing: it moves a signed authorization from a payer to a facilitator. MiCA compliance belongs to the asset’s issuer, never to this library.

Report a vulnerability through the process in [`SECURITY.md`](https://github.com/Atypical-Consulting/x402-dotnet/blob/main/SECURITY.md), not in a public issue.
