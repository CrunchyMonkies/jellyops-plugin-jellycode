using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DistributedTranscoding.Worker;

/// <summary>
/// Entry point for the distributed transcoding worker.
///
/// Usage:
///   Worker --server http://127.0.0.1:9090 --worker-id w1 --ffmpeg /path/to/ffmpeg [--max-concurrent 2] [--scratch /tmp/jellycode-worker] [--metrics-port 9091]
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var opts = ParseArgs(args);

        var server = opts.GetValueOrDefault("server", "http://127.0.0.1:9090");
        var workerId = opts.GetValueOrDefault("worker-id", Environment.MachineName + "-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var ffmpeg = opts.GetValueOrDefault("ffmpeg", "ffmpeg");
        var scratch = opts.GetValueOrDefault("scratch", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jellycode-worker"));
        var maxConcurrent = int.TryParse(opts.GetValueOrDefault("max-concurrent", "2"), out var mc) ? mc : 2;
        var metricsPort = int.TryParse(opts.GetValueOrDefault("metrics-port", "9091"), out var mp) ? mp : 9091;

        // Required for cleartext HTTP/2 (h2c) with Grpc.Net.Client; the server hosts h2c on its own port.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        Console.WriteLine($"[worker] id={workerId} server={server} ffmpeg={ffmpeg} maxConcurrent={maxConcurrent} scratch={scratch} metricsPort={metricsPort}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var metrics = new WorkerMetrics();

        // The metrics endpoint is strictly best-effort: it must NEVER block or crash the worker.
        // Start it off the critical path (fire-and-forget) so a slow or hung Kestrel start — observed
        // on some hardware ffmpeg images — cannot prevent the worker from probing ffmpeg and
        // registering with the mesh. A startup timeout bounds the diagnostic wait.
        MetricsHost? metricsHost = null;
        if (metricsPort != 0)
        {
            metricsHost = new MetricsHost(metricsPort);
            _ = StartMetricsBestEffortAsync(metricsHost, cts.Token);
        }

        try
        {
            var agent = new WorkerAgent(server, workerId, ffmpeg, scratch, maxConcurrent, metrics);
            return await agent.RunAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            if (metricsHost is not null)
            {
                try
                {
                    await metricsHost.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[worker] metrics host stop failed: {ex.Message}");
                }
            }
        }
    }

    private static async Task StartMetricsBestEffortAsync(MetricsHost host, CancellationToken cancellationToken)
    {
        try
        {
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startCts.CancelAfter(TimeSpan.FromSeconds(20));
            await host.StartAsync(startCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[worker] metrics host failed to start (continuing without metrics): {ex.Message}");
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            key = key[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = args[++i];
            }
            else
            {
                result[key] = "true";
            }
        }

        return result;
    }
}
