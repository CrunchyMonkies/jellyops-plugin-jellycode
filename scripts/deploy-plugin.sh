#!/usr/bin/env bash
#
# Build the plugin in Release and stage it into a Jellyfin plugins directory.
#
# Usage:
#   scripts/deploy-plugin.sh [PLUGINS_DIR]
#
# PLUGINS_DIR defaults to ~/.local/share/jellyfin/plugins
# The plugin is copied to "<PLUGINS_DIR>/Distributed Transcoding_0.0.1.0/".
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PLUGINS_DIR="${1:-$HOME/.local/share/jellyfin/plugins}"
TARGET="$PLUGINS_DIR/Distributed Transcoding_0.0.1.0"
SRC="$REPO_ROOT/src/Plugin/bin/Release/net10.0"

echo "[deploy] building plugin (Release)..."
dotnet build "$REPO_ROOT/src/Plugin/Jellyfin.Plugin.DistributedTranscoding.csproj" -c Release >/dev/null

echo "[deploy] staging to: $TARGET"
rm -rf "$TARGET"
mkdir -p "$TARGET"

# Ship only our assemblies + third-party deps the host does not provide, plus meta.json.
# (The StripHostAssemblies build target already removed Jellyfin/Emby/MediaBrowser DLLs from $SRC.)
cp "$SRC"/*.dll "$TARGET"/
cp "$SRC"/meta.json "$TARGET"/

echo "[deploy] staged files:"
ls -1 "$TARGET"
echo "[deploy] done. Restart Jellyfin to load the plugin."
