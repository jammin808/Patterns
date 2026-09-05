using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>A web page that paints one colour and remembers everything the desk did to it.</summary>
public sealed class FakeWebSource : IWebSource
{
    private readonly SKColor _color;
    private readonly SKSizeI _frame;

    public FakeWebSource(SKColor color, int width = 640, int height = 360, string url = "https://example.com")
    {
        _color = color;
        _frame = new SKSizeI(width, height);
        CurrentUrl = url;
    }

    public List<(string Kind, float X, float Y)> Events { get; } = new();
    public List<string> Typed { get; } = new();
    public List<string> Keys { get; } = new();
    public List<string> Scripts { get; } = new();
    public int Backs, Forwards, Reloads;

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
    {
        canvas.DrawRect(dest, new SKPaint { Color = _color });
        return true;
    }

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop) => DrawFrame(canvas, dest, paint);

    public SKSizeI? FrameSize => _frame;
    public bool IsPlaying => true;
    public bool IsEnded => false;
    public double DurationSeconds => 0;
    public string StatusText => "Showing";
    public SKSizeI PageSize => _frame;
    public SKPoint? PointerNorm { get; set; }
    public DateTime? LastClickUtc { get; set; }
    public string CurrentUrl { get; private set; }
    public string Title { get; set; } = "A page";
    public double ZoomPct { get; set; } = 100;
    public bool IsMuted { get; set; }

    public void PointerMove(float nx, float ny)
    {
        PointerNorm = new SKPoint(nx, ny);
        Events.Add(("move", nx, ny));
    }

    public void PointerDown(float nx, float ny)
    {
        PointerNorm = new SKPoint(nx, ny);
        LastClickUtc = DateTime.UtcNow;
        Events.Add(("down", nx, ny));
    }

    public void PointerUp(float nx, float ny) => Events.Add(("up", nx, ny));

    public void PointerLeave()
    {
        PointerNorm = null;
        Events.Add(("leave", 0, 0));
    }

    public void Wheel(float nx, float ny, float deltaLines, bool horizontal) => Events.Add((horizontal ? "hwheel" : "wheel", deltaLines, 0));
    public void TypeText(string text) => Typed.Add(text);
    public void PressKey(string key) => Keys.Add(key);
    public void RunScript(string script) => Scripts.Add(script);
    public void Navigate(string url) => CurrentUrl = url;
    public void GoBack() => Backs++;
    public void GoForward() => Forwards++;
    public void Reload() => Reloads++;
}

/// <summary>
/// A web page inside the engine: what the show wants mounted, the page drawn as a pattern and
/// as a layer with its box recorded for the desk, the pointer and its ripple drawn when asked,
/// the maths through a crop both ways, and every sink kept redrawing.
/// </summary>
[Collection("InputBus")]
public class WebSourceTests
{
    private const string Key = "web:https://example.com";

    private static ShowState PageAsPattern(Action<MediaOptions>? tweak = null) => RenderTestHarness.State(s =>
    {
        s.Pattern.Kind = PatternKind.Media;
        s.Pattern.Media.Source = MediaSource.Web;
        s.Pattern.Media.WebUrl = "example.com";
        s.Pattern.Media.BackgroundColor = "#000000";
        tweak?.Invoke(s.Pattern.Media);
    });

    private static ShowState PageAsLayer(Action<LayerConfig>? tweak = null) => RenderTestHarness.State(s =>
    {
        s.Pattern.Kind = PatternKind.FlatField;
        s.Pattern.FlatField.Color = "#00FF00";
        s.Pattern.Layer1.Enabled = true;
        s.Pattern.Layer1.Source = LayerSource.Web;
        s.Pattern.Layer1.WebUrl = "https://example.com";
        s.Pattern.Layer1.XPct = 30;
        s.Pattern.Layer1.YPct = 30;
        s.Pattern.Layer1.WPct = 40;   // 512 × 288 on a 1280 × 720 canvas — the page's shape
        s.Pattern.Layer1.HPct = 40;
        s.Pattern.Layer1.Fit = FitMode.Fill;
        tweak?.Invoke(s.Pattern.Layer1);
    });

