using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 24: how one picture becomes the next. A desk that only dissolves has one answer for every
/// change; these are the others — a dip through a colour, a wipe, a push, a reactive scene used as
/// the matte, and the show's own brand swept over the cut as a stinger.
///
/// The tests that matter most here are the render ones, and they run on a raster surface with no
/// graphics card attached — the path the stream, NDI and the thumbnails take. A transition the
/// stream cannot draw is a transition that shows the client something different from the wall, so
/// every kind is proved complete at both ends and different in the middle on exactly that path.
/// </summary>
public class TransitionKindTests
{
    // ---- the curves ---------------------------------------------------------------------------

    [Fact]
    public void TheCurvesHaveTheShapeAScreenNeeds()
    {
        // Nothing on a screen starts or stops moving abruptly.
        Assert.Equal(0, Transitions.Ease(0), 6);
        Assert.Equal(1, Transitions.Ease(1), 6);
        Assert.Equal(0.5, Transitions.Ease(0.5), 6);
        Assert.Equal(0, Transitions.Ease(-3), 6);   // clamped, never past the ends
        Assert.Equal(1, Transitions.Ease(9), 6);
        var last = -1.0;
        for (var i = 0; i <= 20; i++)
        {
            var v = Transitions.Ease(i / 20.0);
            Assert.True(v >= last, "the ease never goes backwards");
            last = v;
        }

        // A cover is nothing at either end and complete across the middle. The plateau is the
        // point: the cut happens under it, and a cover that is only briefly whole shows a frame
        // of the switch.
        Assert.Equal(0, Transitions.Cover(0), 6);
        Assert.Equal(0, Transitions.Cover(1), 6);
        Assert.Equal(1, Transitions.Cover(0.5), 6);
        Assert.Equal(1, Transitions.Cover(0.4), 6);
        Assert.Equal(1, Transitions.Cover(0.6), 6);
        Assert.True(Transitions.Cover(0.2) is > 0 and < 1);

        // Two kinds hide the cut behind a cover; the rest work on the outgoing picture.
        Assert.True(Transitions.CoversTheCut(TransitionKind.Dip));
        Assert.True(Transitions.CoversTheCut(TransitionKind.BrandStinger));
        Assert.False(Transitions.CoversTheCut(TransitionKind.Dissolve));
        Assert.False(Transitions.CoversTheCut(TransitionKind.Wipe));
        Assert.False(Transitions.CoversTheCut(TransitionKind.Push));
        Assert.False(Transitions.CoversTheCut(TransitionKind.Reactive));

        // Under a cover the picture switches at the halfway point, where the cover is whole.
        Assert.True(Transitions.ShowsOutgoing(0));
        Assert.True(Transitions.ShowsOutgoing(0.49));
        Assert.False(Transitions.ShowsOutgoing(0.5));
        Assert.False(Transitions.ShowsOutgoing(1));
    }

    [Fact]
    public void APushMovesTheWholePictureTheWayItWasAsked()
    {
        var size = new SKSizeI(1920, 1080);
        Assert.Equal((1920f, 0f), Transitions.PushBy(TransitionDirection.Right, size, 1));
        Assert.Equal((-1920f, 0f), Transitions.PushBy(TransitionDirection.Left, size, 1));
        Assert.Equal((0f, -1080f), Transitions.PushBy(TransitionDirection.Up, size, 1));
        Assert.Equal((0f, 1080f), Transitions.PushBy(TransitionDirection.Down, size, 1));
        // Nothing moves at rest, and half a push is half the screen.
        Assert.Equal((0f, 0f), Transitions.PushBy(TransitionDirection.Right, size, 0));
        Assert.Equal((960f, 0f), Transitions.PushBy(TransitionDirection.Right, size, 0.5));
    }

