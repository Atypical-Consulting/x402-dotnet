# Contributing to x402-dotnet

Thanks for your interest in x402-dotnet! This document explains how to build the project, the
conventions it follows, and how to get a change merged. By contributing you agree that your
contributions are licensed under the project's [Apache-2.0 License](LICENSE).

## Ground rules

- Be respectful — see the [Code of Conduct](CODE_OF_CONDUCT.md).
- Open an issue before starting non-trivial work so we can align on the approach.
- x402 v2 only — no v1 compatibility (no `X-PAYMENT` header, no non-CAIP-2 network identifiers, no
  `x402Version: 1`).
- Keep the package layering intact (see below) — it's what keeps `X402.Core` reusable outside ASP.NET Core.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/) — `global.json` pins `10.0.301`, `rollForward: latestFeature`.

## Build & test

```bash
dotnet restore
dotnet build --configuration Release   # TreatWarningsAsErrors is on — a warning fails the build
dotnet test                            # the full suite, across five test projects
dotnet pack --configuration Release --output ./nupkg
```

Run a single test class:

```bash
dotnet test --filter "FullyQualifiedName~X402CodecTests"
```

Try the samples — see [`samples/README.md`](samples/README.md) for the full walkthrough, including
running the whole demo offline with no facilitator and no wallet:

```bash
dotnet run --project samples/PaidApi &
dotnet run --project samples/PayingAgent
```

## Architecture: keep the layering intact

```
X402.Core        protocol types, transport codec, extension points
                 no ASP.NET Core reference, no signing library, no network call, no telemetry
  ↑
X402.AspNetCore  server middleware, imperative gate, facilitator client, idempotency ledger
                 no signing library reference, no key/secret/mnemonic property anywhere
X402.Client      EIP-3009 signing, the paying DelegatingHandler, per-asset spending limits
```

Both boundaries are enforced by tests, not just by convention:

- `X402.Core` staying free of ASP.NET Core, crypto and network/telemetry dependencies is checked
  by an architecture test in `tests/X402.Core.Tests`.
- `X402.AspNetCore` never referencing a signing library, and `X402Options` never exposing a key
  property, is checked by a test guarding [ADR 0003](docs/adr/0003-the-server-never-holds-a-signing-key.md).
  Adding a signing dependency to `X402.AspNetCore` supersedes that record; it must not happen quietly.

## Conventions

- **Conventional Commits** for every commit and PR title — `feat:`, `fix:`, `docs:`, `test:`,
  `chore:`, `refactor:`, `ci:`. `release-please` derives the next version for all three packages
  from these; the PR-title-lint workflow rejects a non-conforming title before merge.
- **Package versions** are centralized in `Directory.Packages.props` — never set a version in an
  individual `.csproj`.
- **XML documentation is required on every public API member.** `GenerateDocumentationFile` is on
  repo-wide, so an undocumented public member fails the build with CS1591. Write `<summary>` text
  in English — the language of the published packages.
- **Test naming**: `MethodOrScenario_expected_behaviour`, in English, one logical assertion per
  test. Uses [Shouldly](https://github.com/shouldly/shouldly) (`value.ShouldBe(...)`), not
  `Assert.Equal`.
- **Architecture Decision Records** for anything that deviates from the obvious reading of a
  requirement — see [`docs/adr/`](docs/adr/), MADR format, `docs/adr/template.md` for the shape.

## Submitting a change

1. Fork → branch (`git checkout -b feat/my-feature`)
2. Commit (`git commit -m 'feat: ...'`) — Conventional Commits, see above
3. Push + Pull Request — the title is linted for the same convention, since it becomes the
   squash-merge commit message on `main`

## Security

Please don't open a public issue for a security vulnerability — see [`SECURITY.md`](SECURITY.md).
