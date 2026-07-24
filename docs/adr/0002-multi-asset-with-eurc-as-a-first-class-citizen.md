# Multi-asset settlement, with EURC as a first-class citizen

## Context and Problem Statement

The x402 v2 protocol must support settlement in multiple stablecoin denominations to accommodate operators and payers in different regions and with different preferences. The European market requires euro-denominated settlement, while other markets may prefer USD-denominated stablecoins. The library must provide a clean, safe API for working with multiple assets while maintaining strong guarantees about precision, network consistency, and EIP-712 signature security.

## Decision Drivers

* Need to support both EURC (euro-denominated stablecoin) and USDC (dollar-denominated stablecoin) as first-class settlement options
* EURC should be the default and primary recommendation for operators
* EIP-712 domain values vary between networks and tokens (USDC has different domain names on Base Sepolia vs Base mainnet) and must be read from on-chain data, not documentation
* Price precision must be exact — no silent rounding that would bill an amount the operator did not write
* Assets from different networks cannot be mixed in a single payment demand
* The library should not provide any currency conversion, exchange rates, or multi-asset arbitrage APIs

## Considered Options

* Single-asset library with manual per-operator asset configuration
* Hard-coded multi-asset support with documentation-sourced EIP-712 domain values
* Flexible multi-asset catalogue with on-chain EIP-712 values and strict validation
* Allow any combination of assets across networks in a single price set

## Decision Outcome

Chosen option: "Flexible multi-asset catalogue with on-chain EIP-712 values and strict validation", because it provides both convenience (KnownAssets catalogue) and safety (validated on-chain values, network consistency, precision guarantees), establishes EURC as the default while fully supporting USDC, and makes the cost of mistakes (wrong EIP-712 domains, silent rounding, network mixing) immediately visible through compile-time and test-time failures.

### Consequences

* Good, because operators can easily configure common assets (EURC and USDC on both networks) through the catalogue
* Good, because the library refuses silent rounding — any precision loss is caught at price construction time
* Good, because network consistency is enforced — a single PriceSet cannot mix Base Sepolia and Base mainnet assets
* Good, because EIP-712 domain values are locked in tests, catching any accidental "harmonization" that would break mainnet payments
* Good, because EURC is listed first in ForNetwork, establishing it as the default recommendation
* Good, because the public API provides no currency conversion, keeping the library's responsibilities clear
* Bad, because operators must use Price.Atomic if they need to work with assets not in the catalogue
* Bad, because EIP-712 domain values must be re-verified on-chain whenever the library updates them, adding operational overhead
