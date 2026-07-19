using System;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DistributedTranscoding.Server;

/// <summary>
/// Per-job server-side state for a one-shot remote batch job (e.g. trickplay frame extraction) that
/// produces a fixed set of output files and then exits. Unlike <see cref="RemoteJobHandle"/> there is no
/// <c>StreamState</c>/<c>TranscodingJob</c> or playback session — the caller simply awaits
/// <see cref="ExitedTcs"/> and then reads the reassembled files from <see cref="OutputDir"/>.
/// </summary>
public sealed class BatchJobHandle : IDisposable
{
    private readonly SegmentFileWriter _writer;

    public BatchJobHandle(string jobId, WorkerConnection worker, string outputDir)
    {
        JobId = jobId;
        Worker = worker;
        OutputDir = outputDir;
        _writer = new SegmentFileWriter(outputDir);
    }

    public string JobId { get; }

    public WorkerConnection Worker { get; }

    public string OutputDir { get; }

    /// <summary>Completes when the worker accepts (true) or rejects/dies pre-accept (false).</summary>
    public TaskCompletionSource<bool> AcceptedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with the ffmpeg exit code once the worker signals the job has exited.</summary>
    public TaskCompletionSource<int> ExitedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public float Framerate { get; set; }

    public double PercentComplete { get; set; }

    public float Speed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this job was preempted by a streaming transcode request.
    /// When true, a non-zero exit code is treated as expected (the worker was told to stop) rather than
    /// as a failure, and the caller retries on the mesh without counting it as a hard failure.
    /// </summary>
    public bool Preempted { get; set; }

    /// <summary>
    /// Appends a streamed chunk to a temp file; on eof atomically promotes it to the canonical path
    /// inside <see cref="OutputDir"/>, so the caller only ever enumerates complete files.
    /// </summary>
    public void WriteSegment(string relPath, ReadOnlyMemory<byte> chunk, bool eof)
        => _writer.Write(relPath, chunk, eof);

    public void Dispose() => _writer.Dispose();
}
