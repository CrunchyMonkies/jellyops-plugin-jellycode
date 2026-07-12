using Jellyfin.Plugin.DistributedTranscoding.Configuration;
using Jellyfin.Plugin.DistributedTranscoding.Transcoding;
using Jellyfin.Plugin.DistributedTranscoding.Worker;
using Xunit;

namespace Jellyfin.Plugin.DistributedTranscoding.UnitTests;

public class WorkerTypeArgsTests
{
    private const string NvencArgs = "-i in.mkv -c:v h264_nvenc -preset p4 -f hls out.m3u8";
    private const string SoftwareArgs = "-i in.mkv -c:v libx264 -crf 23 -f hls out.m3u8";

    [Fact]
    public void Defaults_AreNoOp()
    {
        var o = new WorkerTypeOptions { Type = "nvidia" }; // all defaults
        Assert.Equal(NvencArgs, WorkerTypeArgs.Apply(NvencArgs, o));
    }

    [Fact]
    public void ExplicitEncoder_ReplacesVideoEncoder()
    {
        var o = new WorkerTypeOptions { Type = "nvidia", Encoder = "hevc_nvenc" };
        var result = WorkerTypeArgs.Apply(NvencArgs, o);
        Assert.Contains("-c:v hevc_nvenc", result);
        Assert.DoesNotContain("h264_nvenc", result);
    }

    [Fact]
    public void VideoCodec_RemapsFamilyKeepingAccelerator()
    {
        var o = new WorkerTypeOptions { Type = "nvidia", VideoCodec = "av1" };
        Assert.Contains("-c:v av1_nvenc", WorkerTypeArgs.Apply(NvencArgs, o));
    }

    [Fact]
    public void VideoCodec_RemapsSoftwareToLibEncoder()
    {
        var o = new WorkerTypeOptions { Type = "cpu", VideoCodec = "hevc" };
        Assert.Contains("-c:v libx265", WorkerTypeArgs.Apply(SoftwareArgs, o));
    }

    [Fact]
    public void ExplicitEncoder_WinsOverCodec()
    {
        var o = new WorkerTypeOptions { Type = "nvidia", VideoCodec = "hevc", Encoder = "av1_nvenc" };
        Assert.Contains("-c:v av1_nvenc", WorkerTypeArgs.Apply(NvencArgs, o));
    }

    [Fact]
    public void QualityCq_InjectsCqFlagAfterEncoder()
    {
        var o = new WorkerTypeOptions { Type = "nvidia", QualityMode = "cq", QualityValue = 26 };
        var result = WorkerTypeArgs.Apply(NvencArgs, o);
        Assert.Contains("-c:v h264_nvenc -cq 26", result);
    }

    [Fact]
    public void QualityCrf_IsNotInjectedAtArgLevel()
    {
        // CRF is applied via EncodingOptions, not the arg transform.
        var o = new WorkerTypeOptions { Type = "cpu", QualityMode = "crf", QualityValue = 20 };
        Assert.DoesNotContain("-cq", WorkerTypeArgs.Apply(SoftwareArgs, o));
    }

    [Fact]
    public void Bitrate_InjectsMaxrateAndBufsize()
    {
        var o = new WorkerTypeOptions { Type = "nvidia", MaxBitrateKbps = 8000 };
        var result = WorkerTypeArgs.Apply(NvencArgs, o);
        Assert.Contains("-maxrate 8000k -bufsize 16000k", result);
    }

    [Fact]
    public void ExtraArgs_InjectedAfterEncoder()
    {
        var o = new WorkerTypeOptions { Type = "nvidia", ExtraArgs = "-tune hq" };
        var result = WorkerTypeArgs.Apply(NvencArgs, o);
        Assert.Contains("-c:v h264_nvenc -tune hq", result);
    }

    [Fact]
    public void ExtraArgs_AppendedWhenNoVideoEncoder()
    {
        const string copyArgs = "-i in.mkv -c copy -f hls out.m3u8";
        var o = new WorkerTypeOptions { Type = "cpu", ExtraArgs = "-x extra" };
        Assert.EndsWith("-x extra", WorkerTypeArgs.Apply(copyArgs, o));
    }

    [Theory]
    [InlineData(new[] { "libx264" }, new string[0])]
    [InlineData(new[] { "h264_nvenc", "hevc_nvenc" }, new[] { "nvenc" })]
    [InlineData(new[] { "h264_vaapi", "h264_qsv" }, new[] { "vaapi", "qsv" })]
    public void DeriveHwAccels_MapsEncoderFamilies(string[] encoders, string[] expected)
    {
        var accels = FfmpegCapabilities.DeriveHwAccels(encoders);
        Assert.Equal(expected, accels);
    }

    [Fact]
    public void FilterForAccels_KeepsSoftwareAndMatchingHardwareOnly()
    {
        // jellyfin-ffmpeg lists every compiled-in encoder; an Intel (vaapi) worker must not advertise
        // nvenc/qsv it cannot use.
        var all = new[] { "libx264", "libx265", "libsvtav1", "h264_nvenc", "h264_vaapi", "hevc_vaapi", "h264_qsv" };
        var filtered = FfmpegCapabilities.FilterForAccels(all, new[] { "vaapi" });
        Assert.Equal(new[] { "libx264", "libx265", "libsvtav1", "h264_vaapi", "hevc_vaapi" }, filtered);
    }

    [Fact]
    public void FilterForAccels_CpuKeepsOnlySoftware()
    {
        var all = new[] { "libx264", "h264_nvenc", "h264_vaapi", "h264_qsv" };
        Assert.Equal(new[] { "libx264" }, FfmpegCapabilities.FilterForAccels(all, System.Array.Empty<string>()));
    }

    [Fact]
    public void Defaults_CoverAllThreeTypes()
    {
        var defaults = WorkerTypeOptions.Defaults();
        Assert.Equal(new[] { "cpu", "intel", "nvidia" }, System.Array.ConvertAll(defaults, d => d.Type));
        Assert.All(defaults, d =>
        {
            Assert.True(d.Enabled);
            Assert.Equal("auto", d.VideoCodec);
            Assert.Equal("auto", d.Encoder);
        });
    }
}
