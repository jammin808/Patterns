using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 63: "Overlays and Layers need the ability to use transitions on Take or appearing live,
/// default as fade." A thing switched on arrives over the show's transition time and a thing
/// switched off leaves the same way, drawn from the snapshot that had it; a cut, the show's
/// transitions off, a whole-picture crossfade or a first frame settle at once.
/// </summary>
public class AppearanceTests
{
    private static ShowSnapshot Snap(ShowState state, long version = 1) => new() { State = state, Version = version };

    [Fact]
    public void TheTrackerArrivesLeavesAndSwapsOverTheSecondsAndSettlesWhenTold()
    {
        var tracker = new AppearanceTracker();
        var a = Snap(new ShowState(), 1);
        var b = Snap(new ShowState(), 2);

        // A first frame settles whatever it sees — a monitor tile appearing does not fade its overlays in.
        Assert.Equal(Presence.Settled(false), tracker.Read(AppearKey.Clock, false, 0, 10, 0.4, true, a));
        Assert.False(tracker.Running(10));

        // Switched on at 10: arriving over 0.4 s, eased — nothing at the start, half at the middle, whole at the end.
        var start = tracker.Read(AppearKey.Clock, true, 0, 10, 0.4, true, a);
        Assert.Equal(0f, start.In);
        Assert.False(start.DrawsOutgoing);
        Assert.True(tracker.Running(10.1));
        var middle = tracker.Read(AppearKey.Clock, true, 0, 10.2, 0.4, true, a);
        Assert.InRange(middle.In, 0.45f, 0.55f);
        Assert.Equal(1f, tracker.Read(AppearKey.Clock, true, 0, 10.4, 0.4, true, a).In);
        Assert.False(tracker.Running(10.5));

        // Switched off, first seen at 11: leaving from the snapshot that had it, presence falling.
        var going = tracker.Read(AppearKey.Clock, false, 0, 11.0, 0.4, true, b);
        Assert.Equal(0f, going.In);
        Assert.Equal(1f, going.Out);
        Assert.Same(a, going.Outgoing);     // the last snapshot that showed it, not the one that dropped it
        var leaving = tracker.Read(AppearKey.Clock, false, 0, 11.2, 0.4, true, b);
        Assert.True(leaving.DrawsOutgoing);
        Assert.InRange(leaving.Out, 0.45f, 0.55f);
        Assert.Equal(Presence.Settled(false), tracker.Read(AppearKey.Clock, false, 0, 11.6, 0.4, true, b));

        // A new picture in a layer: the old leaves and the new arrives together.
        tracker.Read(AppearKey.Layer1, true, 1, 20, 0.4, true, a);
        var swapping = tracker.Read(AppearKey.Layer1, true, 2, 20.2, 0.4, true, b);   // first seen at 20.2: the move starts here
        Assert.Equal(0f, swapping.In);
        Assert.Equal(1f, swapping.Out);
        var swap = tracker.Read(AppearKey.Layer1, true, 2, 20.4, 0.4, true, b);
        Assert.InRange(swap.In, 0.45f, 0.55f);
        Assert.InRange(swap.Out, 0.45f, 0.55f);
        Assert.Same(a, swap.Outgoing);

        // Told not to animate — a cut, transitions off, a crossfade already running — it settles at once.
        Assert.Equal(Presence.Settled(true), tracker.Read(AppearKey.Logo, true, 0, 30, 0.4, animate: false, a));
        Assert.Equal(Presence.Settled(false), tracker.Read(AppearKey.Logo, false, 0, 30.1, 0.4, animate: false, a));
        Assert.False(tracker.Running(30.2));
    }

    [Fact]
    public void TheSlideComesFromTheEdgeTheThingSitsAtAndHomesAsItArrives()
    {
        var space = new SKSizeI(1000, 500);
        Assert.Equal(new SKPoint(0, -30), Appearances.SlideOffset(Anchor9.TopCenter, space, 0f));
        Assert.Equal(new SKPoint(60, 0), Appearances.SlideOffset(Anchor9.MiddleRight, space, 0f));
        Assert.Equal(new SKPoint(-60, 30), Appearances.SlideOffset(Anchor9.BottomLeft, space, 0f));
        Assert.Equal(new SKPoint(0, 30), Appearances.SlideOffset(Anchor9.Center, space, 0f));     // the middle comes up from below
        Assert.Equal(new SKPoint(0, -15), Appearances.SlideOffset(Anchor9.TopCenter, space, 0.5f));
        Assert.Equal(new SKPoint(0, 0), Appearances.SlideOffset(Anchor9.TopCenter, space, 1f));
        // A layer slides from the canvas edge nearest its box.
        Assert.Equal(new SKPoint(-60, 0), Appearances.SlideOffset(SKRect.Create(20, 200, 100, 100), space, 0f));
        Assert.Equal(new SKPoint(0, 30), Appearances.SlideOffset(SKRect.Create(450, 380, 100, 100), space, 0f));
    }

