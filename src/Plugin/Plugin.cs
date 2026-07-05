using System;
using Jellyfin.Plugin.DistributedTranscoding.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DistributedTranscoding;

/// <summary>
/// The Distributed Transcoding plugin. Overrides ITranscodeManager so ffmpeg runs on remote workers,
/// and hosts a gRPC listener those workers dial.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Distributed Transcoding";

    /// <inheritdoc />
    public override Guid Id => new("b9f8c1a2-3d4e-4f5a-9b6c-7d8e9f0a1b2c");

    /// <inheritdoc />
    public override string Description => "Offloads ffmpeg transcoding to a pool of remote worker pods over gRPC.";
}
