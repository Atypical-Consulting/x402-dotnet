# The server never holds a signing key

## Context and Problem Statement

x402 settles on-chain. A resource server could plausibly hold a key — to sponsor gas, to batch
settlements, to custody incoming funds. Each of those turns the operator into a custodian, with
the regulatory and security weight that carries.

## Decision Drivers

* Custody of third-party funds is a regulated activity in most jurisdictions.
* A stolen server key drains every payment routed through it.
* The protocol does not require it: the payer signs, the facilitator broadcasts.

## Decision Outcome

Chosen: **the server holds nothing and signs nothing.** `X402.AspNetCore` references no signing
library, and `X402Options` exposes no key, secret or mnemonic property. Payments move directly
from the payer to the configured `PayTo` address.

## Consequences

* The constraint is structural, not documentary: there is no property to fill in, so no operator
  can accidentally configure custody. A test asserts that no such property appears.
* Gas sponsoring, batching and custody are facilitator concerns. An operator who wants them
  chooses a facilitator that offers them, or runs one.
* Adding a signing dependency to `X402.AspNetCore` supersedes this record; it must not happen
  quietly.
