# Design: Distributed Transcoding Pods for Jellyfin (gRPC streaming, plugin-based)

## Context

Today Jellyfin transcodes **in-process on the main server**. `TranscodeManager.StartFfMpeg()`
(`MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs:371`) spawns a local
`System.Diagnostics.Process` running ffmpeg, which writes HLS segments / progressive output to the
server's local transcode temp path. The HTTP layer then serves those files by **polling the local
filesystem** for them.

This couples encoding capacity to the box running Jellyfin and makes it impossible to scale
transcoding horizontally (e.g. one ffmpeg-heavy 4K HEVC→H.264 transcode can saturate a host while
the rest of the server sits idle).

**Goal:** Offload ffmpeg execution to a pool of separate **transcoding worker pods**. Workers
**subscribe to the server to advertise availability** (multiple pods register and are load-balanced),
and a **single pod can run multiple concurrent streams**. Communication is a **gRPC bidirectional
stream**; transcoded **segments are streamed back over that channel**. Delivered as a **Jellyfin
plugin with no core fork**.

**Status:** design / RFC. No implementation in this document — it defines the architecture, the
contracts, the constraints, and a phased roadmap so a POC can be built against it.

> Research note: produced with Explore agents over the Jellyfin `master` checkout (serena symbol
> tooling for precise signatures). The file/line references below were verified against that tree.

---

## Why this combination is clean (the key insight)

The HTTP/HLS serving layer **never needs to change**, because it does not know or care *how* segment
files appear on disk — it only polls for them:

- `DynamicHlsController.GetDynamicSegment` / `GetSegmentResult` wait in a loop on
  `File.Exists(segmentPath)` (and `File.Exists(nextSegmentPath)`), `Task.Delay(100)` between checks
  (`Jellyfin.Api/Controllers/DynamicHlsController.cs`, ~lines 1900–1983).
- `TranscodeManager.StartFfMpeg` itself returns only once `File.Exists(state.WaitForPath ?? outputPath)`
  becomes true (`TranscodeManager.cs:508–515`).

So if our plugin's transcode manager **receives segment bytes over gRPC and writes complete files to
the exact canonical local paths** ffmpeg would have used, every controller, the master/variant
playlist generator, segment cleaner, and `ActiveRequestCount` output-sharing logic keep working
verbatim. The distributed boundary is hidden entirely behind one interface.

**The only swap needed is `ITranscodeManager`.** We deliberately keep `IMediaEncoder`
(ffprobe / media analysis) **local on the server** — see "Probing stays local" below.

---

## Jellyfin seams we build on (verified)

| Concern | Symbol / file | Role in design |
|---|---|---|
| Service registration | `Emby.Server.Implementations/ApplicationHost.cs:597` registers `ITranscodeManager → TranscodeManager` | We override this binding from the plugin. |
| Plugin DI override | `MediaBrowser.Controller/Plugins/IPluginServiceRegistrator.cs` (called by `PluginManager.RegisterServices`, `Plugins/PluginManager.cs:206`) | Plugin re-registers `ITranscodeManager` with our remote implementation. |
| Transcode interface | `MediaBrowser.Controller/MediaEncoding/ITranscodeManager.cs` | The single seam. `StartFfMpeg`, `KillTranscodingJobs`, `PingTranscodingJob`, `ReportTranscodingProgress`, `GetTranscodingJob`, `OnTranscodeBegin/EndRequest`, `LockAsync`. |
| Job model | `MediaBrowser.Controller/MediaEncoding/TranscodingJob.cs` (sealed; holds `Process?`, `Stop()` writes `"q"` to ffmpeg stdin) | Reused as the in-memory handle, but `Process` stays null for remote jobs (see risks). |
| Command-line builder | `EncodingHelper` + `DynamicHlsController.GetCommandLineArguments()` | Stays **server-side**; produces the ffmpeg arg string we ship to the worker. |
| Output/segment paths | `IServerConfigurationManager.GetTranscodePath()` (`MediaBrowser.Common/Configuration/EncodingConfigurationExtensions.cs:29`) | Canonical paths the plugin writes received bytes to. |
| Output sharing | `ActiveRequestCount` + per-path job lookup (`TranscodeManager.GetTranscodingJob(path,type)`) | Unchanged — multiple clients still share one remote job. |
| Config | `MediaBrowser.Model/Configuration/EncodingOptions.cs` ("encoding" store) | Worker pool settings live in our own plugin config store. |

