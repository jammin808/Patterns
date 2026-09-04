using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The master frame rate and what follows it, capture modes, and a screen changing its id.</summary>
public class FrameRateTests
{
    [Theory]
    [InlineData(30, 60, 120, 60)]   // every other vsync
    [InlineData(25, 60, 120, 50)]   // 5 of every 12
    [InlineData(60, 60, 120, 120)]  // every vsync
    [InlineData(24, 60, 300, 120)]  // 2 of every 5
    [InlineData(0, 60, 120, 120)]   // unlimited
    public void ThePacerPresentsTheTargetShareOfVsyncs(int target, int vsync, int ticks, int presents)
    {
        long slot = -1;
        var shown = 0;
        for (var i = 0; i < ticks; i++)
        {
            if (FramePacer.ShouldPresent(1000.0 + i / (double)vsync, target, ref slot)) shown++;
        }
        Assert.Equal(presents, shown);
    }

    [Fact]
    public void ThePacerNeverBurstsAndTwoSinksAgreeOnTheClock()
    {
        // 25 on 60: presents are 2 or 3 vsyncs apart, never adjacent, never 4 apart.
        long slot = -1;
        var last = -10;
        for (var i = 0; i < 600; i++)
        {
            if (!FramePacer.ShouldPresent(i / 60.0, 25, ref slot)) continue;
            if (last >= 0) Assert.InRange(i - last, 2, 3);
            last = i;
        }

        // Two sinks that started at different times present in the same slots.
        long a = -1, b = -1;
        var aFrames = new List<long>();
        var bFrames = new List<long>();
        for (var i = 0; i < 120; i++)
        {
            if (FramePacer.ShouldPresent(i / 60.0, 30, ref a)) aFrames.Add(FramePacer.SlotOf(i / 60.0, 30));
            if (i >= 7 && FramePacer.ShouldPresent(i / 60.0, 30, ref b)) bFrames.Add(FramePacer.SlotOf(i / 60.0, 30));
        }
        Assert.Equal(aFrames.Skip(aFrames.Count - bFrames.Count), bFrames);
    }

    [Fact]
    public void AnNdiSenderOnMasterSendsAtTheMasterRate()
    {
        Assert.Equal((30000, 1000), NdiRateTable.Resolve(NdiRateTable.MasterKey, 30));
        Assert.Equal((25000, 1000), NdiRateTable.Resolve("master", 25));
        Assert.Equal((60000, 1000), NdiRateTable.Resolve("master", 0));      // unlimited master: 60
        Assert.Equal((60000, 1001), NdiRateTable.Resolve("59.94", 30));      // a fixed key ignores the master
        Assert.Equal(NdiRateTable.MasterKey, NdiRateTable.Keys[0]);
    }

    [Fact]
    public void TheStreamFollowsTheMasterOnlyWhenAsked()
    {
        var cfg = new StreamConfig { Fps = 30 };
        Assert.Equal(30, StreamMrl.EffectiveFps(cfg, 50));
        cfg.FpsFollowsMaster = true;
        Assert.Equal(50, StreamMrl.EffectiveFps(cfg, 50));
        Assert.Equal(30, StreamMrl.EffectiveFps(cfg, 0));      // unlimited master: its own number
        Assert.Equal(60, StreamMrl.EffectiveFps(cfg, 120));    // the encoder's ceiling

        var plan = StreamMrl.Build(cfg, SKRectI.Create(0, 0, 1920, 1080), new[] { "rtmp://x/y" }, masterFps: 25);
        Assert.NotNull(plan);
        Assert.Contains(":screen-fps=25", plan!.Options);
        Assert.Contains(plan.Options, o => o.Contains("keyint=50"));
    }

    [Fact]
    public void ACaptureFormatRoundTripsThroughItsKey()
    {
        var f = new CaptureFormat(1920, 1080, 59.94);
        Assert.Equal("1920x1080@59.94", f.Key);
        Assert.Equal("1920×1080 @ 59.94", f.Label);
        Assert.True(CaptureFormat.TryParse(f.Key, out var back));
        Assert.Equal(f, back);
        Assert.True(CaptureFormat.TryParse("1280x720@60", out var p));
        Assert.Equal(new CaptureFormat(1280, 720, 60), p);
        Assert.Equal("1280x720@60", p.Key);
        Assert.False(CaptureFormat.TryParse("", out _));
        Assert.False(CaptureFormat.TryParse("1920x1080", out _));
        Assert.False(CaptureFormat.TryParse("junk@60", out _));
        Assert.False(CaptureFormat.TryParse("0x0@60", out _));
    }

