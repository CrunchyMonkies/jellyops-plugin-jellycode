using System;
using System.Linq;
using Jellyfin.Plugin.DistributedTranscoding.Transcoding;
using Xunit;

namespace Jellyfin.Plugin.DistributedTranscoding.UnitTests;

public class SelectionStagesTests
{
    private static readonly string[] Default = { "intel", "nvidia", "cpu" };
    private static readonly Func<string, bool> AllEnabled = _ => true;

    [Fact]
    public void Build_VideoEncode_HwDecodePassSkipsCpu()
    {
        var stages = SelectionStages.Build(isVideoEncode: true, Default);
        Assert.Equal(
            new[] { ("intel", true), ("nvidia", true), ("intel", false), ("nvidia", false), ("cpu", false) },
            stages.Select(s => (s.Type!, s.RequireHwDecode)).ToArray());
    }

    [Fact]
    public void Build_CpuOnlyPriority_HasSingleRelaxedStage()
    {
        var stages = SelectionStages.Build(isVideoEncode: true, new[] { "cpu" });
        Assert.Single(stages);
        Assert.False(stages[0].RequireHwDecode);
    }

    [Fact]
    public void Build_Copy_PrefersCpuThenAny()
    {
        var stages = SelectionStages.Build(isVideoEncode: false, Default);
        Assert.Equal(2, stages.Count);
        Assert.Equal("cpu", stages[0].Type);
        Assert.True(stages[1].AnyWorker);
    }

    [Fact]
    public void ShouldWait_SkipsTypesWithNoConnectedWorker()
    {
        var connected = new[] { "cpu" };
        Assert.False(SelectionStages.ShouldWait(new SelectionStage("intel", true, false), false, connected, AllEnabled));
        Assert.False(SelectionStages.ShouldWait(new SelectionStage("nvidia", false, false), false, connected, AllEnabled));
        Assert.True(SelectionStages.ShouldWait(new SelectionStage("cpu", false, false), false, connected, AllEnabled));
    }

    [Fact]
    public void ShouldWait_MatchesWorkerTypeCaseInsensitively()
    {
        Assert.True(SelectionStages.ShouldWait(new SelectionStage("Intel", false, false), false, new[] { "intel" }, AllEnabled));
    }

    [Fact]
    public void ShouldWait_SkipsDisabledType()
    {
        Assert.False(SelectionStages.ShouldWait(new SelectionStage("intel", false, false), false, new[] { "intel" }, t => t != "intel"));
    }

    [Fact]
    public void ShouldWait_LastStageAlwaysWaits()
    {
        Assert.True(SelectionStages.ShouldWait(new SelectionStage("cpu", false, false), true, Array.Empty<string>(), AllEnabled));
    }

    [Fact]
    public void ShouldWait_AnyStageWaitsOnlyWithConnectedWorkers()
    {
        var any = new SelectionStage(null, false, true);
        Assert.False(SelectionStages.ShouldWait(any, false, Array.Empty<string>(), AllEnabled));
        Assert.True(SelectionStages.ShouldWait(any, false, new[] { "cpu" }, AllEnabled));
    }
}
