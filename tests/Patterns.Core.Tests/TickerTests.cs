using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The message ticker: a seamless loop (the phase wraps by one copy, never by the canvas), one
/// travel line shared by every sink, and the backgrounds behind the text.
/// </summary>
public class TickerTests
{
    // ---- pure maths ---------------------------------------------------------

    [Fact]
    public void WrapIsATrueModulo()
    {
        Assert.Equal(5, TickerMath.Wrap(25, 10));
        Assert.Equal(9, TickerMath.Wrap(-1, 10));
        Assert.Equal(0, TickerMath.Wrap(10, 10));
        Assert.Equal(0, TickerMath.Wrap(3, 0));
    }

    [Fact]
    public void TheLeadCopyAlwaysSitsWithinOnePeriodOfTheRightEdge()
    {
        const double period = 450, w = 480;
        for (var d = 0.0; d < 5000; d += 7.3)
        {
            var lead = TickerMath.LeadX(d, period, w);
            Assert.True(lead > w - period && lead <= w, $"lead {lead} at distance {d}");
        }
    }

    [Fact]
    public void WrappingMovesTheTrainByExactlyOneCopy()
    {
        // The bug: the phase wrapped modulo canvas + period, so every wrap snapped the train by
        // (canvas mod period) px — 30 px here. Modulo the period, the train one period on is the
        // same train, and the frames either side of a wrap differ by the tiny step alone.
        const double period = 450, textW = 330, w = 480;
        var before = TickerMath.CopyPositions(period - 0.5, period, textW, w).ToList();
        var after = TickerMath.CopyPositions(period + 0.5, period, textW, w).ToList();
        Assert.NotEmpty(before);
        // Every copy after the wrap is one pixel further left than a copy before it (the copy
        // just entering from the right edge was off the canvas a pixel ago and is excused).
        foreach (var x in after.Where(x => x + 1 <= w))
        {
            Assert.Contains(before, b => Math.Abs(b - 1 - x) < 1e-3);
        }
        Assert.Contains(after, x => x > w - 1);   // and the wrap did bring the next copy in
        Assert.Equal(
            TickerMath.CopyPositions(123, period, textW, w),
            TickerMath.CopyPositions(123 + 3 * period, period, textW, w));
    }

    [Fact]
    public void TheTravelLineIsContinuousAcrossASpeedChange()
    {
        var line = TickerLine.From(100);
        Assert.Equal(500, line.DistanceAt(5));

        var faster = line.WithSpeed(300, 5);
        Assert.Equal(500, faster.DistanceAt(5));
        Assert.Equal(800, faster.DistanceAt(6));

        // The same speed again is the same line — no re-anchor, no drift.
        Assert.Equal(faster, faster.WithSpeed(300, 9));
    }

    // ---- the bus ------------------------------------------------------------

    private static ShowState Ticker(double speed) => RenderTestHarness.State(s =>
    {
        s.Overlays.Message.Enabled = true;
        s.Overlays.Message.Scroll = true;
        s.Overlays.Message.ScrollPxPerSec = speed;
    });

    [Fact]
    public void TheBusReAnchorsTheLineOnlyWhenTheSpeedChanges()
    {
        var clock = 0.0;
        var state = Ticker(120);
        var bus = new SnapshotBus(state, () => clock);
        Assert.Equal(TickerLine.From(120), bus.Current.Ticker);

        clock = 5;
        state.Overlays.Message.Text = "Unrelated edit";
        bus.Publish(state);
        Assert.Equal(TickerLine.From(120), bus.Current.Ticker);
        Assert.Equal(5, bus.Current.PublishedClock);

        state.Overlays.Message.ScrollPxPerSec = 300;
        bus.Publish(state);
        var line = bus.Current.Ticker!.Value;
        Assert.Equal(600, line.DistanceAt(5));   // continuous at the join: 5 s × 120
        Assert.Equal(900, line.DistanceAt(6));   // then 300 px/s
    }

