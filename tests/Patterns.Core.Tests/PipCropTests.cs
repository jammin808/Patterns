using Patterns.Core.Media;
using Patterns.Core.Model;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The PiP inset's crop: the rect maths, the crop reaching the source, the cropped shape, and a source that knows no crop.</summary>
[Collection("InputBus")]
public class PipCropTests
{
    private sealed class RecordingSource : IVideoFrameSource
    {
        public FrameCrop? LastCrop;
        public SKRect LastDest;
        public int Calls;

        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
        {
            Calls++;
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

    /// <summary>A source from before crops existed: only the three-argument draw.</summary>
    private sealed class LegacySource : IVideoFrameSource
    {
        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
        {
            canvas.DrawRect(dest, new SKPaint { Color = SKColors.Blue });
            return true;
        }

        public SKSizeI? FrameSize => new SKSizeI(1920, 1080);
        public bool IsPlaying => true;
        public bool IsEnded => false;
        public double DurationSeconds => 0;
        public string StatusText => "legacy";
    }

    [Fact]
    public void TheSourceRectIsTheShareThatSurvivesAndNeverLessThanATenth()
    {
        var frame = new SKSizeI(1920, 1080);
        Assert.Equal(new SKRect(0, 0, 1920, 1080), FrameCrop.None.SourceRect(frame));
        Assert.False(FrameCrop.None.Any);

        var crop = new FrameCrop(10, 0, 20, 5);
        Assert.True(crop.Any);
        Assert.Equal(new SKRect(192, 0, 1536, 1026), crop.SourceRect(frame));
        Assert.Equal(1344f / 1026f, crop.AspectOf(frame), 4);

        // Opposite sides at the inset's ceiling still leave a tenth; wild values are clamped to a
        // side's 90 %, and two sides that would meet leave a twentieth (the left wins).
        var tight = new FrameCrop(45, 45, 45, 45).SourceRect(frame);
        Assert.Equal(192f, tight.Width, 2);
        Assert.Equal(108f, tight.Height, 2);
        var wild = new FrameCrop(90, -5, 200, 0).SourceRect(frame);
        Assert.Equal(96f, wild.Width, 2);
        Assert.Equal(1728f, wild.Left, 2);
        Assert.Equal(1080f, wild.Height, 2);

        var pip = new PipOverlay { CropLeftPct = 80, CropTopPct = -3, CropRightPct = 12.5, CropBottomPct = 0 };
        Assert.Equal(new FrameCrop(45, 0, 12.5, 0), FrameCrop.From(pip));
    }

    private static ShowState PipState(string ndiName, Action<PipOverlay>? mutate = null) => RenderTestHarness.State(s =>
    {
        s.Pattern.Kind = PatternKind.FlatField;
        s.Pattern.FlatField.Color = "#000000";
        s.Pattern.FlatField.ShowLabel = false;
        s.Pattern.FlatField.ShowBorder = false;
        s.Overlays.Pip.Enabled = true;
        s.Overlays.Pip.Source = PipSource.NdiFeed;
        s.Overlays.Pip.NdiSourceName = ndiName;
        s.Overlays.Pip.WidthPct = 25;
        s.Overlays.Pip.Anchor = Anchor9.BottomRight;
        s.Overlays.Pip.ShowBorder = false;
        mutate?.Invoke(s.Overlays.Pip);
    });

    [Fact]
    public void TheInsetHandsTheCropToTheSourceAndTakesTheCroppedShape()
    {
        InputBus.Clear();
        var source = new RecordingSource();
        try
        {
            InputBus.Mount(InputKeys.Ndi("Cam"), source);
            var state = PipState("Cam", p =>
            {
                p.CropLeftPct = 20;
                p.CropRightPct = 20;
            });
            using var bmp = RenderTestHarness.Render(state, 800, 450);

            Assert.Equal(new FrameCrop(20, 0, 20, 0), source.LastCrop);
            Assert.Equal(200f, source.LastDest.Width, 1);              // 25 % of 800
            Assert.Equal(200f / (1152f / 1080f), source.LastDest.Height, 1); // the cropped 1152×1080 shape, not 16:9
            Assert.Equal(SKColors.Red, bmp.GetPixel(700, 380));

            // Uncropped, the same inset is 16:9 again.
            var plain = PipState("Cam");
            using var _ = RenderTestHarness.Render(plain, 800, 450);
            Assert.Equal(FrameCrop.None, source.LastCrop);
            Assert.Equal(112.5f, source.LastDest.Height, 1);
        }
        finally
        {
            InputBus.Clear();
        }
    }

    [Fact]
    public void ASourceThatKnowsNoCropStillDraws()
    {
        InputBus.Clear();
        try
        {
            InputBus.Mount(InputKeys.Ndi("Old"), new LegacySource());
            var state = PipState("Old", p => p.CropTopPct = 30);
            using var bmp = RenderTestHarness.Render(state, 800, 450);
            // Bottom-right, 200 px wide, shaped by the crop (1920×756 → 200×78.75), 9 px in from the edges.
            Assert.Equal(SKColors.Blue, bmp.GetPixel(700, 400));
            Assert.Equal(SKColors.Black, bmp.GetPixel(700, 340));
        }
        finally
        {
            InputBus.Clear();
        }
    }
}
