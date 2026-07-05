using System;
using System.Collections.Generic;
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
    private readonly object _lock = new();
    private readonly Dictionary<string, FileStream> _open = new(StringComparer.Ordinal);

    public RemoteJobHandle(string jobId, WorkerConnection worker, string outputDir, string playlistPath, TranscodingJob job, StreamState state, Stream logStream)
    {
        JobId = jobId;
        Worker = worker;
        OutputDir = outputDir;
        PlaylistPath = playlistPath;
        Job = job;
        State = state;
        LogStream = logStream;
    }

    public string JobId { get; }

    public WorkerConnection Worker { get; }

    public string OutputDir { get; }

    public string PlaylistPath { get; }

    public TranscodingJob Job { get; }

    public StreamState State { get; }

    public Stream LogStream { get; }

    public TaskCompletionSource<bool> AcceptedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Appends a chunk for <paramref name="relPath"/> to a temp file; on eof atomically promotes it to
    /// the canonical path the controller polls.
    /// </summary>
    public void WriteSegment(string relPath, ReadOnlyMemory<byte> chunk, bool eof)
    {
        var finalPath = Path.GetFullPath(Path.Combine(OutputDir, relPath));
        var tempPath = finalPath + ".part";

        lock (_lock)
        {
            if (!_open.TryGetValue(relPath, out var stream))
            {
                stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                _open[relPath] = stream;
            }

            if (!chunk.IsEmpty)
            {
                stream.Write(chunk.Span);
            }

            if (eof)
            {
                stream.Flush();
                stream.Dispose();
                _open.Remove(relPath);

                var isPlaylist = relPath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
                Promote(tempPath, finalPath, overwrite: isPlaylist);
            }
        }
    }

    private static void Promote(string tempPath, string finalPath, bool overwrite)
    {
        try
        {
            File.Move(tempPath, finalPath, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(finalPath))
        {
            // Segment already present (duplicate send) — discard the temp.
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Appends a forwarded ffmpeg stderr line to the server-side log file.
    /// </summary>
    public void AppendLog(string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + Environment.NewLine);
        lock (_lock)
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var stream in _open.Values)
            {
                try
                {
                    stream.Dispose();
                }
                catch (IOException)
                {
                }
            }

            _open.Clear();
        }

        try
        {
            LogStream.Dispose();
        }
        catch (IOException)
        {
        }
    }
}