    [Fact]
    public void TheClockFadesInWhenSwitchedOnLiveAndFadesOutWhenSwitchedOff()
    {
        var state = Black();
        using var sink = new SinkState();
        var engine = new PatternEngine();

        // Settled with the clock off; then on, published: the first frame draws it barely, later frames whole.
        Render(engine, sink, state, 0.0, version: 1);
        state.Overlays.Clock.Enabled = true;
        var (start, startHits) = Render(engine, sink, state, 5.0, version: 2);
        Assert.DoesNotContain(startHits, h => h.Kind == HitKind.Clock);   // not there yet: nothing to take hold of
        var (middle, middleHits) = Render(engine, sink, state, 5.2, version: 2);
        Assert.Contains(middleHits, h => h.Kind == HitKind.Clock);        // arriving: already a handle
        Assert.True(sink.Appearances.Running(5.3));                        // and the sink asks for the next frame
        var (whole, hits) = Render(engine, sink, state, 5.6, version: 2);
        Assert.False(sink.Appearances.Running(5.7));                       // settled: no more frames for it
        var box = Assert.Single(hits, h => h.Kind == HitKind.Clock).Rect;
        var dark = Brightest(start, box);
        var half = Brightest(middle, box);
        var full = Brightest(whole, box);
        Assert.True(full > 150, $"the clock is drawn whole at the end ({full})");
        Assert.True(dark < full / 4, $"the first frame is barely there ({dark} of {full})");
        Assert.True(half > dark + 30 && half < full - 30, $"halfway is between ({dark} < {half} < {full})");

        // Switched off: it leaves from the snapshot that had it, then it is gone.
        state.Overlays.Clock.Enabled = false;
        var (going, goingHits) = Render(engine, sink, state, 8.05, version: 3);
        Assert.DoesNotContain(goingHits, h => h.Kind == HitKind.Clock);   // a leaving overlay is no handle
        Assert.True(Brightest(going, box) > full * 0.6, "just switched off, it is still nearly whole");
        var (gone, _) = Render(engine, sink, state, 8.6, version: 3);
        Assert.True(Brightest(gone, box) < 24, "and then it is gone");
        start.Dispose(); middle.Dispose(); whole.Dispose(); going.Dispose(); gone.Dispose();
    }

    [Fact]
    public void ACutASwitchedOffTransitionOrACutKindLandsAtOnce()
    {
        var engine = new PatternEngine();

        // The show's transitions off: everything switches.
        var off = Black();
        off.Transition.Enabled = false;
        using (var sink = new SinkState())
        {
            Render(engine, sink, off, 0, 1);
            off.Overlays.Clock.Enabled = true;
            var (bmp, hits) = Render(engine, sink, off, 1.0, 2);
            var box = Assert.Single(hits, h => h.Kind == HitKind.Clock).Rect;
            Assert.True(Brightest(bmp, box) > 150);
            Assert.False(sink.Appearances.Running(1.1));
            bmp.Dispose();
        }

        // The overlays set to cut: the same.
        var cut = Black();
        cut.Overlays.Appear.Kind = AppearKind.Cut;
        using (var sink = new SinkState())
        {
            Render(engine, sink, cut, 0, 1);
            cut.Overlays.Clock.Enabled = true;
            var (bmp, hits) = Render(engine, sink, cut, 1.0, 2);
            Assert.True(Brightest(bmp, Assert.Single(hits, h => h.Kind == HitKind.Clock).Rect) > 150);
            bmp.Dispose();
        }

        // A CUT publish: the version it landed on switches.
        var take = Black();
        using (var sink = new SinkState())
        {
            Render(engine, sink, take, 0, 1);
            take.Overlays.Clock.Enabled = true;
            var (bmp, hits) = Render(engine, sink, take, 1.0, 2, cutAt: 2);
            Assert.True(Brightest(bmp, Assert.Single(hits, h => h.Kind == HitKind.Clock).Rect) > 150);
            bmp.Dispose();
        }

        // A time of its own: 100 ms, so the clock is whole a tenth of a second on.
        var quick = Black();
        quick.Overlays.Appear.DurationMs = 100;
        using (var sink = new SinkState())
        {
            Render(engine, sink, quick, 0, 1);
            quick.Overlays.Clock.Enabled = true;
            var (early, _) = Render(engine, sink, quick, 1.0, 2);
            var (done, hits) = Render(engine, sink, quick, 1.12, 2);
            var box = Assert.Single(hits, h => h.Kind == HitKind.Clock).Rect;
            Assert.True(Brightest(early, box) < 60);
            Assert.True(Brightest(done, box) > 150);
            early.Dispose(); done.Dispose();
        }
    }

