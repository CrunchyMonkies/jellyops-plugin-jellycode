using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DistributedTranscoding.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedTranscoding.Hosting;

/// <summary>
/// Hosts a SEPARATE Kestrel + gRPC server on its own port inside the Jellyfin process. This avoids a
/// core fork (Jellyfin's own Kestrel/endpoint routing is untouched) while giving workers an endpoint
/// to dial. Phase 0 uses cleartext HTTP/2 (h2c); TLS/auth is Phase 3.
/// </summary>
public sealed class GrpcHostedService : IHostedService
{
    private readonly WorkerRegistry _registry;
    private readonly IRemoteFrameSink _sink;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GrpcHostedService> _logger;

    private WebApplication? _app;

    public GrpcHostedService(WorkerRegistry registry, IRemoteFrameSink sink, ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _sink = sink;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GrpcHostedService>();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var port = config.GrpcPort;
        var listenAddress = config.ListenAddress;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });

        // Route the child container's logging through Jellyfin's logger factory.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggerFactory);

        // Do NOT inherit Jellyfin's ASPNETCORE_URLS / Kestrel endpoint config — bind only our own port.
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");

        builder.WebHost.ConfigureKestrel(options =>
        {
            void ConfigureHttp2(ListenOptions lo) => lo.Protocols = HttpProtocols.Http2;

            if (string.IsNullOrWhiteSpace(listenAddress)
                || listenAddress == "0.0.0.0"
                || !IPAddress.TryParse(listenAddress, out var ip))
            {
                options.ListenAnyIP(port, ConfigureHttp2);
            }
            else
            {
                options.Listen(ip, port, ConfigureHttp2);
            }
        });

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(_registry);
        builder.Services.AddSingleton(_sink);
        builder.Services.AddSingleton<TranscodeMeshService>();

        _app = builder.Build();
        _app.MapGrpcService<TranscodeMeshService>();

        await _app.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Distributed transcoding gRPC listener started on {Address}:{Port} (h2c)", listenAddress, port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            try
            {
                await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await _app.DisposeAsync().ConfigureAwait(false);
                _app = null;
            }
        }
    }
}
