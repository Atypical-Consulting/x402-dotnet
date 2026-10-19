---
title: Facilitators
description: What the facilitator does, how the server talks to it, and how to find out whether it settles your asset.
---

# Facilitators

The facilitator is the service that verifies a signed authorization and broadcasts its settlement on-chain. It is a service you choose and configure with `X402:FacilitatorUrl`. It is not part of this library, and this library does not run one.

## The client the server uses

The server talks to the facilitator through <xref:X402.AspNetCore.Facilitator.IFacilitatorClient>:

| Member | Wire call | Moves funds |
| --- | --- | --- |
| `VerifyAsync` | `POST verify` | No |
| `SettleAsync` | `POST settle` | Yes |
| `GetSupportedAsync` | `GET supported` | No |

A rejection is a response, not a fault: `VerifyAsync` returns a <xref:X402.Protocol.VerifyResponse> with `IsValid` false rather than throwing. Only transport failures, timeouts and unusable responses raise <xref:X402.AspNetCore.Facilitator.FacilitatorException>. A rejection reason a facilitator returns is passed through verbatim, whether or not it is one of the documented <xref:X402.Protocol.X402ErrorReason> constants.

The default implementation retries `verify` and `settle` under different rules, because only one of them has a side effect:

- **verify** is retried on a transport failure, an attempt timeout or a 5xx.
- **settle** is retried only when no response arrived at all. A facilitator that answered, even with a 500, may already have broadcast the transfer.

Each attempt's timeout follows the payment's own `MaxTimeoutSeconds`. The two calls go through two named `HttpClient`s, `x402-verify` and `x402-settle`, so their transport can be replaced; the [testing guide](testing.md) uses exactly that seam.

## What `/supported` can and cannot tell you

`GetSupportedAsync` returns scheme and network pairs, such as `exact` on `eip155:84532`, plus extensions and signers. It never lists the assets behind them. No start-up check can therefore confirm that a facilitator settles EURC, or any other token.

> [!IMPORTANT]
> This library supporting EURC is not evidence that a given facilitator settles it. That is the facilitator’s property, and the x402 wire protocol never advertises it.

Nothing in `X402.AspNetCore` or `X402.Client` calls `GetSupportedAsync` to adjust which assets are offered or accepted. There is no automatic adaptation to a facilitator’s capabilities: the operator decides. You can call it yourself:

```csharp
using X402.AspNetCore.Facilitator;

var facilitator = app.Services.GetRequiredService<IFacilitatorClient>();
var supported = await facilitator.GetSupportedAsync();
foreach (var kind in supported.Kinds)
{
    Console.WriteLine($"{kind.Scheme} {kind.Network}");
}
```

## Probe before you rely on one

`samples/PayingAgent --probe` answers the question the protocol cannot, empirically. For each configured asset it attempts a real settlement of one atomic unit (0.000001 EURC or USDC) against the facilitator you name, and reports what happened:

```bash
export X402_FACILITATOR_URL=https://your-candidate-facilitator.example
export X402_PRIVATE_KEY=0x...   # a key funded with the asset you want to test
dotnet run --project samples/PayingAgent -- --probe
```

An empty wallet cannot tell “this facilitator does not settle EURC” apart from “this wallet holds no EURC”: both refuse the same way. Fund the probing key with one asset only, from [faucet.circle.com](https://faucet.circle.com/), and run it again to see the two outcomes differ. `--probe --fake` runs the same probe against the in-process fake facilitator, which settles anything well-formed: it shows the probe’s shape and teaches nothing about a real facilitator.

The real output of a probe against `https://x402.org/facilitator` is in [`samples/README.md`](https://github.com/Atypical-Consulting/x402-dotnet/blob/main/samples/README.md#the-facilitator-probe).

## Replacing the client

`IFacilitatorClient` is registered with `TryAdd`, so an implementation you register yourself wins. Most of the time you do not need to: replacing the primary handler of the two named clients changes the transport while keeping the retry rules above.