There is **no existing** clustering / queue / remote-worker infrastructure — jobs are an in-memory
`List<TranscodingJob>`. We introduce the distributed layer entirely inside the plugin.

---

## Target architecture

```
            ┌──────────────────────────── Jellyfin Server (pod) ─────────────────────────────┐
            │                                                                                  │
  client ──▶│  DynamicHlsController ──poll File.Exists()──▶  transcode temp path (local disk)  │
  (HLS)     │        │                                              ▲                          │
            │        │ StartFfMpeg(state,args)                      │ write received segments  │
            │        ▼                                              │                          │
            │  RemoteTranscodeManager (plugin, ITranscodeManager)   │                          │
            │        │  - EncodingHelper builds ffmpeg args (local) │                          │
            │        │  - picks worker via WorkerRegistry/Scheduler │                          │
            │        ▼                                              │                          │
            │  gRPC server (plugin-hosted Kestrel, separate port) ──┘                          │
            └─────────────▲───────────────────────────▲──────────────────────────────────────┘
                          │ bidi stream (per worker)   │
              ┌───────────┴──────────┐     ┌───────────┴──────────┐
              │ Worker Pod A         │     │ Worker Pod B         │   ... (N pods, autoscaled)
              │  WorkerAgent (gRPC)  │     │  WorkerAgent         │
              │  ├ job 1: ffmpeg ────┼─┐   │  ├ job 3: ffmpeg     │   <- multiple streams / pod
              │  ├ job 2: ffmpeg ────┼─┤   │  └ job 4: ffmpeg     │
              │  └ tails seg files, streams bytes back over channel │
              └──────────────────────┘     └──────────────────────┘
                          shared RO media volume mounted on all (source input)
```

**Direction of dialing:** *workers dial the server*. The server is a stable k8s Service; workers are
ephemeral, autoscaled pods. Each worker opens one long-lived **bidirectional gRPC stream** to the
server's plugin-hosted gRPC endpoint and keeps it open. This is the "subscribe for availability"
channel: registration + heartbeat flow worker→server, job assignments flow server→worker, segment
bytes + progress flow worker→server — all multiplexed on the one stream, framed by `jobId`.

**Plugin hosts its own gRPC listener.** Adding raw gRPC endpoints into Jellyfin's `Startup`/endpoint
routing would require a core fork. Instead the plugin runs an `IHostedService` that starts a
**separate Kestrel + Grpc.AspNetCore server on its own port** (e.g. `:9090`). This keeps "no core
fork" intact.

---

## Control plane — gRPC contract (sketch)

A single bidi stream per worker carries a tagged envelope. Sketch (`transcode.proto`):

```proto
service TranscodeMesh {
  // One long-lived stream per worker. Worker → server frames and server → worker frames
  // are multiplexed; every frame carries a job_id (empty for worker-level frames).
  rpc Connect(stream WorkerFrame) returns (stream ServerFrame);
}

message WorkerFrame {
  oneof msg {
    Register     register   = 1;  // worker-level: capabilities + capacity
    Heartbeat    heartbeat  = 2;  // worker-level: load, free slots, health  (availability)
    JobAccepted  accepted   = 3;
    SegmentData  segment    = 4;  // job-level: bytes of a finished/partial output file
    Progress     progress   = 5;  // job-level: position, fps, %, bytes, bitrate
    JobExited    exited      = 6;  // job-level: exit code
    LogLine      log        = 7;  // job-level: ffmpeg stderr (for the server-side log file)
  }
}

message ServerFrame {
  oneof msg {
    AssignJob    assign     = 1;  // ship encoder path + arg string + path map + job_id
    JobControl   control    = 2;  // PAUSE | RESUME | STOP | PING  (maps to throttle/kill)
    Drain        drain      = 3;  // stop accepting new jobs (graceful pod shutdown)
  }
}

message Register {
  string worker_id = 1;
  int32  max_concurrent = 2;        // multi-stream-per-pod capacity
  repeated string hwaccels = 3;     // vaapi, qsv, cuda, videotoolbox...
  repeated string encoders = 4;     // h264_nvenc, hevc_qsv, libx264...
  string ffmpeg_version = 5;
}

message Heartbeat { int32 active_jobs = 1; int32 free_slots = 2; double cpu = 3; }

message AssignJob {
  string job_id = 1;
  string encoder_path = 2;          // ffmpeg binary path on the worker
  string arguments = 3;             // EncodingHelper output, with paths rewritten (see below)
  TranscodingJobType type = 4;      // Hls | Progressive | Dash
  PathMap path_map = 5;             // source input + output prefix translation
  repeated string output_globs = 6; // which files to tail & stream back (e.g. prefix*.ts, *.m3u8)
}

message SegmentData {
  string job_id = 1;
  string rel_path = 2;              // path relative to output dir → server reconstructs canonical path
  bytes  chunk = 3;
  bool   eof = 4;                   // file complete (server only exposes complete files)
}
```