    [Fact]
    public void ASandboxSpeedNeverMovesTheProgramTrain()
    {
        var clock = 0.0;
        var program = Ticker(120);
        var bus = new SnapshotBus(program, () => clock);

        clock = 8;
        var sandbox = JsonUtil.Clone(program);
        sandbox.Overlays.Message.ScrollPxPerSec = 50;
        bus.PublishSandbox(sandbox);
        Assert.Equal(TickerLine.From(120), bus.Current.Ticker);
        var preview = bus.Sandbox!.Ticker!.Value;
        Assert.Equal(960, preview.DistanceAt(8));   // seeded from the program line
        Assert.Equal(50, preview.Speed);

        // An unrelated program publish while the sandbox is open: still the original line.
        clock = 9;
        bus.Publish(program);
        Assert.Equal(TickerLine.From(120), bus.Current.Ticker);

        // Sending the sandbox to air re-anchors the program line at the moment of the take.
        clock = 10;
        bus.ClearSandbox();
        bus.Publish(sandbox);
        var onAir = bus.Current.Ticker!.Value;
        Assert.Equal(1200, onAir.DistanceAt(10));
        Assert.Equal(1250, onAir.DistanceAt(11));
    }

    // ---- pixels -------------------------------------------------------------

    private static ShowState Scrolling(string field, MessageBackground background = MessageBackground.Auto, Anchor9 anchor = Anchor9.Center)
        => RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.FlatField;
            s.Pattern.FlatField.Color = field;
            s.Pattern.FlatField.ShowLabel = false;
            s.Pattern.FlatField.ShowBorder = false;
            s.Overlays.Message.Enabled = true;
            s.Overlays.Message.Text = "COVERAGE CHECK";
            s.Overlays.Message.TextColor = field == "#000000" ? "#FFFFFF" : "#000000";
            s.Overlays.Message.Scroll = true;
            s.Overlays.Message.ScrollPxPerSec = 240;
            s.Overlays.Message.SizePct = 20;
            s.Overlays.Message.Anchor = anchor;
            s.Overlays.Message.Background = background;
            s.Overlays.Message.BackgroundStrength = 1;
        });

    /// <summary>Columns that carry ink in the text row, as a bit per column.</summary>
    private static bool[] InkColumns(SKBitmap bmp, int rowFrom, int rowTo)
    {
        var ink = new bool[bmp.Width];
        for (var x = 0; x < bmp.Width; x++)
        {
            for (var y = rowFrom; y < rowTo && !ink[x]; y++)
            {
                if (bmp.GetPixel(x, y).Red > 128) ink[x] = true;
            }
        }
        return ink;
    }

    [Fact]
    public void AScrollingTickerIsAPureTranslationBetweenAnyTwoFrames()
    {
        // 240 px/s over a quarter second is exactly 60 px: the ink in the later frame must be the
        // earlier frame's ink moved 60 px left, at every pair of frames — including the pairs that
        // straddle the old wrap point, where the train used to jump.
        var snap = RenderTestHarness.Snap(Scrolling("#000000"));
        const int w = 480, shift = 60;
        for (var t = 0.0; t < 20; t += 0.25)
        {
            using var a = RenderTestHarness.Render(snap, w, 200, time: t);
            using var b = RenderTestHarness.Render(snap, w, 200, time: t + 0.25);
            var inkA = InkColumns(a, 80, 120);
            var inkB = InkColumns(b, 80, 120);
            var mismatches = 0;
            for (var x = 0; x < w - shift; x++)
            {
                if (inkA[x + shift] != inkB[x]) mismatches++;
            }
            Assert.True(mismatches <= 6, $"the train jumped between {t:0.00} s and {t + 0.25:0.00} s ({mismatches} columns differ)");
        }
    }

    [Fact]
    public void EverySinkDrawsTheSameTrainFromTheSnapshotLine()
    {
        // A span's two halves and an NDI sender share nothing but the snapshot: the same distance
        // must come out whatever a sink's own history. A snapshot from the bus at clock 30 renders
        // identically through two fresh sinks.
        var clock = 30.0;
        var bus = new SnapshotBus(Scrolling("#000000"), () => clock);
        using var a = RenderTestHarness.Render(bus.Current, 480, 200, time: 31.0);
        using var b = RenderTestHarness.Render(bus.Current, 480, 200, time: 31.0);
        Assert.Equal(InkColumns(a, 80, 120), InkColumns(b, 80, 120));
        Assert.Contains(InkColumns(a, 80, 120), ink => ink);
    }

    [Fact]
    public void TheFadeBandIsDarkestAtTheAnchoredEdge()
    {
        var bottom = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.FlatField;
            s.Pattern.FlatField.Color = "#FFFFFF";
            s.Pattern.FlatField.ShowLabel = false;
            s.Pattern.FlatField.ShowBorder = false;
            s.Overlays.Message.Enabled = true;
            s.Overlays.Message.Text = "HELLO";
            s.Overlays.Message.SizePct = 10;
            s.Overlays.Message.Anchor = Anchor9.BottomCenter;
            s.Overlays.Message.Background = MessageBackground.Fade;
            s.Overlays.Message.BackgroundStrength = 0.9;
        });
        using var bmp = RenderTestHarness.Render(bottom, 400, 300);
        // Far from the (centred, short) text: the bottom edge is dark, the band lightens upward,
        // the top of the picture is untouched.
        var edge = bmp.GetPixel(10, 298).Red;
        var mid = bmp.GetPixel(10, 265).Red;
        var above = bmp.GetPixel(10, 150).Red;
        Assert.True(edge < 60, $"edge {edge}");
        Assert.True(mid > edge && mid < 255, $"mid {mid}");
        Assert.Equal(255, above);
        Assert.Equal(255, bmp.GetPixel(10, 5).Red);

        bottom.Overlays.Message.Anchor = Anchor9.TopCenter;
        using var top = RenderTestHarness.Render(bottom, 400, 300);
        Assert.True(top.GetPixel(10, 1).Red < 60);
        Assert.Equal(255, top.GetPixel(10, 295).Red);

        bottom.Overlays.Message.Background = MessageBackground.None;
        using var none = RenderTestHarness.Render(bottom, 400, 300);
        Assert.Equal(255, none.GetPixel(10, 1).Red);
        Assert.Equal(255, none.GetPixel(10, 40).Red);
    }

    [Fact]
    public void ASolidBackgroundBehindATickerIsAFullWidthBar()
    {
        var snapBar = RenderTestHarness.Snap(Scrolling("#FFFFFF", MessageBackground.Chip, Anchor9.BottomCenter));
        var snapNone = RenderTestHarness.Snap(Scrolling("#FFFFFF", MessageBackground.None, Anchor9.BottomCenter));
        using var bar = RenderTestHarness.Render(snapBar, 480, 200, time: 2.0);
        using var none = RenderTestHarness.Render(snapNone, 480, 200, time: 2.0);
        // The band (1.6 × 40 px tall, 6 px above the bottom) is dark from edge to edge wherever the
        // text is not; the None render shows the field there.
        var y = 200 - 6 - 32;
        var dark = 0;
        for (var x = 0; x < 480; x++)
        {
            var p = bar.GetPixel(x, y).Red;
            Assert.True(p <= none.GetPixel(x, y).Red, $"column {x} is brighter with the bar");
            if (p < 40) dark++;
        }
        Assert.True(dark > 200, $"only {dark} dark columns");
        // Auto stays as it was: nothing behind a ticker.
        using var auto = RenderTestHarness.Render(RenderTestHarness.Snap(Scrolling("#FFFFFF", MessageBackground.Auto, Anchor9.BottomCenter)), 480, 200, time: 2.0);
        Assert.Equal(none.GetPixel(2, y), auto.GetPixel(2, y));
    }
}
