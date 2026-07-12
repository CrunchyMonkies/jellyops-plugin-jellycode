using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.DistributedTranscoding.Configuration;

namespace Jellyfin.Plugin.DistributedTranscoding.Transcoding;

/// <summary>
/// Applies the arg-level portion of a worker type's transcoding options onto an already-generated
/// ffmpeg command line — the parts not expressible through core <c>EncodingOptions</c>: forcing a
/// specific encoder/codec, a max-bitrate cap, hardware quality (cq/qp/global_quality) and free-text
/// extra args. Preset and CRF are applied earlier via EncodingOptions.
/// </summary>
public static class WorkerTypeArgs
{
    // Matches the (first) video encoder selection, e.g. "-c:v h264_nvenc" / "-codec:v libx264".
    private static readonly Regex VideoEncoderRegex =
        new(@"-(?:c|codec):v\s+(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Returns <paramref name="args"/> with the type's arg-level overrides applied. No-ops for any
    /// option left at its default.
    /// </summary>
    /// <param name="args">The base ffmpeg argument string.</param>
    /// <param name="o">The worker type options.</param>
    /// <returns>The transformed argument string.</returns>
    public static string Apply(string args, WorkerTypeOptions o)
    {
        if (string.IsNullOrEmpty(args) || o is null)
        {
            return args;
        }

        var match = VideoEncoderRegex.Match(args);
        if (match.Success)
        {
            var currentEncoder = match.Groups[1].Value;
            var newEncoder = ResolveEncoder(currentEncoder, o);

            // Flags injected right after the encoder token so they bind to the video output stream.
            var injected = new List<string>();
            AppendQuality(injected, o);
            AppendBitrate(injected, o);
            if (!string.IsNullOrWhiteSpace(o.ExtraArgs))
            {
                injected.Add(o.ExtraArgs.Trim());
            }

            var replacement = $"-c:v {newEncoder}";
            if (injected.Count > 0)
            {
                replacement += " " + string.Join(" ", injected);
            }

            args = args.Substring(0, match.Index) + replacement + args.Substring(match.Index + match.Length);
        }
        else if (!string.IsNullOrWhiteSpace(o.ExtraArgs))
        {
            // No video encoder token (e.g. copy/audio job): still honor extra args by appending.
            args = args.TrimEnd() + " " + o.ExtraArgs.Trim();
        }

        return args;
    }

    /// <summary>Chooses the encoder token: explicit Encoder wins; else remap the codec family.</summary>
    private static string ResolveEncoder(string currentEncoder, WorkerTypeOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.Encoder) && !string.Equals(o.Encoder, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return o.Encoder;
        }

        if (!string.IsNullOrWhiteSpace(o.VideoCodec) && !string.Equals(o.VideoCodec, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return RemapCodec(currentEncoder, o.VideoCodec.ToLowerInvariant());
        }

        return currentEncoder;
    }

    /// <summary>Swaps just the codec family of an encoder token, preserving its accelerator suffix.</summary>
    private static string RemapCodec(string currentEncoder, string codec)
    {
        var lower = currentEncoder.ToLowerInvariant();
        if (lower.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            return codec + "_nvenc";
        }

        if (lower.EndsWith("_vaapi", StringComparison.Ordinal))
        {
            return codec + "_vaapi";
        }

        if (lower.EndsWith("_qsv", StringComparison.Ordinal))
        {
            return codec + "_qsv";
        }

        // Software: map codec to its libx encoder.
        return codec switch
        {
            "h264" => "libx264",
            "hevc" => "libx265",
            "av1" => "libsvtav1",
            _ => currentEncoder,
        };
    }

    private static void AppendQuality(List<string> injected, WorkerTypeOptions o)
    {
        if (o.QualityValue <= 0)
        {
            return;
        }

        var value = o.QualityValue.ToString(CultureInfo.InvariantCulture);
        switch (o.QualityMode?.ToLowerInvariant())
        {
            case "cq":
                injected.Add($"-cq {value}");
                break;
            case "qp":
                injected.Add($"-qp {value}");
                break;
            case "global_quality":
                injected.Add($"-global_quality {value}");
                break;
            // "crf" is handled via EncodingOptions; "auto" adds nothing.
        }
    }

    private static void AppendBitrate(List<string> injected, WorkerTypeOptions o)
    {
        if (o.MaxBitrateKbps <= 0)
        {
            return;
        }

        var k = o.MaxBitrateKbps;
        injected.Add($"-maxrate {k}k -bufsize {k * 2}k");
    }
}
