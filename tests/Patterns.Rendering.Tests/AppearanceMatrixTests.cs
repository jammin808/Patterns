using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering;
using Patterns.Rendering.Media;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 64: the first-frame rule of 63.5, generalised into a table — every appearance subject
/// (the badge, the logo, the clock, the weather, the countdown, the message, the PiP, both
/// layers) under every kind (fade, cut, slide), through the whole state machine: the arrival's
/// first frame has the subject on the hit map with nothing of it showing yet (a cut shows it
/// whole); mid-arrival it is a handle and the cadence stays continuous; settled it is whole and
/// the cadence is the content's; the first frame of a departure still shows it and offers no
/// handle for a thing the show says is gone; departed it is neither seen nor a handle. A layer
/// whose source changes rides the whole-picture crossfade with its handle kept, and an overlay
/// switched on inside a running crossfade never animates a second time.
/// </summary>
public sealed class AppearanceMatrixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "patterns-appear-" + Guid.NewGuid().ToString("N"));
    private readonly string _logo;

    public AppearanceMatrixTests()
    {
        Directory.CreateDirectory(_dir);
        using var bmp = new SKBitmap(64, 64);
        bmp.Erase(new SKColor(230, 230, 230));
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        _logo = Path.Combine(_dir, "logo.png");
        File.WriteAllBytes(_logo, data.ToArray());
    }

    public void Dispose()
    {
        ImageCache.ClearForTests();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    public static readonly string[] Subjects = { "Badge", "Logo", "Clock", "Weather", "Countdown", "Message", "Pip", "Layer1", "Layer2" };

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var subject in Subjects)
        {
            foreach (var kind in new[] { AppearKind.Fade, AppearKind.Cut, AppearKind.Slide }) yield return new object[] { subject, kind };
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void EverySubjectArrivesIsAHandleSettlesAndLeavesAsTheKindSays(string subject, AppearKind kind)
    {
        var state = Black();
        SetKind(state, subject, kind);
        Switch(state, subject, on: false);
        using var sink = new SinkState();
        var engine = new PatternEngine();
        var hitKind = Enum.Parse<HitKind>(subject);
        var on = SinkFor(subject);
        Render(engine, sink, state, 0.0, 1, on);                                                     // settled with it off
        Assert.DoesNotContain(Render(engine, sink, state, 0.5, 1, on).Hits, h => h.Kind == hitKind);

        // Arrival, first frame: the show says it is there — on the map at once; the eye gets it at
        // the kind's pace (a cut, whole).
        Switch(state, subject, on: true);
        var (start, startHits) = Render(engine, sink, state, 5.0, 2, on);
        var box = Assert.Single(startHits, h => h.Kind == hitKind).Rect;
        Assert.True(box.Width > 4 && box.Height > 4, $"{subject}: a box worth taking hold of ({box})");
        var animates = kind != AppearKind.Cut;
        Assert.Equal(animates, sink.Appearances.Running(5.05));

        // Mid-arrival: still a handle, the cadence still continuous.
        var (middle, middleHits) = Render(engine, sink, state, 5.2, 2, on);
        Assert.Contains(middleHits, h => h.Kind == hitKind);
        Assert.Equal(animates, sink.Appearances.Running(5.25));

        // Settled: whole, and the sink asks for no more frames on its account.
        var (whole, wholeHits) = Render(engine, sink, state, 5.6, 2, on);
        Assert.Contains(wholeHits, h => h.Kind == hitKind);
        Assert.False(sink.Appearances.Running(5.7));
        var full = Brightest(whole, box);
        Assert.True(full > 40, $"{subject}: drawn whole at the end ({full})");
        var dark = Brightest(start, box);
        var half = Brightest(middle, box);
        if (animates)
        {
            Assert.True(dark < full / 3, $"{subject} {kind}: nothing of it shows on the first frame ({dark} of {full})");
            Assert.True(half <= full, $"{subject} {kind}: mid-arrival is on its way ({half} of {full})");
        }
        else
        {
            Assert.True(dark >= full * 0.9, $"{subject} cut: whole at once ({dark} of {full})");
        }

        // Departure, first frame: the eye still has it, the map does not — no handle for a thing
        // the show says is gone. Then it is gone from both.
        Switch(state, subject, on: false);
        var (going, goingHits) = Render(engine, sink, state, 8.05, 3, on);
        Assert.DoesNotContain(goingHits, h => h.Kind == hitKind);
        var leaving = Brightest(going, box);
        if (animates) Assert.True(leaving > full / 2, $"{subject} {kind}: still nearly whole as it starts to leave ({leaving} of {full})");
        else Assert.True(leaving < full / 3, $"{subject} cut: gone at once ({leaving} of {full})");
        Assert.Equal(animates, sink.Appearances.Running(8.1));
        var (gone, goneHits) = Render(engine, sink, state, 8.6, 3, on);
        Assert.DoesNotContain(goneHits, h => h.Kind == hitKind);
        Assert.True(Brightest(gone, box) < full / 3, $"{subject} {kind}: gone ({Brightest(gone, box)} of {full})");
        Assert.False(sink.Appearances.Running(8.7));
        start.Dispose(); middle.Dispose(); whole.Dispose(); going.Dispose(); gone.Dispose();
    }

    [Fact]
    public void ALayerWhoseSourceChangesRidesTheWholePictureCrossfadeWithItsHandleKept()
    {
        var state = Black();
        state.Pattern.Layer1.Source = LayerSource.Image;
        Place(state.Pattern.Layer1, 30, 30, 40, 40);
        state.Pattern.Layer1.Enabled = true;
        using var sink = new SinkState();
        var engine = new PatternEngine();
        Render(engine, sink, state, 0.0, 1, SinkKind.Preview);
        Render(engine, sink, state, 0.6, 1, SinkKind.Preview);                                       // settled, the layer whole
        Assert.False(sink.Appearances.Running(0.7));

        // A new source is a new identity: the whole picture crossfades, and the layer's own
        // arrival does not run a second time inside it — the map keeps the layer throughout.
        state.Pattern.Layer1.Source = LayerSource.Web;
        state.Pattern.Layer1.WebUrl = "https://example.test/";
        var (_, hits) = Render(engine, sink, state, 5.0, 2, SinkKind.Preview, take: true);            // a TAKE: the change crosses
        Assert.True(sink.TransitionEndClock > 5.0, "the picture crossfades on the layer's new source");
        Assert.False(sink.Appearances.Running(5.05));
        Assert.Contains(hits, h => h.Kind == HitKind.Layer1);
        var (_, later) = Render(engine, sink, state, 5.2, 2, SinkKind.Preview, take: true);
        Assert.Contains(later, h => h.Kind == HitKind.Layer1);
    }

    [Fact]
    public void AnOverlaySwitchedOnInsideARunningCrossfadeArrivesWithItAndNeverASecondTime()
    {
        var state = Black();
        using var sink = new SinkState();
        var engine = new PatternEngine();
        Render(engine, sink, state, 0.0, 1);
        Render(engine, sink, state, 0.6, 1);

        // The picture changes and the clock comes on in one publish: one crossfade carries both.
        state.Pattern.Kind = PatternKind.Grid;
        state.Overlays.Clock.Enabled = true;
        var (_, hits) = Render(engine, sink, state, 5.0, 2, take: true);                              // a TAKE: the change crosses
        Assert.True(sink.TransitionEndClock > 5.0, "the picture crossfades");
        Assert.False(sink.Appearances.Running(5.05));                                                // the clock rides the crossfade: no second animation
        Assert.Contains(hits, h => h.Kind == HitKind.Clock);
    }

    // ---- the pieces ----

    private static void SetKind(ShowState s, string subject, AppearKind kind)
    {
        switch (subject)
        {
            case "Layer1": s.Pattern.Layer1.Appear.Kind = kind; break;
            case "Layer2": s.Pattern.Layer2.Appear.Kind = kind; break;
            default: s.Overlays.Appear.Kind = kind; break;
        }
    }

    private void Switch(ShowState s, string subject, bool on)
    {
        switch (subject)
        {
            case "Badge": s.Overlays.Badge.Enabled = on; break;
            case "Logo": s.Brand.LogoPath = _logo; s.Overlays.Logo.Enabled = on; break;
            case "Clock": s.Overlays.Clock.Enabled = on; break;
            case "Weather": s.Overlays.Weather.Enabled = on; break;
            case "Countdown": s.Countdown.Enabled = on; break;
            case "Message": s.Overlays.Message.Text = "A message for the room"; s.Overlays.Message.Enabled = on; break;
            case "Pip": s.Overlays.Pip.Source = PipSource.Capture; s.Overlays.Pip.CaptureDevice = "Cam"; s.Overlays.Pip.Enabled = on; break;
            case "Layer1": s.Pattern.Layer1.Source = LayerSource.Image; Place(s.Pattern.Layer1, 30, 30, 40, 40); s.Pattern.Layer1.Enabled = on; break;
            case "Layer2": s.Pattern.Layer2.Source = LayerSource.Image; Place(s.Pattern.Layer2, 10, 10, 40, 40); s.Pattern.Layer2.Enabled = on; break;
            default: throw new ArgumentException(subject);
        }
    }

    private static void Place(LayerConfig l, double x, double y, double w, double h)
    {
        l.XPct = x; l.YPct = y; l.WPct = w; l.HPct = h;
    }

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

    /// <summary>The desk's own sink for a layer (an empty layer's box is drawn for the desk to place, never for the room); an output for everything else.</summary>
    private static SinkKind SinkFor(string subject) => subject.StartsWith("Layer", StringComparison.Ordinal) ? SinkKind.Preview : SinkKind.Output;

    /// <summary>A frame of the show as published: the state cloned, as the bus clones it — the outgoing snapshot keeps what it had when the show moves on.</summary>
    private static (SKBitmap Bmp, List<HitRect> Hits) Render(PatternEngine engine, SinkState sink, ShowState state, double time, long version, SinkKind kind = SinkKind.Output, bool take = false)
    {
        var snap = new ShowSnapshot { State = SnapshotClone.Clone(state), Version = version, IsTake = take };
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
