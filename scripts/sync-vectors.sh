#!/usr/bin/env bash
# Resynchronise the vendored x402 specification documents used as test vectors.
#
# The tests never hit the network: they read the committed copies under
# tests/X402.Core.Tests/vectors/_spec/. Run this script to refresh them, then
# inspect `git diff` — a non-empty diff means the specification moved.
#
# No associative arrays (declare -A, bash 4+): this repo's own README cites this script as the
# mechanism backing its vectors' provenance claim, and macOS ships bash 3.2 by default — a script
# that cannot run there undermines the section it supports. Parallel indexed arrays instead, which
# bash 3.2 supports.
set -euo pipefail

REPO="x402-foundation/x402"
COMMIT="90688e52e58ae9185f2860988bd2c46d2801ceda"
DEST="tests/X402.Core.Tests/vectors/_spec"

LOCAL_NAMES=(
  "spec-v2.md"
  "http.md"
  "mcp.md"
  "exact-evm.md"
)
REMOTE_PATHS=(
  "specs/x402-specification-v2.md"
  "specs/transports-v2/http.md"
  "specs/transports-v2/mcp.md"
  "specs/schemes/exact/scheme_exact_evm.md"
)

mkdir -p "$DEST"
for i in "${!LOCAL_NAMES[@]}"; do
  local="${LOCAL_NAMES[$i]}"
  remote="${REMOTE_PATHS[$i]}"
  gh api "repos/$REPO/contents/$remote?ref=$COMMIT" --jq .content | base64 -d > "$DEST/$local"
  echo "synced $DEST/$local  <-  $remote @ $COMMIT"
done
