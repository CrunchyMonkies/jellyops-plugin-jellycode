# Distributed Transcoding for Jellyfin — Phase 0 (POC)

Offloads ffmpeg transcoding from the Jellyfin server to a pool of remote **worker** processes over a
**gRPC bidirectional stream**. Transcoded HLS segments are streamed back over that channel and written
to the **exact canonical paths** the HLS controller already polls, so the HTTP serving layer needs no
changes. Delivered as a **Jellyfin plugin with no core fork**.

See [`docs/distributed-transcoding.md`](docs/distributed-transcoding.md) for the full design/RFC.

> **Scope:** This is Phase 0 — single hard-coded worker, HLS only, cleartext h2c (no TLS/auth), no
> scheduler, no throttler. It exists to prove the core insight end-to-end. Phases 1–3 (worker pool /
> scheduler, lifecycle parity, mTLS, hwaccel matching, k8s) are intentionally out of scope.

## Architecture

```
Jellyfin server (plugin)                         Worker process
  DynamicHlsController ──poll File.Exists()         ffmpeg → local scratch dir
        │                                                │  OutputTailer streams completed
        ▼                                                ▼  segment/playlist files back
  RemoteTranscodeManager (ITranscodeManager) ◀── gRPC bidi stream ──▶ WorkerAgent
        │  builds nothing new — reuses EncodingHelper args             (dials the server)
        ▼  writes received bytes to canonical transcode path
  GrpcHostedService (separate Kestrel :9090, h2c)
```

- **The only DI swap is `ITranscodeManager`.** `RemoteTranscodeManager` reuses all of core's local prep
  (directory + log file + in-memory job bookkeeping + the `WaitForPath` contract) and replaces
  `process.Start()` with an `AssignJob` sent to a worker. Received `SegmentData` bytes are written to
  the canonical path (temp file + atomic rename on `eof`) so the controller never sees a partial file.
- **Kill/stop routes to a gRPC `JobControl{STOP}`** instead of core `TranscodingJob.Stop()` (which
  assumes a local `Process` and would NRE for remote jobs).
- The plugin self-hosts a **second Kestrel + gRPC listener** on its own port — Jellyfin's own
  Kestrel/routing is untouched.

## Projects

| Project | Output | Purpose |
|---|---|---|
| `src/Contracts` | library | `transcode.proto` (gRPC client + server stubs), shared by both sides |
| `src/Plugin` | the loaded plugin DLL | `ITranscodeManager` override + gRPC server |
| `src/Worker` | console exe | dials the server, runs ffmpeg, streams segments back |
| `test/SmokeHost` | console exe | transport-level end-to-end test (no Jellyfin needed) |

Built against the local Jellyfin checkout at `../jellyfin-src` via `ProjectReference` with
`Private=false` (compile against the exact running assemblies, ship none of them). A post-build target
strips host-owned assemblies; only our DLLs + `Grpc.*`/`Google.Protobuf` are shipped.

## Build

```bash
dotnet build -c Release        # builds Contracts + Worker + Plugin
```

## Verify (no Jellyfin) — transport smoke test

Exercises worker dial/register, job accept, ffmpeg launch + path mapping, output tailing, segment
framing, and the server-side reconstruction — using a fake ffmpeg:

```bash
dotnet build test/SmokeHost/Jellyfin.Plugin.DistributedTranscoding.SmokeHost.csproj -c Release
dotnet test/SmokeHost/bin/Release/net10.0/Jellyfin.Plugin.DistributedTranscoding.SmokeHost.dll
# expect: [smoke] PASS  (exit 0)
```

## Run in Docker (verified end-to-end)

Because the plugin targets the local `../jellyfin-src` (v12.0.0) — not the public 10.x images — the
container builds Jellyfin from that source and runs it headless with the plugin + an in-container worker.

```bash
docker/run.sh build     # publish server + worker + plugin, assemble context, docker build
docker/run.sh upd       # run detached (maps :8096 API, :9090 gRPC mesh)
docker/run.sh logs      # follow logs
docker/run.sh down      # stop & remove
```

On boot you should see in the logs:

```
PluginManager: Loaded plugin: Distributed Transcoding 0.0.1.0
GrpcHostedService: Distributed transcoding gRPC listener started on 0.0.0.0:9090 (h2c)
WorkerRegistry: Worker registered: docker-w1 (maxConcurrent=2)
```

This has been verified end-to-end: after completing the startup wizard and requesting an HLS segment
via the API, the logs show `RemoteTranscodeManager: Assigning remote transcode … to worker docker-w1`,
the worker runs ffmpeg against a path-mapped local scratch dir, segments stream back over gRPC, and the
unmodified `DynamicHlsController` serves a valid MPEG-TS segment from the canonical transcode path. The
worker scratch dir ends up empty — proving the segments traveled over the channel, not a shared volume.

