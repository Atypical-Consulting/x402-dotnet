# Provenance of the test vectors

These documents are verbatim copies of the x402 specification, pinned to a single commit.

- Repository: https://github.com/x402-foundation/x402
- Commit: `90688e52e58ae9185f2860988bd2c46d2801ceda` (2026-07-24)
- Refresh with: `./scripts/sync-vectors.sh`

| File | Source path |
| --- | --- |
| `spec-v2.md` | `specs/x402-specification-v2.md` |
| `http.md` | `specs/transports-v2/http.md` |
| `mcp.md` | `specs/transports-v2/mcp.md` |
| `exact-evm.md` | `specs/schemes/exact/scheme_exact_evm.md` |

The x402 Foundation does not publish a cross-language vector corpus — the only files named
"vectors" in the repository belong to the CBOR builder-code extension. The JSON examples embedded
in these specification documents are therefore the most authoritative fixed data available, and
`SpecVectorSource` extracts them mechanically rather than by hand, so no transcription error is
possible.