**Availability subscription = the `Register` + `Heartbeat` stream.** The server keeps a
`WorkerRegistry` (replacing reliance on the in-memory job list for scheduling): set of connected
workers, their capabilities, free slots, and last-heartbeat. A `Scheduler` picks a worker for each
new job (least-loaded with matching hwaccel/encoder; reject/queue if none free). Worker death =
stream closes → registry evicts it → its in-flight jobs are failed and (optionally) rescheduled.

---

## Data plane — segments over the channel

1. `RemoteTranscodeManager.StartFfMpeg(state, outputPath, args, …)`:
   - Runs **all the existing local prep** that the controller already expects: `Directory.CreateDirectory`,
     creates the server-side ffmpeg log file, registers the `TranscodingJob` via the same
     `OnTranscodeBeginning` bookkeeping, starts kill timer / progress reporting.
   - Instead of `process.Start()`, it asks the `Scheduler` for a worker and sends `AssignJob` with the
     arg string and a **path map**.
2. **Worker** rewrites paths to pod-local temp, launches ffmpeg, and `tail`s the output dir. As each
   segment / playlist file is completed it streams `SegmentData{rel_path, chunk, eof}` back.
3. **Server** writes the bytes to `Path.Combine(GetTranscodePath()-derived dir, rel_path)` — i.e. the
   **canonical local path the controller is already polling**. It writes to a temp name and atomically
   renames on `eof` so the controller never sees a partial file.
4. `StartFfMpeg` returns once the first expected file (`state.WaitForPath ?? outputPath`) exists —
   identical contract to today.

This is why **back-pressure is naturally handled**: gRPC streams have built-in HTTP/2 flow control, and
Jellyfin's existing `TranscodingThrottler`/segment-pacing means the worker is only ever a little ahead
of the client. (Throttle "pause" maps to a `JobControl{PAUSE}` → worker sends `q`/SIGSTOP to its
ffmpeg, replacing the local stdin-`"q"` mechanism in `TranscodingJob.Stop()`.)

---

## Multiple streams to the same pod

The `WorkerAgent` maintains a `Dictionary<jobId, FfmpegProcess>` and runs up to `max_concurrent`
ffmpeg processes simultaneously. All their segment/progress/log frames are tagged with `job_id` and
multiplexed onto the single bidi stream. `free_slots` in each `Heartbeat` lets the server keep packing
streams onto a pod until capacity, then spill to the next pod. This satisfies both "multiple pods
subscribe" (N registered workers) and "multiple streams to the same pod" (N jobs per worker).

---

## Probing stays local (scope control)

`IMediaEncoder.GetMediaInfo` (ffprobe) and command-line construction (`EncodingHelper`) **remain on
the server**. The server already mounts the media library; probing is cheap relative to encoding.
Keeping them local means:
- only `ITranscodeManager` is swapped (smaller, safer surface),
- the heavy/expensive work (the actual encode) is what gets distributed,
- arg generation, hwaccel selection, and subtitle/attachment logic stay in one place.

The server therefore needs a usable `ffmpeg`/`ffprobe` for probing even though it no longer encodes.
(Alternative, out of scope: also offload probing — larger surface, deferred.)

---

## Required infrastructure assumptions

- **Source media access on workers.** ffmpeg on the pod must *read the source file*. The arg string's
  input path must resolve on the worker. Realistic approach: mount the **media library read-only on all
  worker pods at the same path** as the server (or provide a `PathMap` the worker applies). Streaming the
  *input* over gRPC is explicitly out of scope (the chosen design streams *output* back only).
- **ffmpeg + hwaccel drivers in the worker image.** Worker pods carry the ffmpeg build and any
  GPU/VAAPI/QSV runtime; `Register.hwaccels/encoders` advertises what each pod can do so the scheduler
  matches jobs to capable pods.
