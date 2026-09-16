using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 60: the staged verbs. A picture on a target's PVW in the preview and nowhere else —
/// SCREEN n PVW LOOK / PRESET / PATTERN / PROGRAM / RESET, and PVW … for the programme — so a
/// right-click menu, a cue or a key can build the next picture without the audience seeing a
/// thing until CUT or TAKE. These hold the words: the table classifies them, the summary reads
/// them, the sheet parses them, the validator checks them, OSC carries them.
/// </summary>
public class StagedVerbsTests
{
    private static ShowState Rig()
    {
        var s = new ShowState();
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", CustomLabel = "Stage left", Enabled = true });
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", CustomLabel = "Stage right", Enabled = true, X = 4000 });
        s.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in", Json = LookService.Capture(s) });
        return s;
    }

    [Fact]
    public void TheStagedKindsAreClassifiedAndReadAsWords()
    {
        var staged = new[] { ShowActionKind.ScreenStageLook, ShowActionKind.ScreenStagePreset, ShowActionKind.ScreenStagePattern, ShowActionKind.ScreenStageProgram, ShowActionKind.ScreenStageReset, ShowActionKind.ScreenStageLibrary };
        foreach (var kind in staged)
        {
            Assert.True(ActionSpec.IsStaged(kind));
            Assert.Contains(kind, ActionSpec.CueKinds);                // a rehearsal cue may build the preview
            Assert.Equal(TargetKind.Stage, ActionSpec.For(kind).Target); // a screen, a canvas, or the programme
            Assert.StartsWith("Preview —", ActionSpec.Label(kind));
            Assert.False(ActionSpec.ChangesContent(kind));           // never content on air: a video stinger may share the cue
        }
        Assert.False(ActionSpec.IsStaged(ShowActionKind.ScreenPattern));
        Assert.False(ActionSpec.IsStaged(ShowActionKind.ScreenLook));
        Assert.True(ActionSpec.ChangesContent(ShowActionKind.ScreenPattern)); // the live twin is content
        Assert.Equal((TargetKind.Screen, ValueKind.PatternKind), ActionSpec.For(ShowActionKind.ScreenPattern));

        var s = Rig();
        Assert.Equal("PVW of screen 'Stage left' ← look 'Walk-in'", CueSummary.DescribeAction(s, new CueActionConfig { Kind = ShowActionKind.ScreenStageLook, Target = "a", Value = "Walk-in" }));
        Assert.Equal("PVW of the programme ← Grid", CueSummary.DescribeAction(s, new CueActionConfig { Kind = ShowActionKind.ScreenStagePattern, Target = "", Value = "Grid" }));
        Assert.Equal("PVW of the programme ← the look on air", CueSummary.DescribeAction(s, new CueActionConfig { Kind = ShowActionKind.ScreenStageReset, Target = "PGM" }));
        Assert.Equal("PVW of screen 'Stage right' ← the programme", CueSummary.DescribeAction(s, new CueActionConfig { Kind = ShowActionKind.ScreenStageProgram, Target = "b" }));
        Assert.Equal("PVW of screen 'Stage right' ← preset 'Bars'", CueSummary.DescribeAction(s, new CueActionConfig { Kind = ShowActionKind.ScreenStagePreset, Target = "b", Value = "Bars" }));
        Assert.Equal("Screen 'Stage left' → LedWall", CueSummary.DescribeAction(s, new CueActionConfig { Kind = ShowActionKind.ScreenPattern, Target = "a", Value = "LedWall" }));
        // Round 73: a Library tile is a staged verb like the others.
        Assert.Equal("PVW of screen 'Stage right' ← library 'Mandelbrot'", CueSummary.DescribeAction(s, new CueActionConfig { Kind = ShowActionKind.ScreenStageLibrary, Target = "b", Value = "Mandelbrot" }));
        Assert.Equal((TargetKind.Stage, ValueKind.Library), ActionSpec.For(ShowActionKind.ScreenStageLibrary));

        // The sheet's words come back, the short ones included.
        Assert.Equal(ShowActionKind.ScreenStagePattern, CueSheet.ParseKind("PVW pattern"));
        Assert.Equal(ShowActionKind.ScreenStageReset, CueSheet.ParseKind("reset"));
        Assert.Equal(ShowActionKind.ScreenStageLook, CueSheet.ParseKind("stage look"));
        Assert.Equal(ShowActionKind.ScreenStageLibrary, CueSheet.ParseKind("pvw library"));
        Assert.Equal(ShowActionKind.ScreenStageLibrary, CueSheet.ParseKind("library"));
        Assert.Equal(ShowActionKind.ScreenPattern, CueSheet.ParseKind("screen pattern"));
        Assert.Equal(ShowActionKind.ApplyLookToPreview, CueSheet.ParseKind("preview")); // the older word keeps its meaning

        // The programme is blank, PGM or PROGRAM; a screen is a screen.
        Assert.True(ContentTargets.IsProgramTarget(""));
        Assert.True(ContentTargets.IsProgramTarget("pgm"));
        Assert.True(ContentTargets.IsProgramTarget("Programme"));
        Assert.False(ContentTargets.IsProgramTarget("a"));
    }

