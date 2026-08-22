#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEFAULT_VERSION="$(tr -d '[:space:]' < "$ROOT/VERSION")"
VERSION="${1:-$DEFAULT_VERSION}"
JELLYFIN_VERSION="${JELLYFIN_VERSION:-10.11.11}"
PROJECT="$ROOT/Jellyfin.Plugin.PlexBridge/Jellyfin.Plugin.PlexBridge.csproj"
OUT="$ROOT/dist"
STAGE="$OUT/stage"
ZIP="$OUT/PlexBridge-${VERSION}-jellyfin-${JELLYFIN_VERSION}.zip"

rm -rf "$OUT"
mkdir -p "$STAGE"

dotnet restore "$PROJECT" -p:JellyfinVersion="$JELLYFIN_VERSION"
dotnet build "$PROJECT" -c Release --no-restore \
  -p:JellyfinVersion="$JELLYFIN_VERSION" \
  -p:Version="${VERSION}.0"

cp "$ROOT/Jellyfin.Plugin.PlexBridge/bin/Release/net9.0/Jellyfin.Plugin.PlexBridge.dll" "$STAGE/"
if [[ -f "$ROOT/Jellyfin.Plugin.PlexBridge/bin/Release/net9.0/Jellyfin.Plugin.PlexBridge.pdb" ]]; then
  cp "$ROOT/Jellyfin.Plugin.PlexBridge/bin/Release/net9.0/Jellyfin.Plugin.PlexBridge.pdb" "$STAGE/"
fi
cp "$ROOT/LICENSE" "$STAGE/LICENSE"

# Jellyfin's repository installer creates the plugin directory itself, then extracts
# the ZIP into that directory. Package files therefore belong at the ZIP root.
(
  cd "$STAGE"
  zip -qr "$ZIP" .
)

CHECKSUM="$(md5sum "$ZIP" | awk '{print $1}')"
rm -rf "$STAGE"

echo "Created: $ZIP"
echo "Jellyfin repository checksum (MD5): $CHECKSUM"
