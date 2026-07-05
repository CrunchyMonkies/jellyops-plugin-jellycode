#!/usr/bin/env bash
#
# Build & run the Jellyfin-v12 + Distributed-Transcoding-plugin container.
#
#   docker/run.sh build     # publish server+worker+plugin, assemble context, docker build
#   docker/run.sh up        # run the container (foreground)
#   docker/run.sh upd       # run detached
#   docker/run.sh logs      # follow logs
#   docker/run.sh down      # stop & remove
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
JELLYFIN_SRC="$(cd "$REPO_ROOT/../jellyfin-src" && pwd)"
CTX="/tmp/jf-docker"
IMAGE="jellycode/jellyfin-distributed:dev"
NAME="jellyfin-distributed"

build() {
  echo "[build] publishing Jellyfin server (Release)..."
  rm -rf /tmp/jf-publish
  dotnet publish "$JELLYFIN_SRC/Jellyfin.Server/Jellyfin.Server.csproj" -c Release -o /tmp/jf-publish

  echo "[build] publishing worker..."
  rm -rf /tmp/jf-worker
  dotnet publish "$REPO_ROOT/src/Worker/Jellyfin.Plugin.DistributedTranscoding.Worker.csproj" -c Release -o /tmp/jf-worker

  echo "[build] building plugin..."
  dotnet build "$REPO_ROOT/src/Plugin/Jellyfin.Plugin.DistributedTranscoding.csproj" -c Release >/dev/null

  echo "[build] assembling docker context at $CTX ..."
  rm -rf "$CTX"
  mkdir -p "$CTX/server" "$CTX/worker" "$CTX/plugin"
  cp -r /tmp/jf-publish/. "$CTX/server/"
  cp -r /tmp/jf-worker/. "$CTX/worker/"
  cp "$REPO_ROOT"/src/Plugin/bin/Release/net10.0/*.dll "$CTX/plugin/"
  cp "$REPO_ROOT"/src/Plugin/bin/Release/net10.0/meta.json "$CTX/plugin/"
  cp "$REPO_ROOT/docker/Dockerfile" "$CTX/Dockerfile"
  cp "$REPO_ROOT/docker/entrypoint.sh" "$CTX/entrypoint.sh"

  echo "[build] docker build -> $IMAGE"
  docker build -t "$IMAGE" "$CTX"
  echo "[build] done."
}

up()  { docker rm -f "$NAME" 2>/dev/null || true; docker run --name "$NAME" -p 8096:8096 -p 9090:9090 "$IMAGE"; }
upd() { docker rm -f "$NAME" 2>/dev/null || true; docker run -d --name "$NAME" -p 8096:8096 -p 9090:9090 "$IMAGE"; echo "started $NAME"; }
logs() { docker logs -f "$NAME"; }
down() { docker rm -f "$NAME" 2>/dev/null || true; echo "removed $NAME"; }

cmd="${1:-build}"
case "$cmd" in
  build) build ;;
  up) up ;;
  upd) upd ;;
  logs) logs ;;
  down) down ;;
  *) echo "usage: $0 {build|up|upd|logs|down}"; exit 1 ;;
esac
