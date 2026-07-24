# Multi-asset settlement, with EURC as a first-class citizen

## Context and Problem Statement

The brief specified USDC as the settlement asset. Every reference x402 SDK — TypeScript, Python,
Go — ships a default-asset map covering twenty chains with one stablecoin each, all
dollar-denominated; no euro asset appears anywhere. Meanwhile EURC is an EIP-3009 token on both
Base networks, settleable by the `exact` scheme with no protocol extension whatsoever.

Should this library follow the brief and support a single dollar asset, or accept several?

## Decision Drivers

* Euro-denominated billing is the only technically demonstrable gap against the existing SDKs.
* Moving from a single configured asset to a list is a breaking API change if deferred.
* Any automatic EUR/USD conversion would make this library a trusted third party on value, and
  would make the billed amount irreproducible.

## Considered Options

* Multi-asset from the start, EURC first-class
* Multi-asset shape, USDC only enabled
* USDC only, European positioning carried by documentation alone

## Decision Outcome

Chosen: **multi-asset from the start, EURC first-class**. `X402Options.Assets` is a list, prices
are declared per asset, and `KnownAssets` ships verified profiles for EURC and USDC on both Base
networks.

This deviates from the brief, deliberately and with the sponsor's agreement.

## Consequences

* A resource can be offered in euros and dollars at once; the payer settles in what it holds.
* No conversion API exists anywhere in the library, and a test enforces that.
* Spending limits are tracked per asset, never aggregated — a single cap across currencies would
  imply an exchange rate we do not have.
* A facilitator advertises scheme × network pairs but never the assets it settles, so no
  start-up check can confirm EURC support. The `--probe` command answers it empirically instead.
* MiCA compliance belongs to the asset's issuer — Circle France SAS, ACPR EMI register 17788 —
  never to this library. Documentation must not blur that line.
