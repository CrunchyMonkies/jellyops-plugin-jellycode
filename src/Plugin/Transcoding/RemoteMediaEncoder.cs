#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedTranscoding.Transcoding;

/// <summary>
/// Decorates the core <see cref="IMediaEncoder"/>, forwarding every member to the wrapped instance except
/// <see cref="ExtractVideoImagesOnIntervalAccelerated"/> (used only by trickplay), which it offloads to a
/// remote transcode worker via <see cref="RemoteTranscodeManager.ExtractImagesRemoteAsync"/>. If offload is
/// disabled, no worker is available, or the remote job fails, it falls back to local extraction so trickplay
/// never regresses.
///
/// The argument construction below is reproduced from
/// <c>MediaBrowser.MediaEncoding.Encoder.MediaEncoder.ExtractVideoImagesOnIntervalAccelerated</c> /
/// <c>ExtractVideoImagesOnIntervalInternal</c> (MediaEncoder.cs:832) because those are private and we do not
/// fork jellyfin-src. Re-sync this method if the upstream ffmpeg trickplay args change.
/// </summary>
public sealed class RemoteMediaEncoder : IMediaEncoder
{
    private readonly IMediaEncoder _inner;
    private readonly Lazy<RemoteTranscodeManager> _transcodeManager;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ILogger<RemoteMediaEncoder> _logger;

    public RemoteMediaEncoder(
        IMediaEncoder inner,
        Lazy<RemoteTranscodeManager> transcodeManager,
        IServerConfigurationManager serverConfig,
        ILogger<RemoteMediaEncoder> logger)
    {
        _inner = inner;
        _transcodeManager = transcodeManager;
        _serverConfig = serverConfig;
        _logger = logger;
    }

