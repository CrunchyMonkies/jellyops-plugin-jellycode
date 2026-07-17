using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.DistributedTranscoding.Server;

/// <summary>
/// Reassembles chunked <c>SegmentData</c> frames into complete files in an output directory using a
/// temp-write + atomic-rename strategy, so a consumer polling that directory never sees a partial file.
/// Shared by <see cref="RemoteJobHandle"/> (HLS segments) and <see cref="BatchJobHandle"/> (trickplay jpgs).
/// </summary>
public sealed class SegmentFileWriter : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, FileStream> _open = new(StringComparer.Ordinal);
    private readonly string _outputDir;

    public SegmentFileWriter(string outputDir)
    {
        _outputDir = outputDir;
    }

    /// <summary>
    /// Appends a chunk for <paramref name="relPath"/> to a temp file; on eof atomically promotes it to
    /// the canonical path the consumer polls. Playlists (.m3u8) are overwritten; everything else is
    /// write-once (a duplicate send is discarded).
    /// </summary>
    public void Write(string relPath, ReadOnlyMemory<byte> chunk, bool eof)
    {
        var finalPath = Path.GetFullPath(Path.Combine(_outputDir, relPath));
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
    }
}
