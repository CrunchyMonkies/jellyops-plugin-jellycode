using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;

namespace Jellyfin.Plugin.DistributedTranscoding.Worker;

/// <summary>
/// Hosts a minimal Kestrel HTTP/1.1 server exposing <c>/metrics</c> (Prometheus scrape),
/// <c>/healthz</c>, and <c>/readyz</c>. Separate from the gRPC h2c channel.
/// </summary>
public sealed class MetricsHost
{
    private readonly int _port;
    private WebApplication? _app;

    public MetricsHost(int port)
    {
        _port = port;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Services.AddOpenTelemetry()
            .WithMetrics(m => m
                .AddMeter(WorkerMetrics.MeterName)
                .AddPrometheusExporter());

        var app = builder.Build();

        app.Urls.Add($"http://0.0.0.0:{_port}");
        app.MapPrometheusScrapingEndpoint("/metrics");
        app.MapGet("/healthz", () => Results.Ok("ok"));
        app.MapGet("/readyz", () => Results.Ok("ok"));

        _app = app;
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[worker] metrics listening on http://0.0.0.0:{_port}/metrics");
    }

    public async Task StopAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }
}