    [Fact]
    public void TheValidatorReadsAStagedStep()
    {
        var s = Rig();
        var ctx = new CueValidationContext { Presets = new[] { "Bars" } };
        RunCueConfig Cue(ShowActionKind kind, string target, string value = "")
        {
            var cue = new RunCueConfig { Name = kind.ToString() };
            cue.Actions.Add(new CueActionConfig { Kind = kind, Target = target, Value = value });
            return cue;
        }
        Assert.Equal(0, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStageLook, "", "Walk-in"), ctx).BrokenCount);   // the programme
        Assert.Equal(0, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStageLook, "PGM", "Walk-in"), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStageLook, "a", "Walk-in"), ctx).BrokenCount);
        Assert.Equal(1, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStageLook, "zz", "Walk-in"), ctx).BrokenCount);  // not in the rig
        Assert.Equal(1, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStageLook, "a", "Nope"), ctx).BrokenCount);      // no such look
        Assert.Equal(1, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStageLook, "a"), ctx).BrokenCount);              // which look?
        Assert.Equal(1, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStagePattern, "a", "Bogus"), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStagePattern, "", "LED wall"), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStageProgram, "b"), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStageReset, ""), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStagePreset, "a", "Bars"), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStageLibrary, "a", "Mandelbrot"), ctx).BrokenCount);   // found when the cue runs
        Assert.Equal(1, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStageLibrary, "a"), ctx).BrokenCount);                 // which tile?
        Assert.Equal(1, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStageLibrary, "zz", "Mandelbrot"), ctx).BrokenCount);  // not in the rig
        // A preset this machine lacks is a warning, not a broken cue — presets are files beside the show.
        var missing = CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenStagePreset, "a", "Elsewhere"), ctx);
        Assert.Equal(0, missing.BrokenCount);
        Assert.Contains(missing.Issues, i => i.Text.Contains("not in the presets folder"));
        Assert.Equal(1, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenPattern, "a", "Bogus"), ctx).BrokenCount);
        Assert.Equal(1, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenPattern, "zz", "Grid"), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(s, Cue(ShowActionKind.ScreenPattern, "a", "Grid"), ctx).BrokenCount);
    }

    [Fact]
    public void OscAndTheWireCarryTheStagedWords()
    {
        var lines = new (string Address, string? Arg, string Line, ShowActionKind Kind)[]
        {
            ("/patterns/screen/2/pvw/look", "Walk-in", "SCREEN 2 PVW LOOK Walk-in", ShowActionKind.ScreenStageLook),
            ("/patterns/screen/2/pvw/preset/Grid", null, "SCREEN 2 PVW PRESET Grid", ShowActionKind.ScreenStagePreset),
            ("/patterns/screen/2/preview/pattern/LED/wall", null, "SCREEN 2 PVW PATTERN LED wall", ShowActionKind.ScreenStagePattern),
            ("/patterns/screen/2/pvw/program", null, "SCREEN 2 PVW PROGRAM", ShowActionKind.ScreenStageProgram),
            ("/patterns/screen/2/pvw/reset", null, "SCREEN 2 PVW RESET", ShowActionKind.ScreenStageReset),
            ("/patterns/screen/2/pvw", null, "SCREEN 2 PVW", ShowActionKind.ScreenToPreview),
            ("/patterns/screen/2/pattern/LED/wall", null, "SCREEN 2 PATTERN LED wall", ShowActionKind.ScreenPattern),
            ("/patterns/screen/2/pattern", "Grid", "SCREEN 2 PATTERN Grid", ShowActionKind.ScreenPattern),
            ("/patterns/pvw/pattern", "Grid", "PVW PATTERN Grid", ShowActionKind.ScreenStagePattern),
            ("/patterns/pvw/preset/Walk-in", null, "PVW PRESET Walk-in", ShowActionKind.ScreenStagePreset),
            ("/patterns/pvw/look", "Walk-in", "PVW LOOK Walk-in", ShowActionKind.ApplyLookToPreview),
            ("/patterns/pvw/reset", null, "PVW RESET", ShowActionKind.ScreenStageReset),
            ("/patterns/pvw/program", null, "PVW PROGRAM", ShowActionKind.ScreenToPreview),
            ("/patterns/preview", null, "PVW", ShowActionKind.ScreenToPreview),
            // Round 73: a Library tile, on a screen's PVW, on the programme's, or on the desk's editing target (FOCUSED).
            ("/patterns/screen/2/pvw/library/Mandelbrot", null, "SCREEN 2 PVW LIBRARY Mandelbrot", ShowActionKind.ScreenStageLibrary),
            ("/patterns/pvw/library", "Mandelbrot", "PVW LIBRARY Mandelbrot", ShowActionKind.ScreenStageLibrary),
            ("/patterns/library", "Mandelbrot", "LIBRARY Mandelbrot", ShowActionKind.ScreenStageLibrary),
            ("/patterns/library/Walk-in", null, "LIBRARY Walk-in", ShowActionKind.ScreenStageLibrary),
        };
        foreach (var (address, arg, line, kind) in lines)
        {
            var message = arg is null ? OscMessage.Of(address) : OscMessage.Of(address, arg);
            Assert.Equal(line, OscMap.ToLine(message));
            var cmd = ControlProtocol.Parse(line);
            Assert.True(cmd.IsAction, line);
            Assert.Equal(kind, cmd.Action.Kind);
        }
        // A PVW with words the desk does not know is a refusal, never a screen toggle.
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN 2 PVW DANCE").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN 2 PVW LOOK").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("PVW WHATEVER").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN 2 PATTERN").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("LIBRARY").Kind);               // a bare LIBRARY names nothing
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN 2 PVW LIBRARY").Kind);
        Assert.Equal("FOCUSED", ControlProtocol.Parse("LIBRARY Mandelbrot").Action.Target);           // the desk's editing target
        Assert.Equal("", ControlProtocol.Parse("PVW LIBRARY Mandelbrot").Action.Target);              // the programme
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/library"));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/screen/2/pvw/dance")));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/screen/<n>/pvw/look"));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/screen/<n>/pattern"));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/pvw/pattern"));
    }
}