> **Auth note (API testing):** use the standard `Authorization: MediaBrowser Token="…"` header (and
> `Authorization: MediaBrowser Client="…", Device="…", DeviceId="…", Version="…"` for
> `AuthenticateByName`). The `X-Emby-Authorization` header and `api_key=` query param did not parse on
> this v12 build.

## Worker images (GitHub Container Registry)

The worker is published to GHCR by the [`worker-images`](.github/workflows/worker-images.yml) workflow
on every push to `main` (and on `v*` tags) as a **single package with per-variant tags**:

**`ghcr.io/crunchymonkies/jellyops-plugin-jellycode/worker`**

| Tag | ffmpeg | Runtime requirement |
|---|---|---|
| `:cpu` (`:latest`) | distro ffmpeg (software x264/x265) | none |
| `:intel` | jellyfin-ffmpeg (VAAPI/QSV, bundled Intel iHD driver) | an Intel GPU render node — `/dev/dri` (or the Intel device-plugin resource `gpu.intel.com/xe` / `i915`), and Jellyfin set to `HardwareAccelerationType: vaapi` |
| `:nvidia` | jellyfin-ffmpeg (NVENC) | the NVIDIA container runtime + a GPU (`nvidia.com/gpu`, `runtimeClassName: nvidia`), and Jellyfin set to `nvenc` |

Tags also include `:<variant>-<sha>` (every build) and `:<variant>-<tag>` (on releases). Pull:

```bash
docker pull ghcr.io/crunchymonkies/jellyops-plugin-jellycode/worker:cpu
docker pull ghcr.io/crunchymonkies/jellyops-plugin-jellycode/worker:intel
docker pull ghcr.io/crunchymonkies/jellyops-plugin-jellycode/worker:nvidia
```

> The HW variants only add ffmpeg + drivers; the worker binary is identical. The device path in the
> ffmpeg args comes from the Jellyfin server's config and is passed through unchanged, so the worker
> must expose the same `/dev/dri` render node the server is configured for. On first publish the GHCR
> package is **private** — flip it to public in the repo's *Packages* settings if you want open pulls.

## Container images (Harbor)

Published to `harbor.bne1.ouchi.com.au/applications/`:

| Image | Purpose |
|---|---|
| `jellyfin-distributed-server:<tag>` | Jellyfin v12 (built from `../jellyfin-src`) + the plugin baked in. No embedded worker; hosts the gRPC mesh on :9090. Headless (`--nowebclient`). Non-operator docker path. |
| `jellyfin-distributed-worker:<tag>` | Standalone ffmpeg worker that dials the server and streams segments back. The horizontally-scaled artifact. |
| `jellyfin-server:12.0.0` | **Plugin-free** Jellyfin v12 — the `Jellyfin` CR `spec.image` for the JellyOps path. The `12.0.0` tag satisfies the plugin's `targetAbi`. |
| `jellyfin-distributed-plugin:0.0.1.0` | **Plugin-only image-volume payload** (`meta.json` + DLLs at root) that JellyOps mounts and injects. |

Tags: a date-based `YYYYMM.DD.N` plus `latest`. Build & push both (requires
`docker login harbor.bne1.ouchi.com.au`):

```bash
docker/build-push.sh             # date-based tag + latest
docker/build-push.sh 202606.21.1 # explicit tag
```

**Worker env** (override per Deployment): `DT_SERVER` (default `http://jellyfin:9090`), `DT_WORKER_ID`
(default hostname), `DT_FFMPEG` (`/usr/bin/ffmpeg`), `DT_MAX_CONCURRENT` (`2`), `DT_SCRATCH` (`/tmp/worker`).

Quick two-container check (server + worker on one network):

```bash
docker network create dt-net
docker run -d --name dt-server  --network dt-net -p 8096:8096 \
  harbor.bne1.ouchi.com.au/applications/jellyfin-distributed-server:latest
docker run -d --name dt-worker  --network dt-net \
  -e DT_SERVER=http://dt-server:9090 -e DT_WORKER_ID=pod-a \
  harbor.bne1.ouchi.com.au/applications/jellyfin-distributed-worker:latest
docker logs dt-server | grep "Worker registered"   # -> Worker registered: pod-a
```

> The combined all-in-one dev image (`docker/run.sh`, server + plugin + worker in one container) remains
> available for local testing; the two images above are the deployable split matching the pod topology.

## Deploy under JellyOps (Kubernetes)

The [JellyOps](../jellyops) operator delivers this plugin declaratively: the plugin ships as an OCI
**image volume** (no bytes baked into the server) and the ffmpeg **worker** pool is managed as a
companion workload. The manifests live in [`k8s/`](k8s/) — see [`k8s/README.md`](k8s/README.md).

```bash
docker/build-push.sh                       # builds jellyfin-server:12.0.0 + jellyfin-distributed-plugin
                                           # (+ worker), pushes to Harbor, prints the @sha256 digests
# paste the printed digests into k8s/30-jellyfinplugin.yaml, then:
kubectl apply -f k8s/                       # namespace, media PVC, Jellyfin CR, JellyfinPlugin CR
```