    [Fact]
    public void ADipTakesItsColourFromTheBrandOrTheShow()
    {
        var cfg = new TransitionConfig();
        Assert.True(cfg.DipUsesBrand);
        Assert.Equal(new SKColor(0x11, 0x22, 0x33), Transitions.DipColorFor(cfg, new SKColor(0x11, 0x22, 0x33)));

        cfg.DipUsesBrand = false;
        cfg.DipColor = "#FFFFFF";
        Assert.Equal(SKColors.White, Transitions.DipColorFor(cfg, SKColors.Red));
        cfg.DipColor = "not a colour";
        Assert.Equal(SKColors.Black, Transitions.DipColorFor(cfg, SKColors.Red)); // never a guess

        // Luminance is what decides whether a dip is a light change the room has to be protected from.
        Assert.Equal(1f, Transitions.Luma(SKColors.White), 3);
        Assert.Equal(0f, Transitions.Luma(SKColors.Black), 3);
        Assert.True(Transitions.Luma(SKColors.White) > Transitions.BrightDip);
        Assert.True(Transitions.Luma(SKColors.Black) < Transitions.BrightDip);
        Assert.True(Transitions.Luma(new SKColor(0x0A, 0x0A, 0x14)) < Transitions.BrightDip); // the desk's own dark
    }

    // ---- the reactive matte -------------------------------------------------------------------

    [Fact]
    public void AMatteIsTheSamePictureEveryTimeAndCostsTheSameAtEverySize()
    {
        // Small, and the picture's own shape so nothing in it is stretched.
        Assert.Equal(new SKSizeI(256, 144), Transitions.MatteSize(new SKSizeI(1920, 1080)));
        Assert.Equal(new SKSizeI(256, 144), Transitions.MatteSize(new SKSizeI(3840, 2160))); // a 4K wall costs no more
        Assert.Equal(new SKSizeI(256, 256), Transitions.MatteSize(new SKSizeI(1000, 1000)));
        var tiny = Transitions.MatteSize(new SKSizeI(8, 4));
        Assert.Equal(8, tiny.Width);
        Assert.True(tiny.Height >= 2); // never a zero-height buffer

        // Two sinks arming the same change a frame apart must wipe with the same picture.
        var a = Transitions.Matte(ReactiveScene.Plasma, new SKSizeI(64, 36), 2.5);
        var b = Transitions.Matte(ReactiveScene.Plasma, new SKSizeI(64, 36), 2.5);
        Assert.Equal(a, b);
        Assert.Equal(64 * 36, a.Length);

        // A scene is a picture, not a flat field — otherwise the wipe has no shape.
        Assert.True(a.Max() - a.Min() > 40, "the matte has range to wipe with");
        foreach (var scene in Enum.GetValues<ReactiveScene>())
        {
            var field = Transitions.Matte(scene, new SKSizeI(64, 36), 1.0);
            Assert.Equal(64 * 36, field.Length);
            Assert.True(field.Max() - field.Min() > 20, $"{scene} has range");
        }
        Assert.NotEqual(
            Transitions.Matte(ReactiveScene.Plasma, new SKSizeI(64, 36), 1.0),
            Transitions.Matte(ReactiveScene.Vortex, new SKSizeI(64, 36), 1.0));
    }

    [Fact]
    public void AMatteKeepsTheWholePictureAtTheStartAndNoneOfItAtTheEnd()
    {
        var field = Transitions.Matte(ReactiveScene.Kaleidoscope, new SKSizeI(32, 18), 0.75);
        var pixels = new int[field.Length];

        Transitions.MatteAt(field, pixels, 0, 0.18);
        foreach (var p in pixels) Assert.Equal(0xFFFFFFFFu, (uint)p); // whole, and premultiplied white

        Transitions.MatteAt(field, pixels, 1, 0.18);
        foreach (var p in pixels) Assert.Equal(0u, (uint)p); // gone

        // In between it is a mixture, and every pixel stays premultiplied — a mask with a colour
        // channel above its alpha would tint the picture it is masking.
        Transitions.MatteAt(field, pixels, 0.5, 0.18);
        var open = 0;
        foreach (var p in pixels)
        {
            var a = (uint)p >> 24;
            Assert.Equal(a, ((uint)p >> 16) & 0xFF);
            Assert.Equal(a, ((uint)p >> 8) & 0xFF);
            Assert.Equal(a, (uint)p & 0xFF);
            if (a < 128) open++;
        }
        Assert.True(open > 0 && open < pixels.Length, "half way through, the wipe is half way through");

        // A hard edge is allowed to be asked for and still ends completely.
        Transitions.MatteAt(field, pixels, 1, 0);
        foreach (var p in pixels) Assert.Equal(0u, (uint)p);
    }

