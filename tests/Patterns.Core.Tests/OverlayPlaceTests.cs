using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 20: "When an overlay is repositioned by drag and drop, it affects the center point of the
/// sliders. It needs to stay relative." The drop is told from the nearest anchor — the same pixels,
/// so nothing moves, but the Nudge sliders come back to counting from a corner or an edge that is
/// still there at another size and on a canvas of another shape. Plus the pixel arithmetic the
/// pages' Place X / Y fields write through.
/// </summary>
public class OverlayPlaceTests
{
    private static readonly SKSizeI Hd = new(1920, 1080);

    private static SKRect Box(SKSizeI canvas, float w, float h, Anchor9 anchor, double offX, double offY)
        => DrawUtil.Anchored(canvas, w, h, anchor, OverlayPlace.MarginFor(canvas), offX, offY);

    [Fact]
    public void TheMarginAndTheBasesAreTheRenderersOwn()
    {
        var margin = OverlayPlace.MarginFor(Hd);
        Assert.Equal(1080 * 0.03f, margin, 3);
        Assert.Equal(10f, OverlayPlace.MarginFor(new SKSizeI(320, 180)));   // never thinner than ten pixels

        // Left holds the left edge, right the right, centre the centre — whatever the box's size.
        Assert.Equal(margin, OverlayPlace.BaseLeft(Hd, 400, Anchor9.TopLeft, margin), 3);
        Assert.Equal(1920 - margin - 400, OverlayPlace.BaseLeft(Hd, 400, Anchor9.BottomRight, margin), 3);
        Assert.Equal((1920 - 400) / 2f, OverlayPlace.BaseLeft(Hd, 400, Anchor9.Center, margin), 3);
        Assert.Equal(margin, OverlayPlace.BaseTop(Hd, 200, Anchor9.TopCenter, margin), 3);
        Assert.Equal(1080 - margin - 200, OverlayPlace.BaseTop(Hd, 200, Anchor9.BottomCenter, margin), 3);

        // Anchor9 is a three-by-three grid and the two readings agree.
        foreach (Anchor9 a in Enum.GetValues(typeof(Anchor9)))
        {
            var (col, row) = OverlayPlace.GridOf(a);
            Assert.Equal(a, OverlayPlace.AnchorOf(col, row));
        }
    }

    [Fact]
    public void ReanchoringMovesNothingAtAll()
    {
        // A chip dragged from the middle towards the bottom-right: whatever anchor it ends up told
        // from, it must occupy exactly the pixels it occupied before.
        var margin = OverlayPlace.MarginFor(Hd);
        foreach (Anchor9 from in Enum.GetValues(typeof(Anchor9)))
        {
            foreach (var (ox, oy) in new[] { (0.0, 0.0), (12.5, -8.0), (-30.0, 22.0), (44.0, 41.0), (-45.0, -44.0) })
            {
                var before = Box(Hd, 360, 140, from, ox, oy);
                var (anchor, x, y) = OverlayPlace.Reanchor(Hd, before, margin);
                var after = Box(Hd, 360, 140, anchor, x, y);
                Assert.Equal(before.Left, after.Left, 2);
                Assert.Equal(before.Top, after.Top, 2);
                Assert.Equal(before.Width, after.Width, 2);
                Assert.Equal(before.Height, after.Height, 2);
            }
        }
    }

    [Fact]
    public void TheDropIsToldFromTheCornerItLandedIn()
    {
        var margin = OverlayPlace.MarginFor(Hd);

        // Dropped exactly in the bottom-right: that corner, and a nudge of nothing — so it stays in
        // the corner on a 32:9 wall and when the box's own size changes.
        var corner = Box(Hd, 360, 140, Anchor9.BottomRight, 0, 0);
        var (anchor, x, y) = OverlayPlace.Reanchor(Hd, corner, margin);
        Assert.Equal(Anchor9.BottomRight, anchor);
        Assert.Equal(0, x, 3);
        Assert.Equal(0, y, 3);

        // The same pixels reached from the middle used to read +40 / +42 from Center — a
        // displacement, not a place. Now they read as the corner.
        var fromCentre = OverlayPlace.Reanchor(Hd, Box(Hd, 360, 140, Anchor9.Center, 41.7, 42.5), margin);
        Assert.Equal(Anchor9.BottomRight, fromCentre.Anchor);
        Assert.True(Math.Abs(fromCentre.OffsetXPct) < 3, $"a small nudge, not {fromCentre.OffsetXPct}");
        Assert.True(Math.Abs(fromCentre.OffsetYPct) < 3, $"a small nudge, not {fromCentre.OffsetYPct}");

        // Dropped in the middle: the centre, near zero.
        var middle = OverlayPlace.Reanchor(Hd, Box(Hd, 360, 140, Anchor9.Center, 0, 0), margin);
        Assert.Equal(Anchor9.Center, middle.Anchor);
        Assert.Equal(0, middle.OffsetXPct, 3);
        Assert.Equal(0, middle.OffsetYPct, 3);

        // Top-centre: the row and the column are chosen apart, so a title bar reads as top-centre.
        var top = OverlayPlace.Reanchor(Hd, Box(Hd, 360, 140, Anchor9.TopCenter, 0, 0), margin);
        Assert.Equal(Anchor9.TopCenter, top.Anchor);
    }

