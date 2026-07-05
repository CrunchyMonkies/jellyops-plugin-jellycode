#!/usr/bin/env bash
#
# Build and push the JellyOps + all-in-one images to Harbor:
#   - jellyfin-distributed-server  all-in-one (plugin baked in) — non-operator docker path
#   - jellyfin-distributed-worker  ffmpeg worker pod
#   - jellyfin-server:12.0.0       plugin-free v12 server — Jellyfin CR spec.image for JellyOps
#   - jellyfin-distributed-plugin  plugin-only image-volume payload for JellyOps
#
#   docker/build-push.sh [TAG]
#
# TAG defaults to a date-based tag (YYYYMM.DD.1). Images are also tagged :latest, and the
# JellyOps images additionally carry version tags (server :12.0.0, plugin :0.0.1.0).
# On completion the pushed @sha256 digests are printed for pinning the JellyfinPlugin CR.
# Requires: `docker login harbor.bne1.ouchi.com.au` already done.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
JELLYFIN_SRC="$(cd "$REPO_ROOT/../jellyfin-src" && pwd)"
JELLYFIN_WEB="$(cd "$REPO_ROOT/../jellyfin-web" && pwd)"   # sibling checkout; provides the web UI (dist/)
REGISTRY="harbor.bne1.ouchi.com.au/applications"
SERVER_IMG="$REGISTRY/jellyfin-distributed-server"    # all-in-one (plugin baked in) — non-operator docker path
WORKER_IMG="$REGISTRY/jellyfin-distributed-worker"    # horizontally-scaled ffmpeg worker
SERVERBASE_IMG="$REGISTRY/jellyfin-server"            # plugin-free v12 server — Jellyfin CR spec.image for JellyOps
PLUGIN_IMG="$REGISTRY/jellyfin-distributed-plugin"    # plugin-only image-volume payload for JellyOps
TAG="${1:-$(date +%Y%m).$(date +%d).1}"

# The plugin-free server MUST carry a v12 version tag: JellyOps derives the server
# version from the image tag to gate the plugin's targetAbi (12.0.0.0).
SERVER_VERSION="12.0.0"
# The plugin image is versioned by the plugin version (mirrors meta.json).
PLUGIN_VERSION="0.0.1.0"

echo "[build-push] tag=$TAG"

echo "[build-push] publishing artifacts..."
rm -rf /tmp/jf-publish /tmp/jf-worker
dotnet publish "$JELLYFIN_SRC/Jellyfin.Server/Jellyfin.Server.csproj" -c Release -o /tmp/jf-publish >/dev/null
dotnet publish "$REPO_ROOT/src/Worker/Jellyfin.Plugin.DistributedTranscoding.Worker.csproj" -c Release -o /tmp/jf-worker >/dev/null
dotnet build "$REPO_ROOT/src/Plugin/Jellyfin.Plugin.DistributedTranscoding.csproj" -c Release >/dev/null

# Build the Jellyfin web UI (matched to the server commit) if not already built.
if [ ! -f "$JELLYFIN_WEB/dist/index.html" ]; then
  echo "[build-push] building jellyfin-web (npm ci && build:production)..."
  ( cd "$JELLYFIN_WEB" && npm ci && npm run build:production ) >/dev/null
fi