    // ---- the words ----------------------------------------------------------------------------

    [Fact]
    public void TheWordsOnACueSheetNameATransition()
    {
        Assert.True(ActionSpec.TryParseTransition("wipe 800", out var cut, out var ms, out var kind, out var scene, out var way));
        Assert.False(cut);
        Assert.Equal(800, ms);
        Assert.Equal(TransitionKind.Wipe, kind);
        Assert.Null(scene);
        Assert.Null(way);

        // The order the operator writes them in is the operator's business.
        Assert.True(ActionSpec.TryParseTransition("1200 reactive", out _, out ms, out kind, out _, out _));
        Assert.Equal(1200, ms);
        Assert.Equal(TransitionKind.Reactive, kind);

        Assert.True(ActionSpec.TryParseTransition("reactive vortex 1200", out _, out ms, out kind, out scene, out _));
        Assert.Equal(1200, ms);
        Assert.Equal(TransitionKind.Reactive, kind);
        Assert.Equal(ReactiveScene.Vortex, scene);

        // Naming a scene is asking for the scene to wipe with.
        Assert.True(ActionSpec.TryParseTransition("starwarp", out _, out _, out kind, out scene, out _));
        Assert.Equal(TransitionKind.Reactive, kind);
        Assert.Equal(ReactiveScene.StarWarp, scene);

        // The way it travels, with or without a kind in front of it.
        Assert.True(ActionSpec.TryParseTransition("wipe left 600", out _, out ms, out kind, out _, out way));
        Assert.Equal(600, ms);
        Assert.Equal(TransitionKind.Wipe, kind);
        Assert.Equal(TransitionDirection.Left, way);
        Assert.True(ActionSpec.TryParseTransition("down", out _, out _, out kind, out _, out way));
        Assert.Null(kind); // the show's own transition, travelling the way this cue asked
        Assert.Equal(TransitionDirection.Down, way);

        // The names an operator actually writes.
        Assert.Equal(TransitionKind.Dip, ActionSpec.TransitionWord("dip"));
        Assert.Equal(TransitionKind.Dip, ActionSpec.TransitionWord("DTB"));
        Assert.Equal(TransitionKind.BrandStinger, ActionSpec.TransitionWord("stinger"));
        Assert.Equal(TransitionKind.Push, ActionSpec.TransitionWord("slide"));
        Assert.Equal(TransitionKind.Dissolve, ActionSpec.TransitionWord("mix"));
        Assert.Null(ActionSpec.TransitionWord("banana"));
        Assert.Equal(ReactiveScene.Kaleidoscope, ActionSpec.SceneWord("kaleido"));
        Assert.Equal(ReactiveScene.Tunnel, ActionSpec.SceneWord("TUNNEL"));

        // Blank is the show's own, and "cut" still means cut.
        Assert.True(ActionSpec.TryParseTransition("", out cut, out ms, out kind, out scene, out way));
        Assert.False(cut);
        Assert.Equal(-1, ms);
        Assert.Null(kind);
        Assert.True(ActionSpec.TryParseTransition("cut", out cut, out _, out _, out _, out _));
        Assert.True(cut);

        // A typo is refused rather than guessed at: the checks catch it, not the wall.
        Assert.False(ActionSpec.TryParseTransition("wype", out _, out _, out _, out _, out _));
        Assert.False(ActionSpec.TryParseTransition("wipe sideways", out _, out _, out _, out _, out _));
        Assert.False(ActionSpec.TryParseTransition("-40", out _, out _, out _, out _, out _));

        // The old three-argument reading still answers the same for what it knew about.
        Assert.True(ActionSpec.TryParseTransition("cut", out cut, out ms));
        Assert.True(cut);
        Assert.True(ActionSpec.TryParseTransition("450", out _, out ms));
        Assert.Equal(450, ms);
    }

