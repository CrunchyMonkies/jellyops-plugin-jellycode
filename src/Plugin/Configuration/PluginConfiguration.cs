using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DistributedTranscoding.Configuration;

/// <summary>
/// Plugin configuration for the distributed transcoding mesh.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the address the plugin-hosted gRPC listener binds to.
    /// </summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Gets or sets the port the plugin-hosted gRPC listener uses (separate from Jellyfin's own ports).
    /// </summary>
    public int GrpcPort { get; set; } = 9090;

    /// <summary>
    /// Gets or sets how long StartFfMpeg waits for a worker / first output before failing playback.
    /// </summary>
    public int FirstSegmentTimeoutSeconds { get; set; } = 60;
}
