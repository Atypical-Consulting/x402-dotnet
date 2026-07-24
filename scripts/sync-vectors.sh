#!/usr/bin/env bash
# Resynchronise the vendored x402 specification documents used as test vectors.
#
# The tests never hit the network: they read the committed copies under
# tests/X402.Core.Tests/vectors/_spec/. Run this script to refresh them, then
# inspect `git diff` — a non-empty diff means the specification moved.
set -euo pipefail

REPO="x402-foundation/x402"
COMMIT="90688e52e58ae9185f2860988bd2c46d2801ceda"
DEST="tests/X402.Core.Tests/vectors/_spec"

declare -A FILES=(
  ["spec-v2.md"]="specs/x402-specification-v2.md"
  ["http.md"]="specs/transports-v2/http.md"
  ["mcp.md"]="specs/transports-v2/mcp.md"
  ["exact-evm.md"]="specs/schemes/exact/scheme_exact_evm.md"
)

mkdir -p "$DEST"
for local in "${!FILES[@]}"; do
  remote="${FILES[$local]}"
  gh api "repos/$REPO/contents/$remote?ref=$COMMIT" --jq .content | base64 -d > "$DEST/$local"
  echo "synced $DEST/$local  <-  $remote @ $COMMIT"
done
