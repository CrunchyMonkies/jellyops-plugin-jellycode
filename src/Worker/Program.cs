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

        MetricsHost? metricsHost = null;
        if (metricsPort != 0)
        {
            metricsHost = new MetricsHost(metricsPort);
            await metricsHost.StartAsync(cts.Token).ConfigureAwait(false);
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
                await metricsHost.StopAsync().ConfigureAwait(false);
            }
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
