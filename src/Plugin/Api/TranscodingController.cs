using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Jellyfin.Plugin.DistributedTranscoding.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DistributedTranscoding.Api;

/// <summary>
/// Admin API for the Distributed Transcoding settings page: lists the live workers and the union of
/// capabilities per worker type so the UI can offer only options the workers can actually satisfy.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("DistributedTranscoding")]
[Produces("application/json")]
public class TranscodingController : ControllerBase
{
    // The dashboard client script is a small embedded asset; load + hash it once (thread-safe).
    private static readonly Lazy<(string Content, string ETag)> ScriptCache =
        new(LoadScriptFromResource, LazyThreadSafetyMode.ExecutionAndPublication);

    // Chart.js UMD bundle served same-origin for the settings-page graph.
    private static readonly Lazy<(byte[] Content, string ETag)> ChartJsCache =
        new(LoadChartJsFromResource, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly WorkerRegistry _registry;
    private readonly IActiveJobSource _jobSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodingController"/> class.
    /// </summary>
    /// <param name="registry">The shared worker registry singleton.</param>
    /// <param name="jobSource">Provides active-job snapshots for the metrics endpoint.</param>
    public TranscodingController(WorkerRegistry registry, IActiveJobSource jobSource)
    {
        _registry = registry;
        _jobSource = jobSource;
    }

    /// <summary>
    /// Returns the Distributed Transcoding dashboard client script. Injected into index.html by the
    /// File Transformation plugin; it hides the native transcoding controls this plugin overrides and
    /// links to the plugin settings. Served anonymously because the browser fetches it as a plain
    /// &lt;script&gt; with no API auth header.
    /// </summary>
    /// <returns>The client JavaScript.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetClientScript()
    {
        var (content, etag) = ScriptCache.Value;

        var requestETag = Request.Headers.IfNoneMatch.FirstOrDefault();
        if (!string.IsNullOrEmpty(requestETag) && requestETag == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.CacheControl = "public, max-age=3600";
        Response.Headers.ETag = etag;
        return Content(content, "text/javascript");
    }

    private static (string Content, string ETag) LoadScriptFromResource()
    {
        var assembly = typeof(TranscodingController).Assembly;
        const string ResourceName = "Jellyfin.Plugin.DistributedTranscoding.Web.plugin.js";
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        var etag = $"\"{Convert.ToBase64String(hash)[..16]}\"";
        return (content, etag);
    }

    private static (byte[] Content, string ETag) LoadChartJsFromResource()
    {
        var assembly = typeof(TranscodingController).Assembly;
        const string ResourceName = "Jellyfin.Plugin.DistributedTranscoding.Web.chart.umd.min.js";
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var content = ms.ToArray();
        var hash = SHA256.HashData(content);
        var etag = $"\"{Convert.ToBase64String(hash)[..16]}\"";
        return (content, etag);
    }

    /// <summary>
    /// Returns aggregated transcode metrics for the settings-page graph.
    /// </summary>
    /// <returns>Cluster-wide and per-worker metrics.</returns>
    [HttpGet("Metrics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MetricsDto> GetMetrics()
    {
        var workers = _registry.Snapshot();
        var activeJobs = _jobSource.SnapshotActiveJobs();

        var jobsByWorker = activeJobs
            .GroupBy(j => j.WorkerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var workerMetrics = workers
            .Select(w =>
            {
                jobsByWorker.TryGetValue(w.WorkerId, out var jobs);
                return new WorkerMetricsDto
                {
                    WorkerId = w.WorkerId,
                    ActiveJobs = w.ActiveJobs,
                    EncodeFps = jobs?.Sum(j => j.Framerate) ?? 0,
                    BitrateKbps = jobs?.Sum(j => j.BitrateKbps) ?? 0,
                };
            })
            .OrderBy(w => w.WorkerId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var streamJobs = activeJobs.Where(j => string.Equals(j.Kind, "stream", StringComparison.Ordinal)).ToList();
        var trickplayActive = activeJobs.Count(j => string.Equals(j.Kind, "trickplay", StringComparison.Ordinal));
        var trickplayStats = _jobSource.GetTrickplayStats();

        var dto = new MetricsDto
        {
            ActiveStreams = streamJobs.Count,
            EncodeFps = activeJobs.Sum(j => j.Framerate),
            ThroughputKbps = activeJobs.Sum(j => j.BitrateKbps),
            AvgSpeed = activeJobs.Count > 0 ? activeJobs.Average(j => j.Speed) : 0,
            Workers = workerMetrics,
            TrickplayActive = trickplayActive,
            TrickplayAttempts = trickplayStats.Attempts,
            TrickplayRemoteOk = trickplayStats.RemoteOk,
            TrickplayRemoteFailed = trickplayStats.RemoteFailed,
            TrickplayLocalFallback = trickplayStats.LocalFallback,
        };

        return Ok(dto);
    }

    /// <summary>
    /// Returns the vendored Chart.js UMD bundle. Served anonymously because the browser loads it
    /// as a plain &lt;script src&gt; with no API auth header (same rationale as ClientScript).
    /// </summary>
    /// <returns>The Chart.js JavaScript bundle.</returns>
    [HttpGet("ChartJs")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetChartJs()
    {
        var (content, etag) = ChartJsCache.Value;

        var requestETag = Request.Headers.IfNoneMatch.FirstOrDefault();
        if (!string.IsNullOrEmpty(requestETag) && requestETag == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.CacheControl = "public, max-age=3600";
        Response.Headers.ETag = etag;
        return File(content, "text/javascript");
    }

    /// <summary>
    /// Lists the currently connected transcoder workers and their live state.
    /// </summary>
    /// <returns>The connected workers.</returns>
    [HttpGet("Workers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<WorkerDto>> GetWorkers()
    {
        var workers = _registry.Snapshot()
            .Select(WorkerDto.From)
            .OrderBy(w => w.Type)
            .ThenBy(w => w.WorkerId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(workers);
    }

    /// <summary>
    /// Returns, per worker type (cpu/intel/nvidia), the union of hardware accelerators and video
    /// encoders currently advertised by connected workers of that type. The settings UI uses this to
    /// filter the codec/encoder options it offers.
    /// </summary>
    /// <returns>Capabilities keyed by worker type.</returns>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<TypeCapabilitiesDto>> GetCapabilities()
    {
        var byType = _registry.Snapshot()
            .GroupBy(w => w.WorkerType, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TypeCapabilitiesDto
            {
                Type = g.Key,
                WorkerCount = g.Count(),
                HwAccels = g.SelectMany(w => w.HwAccels).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(),
                Encoders = g.SelectMany(w => w.Encoders).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(),
                Decoders = g.SelectMany(w => w.Decoders).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(),
            })
            .OrderBy(c => c.Type)
            .ToList();
        return Ok(byType);
    }
}

/// <summary>
/// Serializable view of one connected worker.
/// </summary>
public class WorkerDto
{
    /// <summary>Gets or sets the worker id.</summary>
    public string WorkerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the inferred worker type (cpu/intel/nvidia).</summary>
    public string Type { get; set; } = "cpu";

    /// <summary>Gets or sets the advertised hardware accelerators.</summary>
    public IReadOnlyList<string> HwAccels { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the advertised video encoders.</summary>
    public IReadOnlyList<string> Encoders { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the advertised video decoders.</summary>
    public IReadOnlyList<string> Decoders { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the worker's ffmpeg version.</summary>
    public string FfmpegVersion { get; set; } = "unknown";

    /// <summary>Gets or sets the maximum concurrent jobs the worker accepts.</summary>
    public int MaxConcurrent { get; set; }

    /// <summary>Gets or sets the currently free job slots.</summary>
    public int FreeSlots { get; set; }

    /// <summary>Gets or sets the number of active jobs.</summary>
    public int ActiveJobs { get; set; }

    /// <summary>Gets or sets the UTC time of the last frame from this worker.</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Maps a <see cref="WorkerConnection"/> to a DTO.</summary>
    /// <param name="w">The worker connection.</param>
    /// <returns>The DTO.</returns>
    public static WorkerDto From(WorkerConnection w) => new()
    {
        WorkerId = w.WorkerId,
        Type = w.WorkerType,
        HwAccels = w.HwAccels,
        Encoders = w.Encoders,
        Decoders = w.Decoders,
        FfmpegVersion = w.FfmpegVersion,
        MaxConcurrent = w.MaxConcurrent,
        FreeSlots = w.FreeSlots,
        ActiveJobs = w.ActiveJobs,
        LastSeenUtc = w.LastSeenUtc,
    };
}

/// <summary>
/// Union of capabilities advertised by connected workers of a given type.
/// </summary>
public class TypeCapabilitiesDto
{
    /// <summary>Gets or sets the worker type (cpu/intel/nvidia).</summary>
    public string Type { get; set; } = "cpu";

    /// <summary>Gets or sets the number of connected workers of this type.</summary>
    public int WorkerCount { get; set; }

    /// <summary>Gets or sets the union of advertised hardware accelerators.</summary>
    public IReadOnlyList<string> HwAccels { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the union of advertised video encoders.</summary>
    public IReadOnlyList<string> Encoders { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the union of advertised video decoders.</summary>
    public IReadOnlyList<string> Decoders { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Snapshot of a single active transcode job for the metrics API.
/// </summary>
public class ActiveJobDto
{
    /// <summary>Gets or sets the job identifier.</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>Gets or sets the worker handling this job.</summary>
    public string WorkerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the job kind ("stream" for playback transcodes, "trickplay" for batch image extraction).</summary>
    public string Kind { get; set; } = "stream";

    /// <summary>Gets or sets the current encode framerate.</summary>
    public float Framerate { get; set; }

    /// <summary>Gets or sets the current bitrate in kbps.</summary>
    public float BitrateKbps { get; set; }

    /// <summary>Gets or sets the total bytes transcoded so far.</summary>
    public long BytesTranscoded { get; set; }

    /// <summary>Gets or sets the completion percentage (0-100).</summary>
    public double PercentComplete { get; set; }

    /// <summary>Gets or sets the encode speed as a multiple of realtime.</summary>
    public float Speed { get; set; }
}

/// <summary>
/// Aggregated cluster-wide transcode metrics returned by the Metrics endpoint.
/// </summary>
public class MetricsDto
{
    /// <summary>Gets or sets the total number of active transcode streams (excludes trickplay).</summary>
    public int ActiveStreams { get; set; }

    /// <summary>Gets or sets the total encode framerate across all jobs (fps).</summary>
    public float EncodeFps { get; set; }

    /// <summary>Gets or sets the total throughput across all jobs (kbps).</summary>
    public float ThroughputKbps { get; set; }

    /// <summary>Gets or sets the average encode speed (multiple of realtime).</summary>
    public float AvgSpeed { get; set; }

    /// <summary>Gets or sets per-worker metrics.</summary>
    public IReadOnlyList<WorkerMetricsDto> Workers { get; set; } = Array.Empty<WorkerMetricsDto>();

    /// <summary>Gets or sets the number of active trickplay extraction jobs.</summary>
    public int TrickplayActive { get; set; }

    /// <summary>Gets or sets the total trickplay extraction attempts since plugin load.</summary>
    public long TrickplayAttempts { get; set; }

    /// <summary>Gets or sets the total successful remote trickplay extractions.</summary>
    public long TrickplayRemoteOk { get; set; }

    /// <summary>Gets or sets the total remote trickplay failures.</summary>
    public long TrickplayRemoteFailed { get; set; }

    /// <summary>Gets or sets the total trickplay local fallbacks.</summary>
    public long TrickplayLocalFallback { get; set; }
}

/// <summary>
/// Per-worker metrics within a <see cref="MetricsDto"/>.
/// </summary>
public class WorkerMetricsDto
{
    /// <summary>Gets or sets the worker identifier.</summary>
    public string WorkerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of active jobs on this worker.</summary>
    public int ActiveJobs { get; set; }

    /// <summary>Gets or sets the aggregate encode framerate for this worker (fps).</summary>
    public float EncodeFps { get; set; }

    /// <summary>Gets or sets the aggregate bitrate for this worker (kbps).</summary>
    public float BitrateKbps { get; set; }
}
