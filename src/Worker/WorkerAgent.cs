using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Jellyfin.Plugin.DistributedTranscoding.Contracts;

namespace Jellyfin.Plugin.DistributedTranscoding.Worker;

/// <summary>
/// Dials the server, registers, and pumps the single bidirectional stream:
/// outbound frames from all jobs are funneled through one writer (gRPC client streams are not
/// safe for concurrent writes); inbound frames are dispatched to per-job handlers.
/// </summary>
public sealed class WorkerAgent
{
    private readonly string _serverUrl;
    private readonly string _workerId;
    private readonly string _ffmpegPath;
    private readonly string _scratchRoot;
    private readonly int _maxConcurrent;
    private readonly string[] _hwAccels;
    private readonly string[] _encoders;
    private readonly string[] _decoders;
    private readonly string _ffmpegVersion;
    private readonly WorkerMetrics _metrics;

    private readonly ConcurrentDictionary<string, FfmpegJob> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _jobTypes = new(StringComparer.Ordinal);

    // Recreated on every (re)connection: a Channel, once completed on disconnect, can never accept
    // writes again. A single lifetime channel would leave the worker able to Register (written
    // directly to the stream) but unable to send JobAccepted/Heartbeat/SegmentData after the first
    // reconnect. See ConnectOnceAsync.
    private Channel<WorkerFrame> _outbound =
        Channel.CreateUnbounded<WorkerFrame>(new UnboundedChannelOptions { SingleReader = true });

    private volatile bool _draining;