    [Fact]
    public void TheSheetAndTheChecksBothSpeakTheseWords()
    {
        var state = new ShowState();
        var look = new LookConfig { Name = "Walk-in", Json = "{}" };
        state.LooksAndCues.Looks.Add(look);
        string Sheet(string value) => CueSummary.DescribeAction(state, new CueActionConfig
        {
            Kind = ShowActionKind.ApplyLook,
            Target = look.Id,
            Value = value,
        });

        // What the operator wrote is what the printed sheet says back.
        Assert.Contains("(cut)", Sheet("cut"));
        Assert.Contains("(800 ms)", Sheet("800"));
        Assert.Contains("(wipe right to left, 600 ms)", Sheet("wipe left 600"));
        Assert.Contains("(brand stinger)", Sheet("stinger"));
        Assert.Contains("(reactive vortex, 1200 ms)", Sheet("reactive vortex 1200"));
        Assert.Contains("(dip)", Sheet("dip"));

        // And a word the desk cannot read is a hard stop in the checks, before the show, with the
        // words that would have worked printed beside it.
        var cue = new RunCueConfig { Number = "1", Name = "Go" };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ApplyLook, Target = look.Id, Value = "wype" });
        var stack = new CueStackConfig { Name = "Main" };
        stack.Cues.Add(cue);
        var reason = CueValidator.Validate(state, stack, new CueValidationContext { FileExists = _ => true }).ReasonFor(cue.Id);
        Assert.NotNull(reason);
        Assert.Contains("wype", reason);
        Assert.Contains("wipe", reason); // and the words that would have worked

        // The ones that do work are not flagged.
        foreach (var good in new[] { "cut", "800", "wipe left 600", "stinger", "reactive vortex 1200", "" })
        {
            cue.Actions[0].Value = good;
            Assert.Null(CueValidator.Validate(state, stack, new CueValidationContext { FileExists = _ => true }).ReasonFor(cue.Id));
        }
    }

    [Fact]
    public void ATransitionOverrideRidesExactlyOneSnapshot()
    {
        var state = new ShowState();
        state.Transition.Enabled = false; // the show does not fade at all
        state.Transition.Kind = TransitionKind.Dissolve;
        state.Transition.Scene = ReactiveScene.Plasma;
        state.Transition.Direction = TransitionDirection.Right;
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        var plain = bus.Current;
        Assert.False(plain.FadesEnabled);
        Assert.Equal(TransitionKind.Dissolve, plain.TransitionKindFor(plain.Version));

        bus.TransitionOnNextPublish(TransitionKind.Reactive, ReactiveScene.Vortex, TransitionDirection.Up);
        bus.Publish(state);
        var one = bus.Current;
        Assert.True(one.FadesEnabled); // naming a transition is asking for one, fades off or not
        Assert.Equal(TransitionKind.Reactive, one.TransitionKindFor(one.Version));
        Assert.Equal(ReactiveScene.Vortex, one.TransitionSceneFor(one.Version));
        Assert.Equal(TransitionDirection.Up, one.TransitionDirectionFor(one.Version));
        // Only the change published on that version: a sink still fading an older one is unaffected.
        Assert.Equal(TransitionKind.Dissolve, one.TransitionKindFor(one.Version - 1));

        bus.Publish(state);
        var next = bus.Current;
        Assert.False(next.FadesEnabled);
        Assert.Equal(TransitionKind.Dissolve, next.TransitionKindFor(next.Version));
        Assert.Equal(ReactiveScene.Plasma, next.TransitionSceneFor(next.Version));
        Assert.Equal(TransitionDirection.Right, next.TransitionDirectionFor(next.Version));
    }

    // ---- what actually reaches the glass ------------------------------------------------------

    [Fact]
    public void EveryTransitionIsWholeAtBothEndsOnASinkWithNoGraphicsCard()
    {
        foreach (var kind in Enum.GetValues<TransitionKind>())
        {
            using var run = new Run(kind, SinkKind.Stream);
            var start = run.At(0.0);
            Assert.True(Share(start, Red) > 0.97, $"{kind} starts on the picture the room already has ({Share(start, Red):P0} red)");

            var end = run.At(0.999);
            Assert.True(Share(end, Blue) > 0.97, $"{kind} ends on the picture the show asked for ({Share(end, Blue):P0} blue)");

            // And once it is over, the sink is back to drawing the show and holding nothing.
            var after = run.At(1.5);
            Assert.True(Share(after, Blue) > 0.99, $"{kind} leaves the new picture up");
            Assert.Null(run.Sink.MatteField);
            Assert.Null(run.Sink.MatteBitmap);
        }
    }

    [Fact]
    public void EveryTransitionIsActuallyDoingSomethingInTheMiddle()
    {
        foreach (var kind in Enum.GetValues<TransitionKind>())
        {
            using var run = new Run(kind, SinkKind.Stream);
            run.At(0.0);
            var mid = run.At(0.5);
            var red = Share(mid, Red);
            var blue = Share(mid, Blue);
            Assert.True(red < 0.95 && blue < 0.95,
                $"{kind} half way through is neither picture whole (red {red:P0}, blue {blue:P0})");
        }
    }

    [Fact]
    public void TheOutputAndTheStreamDrawTheSameTransition()
    {
        // The desk's outputs have a graphics card and the stream does not. A transition that took
        // a different path on each would show the client something different from the room.
        foreach (var kind in Enum.GetValues<TransitionKind>())
        {
            using var wall = new Run(kind, SinkKind.Output);
            using var stream = new Run(kind, SinkKind.Stream);
            wall.At(0.0);
            stream.At(0.0);
            var diff = MeanDiff(wall.At(0.4), stream.At(0.4));
            Assert.True(diff < 2.0, $"{kind} draws the same on the wall and on the stream (mean difference {diff:F1})");
        }
    }

    [Fact]
    public void ADipCoversTheCutWithItsColourAndTheStingerWithTheShowsOwn()
    {
        // A dip is whole across the middle: the change happens under it and is never seen.
        using (var dip = new Run(TransitionKind.Dip, SinkKind.Stream, s =>
        {
            s.Transition.DipUsesBrand = false;
            s.Transition.DipColor = "#000000";
        }))
        {
            dip.At(0.0);
            var covered = dip.At(0.5);
            Assert.True(Share(covered, Black) > 0.99, "the dip is complete where the cut happens");
        }

        // The stinger is the show's own identity swept over the cut, not a clip somebody has to ship.
        using var sting = new Run(TransitionKind.BrandStinger, SinkKind.Stream, s =>
        {
            s.Brand.PrimaryColor = "#00FF00";
            s.Brand.SecondaryColor = "#FFFF00";
            s.Brand.BackgroundColor = "#000000";
        });
        sting.At(0.0);
        var peak = sting.At(0.5);
        Assert.True(Share(peak, Red) < 0.02 && Share(peak, Blue) < 0.02, "nothing of either picture shows at the peak");
        Assert.Equal(new SKColor(0x00, 0xFF, 0x00), peak.GetPixel(4, 16));            // the primary sweeps in from the left
        Assert.Equal(new SKColor(0xFF, 0xFF, 0x00), peak.GetPixel(peak.Width - 5, 16)); // the secondary from the right
    }

    [Fact]
    public void AWipeAndAPushTravelTheWayTheShowAsked()
    {
        // A wipe travelling right has taken over the left of the picture first.
        using (var right = new Run(TransitionKind.Wipe, SinkKind.Stream, s => s.Transition.Direction = TransitionDirection.Right))
        {
            right.At(0.0);
            var half = right.At(0.5);
            Assert.True(Blue(half.GetPixel(2, 16)), "the new picture has arrived on the left");
            Assert.True(Red(half.GetPixel(half.Width - 3, 16)), "and not yet on the right");
        }

        // And travelling left, the other way round — the same run with one setting changed.
        using (var left = new Run(TransitionKind.Wipe, SinkKind.Stream, s => s.Transition.Direction = TransitionDirection.Left))
        {
            left.At(0.0);
            var half = left.At(0.5);
            Assert.True(Red(half.GetPixel(2, 16)));
            Assert.True(Blue(half.GetPixel(half.Width - 3, 16)));
        }

        // A push up: the new picture is coming from the bottom, so the top is still the old one.
        using var up = new Run(TransitionKind.Push, SinkKind.Stream, s => s.Transition.Direction = TransitionDirection.Up);
        up.At(0.0);
        var mid = up.At(0.5);
        Assert.True(Red(mid.GetPixel(32, 2)));
        Assert.True(Blue(mid.GetPixel(32, mid.Height - 3)));
    }

    [Fact]
    public void ATransitionSettlesItsLookOnceAndCannotChangeMidWay()
    {
        // A wipe crossing the screen must not become a push because somebody opened the page and
        // changed the setting while it was running.
        using var run = new Run(TransitionKind.Wipe, SinkKind.Stream, s => s.Transition.Direction = TransitionDirection.Right);
        run.At(0.0);
        Assert.Equal(TransitionKind.Wipe, run.Sink.TransitionLook.Kind);
        run.Incoming.State.Transition.Kind = TransitionKind.Push;
        run.Incoming.State.Transition.Direction = TransitionDirection.Down;
        var half = run.At(0.5);
        Assert.Equal(TransitionKind.Wipe, run.Sink.TransitionLook.Kind);
        Assert.Equal(TransitionDirection.Right, run.Sink.TransitionLook.Direction);
        Assert.True(Blue(half.GetPixel(2, 16)) && Red(half.GetPixel(half.Width - 3, 16)), "still the wipe it started as");
    }

    [Fact]
    public void ABrightDipTooSoonAfterTheLastOneIsDrawnAsADissolveInstead()
    {
        // A dip through a bright colour is a whole-screen light change, and the room gets the same
        // three-a-second protection a strobing sting gets. The picture still changes; it just does
        // not flash to do it.
        var engine = new PatternEngine();
        using var sink = new SinkState();
        Configure(out var a, "#FF0000", TransitionKind.Dip, s => { s.Transition.DipUsesBrand = false; s.Transition.DipColor = "#FFFFFF"; });
        Configure(out var b, "#0000FF", TransitionKind.Dip, s => { s.Transition.DipUsesBrand = false; s.Transition.DipColor = "#FFFFFF"; });
        Configure(out var c, "#00FF00", TransitionKind.Dip, s => { s.Transition.DipUsesBrand = false; s.Transition.DipColor = "#FFFFFF"; });
        Configure(out var d, "#FF00FF", TransitionKind.Dip, s => { s.Transition.DipUsesBrand = false; s.Transition.DipColor = "#FFFFFF"; });
        foreach (var s in new[] { a, b, c, d }) s.Transition.DurationMs = 100;

        Draw(engine, sink, RenderTestHarness.Snap(a, 1), 0.0);
        Draw(engine, sink, RenderTestHarness.Snap(b, 2), 0.0);
        Assert.Equal(TransitionKind.Dip, sink.TransitionLook.Kind); // the first one lands

        Draw(engine, sink, RenderTestHarness.Snap(c, 3), 0.2); // sooner than a third of a second
        Assert.Equal(TransitionKind.Dissolve, sink.TransitionLook.Kind);
        Assert.Equal(1, sink.Flash.Dropped);

        Draw(engine, sink, RenderTestHarness.Snap(d, 4), 0.9); // far enough on, and it dips again
        Assert.Equal(TransitionKind.Dip, sink.TransitionLook.Kind);
        Assert.Equal(2, sink.Flash.Allowed);

        // A dark dip is not a light change at all and is never held back.
        using var dark = new SinkState();
        Configure(out var e, "#FF0000", TransitionKind.Dip, s => { s.Transition.DipUsesBrand = false; s.Transition.DipColor = "#000000"; });
        Configure(out var f, "#0000FF", TransitionKind.Dip, s => { s.Transition.DipUsesBrand = false; s.Transition.DipColor = "#000000"; });
        Configure(out var g, "#00FF00", TransitionKind.Dip, s => { s.Transition.DipUsesBrand = false; s.Transition.DipColor = "#000000"; });
        Draw(engine, dark, RenderTestHarness.Snap(e, 1), 0.0);
        Draw(engine, dark, RenderTestHarness.Snap(f, 2), 0.0);
        Draw(engine, dark, RenderTestHarness.Snap(g, 3), 0.05);
        Assert.Equal(TransitionKind.Dip, dark.TransitionLook.Kind);
        Assert.Equal(0, dark.Flash.Dropped);
    }

    [Fact]
    public void AStillPictureHoldsNoMatteAndACutThrowsTheOneItHadAway()
    {
        var engine = new PatternEngine();
        using var sink = new SinkState();
        Configure(out var a, "#FF0000", TransitionKind.Reactive);
        Configure(out var b, "#0000FF", TransitionKind.Reactive);
        Configure(out var c, "#00FF00", TransitionKind.Reactive);

        Draw(engine, sink, RenderTestHarness.Snap(a, 1), 0.0);
        Assert.Null(sink.MatteField); // nothing is changing, so nothing is held

        Draw(engine, sink, RenderTestHarness.Snap(b, 2), 0.0);
        Assert.NotNull(sink.MatteField);
        var held = sink.MatteField;
        Assert.NotNull(sink.MattePixels);

        // The same scene at the same size mid-run is not rebuilt — a transition allocates once.
        Draw(engine, sink, RenderTestHarness.Snap(b, 2), 0.2);
        Assert.Same(held, sink.MatteField);

        // The change ends of its own accord: the buffer goes with it, so a still picture on a
        // wall for an hour holds nothing a transition needed.
        Draw(engine, sink, RenderTestHarness.Snap(b, 2), 1.5);
        Assert.Null(sink.MatteField);
        Assert.Null(sink.MatteBitmap);
        Assert.Null(sink.MattePixels);

        // A CUT abandons the change in flight, and the buffer goes with it.
        var cut = new ShowSnapshot { State = c, Version = 3, CutAtVersion = 3, IsTake = true };
        Draw(engine, sink, cut, 0.3);
        Assert.Null(sink.MatteField);
        Assert.Null(sink.MatteBitmap);
    }

    // ---- harness ------------------------------------------------------------------------------

    private const int W = 64;
    private const int H = 32;

    /// <summary>One change, from a red picture to a blue one, drawn through the real engine.</summary>
    private sealed class Run : IDisposable
    {
        private readonly PatternEngine _engine = new();
        private readonly ShowSnapshot _from;
        private readonly SinkKind _kind;

        public Run(TransitionKind kind, SinkKind sinkKind, Action<ShowState>? more = null)
        {
            Configure(out var from, "#FF0000", kind, more);
            Configure(out var to, "#0000FF", kind, more);
            _from = RenderTestHarness.Snap(from, 1);
            Incoming = RenderTestHarness.Snap(to, 2);
            _kind = sinkKind;
            Sink = new SinkState();
        }

        public SinkState Sink { get; }

        public ShowSnapshot Incoming { get; }

        /// <summary>The frame at this point through the change; the first call arms it.</summary>
        public SKBitmap At(double t)
        {
            if (Sink.TransitionKey == SinkState.NoKey) Draw(_engine, Sink, _from, 0, _kind);
            return Draw(_engine, Sink, Incoming, t, _kind);
        }

        public void Dispose() => Sink.Dispose();
    }

    private static void Configure(out ShowState state, string color, TransitionKind kind, Action<ShowState>? more = null)
    {
        state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.FlatField;
            s.Pattern.FlatField.Color = color;
            s.Pattern.FlatField.ShowLabel = false;
            s.Pattern.FlatField.ShowBorder = false;
            s.Pattern.Canvas.FollowOutput = true;
            s.Transition.Enabled = true;
            s.Transition.DurationMs = 1000;
            s.Transition.Kind = kind;
            more?.Invoke(s);
        });
    }

    private static SKBitmap Draw(PatternEngine engine, SinkState sink, ShowSnapshot snap, double time, SinkKind kind = SinkKind.Stream)
    {
        var info = new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(W, H),
            ReferenceSize = new SKSizeI(W, H),
            Time = time,
            Now = new DateTime(2026, 8, 29, 12, 0, 0),
            UtcNow = RenderTestHarness.FixedUtcNow,
            Sink = kind,
            SinkIndex = 1,
            SinkLabel = "t",
        };
        engine.Render(surface.Canvas, snap, in ctx, sink);
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    private static bool Red(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;
    private static bool Blue(SKColor c) => c.Blue > 200 && c.Green < 60 && c.Red < 60;
    private static bool Black(SKColor c) => c.Red < 12 && c.Green < 12 && c.Blue < 12;

    private static double Share(SKBitmap bmp, Func<SKColor, bool> match)
    {
        var hit = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (match(bmp.GetPixel(x, y))) hit++;
        return hit / (double)(bmp.Width * bmp.Height);
    }

    private static double MeanDiff(SKBitmap a, SKBitmap b)
    {
        var total = 0.0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                total += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
            }
        }
        return total / (a.Width * a.Height * 3.0);
    }
}
