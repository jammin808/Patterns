using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Re-review reproducers (kept under docs/rereview-repro, outside the test projects). A PASS confirms the finding: RT-2, RT-3 (the master rate's follow), TF-8 (a scoped take's row in the replay).</summary>
public class RereviewRateRepro
{
    // RT-2: GDI's integer refresh — 29.97 and 23.976 Hz read as 29 and 23 — is followed; and Win32's "hardware default" 1 is followed to 1 fps.
    [Theory]
    [InlineData(30, 29)]
    [InlineData(24, 23)]
    public void RT2a_AFractionalModeReportedAsAnIntegerDragsTheWholeShowDown(int set, int reported)
        => Assert.Equal(reported, OutputRate.Master(set, true, new[] { ("TV", reported) }).Effective);

    [Fact]
    public void RT2b_TheHardwareDefaultValueOneIsFollowedToOneFps()
        => Assert.Equal(1, OutputRate.Master(60, true, new[] { ("Display 1", 1) }).Effective);

    // RT-3: a screen running at its own rate still leads the master rate, and with the follow off it is named as over-asked.
    [Fact]
    public void RT3_AScreenOnItsOwnRateStillLeadsTheMasterRate()
    {
        var output = new OutputConfig { MasterFps = 60 };
        output.Placements.Add(new ScreenPlacement { ScreenId = "main", Enabled = true, DisplayHz = 60 });
        output.Placements.Add(new ScreenPlacement { ScreenId = "lobby", CustomLabel = "Lobby", Enabled = true, DisplayHz = 50, FpsOverride = 50 });
        Assert.True(output.FollowDisplays);                         // the default, and every saved show's
        Assert.Equal(50, OutputRate.EffectiveMaster(output));       // FINDING: every other output, NDI and the stream go to 50
        output.FollowDisplays = false;
        Assert.True(OutputRate.Master(output).Overasks);            // FINDING: "Lobby refreshes at 50 Hz but the show asks for 60"
    }

    // TF-8: a scoped take's journal row names the scope word, so the replay lights no screen; an unseen take reads green.
    private static readonly DateTime T0 = new(2026, 9, 24, 19, 0, 0, DateTimeKind.Utc);
    private static DateTime S(double seconds) => T0.AddSeconds(seconds);

    private static EyeFacts Rig() => new()
    {
        MachineName = "SHOW-PC",
        OutputsLive = true,
        Health = CheckLight.Green,
        Displays = new[]
        {
            new EyeDisplay("d1", "\\\\.\\DISPLAY1", 1920, 1080, 60, true, false, false),
            new EyeDisplay("d2", "\\\\.\\DISPLAY2", 1920, 1080, 60, false, false, false),
        },
        Screens = new[]
        {
            new EyeScreen { Id = "s1", Number = "1", Label = "Main wall", DisplayId = "d1", OnAir = true, Contract = "1920x1080 60 RGB 8", Verdict = "MATCH" },
            new EyeScreen { Id = "s2", Number = "2", Label = "Projector 1", DisplayId = "d2", OnAir = true, Contract = "1920x1080 60 RGB 8", Verdict = "MATCH" },
        },
    };

    [Fact]
    public void TF8_AFocusedTakeRowLightsNoScreenAndAnUnseenTakeReadsGreen()
    {
        var row = new ShowLogEntry(S(10), "desk", "Take", "FOCUSED", "Done", "TAKE — the preview fades up on 2 · Projector 1 alone", "OutputsOff", "Changed");
        var record = ReplayRecord.From(new[] { row }, new[] { new MetricSample { Utc = S(0), P95FrameMs = 12, OutputFps = 60 } });
        var replay = EyeReplay.Apply(EyeGraph.Build(Rig()), EyeReplay.At(record, S(20)));
        Assert.Equal(EyeReplay.NoRecord, replay.Find("screen:s2")!.Sub);        // FINDING: the screen that took reads "no record"
        Assert.Equal(CheckLight.Green, replay.Find(EyeGraph.DeskId)!.Light);    // FINDING: a take nobody could see reads green
    }
}
