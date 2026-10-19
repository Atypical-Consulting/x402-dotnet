---
title: Assets and networks
description: KnownAssets, KnownNetworks, CAIP-2 identifiers, describing your own token, and why nothing converts between assets.
---

# Assets and networks

An asset here is a specific token contract on a specific network, described by an <xref:X402.Assets.AssetDescriptor>. A network is a CAIP-2 identifier. Neither is ever a bare ticker symbol.

## The catalogue

<xref:X402.Assets.KnownAssets> ships verified profiles for EURC and USDC on both Base networks. Each value was read on-chain (`name()`, `version()`, `decimals()`), not copied from documentation.

| Member | Network | Contract | EIP-712 name |
| --- | --- | --- | --- |
| `KnownAssets.EurcBaseSepolia` | `eip155:84532` | `0x808456652fdb597867f38412077A9182bf77359F` | `EURC` |
| `KnownAssets.UsdcBaseSepolia` | `eip155:84532` | `0x036CbD53842c5426634e7929541eC2318f3dCF7e` | `USDC` |
| `KnownAssets.EurcBaseMainnet` | `eip155:8453` | `0x60a3E35Cc302bFA44Cb288Bc5a4F316Fdb1adb42` | `EURC` |
| `KnownAssets.UsdcBaseMainnet` | `eip155:8453` | `0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913` | `USD Coin` |

All four use 6 decimals and EIP-712 domain version `2`. Note that USDC's domain name differs between the two networks. A wrong domain value does not fail loudly: it produces a signature that recovers to a different address, and the facilitator rejects it without an actionable reason.

```csharp
using X402.Assets;
using X402.Networks;

if (KnownAssets.TryGet(KnownNetworks.BaseSepolia, "EURC", out var eurc))
{
    Console.WriteLine($"{eurc.Symbol} {eurc.Address} ({eurc.Decimals} decimals)");
}

foreach (var asset in KnownAssets.ForNetwork(KnownNetworks.BaseMainnet))
{
    Console.WriteLine(asset.Symbol); // EURC, then USDC: euro-denominated first
}
```

## EURC first

Every reference x402 SDK (TypeScript, Python, Go) ships one dollar stablecoin per chain and no euro asset. This library settles EURC end to end on equal footing with USDC, and offers the euro first: `KnownAssets.ForNetwork` returns EURC before USDC, the samples price EURC first, and these guides configure `"Assets": [{ "Symbol": "EURC" }, { "Symbol": "USDC" }]` in that order. The order is yours to choose; it becomes the order of the `accepts` array payers see. Why the library departs from the reference SDKs here is recorded in [ADR 0002](../../adr/0002-multi-asset-with-eurc-as-a-first-class-citizen.md).

> [!NOTE]
> EURC support is this library’s property. Whether a given facilitator settles EURC is that facilitator’s property, and the protocol has no way to ask. See [Facilitators](facilitators.md).

## Networks are CAIP-2

x402 v2 identifies every network as `namespace:reference`. <xref:X402.Networks.KnownNetworks> holds the two this library ships profiles for:

| Constant | Value |
| --- | --- |
| `KnownNetworks.BaseSepolia` | `eip155:84532` |
| `KnownNetworks.BaseMainnet` | `eip155:8453` |

The v1 shorthands such as `base-sepolia` are refused everywhere: by server start-up validation, by client start-up validation, and by <xref:X402.Networks.Caip2Network.Parse*>:

```csharp
var network = Caip2Network.Parse("eip155:8453");
Console.WriteLine(network.ChainId);                                // 8453
Console.WriteLine(Caip2Network.TryParse("base-sepolia", out _));   // False
```

## A token outside the catalogue

For a server, the catalogue is a convenience, not an allow-list. Any EIP-3009 token can be accepted by describing it in full in configuration:

```json
{
  "X402": {
    "Assets": [
      { "Symbol": "EURC" },
      {
        "Symbol": "XYZ",
        "Address": "0x0000000000000000000000000000000000000001",
        "Decimals": 18,
        "Eip712Name": "XYZ Token",
        "Eip712Version": "1"
      }
    ]
  }
}
```

and pricing routes with a matching descriptor:

```csharp
using X402.Assets;
using X402.Networks;
using X402.Pricing;

var xyz = new AssetDescriptor
{
    Network = KnownNetworks.BaseSepolia,
    Address = "0x0000000000000000000000000000000000000001",
    Symbol = "XYZ",
    Decimals = 18,
    Eip712Name = "XYZ Token",
    Eip712Version = "1",
};
var price = Price.For(xyz, 0.5m);
```

Read the EIP-712 values from the contract itself. `Address`, `Decimals`, `Eip712Name` and `Eip712Version` are all required when the symbol is not catalogued for the configured network; start-up fails otherwise.

The paying client is stricter on purpose: <xref:X402.Client.X402PaymentHandler> only pays assets listed in `KnownAssets`, because signing for anything else would mean taking the EIP-712 domain from server-supplied data. A spending limit set for an uncatalogued asset is never used.

## No conversion, anywhere

Prices and client spending limits are held per asset, keyed by network and contract address, and never aggregated. There is no exchange rate in this library and no API that asks for one:

- a <xref:X402.Pricing.Price> is always bound to its asset, never a bare number;
- a route priced in EURC and USDC carries two prices you wrote, not one price and a conversion;
- a client limit for EURC never draws down the USDC budget, and the reverse.

A shared cap across EURC and USDC would imply an exchange rate this library does not have, so a test in the repository enforces that no conversion API exists.
