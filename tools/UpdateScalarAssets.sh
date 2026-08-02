#!/usr/bin/env bash
# Updates the bundled offline Scalar API-reference UI assets (src/Elsie/OpenApiUi/).
#
# Scalar (https://github.com/scalar/scalar) is MIT-licensed. The standalone browser bundle
# is self-contained (styles inlined, no runtime CDN fetches) and is embedded into the Elsie
# host assembly so `UseScalarCdn = false` serves a fully offline API reference.
#
# Usage:   ./tools/UpdateScalarAssets.sh [version]
# Default: latest stable published version.
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${1:-1.64.0}"
URL="https://cdn.jsdelivr.net/npm/@scalar/api-reference@${VERSION}/dist/browser/standalone.js"
TARGET="src/Elsie/OpenApiUi/standalone.js"

echo "Fetching @scalar/api-reference@${VERSION} standalone bundle → ${TARGET}"
curl -fsSL -o "$TARGET" "$URL"

SIZE=$(wc -c < "$TARGET")
echo "Downloaded ${SIZE} bytes."
grep -q "api-reference" "$TARGET" || { echo "ERROR: downloaded file does not look like the Scalar standalone bundle"; exit 1; }

echo "Done. Commit the updated asset together with the pinned version in tools/UpdateScalarAssets.sh."
