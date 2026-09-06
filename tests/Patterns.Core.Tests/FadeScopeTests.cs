using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 15: a fade to black on one screen or a group while the rest of the rig keeps its picture —
/// the scope's words, the verb and the OSC address that carry them, the cue action, the gain table
/// that takes the programme's sound with a fade that darkens the whole rig, and the engine drawing
/// a target black on its own with only that sink's crossfade running.
/// </summary>
public class FadeScopeTests
{
    [Fact]
    public void TheScopeWordsParseBothWaysAndJunkIsNull()
    {
        Assert.Equal(FadeScope.Everything, FadeScope.Parse(""));
        Assert.Equal(FadeScope.Everything, FadeScope.Parse(null));
        Assert.Equal(FadeScope.Everything, FadeScope.Parse(" all "));
        Assert.Equal(FadeScope.Focused, FadeScope.Parse("FOCUSED"));
        Assert.Equal(FadeScope.Focused, FadeScope.Parse("current"));
        Assert.Equal(FadeScope.Ticked, FadeScope.Parse("ticked"));
        Assert.Equal(FadeScope.Ticked, FadeScope.Parse("SELECTED"));
        Assert.Equal(FadeScope.Groups, FadeScope.Parse("GROUPS"));
        Assert.Equal(FadeScope.Groups, FadeScope.Parse("canvases"));
        Assert.Equal(new FadeScope(FadeScopeKind.Screen, "2"), FadeScope.Parse("SCREEN 2"));
        Assert.Equal(new FadeScope(FadeScopeKind.Screen, "12"), FadeScope.Parse("screen  12"));
        Assert.Equal(new FadeScope(FadeScopeKind.Group, "A"), FadeScope.Parse("group a"));
        Assert.Equal(new FadeScope(FadeScopeKind.Group, "B"), FadeScope.Parse("CANVAS B"));
        Assert.Equal(new FadeScope(FadeScopeKind.Target, "a+b"), FadeScope.Parse("a+b"));
        Assert.Equal(new FadeScope(FadeScopeKind.Target, "DISPLAY1"), FadeScope.Parse("ID DISPLAY1"));
        Assert.Equal(new FadeScope(FadeScopeKind.Target, "b"), FadeScope.Parse("target b"));

        // Words that mean nothing never fade the wrong thing — a bare word is not an id (only a canvas key is).
        Assert.Null(FadeScope.Parse("slowly"));
        Assert.Null(FadeScope.Parse("DISPLAY1"));
        Assert.Null(FadeScope.Parse("ID"));
        Assert.Null(FadeScope.Parse("ID a b"));
        Assert.Null(FadeScope.Parse("SCREEN"));
        Assert.Null(FadeScope.Parse("SCREEN x"));
        Assert.Null(FadeScope.Parse("SCREEN 0"));
        Assert.Null(FadeScope.Parse("SCREEN -2"));
        Assert.Null(FadeScope.Parse("GROUP AB"));
        Assert.Null(FadeScope.Parse("GROUP 1"));
        Assert.Null(FadeScope.Parse("two words"));
        Assert.Null(FadeScope.Parse("ALL screens"));

        // The words back as the wire writes them, and the labels for a sentence.
        Assert.Equal("", FadeScope.Everything.Words);
        Assert.Equal("FOCUSED", FadeScope.Focused.Words);
        Assert.Equal("TICKED", FadeScope.Ticked.Words);
        Assert.Equal("GROUPS", FadeScope.Groups.Words);
        Assert.Equal("SCREEN 2", FadeScope.Parse("screen 2")!.Value.Words);
        Assert.Equal("GROUP A", FadeScope.Parse("canvas a")!.Value.Words);
        Assert.Equal("a+b", FadeScope.Parse("a+b")!.Value.Words);
        Assert.Equal("ID DISPLAY1", FadeScope.Parse("id DISPLAY1")!.Value.Words);
        Assert.Equal("every screen", FadeScope.Everything.Label);
        Assert.Equal("the focused screen", FadeScope.Focused.Label);
        Assert.Equal("the ticked screens", FadeScope.Ticked.Label);
        Assert.Equal("the ticked groups", FadeScope.Groups.Label);
        Assert.Equal("screen 2", FadeScope.Parse("SCREEN 2")!.Value.Label);
        Assert.Equal("group A", FadeScope.Parse("group a")!.Value.Label);
        Assert.True(FadeScope.Everything.IsEverything);
        Assert.False(FadeScope.Focused.IsEverything);
        foreach (var words in new[] { "", "FOCUSED", "TICKED", "GROUPS", "SCREEN 3", "GROUP C", "a+b", "ID DISPLAY1" })
        {
            Assert.Equal(words, FadeScope.Parse(words)!.Value.Words); // a round trip
        }
    }