- **Subtitle burn-in / attachment extraction** (`StartFfMpeg` lines 398–415) needs source + font access
  on the worker too; covered by the shared media mount, but DVD/BluRay `.concat` extraction paths must
  be mapped or also performed worker-side.
- **Auth between server and workers:** mTLS or a shared bearer token on the gRPC channel; workers are
  trusted compute. Treated as a hard requirement (workers receive media paths and stream content).

---

## Constraints & risks

1. **`TranscodingJob` is sealed and assumes a local `Process`.** `Stop()` does
   `process!.StandardInput.WriteLine("q")` and `Dispose()` disposes `Process`. For remote jobs `Process`
   is null → those paths must be avoided. Two options: (a) the plugin keeps its own remote
   job-handle map and only uses `TranscodingJob` as a lookup record, routing stop/pause to
   `JobControl` frames; (b) propose a small upstream change making lifecycle pluggable. Given "no core
   fork," **(a)** is the design baseline; (b) is noted as a future upstreaming opportunity.
2. **Throttler & segment cleaner** (`StartThrottler`/`StartSegmentCleaner`) currently act on local files
   and a local process. Segment cleaner still works (it deletes local files the server now owns).
   Throttler's pause must be redirected to `JobControl{PAUSE/RESUME}` over the stream.
3. **First-segment latency** now includes a network round-trip + worker scheduling. The existing
   `WaitForPath` loop tolerates this, but cold-start (no free worker / pod scale-up) needs a timeout +
   user-facing failure path.
4. **Ordering & atomicity:** only expose complete files (temp-write + rename on `eof`) so the
   controller's `File.Exists(nextSegmentPath)` "is it done" heuristic stays valid.
5. **gRPC endpoint hosting from a plugin** (separate Kestrel port) — validate the plugin can start an
   `IHostedService`/background service at the right point in Jellyfin's lifecycle.
6. **Scheduling/back-pressure when the pool is saturated:** define behaviour (queue vs. fall back to
   local encode vs. reject). Recommend optional **local fallback** so a zero-worker deployment still plays.

---

## Phased roadmap

- **Phase 0 — Proof of concept:** plugin swaps `ITranscodeManager`; one hard-coded worker; single HLS
  job; segments streamed back to canonical path; play one file end-to-end. Validates the core insight.
- **Phase 1 — Worker pool & availability:** `WorkerRegistry` + `Heartbeat`, `Scheduler`, multiple pods,
  multiple streams per pod, graceful drain on pod shutdown.
- **Phase 2 — Lifecycle parity:** pause/throttle/stop/ping/progress mapped to control frames; log
  forwarding; segment cleaner; kill-timer + `ActiveRequestCount` sharing verified across clients.
- **Phase 3 — Hardening:** mTLS/auth, hwaccel capability matching, path mapping for DVD/BluRay/subtitle
  cases, local-encode fallback, k8s manifests (server Service, worker Deployment+HPA, RO media PVC).

---

## POC acceptance criteria (how to validate the design)

1. **Transparency to the HTTP layer:** with the plugin loaded and `ITranscodeManager` overridden,
   `GET /Videos/{id}/master.m3u8` then sequential segment requests succeed unchanged — confirmed by
   the controller never being modified and segments appearing at the polled paths.
2. **Availability subscription:** start the server with 0 workers (playback fails/queues), scale workers
   to N, observe `Register`/`Heartbeat` populate the registry and playback succeed; kill a worker mid-
   stream and confirm eviction + (re)assignment.
3. **Multi-stream per pod:** pin all workers but one; start ≥2 concurrent plays; confirm both run as
   separate ffmpeg jobs on the same pod (distinct `job_id`s, `free_slots` decrementing).
4. **Output sharing intact:** two clients, identical encoding params → one remote job, `ActiveRequestCount==2`.
5. **Lifecycle:** seek (forces restart), pause (throttle → `PAUSE` frame), and stop
   (`DELETE /Videos/ActiveEncodings` → `JobControl{STOP}`) all terminate the worker ffmpeg promptly.
6. **Build sanity (when code exists):** `dotnet build Jellyfin.sln`; plugin loads; gRPC port reachable.

References for implementers: `ITranscodeManager.cs`, `TranscodeManager.cs:371` (StartFfMpeg),
`DynamicHlsController.cs` (GetDynamicSegment/GetSegmentResult), `EncodingConfigurationExtensions.cs:29`
(GetTranscodePath), `IPluginServiceRegistrator.cs`, `ApplicationHost.cs:597` (binding to override).