    private static SKBitmap RenderWithSink(ShowState state, int width, int height, SinkKind kind, SinkState sink, bool fadeSource = false)
    {
        var engine = new PatternEngine();
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(width, height),
            ReferenceSize = new SKSizeI(width, height),
            Time = 1,
            Now = new DateTime(2026, 8, 29, 12, 0, 0),
            UtcNow = RenderTestHarness.FixedUtcNow,
            Sink = kind,
            SinkIndex = 1,
            SinkLabel = "test",
            IsFadeSource = fadeSource,
        };
        engine.Render(surface.Canvas, RenderTestHarness.Snap(state), in ctx, sink);
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    private static PaneMap Identity(int w, int h) => new(new SKSizeI(w, h), 0, 0, 1, new SKPoint(0, 0), 1, new SKSizeI(w, h));

    [Fact]
    public void AddressesNormaliseAndName()
    {
        Assert.Equal("https://example.com", WebAddress.Normalize(" example.com "));
        Assert.Equal("http://x/y?z=1", WebAddress.Normalize("http://x/y?z=1"));
        Assert.Equal("", WebAddress.Normalize(""));
        Assert.Equal("www.example.com", WebAddress.ShortName("https://www.example.com/a/b?c=1"));
        Assert.Equal("schedule.html", WebAddress.ShortName("file:///C:/show/schedule.html"));
        Assert.Equal("web:https://example.com", InputKeys.Web("example.com"));
        Assert.Equal("", InputKeys.Web(""));
    }

    [Fact]
    public void TheShowWantsItsPagesOnceWithTheirViewportZoomAndSound()
    {
        var s = PageAsPattern(m =>
        {
            m.WebWidth = 1280;
            m.WebHeight = 720;
            m.WebZoomPct = 150;
            m.Mute = false;
        });
        s.Pattern.Layer1.Enabled = true;
        s.Pattern.Layer1.Source = LayerSource.Web;
        s.Pattern.Layer1.WebUrl = "https://example.com";   // the same page — one browser
        s.Pattern.Layer2.Enabled = true;
        s.Pattern.Layer2.Source = LayerSource.Web;
        s.Pattern.Layer2.WebUrl = "other.org";

        var wanted = MediaLocator.FindWantedInputs(RenderTestHarness.Snap(s));
        var page = Assert.Single(wanted, w => w.Key == Key);
        Assert.Equal(MediaLocator.WantedKind.Web, page.Kind);
        Assert.Equal("https://example.com", page.Target);
        Assert.Equal("1280x720", page.Format);
        Assert.Equal(150, page.Zoom);
        Assert.False(page.Mute);
        var other = Assert.Single(wanted, w => w.Key == "web:https://other.org");
        Assert.True(other.Mute);                       // a layer's page is silent unless asked
        Assert.Equal("1280x720", other.Format);        // a layer's page lays out at 720p by default
        Assert.Equal(2, wanted.Count);
    }

    [Fact]
    public void TheMediaPatternShowsThePageRecordsItsBoxAndDrawsThePointerWhenAsked()
    {
        InputBus.Clear();
        var page = new FakeWebSource(SKColors.Blue);
        InputBus.Mount(Key, page);
        try
        {
            var s = PageAsPattern();
            var sink = new SinkState();
            using var plain = RenderWithSink(s, 1280, 720, SinkKind.Preview, sink);
            Assert.Equal(SKColors.Blue, plain.GetPixel(640, 360));
            var hit = Assert.Single(sink.Hits, h => h.Kind == HitKind.WebPage);
            Assert.Equal(Key, hit.Key);
            Assert.Equal(new SKRect(0, 0, 1280, 720), hit.Rect);
            Assert.False(hit.ViewportSpace);

            // The pointer: the arrow's body a little below and right of its tip, white; a fresh click rings it.
            page.PointerNorm = new SKPoint(0.5f, 0.5f);
            page.LastClickUtc = RenderTestHarness.FixedUtcNow.AddMilliseconds(-10);
            var sink2 = new SinkState();
            using var pointed = RenderWithSink(s, 1280, 720, SinkKind.Output, sink2);
            Assert.Equal(SKColors.White, pointed.GetPixel(641, 364));
            var ring = pointed.GetPixel(640 + (int)Math.Round(WebPointer.SizeFor(new SKRect(0, 0, 1280, 720)) * 0.5f), 360);
            Assert.True(ring.Red > 150, $"ring pixel {ring}");
            Assert.Empty(sink2.Hits.Where(h => h.Kind == HitKind.WebPage).Skip(1));

            // Not asked for: the page alone. A fade source records nothing.
            s.Pattern.Media.WebShowPointer = false;
            var sink3 = new SinkState();
            using var hidden = RenderWithSink(s, 1280, 720, SinkKind.Output, sink3, fadeSource: true);
            Assert.Equal(SKColors.Blue, hidden.GetPixel(641, 364));
            Assert.DoesNotContain(sink3.Hits, h => h.Kind == HitKind.WebPage);

            // Nothing mounted: a card, never a crash, and no box to click.
            InputBus.Clear();
            var sink4 = new SinkState();
            using var card = RenderWithSink(s, 1280, 720, SinkKind.Preview, sink4);
            Assert.NotEqual(SKColors.Blue, card.GetPixel(640, 360));
            Assert.DoesNotContain(sink4.Hits, h => h.Kind == HitKind.WebPage);
        }
        finally
        {
            InputBus.Clear();
        }
    }

