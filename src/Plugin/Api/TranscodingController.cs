using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly WorkerRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodingController"/> class.
    /// </summary>
    /// <param name="registry">The shared worker registry singleton.</param>
    public TranscodingController(WorkerRegistry registry)
    {
        _registry = registry;
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