    [Fact]
    public void TheVerbTakesTheSecondsAndThePlaceInEitherOrder()
    {
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 2000, "SCREEN 2"), ControlProtocol.Parse("FADE 2 SCREEN 2"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 0, "SCREEN 2"), ControlProtocol.Parse("FADE SCREEN 2"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 1500, "SCREEN 2"), ControlProtocol.Parse("fade screen 2 1.5"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 0, "GROUP A"), ControlProtocol.Parse("FADE GROUP A"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 500, "GROUP A"), ControlProtocol.Parse("FADE 500ms canvas a"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 0, "FOCUSED"), ControlProtocol.Parse("FADE FOCUSED"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 0, "GROUPS"), ControlProtocol.Parse("FADE DOWN GROUPS"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 3000, "TICKED"), ControlProtocol.Parse("FADE BLACK 3 TICKED"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 0, "a+b"), ControlProtocol.Parse("FADE a+b"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 2000, "ID DISPLAY1"), ControlProtocol.Parse("FADE 2 ID DISPLAY1"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeUp, 2000, "TICKED"), ControlProtocol.Parse("FADE UP TICKED 2"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeUp, 3000, "FOCUSED"), ControlProtocol.Parse("FADEUP 3 FOCUSED"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeUp, 0, "SCREEN 4"), ControlProtocol.Parse("FADE IN SCREEN 4"));

        // The old forms read exactly as they did: the rig, with or without seconds.
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 0, ""), ControlProtocol.Parse("FADE"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 2000, ""), ControlProtocol.Parse("FADE 2"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeUp, 3000, ""), ControlProtocol.Parse("FADEUP 3s"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeUp, 0, ""), ControlProtocol.Parse("FADE UP"));

        // Words that mean nothing are refused, never a fade of the wrong thing.
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("FADE slowly").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("FADE SCREEN").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("FADE 2 SCREEN").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("FADE SCREEN 2 GROUP A").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("FADE UP 2 3 4").Kind);

        Assert.True(ControlProtocol.TryParseFadeWords("", out var ms, out var scope) && ms == 0 && scope.IsEverything);
        Assert.True(ControlProtocol.TryParseFadeWords("2", out ms, out scope) && ms == 2000 && scope.IsEverything);
        Assert.True(ControlProtocol.TryParseFadeWords("SCREEN 2", out ms, out scope) && ms == 0 && scope.Kind == FadeScopeKind.Screen && scope.Arg == "2");
        Assert.True(ControlProtocol.TryParseFadeWords("SCREEN 2 2", out ms, out scope) && ms == 2000 && scope.Kind == FadeScopeKind.Screen);
        Assert.True(ControlProtocol.TryParseFadeWords("2 SCREEN 2", out ms, out scope) && ms == 2000 && scope.Kind == FadeScopeKind.Screen);
        Assert.False(ControlProtocol.TryParseFadeWords("2 2 SCREEN 2", out _, out _));
    }

    [Fact]
    public void TheOscAddressesCarryThePlace()
    {
        Assert.Equal("FADE 2 SCREEN 2", OscMap.ToLine(OscMessage.Of("/patterns/fade/screen/2", 2)));
        Assert.Equal("FADE SCREEN 3", OscMap.ToLine(OscMessage.Of("/patterns/fade/screen/3")));
        Assert.Equal("FADE 1.5 SCREEN 3", OscMap.ToLine(OscMessage.Of("/patterns/fade/screen/3/1.5")));
        Assert.Equal("FADEUP GROUP A", OscMap.ToLine(OscMessage.Of("/patterns/fade/up/group/A")));
        Assert.Equal("FADEUP 2 GROUP B", OscMap.ToLine(OscMessage.Of("/patterns/fade/up/canvas/B", 2)));   // a canvas is a group, in the wire's own words
        Assert.Equal("FADE 1.5 FOCUSED", OscMap.ToLine(OscMessage.Of("/patterns/fade/focused", 1.5)));
        Assert.Equal("FADE 2 TICKED", OscMap.ToLine(OscMessage.Of("/patterns/fade/ticked/2")));
        Assert.Equal("FADE GROUPS", OscMap.ToLine(OscMessage.Of("/patterns/fade/down/groups")));
        Assert.Equal("FADE SCREEN 2", OscMap.ToLine(OscMessage.Of("/patterns/fade", "SCREEN 2")));
        Assert.Equal("FADEUP GROUP A", OscMap.ToLine(OscMessage.Of("/patterns/fade/up", "group a")));
        Assert.Equal("FADE 2 FOCUSED", OscMap.ToLine(OscMessage.Of("/patterns/fade/2", "FOCUSED")));

        // The old addresses read exactly as they did.
        Assert.Equal("FADE 2", OscMap.ToLine(OscMessage.Of("/patterns/fade", 2)));
        Assert.Equal("FADE", OscMap.ToLine(OscMessage.Of("/patterns/fade")));
        Assert.Equal("FADEUP 3", OscMap.ToLine(OscMessage.Of("/patterns/fade/up", 3)));
        Assert.Equal("FADE 1.5", OscMap.ToLine(OscMessage.Of("/patterns/fade/down/1.5")));

        // Nonsense is no line at all.
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/fade/screen")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/fade/sideways")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/fade", "SCREEN")));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/fade/screen"));

        // The feedback: how many screens are black on their own, their names, the sound down with them.
        var fed = OscFeedback.FromState("{\"blackout\":false,\"black\":{\"count\":2,\"text\":\"Screen 2 · A · Main wall\",\"audio\":true}}");
        Assert.Equal(2, Assert.Single(fed, x => x.Address == "/patterns/state/black/count").Args[0]);
        Assert.Equal("Screen 2 · A · Main wall", Assert.Single(fed, x => x.Address == "/patterns/state/black/text").Args[0]);
        Assert.Equal(1, Assert.Single(fed, x => x.Address == "/patterns/state/black/audio").Args[0]);
        Assert.Equal(0, Assert.Single(fed, x => x.Address == "/patterns/state/blackout").Args[0]);
        Assert.DoesNotContain(OscFeedback.FromState("{\"blackout\":true}"), x => x.Address.StartsWith("/patterns/state/black/"));
    }

    [Fact]
    public void TheCueActionKnowsItsPlaceAndItsSeconds()
    {
        Assert.Contains(ShowActionKind.FadeToBlack, ActionSpec.CueKinds);
        Assert.Contains(ShowActionKind.FadeUp, ActionSpec.CueKinds);
        Assert.Equal((TargetKind.Place, ValueKind.Seconds), ActionSpec.For(ShowActionKind.FadeToBlack));
        Assert.Equal((TargetKind.Place, ValueKind.Seconds), ActionSpec.For(ShowActionKind.FadeUp));
        Assert.StartsWith("Fade to black", ActionSpec.Label(ShowActionKind.FadeToBlack));
        Assert.StartsWith("Fade up", ActionSpec.Label(ShowActionKind.FadeUp));
        Assert.Equal(ShowActionKind.FadeToBlack, CueSheet.ParseKind("ftb"));
        Assert.Equal(ShowActionKind.FadeToBlack, CueSheet.ParseKind("Fade to black"));
        Assert.Equal(ShowActionKind.FadeToBlack, CueSheet.ParseKind("fade"));
        Assert.Equal(ShowActionKind.FadeUp, CueSheet.ParseKind("fade up"));
        Assert.Equal(ShowActionKind.FadeUp, CueSheet.ParseKind("fadein"));

        var state = new ShowState();
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", CustomLabel = "Stage left" });
        Assert.Equal("Fade to black — every screen", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.FadeToBlack }));
        Assert.Equal("Fade to black — screen 2 over 2 s", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.FadeToBlack, Target = "SCREEN 2", Value = "2" }));
        Assert.Equal("Fade up — group A", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.FadeUp, Target = "GROUP A" }));
        Assert.Equal("Fade to black — the ticked screens over 1.5 s", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.FadeToBlack, Target = "TICKED", Value = "1.5" }));
        Assert.Equal("Fade to black — Stage left", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.FadeToBlack, Target = "ID b" }));
        Assert.Equal("Fade up — 'no such place'?", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.FadeUp, Target = "no such place" }));
        Assert.Equal("Fade up — 'b'?", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.FadeUp, Target = "b" }));

        // The checks: a place that is not one, a screen that is not in the rig, seconds that are not.
        var ctx = new CueValidationContext { FileExists = _ => true };
        RunCueConfig Cue(ShowActionKind kind, string target, string value)
        {
            var cue = new RunCueConfig { Number = "1", Name = "Fade" };
            cue.Actions.Add(new CueActionConfig { Kind = kind, Target = target, Value = value });
            return cue;
        }
        Assert.Equal(0, CueValidator.ValidateOne(state, Cue(ShowActionKind.FadeToBlack, "", ""), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(state, Cue(ShowActionKind.FadeToBlack, "SCREEN 2", "2"), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(state, Cue(ShowActionKind.FadeUp, "GROUP A", "0.5"), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(state, Cue(ShowActionKind.FadeUp, "ID b", ""), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(state, Cue(ShowActionKind.FadeToBlack, "TICKED", ""), ctx).BrokenCount);
        var badPlace = Cue(ShowActionKind.FadeToBlack, "SCREEN", "");
        Assert.Contains("not a place to fade", CueValidator.ValidateOne(state, badPlace, ctx).ReasonFor(badPlace.Id));
        var bareWord = Cue(ShowActionKind.FadeToBlack, "b", "");
        Assert.Contains("not a place to fade", CueValidator.ValidateOne(state, bareWord, ctx).ReasonFor(bareWord.Id));
        var gone = Cue(ShowActionKind.FadeToBlack, "ID zz", "");
        Assert.Contains("not in the rig", CueValidator.ValidateOne(state, gone, ctx).ReasonFor(gone.Id));
        var slow = Cue(ShowActionKind.FadeUp, "", "slowly");
        Assert.Contains("not a number of seconds", CueValidator.ValidateOne(state, slow, ctx).ReasonFor(slow.Id));

        // A sheet: the place as the wire writes it, a screen by its label, nothing for every screen; nonsense is a note and the cue still lands.
        var csv = "Number,Name,Action,Target,Value\n1,Out,Fade to black,Stage left,2\n2,Up,fade up,screen 2,\n3,All,ftb,,\n4,Bad,fade,nowhere,\n";
        var imported = CueSheet.Import(CsvTable.Parse(csv), state);
        Assert.Equal(4, imported.Cues.Count);
        Assert.Equal(("ID b", "2"), (imported.Cues[0].Actions[0].Target, imported.Cues[0].Actions[0].Value));
        Assert.Equal((ShowActionKind.FadeUp, "SCREEN 2"), (imported.Cues[1].Actions[0].Kind, imported.Cues[1].Actions[0].Target));
        Assert.Equal("", imported.Cues[2].Actions[0].Target);
        Assert.Contains(imported.Notes, n => n.Contains("not a place to fade"));

        // Exported, the screen's id goes out as its label and the scope's words as they are.
        var stack = new CueStackConfig { Name = "Caller" };
        stack.Cues.Add(imported.Cues[0]);
        stack.Cues.Add(imported.Cues[1]);
        var back = CsvTable.Parse(CueSheet.Export(state, stack));
        Assert.Equal("Stage left", back.Get(0, "Target"));
        Assert.Equal("SCREEN 2", back.Get(1, "Target"));
    }

    [Fact]
    public void TheGainTableTakesTheProgrammeDownWithTheBlack()
    {
        // The music and a clip's soundtrack follow the black; a VOG and a stinger's own sound play through.
        var half = new GainInputs(false, 30, 1, 1, 0.5);
        Assert.Equal(0.5, GainRules.For(AudioBus.Music, half));
        Assert.Equal(0.5, GainRules.For(AudioBus.ClipAudio, half));
        Assert.Equal(1.0, GainRules.For(AudioBus.StingSound, half));
        Assert.Equal(1.0, GainRules.For(AudioBus.VogSound, half));
        var dark = new GainInputs(false, 30, 1, 1, 0);
        Assert.Equal(0.0, GainRules.For(AudioBus.Music, dark));
        Assert.Equal(0.0, GainRules.For(AudioBus.ClipAudio, dark));
        Assert.Equal(1.0, GainRules.For(AudioBus.StingSound, dark));
        // Combined with a duck and a sting ramp: the black multiplies what the others leave.
        Assert.Equal(0.3 * 0.5, GainRules.For(AudioBus.Music, new GainInputs(true, 30, 1, 1, 0.5)), 6);
        Assert.Equal(0.4 * 0.5, GainRules.For(AudioBus.Music, new GainInputs(false, 30, 0.4, 1, 0.5)), 6);
        Assert.Equal(0.3 * 0.5, GainRules.For(AudioBus.ClipAudio, new GainInputs(true, 30, 1, 1, 0.5)), 6);
        // Clamped, and 1 by default — every caller that never heard of the black reads as before.
        Assert.Equal(1.0, GainRules.For(AudioBus.Music, new GainInputs(false, 30, 1, 1, 7)));
        Assert.Equal(0.0, GainRules.For(AudioBus.Music, new GainInputs(false, 30, 1, 1, -1)));
        Assert.Equal(1.0, GainRules.For(AudioBus.Music, new GainInputs(false, 30, 1)));
    }

    private static ShowState Flat(string color) => RenderTestHarness.State(s =>
    {
        s.Pattern.Kind = PatternKind.FlatField;
        s.Pattern.FlatField.Color = color;
        s.Pattern.FlatField.ShowLabel = false;
        s.Pattern.Canvas.FollowOutput = true;
        s.Transition.Enabled = false;
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "a" });
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "b" });
    });

    private static SKBitmap Render(PatternEngine engine, SinkState sink, ShowSnapshot snap, string? screenId, double time = 1.0)
    {
        var info = new SKImageInfo(64, 32, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(64, 32),
            ReferenceSize = new SKSizeI(64, 32),
            Time = time,
            Now = new DateTime(2026, 8, 29, 12, 0, 0),
            UtcNow = RenderTestHarness.FixedUtcNow,
            Sink = SinkKind.Output,
            SinkIndex = 1,
            SinkLabel = "t",
            ScreenId = screenId,
        };
        engine.Render(surface.Canvas, snap, in ctx, sink);
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    private static bool Red(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;
    private static bool Black(SKColor c) => c.Red < 20 && c.Green < 20 && c.Blue < 20;

    [Fact]
    public void ATargetBlackOnItsOwnDrawsBlackAndOnlyItsSinkFades()
    {
        var engine = new PatternEngine();
        using var left = new SinkState();
        using var right = new SinkState();

        var lit = new ShowSnapshot { State = Flat("#FF0000"), Version = 1 };
        Assert.False(lit.IsBlack("a"));
        Assert.False(lit.IsBlack(null));
        using (var l = Render(engine, left, lit, "a")) Assert.True(Red(l.GetPixel(32, 16)));
        using (var r = Render(engine, right, lit, "b")) Assert.True(Red(r.GetPixel(32, 16)));

        // Screen b faded to black on its own: b draws black, a keeps its red, the program (null) is never black.
        var partly = new ShowSnapshot { State = Flat("#FF0000"), Version = 2, BlackTargets = new[] { "b" } };
        Assert.True(partly.IsBlack("b"));
        Assert.False(partly.IsBlack("a"));
        Assert.False(partly.IsBlack(null));
        using (var l = Render(engine, left, partly, "a", 2.0)) Assert.True(Red(l.GetPixel(32, 16)), "the lit screen keeps its picture");
        using (var r = Render(engine, right, partly, "b", 2.0)) Assert.True(Black(r.GetPixel(32, 16)), "the faded screen is black");
        using (var p = Render(engine, left, partly, null, 2.0)) Assert.True(Red(p.GetPixel(32, 16)), "the program bus is never black on its own");

        // Only the faded sink's content identity changes — its crossfade runs, the other's does not.
        Assert.NotEqual(lit.TransitionKeyFor("b"), partly.TransitionKeyFor("b"));
        Assert.Equal(lit.TransitionKeyFor("a"), partly.TransitionKeyFor("a"));
        Assert.Equal(lit.TransitionKeyFor(null), partly.TransitionKeyFor(null));

        // A blackout still covers everything, black on its own or not.
        var blackState = Flat("#FF0000");
        blackState.Blackout = true;
        var all = new ShowSnapshot { State = blackState, Version = 3, BlackTargets = new[] { "b" } };
        using (var l = Render(engine, left, all, "a", 3.0)) Assert.True(Black(l.GetPixel(32, 16)));
        using (var r = Render(engine, right, all, "b", 3.0)) Assert.True(Black(r.GetPixel(32, 16)));

        // A frozen output faded to black on its own goes black — the freeze holds a picture, not a fade.
        var frozen = new ShowSnapshot { State = Flat("#FF0000"), Version = 4, Frozen = true, BlackTargets = new[] { "b" } };
        using (var r = Render(engine, right, frozen, "b", 4.0)) Assert.True(Black(r.GetPixel(32, 16)));
    }
}