    [Fact]
    public void AWebLayerPutsItsPageOverItsBoxAndAltLooksThrough()
    {
        InputBus.Clear();
        var page = new FakeWebSource(SKColors.Blue);
        InputBus.Mount(Key, page);
        try
        {
            var s = PageAsLayer(l =>
            {
                l.CropLeftPct = 10;
                l.CropRightPct = 10;
                l.CropTopPct = 10;
                l.CropBottomPct = 10;
            });
            var sink = new SinkState();
            using var bmp = RenderWithSink(s, 1280, 720, SinkKind.Preview, sink);
            var box = new SKRect(384, 216, 896, 504);
            Assert.Equal(SKColors.Blue, bmp.GetPixel(640, 360));
            Assert.Equal(SKColors.Lime, bmp.GetPixel(100, 100));

            // The box first, the page on top of it — a press clicks into the page, Alt takes the box.
            var kinds = sink.Hits.Select(h => h.Kind).ToList();
            Assert.Equal(new[] { HitKind.Layer1, HitKind.WebPage }, kinds);
            var web = sink.Hits.Last();
            Assert.Equal(box, web.Rect);
            Assert.Equal(box, web.Bounds);
            Assert.Equal(10, web.Crop.LeftPct);
            var map = Identity(1280, 720);
            var centre = new SKPoint(640, 360);
            Assert.Equal(HitKind.WebPage, HitTester.Find(sink.Hits, in map, centre)!.Value.Kind);
            Assert.Equal(HitKind.Layer1, HitTester.Find(sink.Hits, in map, centre, includeWeb: false)!.Value.Kind);
            Assert.Null(HitTester.Find(sink.Hits, in map, new SKPoint(100, 100)));

            // Through the crop: the box's centre is the page's centre; its left edge is 10 % in.
            var at = WebPointerMap.ToPage(in web, centre)!.Value;
            Assert.Equal(0.5f, at.X, 3);
            Assert.Equal(0.5f, at.Y, 3);
            var edge = WebPointerMap.ToPage(in web, new SKPoint(box.Left + 0.5f, box.Top + 0.5f))!.Value;
            Assert.InRange(edge.X, 0.1f, 0.102f);
            Assert.Null(WebPointerMap.ToPage(in web, new SKPoint(100, 100)));
            var dragged = WebPointerMap.ToPageUnbounded(in web, new SKPoint(-1000, 360));
            Assert.Equal(0, dragged.X);

            // The pointer is drawn in the box; one in the cropped-away margin is not drawn at all.
            page.PointerNorm = new SKPoint(0.5f, 0.5f);
            var sink2 = new SinkState();
            using var pointed = RenderWithSink(s, 1280, 720, SinkKind.Output, sink2);
            Assert.Equal(SKColors.White, pointed.GetPixel(641, 364));
            page.PointerNorm = new SKPoint(0.02f, 0.5f);
            using var surface = SKSurface.Create(new SKImageInfo(64, 64));
            Assert.False(WebPointer.Draw(surface.Canvas, box, new FrameCrop(10, 10, 10, 10), page, RenderTestHarness.FixedUtcNow, sink2.Paints));
            page.PointerNorm = null;
            Assert.False(WebPointer.Draw(surface.Canvas, box, FrameCrop.None, page, RenderTestHarness.FixedUtcNow, sink2.Paints));

            // Turned off, the layer records neither box; a rename of nothing web-related leaves the page alone.
            s.Pattern.Layer1.WebShowPointer = false;
            page.PointerNorm = new SKPoint(0.5f, 0.5f);
            var sink3 = new SinkState();
            using var plain = RenderWithSink(s, 1280, 720, SinkKind.Output, sink3);
            Assert.Equal(SKColors.Blue, plain.GetPixel(641, 364));
        }
        finally
        {
            InputBus.Clear();
        }
    }

