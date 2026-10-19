#!/usr/bin/env bash
# Assemble the GitHub Pages site: the hand-made landing page (site/) at the root, and the DocFX
# documentation (docs/site/) under /docs/. The docs pages load the landing page's style.css and
# site.js from one level up, so the two only render correctly once assembled like this.
#
# Used by .github/workflows/pages.yml, and locally:
#   scripts/build-site.sh && python3 -m http.server -d artifacts/site 8000
set -euo pipefail

cd "$(dirname "$0")/.."
OUT="${1:-artifacts/site}"

rm -rf "$OUT"
mkdir -p "$OUT"
cp -R site/. "$OUT/"

dotnet tool restore
# --warningsAsErrors: a broken cross-reference fails the build instead of shipping a dead link.
dotnet docfx docs/site/docfx.json --output "$OUT/docs" --warningsAsErrors
