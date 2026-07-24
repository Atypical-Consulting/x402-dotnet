# The server never holds a signing key

## Context and Problem Statement

x402 v2 payments are authorized by the payer, who signs an EIP-3009 `transferWithAuthorization`
message with their own wallet key and hands the signed payload to the server. The server's job is
to demand payment, forward that signature to a facilitator for verification and settlement, and
serve the resource once settlement succeeds. Nothing in that flow requires the server to hold a
private key, mnemonic, or any other signing secret: funds move directly from the payer's address to
`PayTo`. `X402.AspNetCore.Configuration.X402Options` is the surface every consumer configures the
server through, and it is also the easiest place for a non-custodial guarantee to quietly erode — a
future "convenience" property for a signing key, or a dependency pulled in for an unrelated reason
that happens to carry a signer, would turn this package into something that can move money on
someone else's behalf.

## Decision Drivers

* A server that never holds a signing key cannot leak one, cannot be compelled to sign a payment it
  was not asked to sign, and is not a target worth attacking for key theft
* x402's trust model already places settlement authority with the facilitator, not the resource
  server — the server has no legitimate need to produce a signature
* `X402Options` is user-facing configuration; anything that compiles against it becomes part of the
  package's contract, so a private-key property here would be very hard to walk back once shipped
* Auditing "does this package ever touch a secret" should be a dependency-graph question, answerable
  without reading every code path, not a matter of reviewer discipline

## Considered Options

* Allow `X402Options` to optionally carry a signing key, for operators who want the server itself to
  pay on a payer's behalf in automated, agent-to-agent scenarios
* Keep signing entirely out of `X402.AspNetCore`, and leave it to `X402.Client`, a separate package a
  payer depends on — never a resource server
* Keep signing out of the server package, but still reference a signing library (for example
  Nethereum) for address-related utilities such as the EIP-55 checksum

## Decision Outcome

Chosen option: "Keep signing entirely out of `X402.AspNetCore`, and leave it to `X402.Client`, a
separate package a payer depends on — never a resource server", because the resource server's role
in x402 is to demand and verify payment, not to make one. Keeping the two roles in separate packages
makes the non-custodial guarantee structural rather than a matter of discipline: `X402Options` has no
property a signing key could occupy — enforced by
`OptionsValidationTests.Options_expose_no_private_key_property` — and `X402.AspNetCore` carries no
dependency capable of producing a signature. The one piece of address-related cryptography the server
package does need — the EIP-55 checksum on `PayTo`, which catches a mistyped payee address before it
sends funds somewhere unrecoverable — is served by a from-scratch Keccak-256 implementation in
`X402.Core` (`X402.Cryptography.Keccak256`, tested against the published test vectors). Keccak-256 is
a pure hash function with no key and no secret: it does not touch the non-custodial constraint, so it
does not need to live behind the same wall as signing.

### Consequences

* Good, because the non-custodial guarantee is enforced by the absence of a dependency and the
  absence of a property, not by a comment asking future contributors to be careful
* Good, because a security review of `X402.AspNetCore` can stop at "does it reference a signing
  library" and answer no, instead of auditing every code path for private-key handling
* Good, because `X402.Cryptography.Keccak256` lives in the dependency-free `X402.Core`, so
  `X402.Client` and any future consumer that needs the same hash function can reuse a single,
  vector-tested implementation instead of writing another one
* Bad, because an operator who wants a single server to both demand payment for its own resources
  and pay for its own upstream x402-metered dependencies must compose two packages
  (`X402.AspNetCore` and `X402.Client`) rather than configuring both through one `X402Options`
* Bad, because `X402.Client` already depends on `Nethereum.Signer.EIP712` for EIP-712 signing, which
  carries its own internal Keccak-256. `X402.Core`'s implementation does not replace that dependency,
  so two independent Keccak-256 implementations exist in this repository's dependency graph until, or
  unless, `X402.Client`'s non-signing hashing needs are migrated onto `X402.Core`'s version. Accepted
  because Keccak-256 is a public, fixed, vector-tested algorithm — not a place independent
  implementations are likely to silently drift apart
