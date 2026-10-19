---
title: Idempotency and settlement
description: When a payment settles, what happens when it fails, and what an authorization presented twice gets.
---

# Idempotency and settlement

A paid request goes through two facilitator calls. **Verify** runs before the endpoint and moves nothing. **Settle** runs after the endpoint, and moves the funds. Between the two, the endpoint’s response is held back, so that content is never delivered unpaid.

## The order of events

1. The request arrives with a `PAYMENT-SIGNATURE` header. The middleware decodes it and checks it against the route’s price.
2. The facilitator verifies the authorization. A refusal answers 402, with the reason in the `PAYMENT-REQUIRED` header; the endpoint never runs.
3. The ledger is asked for the right to settle this authorization (see below). The endpoint runs, and its response is buffered.
4. The endpoint returns. If its status is below 400, the facilitator settles the authorization.
5. Settled: the buffered response is delivered with a `PAYMENT-RESPONSE` header. Settlement failed: the buffered response is discarded and a 402 goes out instead.

## When nothing is charged

- **An error response is never settled.** A response with a status of 400 or above is delivered as it is, and the authorization stays usable. A failing endpoint costs the payer nothing.
- **A client that disconnected is not charged.** If the request is aborted before settlement starts, settlement does not run. A settlement already under way runs to completion; cancelling it halfway would leave the outcome unknown.
- **A refused verification runs nothing.** Neither the endpoint nor settlement.

## When the outcome is unknown

If the facilitator times out, or the connection resets after the settle request went out, the facilitator may still have broadcast the transfer. The server treats that settlement as **failed**: it withholds the content (402) and releases the authorization for a retry, which then fails on the already-used nonce.

The payer may therefore have paid for content they did not receive. That is the deliberate side of the trade-off: content is never delivered unpaid. It is inherent to settling over HTTP, and nothing here queries the chain to resolve it.

## Large responses

The response is buffered up to `X402Options.MaxBufferedResponseBytes` (1 MiB by default). A response that outgrows the buffer is settled at that point and then streamed. From then on, a failed settlement can no longer withhold the content, because part of it may already be on the wire.

## The settlement ledger

<xref:X402.AspNetCore.Idempotency.ISettlementLedger> guarantees that one authorization settles at most once, whoever presents it and however often. That guarantee is what makes settlement idempotent, not the retry policy. An authorization is identified by a <xref:X402.AspNetCore.Idempotency.PaymentIdentity>: network, token contract and EIP-3009 nonce.

For each paid request, the ledger answers one of three ways (<xref:X402.AspNetCore.Idempotency.SettlementSlotState>):

| State | Meaning | Response |
| --- | --- | --- |
| `Acquired` | This request may settle. | The endpoint runs; settlement follows. |
| `InFlight` | Another request is settling the same authorization right now. | 409 Conflict. |
| `AlreadySettled` | This authorization was settled before. | See the replay rules below. |

### Replay rules

- **The same authorization, sent again to the resource it paid for, gets the content again** without a second settlement, for as long as the ledger remembers it. The endpoint runs again and the memorised settlement is replayed in `PAYMENT-RESPONSE`; the facilitator is not asked to settle twice. Anyone who sees that `PAYMENT-SIGNATURE` header can do the same.
- **“The resource” is the URL, including its query string.** The request body is not part of it, so a replay with a different body that prices the same is honoured.
- **Presented to any other resource, it is refused.**
- **An authorization whose settlement failed is refused** without running the endpoint.
- **An authorization valid for too long is refused.** If its `validBefore` lies more than `MaxTimeoutSeconds` plus 30 seconds of clock skew in the future, it could outlive the ledger’s memory of it, so it is not accepted.

The default ledger, <xref:X402.AspNetCore.Idempotency.InMemorySettlementLedger>, remembers a settlement for at least ten minutes, and never less than `MaxTimeoutSeconds`.

### Multiple instances

The default ledger lives in one process’s memory. Behind a load balancer, two instances would each believe they hold the right to settle the same authorization. Implement `ISettlementLedger` over a shared store and register it; the pipeline registers its own with `TryAdd`, so yours takes precedence:

```csharp
using X402.AspNetCore.DependencyInjection;
using X402.AspNetCore.Idempotency;

builder.Services.AddX402(builder.Configuration.GetSection("X402"));
builder.Services.AddSingleton<ISettlementLedger, RedisSettlementLedger>(); // your implementation
```

Read the remarks on <xref:X402.AspNetCore.Idempotency.ISettlementLedger.AcquireAsync*>, <xref:X402.AspNetCore.Idempotency.ISettlementLedger.CompleteAsync*> and <xref:X402.AspNetCore.Idempotency.ISettlementLedger.AbandonAsync*> before writing one: `CompleteAsync` must never throw or discard an outcome, `AbandonAsync` must never erase a completed settlement, and the lease timeout must stay far above the slowest realistic settlement.

## On the client

The paying client never takes back a payment it has sent. Once <xref:X402.Client.X402PaymentHandler> has handed a signed authorization to the transport, its amount stays counted against the session limit even if the request then fails or is refused, because the server may still settle it. Only a failure before sending returns the budget. A second 402 after paying is never retried.