    [Fact]
    public void ACornerStaysACornerOnAWallOfAnotherShape()
    {
        // The point of re-anchoring: the same drop, read on a 32:9 canvas, is still in the corner.
        var wide = new SKSizeI(5760, 1080);
        var hdMargin = OverlayPlace.MarginFor(Hd);
        var wideMargin = OverlayPlace.MarginFor(wide);

        // Dropped in the bottom-right corner of an HD preview.
        var dropped = Box(Hd, 360, 140, Anchor9.BottomRight, 0, 0);
        var told = OverlayPlace.Reanchor(Hd, dropped, hdMargin);

        var onWide = DrawUtil.Anchored(wide, 360, 140, told.Anchor, wideMargin, told.OffsetXPct, told.OffsetYPct);
        var cornerOnWide = DrawUtil.Anchored(wide, 360, 140, Anchor9.BottomRight, wideMargin);
        Assert.Equal(cornerOnWide.Left, onWide.Left, 2);   // still the corner on a canvas three times as wide
        Assert.Equal(cornerOnWide.Top, onWide.Top, 2);

        // The same drop told the old way — kept as a nudge from the middle, which is what the
        // sliders read before — lands hundreds of pixels short, and further out the wider the wall.
        var fromMiddle = (dropped.Left - OverlayPlace.BaseLeft(Hd, 360, Anchor9.Center, hdMargin)) * 100.0 / Hd.Width;
        var oldWay = DrawUtil.Anchored(wide, 360, 140, Anchor9.Center, wideMargin, fromMiddle, 0);
        var oldMiss = Math.Abs(oldWay.Left - cornerOnWide.Left);
        Assert.True(oldMiss > 400, $"the nudge from the middle misses the corner by {oldMiss:0} px");
    }

    [Fact]
    public void TheBoxGrowingKeepsTheAnchorItWasToldFrom()
    {
        // A countdown's digits narrow from 10:00 to 9:59, and its Size slider moves: told from the
        // corner it was dropped in, the box keeps that corner instead of sliding.
        var margin = OverlayPlace.MarginFor(Hd);
        var small = Box(Hd, 300, 120, Anchor9.BottomRight, 0, 0);
        var big = Box(Hd, 520, 200, Anchor9.BottomRight, 0, 0);
        Assert.Equal(small.Right, big.Right, 2);
        Assert.Equal(small.Bottom, big.Bottom, 2);
    }

    [Fact]
    public void TypingPixelsLandsTheBoxOnThosePixels()
    {
        var margin = OverlayPlace.MarginFor(Hd);
        foreach (Anchor9 anchor in Enum.GetValues(typeof(Anchor9)))
        {
            var (x, y) = OverlayPlace.NudgeForTopLeft(Hd, 400, 160, anchor, 640, 300, margin);
            var box = Box(Hd, 400, 160, anchor, x, y);
            Assert.Equal(640, box.Left, 2);
            Assert.Equal(300, box.Top, 2);
        }

        // The nudge keeps the sliders' own limits, and a canvas with no size answers with nothing.
        Assert.Equal(100, OverlayPlace.Clamp(400));
        Assert.Equal(-100, OverlayPlace.Clamp(-400));
        Assert.Equal((0.0, 0.0), OverlayPlace.NudgeForTopLeft(new SKSizeI(0, 0), 10, 10, Anchor9.Center, 5, 5, 4));
        Assert.Equal(Anchor9.Center, OverlayPlace.Reanchor(new SKSizeI(0, 0), SKRect.Create(0, 0, 4, 4), 4).Anchor);
    }

    [Fact]
    public void ATickerKeepsTheColumnItWasGiven()
    {
        // The message as a ticker takes the full width, so no column is nearer than another: the
        // axis it fills keeps the anchor it had, or a vertical drag would quietly re-centre a chip
        // the operator had set to the left for when it stops scrolling.
        var margin = OverlayPlace.MarginFor(Hd);
        var band = DrawUtil.Anchored(Hd, Hd.Width, 90, Anchor9.BottomLeft, margin, 0, -12);

        var kept = OverlayPlace.Reanchor(Hd, band, margin, Anchor9.BottomLeft);
        Assert.Equal(0, OverlayPlace.GridOf(kept.Anchor).Col);        // still the left column
        var back = DrawUtil.Anchored(Hd, Hd.Width, 90, kept.Anchor, margin, kept.OffsetXPct, kept.OffsetYPct);
        Assert.Equal(band.Top, back.Top, 2);                          // and exactly where it was

        // Told nothing about the anchor it has, it falls back to the nearest — which is still exact,
        // just not necessarily the column the operator chose. The drag always passes the current one.
        var guessed = OverlayPlace.Reanchor(Hd, band, margin);
        var guessedBox = DrawUtil.Anchored(Hd, Hd.Width, 90, guessed.Anchor, margin, guessed.OffsetXPct, guessed.OffsetYPct);
        Assert.Equal(band.Left, guessedBox.Left, 2);
        Assert.Equal(band.Top, guessedBox.Top, 2);
    }

    [Fact]
    public void TheTopLeftReadingIsWhereTheBoxIs()
    {
        var margin = OverlayPlace.MarginFor(Hd);
        var point = OverlayPlace.TopLeftOf(Hd, 400, 160, Anchor9.TopLeft, 10, 5, margin);
        var box = Box(Hd, 400, 160, Anchor9.TopLeft, 10, 5);
        Assert.Equal(box.Left, point.X, 2);
        Assert.Equal(box.Top, point.Y, 2);
    }
}
