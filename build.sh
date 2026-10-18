#!/usr/bin/env bash
# Builds the plugin in a .NET 9 container and writes dist/epg-channel-names_<ver>.zip + dist/manifest.json.
# Usage: ./build.sh [version] [repo-base-url]
set -euo pipefail
VER="${1:-1.0.0.0}"
BASE_URL="${2:-https://github.com/Yofuness/jellyfin-epg-sync/releases/download/v$VER}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJ="$ROOT/Jellyfin.Plugin.EpgChannelNames"

docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -v "$PROJ":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet build -c Release -p:Version="$VER" -p:AssemblyVersion="$VER" -p:FileVersion="$VER"

mkdir -p "$ROOT/dist"
ZIP="epg-channel-names_$VER.zip"
rm -f "$ROOT/dist/$ZIP"
(cd "$PROJ/bin/Release/net9.0" && zip -q "$ROOT/dist/$ZIP" Jellyfin.Plugin.EpgChannelNames.dll)
SUM=$(md5sum "$ROOT/dist/$ZIP" | cut -d' ' -f1)

cat > "$ROOT/dist/manifest.json" <<JSON
[
  {
    "guid": "4f1c7a3e-6b2d-4e8a-9c51-0d7e2a9b3f64",
    "name": "EPG Channel Names",
    "description": "Renames Live TV channels to the program currently airing in the EPG.",
    "overview": "Fixes stale channel names like 'NHL GAME 01 Team A vs Team B'.",
    "owner": "johann",
    "category": "LiveTV",
    "versions": [
      {
        "version": "$VER",
        "changelog": "Release $VER",
        "targetAbi": "10.11.0.0",
        "sourceUrl": "$BASE_URL/$ZIP",
        "checksum": "$SUM",
        "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
      }
    ]
  }
]
JSON
echo "Built dist/$ZIP and dist/manifest.json"
