using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedTranscoding.Server;

/// <summary>
/// Tracks connected workers. Phase 0 scheduling is "first worker with a free slot"; the structure
/// supports many workers so Phase 1 can add a real least-loaded scheduler.
/// </summary>
public sealed class WorkerRegistry
{
    private readonly ConcurrentDictionary<string, WorkerConnection> _workers = new(StringComparer.Ordinal);
    private readonly ILogger<WorkerRegistry> _logger;

    public WorkerRegistry(ILogger<WorkerRegistry> logger)
    {
        _logger = logger;
    }

    public void Add(WorkerConnection worker)
    {
        _workers[worker.WorkerId] = worker;
        _logger.LogInformation("Worker registered: {WorkerId} (maxConcurrent={Max})", worker.WorkerId, worker.MaxConcurrent);
    }

    public void Remove(WorkerConnection worker)
    {
        _workers.TryRemove(worker.WorkerId, out _);
        _logger.LogInformation("Worker removed: {WorkerId}", worker.WorkerId);
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a worker with a free slot and returns it.
    /// </summary>
    public async Task<WorkerConnection> GetWorkerAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var candidate = _workers.Values
                .Where(w => w.FreeSlots > 0)
                .OrderByDescending(w => w.FreeSlots)
                .FirstOrDefault();

            if (candidate is not null)
            {
                return candidate;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("No transcoding worker with a free slot is available.");
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }
}