    // ----------------------------------------------------------------------------------------------
    // The one overridden member — offload trickplay frame extraction to the mesh.
    // ----------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<string> ExtractVideoImagesOnIntervalAccelerated(
        string inputFile,
        string container,
        MediaSourceInfo mediaSource,
        MediaStream imageStream,
        int maxWidth,
        TimeSpan interval,
        bool allowHwAccel,
        bool enableHwEncoding,
        int? threads,
        int? qualityScale,
        ProcessPriorityClass? priority,
        bool enableKeyFrameOnlyExtraction,
        EncodingHelper encodingHelper,
        CancellationToken cancellationToken)
    {
        var offload = Plugin.Instance?.Configuration?.OffloadTrickplay ?? true;
        if (!offload)
        {
            return await Local().ConfigureAwait(false);
        }

        var mgr = _transcodeManager.Value;
        mgr.IncrementTrickplayAttempt();

        string targetDirectory = null;
        string args;
        string vidEncoder;
        try
        {
            (args, targetDirectory, vidEncoder) = BuildImageExtractionCommand(
                inputFile, container, mediaSource, imageStream, maxWidth, interval,
                allowHwAccel, enableHwEncoding, threads, qualityScale, enableKeyFrameOnlyExtraction, encodingHelper);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build remote trickplay command for {Input}; extracting locally.", inputFile);
            TryCleanupDirectory(targetDirectory);
            mgr.IncrementTrickplayRemoteFailed();
            mgr.IncrementTrickplayLocalFallback();
            return await Local().ConfigureAwait(false);
        }

        try
        {
            var ran = await mgr
                .ExtractImagesRemoteAsync(args, targetDirectory, vidEncoder, cancellationToken)
                .ConfigureAwait(false);

            if (ran)
            {
                if (!Directory.Exists(targetDirectory) || !Directory.EnumerateFiles(targetDirectory, "*.jpg").Any())
                {
                    throw new FfmpegException("Remote trickplay extraction produced no images.");
                }

                mgr.IncrementTrickplayRemoteOk();
                return targetDirectory;
            }

            _logger.LogInformation("No remote worker available for trickplay ({Input}); extracting locally.", inputFile);
            mgr.IncrementTrickplayLocalFallback();
        }
        catch (OperationCanceledException)
        {
            TryCleanupDirectory(targetDirectory);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote trickplay extraction failed for {Input}; extracting locally.", inputFile);
            mgr.IncrementTrickplayRemoteFailed();
            mgr.IncrementTrickplayLocalFallback();
        }

        TryCleanupDirectory(targetDirectory);
        return await Local().ConfigureAwait(false);

        Task<string> Local() => _inner.ExtractVideoImagesOnIntervalAccelerated(
            inputFile, container, mediaSource, imageStream, maxWidth, interval, allowHwAccel, enableHwEncoding,
            threads, qualityScale, priority, enableKeyFrameOnlyExtraction, encodingHelper, cancellationToken);
    }

    /// <summary>
    /// Reproduces the ffmpeg command core builds for interval image extraction, but returns it as a string
    /// (plus the server-side output dir and chosen encoder) instead of launching a local process.
    /// </summary>
    private (string Args, string TargetDirectory, string VidEncoder) BuildImageExtractionCommand(
        string inputFile,
        string container,
        MediaSourceInfo mediaSource,
        MediaStream imageStream,
        int maxWidth,
        TimeSpan interval,
        bool allowHwAccel,
        bool enableHwEncoding,
        int? threads,
        int? qualityScale,
        bool enableKeyFrameOnlyExtraction,
        EncodingHelper encodingHelper)
    {
        var options = allowHwAccel ? _serverConfig.GetEncodingOptions() : new EncodingOptions();
        threads ??= EncodingHelper.GetNumberOfThreads(null, options, null);

        if (allowHwAccel && enableKeyFrameOnlyExtraction)
        {
            var hardwareAccelerationType = options.HardwareAccelerationType;
            var supportsKeyFrameOnly = (hardwareAccelerationType == HardwareAccelerationType.nvenc && options.EnableEnhancedNvdecDecoder)
                                       || (hardwareAccelerationType == HardwareAccelerationType.amf && OperatingSystem.IsWindows())
                                       || (hardwareAccelerationType == HardwareAccelerationType.qsv && options.PreferSystemNativeHwDecoder)
                                       || hardwareAccelerationType == HardwareAccelerationType.vaapi
                                       || hardwareAccelerationType == HardwareAccelerationType.videotoolbox
                                       || hardwareAccelerationType == HardwareAccelerationType.rkmpp;
            if (!supportsKeyFrameOnly)
            {
                // A new EncodingOptions instance must be used to not disable HW acceleration for all of Jellyfin.
                allowHwAccel = false;
                options = new EncodingOptions();
            }
        }

        if (!allowHwAccel)
        {
            options.EnableHardwareEncoding = false;
            options.HardwareAccelerationType = HardwareAccelerationType.none;
            options.EnableTonemapping = false;
        }

        if (imageStream.Width is not null && imageStream.Height is not null && !string.IsNullOrEmpty(imageStream.AspectRatio))
        {
            // For hardware trickplay encoders, re-calculate the size because they use fixed scale dimensions.
            var darParts = imageStream.AspectRatio.Split(':');
            var (wa, ha) = (double.Parse(darParts[0], CultureInfo.InvariantCulture), double.Parse(darParts[1], CultureInfo.InvariantCulture));
            var shouldResetHeight = Math.Abs((imageStream.Width.Value * ha) - (imageStream.Height.Value * wa)) > .05;
            if (shouldResetHeight)
            {
                imageStream.Height = Convert.ToInt32(imageStream.Width.Value * ha / wa);
            }
        }

        var baseRequest = new BaseEncodingJobOptions { MaxWidth = maxWidth, MaxFramerate = (float)(1.0 / interval.TotalSeconds) };
        var jobState = new EncodingJobInfo(TranscodingJobType.Progressive)
        {
            IsVideoRequest = true, // must be true for InputVideoHwaccelArgs to return non-empty value
            MediaSource = mediaSource,
            VideoStream = imageStream,
            BaseRequest = baseRequest, // GetVideoProcessingFilterParam errors if null
            MediaPath = inputFile,
            OutputVideoCodec = "mjpeg"
        };
        var vidEncoder = enableHwEncoding ? encodingHelper.GetVideoEncoder(jobState, options) : jobState.OutputVideoCodec;

        // Get input and filter arguments
        var inputArg = encodingHelper.GetInputArgument(jobState, options, container).Trim();
        if (string.IsNullOrWhiteSpace(inputArg))
        {
            throw new InvalidOperationException("EncodingHelper returned empty input arguments.");
        }

        if (!allowHwAccel)
        {
            inputArg = "-threads " + threads.GetValueOrDefault() + " " + inputArg; // HW accel sets thread count elsewhere
        }

        // Note: core also prepends videotoolbox "-hwaccel_flags +low_priority" when supported; workers are
        // Linux (no videotoolbox), so that branch is intentionally omitted here.

        var filterParam = encodingHelper.GetVideoProcessingFilterParam(jobState, options, vidEncoder).Trim();
        if (string.IsNullOrWhiteSpace(filterParam))
        {
            throw new InvalidOperationException("EncodingHelper returned empty or invalid filter parameters.");
        }

        if (enableKeyFrameOnlyExtraction)
        {
            inputArg = "-skip_frame nokey " + inputArg;
        }

        // ffmpeg qscale is a value from 1-31, with 1 being best quality and 31 being worst.
        var encoderQuality = Math.Clamp(qualityScale ?? 4, 1, 31);
        var encoderQualityOption = "-qscale:v ";

        if (vidEncoder.Contains("vaapi", StringComparison.InvariantCultureIgnoreCase)
            || vidEncoder.Contains("qsv", StringComparison.InvariantCultureIgnoreCase))
        {
            encoderQuality = 100 - ((encoderQuality - 1) * (100 / 30));
            encoderQualityOption = "-global_quality:v ";
        }

        if (vidEncoder.Contains("videotoolbox", StringComparison.InvariantCultureIgnoreCase))
        {
            // videotoolbox's mjpeg encoder uses jpeg quality scaled to QP2LAMBDA (118).
            encoderQuality = 118 - ((encoderQuality - 1) * (118 / 30));
        }

        if (vidEncoder.Contains("rkmpp", StringComparison.InvariantCultureIgnoreCase))
        {
            encoderQuality = 99 - ((encoderQuality - 1) * (99 / 30));
            encoderQualityOption = "-qp_init:v ";
        }

        // Output arguments
        var targetDirectory = Path.Combine(_serverConfig.ApplicationPaths.TempDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetDirectory);
        var outputPath = Path.Combine(targetDirectory, "%08d.jpg");

        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-loglevel error {0} -an -sn {1} -threads {2} -c:v {3} {4}{5}{6}-f {7} \"{8}\"",
            inputArg,
            filterParam,
            threads.GetValueOrDefault(),
            vidEncoder,
            encoderQualityOption + encoderQuality + " ",
            vidEncoder.Contains("videotoolbox", StringComparison.InvariantCultureIgnoreCase) ? "-allow_sw 1 " : string.Empty,
            EncodingHelper.GetVideoSyncOption("0", _inner.EncoderVersion).Trim() + " ", // passthrough timestamp
            "image2",
            outputPath);

        return (args, targetDirectory, vidEncoder);
    }

    private void TryCleanupDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to clean up remote trickplay temp directory {Directory}", directory);
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Everything else forwards to the wrapped encoder.
    // ----------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public string EncoderPath => _inner.EncoderPath;

    /// <inheritdoc />
    public string ProbePath => _inner.ProbePath;

    /// <inheritdoc />
    public Version EncoderVersion => _inner.EncoderVersion;

    /// <inheritdoc />
    public bool IsPkeyPauseSupported => _inner.IsPkeyPauseSupported;

    /// <inheritdoc />
    public bool IsVaapiDeviceAmd => _inner.IsVaapiDeviceAmd;

    /// <inheritdoc />
    public bool IsVaapiDeviceInteliHD => _inner.IsVaapiDeviceInteliHD;

    /// <inheritdoc />
    public bool IsVaapiDeviceInteli965 => _inner.IsVaapiDeviceInteli965;

    /// <inheritdoc />
    public bool IsVaapiDeviceSupportVulkanDrmModifier => _inner.IsVaapiDeviceSupportVulkanDrmModifier;

    /// <inheritdoc />
    public bool IsVaapiDeviceSupportVulkanDrmInterop => _inner.IsVaapiDeviceSupportVulkanDrmInterop;

    /// <inheritdoc />
    public bool IsVideoToolboxAv1DecodeAvailable => _inner.IsVideoToolboxAv1DecodeAvailable;

    /// <inheritdoc />
    public bool SupportsEncoder(string encoder) => _inner.SupportsEncoder(encoder);

    /// <inheritdoc />
    public bool SupportsDecoder(string decoder) => _inner.SupportsDecoder(decoder);

    /// <inheritdoc />
    public bool SupportsHwaccel(string hwaccel) => _inner.SupportsHwaccel(hwaccel);

    /// <inheritdoc />
    public bool SupportsFilter(string filter) => _inner.SupportsFilter(filter);

    /// <inheritdoc />
    public bool SupportsFilterWithOption(FilterOptionType option) => _inner.SupportsFilterWithOption(option);

    /// <inheritdoc />
    public bool SupportsBitStreamFilterWithOption(BitStreamFilterOptionType option) => _inner.SupportsBitStreamFilterWithOption(option);

    /// <inheritdoc />
    public Task<string> ExtractAudioImage(string path, int? imageStreamIndex, CancellationToken cancellationToken)
        => _inner.ExtractAudioImage(path, imageStreamIndex, cancellationToken);

    /// <inheritdoc />
    public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream videoStream, Video3DFormat? threedFormat, TimeSpan? offset, CancellationToken cancellationToken)
        => _inner.ExtractVideoImage(inputFile, container, mediaSource, videoStream, threedFormat, offset, cancellationToken);

    /// <inheritdoc />
    public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int? imageStreamIndex, ImageFormat? targetFormat, CancellationToken cancellationToken)
        => _inner.ExtractVideoImage(inputFile, container, mediaSource, imageStream, imageStreamIndex, targetFormat, cancellationToken);

    /// <inheritdoc />
    public Task<MediaInfo> GetMediaInfo(MediaInfoRequest request, CancellationToken cancellationToken)
        => _inner.GetMediaInfo(request, cancellationToken);

    /// <inheritdoc />
    public string GetInputArgument(string inputFile, MediaSourceInfo mediaSource)
        => _inner.GetInputArgument(inputFile, mediaSource);

    /// <inheritdoc />
    public string GetInputArgument(IReadOnlyList<string> inputFiles, MediaSourceInfo mediaSource)
        => _inner.GetInputArgument(inputFiles, mediaSource);

    /// <inheritdoc />
    public string GetExternalSubtitleInputArgument(string inputFile)
        => _inner.GetExternalSubtitleInputArgument(inputFile);

    /// <inheritdoc />
    public string GetTimeParameter(long ticks) => _inner.GetTimeParameter(ticks);

    /// <inheritdoc />
    public Task ConvertImage(string inputPath, string outputPath) => _inner.ConvertImage(inputPath, outputPath);

    /// <inheritdoc />
    public string EscapeSubtitleFilterPath(string path) => _inner.EscapeSubtitleFilterPath(path);

    /// <inheritdoc />
    public bool SetFFmpegPath() => _inner.SetFFmpegPath();

    /// <inheritdoc />
    public IReadOnlyList<string> GetPrimaryPlaylistVobFiles(string path, uint? titleNumber)
        => _inner.GetPrimaryPlaylistVobFiles(path, titleNumber);

    /// <inheritdoc />
    public IReadOnlyList<string> GetPrimaryPlaylistM2tsFiles(string path)
        => _inner.GetPrimaryPlaylistM2tsFiles(path);

    /// <inheritdoc />
    public string GetInputPathArgument(EncodingJobInfo state) => _inner.GetInputPathArgument(state);

    /// <inheritdoc />
    public string GetInputPathArgument(string path, MediaSourceInfo mediaSource)
        => _inner.GetInputPathArgument(path, mediaSource);

    /// <inheritdoc />
    public void GenerateConcatConfig(MediaSourceInfo source, string concatFilePath)
        => _inner.GenerateConcatConfig(source, concatFilePath);

    /// <inheritdoc />
    public bool CanEncodeToAudioCodec(string codec) => _inner.CanEncodeToAudioCodec(codec);

    /// <inheritdoc />
    public bool CanEncodeToSubtitleCodec(string codec) => _inner.CanEncodeToSubtitleCodec(codec);

    /// <inheritdoc />
    public bool CanExtractSubtitles(string codec) => _inner.CanExtractSubtitles(codec);
}
