using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using Jellyfin.Plugin.DistributedTranscoding.Contracts;

namespace Jellyfin.Plugin.DistributedTranscoding.Server;

/// <summary>
/// Server-side view of one connected worker and its single outbound (server-&gt;worker) frame queue.
/// All sends funnel through the channel; a single writer task in <see cref="TranscodeMeshService"/>
/// drains it to the response stream (gRPC server streams are not safe for concurrent writes).
/// </summary>
public sealed class WorkerConnection
{
    private readonly Channel<ServerFrame> _outbound =
        Channel.CreateUnbounded<ServerFrame>(new UnboundedChannelOptions { SingleReader = true });

    public WorkerConnection(
        string workerId,
        int maxConcurrent,
        IReadOnlyList<string>? hwAccels = null,
        IReadOnlyList<string>? encoders = null,
        string? ffmpegVersion = null,
        IReadOnlyList<string>? decoders = null)
    {
        WorkerId = workerId;
        MaxConcurrent = maxConcurrent;
        FreeSlots = maxConcurrent;
        HwAccels = hwAccels ?? Array.Empty<string>();
        Encoders = encoders ?? Array.Empty<string>();
        Decoders = decoders ?? Array.Empty<string>();
        FfmpegVersion = string.IsNullOrWhiteSpace(ffmpegVersion) ? "unknown" : ffmpegVersion;
        LastSeenUtc = DateTime.UtcNow;
    }

    public string WorkerId { get; }

    public int MaxConcurrent { get; }

    public int FreeSlots { get; set; }

    public int ActiveJobs { get; set; }

    /// <summary>
    /// Hardware accelerators advertised by this worker at registration time.
    /// </summary>
    public IReadOnlyList<string> HwAccels { get; }

    /// <summary>
    /// Concrete video encoders the worker's ffmpeg supports (probed at startup), e.g.
    /// h264_nvenc, hevc_qsv, libx264. Drives the per-type option filtering in the settings UI.
    /// </summary>
    public IReadOnlyList<string> Encoders { get; }

    /// <summary>
    /// Concrete video decoders the worker's ffmpeg supports (probed at startup), e.g.
    /// h264_cuvid, hevc_qsv, h264_vaapi, av1. Used to prefer a worker that can hardware-decode a
    /// job's source codec.
    /// </summary>
    public IReadOnlyList<string> Decoders { get; }

    /// <summary>
    /// The worker's ffmpeg version string (or "unknown").
    /// </summary>
    public string FfmpegVersion { get; }

    /// <summary>
    /// UTC time of the last frame received from this worker (register or heartbeat).
    /// </summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// Coarse worker type inferred from advertised accelerators: nvenc -&gt; "nvidia",
    /// vaapi/qsv -&gt; "intel", otherwise "cpu". This is the key the per-type config is stored under.
    /// </summary>
    public string WorkerType =>
        CanNvenc ? "nvidia" : (CanVaapi || CanQsv ? "intel" : "cpu");

    /// <summary>
    /// Convenience: true when the worker advertises Intel QuickSync (qsv) capability.
    /// </summary>
    public bool CanQsv => HwAccels.Any(h => string.Equals(h, "qsv", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Convenience: true when the worker advertises vaapi capability.
    /// </summary>
    public bool CanVaapi => HwAccels.Any(h => string.Equals(h, "vaapi", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Convenience: true when the worker advertises nvenc (NVIDIA) capability.
    /// </summary>
    public bool CanNvenc => HwAccels.Any(h => string.Equals(h, "nvenc", StringComparison.OrdinalIgnoreCase));

    public ChannelReader<ServerFrame> Outbound => _outbound.Reader;

    public bool TrySend(ServerFrame frame) => _outbound.Writer.TryWrite(frame);

    public void CompleteOutbound() => _outbound.Writer.TryComplete();
}
