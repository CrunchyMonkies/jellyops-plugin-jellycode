using System;
using Jellyfin.Plugin.DistributedTranscoding.Contracts;

namespace Jellyfin.Plugin.DistributedTranscoding.Server;

/// <summary>
/// Receives worker-&gt;server job frames. Implemented by the RemoteTranscodeManager; the gRPC service
/// resolves it from the shared singleton so frames reach the manager that owns the job handles.
/// </summary>
public interface IRemoteFrameSink
{
    void OnJobAccepted(string jobId, bool accepted, string? reason);

    void OnSegmentData(string jobId, string relPath, ReadOnlyMemory<byte> chunk, bool eof);

    void OnProgress(string jobId, Progress progress);

    void OnLog(string jobId, string line);

    void OnJobExited(string jobId, int exitCode);

    void OnWorkerLost(WorkerConnection worker);
}
