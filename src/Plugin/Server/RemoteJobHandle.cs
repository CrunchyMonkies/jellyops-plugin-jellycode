using System;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;

namespace Jellyfin.Plugin.DistributedTranscoding.Server;

/// <summary>
/// Per-job server-side state for a remote transcode: the gRPC routing target plus the logic that
/// turns streamed bytes into complete files at the canonical polled paths (temp-write + atomic rename),
/// so the HLS controller never sees a partial file.
/// </summary>
public sealed class RemoteJobHandle : IDisposable
{
    private readonly object _logLock = new();
    private readonly SegmentFileWriter _writer;

    public RemoteJobHandle(string jobId, WorkerConnection worker, string outputDir, string playlistPath, TranscodingJob job, StreamState state, Stream logStream)
    {
        JobId = jobId;
        Worker = worker;
        OutputDir = outputDir;
        PlaylistPath = playlistPath;
        Job = job;
        State = state;
        LogStream = logStream;
        _writer = new SegmentFileWriter(outputDir);
    }

    public string JobId { get; }

    public WorkerConnection Worker { get; }

    public string OutputDir { get; }

    public string PlaylistPath { get; }

    public TranscodingJob Job { get; }

    public StreamState State { get; }

    public Stream LogStream { get; }

    /// <summary>
    /// Gets or sets the last reported ffmpeg encode speed (multiple of realtime).
    /// Stored here because the core <see cref="TranscodingJob"/> has no speed field.
    /// </summary>
    public float Speed { get; set; }

    public TaskCompletionSource<bool> AcceptedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Appends a chunk for <paramref name="relPath"/> to a temp file; on eof atomically promotes it to
    /// the canonical path the controller polls.
    /// </summary>
    public void WriteSegment(string relPath, ReadOnlyMemory<byte> chunk, bool eof)
        => _writer.Write(relPath, chunk, eof);

    /// <summary>
    /// Appends a forwarded ffmpeg stderr line to the server-side log file.
    /// </summary>
    public void AppendLog(string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + Environment.NewLine);
        lock (_logLock)
        {
            try
            {
                LogStream.Write(bytes, 0, bytes.Length);
                LogStream.Flush();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        _writer.Dispose();

        try
        {
            LogStream.Dispose();
        }
        catch (IOException)
        {
        }
    }
}
