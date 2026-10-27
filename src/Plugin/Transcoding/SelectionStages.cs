using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DistributedTranscoding.Transcoding;

/// <summary>One worker-selection pass: a worker type (or any worker) and whether it must hardware-decode.</summary>
/// <param name="Type">Worker type (cpu / intel / nvidia), or null for any worker.</param>
/// <param name="RequireHwDecode">Whether the worker must advertise a hardware decoder for the source codec.</param>
/// <param name="AnyWorker">Whether any worker type qualifies.</param>
public readonly record struct SelectionStage(string? Type, bool RequireHwDecode, bool AnyWorker);

/// <summary>
/// Pure stage planning for <see cref="RemoteTranscodeManager"/> (no Jellyfin dependencies, unit-tested).
/// </summary>
public static class SelectionStages
{
    /// <summary>
    /// Builds the ordered selection stages for a job. Video-encode jobs get two passes over the
    /// resolved worker-type priority — first preferring workers that can hardware-decode the source,
    /// then relaxed. The hardware-decode pass skips types that have no hardware decoders (cpu), which
    /// could never match it. Copy/audio jobs prefer a non-GPU worker, then any worker.
    /// </summary>
    /// <param name="isVideoEncode">Whether the job re-encodes video.</param>
    /// <param name="priority">Resolved worker-type priority.</param>
    /// <returns>The stages in the order they are tried.</returns>
    public static List<SelectionStage> Build(bool isVideoEncode, string[] priority)
    {
        var stages = new List<SelectionStage>();
        if (isVideoEncode)
        {
            foreach (var t in priority)
            {
                if (HasHwDecoders(t))
                {
                    stages.Add(new SelectionStage(t, RequireHwDecode: true, AnyWorker: false));
                }
            }

            foreach (var t in priority)
            {
                stages.Add(new SelectionStage(t, RequireHwDecode: false, AnyWorker: false));
            }
        }
        else
        {
            stages.Add(new SelectionStage("cpu", RequireHwDecode: false, AnyWorker: false));
            stages.Add(new SelectionStage(Type: null, RequireHwDecode: false, AnyWorker: true));
        }

        return stages;
    }

    /// <summary>
    /// Whether a stage should wait the full worker timeout for a free slot. Waiting only helps when a
    /// worker that could take the job is connected (busy now, may free a slot); otherwise the stage
    /// is skipped at once instead of stalling playback for the whole timeout. The last stage always
    /// waits, so a worker that is reconnecting can still pick the job up.
    /// </summary>
    /// <param name="stage">The stage.</param>
    /// <param name="isLastStage">Whether this is the final stage.</param>
    /// <param name="connectedWorkerTypes">Worker types of the currently connected workers.</param>
    /// <param name="typeEnabled">Whether a worker type is enabled in the plugin settings.</param>
    /// <returns>True to wait the full timeout; false to try the stage once without waiting.</returns>
    public static bool ShouldWait(SelectionStage stage, bool isLastStage, IReadOnlyCollection<string> connectedWorkerTypes, Func<string, bool> typeEnabled)
    {
        if (isLastStage)
        {
            return true;
        }

        if (stage.AnyWorker || stage.Type is null)
        {
            return connectedWorkerTypes.Count > 0;
        }

        if (!typeEnabled(stage.Type))
        {
            return false;
        }

        foreach (var t in connectedWorkerTypes)
        {
            if (string.Equals(t, stage.Type, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a worker type has hardware decoders (NVIDIA *_cuvid, Intel *_qsv/*_vaapi).</summary>
    /// <param name="type">Worker type.</param>
    /// <returns>True for GPU worker types.</returns>
    public static bool HasHwDecoders(string type)
        => string.Equals(type, "intel", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "nvidia", StringComparison.OrdinalIgnoreCase);
}
