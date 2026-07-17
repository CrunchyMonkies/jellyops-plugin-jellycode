#!/usr/bin/env bash
set -euo pipefail

WID="${DT_WORKER_ID:-$(hostname)}"
mkdir -p "$DT_SCRATCH"

echo "[worker] id=$WID server=$DT_SERVER ffmpeg=$DT_FFMPEG maxConcurrent=$DT_MAX_CONCURRENT metricsPort=${DT_METRICS_PORT:-9091}"
exec dotnet /worker/Jellyfin.Plugin.DistributedTranscoding.Worker.dll \
  --server "$DT_SERVER" \
  --worker-id "$WID" \
  --ffmpeg "$DT_FFMPEG" \
  --max-concurrent "$DT_MAX_CONCURRENT" \
  --scratch "$DT_SCRATCH" \
  --metrics-port "${DT_METRICS_PORT:-9091}"
