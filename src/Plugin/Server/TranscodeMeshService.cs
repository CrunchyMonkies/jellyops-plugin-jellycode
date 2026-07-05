using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Jellyfin.Plugin.DistributedTranscoding.Contracts;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedTranscoding.Server;

/// <summary>
/// Server side of the bidirectional mesh stream. One instance per worker connection.
/// </summary>
public sealed class TranscodeMeshService : TranscodeMesh.TranscodeMeshBase
{
    private readonly WorkerRegistry _registry;
    private readonly IRemoteFrameSink _sink;
    private readonly ILogger<TranscodeMeshService> _logger;

    public TranscodeMeshService(WorkerRegistry registry, IRemoteFrameSink sink, ILogger<TranscodeMeshService> logger)
    {
        _registry = registry;
        _sink = sink;
        _logger = logger;
    }

    public override async Task Connect(
        IAsyncStreamReader<WorkerFrame> requestStream,
        IServerStreamWriter<ServerFrame> responseStream,
        ServerCallContext context)
    {
        var token = context.CancellationToken;

        // First frame must be Register.
        if (!await requestStream.MoveNext(token).ConfigureAwait(false)
            || requestStream.Current.MsgCase != WorkerFrame.MsgOneofCase.Register)
        {
            _logger.LogWarning("Worker connection did not begin with a Register frame; closing.");
            return;
        }

        var register = requestStream.Current.Register;
        var worker = new WorkerConnection(register.WorkerId, Math.Max(1, register.MaxConcurrent));
        _registry.Add(worker);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var writerTask = DrainOutboundAsync(worker, responseStream, linked.Token);

        try
        {
            while (await requestStream.MoveNext(token).ConfigureAwait(false))
            {
                HandleFrame(worker, requestStream.Current);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Worker {WorkerId} stream error", worker.WorkerId);
        }
        finally
        {
            _registry.Remove(worker);
            _sink.OnWorkerLost(worker);
            worker.CompleteOutbound();
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await writerTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // teardown
            }
        }
    }

    private void HandleFrame(WorkerConnection worker, WorkerFrame frame)
    {
        switch (frame.MsgCase)
        {
            case WorkerFrame.MsgOneofCase.Heartbeat:
                worker.ActiveJobs = frame.Heartbeat.ActiveJobs;
                worker.FreeSlots = frame.Heartbeat.FreeSlots;
                break;
            case WorkerFrame.MsgOneofCase.Accepted:
                _sink.OnJobAccepted(frame.Accepted.JobId, frame.Accepted.Accepted, frame.Accepted.Reason);
                break;
            case WorkerFrame.MsgOneofCase.Segment:
                _sink.OnSegmentData(frame.Segment.JobId, frame.Segment.RelPath, frame.Segment.Chunk.Memory, frame.Segment.Eof);
                break;
            case WorkerFrame.MsgOneofCase.Progress:
                _sink.OnProgress(frame.Progress.JobId, frame.Progress);
                break;
            case WorkerFrame.MsgOneofCase.Log:
                _sink.OnLog(frame.Log.JobId, frame.Log.Line);
                break;
            case WorkerFrame.MsgOneofCase.Exited:
                _sink.OnJobExited(frame.Exited.JobId, frame.Exited.ExitCode);
                break;
        }
    }

    private static async Task DrainOutboundAsync(
        WorkerConnection worker,
        IServerStreamWriter<ServerFrame> responseStream,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in worker.Outbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