    [Fact]
    public void ALayerSwitchedOnArrivesOnTheDesksPaneAndTheSettingIsNotIdentity()
    {
        var state = Black();
        state.Pattern.Layer1.Source = LayerSource.Image;   // no file: the pane draws the dashed box and the name to place it
        state.Pattern.Layer1.XPct = 30; state.Pattern.Layer1.YPct = 30; state.Pattern.Layer1.WPct = 40; state.Pattern.Layer1.HPct = 40;
        using var sink = new SinkState();
        var engine = new PatternEngine();
        Render(engine, sink, state, 0, 1, SinkKind.Preview);
        state.Pattern.Layer1.Enabled = true;
        var (start, _) = Render(engine, sink, state, 2.0, 2, SinkKind.Preview);
        var (whole, hits) = Render(engine, sink, state, 2.6, 2, SinkKind.Preview);
        var box = Assert.Single(hits, h => h.Kind == HitKind.Layer1).Rect;
        var dark = Brightest(start, box);
        var full = Brightest(whole, box);
        Assert.True(full > 100, $"the layer's frame is drawn whole at the end ({full})");
        Assert.True(dark < full / 3, $"and barely at the start ({dark} of {full})");
        start.Dispose(); whole.Dispose();

        // Choosing how a layer appears never starts a crossfade of the whole picture.
        var before = JsonUtil.SerializeIdentity(state.Pattern);
        state.Pattern.Layer1.Appear.Kind = AppearKind.Slide;
        state.Pattern.Layer1.Appear.DurationMs = 800;
        Assert.Equal(before, JsonUtil.SerializeIdentity(state.Pattern));
    }

    // ---- the pieces ----

    private static ShowState Black()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.FlatField;
        state.Pattern.FlatField.Color = "#000000";
        state.Pattern.FlatField.ShowLabel = false;
        state.Pattern.Canvas.FollowOutput = true;
        state.Overlays.Badge.Enabled = false;
        return state;
    }

    private static (SKBitmap Bmp, List<HitRect> Hits) Render(PatternEngine engine, SinkState sink, ShowState state, double time, long version, SinkKind kind = SinkKind.Output, long cutAt = 0)
    {
        var snap = new ShowSnapshot { State = state, Version = version, CutAtVersion = cutAt };
        var info = new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(1280, 720),
            ReferenceSize = new SKSizeI(1280, 720),
            Time = time,
            Now = new DateTime(2026, 9, 15, 12, 30, 0),
            UtcNow = new DateTime(2026, 9, 15, 11, 30, 0, DateTimeKind.Utc),
            Sink = kind,
            SinkLabel = "out",
            DeviceScale = 1f,
        };
        engine.Render(surface.Canvas, snap, in ctx, sink);
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return (bmp, sink.Hits.ToList());
    }

    /// <summary>The brightest pixel in a box, 0–255.</summary>
    private static int Brightest(SKBitmap bmp, SKRect box)
    {
        var best = 0;
        for (var y = Math.Max(0, (int)box.Top); y < Math.Min(bmp.Height, (int)box.Bottom); y++)
        {
            for (var x = Math.Max(0, (int)box.Left); x < Math.Min(bmp.Width, (int)box.Right); x++)
            {
                var p = bmp.GetPixel(x, y);
                var v = Math.Max(p.Red, Math.Max(p.Green, p.Blue));
                if (v > best) best = v;
            }
        }
        return best;
    }
}