    public WorkerAgent(string serverUrl, string workerId, string ffmpegPath, string scratchRoot, int maxConcurrent, WorkerMetrics metrics)
    {
        _serverUrl = serverUrl;
        _workerId = workerId;
        _ffmpegPath = ffmpegPath;
        _scratchRoot = scratchRoot;
        _maxConcurrent = Math.Max(1, maxConcurrent);
        _metrics = metrics;

        // The worker's actual hardware accelerators are declared by env, not inferred from ffmpeg's
        // encoder list (ffmpeg advertises every compiled-in encoder regardless of whether the hardware
        // is present). DT_HWACCELS is authoritative; DT_CLASS=hw with no list defaults to vaapi (Intel);
        // anything else is a software/cpu worker.
        var accelsEnv = Environment.GetEnvironmentVariable("DT_HWACCELS");
        var classEnv = Environment.GetEnvironmentVariable("DT_CLASS")?.Trim().ToLowerInvariant() ?? "cpu";
        if (!string.IsNullOrWhiteSpace(accelsEnv))
        {
            _hwAccels = accelsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToArray();
        }
        else if (string.Equals(classEnv, "hw", StringComparison.Ordinal))
        {
            _hwAccels = new[] { "vaapi" };
        }
        else
        {
            _hwAccels = Array.Empty<string>();
        }

        // Probe ffmpeg for the supported encoders / version, then keep only the encoders this worker
        // can actually use (software always; hardware encoders only for its advertised accelerators).
        // This is what drives the per-type codec options in the plugin settings UI.
        var caps = FfmpegCapabilities.Probe(_ffmpegPath);
        _encoders = FfmpegCapabilities.FilterForAccels(caps.Encoders, _hwAccels).ToArray();
        // Decode capability includes VAAPI/QSV entries synthesized from -hwaccels, since ffmpeg exposes
        // those decoders via -hwaccel rather than as named -decoders entries.
        _decoders = FfmpegCapabilities.BuildDecoderCapabilities(caps.Decoders, caps.HwAccelMethods, _hwAccels).ToArray();
        _ffmpegVersion = caps.Version;

        Console.WriteLine($"[worker] ffmpeg {_ffmpegVersion}; hwaccels=[{string.Join(",", _hwAccels)}]; encoders=[{string.Join(",", _encoders)}]; decoders=[{string.Join(",", _decoders)}]");

        _metrics.SetIdentity(_workerId, _ffmpegVersion, string.Join(",", _hwAccels));
        _metrics.SetCapacitySource(
            () => _jobs.Count,
            () => Math.Max(0, _maxConcurrent - _jobs.Count),
            () => _maxConcurrent);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[worker] connection error: {ex.Message}. Reconnecting in 3s...");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        return 0;
    }

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        // Keepalive pings keep the long-lived stream alive so the server's Kestrel doesn't close it
        // as idle (observed as "HTTP/2 server closed the connection NO_ERROR"), which would otherwise
        // force frequent reconnects.
        using var channel = GrpcChannel.ForAddress(_serverUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            },
        });
        var client = new TranscodeMesh.TranscodeMeshClient(channel);
        using var call = client.Connect(cancellationToken: cancellationToken);

        Console.WriteLine($"[worker] connected to {_serverUrl}");

        // Fresh outbound channel for this connection: the previous one was permanently completed on
        // the last disconnect, so reusing it would silently drop every frame except Register (which
        // is written directly to the stream below).
        _outbound = Channel.CreateUnbounded<WorkerFrame>(new UnboundedChannelOptions { SingleReader = true });

        // Register must be the first frame.
        var reg = new Register
        {
            WorkerId = _workerId,
            MaxConcurrent = _maxConcurrent,
            FfmpegVersion = _ffmpegVersion
        };
        reg.Hwaccels.AddRange(_hwAccels);
        reg.Encoders.AddRange(_encoders);
        reg.Decoders.AddRange(_decoders);
        var register = new WorkerFrame { Register = reg };
        await call.RequestStream.WriteAsync(register, cancellationToken).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var writerTask = WriteOutboundAsync(call.RequestStream, linked.Token);
        var heartbeatTask = HeartbeatLoopAsync(linked.Token);

        try
        {
            await foreach (var frame in call.ResponseStream.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                Dispatch(frame);
            }
        }
        finally
        {
            linked.Cancel();
            _outbound.Writer.TryComplete();
            await Task.WhenAll(Safe(writerTask), Safe(heartbeatTask)).ConfigureAwait(false);
        }
    }

    private void Dispatch(ServerFrame frame)
    {
        switch (frame.MsgCase)
        {
            case ServerFrame.MsgOneofCase.Assign:
                OnAssign(frame.Assign);
                break;
            case ServerFrame.MsgOneofCase.Control:
                OnControl(frame.Control);
                break;
            case ServerFrame.MsgOneofCase.Drain:
                _draining = true;
                Console.WriteLine($"[worker] draining: {frame.Drain.Reason}");
                break;
        }
    }

    private void OnAssign(AssignJob assign)
    {
        if (_draining || _jobs.Count >= _maxConcurrent)
        {
            _outbound.Writer.TryWrite(new WorkerFrame
            {
                Accepted = new JobAccepted { JobId = assign.JobId, Accepted = false, Reason = _draining ? "draining" : "no free slots" }
            });
            return;
        }

        // Reject if the job requires a hardware encoder this worker does not advertise.
        var missing = MissingAccelerator(assign.Arguments);
        if (missing is not null)
        {
            _outbound.Writer.TryWrite(new WorkerFrame
            {
                Accepted = new JobAccepted { JobId = assign.JobId, Accepted = false, Reason = $"no {missing}" }
            });
            return;
        }

        var job = new FfmpegJob(assign, _ffmpegPath, _scratchRoot, _outbound.Writer, OnJobFinished, _metrics);
        if (!_jobs.TryAdd(assign.JobId, job))
        {
            _outbound.Writer.TryWrite(new WorkerFrame
            {
                Accepted = new JobAccepted { JobId = assign.JobId, Accepted = false, Reason = "duplicate job id" }
            });
            return;
        }

        var jobTypeTag = WorkerMetrics.JobTypeTag(assign.Type);
        _jobTypes[assign.JobId] = jobTypeTag;

        _outbound.Writer.TryWrite(new WorkerFrame { Accepted = new JobAccepted { JobId = assign.JobId, Accepted = true } });
        _metrics.JobStarted(jobTypeTag);

        try
        {
            job.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[worker] failed to start job {assign.JobId}: {ex.Message}");
            _outbound.Writer.TryWrite(new WorkerFrame { Exited = new JobExited { JobId = assign.JobId, ExitCode = -1 } });
            _jobs.TryRemove(assign.JobId, out _);
            _jobTypes.TryRemove(assign.JobId, out _);
            _metrics.RemoveJob(assign.JobId);
            _metrics.JobFailed(jobTypeTag);
        }
    }

    private void OnControl(JobControl control)
    {
        if (!_jobs.TryGetValue(control.JobId, out var job))
        {
            return;
        }

        switch (control.Action)
        {
            case JobControl.Types.Action.Stop:
                job.Stop();
                break;
            case JobControl.Types.Action.Pause:
                job.Pause();
                break;
            case JobControl.Types.Action.Resume:
                job.Resume();
                break;
            case JobControl.Types.Action.Ping:
                break;
        }
    }

    private void OnJobFinished(string jobId, int exitCode)
    {
        _jobs.TryRemove(jobId, out _);
        _jobTypes.TryRemove(jobId, out var jobTypeTag);
        jobTypeTag ??= "unknown";
        _metrics.RemoveJob(jobId);

        if (exitCode == 0)
        {
            _metrics.JobCompleted(jobTypeTag);
        }
        else
        {
            _metrics.JobFailed(jobTypeTag);
        }
    }

    private async Task WriteOutboundAsync(IClientStreamWriter<WorkerFrame> stream, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var active = _jobs.Count;
                _outbound.Writer.TryWrite(new WorkerFrame
                {
                    Heartbeat = new Heartbeat
                    {
                        ActiveJobs = active,
                        FreeSlots = Math.Max(0, _maxConcurrent - active),
                        Cpu = 0
                    }
                });

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static readonly Regex VaapiEncoderRegex = new(@"\b\w+_vaapi\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex NvencEncoderRegex = new(@"\b\w+_nvenc\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Returns the name of a hardware accelerator the ffmpeg arguments require but this worker does not
    /// advertise (e.g. "vaapi" for h264_vaapi/hevc_vaapi, "nvenc" for h264_nvenc/hevc_nvenc), or null
    /// when the worker can run the job.
    /// </summary>
    private string? MissingAccelerator(string arguments)
    {
        if (VaapiEncoderRegex.IsMatch(arguments) && !HasAccel("vaapi"))
        {
            return "vaapi";
        }

        if (NvencEncoderRegex.IsMatch(arguments) && !HasAccel("nvenc"))
        {
            return "nvenc";
        }

        return null;
    }

    private bool HasAccel(string name)
        => Array.Exists(_hwAccels, h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));

    private static async Task Safe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // swallow — connection teardown
        }
    }
}
