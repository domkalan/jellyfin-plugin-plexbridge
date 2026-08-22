#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEFAULT_VERSION="$(tr -d '[:space:]' < "$ROOT/VERSION")"
VERSION="${1:-$DEFAULT_VERSION}"
JELLYFIN_VERSION="${JELLYFIN_VERSION:-10.11.11}"
OUT="$ROOT/docker-dist"

rm -rf "$OUT"
mkdir -p "$OUT"

docker build \
  --file "$ROOT/Dockerfile.build" \
  --target export \
  --build-arg "PLUGIN_VERSION=$VERSION" \
  --build-arg "JELLYFIN_VERSION=$JELLYFIN_VERSION" \
  --output "type=local,dest=$OUT" \
  "$ROOT"

echo "Docker build output: $OUT"
