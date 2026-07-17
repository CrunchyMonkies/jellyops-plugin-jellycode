using System;
using System.Linq;
using Jellyfin.Plugin.DistributedTranscoding.Hosting;
using Jellyfin.Plugin.DistributedTranscoding.Server;
using Jellyfin.Plugin.DistributedTranscoding.Transcoding;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedTranscoding;

/// <summary>
/// Registers the plugin's services. Runs AFTER core registration, so re-registering
/// <see cref="ITranscodeManager"/> overrides the core binding (last registration wins).
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<WorkerRegistry>();
        serviceCollection.AddSingleton<RemoteTranscodeManager>();

        // Override the core ITranscodeManager binding with our remote implementation.
        serviceCollection.AddSingleton<ITranscodeManager>(sp => sp.GetRequiredService<RemoteTranscodeManager>());

        // The gRPC service delivers worker frames to the same manager instance.
        serviceCollection.AddSingleton<IRemoteFrameSink>(sp => sp.GetRequiredService<RemoteTranscodeManager>());

        // Self-hosted Kestrel + gRPC listener the workers dial.
        serviceCollection.AddHostedService<GrpcHostedService>();

        DecorateMediaEncoder(serviceCollection);
    }

    /// <summary>
    /// Wraps the core <see cref="IMediaEncoder"/> with <see cref="RemoteMediaEncoder"/> so trickplay frame
    /// extraction can be offloaded to the mesh. The wrapped inner encoder is constructed from the original
    /// descriptor's implementation type; every other <see cref="IMediaEncoder"/> member forwards to it.
    /// </summary>
    private static void DecorateMediaEncoder(IServiceCollection serviceCollection)
    {
        var descriptor = serviceCollection.LastOrDefault(d => d.ServiceType == typeof(IMediaEncoder));
        if (descriptor?.ImplementationType is not { } innerType)
        {
            // Core registers IMediaEncoder via an implementation type; if that ever changes we skip
            // decoration rather than risk losing the binding (trickplay simply stays local).
            return;
        }

        serviceCollection.Remove(descriptor);
        serviceCollection.AddSingleton<IMediaEncoder>(sp =>
        {
            var inner = (IMediaEncoder)ActivatorUtilities.CreateInstance(sp, innerType);

            // Resolve the manager lazily to break the IMediaEncoder <-> RemoteTranscodeManager cycle
            // (the manager depends on IMediaEncoder in its constructor).
            var manager = new Lazy<RemoteTranscodeManager>(sp.GetRequiredService<RemoteTranscodeManager>);

            return new RemoteMediaEncoder(
                inner,
                manager,
                sp.GetRequiredService<IServerConfigurationManager>(),
                sp.GetRequiredService<ILogger<RemoteMediaEncoder>>());
        });
    }
}
