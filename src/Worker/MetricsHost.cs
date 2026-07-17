using System;
using System.Threading;
using System.Threading.Tasks;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Jellyfin.Plugin.DistributedTranscoding.Worker;

/// <summary>
/// Serves the Prometheus <c>/metrics</c> scrape endpoint from a minimal
/// <see cref="System.Net.HttpListener"/> via OpenTelemetry's Prometheus HttpListener exporter.
/// Deliberately avoids the ASP.NET/Kestrel host: its startup could hang on worker nodes with
/// flaky DNS (host/hostname bootstrap), which previously blocked the worker from registering.
/// Separate from the gRPC h2c channel.
/// </summary>
public sealed class MetricsHost
{
    private readonly int _port;
    private MeterProvider? _provider;

    public MetricsHost(int port)
    {
        _port = port;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"[worker] metrics: starting Prometheus HttpListener on 0.0.0.0:{_port}...");
        _provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(WorkerMetrics.MeterName)
            .AddPrometheusHttpListener(options =>
            {
                options.ScrapeEndpointPath = "/metrics";
                options.ConfigureHttpListener = (_, listener) =>
                {
                    listener.Prefixes.Clear();
                    listener.Prefixes.Add($"http://+:{_port}/");
                };
            })
            .Build();
        Console.WriteLine($"[worker] metrics listening on http://0.0.0.0:{_port}/metrics");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _provider?.Dispose();
        _provider = null;
        return Task.CompletedTask;
    }
}
