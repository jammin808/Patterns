using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The area of interest on an input: the crop maths past the inset's ceiling and the box that
/// composes, a still cropped, mirrored and turned on the output with the desk's handle
/// recorded, and a live feed cropped through its source with the pointer mapping through it.
/// </summary>
[Collection("InputBus")]
public class InputCropTests
{
    private sealed class RecordingSource : IVideoFrameSource
    {
        public FrameCrop? LastCrop;
        public SKRect LastDest;

        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
        {
            LastDest = dest;
            canvas.DrawRect(dest, new SKPaint { Color = SKColors.Red });
            return true;
        }

        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop)
        {
            LastCrop = crop;
            return DrawFrame(canvas, dest, paint);
        }

        public SKSizeI? FrameSize => new SKSizeI(1920, 1080);
        public bool IsPlaying => true;
        public bool IsEnded => false;
        public double DurationSeconds => 0;
        public string StatusText => "recording";
    }

    /// <summary>A 200 × 100 still with four coloured quarters: red top-left, green top-right, blue bottom-left, yellow bottom-right.</summary>
    internal static string Quarters(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "quarters.png");
        using var bmp = new SKBitmap(200, 100);
        using (var c = new SKCanvas(bmp))
        {
            c.DrawRect(SKRect.Create(0, 0, 100, 50), new SKPaint { Color = SKColors.Red });
            c.DrawRect(SKRect.Create(100, 0, 100, 50), new SKPaint { Color = SKColors.Lime });
            c.DrawRect(SKRect.Create(0, 50, 100, 50), new SKPaint { Color = SKColors.Blue });
            c.DrawRect(SKRect.Create(100, 50, 100, 50), new SKPaint { Color = SKColors.Yellow });
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static bool Is(SKColor c, SKColor want) => Math.Abs(c.Red - want.Red) < 40 && Math.Abs(c.Green - want.Green) < 40 && Math.Abs(c.Blue - want.Blue) < 40;

    private static (SKBitmap Bmp, List<HitRect> Hits) Render(ShowState state, int w, int h)
    {
        var engine = new PatternEngine();
        using var sink = new SinkState();
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(w, h),
            ReferenceSize = new SKSizeI(w, h),
            Time = 1.0,
            Now = new DateTime(2026, 9, 5, 12, 0, 0),
            UtcNow = RenderTestHarness.FixedUtcNow,
            Sink = SinkKind.Preview,
            SinkIndex = 1,
            SinkLabel = "crop",
        };
        engine.Render(surface.Canvas, RenderTestHarness.Snap(state), in ctx, sink);
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return (bmp, sink.Hits.ToList());
    }

    private static ShowState Media(Action<MediaOptions> mutate) => RenderTestHarness.State(s =>
    {
        s.Pattern.Kind = PatternKind.Media;
        s.Pattern.Canvas.FollowOutput = true;
        s.Transition.Enabled = false;
        s.Pattern.Media.BackgroundColor = "#000000";
        mutate(s.Pattern.Media);
    });

    [Fact]
    public void TheCropReachesNinetyPercentKeepsATwentiethAndComposes()
    {
        var frame = new SKSizeI(1000, 500);
        Assert.Equal(90, FrameCrop.MaxPct);
        var half = new FrameCrop(0, 0, 50, 50).SourceRect(frame);
        Assert.Equal(new SKRect(0, 0, 500, 250), half);            // past the inset's old 45 % ceiling
        var meet = new FrameCrop(60, 10, 60, 88).SourceRect(frame);
        Assert.Equal(600f, meet.Left, 2);
        Assert.Equal(650f, meet.Right, 2);                          // the left wins; a twentieth survives
        Assert.Equal(50f, meet.Top, 2);
        Assert.Equal(75f, meet.Bottom, 2);

        // A box on the picture as it shows: the sides compose, a box drawn backwards is put the right way round.
        var first = FrameCrop.None.Within(0.25, 0.25, 0.75, 0.75);
        Assert.Equal(new FrameCrop(25, 25, 25, 25), first);
        var second = first.Within(0.5, 0, 1, 1);                    // the right half of what is left
        Assert.Equal(50, second.LeftPct, 4);
        Assert.Equal(25, second.RightPct, 4);
        Assert.Equal(25, second.TopPct, 4);
        Assert.Equal(25, second.BottomPct, 4);
        Assert.Equal(first, FrameCrop.None.Within(0.75, 0.75, 0.25, 0.25));
        Assert.Equal(new FrameCrop(0, 0, 0, 0), FrameCrop.None.Within(-1, -1, 2, 2));

        Assert.Equal("The whole picture.", FrameCrop.None.Summary());
        Assert.Equal("Keeps 50% × 50% of the picture (from 25% in, 25% down).", first.Summary());
        Assert.Equal("Keeps 75% × 100% of the picture.", new FrameCrop(0, 0, 25, 0).Summary());

        // The model clamps a side at 90 and reads the crop back; a turn wraps.
        var o = new MediaOptions { CropLeftPct = 120, CropRightPct = -3, RotateQuarters = 5 };
        Assert.Equal(90, o.CropLeftPct);
        Assert.Equal(0, o.CropRightPct);
        Assert.Equal(1, o.RotateQuarters);
        Assert.Equal(new FrameCrop(90, 0, 0, 0), o.Crop);
        Assert.True(o.HasAdjustments);
        o.RotateQuarters = -1;
        Assert.Equal(3, o.RotateQuarters);
    }

    [Fact]
    public void AStillIsCroppedMirroredAndTurnedOnTheOutputAndTheDeskGetsItsHandle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-crop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Quarters(dir);
            var state = Media(m =>
            {
                m.Source = MediaSource.Image;
                m.ImagePath = path;
                m.Fit = FitMode.Stretch;
            });

            // Whole: the four quarters where they were, and the picture's handle over the whole canvas.
            var (whole, hits) = Render(state, 200, 100);
            using (whole)
            {
                Assert.True(Is(whole.GetPixel(50, 25), SKColors.Red));
                Assert.True(Is(whole.GetPixel(150, 25), SKColors.Lime));
                Assert.True(Is(whole.GetPixel(50, 75), SKColors.Blue));
                Assert.True(Is(whole.GetPixel(150, 75), SKColors.Yellow));
            }
            var handle = Assert.Single(hits, h => h.Kind == HitKind.MediaPicture);
            Assert.Equal(SKRect.Create(0, 0, 200, 100), handle.Rect);
            Assert.False(handle.Crop.Any);

            // The top-left quarter kept: it fills the frame, and the handle carries the crop.
            state.Pattern.Media.CropRightPct = 50;
            state.Pattern.Media.CropBottomPct = 50;
            var (cropped, hits2) = Render(state, 200, 100);
            using (cropped)
            {
                Assert.True(Is(cropped.GetPixel(50, 25), SKColors.Red));
                Assert.True(Is(cropped.GetPixel(150, 75), SKColors.Red), $"the kept quarter fills the frame, got {cropped.GetPixel(150, 75)}");
            }
            Assert.Equal(new FrameCrop(0, 0, 50, 50), Assert.Single(hits2, h => h.Kind == HitKind.MediaPicture).Crop);
            state.Pattern.Media.CropRightPct = 0;
            state.Pattern.Media.CropBottomPct = 0;

            // Mirrored: left and right swap; upside down: top and bottom swap.
            state.Pattern.Media.FlipHorizontal = true;
            using (var mirrored = Render(state, 200, 100).Bmp)
            {
                Assert.True(Is(mirrored.GetPixel(50, 25), SKColors.Lime));
                Assert.True(Is(mirrored.GetPixel(150, 25), SKColors.Red));
                Assert.True(Is(mirrored.GetPixel(50, 75), SKColors.Yellow));
            }
            state.Pattern.Media.FlipHorizontal = false;
            state.Pattern.Media.FlipVertical = true;
            using (var flipped = Render(state, 200, 100).Bmp)
            {
                Assert.True(Is(flipped.GetPixel(50, 25), SKColors.Blue));
                Assert.True(Is(flipped.GetPixel(150, 75), SKColors.Lime));
            }
            state.Pattern.Media.FlipVertical = false;

            // A quarter turn clockwise, fitted: a portrait picture centred, the old left column along the top (red now top-right).
            state.Pattern.Media.RotateQuarters = 1;
            state.Pattern.Media.Fit = FitMode.Fit;
            var (turned, hits3) = Render(state, 200, 100);
            using (turned)
            {
                Assert.True(Is(turned.GetPixel(112, 25), SKColors.Red), $"top-right of the turned picture is red, got {turned.GetPixel(112, 25)}");
                Assert.True(Is(turned.GetPixel(87, 25), SKColors.Blue));
                Assert.True(Is(turned.GetPixel(112, 75), SKColors.Lime));
                Assert.True(Is(turned.GetPixel(87, 75), SKColors.Yellow));
                Assert.True(Is(turned.GetPixel(20, 50), SKColors.Black), "outside the portrait box is the background");
            }
            var portrait = Assert.Single(hits3, h => h.Kind == HitKind.MediaPicture).Rect;
            Assert.Equal(50f, portrait.Width, 1);
            Assert.Equal(100f, portrait.Height, 1);
            Assert.Equal(75f, portrait.Left, 1);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* a temp dir */ }
        }
    }

    [Fact]
    public void ALiveFeedIsCroppedThroughItsSourceAndAPageKeepsItsClicksThroughTheCrop()
    {
        var feed = new RecordingSource();
        var key = InputKeys.Ndi("cam");
        InputBus.Mount(key, feed);
        var page = new FakeWebSource(SKColors.Blue);
        var webKey = InputKeys.Web("https://example.com");
        InputBus.Mount(webKey, page);
        try
        {
            // The source draws the part that survives into a box of the cropped shape.
            var state = Media(m =>
            {
                m.Source = MediaSource.NdiFeed;
                m.NdiSourceName = "cam";
                m.Fit = FitMode.Fit;
                m.CropLeftPct = 10;
                m.CropRightPct = 30;
            });
            var (bmp, hits) = Render(state, 640, 360);
            bmp.Dispose();
            Assert.Equal(new FrameCrop(10, 0, 30, 0), feed.LastCrop);
            // 1152 × 1080 of the 1920 × 1080 frame survives: fitted into 640 × 360 it is 384 wide, centred.
            Assert.Equal(384f, feed.LastDest.Width, 1);
            Assert.Equal(360f, feed.LastDest.Height, 1);
            Assert.Equal(128f, feed.LastDest.Left, 1);
            var handle = Assert.Single(hits, h => h.Kind == HitKind.MediaPicture);
            Assert.Equal(key, handle.Key);
            Assert.Equal(feed.LastDest, handle.Rect);

            // A web page: its click box carries the crop, so a click lands on the part of the page it shows.
            var web = Media(m =>
            {
                m.Source = MediaSource.Web;
                m.WebUrl = "https://example.com";
                m.CropLeftPct = 20;
                m.CropBottomPct = 40;
            });
            var (wb, whits) = Render(web, 640, 360);
            wb.Dispose();
            var box = Assert.Single(whits, h => h.Kind == HitKind.WebPage);
            Assert.Equal(new FrameCrop(20, 0, 0, 40), box.Crop);
            var at = WebPointerMap.ToPageUnbounded(in box, new SKPoint(box.Rect.Left, box.Rect.Top));
            Assert.Equal(0.2f, at.X, 3);
            Assert.Equal(0f, at.Y, 3);
            Assert.Contains(whits, h => h.Kind == HitKind.MediaPicture && h.Key == webKey);

            // Turned, the page has no click box (the pointer needs it the right way up) but the desk still has its handle.
            web.Pattern.Media.RotateQuarters = 2;
            var (tb, thits) = Render(web, 640, 360);
            tb.Dispose();
            Assert.DoesNotContain(thits, h => h.Kind == HitKind.WebPage);
            Assert.Contains(thits, h => h.Kind == HitKind.MediaPicture);
        }
        finally
        {
            InputBus.Unmount(key);
            InputBus.Unmount(webKey);
        }
    }
}
