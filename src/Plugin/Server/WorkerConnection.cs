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

    public WorkerConnection(string workerId, int maxConcurrent, IReadOnlyList<string>? hwAccels = null)
    {
        WorkerId = workerId;
        MaxConcurrent = maxConcurrent;
        FreeSlots = maxConcurrent;
        HwAccels = hwAccels ?? Array.Empty<string>();
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
