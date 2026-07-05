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
    private readonly string _workerClass;
    private readonly string[] _hwAccels;

    private readonly ConcurrentDictionary<string, FfmpegJob> _jobs = new(StringComparer.Ordinal);

    // Recreated on every (re)connection: a Channel, once completed on disconnect, can never accept
    // writes again. A single lifetime channel would leave the worker able to Register (written
    // directly to the stream) but unable to send JobAccepted/Heartbeat/SegmentData after the first
    // reconnect. See ConnectOnceAsync.
    private Channel<WorkerFrame> _outbound =
        Channel.CreateUnbounded<WorkerFrame>(new UnboundedChannelOptions { SingleReader = true });

    private volatile bool _draining;

    public WorkerAgent(string serverUrl, string workerId, string ffmpegPath, string scratchRoot, int maxConcurrent)
    {
        _serverUrl = serverUrl;
        _workerId = workerId;
        _ffmpegPath = ffmpegPath;
        _scratchRoot = scratchRoot;
        _maxConcurrent = Math.Max(1, maxConcurrent);

        _workerClass = Environment.GetEnvironmentVariable("DT_CLASS")?.Trim().ToLowerInvariant() ?? "cpu";
        if (string.Equals(_workerClass, "hw", StringComparison.Ordinal))
        {
            var extra = Environment.GetEnvironmentVariable("DT_HWACCELS");
            _hwAccels = string.IsNullOrWhiteSpace(extra)
                ? new[] { "vaapi" }
                : extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Append("vaapi")
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToArray();
        }
        else
        {
            _hwAccels = Array.Empty<string>();
        }
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
            FfmpegVersion = "unknown"
        };
        reg.Hwaccels.AddRange(_hwAccels);
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

        // Reject if the job requires vaapi but this worker is not hw-capable.
        if (RequiresVaapi(assign.Arguments) && !Array.Exists(_hwAccels, h => string.Equals(h, "vaapi", StringComparison.OrdinalIgnoreCase)))
        {
            _outbound.Writer.TryWrite(new WorkerFrame
            {
                Accepted = new JobAccepted { JobId = assign.JobId, Accepted = false, Reason = "no vaapi" }
            });
            return;
        }

        var job = new FfmpegJob(assign, _ffmpegPath, _scratchRoot, _outbound.Writer, OnJobFinished);
        if (!_jobs.TryAdd(assign.JobId, job))
        {
            _outbound.Writer.TryWrite(new WorkerFrame
            {
                Accepted = new JobAccepted { JobId = assign.JobId, Accepted = false, Reason = "duplicate job id" }
            });
            return;
        }

        _outbound.Writer.TryWrite(new WorkerFrame { Accepted = new JobAccepted { JobId = assign.JobId, Accepted = true } });

        try
        {
            job.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[worker] failed to start job {assign.JobId}: {ex.Message}");
            _outbound.Writer.TryWrite(new WorkerFrame { Exited = new JobExited { JobId = assign.JobId, ExitCode = -1 } });
            _jobs.TryRemove(assign.JobId, out _);
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

    private void OnJobFinished(string jobId)
    {
        _jobs.TryRemove(jobId, out _);
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

    /// <summary>
    /// Returns true if the ffmpeg argument string contains a vaapi encoder (e.g. h264_vaapi, hevc_vaapi).
    /// </summary>
    private static bool RequiresVaapi(string arguments)
        => Regex.IsMatch(arguments, @"\b\w+_vaapi\b", RegexOptions.None, TimeSpan.FromMilliseconds(100));

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
