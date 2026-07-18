using System.Collections.Generic;
using Jellyfin.Plugin.DistributedTranscoding.Api;

namespace Jellyfin.Plugin.DistributedTranscoding.Server;

/// <summary>
/// Trickplay extraction outcome counters.
/// </summary>
public readonly struct TrickplayStats
{
    public long Attempts { get; init; }

    public long RemoteOk { get; init; }

    public long RemoteFailed { get; init; }

    public long LocalFallback { get; init; }
}

/// <summary>
/// Provides a point-in-time snapshot of active transcode jobs for the metrics API.
/// Implemented by <see cref="Transcoding.RemoteTranscodeManager"/>.
/// </summary>
public interface IActiveJobSource
{
    IReadOnlyList<ActiveJobDto> SnapshotActiveJobs();

    TrickplayStats GetTrickplayStats();
}
