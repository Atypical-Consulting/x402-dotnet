# x402-dotnet

The .NET implementation of the x402 v2 payment protocol. EURC and USDC are both first-class
settlement assets on Base, euro offered first — no reference x402 SDK (TypeScript, Python, Go)
ships a euro-denominated asset today.

Packages: `Atypical.X402.AspNetCore` (accept payments), `Atypical.X402.Client` (pay for requests),
`Atypical.X402.Core` (protocol types, transitive). The namespaces are unprefixed — `using X402;`,
`using X402.Client;`, `using X402.AspNetCore;`.

- **Non-custodial by construction.** `X402.AspNetCore` references no signing library and
  `X402Options` exposes no key, secret or mnemonic property.
- **EURC support is this library's property, not any facilitator's.** Whether a given facilitator
  actually settles EURC is that facilitator's own capability — the x402 protocol never advertises
  it. Probe it before you rely on it (`PayingAgent --probe` in the repository's `samples/`).
- **MiCA compliance belongs to EURC's issuer** — Circle France SAS, licensed as an electronic money
  institution by the ACPR, register number 17788 — never to this library, which issues, holds and
  custodies nothing.

Full documentation, runnable samples and architecture decision records:
https://github.com/Atypical-Consulting/x402-dotnet