JellyOps ABI-gates on the server image tag, so the `Jellyfin` CR uses the plugin-free `jellyfin-server:12.0.0`
image; it injects `jellyfin-distributed-plugin` via `imageVolumeCopy` and runs the workers, which mount the
same media claim at the same path and dial the in-pod gRPC mesh on `:9090`. Requires a K8s ≥ 1.33 cluster
with image volumes enabled.

## Hardware transcoding (Intel / NVIDIA)

ffmpeg runs on the **worker**, so the GPU must be on the **worker** pods — not the Jellyfin server. Pick the
matching worker image variant, give the worker pods the GPU, and enable HW accel in Jellyfin. Because Phase 0
has no hwaccel-aware scheduling, run a **homogeneous** worker pool (all workers the same variant).

**Key caveat (distributed-specific):** the Jellyfin *server* builds the ffmpeg command. For **VAAPI** it only
adds the `-init_hw_device` after probing a *local* render device — so if the server has no GPU (e.g. it's pinned
to a non-GPU node), it emits a `*_vaapi` encoder with no device set up. The `:intel` image handles this: `DT_FFMPEG`
points at a small wrapper (`docker/vaapi-ffmpeg-wrap.sh`) that injects `-init_hw_device vaapi=va:<render node>
-filter_hw_device va` when it sees a `*_vaapi` job with no device — so the worker HW-encodes regardless. (If your
server *does* have the GPU, plain VAAPI works without the wrapper.)

### Intel (VAAPI) — Kubernetes

```yaml
# JellyfinPlugin worker workload
image:
  reference: ghcr.io/crunchymonkies/jellyops-plugin-jellycode/worker:intel   # jellyfin-ffmpeg8 + wrapper
resources:
  limits:
    gpu.intel.com/xe: "1"          # (or gpu.intel.com/i915) — schedules onto a GPU node + injects /dev/dri
tolerations:                        # only if the GPU node is tainted
  - {key: dedicated, operator: Equal, value: intel-gpu, effect: NoExecute}
env:
  - {name: DT_FFMPEG, value: /usr/local/bin/ffmpeg-vaapi-wrap}
```

Then set Jellyfin → Playback → **VAAPI**, device `/dev/dri/renderD128`. HW **encode** (`h264_vaapi`/`hevc_vaapi`)
runs on the GPU; decode stays on CPU unless the server also has the device. Note: **QSV/oneVPL fails on current
Xe/Battlemage cards** (`-17` child-device error) — use VAAPI.

### NVIDIA (NVENC) — Kubernetes

```yaml
image:
  reference: ghcr.io/crunchymonkies/jellyops-plugin-jellycode/worker:nvidia  # jellyfin-ffmpeg8 (NVENC)
resources:
  limits:
    nvidia.com/gpu: "1"
```

Requires the NVIDIA container runtime (`runtimeClassName: nvidia` or the node default) so the driver libs are
injected. Then set Jellyfin → Playback → **NVENC**. NVENC needs no `-init_hw_device`, so no wrapper is used.

## Run against a real Jellyfin

1. **Deploy the plugin:**
   ```bash
   scripts/deploy-plugin.sh [PLUGINS_DIR]   # default ~/.local/share/jellyfin/plugins
   ```
   Stages to `<PLUGINS_DIR>/Distributed Transcoding_0.0.1.0/` (plugin + Contracts + Grpc + meta.json).

2. **Start a worker** (same host as the server in Phase 0, sharing media + transcode paths so the
   identity `PathMap` works). Point `--ffmpeg` at the same binary the server reports as its encoder
   path (e.g. `jellyfin-ffmpeg`):
   ```bash
   dotnet src/Worker/bin/Release/net10.0/Jellyfin.Plugin.DistributedTranscoding.Worker.dll \
     --server http://127.0.0.1:9090 --worker-id w1 \
     --ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg --max-concurrent 2
   ```

3. **Restart Jellyfin** and confirm in the logs: plugin loaded, the gRPC listener started on `:9090`,
   and the worker `Register` was received.

4. **Play a file that requires transcoding** (`GET /Videos/{id}/master.m3u8` then segment requests).
   The server sends `AssignJob`, the worker runs ffmpeg and streams `SegmentData`, the server writes
   `<transcode>/<hash>0.ts` and `<hash>.m3u8` at the canonical polled paths, and playback proceeds with
   the HLS controller unmodified.

5. **Stop playback** (`DELETE /Videos/ActiveEncodings`) → the manager sends `JobControl{STOP}`; the
   worker's ffmpeg exits — no `TranscodingJob.Stop()` NRE.

## Configuration

`PluginConfiguration` (stored by Jellyfin): `GrpcPort` (default `9090`), `ListenAddress`
(default `0.0.0.0`), `FirstSegmentTimeoutSeconds` (default `60`, fails playback if no worker/output).
