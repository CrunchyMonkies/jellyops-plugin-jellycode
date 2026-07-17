using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.DistributedTranscoding.Contracts;

namespace Jellyfin.Plugin.DistributedTranscoding.Worker;

/// <summary>
/// One ffmpeg process for a single assigned job. Launches ffmpeg writing to a worker-local scratch
/// dir, forwards stderr as LogLine frames, tails completed output files back as SegmentData, and
/// reports JobExited on exit.
/// </summary>
public sealed class FfmpegJob
{
    private readonly AssignJob _assign;
    private readonly string _ffmpegPath;
    private readonly string _localDir;
    private readonly ChannelWriter<WorkerFrame> _outbound;
    private readonly Action<string, int> _onFinished;
    private readonly WorkerMetrics _metrics;

    private Process? _process;
    private OutputTailer? _tailer;

    private long _lastFrames;
    private long _lastTotalSize;

    private long _currentFrame;
    private float _currentFps;
    private float _currentBitrateKbps;
    private long _currentTotalSize;
    private long _currentOutTimeUs;
    private float _currentSpeed;

    public FfmpegJob(AssignJob assign, string ffmpegPath, string scratchRoot, ChannelWriter<WorkerFrame> outbound, Action<string, int> onFinished, WorkerMetrics metrics)
    {
        _assign = assign;
        // Always run the worker's OWN ffmpeg (from --ffmpeg/DT_FFMPEG). assign.EncoderPath is the
        // server's local ffmpeg path, which is meaningless in the worker container and may point at a
        // different build than the worker ships (e.g. the worker uses jellyfin-ffmpeg for HW encode).
        // Only fall back to the server's path if the worker was given none.
        _ffmpegPath = string.IsNullOrEmpty(ffmpegPath) ? assign.EncoderPath : ffmpegPath;
        _localDir = Path.Combine(scratchRoot, assign.JobId);
        _outbound = outbound;
        _onFinished = onFinished;
        _metrics = metrics;
    }

    public string JobId => _assign.JobId;

    public void Start()
    {
        Directory.CreateDirectory(_localDir);

        var arguments = PathMapper.RewriteArguments(_assign.Arguments, _assign, _localDir);
        arguments += " -progress pipe:2 -stats_period 1";

        Console.WriteLine($"[worker] job {JobId}: {_ffmpegPath} {arguments}");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                WorkingDirectory = _localDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            },
            EnableRaisingEvents = true
        };

        _process = process;
        _tailer = new OutputTailer(JobId, _localDir, _assign.OutputGlobs, _outbound);

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _outbound.TryWrite(new WorkerFrame { Log = new LogLine { JobId = JobId, Line = e.Data } });
                ParseProgressLine(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        _tailer.Start();

        _ = MonitorAsync(process);
    }

    private void ParseProgressLine(string line)
    {
        var eqIdx = line.IndexOf('=');
        if (eqIdx < 1)
        {
            return;
        }

        var key = line.AsSpan(0, eqIdx).Trim();
        var val = line.AsSpan(eqIdx + 1).Trim();

        if (key.SequenceEqual("frame".AsSpan()))
        {
            if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
            {
                _currentFrame = f;
            }
        }
        else if (key.SequenceEqual("fps".AsSpan()))
        {
            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            {
                _currentFps = f;
            }
        }
        else if (key.SequenceEqual("bitrate".AsSpan()))
        {
            _currentBitrateKbps = ParseBitrate(val);
        }
        else if (key.SequenceEqual("total_size".AsSpan()))
        {
            if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            {
                _currentTotalSize = s;
            }
        }
        else if (key.SequenceEqual("out_time_us".AsSpan()))
        {
            if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
            {
                _currentOutTimeUs = us;
            }
        }
        else if (key.SequenceEqual("speed".AsSpan()))
        {
            _currentSpeed = ParseSpeed(val);
        }
        else if (key.SequenceEqual("progress".AsSpan()))
        {
            FlushProgressBlock();
        }
    }

    private void FlushProgressBlock()
    {
        var frameDelta = _currentFrame - _lastFrames;
        var bytesDelta = _currentTotalSize - _lastTotalSize;
        _lastFrames = _currentFrame;
        _lastTotalSize = _currentTotalSize;

        _metrics.AddFrames(frameDelta);
        _metrics.AddBytes(bytesDelta);
        _metrics.UpdateJob(JobId, _currentFps, _currentSpeed, _currentBitrateKbps);

        _outbound.TryWrite(new WorkerFrame
        {
            Progress = new Progress
            {
                JobId = JobId,
                Framerate = _currentFps,
                Bitrate = (int)_currentBitrateKbps,
                BytesTranscoded = _currentTotalSize,
                PositionTicks = _currentOutTimeUs * 10,
                PercentComplete = 0,
                Speed = _currentSpeed
            }
        });
    }

    private static float ParseBitrate(ReadOnlySpan<char> val)
    {
        if (val.Contains("N/A".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var kIdx = val.IndexOf("kbits/s".AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (kIdx > 0)
        {
            val = val.Slice(0, kIdx);
        }

        if (float.TryParse(val.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
        {
            return rate;
        }

        return 0;
    }

    private static float ParseSpeed(ReadOnlySpan<char> val)
    {
        if (val.Contains("N/A".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (val.Length > 0 && (val[^1] == 'x' || val[^1] == 'X'))
        {
            val = val.Slice(0, val.Length - 1);
        }

        if (float.TryParse(val.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            return s;
        }

        return 0;
    }

    private async Task MonitorAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[worker] job {JobId} wait error: {ex.Message}");
        }

        // Final flush: stream any segments that hadn't been deemed complete while running.
        if (_tailer is not null)
        {
            await _tailer.StopAndFlushAsync().ConfigureAwait(false);
        }

        var exitCode = SafeExitCode(process);
        Console.WriteLine($"[worker] job {JobId} exited with code {exitCode}");
        _outbound.TryWrite(new WorkerFrame { Exited = new JobExited { JobId = JobId, ExitCode = exitCode } });

        TryCleanup();
        _onFinished(JobId, exitCode);
    }

    public void Stop()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                // Mirror core behaviour: ask ffmpeg to quit cleanly, then kill if it lingers.
                try
                {
                    process.StandardInput.WriteLine("q");
                }
                catch (Exception)
                {
                    // ignore — stdin may already be closed
                }

                if (!process.WaitForExit(5000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // process already gone
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[worker] job {JobId} stop error: {ex.Message}");
        }
    }

    public void Pause()
    {
        // Phase 0 stub. Phase 2 maps to SIGSTOP / ffmpeg pause-key.
        Console.WriteLine($"[worker] job {JobId} pause requested (stub)");
    }

    public void Resume()
    {
        // Phase 0 stub. Phase 2 maps to SIGCONT / ffmpeg resume-key.
        Console.WriteLine($"[worker] job {JobId} resume requested (stub)");
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private void TryCleanup()
    {
        try
        {
            _process?.Dispose();
            if (Directory.Exists(_localDir))
            {
                Directory.Delete(_localDir, recursive: true);
            }
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
