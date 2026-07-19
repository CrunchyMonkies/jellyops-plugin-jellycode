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

    /// <summary>
    /// Gets or sets a value indicating whether trickplay image extraction is offloaded to remote workers.
    /// When enabled (default), trickplay ffmpeg passes run on a worker; if no worker is available or the
    /// remote job fails, extraction falls back to the local server so trickplay never regresses.
    /// </summary>
    public bool OffloadTrickplay { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a streaming transcode request may preempt (cancel) an
    /// in-progress trickplay extraction job when all workers are busy, freeing a slot for the stream.
    /// The preempted trickplay job will be re-queued automatically.
    /// </summary>
    public bool StreamingPreemptsTrickplay { get; set; } = true;

    /// <summary>
    /// Gets or sets the per-worker-type transcoding options (cpu / intel / nvidia). Defaults leave
    /// the pipeline unchanged; operators can override codec/encoder/preset/quality/bitrate/etc. per
    /// type from the plugin settings page.
    /// </summary>
    public WorkerTypeOptions[] WorkerTypes { get; set; } = WorkerTypeOptions.Defaults();

    /// <summary>
    /// Gets or sets the default worker-type priority order used when no routing rule matches a job.
    /// The default (intel → nvidia → cpu) preserves the historical vaapi → nvenc → software ladder.
    /// </summary>
    public string[] DefaultWorkerPriority { get; set; } = ["intel", "nvidia", "cpu"];

    /// <summary>
    /// Gets or sets the codec-based routing rules. Each maps a (decode, encode) codec pair to an
    /// ordered worker-type preference; the most specific matching rule wins.
    /// </summary>
    public RoutingRule[] RoutingRules { get; set; } = System.Array.Empty<RoutingRule>();

    /// <summary>
    /// Returns the options for a worker type, falling back to defaults when absent.
    /// </summary>
    /// <param name="type">The worker type (cpu/intel/nvidia).</param>
    /// <returns>The matching options, or a default block.</returns>
    public WorkerTypeOptions OptionsFor(string type)
    {
        foreach (var wt in WorkerTypes)
        {
            if (string.Equals(wt.Type, type, System.StringComparison.OrdinalIgnoreCase))
            {
                return wt;
            }
        }

        return new WorkerTypeOptions { Type = type };
    }

    /// <summary>
    /// Resolves the ordered worker-type priority for a job, picking the most specific matching routing
    /// rule (both-codec &gt; single-codec &gt; wildcard) and falling back to <see cref="DefaultWorkerPriority"/>.
    /// </summary>
    /// <param name="decodeCodec">The job's source (decode) codec, or null.</param>
    /// <param name="encodeCodec">The job's target (encode) codec, <c>copy</c> for stream-copy, or null.</param>
    /// <returns>The ordered worker types (e.g. ["nvidia","intel","cpu"]).</returns>
    public string[] ResolveWorkerPriority(string? decodeCodec, string? encodeCodec) =>
        RoutingRule.Resolve(RoutingRules, DefaultWorkerPriority, decodeCodec, encodeCodec);
}
