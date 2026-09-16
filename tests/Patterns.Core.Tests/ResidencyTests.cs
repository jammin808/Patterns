using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 69: the residency policy — the grace an idle thing gets by the machine's class and the pressure,
/// the reason a held thing has, the pictures the show names, and the words the ledger reads in.
/// </summary>
public class ResidencyTests
{
    [Fact]
    public void TheGraceIsTheClassesShortenedAsThePressureClimbs()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), Residency.Grace(MachineClass.Small, MemoryPressure.None));
        Assert.Equal(TimeSpan.FromSeconds(60), Residency.Grace(MachineClass.Standard, MemoryPressure.None));
        Assert.Equal(TimeSpan.FromSeconds(180), Residency.Grace(MachineClass.Big, MemoryPressure.None));
        Assert.Equal(TimeSpan.FromSeconds(30), Residency.Grace(MachineClass.Standard, MemoryPressure.Elevated));
        Assert.Equal(TimeSpan.FromSeconds(15), Residency.Grace(MachineClass.Standard, MemoryPressure.High));
        Assert.Equal(TimeSpan.Zero, Residency.Grace(MachineClass.Big, MemoryPressure.Critical));
    }

    [Fact]
    public void EveryHeldThingHasAReasonReadFromWhatItIsOn()
    {
        Assert.Equal(HoldReason.PreRolled, Residency.ForBuses(new[] { MediaBus.Program }, preRoll: true));
        Assert.Equal(HoldReason.OnAir, Residency.ForBuses(new[] { MediaBus.Program }, preRoll: false));
        Assert.Equal(HoldReason.OnAir, Residency.ForBuses(new[] { MediaBus.Sandbox, MediaBus.Output("INFO") }, preRoll: false));
        Assert.Equal(HoldReason.Preview, Residency.ForBuses(new[] { MediaBus.Sandbox }, preRoll: false));
        Assert.Equal(HoldReason.OnAir, Residency.ForBuses(null, preRoll: false));                 // no bus list: the engine holds it for the air
        Assert.Equal(HoldReason.OnAir, Residency.ForPicture(0.2, named: false));                  // drawn this second
        Assert.Equal(HoldReason.Named, Residency.ForPicture(40, named: true));
        Assert.Equal(HoldReason.Idle, Residency.ForPicture(40, named: false));
        Assert.Equal("on air", Residency.Word(HoldReason.OnAir));
        Assert.Equal("armed at its mark", Residency.Word(HoldReason.Armed));
        Assert.Equal("pre-rolled for the standby cue", Residency.Word(HoldReason.PreRolled));
        Assert.True(Residency.Rank(HoldReason.OnAir) < Residency.Rank(HoldReason.Idle));
        Assert.True(Residency.Rank(HoldReason.Idle) < Residency.Rank(HoldReason.Retiring));
    }

    [Fact]
    public void TheWordsSayWhatIsHeldWhyAndWhenTheFirstIdleThingGoes()
    {
        var grace = TimeSpan.FromSeconds(60);
        var holds = new[]
        {
            new Hold("vid:a.mp4", "clip", "a.mp4", HoldReason.OnAir, 64L * 1024 * 1024, 0, "playing"),
            new Hold("vid:b.mp4", "clip", "b.mp4", HoldReason.PreRolled, 64L * 1024 * 1024, 0, "pre-rolled"),
            new Hold("pic:logo.png", "picture", "logo.png", HoldReason.Named, 8L * 1024 * 1024, 30),
            new Hold("pic:old.png", "picture", "old.png", HoldReason.Idle, 33L * 1024 * 1024, 42),
            new Hold("pic:older.png", "picture", "older.png", HoldReason.Idle, 1L * 1024 * 1024, 55),
        };
        Assert.Equal("5 held (170 MB): 1 on air, 1 pre-rolled for the standby cue, 1 named by the show, 2 idle — the first lets go in 5 s", Residency.Summary(holds, grace));
        Assert.Equal("nothing held", Residency.Summary(Array.Empty<Hold>(), grace));
        Assert.Contains("idle things go at once under this pressure", Residency.Summary(holds, TimeSpan.Zero));

        Assert.Equal("a.mp4 — on air (playing) · 64 MB", holds[0].Words(grace));
        Assert.Equal("old.png — idle 42 s, lets go in 18 s · 33 MB", holds[3].Words(grace));
        Assert.Equal("older.png — idle 55 s, letting go · 1 MB", holds[4].Words(TimeSpan.FromSeconds(50)));
        Assert.Equal("logo.png — named by the show · 8 MB", holds[2].Words(grace));
    }

    [Fact]
    public void TheShowNamesThePicturesItKeepsDecoded()
    {
        var state = new ShowState();
        state.Pattern.Media.ImagePath = @"C:\show\hero.png";
        state.Pattern.Layer1.ImagePath = @"C:\show\frame.png";
        state.Brand.LogoPath = @"C:\show\logo.png";
        var own = new OutputAssignment { ScreenId = "INFO" };
        own.Pattern.Media.ImagePath = @"C:\show\info.png";
        state.Independent.Add(own);
        var sandbox = new ShowState();
        sandbox.Pattern.Layer2.ImagePath = @"C:\show\next.png";
        var standbyShow = new ShowState();
        standbyShow.Pattern.Media.ImagePath = @"C:\show\cue.png";
        var standby = new LookConfig { Name = "Cue", Json = LookService.Capture(standbyShow) };

        var named = Residency.NamedPictures(state, sandbox, standby);
        Assert.Contains(@"C:\show\hero.png", named);
        Assert.Contains(@"C:\show\frame.png", named);
        Assert.Contains(@"C:\show\logo.png", named);
        Assert.Contains(@"C:\show\info.png", named);
        Assert.Contains(@"C:\show\next.png", named);
        Assert.Contains(@"C:\show\cue.png", named);
        Assert.DoesNotContain("", named);
        Assert.Empty(Residency.NamedPictures(new ShowState()));
        Assert.Empty(Residency.NamedPictures(new ShowState(), null, new LookConfig { Name = "Broken", Json = "{not json" }));
    }
}
