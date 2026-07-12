using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DistributedTranscoding.Worker;

/// <summary>
/// Probes the worker's ffmpeg binary for its version and the set of video encoders / hardware
/// accelerators it actually supports, so the worker advertises real capabilities in its Register
/// frame instead of relying solely on the DT_HWACCELS/DT_CLASS environment variables. The plugin's
/// settings UI uses this to show only the transcoding options a given worker type can satisfy.
/// </summary>
public sealed record FfmpegCapabilities(
    string Version,
    IReadOnlyList<string> HwAccels,
    IReadOnlyList<string> Encoders,
    IReadOnlyList<string> Decoders)
{
    /// <summary>The video encoders we care about advertising, by name as ffmpeg lists them.</summary>
    private static readonly string[] KnownVideoEncoders =
    {
        // software
        "libx264", "libx265", "libsvtav1", "libaom-av1", "libvpx-vp9", "mpeg4",
        // nvidia nvenc
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        // intel quicksync
        "h264_qsv", "hevc_qsv", "av1_qsv", "vp9_qsv",
        // vaapi (intel / amd)
        "h264_vaapi", "hevc_vaapi", "av1_vaapi", "vp9_vaapi",
        // apple
        "h264_videotoolbox", "hevc_videotoolbox",
    };

    /// <summary>The video decoders we care about advertising. NVIDIA hardware decode uses the
    /// <c>*_cuvid</c> family (not nvenc); Intel/AMD use <c>*_qsv</c> / <c>*_vaapi</c>; software
    /// decoders are the bare codec names.</summary>
    private static readonly string[] KnownVideoDecoders =
    {
        // software
        "h264", "hevc", "av1", "vp9", "vp8", "mpeg4", "mpeg2video",
        // nvidia cuvid
        "h264_cuvid", "hevc_cuvid", "av1_cuvid", "vp9_cuvid", "vp8_cuvid", "mpeg4_cuvid", "mpeg2_cuvid",
        // intel quicksync
        "h264_qsv", "hevc_qsv", "av1_qsv", "vp9_qsv", "vp8_qsv", "mpeg2_qsv",
        // vaapi (intel / amd)
        "h264_vaapi", "hevc_vaapi", "av1_vaapi", "vp9_vaapi", "vp8_vaapi", "mpeg2_vaapi",
    };

    private static readonly Regex EncodersLineRegex =
        new(@"^\s*V[\.A-Z]{5,}\s+(\S+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    // -decoders uses the same "V.....  name" column layout as -encoders.
    private static readonly Regex DecodersLineRegex =
        new(@"^\s*V[\.A-Z]{5,}\s+(\S+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Runs ffmpeg to discover version + supported encoders/decoders/hwaccels. Never throws — on any
    /// failure it returns an "unknown" version with empty capability lists so the worker still starts.
    /// </summary>
    public static FfmpegCapabilities Probe(string ffmpegPath)
    {
        var version = ParseVersion(RunFfmpeg(ffmpegPath, "-hide_banner -version"));
        var encoders = ParseEncoders(RunFfmpeg(ffmpegPath, "-hide_banner -encoders"));
        var decoders = ParseVideoList(RunFfmpeg(ffmpegPath, "-hide_banner -decoders"), DecodersLineRegex, KnownVideoDecoders);
        var hwaccels = DeriveHwAccels(encoders);
        return new FfmpegCapabilities(version, hwaccels, encoders, decoders);
    }

    /// <summary>
    /// Filters a probed encoder list down to what a worker with the given advertised accelerators can
    /// actually use: software encoders are always kept; a hardware encoder (<c>*_nvenc/_vaapi/_qsv</c>)
    /// is kept only when its accelerator is in <paramref name="hwAccels"/>. This matters because
    /// ffmpeg lists every compiled-in encoder regardless of whether the hardware is present.
    /// </summary>
    public static IReadOnlyList<string> FilterForAccels(IReadOnlyList<string> encoders, IReadOnlyList<string> hwAccels)
    {
        var kept = new List<string>();
        foreach (var enc in encoders)
        {
            var lower = enc.ToLowerInvariant();
            var accel = AcceleratorOf(lower);
            if (accel is null || hwAccels.Contains(accel, StringComparer.OrdinalIgnoreCase))
            {
                kept.Add(enc);
            }
        }

        return kept;
    }

    /// <summary>Returns the accelerator an encoder/decoder needs (nvenc/vaapi/qsv), or null for
    /// software. NVIDIA decoders use the <c>*_cuvid</c> suffix, which maps to the nvenc accelerator
    /// token the worker advertises for NVIDIA.</summary>
    private static string? AcceleratorOf(string codec)
    {
        if (codec.EndsWith("_nvenc", StringComparison.Ordinal) || codec.EndsWith("_cuvid", StringComparison.Ordinal))
        {
            return "nvenc";
        }

        if (codec.EndsWith("_vaapi", StringComparison.Ordinal))
        {
            return "vaapi";
        }

        if (codec.EndsWith("_qsv", StringComparison.Ordinal))
        {
            return "qsv";
        }

        return null;
    }

    /// <summary>
    /// Maps the concrete encoders present to the accelerator tokens the plugin routes on
    /// (vaapi / nvenc / qsv). NOTE: because ffmpeg lists all compiled-in encoders regardless of the
    /// hardware actually present, this is only reliable when the ffmpeg build is hardware-specific;
    /// the worker advertises its real accelerators from DT_CLASS/DT_HWACCELS instead.
    /// </summary>
    public static IReadOnlyList<string> DeriveHwAccels(IReadOnlyList<string> encoders)
    {
        var accels = new List<string>();
        if (encoders.Any(e => e.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase)))
        {
            accels.Add("nvenc");
        }

        if (encoders.Any(e => e.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase)))
        {
            accels.Add("vaapi");
        }

        if (encoders.Any(e => e.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)))
        {
            accels.Add("qsv");
        }

        return accels;
    }

    private static string ParseVersion(string output)
    {
        // First line looks like: "ffmpeg version 7.1-jellyfin Copyright (c) ..."
        var firstLine = output.Split('\n', 2, StringSplitOptions.None)[0].Trim();
        var match = Regex.Match(firstLine, @"ffmpeg version (\S+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "unknown";
    }

    private static IReadOnlyList<string> ParseEncoders(string output) =>
        ParseVideoList(output, EncodersLineRegex, KnownVideoEncoders);

    /// <summary>
    /// Parses an ffmpeg <c>-encoders</c>/<c>-decoders</c> listing, keeping video entries whose name is
    /// in <paramref name="whitelist"/> (deduplicated, order preserved).
    /// </summary>
    internal static IReadOnlyList<string> ParseVideoList(string output, Regex lineRegex, string[] whitelist)
    {
        var found = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var match = lineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[1].Value;
            if (Array.Exists(whitelist, e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase))
                && !found.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(name);
            }
        }

        return found;
    }

    private static string RunFfmpeg(string ffmpegPath, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            // ffmpeg prints -encoders/-version to stdout; merge stderr defensively.
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);
            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[worker] ffmpeg capability probe failed ({arguments}): {ex.Message}");
            return string.Empty;
        }
    }
}
