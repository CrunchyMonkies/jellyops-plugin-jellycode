#!/usr/bin/env bash
set -euo pipefail

echo "[server] starting jellyfin (headless) ..."
exec dotnet /jellyfin/jellyfin.dll --datadir /config --nowebclient --ffmpeg /usr/bin/ffmpeg
