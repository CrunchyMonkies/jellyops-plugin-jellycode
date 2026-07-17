using System.Collections.Generic;
using Jellyfin.Plugin.DistributedTranscoding.Api;

namespace Jellyfin.Plugin.DistributedTranscoding.Server;

/// <summary>
/// Provides a point-in-time snapshot of active transcode jobs for the metrics API.
/// Implemented by <see cref="Transcoding.RemoteTranscodeManager"/>.
/// </summary>
public interface IActiveJobSource
{
    IReadOnlyList<ActiveJobDto> SnapshotActiveJobs();
}
