using Jellyfin.Plugin.DistributedTranscoding.Configuration;
using Jellyfin.Plugin.DistributedTranscoding.Worker;
using Xunit;

namespace Jellyfin.Plugin.DistributedTranscoding.UnitTests;

public class RoutingAndDecoderTests
{
    private static readonly string[] Default = { "intel", "nvidia", "cpu" };

    [Fact]
    public void Resolve_NoRules_ReturnsDefault()
    {
        Assert.Equal(Default, RoutingRule.Resolve(null, Default, "hevc", "h264"));
    }

    [Fact]
    public void Resolve_EmptyDefault_FallsBackToBuiltIn()
    {
        Assert.Equal(new[] { "intel", "nvidia", "cpu" }, RoutingRule.Resolve(null, System.Array.Empty<string>(), "h264", "h264"));
    }

    [Fact]
    public void Resolve_MostSpecificRuleWins()
    {
        var rules = new[]
        {
            new RoutingRule { DecodeCodec = "any", EncodeCodec = "any", WorkerPriority = new[] { "cpu" } },
            new RoutingRule { DecodeCodec = "hevc", EncodeCodec = "any", WorkerPriority = new[] { "intel" } },
            new RoutingRule { DecodeCodec = "hevc", EncodeCodec = "h264", WorkerPriority = new[] { "nvidia", "intel" } },
        };
        // hevc->h264 : the both-codec rule wins.
        Assert.Equal(new[] { "nvidia", "intel" }, RoutingRule.Resolve(rules, Default, "hevc", "h264"));
        // hevc->av1 : only the single-codec (decode=hevc) rule matches.
        Assert.Equal(new[] { "intel" }, RoutingRule.Resolve(rules, Default, "hevc", "av1"));
        // av1->h264 : only the wildcard rule matches.
        Assert.Equal(new[] { "cpu" }, RoutingRule.Resolve(rules, Default, "av1", "h264"));
    }

    [Fact]
    public void Resolve_CopyEncode_MatchesCopyRule()
    {
        var rules = new[] { new RoutingRule { DecodeCodec = "any", EncodeCodec = "copy", WorkerPriority = new[] { "cpu" } } };
        Assert.Equal(new[] { "cpu" }, RoutingRule.Resolve(rules, Default, "hevc", "copy"));
        // A non-copy encode does NOT match the copy rule -> default.
        Assert.Equal(Default, RoutingRule.Resolve(rules, Default, "hevc", "h264"));
    }

    [Fact]
    public void Resolve_RuleWithNoPriority_IsIgnored()
    {
        var rules = new[] { new RoutingRule { DecodeCodec = "hevc", EncodeCodec = "h264", WorkerPriority = System.Array.Empty<string>() } };
        Assert.Equal(Default, RoutingRule.Resolve(rules, Default, "hevc", "h264"));
    }

    [Theory]
    [InlineData("any", "hevc", true)]
    [InlineData("", "hevc", true)]
    [InlineData("hevc", "hevc", true)]
    [InlineData("HEVC", "hevc", true)]
    [InlineData("hevc", "h264", false)]
    [InlineData("hevc", null, false)]
    public void FieldMatches_Wildcards(string field, string? codec, bool expected)
    {
        Assert.Equal(expected, RoutingRule.FieldMatches(field, codec));
    }

    [Fact]
    public void DecoderProbe_ParsesVideoDecoderRows_IncludingCuvid()
    {
        // Representative `ffmpeg -decoders` output (V flag = video).
        const string output = @"
 Decoders:
 V..... = Video
 V....D h264                 H.264
 V....D hevc                 H.265
 V....D h264_cuvid           Nvidia CUVID H264
 V....D hevc_qsv             HEVC QSV
 V....D av1_vaapi            AV1 VAAPI
 A....D aac                  AAC (audio, ignored)";
        var list = FfmpegCapabilities.ParseVideoList(output, DecoderRegex(), Whitelist());
        Assert.Contains("h264_cuvid", list);
        Assert.Contains("hevc_qsv", list);
        Assert.Contains("av1_vaapi", list);
        Assert.Contains("h264", list);
        Assert.DoesNotContain("aac", list);
    }

    [Fact]
    public void FilterForAccels_KeepsCuvidOnlyForNvenc()
    {
        // NVIDIA decoders use *_cuvid, which maps to the nvenc accelerator token.
        var all = new[] { "h264", "hevc", "h264_cuvid", "hevc_cuvid", "h264_vaapi" };
        var nvidia = FfmpegCapabilities.FilterForAccels(all, new[] { "nvenc" });
        Assert.Equal(new[] { "h264", "hevc", "h264_cuvid", "hevc_cuvid" }, nvidia);
        var cpu = FfmpegCapabilities.FilterForAccels(all, System.Array.Empty<string>());
        Assert.Equal(new[] { "h264", "hevc" }, cpu);
    }

    // Mirror the private regex/whitelist shape used by the decoder probe.
    private static System.Text.RegularExpressions.Regex DecoderRegex() =>
        new(@"^\s*V[\.A-Z]{5,}\s+(\S+)", System.Text.RegularExpressions.RegexOptions.None, System.TimeSpan.FromMilliseconds(200));

    private static string[] Whitelist() => new[]
    {
        "h264", "hevc", "av1", "vp9", "vp8", "mpeg4", "mpeg2video",
        "h264_cuvid", "hevc_cuvid", "av1_cuvid",
        "h264_qsv", "hevc_qsv", "av1_qsv",
        "h264_vaapi", "hevc_vaapi", "av1_vaapi",
    };
}
