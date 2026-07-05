using Jellyfin.Plugin.DistributedTranscoding.Hosting;
using Jellyfin.Plugin.DistributedTranscoding.Server;
using Jellyfin.Plugin.DistributedTranscoding.Transcoding;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

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
    }
}
