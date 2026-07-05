using System;
using System.Diagnostics;
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
    private readonly Action<string> _onFinished;

    private Process? _process;
    private OutputTailer? _tailer;

    public FfmpegJob(AssignJob assign, string ffmpegPath, string scratchRoot, ChannelWriter<WorkerFrame> outbound, Action<string> onFinished)
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
    }

    public string JobId => _assign.JobId;

    public void Start()
    {
        Directory.CreateDirectory(_localDir);

        var arguments = PathMapper.RewriteArguments(_assign.Arguments, _assign, _localDir);

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
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        _tailer.Start();

        _ = MonitorAsync(process);
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
        _onFinished(JobId);
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
