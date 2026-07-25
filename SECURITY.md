# Security Policy

## Supported versions

x402-dotnet is pre-1.0 and under active development. Security fixes are applied to the latest
released `0.x` version and the `main` branch only.

## Reporting a vulnerability

Please **do not open a public issue** for security vulnerabilities.

Instead, report privately via GitHub's
[**Report a vulnerability**](https://github.com/Atypical-Consulting/x402-dotnet/security/advisories/new)
(Security → Advisories), or email **philippe@atypical.consulting** with:

- a description of the issue and its impact,
- a minimal reproduction (a request/response pair, a payload, or a short code sample),
- any suggested remediation.

You can expect an acknowledgement within **5 business days**. We'll keep you informed as we work
on a fix and will credit you in the advisory unless you prefer to remain anonymous.

## Scope

x402-dotnet implements the x402 v2 HTTP payment protocol: it builds and verifies EIP-3009 signed
authorizations and talks to a facilitator to verify and settle them. The most relevant classes of
issue are:

- a signature, nonce, validity-window, amount or recipient check that can be bypassed or confused,
- a payload that a facilitator would reject being accepted here (or vice versa),
- a spending limit (`X402ClientOptions`) that can be exceeded,
- anything that would let this library, or code built on it, move funds without an explicit
  payer-signed authorization.

## Out of scope

This library never holds a signing key, custodies funds, or settles a transaction itself — see
[ADR 0003](docs/adr/0003-the-server-never-holds-a-signing-key.md). Vulnerabilities in a specific
facilitator's on-chain settlement, in the underlying blockchain, or in the issuer of an asset
(EURC's issuer is Circle France SAS; USDC's is Circle) are out of scope here — report those to the
facilitator or issuer directly.

Thank you for helping keep x402-dotnet and its users safe.
