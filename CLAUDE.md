# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

**x402-dotnet** is the .NET implementation of the [x402 v2](https://x402.org) HTTP payment
protocol: a server demands payment via HTTP 402, a client signs an EIP-3009
`transferWithAuthorization`, a facilitator verifies and settles it, the request replays and
succeeds. EURC and USDC are both first-class settlement assets, euro offered first — see
[ADR 0002](docs/adr/0002-multi-asset-with-eurc-as-a-first-class-citizen.md) for why that's a
deliberate deviation from every reference SDK.

Three packages, one shared version (see `Directory.Build.props`, driven by `release-please`):

```
X402.Core        protocol types, transport codec, extension points
                 no ASP.NET Core reference, no signing library, no network call, no telemetry
  ↑
X402.AspNetCore  server middleware, imperative gate, facilitator client, idempotency ledger
                 no signing library reference, no key/secret/mnemonic property anywhere
X402.Client      EIP-3009 signing, the paying DelegatingHandler, per-asset spending limits
```

**Project names and package ids differ on purpose.** The projects, namespaces and assemblies are
`X402.*`; the published ids are `Atypical.X402.*`. The bare `X402.Core` and `X402.Client` ids on
nuget.org belong to an unrelated implementation ([michielpost/x402-dotnet](https://github.com/michielpost/x402-dotnet)).
Never set a `PackageId` back to a bare `X402.*` — see
[ADR 0004](docs/adr/0004-prefix-the-package-ids-with-atypical.md).

`X402.TestKit` is a fourth, unpublished project: `FakeFacilitator` performs *real* EIP-712
signature recovery and validity-window/amount/recipient checks with no on-chain ledger, which
makes tests against it cryptographically meaningful — see "What this can't prove" below for the
line that draws.

## Documentation alignment (required)

**Whenever you change public behavior, check that the documentation still matches — and update
whatever drifted in the same change.** Treat stale docs as a bug in the change. Surfaces that
mirror the code:

- `README.md` — positioning, Quick Start snippets, the API table, "Limits of what's proven here"
- `nuget-readme.md` — the short version embedded in every `.nupkg` (`Directory.Build.targets`
  packs it as `README.md` inside the package; the root `README.md` is the repo-facing one)
- `samples/README.md` — the two runnable samples and their *real, unedited* console output; if a
  code change alters that output, re-run the sample and paste the new output, don't hand-edit it
- `docs/adr/` — add a new ADR (see `docs/adr/template.md`) for any decision that deviates from the
  obvious reading of a requirement; don't silently contradict an existing one
- `CHANGELOG.md` — maintained by `release-please`; don't hand-edit it

## Build & test

```bash
dotnet restore
dotnet build --configuration Release   # TreatWarningsAsErrors is on — a warning fails the build
dotnet test                            # full suite across five test projects
dotnet pack --configuration Release --output ./nupkg
```

```bash
dotnet test --filter "FullyQualifiedName~X402CodecTests"   # a single test class
```

## Conventions that are enforced, not just documented

- **x402 v2 only.** No `X-PAYMENT` header, no non-CAIP-2 network identifiers, no
  `x402Version: 1`.
- **Protocol numeric fields are strings on the wire** — `amount`, `value`, `validAfter`,
  `validBefore`, `nonce`. Emitting them as JSON numbers breaks interop (see the x402
  specification vectors under `tests/X402.Core.Tests/vectors/`).
- **No signing key on the server, structurally** — `X402.AspNetCore` references no signing
  library; `X402Options` has no key/secret/mnemonic property to fill in. A test enforces this
  (see [ADR 0003](docs/adr/0003-the-server-never-holds-a-signing-key.md)). Adding a signing
  dependency to `X402.AspNetCore` supersedes that record; it must not happen quietly.
- **No cross-asset conversion, anywhere.** Spending limits and prices are held per asset
  (network + contract address), never aggregated — a shared cap across EURC and USDC would imply
  an exchange rate this library does not have.
- **`X402.Core` stays dependency-free** of ASP.NET Core, any signing library, network calls and
  telemetry — checked by an architecture test in `tests/X402.Core.Tests`.
- **Package versions live in `Directory.Packages.props` only** — never in an individual `.csproj`.
- **XML docs are mandatory on public API members** — `GenerateDocumentationFile` is on repo-wide;
  an undocumented public member fails the build with CS1591. English `<summary>` text.
- **Test naming**: `MethodOrScenario_expected_behaviour`, English, one logical assertion per test,
  [Shouldly](https://github.com/shouldly/shouldly) (`value.ShouldBe(...)`), not `Assert.Equal`.
- **Conventional Commits** on every commit and PR title — `release-please` derives the shared
  version from them (`release-please-config.json`, injected into `Directory.Build.props` via the
  `x-release-please-version` markers — do not remove those markers or hand-edit the version they
  guard). The PR-title-lint workflow rejects a non-conforming title before merge.

## What this can't prove — read before writing a "it works" claim

- **This library supporting EURC is not evidence a given facilitator settles it.** The x402 wire
  protocol never advertises settled assets (`GET /supported` returns scheme×network pairs only).
  `samples/PayingAgent --probe` answers that empirically, per facilitator, per asset.
- **MiCA compliance is the asset issuer's, never this library's.** EURC's issuer is Circle France
  SAS (ACPR EMI register 17788). This library issues, holds and custodies nothing. Don't write
  anything that lets a reader infer otherwise.
- **No test here reaches an on-chain failure mode.** `FakeFacilitator` has no ledger; its
  settlement hash is derived from the nonce. Insufficient balance, a reverted
  `transferWithAuthorization`, mempool races, and a facilitator reporting success on a transfer
  that later fails are all outside what any test in this repository can catch.
- **There is no automatic adaptation to a facilitator's capabilities.** `GetSupportedAsync` is
  public surface on `IFacilitatorClient`; nothing in `X402.AspNetCore` or `X402.Client` consults
  it to adjust offered or accepted assets. That decision is left to the operator.
- **The conformance vectors are not an official x402 Foundation corpus** — none is published. They
  are extracted mechanically from JSON examples embedded in the vendored specification documents
  (`tests/X402.Core.Tests/vectors/SOURCE.md` records the pinned commit and the resync script).

## Release process

`release-please` (`.github/workflows/release-please.yml`) watches `main`, keeps a release PR
current from Conventional Commits, and on merge tags `vX.Y.Z` and publishes the GitHub Release —
which also packs and pushes the three packages to NuGet.org in that same workflow run (a
GITHUB_TOKEN-pushed tag does not itself retrigger `ci.yml`'s tag-triggered `publish`/`release`
jobs; those exist for a manually pushed tag instead — see the comment at the top of
`release-please.yml`).
