using System;
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

    public WorkerConnection(string workerId, int maxConcurrent)
    {
        WorkerId = workerId;
        MaxConcurrent = maxConcurrent;
        FreeSlots = maxConcurrent;
    }

    public string WorkerId { get; }

    public int MaxConcurrent { get; }

    public int FreeSlots { get; set; }

    public int ActiveJobs { get; set; }

    public ChannelReader<ServerFrame> Outbound => _outbound.Reader;

    public bool TrySend(ServerFrame frame) => _outbound.Writer.TryWrite(frame);

    public void CompleteOutbound() => _outbound.Writer.TryComplete();
}
