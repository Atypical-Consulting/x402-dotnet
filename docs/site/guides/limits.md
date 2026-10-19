---
title: Limits of what’s proven
description: The edges of what this library and its tests establish, stated plainly.
---

# Limits of what’s proven

A library that wants to be a reference implementation earns it by naming its own edges. Read this before writing “it works” anywhere.

## What this library does not claim

- **EURC support is not evidence that a given facilitator settles EURC.** That is the facilitator’s property, and the x402 wire protocol never advertises settled assets: `GET /supported` (<xref:X402.AspNetCore.Facilitator.IFacilitatorClient.GetSupportedAsync*>) returns scheme and network pairs only. `PayingAgent --probe` answers the question empirically, per facilitator, per asset; see [Facilitators](facilitators.md#probe-before-you-rely-on-one).
- **MiCA compliance belongs to the asset’s issuer, never to this library.** EURC is issued by Circle France SAS, licensed as an electronic money institution by the ACPR (register number 17788). x402-dotnet issues nothing, holds nothing and custodies nothing. Any reading of this project as “MiCA-compliant” is a misreading.

## What the tests cannot reach

- **No published conformance vector suite exists.** The x402 Foundation does not publish a cross-language test-vector corpus. This project’s vectors are extracted mechanically from JSON examples in the vendored specification documents, with provenance recorded in [`tests/X402.Core.Tests/vectors/SOURCE.md`](https://github.com/Atypical-Consulting/x402-dotnet/blob/main/tests/X402.Core.Tests/vectors/SOURCE.md). They are the most authoritative fixed data available, not an official suite.
- **No test reaches an on-chain failure mode.** `FakeFacilitator` performs real EIP-712 signature recovery and real validity-window, amount and recipient checks, but it has no ledger; its settlement hash is derived from the nonce. Insufficient balance, a reverted `transferWithAuthorization`, mempool races, or a facilitator reporting success on a transfer that later fails are all outside what any test here can catch. See [Testing with FakeFacilitator](testing.md#what-it-does-not-prove).
- **The in-memory test server is more forgiving than a real transport.** `TestServer` tolerates wire-level inconsistencies, such as a stale `Content-Length`, that a real Kestrel connection rejects. One such defect was found and fixed; the same blind spot could still hide an equivalent one elsewhere in the suite.

## What the runtime leaves to you

- **There is no automatic adaptation to a facilitator’s capabilities.** `GetSupportedAsync` is public surface, but nothing in the server or client package consults it to adjust which assets are offered or accepted. That decision is left to the operator, typically informed by running `--probe` once against the chosen facilitator.
- **A settlement whose outcome is unknown is treated as failed.** If the facilitator times out or the connection resets after the settle request went out, it may still have broadcast the transfer. The server withholds the content and releases the authorization for a retry, which fails on the used nonce: the payer may have paid for content they did not receive. Content is never delivered unpaid; nothing here queries the chain to resolve the ambiguity.
- **An authorization presented again re-delivers its resource, and only that resource**, within the ledger’s retention. Anyone who sees the header can do the same. The full rules are in [Idempotency and settlement](settlement.md#replay-rules).
- **The default ledger is in-memory.** A multi-instance deployment needs a shared <xref:X402.AspNetCore.Idempotency.ISettlementLedger>; see [Multiple instances](settlement.md#multiple-instances).
- **The client never takes back a payment it has sent.** Once an authorization has been sent, its amount stays counted against the session limit even if the request then fails, because the server may still settle it.

## What is exercised so far

The path exercised end to end is Base Sepolia with the `exact` scheme. Base mainnet profiles ship in <xref:X402.Assets.KnownAssets>, read on-chain, but mainnet hardening beyond the Sepolia path is on the [roadmap](https://github.com/Atypical-Consulting/x402-dotnet#roadmap), as are schemes beyond `exact`.
