namespace Jellyfin.Plugin.DistributedTranscoding.Configuration;

/// <summary>
/// Transcoding options applied to jobs routed to a given worker type (cpu / intel / nvidia). All
/// fields default to "auto"/unset so the out-of-the-box behaviour matches the pre-existing pipeline
/// (options come straight from Jellyfin's core encoding options). Values here override that per type
/// when a job is dispatched to a worker of the matching type.
/// </summary>
public class WorkerTypeOptions
{
    /// <summary>
    /// Gets or sets the worker type this block applies to: <c>cpu</c>, <c>intel</c> or <c>nvidia</c>.
    /// Matches <see cref="Server.WorkerConnection.WorkerType"/>.
    /// </summary>
    public string Type { get; set; } = "cpu";

    /// <summary>
    /// Gets or sets a value indicating whether jobs may be routed to this worker type. When false the
    /// scheduler skips workers of this type (the tier ladder steps down to the next type).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the preferred output video codec: <c>auto</c> (keep the server's choice),
    /// <c>h264</c>, <c>hevc</c>, or <c>av1</c>.
    /// </summary>
    public string VideoCodec { get; set; } = "auto";

    /// <summary>
    /// Gets or sets a specific ffmpeg encoder to force (e.g. <c>hevc_nvenc</c>), or <c>auto</c> to let
    /// the encoding pipeline pick based on codec + accelerator. Only encoders the worker reports are
    /// offered in the UI.
    /// </summary>
    public string Encoder { get; set; } = "auto";

    /// <summary>
    /// Gets or sets the encoder preset (e.g. <c>veryfast</c> for x264, <c>p4</c> for NVENC), or empty
    /// to keep the core default.
    /// </summary>
    public string Preset { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the quality control mode: <c>auto</c> (core default), <c>crf</c> (software),
    /// <c>cq</c> (NVENC), <c>qp</c>, or <c>global_quality</c> (QSV/VAAPI).
    /// </summary>
    public string QualityMode { get; set; } = "auto";

    /// <summary>
    /// Gets or sets the quality value paired with <see cref="QualityMode"/> (e.g. CRF/CQ 0-51).
    /// Ignored when <see cref="QualityMode"/> is <c>auto</c>.
    /// </summary>
    public int QualityValue { get; set; }

    /// <summary>
    /// Gets or sets the maximum output video bitrate cap in kbps, or 0 for no cap.
    /// </summary>
    public int MaxBitrateKbps { get; set; }

    /// <summary>
    /// Gets or sets an override for the maximum concurrent jobs per worker of this type, or 0 to use
    /// the value the worker advertised at registration.
    /// </summary>
    public int MaxConcurrentOverride { get; set; }

    /// <summary>
    /// Gets or sets free-text extra ffmpeg arguments appended to jobs on this worker type (advanced).
    /// </summary>
    public string ExtraArgs { get; set; } = string.Empty;

    /// <summary>
    /// Builds the default per-type option set (cpu/intel/nvidia), all "auto"/unset so behaviour is
    /// unchanged until an operator customizes it.
    /// </summary>
    /// <returns>The default per-type options.</returns>
    public static WorkerTypeOptions[] Defaults() =>
    [
        new() { Type = "cpu" },
        new() { Type = "intel" },
        new() { Type = "nvidia" },
    ];
}
