#!/usr/bin/env bash
set -euo pipefail

echo "[server] starting jellyfin (web UI at /web) ..."
exec dotnet /jellyfin/jellyfin.dll --datadir /config --ffmpeg /usr/bin/ffmpeg
