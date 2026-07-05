#!/usr/bin/env bash
set -uo pipefail

# 1. Generate a test clip (H.264/AAC) that we can later force-transcode via the API.
mkdir -p /media /tmp/worker
if [ ! -f /media/testclip.mp4 ]; then
  echo "[entrypoint] generating /media/testclip.mp4 ..."
  ffmpeg -y -f lavfi -i "testsrc=duration=20:size=1280x720:rate=24" \
         -f lavfi -i "sine=frequency=440:duration=20" \
         -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest \
         /media/testclip.mp4 </dev/null >/media/ffmpeg-gen.log 2>&1 \
    && echo "[entrypoint] test clip ready" \
    || echo "[entrypoint] WARNING: test clip generation failed (see /media/ffmpeg-gen.log)"
fi

# 2. Start the transcoding worker. Its reconnect loop waits until the plugin's gRPC listener is up.
echo "[entrypoint] starting worker -> 127.0.0.1:9090"
dotnet /worker/Jellyfin.Plugin.DistributedTranscoding.Worker.dll \
  --server http://127.0.0.1:9090 --worker-id docker-w1 \
  --ffmpeg /usr/bin/ffmpeg --max-concurrent 2 --scratch /tmp/worker &

# 3. Start Jellyfin (headless). Uses the system ffmpeg for probing; encoding is offloaded to the worker.
echo "[entrypoint] starting jellyfin ..."
exec dotnet /jellyfin/jellyfin.dll --datadir /config --nowebclient --ffmpeg /usr/bin/ffmpeg
