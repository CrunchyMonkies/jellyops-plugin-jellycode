using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Jellyfin.Plugin.DistributedTranscoding.Contracts;

namespace Jellyfin.Plugin.DistributedTranscoding.Worker;

/// <summary>
/// Watches the worker-local output dir and streams completed files back over the channel.
///
/// Completion rules (ffmpeg writes HLS output sequentially):
///   - A segment file is complete once a strictly-higher-indexed segment exists, or on final flush.
///   - The .m3u8 playlist is rewritten repeatedly; a fresh whole-file snapshot is sent whenever it
///     changes (and once more on final flush). The server replaces it atomically.
///   - The fmp4 init file (index -1) is just a low-indexed "segment" and flushes once segment 0 lands.
/// Only complete files (eof=true) are sent, so the server never exposes a partial file.
/// </summary>
public sealed class OutputTailer
{
    private const int ChunkSize = 64 * 1024;

    private readonly string _jobId;
    private readonly string _dir;
    private readonly string[] _globs;
    private readonly ChannelWriter<WorkerFrame> _outbound;
    private readonly HashSet<string> _sentSegments = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sweepLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _lastPlaylistSignature;

    public OutputTailer(string jobId, string dir, IEnumerable<string> globs, ChannelWriter<WorkerFrame> outbound)
    {
        _jobId = jobId;
        _dir = dir;
        var list = globs?.ToArray() ?? Array.Empty<string>();
        _globs = list.Length > 0 ? list : new[] { "*.ts", "*.mp4", "*.m3u8" };
        _outbound = outbound;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAndFlushAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Final sweep flushes everything still on disk.
        await SweepAsync(finalFlush: true).ConfigureAwait(false);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SweepAsync(finalFlush: false).ConfigureAwait(false);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SweepAsync(bool finalFlush)
    {
        await _sweepLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_dir))
            {
                return;
            }

            var files = new List<string>();
            foreach (var glob in _globs)
            {
                files.AddRange(Directory.EnumerateFiles(_dir, glob));
            }

            files = files.Distinct(StringComparer.Ordinal).ToList();

            var playlist = files.FirstOrDefault(f => f.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
            var prefix = playlist is not null ? Path.GetFileNameWithoutExtension(playlist) : null;

            // Index segments so we know which ones are "closed" (a higher index exists).
            var segments = files
                .Where(f => !f.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                .Select(f => new { Path = f, Index = ParseIndex(Path.GetFileName(f), prefix) })
                .ToList();

            var maxIndex = segments.Count > 0 ? segments.Max(s => s.Index) : long.MinValue;

            foreach (var seg in segments.OrderBy(s => s.Index))
            {
                var name = Path.GetFileName(seg.Path);
                if (_sentSegments.Contains(name))
                {
                    continue;
                }

                var closed = finalFlush || (seg.Index != long.MinValue && seg.Index < maxIndex);
                if (!closed)
                {
                    continue;
                }

                if (await StreamFileAsync(seg.Path, name).ConfigureAwait(false))
                {
                    _sentSegments.Add(name);
                }
            }

            // Playlist snapshot on change / final flush.
            if (playlist is not null)
            {
                var info = new FileInfo(playlist);
                var signature = info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                if (finalFlush || signature != _lastPlaylistSignature)
                {
                    if (await StreamFileAsync(playlist, Path.GetFileName(playlist)).ConfigureAwait(false))
                    {
                        _lastPlaylistSignature = signature;
                    }
                }
            }
        }
        finally
        {
            _sweepLock.Release();
        }
    }

    private async Task<bool> StreamFileAsync(string path, string relPath)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[ChunkSize];
            long total = 0;
            int read;
            var pending = new List<byte[]>();

            while ((read = await fs.ReadAsync(buffer.AsMemory(0, ChunkSize)).ConfigureAwait(false)) > 0)
            {
                var slice = new byte[read];
                Array.Copy(buffer, slice, read);
                pending.Add(slice);
                total += read;
            }

            // Emit frames; the last one (or a single empty one) carries eof=true.
            if (pending.Count == 0)
            {
                _outbound.TryWrite(new WorkerFrame
                {
                    Segment = new SegmentData { JobId = _jobId, RelPath = relPath, Chunk = ByteString.Empty, Eof = true }
                });
                return true;
            }

            for (var i = 0; i < pending.Count; i++)
            {
                _outbound.TryWrite(new WorkerFrame
                {
                    Segment = new SegmentData
                    {
                        JobId = _jobId,
                        RelPath = relPath,
                        Chunk = ByteString.CopyFrom(pending[i]),
                        Eof = i == pending.Count - 1
                    }
                });
            }

            return true;
        }
        catch (IOException)
        {
            // File still being written / locked; try again on the next sweep.
            return false;
        }
    }

    private static long ParseIndex(string fileName, string? prefix)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);

        if (prefix is not null && stem.StartsWith(prefix, StringComparison.Ordinal) && stem.Length > prefix.Length)
        {
            var rest = stem[prefix.Length..];
            if (long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            {
                return idx;
            }
        }

        // Fallback: trailing digits.
        var end = stem.Length;
        var start = end;
        while (start > 0 && char.IsDigit(stem[start - 1]))
        {
            start--;
        }

        if (start < end && long.TryParse(stem.AsSpan(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tail))
        {
            return tail;
        }

        return long.MinValue;
    }
}
