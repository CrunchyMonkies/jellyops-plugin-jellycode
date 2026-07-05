# Deploy Distributed Transcoding under JellyOps

This bundle runs the jellycode plugin on Kubernetes via the [JellyOps](../../jellyops)
operator. JellyOps delivers the plugin as an OCI **image volume** and manages the ffmpeg
**worker** pool — no plugin bytes are baked into the Jellyfin server image.

## Images (Harbor)

Built and pushed by [`../docker/build-push.sh`](../docker/build-push.sh):

| Image | Role |
|---|---|
| `jellyfin-server:12.0.0` | Plugin-free Jellyfin v12 — the `Jellyfin` CR `spec.image`. The `12.0.0` tag satisfies the plugin's `targetAbi`. |
| `jellyfin-distributed-plugin:0.0.1.0` | Plugin-only image-volume payload (`meta.json` + DLLs at root). |
| `jellyfin-distributed-worker:<tag>` | ffmpeg worker pod; dials the in-pod gRPC mesh on `:9090`. |

## Prerequisites

1. JellyOps CRDs + operator installed (`make install deploy` in `../../jellyops`).
2. A Kubernetes **≥ 1.33** cluster with the image-volume feature enabled (CRI support required).
3. An **RWX** storage class for the shared media claim (edit `10-media-pvc.yaml`).
4. Images built and pushed: `../docker/build-push.sh` — then copy the printed `@sha256`
   digests into `30-jellyfinplugin.yaml` (`pluginImage` and the worker `image`).

## Apply

```bash
kubectl apply -f 00-namespace.yaml
kubectl apply -f 10-media-pvc.yaml     # point at / pre-load your media first
kubectl apply -f 20-jellyfin.yaml
kubectl apply -f 30-jellyfinplugin.yaml
kubectl apply -f 40-networkpolicy.yaml # optional
```

## Verify

```bash
kubectl -n media get jellyfin,jellyfinplugin
# JellyfinPlugin: ABICompatible=True, Injected, WorkersAvailable
kubectl -n media logs deploy/home-media -c jellyfin | grep -E "Loaded plugin|:9090"
kubectl -n media get deploy   # worker replicas Ready; each Registers over the mesh
```

Play a file that requires transcoding: the server assigns the job over gRPC, a worker runs
ffmpeg against the shared `/media` mount, and streams HLS segments back to the canonical
transcode path — the unmodified HLS controller serves them.

Notes: the operator **auto-mounts** the Jellyfin instance's media (from `spec.storage.media`)
into the worker pods — read-only, at the same paths as the server (Phase-0 identity path
mapping) — so the worker reads source files locally; only output segments travel over gRPC.
The media claim must be RWX/NFS (or ROX) so it can be mounted by the worker pods too.
