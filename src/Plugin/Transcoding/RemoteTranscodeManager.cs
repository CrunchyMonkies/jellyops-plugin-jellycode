using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using Jellyfin.Plugin.DistributedTranscoding.Api;
using Jellyfin.Plugin.DistributedTranscoding.Configuration;
using Jellyfin.Plugin.DistributedTranscoding.Contracts;
using Jellyfin.Plugin.DistributedTranscoding.Server;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedTranscoding.Transcoding;

/// <summary>
/// An <see cref="ITranscodeManager"/> that runs ffmpeg on remote workers instead of locally.
///
/// It reuses all the local prep the HLS controller already expects (directory + log file + in-memory
/// job bookkeeping + the WaitForPath contract), but replaces <c>process.Start()</c> with an AssignJob
/// sent to a worker. Segment bytes streamed back over gRPC are written to the canonical transcode
/// paths the controller polls, so the HTTP layer needs no changes.
///
/// The in-memory lookup/lifecycle methods are copied from core <c>TranscodeManager</c>; the only
/// behavioural change is that kill/stop routes to a gRPC <c>JobControl{STOP}</c> instead of the core
/// <c>TranscodingJob.Stop()</c> (which assumes a local Process and would NRE for remote jobs).
/// </summary>
public sealed class RemoteTranscodeManager : ITranscodeManager, IRemoteFrameSink, IActiveJobSource, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RemoteTranscodeManager> _logger;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _appPaths;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly EncodingHelper _encodingHelper;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IAttachmentExtractor _attachmentExtractor;
    private readonly WorkerRegistry _registry;

    private readonly List<TranscodingJob> _activeTranscodingJobs = new();
    private readonly ConcurrentDictionary<string, RemoteJobHandle> _remoteJobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BatchJobHandle> _batchJobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _transcodingLocks = new(StringComparer.Ordinal);

    private long _trickplayAttempts;
    private long _trickplayRemoteOk;
    private long _trickplayRemoteFailed;
    private long _trickplayLocalFallback;

    public RemoteTranscodeManager(
        ILoggerFactory loggerFactory,
        IFileSystem fileSystem,
        IApplicationPaths appPaths,
        IServerConfigurationManager serverConfigurationManager,
        IUserManager userManager,
        ISessionManager sessionManager,
        EncodingHelper encodingHelper,
        IMediaEncoder mediaEncoder,
        IMediaSourceManager mediaSourceManager,
        IAttachmentExtractor attachmentExtractor,
        WorkerRegistry registry)
    {
        _loggerFactory = loggerFactory;
        _fileSystem = fileSystem;
        _appPaths = appPaths;
        _serverConfigurationManager = serverConfigurationManager;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _encodingHelper = encodingHelper;
        _mediaEncoder = mediaEncoder;
        _mediaSourceManager = mediaSourceManager;
        _attachmentExtractor = attachmentExtractor;
        _registry = registry;

        _logger = loggerFactory.CreateLogger<RemoteTranscodeManager>();
        DeleteEncodedMediaCache();
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStart += OnPlaybackProgress;
    }

    private Configuration.PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    // ----------------------------------------------------------------------------------------------
    // ITranscodeManager — in-memory lookups (copied from core)
    // ----------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public TranscodingJob? GetTranscodingJob(string playSessionId)
    {
        lock (_activeTranscodingJobs)
        {
            return _activeTranscodingJobs.FirstOrDefault(j => string.Equals(j.PlaySessionId, playSessionId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type)
    {
        lock (_activeTranscodingJobs)
        {
            return _activeTranscodingJobs.FirstOrDefault(j => j.Type == type && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public void PingTranscodingJob(string playSessionId, bool? isUserPaused)
    {
        ArgumentException.ThrowIfNullOrEmpty(playSessionId);

        List<TranscodingJob> jobs;
        lock (_activeTranscodingJobs)
        {
            jobs = _activeTranscodingJobs.Where(j => string.Equals(playSessionId, j.PlaySessionId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        foreach (var job in jobs)
        {
            if (isUserPaused.HasValue)
            {
                job.IsUserPaused = isUserPaused.Value;
            }

            PingTimer(job, true);
        }
    }

    private void PingTimer(TranscodingJob job, bool isProgressCheckIn)
    {
        if (job.HasExited)
        {
            job.StopKillTimer();
            return;
        }

        var timerDuration = job.Type == TranscodingJobType.Progressive ? 10000 : 60000;
        job.PingTimeout = timerDuration;
        job.LastPingDate = DateTime.UtcNow;

        if (job.Type != TranscodingJobType.Progressive || !isProgressCheckIn)
        {
            job.StartKillTimer(OnTranscodeKillTimerStopped);
        }
        else
        {
            job.ChangeKillTimerIfStarted();
        }
    }

    private async void OnTranscodeKillTimerStopped(object? state)
    {
        var job = state as TranscodingJob ?? throw new ArgumentException($"{nameof(state)} is not of type {nameof(TranscodingJob)}", nameof(state));
        if (!job.HasExited && job.Type != TranscodingJobType.Progressive)
        {
            var timeSinceLastPing = (DateTime.UtcNow - job.LastPingDate).TotalMilliseconds;
            if (timeSinceLastPing < job.PingTimeout)
            {
                job.StartKillTimer(OnTranscodeKillTimerStopped, job.PingTimeout);
                return;
            }
        }

        _logger.LogInformation("Transcoding kill timer stopped for JobId {0} PlaySessionId {1}. Killing transcoding", job.Id, job.PlaySessionId);
        await KillTranscodingJob(job, true, _ => true).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------------------------------
    // ITranscodeManager — kill (routes to gRPC STOP instead of TranscodingJob.Stop())
    // ----------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles)
    {
        var jobs = new List<TranscodingJob>();
        lock (_activeTranscodingJobs)
        {
            jobs.AddRange(_activeTranscodingJobs.Where(j => string.IsNullOrWhiteSpace(playSessionId)
                ? string.Equals(deviceId, j.DeviceId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(playSessionId, j.PlaySessionId, StringComparison.OrdinalIgnoreCase)));
        }

        return Task.WhenAll(jobs.Select(j => KillTranscodingJob(j, false, deleteFiles)));
    }

    private async Task KillTranscodingJob(TranscodingJob job, bool closeLiveStream, Func<string, bool> delete)
    {
        job.DisposeKillTimer();
        _logger.LogDebug("KillTranscodingJob - JobId {0} PlaySessionId {1}", job.Id, job.PlaySessionId);

        lock (_activeTranscodingJobs)
        {
            _activeTranscodingJobs.Remove(job);
            if (job.CancellationTokenSource?.IsCancellationRequested == false)
            {
                job.CancellationTokenSource.Cancel();
            }
        }

        // Remote replacement for job.Stop(): tell the worker to stop its ffmpeg.
        if (job.Id is not null && _remoteJobs.TryGetValue(job.Id, out var handle))
        {
            handle.Worker.TrySend(new ServerFrame
            {
                Control = new JobControl { JobId = job.Id, Action = JobControl.Types.Action.Stop }
            });
        }

        if (delete(job.Path!))
        {
            await DeletePartialStreamFiles(job.Path!, job.Type, 0, 1500).ConfigureAwait(false);
        }

        if (closeLiveStream && !string.IsNullOrWhiteSpace(job.LiveStreamId))
        {
            await _sessionManager.CloseLiveStreamIfNeededAsync(job.LiveStreamId, job.PlaySessionId).ConfigureAwait(false);
        }
    }

    private async Task DeletePartialStreamFiles(string path, TranscodingJobType jobType, int retryCount, int delayMs)
    {
        if (retryCount >= 10)
        {
            return;
        }

        _logger.LogInformation("Deleting partial stream file(s) {Path}", path);
        await Task.Delay(delayMs).ConfigureAwait(false);

        try
        {
            if (jobType == TranscodingJobType.Progressive)
            {
                if (File.Exists(path))
                {
                    _fileSystem.DeleteFile(path);
                }
            }
            else
            {
                DeleteHlsPartialStreamFiles(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path);
            await DeletePartialStreamFiles(path, jobType, retryCount + 1, 500).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path);
        }
    }

    private void DeleteHlsPartialStreamFiles(string outputFilePath)
    {
        var directory = Path.GetDirectoryName(outputFilePath)
                        ?? throw new ArgumentException("Path can't be a root directory.", nameof(outputFilePath));
        var name = Path.GetFileNameWithoutExtension(outputFilePath);

        var filesToDelete = _fileSystem.GetFilePaths(directory)
            .Where(f => f.Contains(name, StringComparison.OrdinalIgnoreCase));

        foreach (var file in filesToDelete)
        {
            try
            {
                _fileSystem.DeleteFile(file);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Error deleting HLS file {Path}", file);
            }
        }
    }

    // ----------------------------------------------------------------------------------------------
    // ITranscodeManager — progress (copied from core)
    // ----------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public void ReportTranscodingProgress(
        TranscodingJob job,
        StreamState state,
        TimeSpan? transcodingPosition,
        float? framerate,
        double? percentComplete,
        long? bytesTranscoded,
        int? bitRate)
    {
        var ticks = transcodingPosition?.Ticks;
        if (job is not null)
        {
            job.Framerate = framerate;
            job.CompletionPercentage = percentComplete;
            job.TranscodingPositionTicks = ticks;
            job.BytesTranscoded = bytesTranscoded;
            job.BitRate = bitRate;
        }

        var deviceId = state.Request.DeviceId;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var audioCodec = state.ActualOutputAudioCodec;
            var videoCodec = state.ActualOutputVideoCodec;
            var hardwareAccelerationType = _serverConfigurationManager.GetEncodingOptions().HardwareAccelerationType;

            _sessionManager.ReportTranscodingInfo(deviceId, new TranscodingInfo
            {
                Bitrate = bitRate ?? state.TotalOutputBitrate,
                AudioCodec = audioCodec,
                VideoCodec = videoCodec,
                Container = state.OutputContainer,
                Framerate = framerate,
                CompletionPercentage = percentComplete,
                Width = state.OutputWidth,
                Height = state.OutputHeight,
                AudioChannels = state.OutputAudioChannels,
                IsAudioDirect = EncodingHelper.IsCopyCodec(state.OutputAudioCodec),
                IsVideoDirect = EncodingHelper.IsCopyCodec(state.OutputVideoCodec),
                HardwareAccelerationType = hardwareAccelerationType,
                TranscodeReasons = state.TranscodeReasons
            });
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Job classification and software fallback
    // ----------------------------------------------------------------------------------------------

    /// <summary>One step in the worker-selection sequence: a worker type to try (with an optional
    /// requirement that it can hardware-decode the source), or an any-worker fallback.</summary>
    private readonly record struct SelectionStage(string? Type, bool RequireHwDecode, bool AnyWorker);

    /// <summary>
    /// Builds the ordered selection stages for a job. Video-encode jobs get two passes over the
    /// resolved worker-type priority — first preferring workers that can hardware-decode the source,
    /// then relaxed. Copy/audio jobs prefer a non-GPU worker, then any worker.
    /// </summary>
    private static List<SelectionStage> BuildSelectionStages(bool isVideoEncode, string[] priority)
    {
        var stages = new List<SelectionStage>();
        if (isVideoEncode)
        {
            foreach (var t in priority)
            {
                stages.Add(new SelectionStage(t, RequireHwDecode: true, AnyWorker: false));
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

    /// <summary>Builds the worker predicate for a selection stage: type match + enabled + concurrency,
    /// plus (for encodes) a best-effort encode-capability gate, optional hw-decode requirement, and
    /// an audio-encoder gate when the job needs a specific audio encoder.</summary>
    private Func<WorkerConnection, bool> BuildStagePredicate(SelectionStage stage, string? decodeCodec, string? encodeCodec, bool isCopy, string? requiredAudioEncoder)
    {
        if (stage.AnyWorker || stage.Type is null)
        {
            return _ => true;
        }

        var type = stage.Type;
        var opts = Config.OptionsFor(type);
        return w =>
        {
            if (!opts.Enabled)
            {
                return false;
            }

            if (!string.Equals(w.WorkerType, type, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!WithinConcurrency(w, opts))
            {
                return false;
            }

            if (!isCopy && !WorkerSupportsEncode(w, encodeCodec, type))
            {
                return false;
            }

            if (requiredAudioEncoder is not null
                && w.Encoders.Count > 0
                && !w.Encoders.Any(e => string.Equals(e, requiredAudioEncoder, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (stage.RequireHwDecode && !WorkerCanHwDecode(w, decodeCodec, type))
            {
                return false;
            }

            return true;
        };
    }

    /// <summary>Best-effort encode-capability check: a worker that reports encoders must advertise the
    /// one needed for (encodeCodec, type); a worker reporting none (older build) is not excluded.</summary>
    private static bool WorkerSupportsEncode(WorkerConnection w, string? encodeCodec, string type)
    {
        if (w.Encoders.Count == 0)
        {
            return true;
        }

        var encoder = WorkerTypeEncoderName(encodeCodec, type);
        if (encoder is null)
        {
            return true; // unknown codec/type mapping — don't exclude
        }

        return w.Encoders.Contains(encoder, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the worker advertises a hardware decoder for the source codec on this type.</summary>
    private static bool WorkerCanHwDecode(WorkerConnection w, string? decodeCodec, string type)
    {
        if (string.IsNullOrEmpty(decodeCodec))
        {
            return false;
        }

        var names = HwDecoderNames(decodeCodec, type);
        return names.Count > 0 && names.Any(n => w.Decoders.Contains(n, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Maps (codec, worker type) to the concrete ffmpeg encoder name (e.g. hevc_nvenc,
    /// h264_vaapi, libx265). Returns null when the mapping is unknown.</summary>
    private static string? WorkerTypeEncoderName(string? codec, string type)
    {
        if (string.IsNullOrEmpty(codec) || string.Equals(codec, "copy", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var c = codec.ToLowerInvariant();
        return type.ToLowerInvariant() switch
        {
            "nvidia" => $"{c}_nvenc",
            "intel" => $"{c}_vaapi",
            "cpu" => c switch { "h264" => "libx264", "hevc" => "libx265", "av1" => "libsvtav1", _ => null },
            _ => null,
        };
    }

    /// <summary>Maps (codec, worker type) to the hardware decoder names for that type (NVIDIA uses
    /// *_cuvid; Intel uses *_qsv or *_vaapi). CPU has no hardware decoders.</summary>
    private static IReadOnlyList<string> HwDecoderNames(string codec, string type)
    {
        var c = codec.ToLowerInvariant();
        return type.ToLowerInvariant() switch
        {
            "nvidia" => new[] { $"{c}_cuvid" },
            "intel" => new[] { $"{c}_qsv", $"{c}_vaapi" },
            _ => Array.Empty<string>(),
        };
    }

    /// <summary>
    /// Builds the HLS command line for a job destined for a given worker type (cpu / intel / nvidia),
    /// applying that type's configured transcoding options on top of the server's encoding options.
    /// </summary>
    /// <param name="state">The stream state.</param>
    /// <param name="outputPath">The HLS output path.</param>
    /// <param name="workerType">The target worker type (cpu/intel/nvidia).</param>
    /// <param name="serverArgs">The command line the server already emitted, reused verbatim for the
    /// intel/vaapi tier when that type has no options that require regenerating the command.</param>
    /// <returns>The final ffmpeg argument string.</returns>
    private string BuildCommandLineForType(StreamState state, string outputPath, string workerType, string? serverArgs)
    {
        var typeOpts = Config.OptionsFor(workerType);

        string args;
        if (serverArgs is not null && !NeedsRegeneration(typeOpts))
        {
            // Intel/vaapi tier with no structural overrides: keep the exact command the server emitted.
            args = serverArgs;
        }
        else
        {
            var opts = CloneEncodingOptions(_serverConfigurationManager.GetEncodingOptions());
            SetHardwareAcceleration(opts, workerType);
            ApplyEncodingOptionOverrides(opts, typeOpts);
            args = _encodingHelper.GetHlsVideoCommandLine(state, opts, outputPath, 0, true);
        }

        // Arg-level overrides safe to apply on any command line: encoder/codec swap, bitrate cap,
        // hardware quality (cq/qp/global_quality) and free-text extra args.
        return WorkerTypeArgs.Apply(args, typeOpts);
    }

    /// <summary>Honors a type's optional per-worker max-concurrent cap when selecting a worker.</summary>
    private static bool WithinConcurrency(Server.WorkerConnection w, WorkerTypeOptions o) =>
        o.MaxConcurrentOverride <= 0 || w.ActiveJobs < o.MaxConcurrentOverride;

    /// <summary>True when a type's options require regenerating the command from EncodingOptions
    /// (preset or CRF) rather than reusing the server-emitted args.</summary>
    private static bool NeedsRegeneration(WorkerTypeOptions o) =>
        !string.IsNullOrWhiteSpace(o.Preset)
        || string.Equals(o.QualityMode, "crf", StringComparison.OrdinalIgnoreCase);

    private static void SetHardwareAcceleration(EncodingOptions opts, string workerType)
    {
        switch (workerType.ToLowerInvariant())
        {
            case "nvidia":
                opts.HardwareAccelerationType = HardwareAccelerationType.nvenc;
                opts.EnableHardwareEncoding = true;
                break;
            case "intel":
                opts.HardwareAccelerationType = HardwareAccelerationType.vaapi;
                opts.EnableHardwareEncoding = true;
                break;
            default: // cpu
                opts.HardwareAccelerationType = HardwareAccelerationType.none;
                opts.EnableHardwareEncoding = false;
                break;
        }
    }

    /// <summary>Applies the option fields that map directly onto core EncodingOptions (preset, CRF).</summary>
    private static void ApplyEncodingOptionOverrides(EncodingOptions opts, WorkerTypeOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.Preset)
            && Enum.TryParse<EncoderPreset>(o.Preset, ignoreCase: true, out var preset))
        {
            opts.EncoderPreset = preset;
        }

        if (string.Equals(o.QualityMode, "crf", StringComparison.OrdinalIgnoreCase) && o.QualityValue > 0)
        {
            opts.H264Crf = o.QualityValue;
            opts.H265Crf = o.QualityValue;
        }
    }

    private static EncodingOptions CloneEncodingOptions(EncodingOptions source)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(source);
        return JsonSerializer.Deserialize<EncodingOptions>(json)!;
    }

    // ----------------------------------------------------------------------------------------------
    // ITranscodeManager — StartFfMpeg (the seam: prep locally, run remotely)
    // ----------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<TranscodingJob> StartFfMpeg(
        StreamState state,
        string outputPath,
        string commandLineArguments,
        Guid userId,
        TranscodingJobType transcodingJobType,
        CancellationTokenSource cancellationTokenSource,
        string? workingDirectory = null)
    {
        if (transcodingJobType != TranscodingJobType.Hls)
        {
            throw new NotSupportedException("Distributed transcoding Phase 0 supports HLS jobs only.");
        }

        var directory = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath));
        Directory.CreateDirectory(directory);

        await AcquireResources(state, cancellationTokenSource).ConfigureAwait(false);

        if (state.VideoRequest is not null && !EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
        {
            var user = userId.IsEmpty() ? null : _userManager.GetUserById(userId);
            if (user is not null && !user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding))
            {
                OnTranscodeFailedToStart(outputPath, transcodingJobType, state);
                throw new ArgumentException("User does not have access to video transcoding.");
            }
        }

        ArgumentException.ThrowIfNullOrEmpty(_mediaEncoder.EncoderPath);

        // Burned-in subtitles may need fonts/attachments extracted from the source (still done server-side).
        if (state.SubtitleStream is not null && (state.SubtitleDeliveryMethod == SubtitleDeliveryMethod.Encode || state.BaseRequest.AlwaysBurnInSubtitleWhenTranscoding))
        {
            if (state.MediaSource.VideoType == VideoType.Dvd || state.MediaSource.VideoType == VideoType.BluRay)
            {
                var concatPath = Path.Join(_appPaths.CachePath, "concat", state.MediaSource.Id + ".concat");
                await _attachmentExtractor.ExtractAllAttachments(concatPath, state.MediaSource, cancellationTokenSource.Token).ConfigureAwait(false);
            }
            else
            {
                await _attachmentExtractor.ExtractAllAttachments(state.MediaPath, state.MediaSource, cancellationTokenSource.Token).ConfigureAwait(false);
            }

            if (state.SubtitleStream.IsExternal && Path.GetExtension(state.SubtitleStream.Path.AsSpan()).Equals(".mks", StringComparison.OrdinalIgnoreCase))
            {
                await _attachmentExtractor.ExtractAllAttachments(state.SubtitleStream.Path, state.MediaSource, cancellationTokenSource.Token).ConfigureAwait(false);
            }
        }

        // Resolve codec-based routing. Both the source (decode) and target (encode) codecs are known
        // here from the StreamState; the configured routing rules turn them into an ordered worker-type
        // priority. Copy/audio jobs don't re-encode, so they route as GPU-agnostic (cpu-preferred).
        var decodeCodec = state.VideoStream?.Codec;
        var isCopy = EncodingHelper.IsCopyCodec(state.OutputVideoCodec);
        var isVideoEncode = state.VideoStream is not null && !isCopy;
        var encodeCodec = isCopy ? "copy" : state.ActualOutputVideoCodec;
        var requiredAudioEncoder = WorkerTypeArgs.ParseAudioEncoder(commandLineArguments);
        var priority = Config.ResolveWorkerPriority(decodeCodec, encodeCodec);
        var stages = BuildSelectionStages(isVideoEncode, priority);
        _logger.LogInformation(
            "Routing job decode={Decode} encode={Encode} -> priority [{Priority}]",
            decodeCodec ?? "?", encodeCodec ?? "?", string.Join(",", priority));

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var timeout = TimeSpan.FromSeconds(Config.FirstSegmentTimeoutSeconds);
        var maxAttempts = stages.Count + 6;
        int stageIndex = 0;
        var argsByType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        TranscodingJob? transcodingJob = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var stage = stages[stageIndex];
            var predicate = BuildStagePredicate(stage, decodeCodec, encodeCodec, isCopy, requiredAudioEncoder);

            // Copy/audio jobs run the server-emitted command verbatim; encode jobs get per-type args.
            string currentArgs;
            if (!isVideoEncode || stage.AnyWorker || stage.Type is null)
            {
                currentArgs = commandLineArguments;
            }
            else if (!argsByType.TryGetValue(stage.Type, out currentArgs!))
            {
                // Intel/vaapi can reuse the server-emitted command; other types regenerate it.
                var serverArgs = string.Equals(stage.Type, "intel", StringComparison.OrdinalIgnoreCase) ? commandLineArguments : null;
                currentArgs = BuildCommandLineForType(state, outputPath, stage.Type, serverArgs);
                argsByType[stage.Type] = currentArgs;
            }

            // Pick a worker matching the predicate.
            WorkerConnection worker;
            try
            {
                worker = await _registry.GetWorkerAsync(timeout, predicate, excluded, cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (stageIndex < stages.Count - 1)
                {
                    // No worker for this stage — advance to the next priority stage.
                    var next = stages[stageIndex + 1];
                    _logger.LogInformation(
                        "No worker for stage {Stage} ({Type}); trying {Next}",
                        stageIndex, stage.Type ?? "any", next.AnyWorker ? "any" : next.Type);
                    stageIndex++;
                    excluded.Clear();
                    continue;
                }

                if (TryPreemptTrickplayFor(predicate))
                {
                    continue;
                }

                OnTranscodeFailedToStart(outputPath, transcodingJobType, state);
                throw new FfmpegException("No transcoding worker available.");
            }

            // Per-attempt bookkeeping — fresh job id, log stream, handle.
            var jobId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            transcodingJob = OnTranscodeBeginning(
                outputPath,
                state.Request.PlaySessionId,
                state.MediaSource.LiveStreamId,
                jobId,
                transcodingJobType,
                state.Request.DeviceId,
                state,
                cancellationTokenSource);

            _logger.LogInformation(
                "Assigning remote transcode {JobId} to worker {WorkerId} (attempt {Attempt}): {Args}",
                transcodingJob.Id, worker.WorkerId, attempt, currentArgs);

            var logStream = CreateLogStream(state, out var logFilePath);
            await WriteLogHeaderAsync(logStream, state, currentArgs, cancellationTokenSource.Token).ConfigureAwait(false);
            _logger.LogDebug("Remote transcode log: {LogFilePath}", logFilePath);

            var handle = new RemoteJobHandle(transcodingJob.Id!, worker, directory, outputPath, transcodingJob, state, logStream);
            _remoteJobs[transcodingJob.Id!] = handle;

            var assign = new AssignJob
            {
                JobId = transcodingJob.Id,
                EncoderPath = _mediaEncoder.EncoderPath,
                Arguments = currentArgs,
                Type = JobType.Hls,
                OutputDir = directory,
                PathMap = new PathMap()
            };
            assign.OutputGlobs.Add("*.ts");
            assign.OutputGlobs.Add("*.mp4");
            assign.OutputGlobs.Add("*.m3u8");

            worker.TrySend(new ServerFrame { Assign = assign });
            worker.FreeSlots = Math.Max(0, worker.FreeSlots - 1); // optimistic; corrected by heartbeat

            // Wait for the worker to accept (bounded).
            bool accepted;
            try
            {
                using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token);
                acceptCts.CancelAfter(timeout);
                accepted = await handle.AcceptedTcs.Task.WaitAsync(acceptCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
            {
                // The overall operation was cancelled — propagate immediately.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Remote transcode {JobId} accept timed out from worker {WorkerId}", transcodingJob.Id, worker.WorkerId);
                accepted = false;
            }

            if (accepted)
            {
                // Success — break out of the retry loop.
                state.TranscodingJob = transcodingJob;
                break;
            }

            // Clean up the failed attempt.
            _logger.LogInformation("Worker {WorkerId} did not accept job {JobId}; cleaning up attempt {Attempt}", worker.WorkerId, transcodingJob.Id, attempt);
            _remoteJobs.TryRemove(transcodingJob.Id!, out _);
            worker.FreeSlots = Math.Min(worker.MaxConcurrent, worker.FreeSlots + 1);

            lock (_activeTranscodingJobs)
            {
                _activeTranscodingJobs.Remove(transcodingJob);
            }

            logStream.Dispose();

            // Exclude the rejecting worker and retry. Another worker in this stage may still qualify;
            // if none do, the next GetWorkerAsync times out and advances to the next priority stage.
            excluded.Add(worker.WorkerId);

            if (attempt >= maxAttempts)
            {
                OnTranscodeFailedToStart(outputPath, transcodingJobType, state);
                throw new FfmpegException("Worker did not accept the transcoding job.");
            }
        }

        if (transcodingJob is null)
        {
            OnTranscodeFailedToStart(outputPath, transcodingJobType, state);
            throw new FfmpegException("No transcoding worker available.");
        }

        var ffmpegTargetFile = state.WaitForPath ?? outputPath;
        _logger.LogDebug("Waiting for the creation of {0}", ffmpegTargetFile);
        while (!File.Exists(ffmpegTargetFile) && !transcodingJob.HasExited)
        {
            await Task.Delay(100, cancellationTokenSource.Token).ConfigureAwait(false);
        }

        _logger.LogDebug("File {0} created or transcoding has finished", ffmpegTargetFile);

        if (!transcodingJob.HasExited)
        {
            // Throttler is skipped in Phase 0 (it targets a local process / pkey-pause). Segment cleaner
            // still applies — it deletes local files the server now owns.
            StartSegmentCleaner(state, transcodingJob);
        }
        else if (transcodingJob.ExitCode != 0)
        {
            throw new FfmpegException(string.Format(CultureInfo.InvariantCulture, "FFmpeg exited with code {0}", transcodingJob.ExitCode));
        }

        _logger.LogDebug("StartFfMpeg() finished successfully");
        return transcodingJob;
    }

    private Stream CreateLogStream(StreamState state, out string logFilePath)
    {
        var logFilePrefix = "FFmpeg.Transcode-";
        if (state.VideoRequest is not null && EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
        {
            logFilePrefix = EncodingHelper.IsCopyCodec(state.OutputAudioCodec) ? "FFmpeg.Remux-" : "FFmpeg.DirectStream-";
        }

        if (state.VideoRequest is null && EncodingHelper.IsCopyCodec(state.OutputAudioCodec))
        {
            logFilePrefix = "FFmpeg.Remux-";
        }

        logFilePath = Path.Combine(
            _serverConfigurationManager.ApplicationPaths.LogDirectoryPath,
            $"{logFilePrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{state.Request.MediaSourceId}_{Guid.NewGuid().ToString()[..8]}.log");

        return new FileStream(
            logFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            IODefaults.FileStreamBufferSize,
            FileOptions.Asynchronous);
    }

    private static async Task WriteLogHeaderAsync(Stream logStream, StreamState state, string arguments, CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(logStream, state.MediaSource, cancellationToken: cancellationToken).ConfigureAwait(false);
        var header = Encoding.UTF8.GetBytes(Environment.NewLine + Environment.NewLine + "[remote] ffmpeg " + arguments + Environment.NewLine + Environment.NewLine);
        await logStream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await logStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StartSegmentCleaner(StreamState state, TranscodingJob transcodingJob)
    {
        if (EnableSegmentCleaning(state))
        {
            transcodingJob.TranscodingSegmentCleaner = new TranscodingSegmentCleaner(
                transcodingJob,
                _loggerFactory.CreateLogger<TranscodingSegmentCleaner>(),
                _serverConfigurationManager,
                _fileSystem,
                _mediaEncoder,
                state.SegmentLength);
            transcodingJob.TranscodingSegmentCleaner.Start();
        }
    }

    private static bool EnableSegmentCleaning(StreamState state)
        => state.InputProtocol is MediaProtocol.File or MediaProtocol.Http
           && state.IsInputVideo
           && state.TranscodingType == TranscodingJobType.Hls
           && state.RunTimeTicks.HasValue
           && state.RunTimeTicks.Value >= TimeSpan.FromMinutes(5).Ticks;

    private TranscodingJob OnTranscodeBeginning(
        string path,
        string? playSessionId,
        string? liveStreamId,
        string transcodingJobId,
        TranscodingJobType type,
        string? deviceId,
        StreamState state,
        CancellationTokenSource cancellationTokenSource)
    {
        lock (_activeTranscodingJobs)
        {
            var job = new TranscodingJob(_loggerFactory.CreateLogger<TranscodingJob>())
            {
                Type = type,
                Path = path,
                Process = null, // remote job — no local process
                ActiveRequestCount = 1,
                DeviceId = deviceId,
                CancellationTokenSource = cancellationTokenSource,
                Id = transcodingJobId,
                PlaySessionId = playSessionId,
                LiveStreamId = liveStreamId,
                MediaSource = state.MediaSource
            };

            _activeTranscodingJobs.Add(job);
            ReportTranscodingProgress(job, state, null, null, null, null, null);
            return job;
        }
    }

    /// <inheritdoc />
    public void OnTranscodeEndRequest(TranscodingJob job)
    {
        job.ActiveRequestCount--;
        _logger.LogDebug("OnTranscodeEndRequest job.ActiveRequestCount={ActiveRequestCount}", job.ActiveRequestCount);
        if (job.ActiveRequestCount <= 0)
        {
            PingTimer(job, false);
        }
    }

    private void OnTranscodeFailedToStart(string path, TranscodingJobType type, StreamState state)
    {
        lock (_activeTranscodingJobs)
        {
            var job = _activeTranscodingJobs.FirstOrDefault(j => j.Type == type && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
            if (job is not null)
            {
                _activeTranscodingJobs.Remove(job);
            }
        }

        if (!string.IsNullOrWhiteSpace(state.Request.DeviceId))
        {
            _sessionManager.ClearTranscodingInfo(state.Request.DeviceId);
        }
    }

    private async Task AcquireResources(StreamState state, CancellationTokenSource cancellationTokenSource)
    {
        if (state.MediaSource.RequiresOpening && string.IsNullOrWhiteSpace(state.Request.LiveStreamId))
        {
            var liveStreamResponse = await _mediaSourceManager.OpenLiveStream(
                    new LiveStreamRequest { OpenToken = state.MediaSource.OpenToken },
                    cancellationTokenSource.Token)
                .ConfigureAwait(false);
            var encodingOptions = _serverConfigurationManager.GetEncodingOptions();

            _encodingHelper.AttachMediaSourceInfo(state, encodingOptions, liveStreamResponse.MediaSource, state.RequestedUrl);

            if (state.VideoRequest is not null)
            {
                _encodingHelper.TryStreamCopy(state, encodingOptions);
            }
        }

        if (state.MediaSource.BufferMs.HasValue)
        {
            await Task.Delay(state.MediaSource.BufferMs.Value, cancellationTokenSource.Token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type)
    {
        lock (_activeTranscodingJobs)
        {
            var job = _activeTranscodingJobs.FirstOrDefault(j => j.Type == type && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
            if (job is null)
            {
                return null;
            }

            job.ActiveRequestCount++;
            if (string.IsNullOrWhiteSpace(job.PlaySessionId) || job.Type == TranscodingJobType.Progressive)
            {
                job.StopKillTimer();
            }

            return job;
        }
    }

    private void OnPlaybackProgress(object? sender, MediaBrowser.Controller.Library.PlaybackProgressEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PlaySessionId))
        {
            PingTranscodingJob(e.PlaySessionId, e.IsPaused);
        }
    }

    private void DeleteEncodedMediaCache()
    {
        var path = _serverConfigurationManager.GetTranscodePath();
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in _fileSystem.GetFilePaths(path, true))
        {
            try
            {
                _fileSystem.DeleteFile(file);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting encoded media cache file {Path}", path);
            }
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken)
    {
        var gate = _transcodingLocks.GetOrAdd(outputPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private bool _released;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Attempts to preempt (cancel) a running trickplay extraction job on a worker that satisfies
    /// <paramref name="predicate"/>, freeing a slot for a streaming transcode. Picks the victim with the
    /// lowest <see cref="BatchJobHandle.PercentComplete"/> to minimise wasted work.
    /// </summary>
    /// <param name="predicate">Worker capability filter from the current selection stage.</param>
    /// <returns><c>true</c> if a trickplay job was successfully preempted; <c>false</c> otherwise.</returns>
    private bool TryPreemptTrickplayFor(Func<WorkerConnection, bool> predicate)
    {
        if (!Config.StreamingPreemptsTrickplay)
        {
            return false;
        }

        BatchJobHandle? victim = null;
        foreach (var kvp in _batchJobs)
        {
            var handle = kvp.Value;
            if (handle.Worker is null || !predicate(handle.Worker))
            {
                continue;
            }

            if (handle.Worker.FreeSlots != 0)
            {
                continue;
            }

            if (victim is null || handle.PercentComplete < victim.PercentComplete)
            {
                victim = handle;
            }
        }

        if (victim is null)
        {
            return false;
        }

        victim.Preempted = true;
        victim.Worker!.TrySend(new ServerFrame
        {
            Control = new JobControl { JobId = victim.JobId, Action = JobControl.Types.Action.Stop }
        });
        victim.Worker.FreeSlots++;
        _logger.LogInformation("Preempting trickplay job {JobId} on worker {WorkerId} for streaming", victim.JobId, victim.Worker.WorkerId);
        return true;
    }

    /// <summary>
    /// Runs a one-shot batch ffmpeg job (e.g. trickplay frame extraction) on a remote worker and waits for
    /// it to finish. The worker runs <paramref name="arguments"/> writing to its local scratch dir and
    /// streams every produced file back to <paramref name="outputDir"/> on the server; this method returns
    /// once the worker reports the process exited. Unlike <see cref="StartFfMpeg"/> it awaits full completion
    /// rather than the first segment, and it has no playback session / <c>StreamState</c>.
    /// </summary>
    /// <param name="arguments">The ffmpeg argument string (output paths must reference <paramref name="outputDir"/>).</param>
    /// <param name="outputDir">Server-side directory the produced files are reassembled into.</param>
    /// <param name="requiredEncoder">The video encoder the args use (e.g. "mjpeg", "mjpeg_vaapi"); used for worker capability routing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if a worker ran the job to a zero exit code; <c>false</c> if no worker was available (caller should fall back to local).</returns>
    /// <exception cref="FfmpegException">A worker ran the job but ffmpeg exited non-zero.</exception>
    public async Task<bool> ExtractImagesRemoteAsync(string arguments, string outputDir, string requiredEncoder, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Config.FirstSegmentTimeoutSeconds);

        // Software mjpeg is available on any ffmpeg worker; a hardware encoder needs a worker that advertises it.
        var isSoftware = string.Equals(requiredEncoder, "mjpeg", StringComparison.OrdinalIgnoreCase);
        Func<WorkerConnection, bool> predicate = isSoftware
            ? (_ => true)
            : (w => w.Encoders.Any(e => string.Equals(e, requiredEncoder, StringComparison.OrdinalIgnoreCase)));

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        const int MaxAttempts = 8;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            WorkerConnection worker;
            try
            {
                worker = await _registry.GetWorkerAsync(timeout, predicate, excluded, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // No capable worker — let the caller fall back to local extraction.
                return false;
            }

            var jobId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var handle = new BatchJobHandle(jobId, worker, outputDir);
            _batchJobs[jobId] = handle;

            var assign = new AssignJob
            {
                JobId = jobId,
                EncoderPath = _mediaEncoder.EncoderPath,
                Arguments = arguments,
                Type = JobType.Trickplay,
                OutputDir = outputDir,
                PathMap = new PathMap()
            };
            assign.OutputGlobs.Add("*.jpg");

            _logger.LogInformation(
                "Assigning remote batch job {JobId} to worker {WorkerId} (attempt {Attempt}, encoder {Encoder})",
                jobId, worker.WorkerId, attempt, requiredEncoder);

            worker.TrySend(new ServerFrame { Assign = assign });
            worker.FreeSlots = Math.Max(0, worker.FreeSlots - 1); // optimistic; corrected by heartbeat

            bool accepted;
            try
            {
                using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                acceptCts.CancelAfter(timeout);
                accepted = await handle.AcceptedTcs.Task.WaitAsync(acceptCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _batchJobs.TryRemove(jobId, out _);
                handle.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Remote batch job {JobId} accept timed out from worker {WorkerId}", jobId, worker.WorkerId);
                accepted = false;
            }

            if (!accepted)
            {
                _batchJobs.TryRemove(jobId, out _);
                handle.Dispose();
                worker.FreeSlots = Math.Min(worker.MaxConcurrent, worker.FreeSlots + 1);
                excluded.Add(worker.WorkerId);
                continue;
            }

            // Accepted — wait for the worker to finish extracting every frame. JobExited is the last frame
            // on the ordered stream, so by the time ExitedTcs completes all files are on disk in outputDir.
            int exitCode;
            try
            {
                exitCode = await handle.ExitedTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _batchJobs.TryRemove(jobId, out _);
                handle.Dispose();
            }

            if (exitCode != 0)
            {
                if (handle.Preempted)
                {
                    // The job was killed to free a slot for streaming — clean partial output and retry.
                    _logger.LogInformation("Batch job {JobId} was preempted; retrying on mesh", jobId);
                    try
                    {
                        Directory.Delete(outputDir, true);
                        Directory.CreateDirectory(outputDir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clean output dir after preemption for job {JobId}", jobId);
                    }

                    continue;
                }

                throw new FfmpegException(string.Format(CultureInfo.InvariantCulture, "Remote batch job {0} failed with exit code {1}.", jobId, exitCode));
            }

            return true;
        }

        // Every attempt was rejected.
        return false;
    }

    // ----------------------------------------------------------------------------------------------
    // IRemoteFrameSink — worker -> server frames
    // ----------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public void OnJobAccepted(string jobId, bool accepted, string? reason)
    {
        if (_remoteJobs.TryGetValue(jobId, out var handle))
        {
            handle.AcceptedTcs.TrySetResult(accepted);
        }
        else if (_batchJobs.TryGetValue(jobId, out var batch))
        {
            batch.AcceptedTcs.TrySetResult(accepted);
        }
        else
        {
            // Handle was already cleaned up (previous attempt in the retry loop).
            return;
        }

        if (!accepted)
        {
            _logger.LogWarning("Worker rejected job {JobId}: {Reason}", jobId, reason);
        }
    }

    /// <inheritdoc />
    public void OnSegmentData(string jobId, string relPath, ReadOnlyMemory<byte> chunk, bool eof)
    {
        if (_remoteJobs.TryGetValue(jobId, out var handle))
        {
            try
            {
                handle.WriteSegment(relPath, chunk, eof);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Error writing segment {RelPath} for job {JobId}", relPath, jobId);
            }
        }
        else if (_batchJobs.TryGetValue(jobId, out var batch))
        {
            try
            {
                batch.WriteSegment(relPath, chunk, eof);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Error writing batch output {RelPath} for job {JobId}", relPath, jobId);
            }
        }
    }

    /// <inheritdoc />
    public void OnProgress(string jobId, Progress progress)
    {
        if (_remoteJobs.TryGetValue(jobId, out var handle))
        {
            ReportTranscodingProgress(
                handle.Job,
                handle.State,
                progress.PositionTicks > 0 ? TimeSpan.FromTicks((long)progress.PositionTicks) : null,
                progress.Framerate > 0 ? progress.Framerate : null,
                progress.PercentComplete > 0 ? progress.PercentComplete : null,
                progress.BytesTranscoded > 0 ? progress.BytesTranscoded : null,
                progress.Bitrate > 0 ? (int)(progress.Bitrate * 1000) : null);

            handle.Speed = progress.Speed;
        }
        else if (_batchJobs.TryGetValue(jobId, out var batch))
        {
            batch.Framerate = progress.Framerate;
            batch.PercentComplete = progress.PercentComplete;
            batch.Speed = progress.Speed;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ActiveJobDto> SnapshotActiveJobs()
    {
        var result = new List<ActiveJobDto>();

        foreach (var kvp in _remoteJobs)
        {
            var handle = kvp.Value;
            var job = handle.Job;
            if (job is null)
            {
                continue;
            }

            result.Add(new ActiveJobDto
            {
                JobId = kvp.Key,
                WorkerId = handle.Worker?.WorkerId ?? string.Empty,
                Kind = "stream",
                Framerate = job.Framerate ?? 0,
                BitrateKbps = (job.BitRate ?? 0) / 1000f,
                BytesTranscoded = job.BytesTranscoded ?? 0,
                PercentComplete = job.CompletionPercentage ?? 0,
                Speed = handle.Speed,
            });
        }

        foreach (var kvp in _batchJobs)
        {
            var batch = kvp.Value;
            result.Add(new ActiveJobDto
            {
                JobId = kvp.Key,
                WorkerId = batch.Worker?.WorkerId ?? string.Empty,
                Kind = "trickplay",
                Framerate = batch.Framerate,
                PercentComplete = batch.PercentComplete,
                Speed = batch.Speed,
            });
        }

        return result;
    }

    /// <inheritdoc />
    public TrickplayStats GetTrickplayStats() => new()
    {
        Attempts = Interlocked.Read(ref _trickplayAttempts),
        RemoteOk = Interlocked.Read(ref _trickplayRemoteOk),
        RemoteFailed = Interlocked.Read(ref _trickplayRemoteFailed),
        LocalFallback = Interlocked.Read(ref _trickplayLocalFallback),
    };

    public void IncrementTrickplayAttempt() => Interlocked.Increment(ref _trickplayAttempts);

    public void IncrementTrickplayRemoteOk() => Interlocked.Increment(ref _trickplayRemoteOk);

    public void IncrementTrickplayRemoteFailed() => Interlocked.Increment(ref _trickplayRemoteFailed);

    public void IncrementTrickplayLocalFallback() => Interlocked.Increment(ref _trickplayLocalFallback);

    /// <inheritdoc />
    public void OnLog(string jobId, string line)
    {
        if (_remoteJobs.TryGetValue(jobId, out var handle))
        {
            handle.AppendLog(line);
        }
    }

    /// <inheritdoc />
    public void OnJobExited(string jobId, int exitCode)
    {
        if (!_remoteJobs.TryRemove(jobId, out var handle))
        {
            if (_batchJobs.TryGetValue(jobId, out var batch))
            {
                // One-shot batch job (e.g. trickplay). Leave it in the registry so any trailing
                // SegmentData already delivered on the ordered stream is written; ExtractImagesRemoteAsync
                // removes and disposes it once ExitedTcs completes.
                batch.AcceptedTcs.TrySetResult(false); // no-op if already accepted
                batch.ExitedTcs.TrySetResult(exitCode);

                if (exitCode == 0)
                {
                    _logger.LogInformation("Remote batch job {JobId} exited with code 0", jobId);
                }
                else
                {
                    _logger.LogError("Remote batch job {JobId} exited with code {ExitCode}", jobId, exitCode);
                }
            }

            return;
        }

        handle.Job.HasExited = true;
        handle.Job.ExitCode = exitCode;
        handle.AcceptedTcs.TrySetResult(false); // unblock StartFfMpeg if it died pre-accept

        if (exitCode == 0)
        {
            _logger.LogInformation("Remote ffmpeg job {JobId} exited with code 0", jobId);
        }
        else
        {
            _logger.LogError("Remote ffmpeg job {JobId} exited with code {ExitCode}", jobId, exitCode);
        }

        try
        {
            ReportTranscodingProgress(handle.Job, handle.State, null, null, null, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting final progress for job {JobId}", jobId);
        }

        handle.State.Dispose();
        handle.Dispose();
        handle.Job.Dispose();
    }

    /// <inheritdoc />
    public void OnWorkerLost(WorkerConnection worker)
    {
        foreach (var jobId in _remoteJobs.Where(kv => ReferenceEquals(kv.Value.Worker, worker)).Select(kv => kv.Key).ToArray())
        {
            _logger.LogWarning("Worker {WorkerId} lost; failing job {JobId}", worker.WorkerId, jobId);
            OnJobExited(jobId, -1);
        }

        foreach (var jobId in _batchJobs.Where(kv => ReferenceEquals(kv.Value.Worker, worker)).Select(kv => kv.Key).ToArray())
        {
            _logger.LogWarning("Worker {WorkerId} lost; failing batch job {JobId}", worker.WorkerId, jobId);
            OnJobExited(jobId, -1);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStart -= OnPlaybackProgress;

        foreach (var handle in _remoteJobs.Values)
        {
            handle.Dispose();
        }

        _remoteJobs.Clear();

        foreach (var gate in _transcodingLocks.Values)
        {
            gate.Dispose();
        }

        _transcodingLocks.Clear();
    }
}