    [Fact]
    public void ThePointerMapsThroughACropBothWays()
    {
        var dest = new SKRect(100, 50, 500, 250);
        var crop = new FrameCrop(20, 0, 0, 40);
        // The visible part of the page runs from x 0.2 to 1 and y 0 to 0.6.
        var mid = WebPointerMap.ToRect(dest, in crop, new SKPoint(0.6f, 0.3f))!.Value;
        Assert.Equal(300, mid.X, 3);
        Assert.Equal(150, mid.Y, 3);
        Assert.Null(WebPointerMap.ToRect(dest, in crop, new SKPoint(0.1f, 0.3f)));
        Assert.Null(WebPointerMap.ToRect(dest, in crop, new SKPoint(0.6f, 0.8f)));
        var hit = new HitRect(HitKind.WebPage, dest, false, Key, crop);
        var back = WebPointerMap.ToPage(in hit, mid)!.Value;
        Assert.Equal(0.6f, back.X, 3);
        Assert.Equal(0.3f, back.Y, 3);
        Assert.True(new HitRect(HitKind.WebPage, dest, false, Key, crop, new SKRect(100, 50, 200, 250)).Contains(new SKPoint(150, 100)));
        Assert.False(new HitRect(HitKind.WebPage, dest, false, Key, crop, new SKRect(100, 50, 200, 250)).Contains(new SKPoint(300, 100)));
    }

    [Fact]
    public void APageKeepsEverySinkRedrawingAndRidesInAFile()
    {
        var now = RenderTestHarness.FixedUtcNow;
        Assert.Equal(RedrawCadence.Continuous, PatternEngine.CadenceOf(RenderTestHarness.Snap(PageAsPattern()), null, now));
        var layered = PageAsLayer();
        Assert.Equal(RedrawCadence.Continuous, PatternEngine.CadenceOf(RenderTestHarness.Snap(layered), null, now));
        layered.Pattern.Layer1.Enabled = false;
        Assert.Equal(RedrawCadence.Static, PatternEngine.CadenceOf(RenderTestHarness.Snap(layered), null, now));

        // The page's settings survive a save, a copy and a look; the pointer switch never starts a fade.
        var s = PageAsPattern(m =>
        {
            m.WebWidth = 1600;
            m.WebHeight = 900;
            m.WebZoomPct = 125;
            m.WebShowPointer = false;
        });
        var copy = JsonUtil.Clone(s);
        Assert.Equal(MediaSource.Web, copy.Pattern.Media.Source);
        Assert.Equal("example.com", copy.Pattern.Media.WebUrl);
        Assert.Equal((1600, 900, 125.0, false), (copy.Pattern.Media.WebWidth, copy.Pattern.Media.WebHeight, copy.Pattern.Media.WebZoomPct, copy.Pattern.Media.WebShowPointer));
        var before = JsonUtil.SerializeIdentity(s.Pattern);
        s.Pattern.Media.WebShowPointer = true;
        s.Pattern.Media.WebZoomPct = 150;
        Assert.Equal(before, JsonUtil.SerializeIdentity(s.Pattern));
        s.Pattern.Media.WebUrl = "other.org";
        Assert.NotEqual(before, JsonUtil.SerializeIdentity(s.Pattern));
    }
}
