using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Re-review reproducers (kept under docs/rereview-repro, outside the test projects). Each test asserts the behaviour a finding describes, so a pass confirms the finding.</summary>
public class RereviewCoreRepro
{
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
            new EyeScreen { Id = "s3", Number = "3", Label = "Confidence", OnAir = false },
        },
        Devices = new[]
        {
            new EyeDevice("dev2", "Lights", "Lines", "10.0.0.30:9000", true, true, "answering", CheckLight.Green, ""),
        },
    };

    private static ShowLogEntry Row(double seconds, string kind, string target, string outcome, string message = "")
        => new(S(seconds), "Desk", kind, target, outcome, message, null, null);

    // EY-2: the journal's own alarm words are unknown to the replay's light table.
    [Fact]
    public void EY2_TheReplayPaintsMismatchAndAlertGreyAndLeavesTheDeskGreenAfterAnUnplug()
    {
        Assert.Equal(CheckLight.Grey, EyeReplay.LightOf("MISMATCH"));
        Assert.Equal(CheckLight.Grey, EyeReplay.LightOf("Alert"));
        var record = ReplayRecord.From(new[] { Row(20, "ScreenLost", "Stage left", "Alert", "SCREEN UNPLUGGED Stage left") },
                                       new[] { new MetricSample { Utc = S(0), P95FrameMs = 12, OutputFps = 60 } });
        var desk = EyeReplay.Apply(EyeGraph.Build(Rig()), EyeReplay.At(record, S(25))).Find(EyeGraph.DeskId)!;
        Assert.Equal(CheckLight.Green, desk.Light);
    }

    // EY-3: a bare-number target of a non-screen verb lights the screen with that number.
    [Theory]
    [InlineData("ApplyLookHotkey", "3", "screen:s3")]
    [InlineData("LowerThirdShow", "2", "screen:s2")]
    [InlineData("AudioPlay", "2", "screen:s2")]
    public void EY3_ANumberedLookLowerThirdOrTrackLightsTheScreenWithThatNumber(string kind, string target, string lit)
    {
        var nodes = EyeReplay.NodesFor(EyeGraph.Build(Rig()), Row(0, kind, target, "Done"));
        Assert.Equal(new[] { lit }, nodes);
    }

    // EY-1 (a): EYE AT is classified as a query, so the pairing gates let an unpaired client ask it.
    [Fact]
    public void EY1_EyeAtIsAQueryAndEyeReplayIsNot()
    {
        Assert.True(ControlProtocol.IsQuery(ControlProtocol.Parse("EYE AT 20:14")));
        Assert.False(ControlProtocol.IsQuery(ControlProtocol.Parse("EYE REPLAY 20:14")));
    }

    // WR-2: a name the desk holds is spliced after a verb whose parser reads sub-verbs and digits first.
    [Theory]
    [InlineData(ShowActionKind.ApplyLook, "Update Walk-in", "", ShowActionKind.LookUpdate)]
    [InlineData(ShowActionKind.ApplyLook, "2", "", ShowActionKind.ApplyLookHotkey)]
    [InlineData(ShowActionKind.ApplyLook, "Delete me", "", ShowActionKind.LookDelete)]
    [InlineData(ShowActionKind.PatternPreset, "", "Save the date", ShowActionKind.PresetSave)]
    [InlineData(ShowActionKind.LowerThirdShow, "Take", "", ShowActionKind.LowerThirdTake)]
    [InlineData(ShowActionKind.StingerFire, "Stop", "", ShowActionKind.StingerStop)]
    public void WR2_ANamedThingIsRecordedAsALineThatReplaysAsAnotherAction(ShowActionKind kind, string target, string value, ShowActionKind becomes)
    {
        var line = WireWriter.Line(new ShowAction(kind, target, value));
        var back = ControlProtocol.Parse(line);
        Assert.True(back.IsAction, "line: " + line);
        Assert.Equal(becomes, back.Action.Kind);
    }
}
