# Prefix the package ids with `Atypical.`

## Context and Problem Statement

The three publishable projects declared `X402.Core`, `X402.Client` and `X402.AspNetCore` as their
`PackageId`. Two of those three ids are already owned by someone else on nuget.org:

| id | owner | versions | project |
|---|---|---|---|
| `x402.Core` | `mailpost` (Michiel Post) | 15, latest 2.2.0 | github.com/michielpost/x402-dotnet |
| `x402.Client` | `mailpost` (Michiel Post) | 17 | github.com/michielpost/x402-dotnet |
| `x402.AspNetCore` | — | none (unclaimed) | — |

None of the `x402.*` ids carries a reserved prefix (`verified = false` on all of them), so the
unclaimed `x402.AspNetCore` was open to anyone, including us.

Two things followed from this that were not intended:

1. **The README advertised another author's packages as ours.** The three shields.io badges
   resolved `X402.Core` and `X402.Client` against nuget.org and rendered *his* `v2.2.0`, linking to
   *his* package pages. This was on the default branch of a public repository.
2. **A release would have squatted the one free id.** `dotnet nuget push "./nupkg/*.nupkg"` expands
   alphabetically, so `X402.AspNetCore.nupkg` went first. It would have published successfully —
   under our account, into a namespace whose other two members belong to a third party — declaring
   a dependency on an `X402.Core` version that does not exist in his package. nuget.org allows
   delisting but never deletion, so this was not reversible.

The second point was live rather than theoretical: both publish jobs guarded on `NUGET_API_KEY`
and both commented that the secret was absent, but that had been checked with `gh secret list`,
which reports repository secrets only. An **organization** `NUGET_API_KEY` had existed since
2026-02-28, so the guard resolved and the push was armed. Release PR #2 was open and mergeable at
the time this was found. That defect is fixed separately, in the same change set.

## Decision Drivers

* An id owned by a third party cannot be acquired by any amount of code — the constraint is
  external and permanent.
* Publishing to nuget.org is irreversible. A wrong id shipped once is delisted, never removed.
* A public README must not present another author's work as this project's distribution.
* Namespaces, assembly names, project names and directory layout have no collision problem — only
  the *distribution identity* does. The smallest correct change is the one that touches only that.
* The rest of the portfolio already publishes under an `Atypical.` prefix, and a prefix opens a
  prefix-reservation request on nuget.org, which prevents the symmetric accident later.

## Considered Options

* Prefix all three ids with `Atypical.`
* Rename only the two colliding ids and keep the bare `X402.AspNetCore`
* Keep the ids and ask the other author to transfer them
* Pick an unrelated new name for the family

## Decision Outcome

Chosen option: **prefix all three ids with `Atypical.`**, because it resolves the collision with a
change confined to three `<PackageId>` lines plus the documentation that quotes them, and it keeps
the public API — namespaces, type names, `using` directives — untouched for every reader.

Renaming only the two colliding ids was rejected: shipping one bare id next to two prefixed ones is
incoherent for anyone browsing the family, and the bare `X402.AspNetCore` is precisely the id an
accidental release would have squatted. Leaving it in place would have preserved the hazard the
rename exists to remove.

Requesting a transfer was rejected as a dependency on a third party's goodwill for a project that
is unrelated to ours and actively maintained on his side.

A new unrelated name was rejected because `x402` is the protocol this library implements; the name
should keep saying so.

### Consequences

* Good, because the collision is closed by construction rather than by convention, and the three
  ids move together.
* Good, because namespaces and assembly names are unchanged: no consumer code, sample or snippet
  in this repository changes beyond the `dotnet add package` lines.
* Good, because `Atypical.` is a real prefix we can ask nuget.org to reserve, which forecloses a
  future accident in the other direction.
* Bad, because the package id and the assembly name now differ (`Atypical.X402.Core` ships
  `X402.Core.dll`). This is common and supported, but it means a reader cannot infer one from the
  other. The `<PackageId>` lines carry a comment saying why.
* Bad, because a consumer who installed both this library and `michielpost/x402-dotnet` would get
  two assemblies named `X402.Core.dll`. No such consumer exists today — nothing has been published
  from this repository — and renaming assemblies is a larger change that should be its own
  decision if it ever becomes real.
* Neutral, because nothing had been published yet: there is no migration path to write and no
  consumer to notify.

Prefix reservation on nuget.org is a manual request made from the owning account; it is not part
of this change and remains to be done before or alongside the first release.