# --- server context ---
SCTX=/tmp/jf-server-ctx
rm -rf "$SCTX"; mkdir -p "$SCTX/server" "$SCTX/plugin" "$SCTX/web"
cp -r /tmp/jf-publish/. "$SCTX/server/"
cp -r "$JELLYFIN_WEB/dist/." "$SCTX/web/"
cp "$REPO_ROOT"/src/Plugin/bin/Release/net10.0/*.dll "$SCTX/plugin/"
cp "$REPO_ROOT"/src/Plugin/bin/Release/net10.0/meta.json "$SCTX/plugin/"
cp "$REPO_ROOT"/docker/Dockerfile.server "$SCTX/Dockerfile"
cp "$REPO_ROOT"/docker/server-entrypoint.sh "$SCTX/server-entrypoint.sh"

# --- worker context ---
WCTX=/tmp/jf-worker-ctx
rm -rf "$WCTX"; mkdir -p "$WCTX/worker"
cp -r /tmp/jf-worker/. "$WCTX/worker/"
cp "$REPO_ROOT"/docker/Dockerfile.worker "$WCTX/Dockerfile"
cp "$REPO_ROOT"/docker/worker-entrypoint.sh "$WCTX/worker-entrypoint.sh"

# --- server-base context (plugin-free v12 for JellyOps) ---
SBCTX=/tmp/jf-serverbase-ctx
rm -rf "$SBCTX"; mkdir -p "$SBCTX/server" "$SBCTX/web"
cp -r /tmp/jf-publish/. "$SBCTX/server/"
cp -r "$JELLYFIN_WEB/dist/." "$SBCTX/web/"
cp "$REPO_ROOT"/docker/Dockerfile.server-base "$SBCTX/Dockerfile"
cp "$REPO_ROOT"/docker/server-entrypoint.sh "$SBCTX/server-entrypoint.sh"

# --- plugin context (image-volume payload for JellyOps) ---
# Root of the plugin image = contents of the plugin dir (meta.json + our DLLs +
# Grpc.*/Google.Protobuf); host DLLs are already stripped by the build.
PCTX=/tmp/jf-plugin-ctx
rm -rf "$PCTX"; mkdir -p "$PCTX/plugin"
cp "$REPO_ROOT"/src/Plugin/bin/Release/net10.0/*.dll "$PCTX/plugin/"
cp "$REPO_ROOT"/src/Plugin/bin/Release/net10.0/meta.json "$PCTX/plugin/"
cp "$REPO_ROOT"/docker/Dockerfile.plugin "$PCTX/Dockerfile"

echo "[build-push] building server image (all-in-one)..."
docker build -t "$SERVER_IMG:$TAG" -t "$SERVER_IMG:latest" "$SCTX"
echo "[build-push] building worker image..."
docker build -t "$WORKER_IMG:$TAG" -t "$WORKER_IMG:latest" "$WCTX"
echo "[build-push] building server-base image (plugin-free v12, web UI bundled)..."
# The -web tag is a distinct pull target so a running node re-pulls when only the web
# bundle changed (IfNotPresent won't re-pull an overwritten :12.0.0). Both parse to ABI 12.0.0.
docker build -t "$SERVERBASE_IMG:$SERVER_VERSION" -t "$SERVERBASE_IMG:$SERVER_VERSION-web" -t "$SERVERBASE_IMG:$TAG" -t "$SERVERBASE_IMG:latest" "$SBCTX"
echo "[build-push] building plugin image (image-volume payload)..."
# --provenance=false keeps the payload a single, plain manifest (no attestation index)
# so the kubelet mounts the image-volume filesystem cleanly.
docker build --provenance=false -t "$PLUGIN_IMG:$PLUGIN_VERSION" -t "$PLUGIN_IMG:$TAG" -t "$PLUGIN_IMG:latest" "$PCTX"

echo "[build-push] pushing..."
docker push "$SERVER_IMG:$TAG"
docker push "$SERVER_IMG:latest"
docker push "$WORKER_IMG:$TAG"
docker push "$WORKER_IMG:latest"
docker push "$SERVERBASE_IMG:$SERVER_VERSION"
docker push "$SERVERBASE_IMG:$SERVER_VERSION-web"
docker push "$SERVERBASE_IMG:$TAG"
docker push "$SERVERBASE_IMG:latest"
docker push "$PLUGIN_IMG:$PLUGIN_VERSION"
docker push "$PLUGIN_IMG:$TAG"
docker push "$PLUGIN_IMG:latest"

# Resolve pushed digests (populated in RepoDigests only after a push).
PLUGIN_DIGEST="$(docker inspect --format='{{index .RepoDigests 0}}' "$PLUGIN_IMG:$PLUGIN_VERSION" 2>/dev/null || true)"
WORKER_DIGEST="$(docker inspect --format='{{index .RepoDigests 0}}' "$WORKER_IMG:$TAG" 2>/dev/null || true)"

echo "[build-push] done:"
echo "  $SERVER_IMG:$TAG (+ latest)"
echo "  $WORKER_IMG:$TAG (+ latest)"
echo "  $SERVERBASE_IMG:$SERVER_VERSION (+ $TAG, latest)"
echo "  $PLUGIN_IMG:$PLUGIN_VERSION (+ $TAG, latest)"
echo
echo "[build-push] pin these in the JellyfinPlugin CR (k8s/30-jellyfinplugin.yaml + jellyops samples):"
echo "  pluginImage.reference: ${PLUGIN_DIGEST:-$PLUGIN_IMG:$PLUGIN_VERSION}"
echo "  workloads[worker].image.reference: ${WORKER_DIGEST:-$WORKER_IMG:$TAG}"
echo "  Jellyfin CR spec.image: $SERVERBASE_IMG:$SERVER_VERSION"