    [Fact]
    public void TheShowRemembersACaptureDevicesModeAndTheWantedInputCarriesIt()
    {
        var s = new ShowState();
        Assert.Equal("", s.CaptureFormatFor("Cam Link 4K"));
        s.SetCaptureFormat("Cam Link 4K", "1920x1080@60");
        Assert.Equal("1920x1080@60", s.CaptureFormatFor("cam link 4k"));
        s.SetCaptureFormat("Cam Link 4K", "3840x2160@30");
        Assert.Single(s.CaptureFormats);
        Assert.Equal("3840x2160@30", s.CaptureFormatFor("Cam Link 4K"));

        s.Pattern.Kind = PatternKind.Media;
        s.Pattern.Media.Source = MediaSource.Capture;
        s.Pattern.Media.CaptureDevice = "Cam Link 4K";
        var wanted = MediaLocator.FindWantedInputs(new ShowSnapshot { State = s, Version = 1 });
        Assert.Single(wanted);
        Assert.Equal("3840x2160@30", wanted[0].Format);

        s.SetCaptureFormat("Cam Link 4K", "");
        Assert.Empty(s.CaptureFormats);
        Assert.Equal("", MediaLocator.FindWantedInputs(new ShowSnapshot { State = s, Version = 2 })[0].Format);

        // The choice survives the show file.
        s.SetCaptureFormat("Cam Link 4K", "1920x1080@50");
        var clone = JsonUtil.Clone(s);
        Assert.Equal("1920x1080@50", clone.CaptureFormatFor("Cam Link 4K"));
    }

    [Fact]
    public void RenamingAScreenMovesEveryReferenceIncludingLooks()
    {
        var s = new ShowState();
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "0:1920x1080@0,0", X = 0 });
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "1:1920x1080@1920,0", X = 1920 });
        s.Output.CanvasNames.Add(new CanvasNameConfig { MemberKey = CanvasNameConfig.KeyFor(new[] { "0:1920x1080@0,0", "1:1920x1080@1920,0" }), Name = "Wall" });
        s.Independent.Add(new OutputAssignment { ScreenId = "0:1920x1080@0,0" });
        s.Independent.Add(new OutputAssignment { ScreenId = CanvasNameConfig.KeyFor(new[] { "0:1920x1080@0,0", "1:1920x1080@1920,0" }) });
        s.Pattern.Multiview.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = "0:1920x1080@0,0" });
        s.Ndi.Senders.Add(new NdiSenderConfig { SourceScreenId = "0:1920x1080@0,0" });
        s.Stream.SourceScreenId = "0:1920x1080@0,0";
        s.Web.TargetScreenId = "0:1920x1080@0,0";
        s.LooksAndCues.Looks.Add(new LookConfig { Name = "L", Json = "{\"ScreenId\":\"0:1920x1080@0,0\"}" });

        const string newId = "0:3840x2160@0,0";
        ContentTargets.RenameScreen(s, "0:1920x1080@0,0", newId);

        Assert.Equal(newId, s.Output.Placements[0].ScreenId);
        Assert.Equal("1:1920x1080@1920,0", s.Output.Placements[1].ScreenId);
        var key = CanvasNameConfig.KeyFor(new[] { newId, "1:1920x1080@1920,0" });
        Assert.Equal(key, s.Output.CanvasNames[0].MemberKey);
        Assert.Equal(newId, s.Independent[0].ScreenId);
        Assert.Equal(key, s.Independent[1].ScreenId);
        Assert.Equal(newId, s.Pattern.Multiview.Tiles[0].ScreenId);
        Assert.Equal(newId, s.Ndi.Senders[0].SourceScreenId);
        Assert.Equal(newId, s.Stream.SourceScreenId);
        Assert.Equal(newId, s.Web.TargetScreenId);
        Assert.Contains(newId, s.LooksAndCues.Looks[0].Json);
        Assert.DoesNotContain("0:1920x1080@0,0", s.LooksAndCues.Looks[0].Json);

        // A no-op rename changes nothing.
        ContentTargets.RenameScreen(s, "ghost", "other");
        ContentTargets.RenameScreen(s, newId, newId);
        Assert.Equal(newId, s.Output.Placements[0].ScreenId);
    }
}
