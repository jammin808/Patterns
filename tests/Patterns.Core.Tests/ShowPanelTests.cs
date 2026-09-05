using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The Show panel's per-screen sends in the pure layer: SCREEN n LOOK / PROGRAM on the wire and over
/// OSC, the cue actions through the spec, the checks, the summary and the sheet's words, and a look's
/// picture for one screen — its own if the look carried one, else the look's program.
/// </summary>
public class ShowPanelTests
{
    private static ShowState Rig()
    {
        var s = new ShowState();
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "a" });
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "b" });
        s.Pattern.Kind = PatternKind.Grid;
        return s;
    }

    private static LookConfig SaveLook(ShowState s, string name, PatternKind kind)
    {
        var other = new ShowState();
        other.Pattern.Kind = kind;
        var look = new LookConfig { Name = name, Json = LookService.Capture(other) };
        s.LooksAndCues.Looks.Add(look);
        return look;
    }

    [Fact]
    public void TheWireCarriesAScreensOwnLookAndTheProgramAndLeavesOnOffToggleAlone()
    {
        var look = ControlProtocol.Parse("SCREEN 2 LOOK Sponsor Logo");
        Assert.Equal(RemoteCommandKind.ScreenLook, look.Kind);
        Assert.Equal(2, look.IntArg);
        Assert.Equal("Sponsor Logo", look.TextArg);
        Assert.Equal(RemoteCommandKind.ScreenLook, ControlProtocol.Parse("screen 1 look daytime").Kind);
        Assert.Equal("daytime", ControlProtocol.Parse("screen 1 look daytime").TextArg);

        // No look named is a refusal, not a toggle.
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN 2 LOOK").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN 2 LOOK   ").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN x LOOK Sponsor").Kind);

        var program = ControlProtocol.Parse("SCREEN 3 PROGRAM");
        Assert.Equal((RemoteCommandKind.ScreenProgram, 3), (program.Kind, program.IntArg));
        Assert.Equal(RemoteCommandKind.ScreenProgram, ControlProtocol.Parse("SCREEN 3 PGM").Kind);
        Assert.Equal(RemoteCommandKind.ScreenProgram, ControlProtocol.Parse("screen 3 follow").Kind);

        // The older words are untouched.
        Assert.Equal(RemoteCommandKind.ScreenOn, ControlProtocol.Parse("SCREEN 2 ON").Kind);
        Assert.Equal(RemoteCommandKind.ScreenOff, ControlProtocol.Parse("SCREEN 2 OFF").Kind);
        Assert.Equal(RemoteCommandKind.ScreenToggle, ControlProtocol.Parse("SCREEN 2 TOGGLE").Kind);
        Assert.Equal(RemoteCommandKind.ScreenToggle, ControlProtocol.Parse("SCREEN 2").Kind);
    }

    [Fact]
    public void OscAddressesAScreensOwnLookAndTheProgram()
    {
        Assert.Equal("SCREEN 2 LOOK Sponsor", OscMap.ToLine(OscMessage.Of("/patterns/screen/2/look", "Sponsor")));
        Assert.Equal("SCREEN 2 LOOK Sponsor", OscMap.ToLine(OscMessage.Of("/patterns/screen/2/look/Sponsor")));
        Assert.Equal("SCREEN 2 LOOK Sponsor Logo", OscMap.ToLine(OscMessage.Of("/patterns/screen/2/look/Sponsor/Logo")));
        Assert.Equal("SCREEN 2 PROGRAM", OscMap.ToLine(OscMessage.Of("/patterns/screen/2/program")));
        Assert.Equal("SCREEN 2 PROGRAM", OscMap.ToLine(OscMessage.Of("/patterns/screen/2/pgm")));
        Assert.Equal("SCREEN 2 PROGRAM", OscMap.ToLine(OscMessage.Of("/patterns/screen/2/follow")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/screen/2/look")));
        Assert.StartsWith("SCREEN 2", OscMap.ToLine(OscMessage.Of("/patterns/screen/2", 1)));        // the switch, as before
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/screen/<n>/look"));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/screen/<n>/program"));
    }

    [Fact]
    public void TheCueActionsAreSpecifiedLabelledAndReadFromASheet()
    {
        Assert.Equal((TargetKind.Screen, ValueKind.Look), CueActionSpec.For(CueActionKind.ScreenLook));
        Assert.Equal((TargetKind.Screen, ValueKind.None), CueActionSpec.For(CueActionKind.ScreenProgram));
        Assert.Contains(CueActionKind.ScreenLook, CueActionSpec.Editable);
        Assert.Contains(CueActionKind.ScreenProgram, CueActionSpec.Editable);
        Assert.True(CueActionSpec.ChangesContent(CueActionKind.ScreenLook));
        Assert.True(CueActionSpec.ChangesContent(CueActionKind.ScreenProgram));
        Assert.Contains("own look", CueActionSpec.Label(CueActionKind.ScreenLook));
        Assert.Contains("program", CueActionSpec.Label(CueActionKind.ScreenProgram));

        Assert.Equal(CueActionKind.ScreenLook, CueSheet.ParseKind("screenlook"));
        Assert.Equal(CueActionKind.ScreenLook, CueSheet.ParseKind("Screen — its own look"));
        Assert.Equal(CueActionKind.ScreenLook, CueSheet.ParseKind("Own look"));
        Assert.Equal(CueActionKind.ScreenLook, CueSheet.ParseKind("send look"));
        Assert.Equal(CueActionKind.ScreenProgram, CueSheet.ParseKind("Screen — back to the program"));
        Assert.Equal(CueActionKind.ScreenProgram, CueSheet.ParseKind("screen program"));
        Assert.Equal(CueActionKind.ScreenProgram, CueSheet.ParseKind("PGM"));
        Assert.Equal(CueActionKind.ScreenProgram, CueSheet.ParseKind("back to program"));
        Assert.Equal(CueActionKind.ScreenProgram, CueSheet.ParseKind("follow"));
    }

    [Fact]
    public void TheChecksWantAScreenInTheRigAndALookThatExistsAndTheSummaryNamesThem()
    {
        var s = Rig();
        var sponsor = SaveLook(s, "Sponsor", PatternKind.ColorBars);
        var cue = new RunCueConfig { Number = "1", Name = "Sponsor on the side" };
        cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ScreenLook, Target = "b", Value = "Sponsor" });
        var stack = new CueStackConfig();
        stack.Cues.Add(cue);

        Assert.False(CueValidator.Validate(s, stack).IsBroken(cue.Id));
        Assert.Contains("Sponsor", CueSummary.DescribeAction(s, cue.Actions[0]));

        cue.Actions[0].Value = sponsor.Id;                                                   // by id too
        Assert.False(CueValidator.Validate(s, stack).IsBroken(cue.Id));
        Assert.Contains("Sponsor", CueSummary.DescribeAction(s, cue.Actions[0]));

        cue.Actions[0].Value = "Nobody";
        Assert.Contains("not found", CueValidator.Validate(s, stack).ReasonFor(cue.Id));
        cue.Actions[0].Value = "";
        Assert.Contains("which look", CueValidator.Validate(s, stack).ReasonFor(cue.Id));
        cue.Actions[0].Value = "Sponsor";
        cue.Actions[0].Target = "zzz";
        Assert.Contains("not in the rig", CueValidator.Validate(s, stack).ReasonFor(cue.Id));

        var back = new RunCueConfig { Number = "2", Name = "Side back" };
        back.Actions.Add(new CueActionConfig { Kind = CueActionKind.ScreenProgram, Target = "b" });
        stack.Cues.Add(back);
        Assert.False(CueValidator.Validate(s, stack).IsBroken(back.Id));
        Assert.Contains("program", CueSummary.DescribeAction(s, back.Actions[0]));
        back.Actions[0].Target = "";
        Assert.True(CueValidator.Validate(s, stack).IsBroken(back.Id));
    }

    [Fact]
    public void ALooksPictureForOneScreenIsItsOwnWhenItCarriedOneElseTheProgram()
    {
        var s = Rig();
        s.Pattern.Kind = PatternKind.Focus;
        ContentTargets.EnsureAssignment(s, "b").Pattern.Kind = PatternKind.Ramp;
        ContentTargets.SetOwnPattern(s, "b", true);
        var json = LookService.Capture(s);

        Assert.Equal(PatternKind.Ramp, LookService.PictureFor(json, "b")!.Kind);
        Assert.Equal(PatternKind.Focus, LookService.PictureFor(json, "a")!.Kind);
        Assert.Equal(PatternKind.Focus, LookService.PictureFor(json, "ghost")!.Kind);     // a target the look never knew: the program
        Assert.Null(LookService.PictureFor("not a look", "a"));
        Assert.Null(LookService.PictureFor("", "a"));

        var first = LookService.PictureFor(json, "a")!;
        first.Kind = PatternKind.Checkerboard;                                            // a clone: the look is untouched
        Assert.Equal(PatternKind.Focus, LookService.PictureFor(json, "a")!.Kind);
    }
}
