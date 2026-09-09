using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// Where an overlay sits, and the arithmetic that keeps a drag honest.
///
/// An overlay's place is an <see cref="Anchor9"/> — which edge or centre of the canvas the box is
/// held to — and a nudge from it as a share of the canvas. The anchor is the point the sliders
/// count from, so a drag that leaves the anchor behind leaves the sliders counting from somewhere
/// the operator can no longer see: drop a chip in the bottom-right from a Center anchor and its
/// nudge reads +40 / +42, which is not a place but a displacement, and it lands somewhere else
/// again on a canvas of another shape or when the box's own size changes.
///
/// So a drag ends by re-anchoring: the same pixels, told from the nearest anchor. The box does not
/// move — <see cref="Reanchor"/> is exact — the sliders simply come back to reading a small nudge
/// from a corner or an edge that is still there on a 32:9 wall and at any size. Pure, so the drag,
/// the pages' pixel fields and the tests all agree.
/// </summary>
public static class OverlayPlace
{
    /// <summary>The margin every canvas overlay keeps from the edge it is anchored to (the renderer's own rule).</summary>
    public static float MarginFor(SKSizeI canvas) => Math.Max(10f, canvas.Height * 0.03f);

    /// <summary>The PiP inset is drawn per viewport and keeps a margin of its own.</summary>
    public static float PipMarginFor(SKSizeI viewport) => Math.Max(8f, viewport.Height * 0.02f);

    /// <summary>A chip (the message standing still, the info badge) sits closer in — <see cref="DrawUtil.ChipBounds"/>'s own default.</summary>
    public static float ChipMarginFor(SKSizeI canvas) => Math.Max(8f, canvas.Height * 0.02f);

    /// <summary>The left of a box of width <paramref name="w"/> held to <paramref name="anchor"/> with no nudge.</summary>
    public static float BaseLeft(SKSizeI canvas, float w, Anchor9 anchor, float margin) => anchor switch
    {
        Anchor9.TopLeft or Anchor9.MiddleLeft or Anchor9.BottomLeft => margin,
        Anchor9.TopRight or Anchor9.MiddleRight or Anchor9.BottomRight => canvas.Width - margin - w,
        _ => (canvas.Width - w) / 2f,
    };

    /// <summary>The top of a box of height <paramref name="h"/> held to <paramref name="anchor"/> with no nudge.</summary>
    public static float BaseTop(SKSizeI canvas, float h, Anchor9 anchor, float margin) => anchor switch
    {
        Anchor9.TopLeft or Anchor9.TopCenter or Anchor9.TopRight => margin,
        Anchor9.BottomLeft or Anchor9.BottomCenter or Anchor9.BottomRight => canvas.Height - margin - h,
        _ => (canvas.Height - h) / 2f,
    };

    /// <summary>The nine anchors as their column (0 left, 1 centre, 2 right) and row (0 top, 1 middle, 2 bottom).</summary>
    public static (int Col, int Row) GridOf(Anchor9 anchor) => ((int)anchor % 3, (int)anchor / 3);

    /// <summary>The anchor a column and a row name.</summary>
    public static Anchor9 AnchorOf(int col, int row) => (Anchor9)(row * 3 + col);

    /// <summary>Where the box actually is: its top-left in canvas pixels, for a place and a box size.</summary>
    public static SKPoint TopLeftOf(SKSizeI canvas, float w, float h, Anchor9 anchor, double offsetXPct, double offsetYPct, float margin)
        => new(
            BaseLeft(canvas, w, anchor, margin) + (float)(canvas.Width * offsetXPct / 100),
            BaseTop(canvas, h, anchor, margin) + (float)(canvas.Height * offsetYPct / 100));

    /// <summary>
    /// The same box, told from the nearest anchor: the anchor whose own column and row leave the
    /// smallest nudge, and the nudge that still lands the box on exactly these pixels. Nothing
    /// moves — the reading changes, not the picture — so a chip dropped in a corner reads as that
    /// corner with a nudge near zero, and stays in it on a canvas of another shape.
    /// </summary>
    /// <param name="current">
    /// The anchor it has now. A box that spans the canvas — the message as a ticker takes the full
    /// width — has no nearest column to speak of (every anchor puts it in the same place), so the
    /// axis it fills keeps the anchor it had rather than drifting to the centre and changing what
    /// the same overlay does when it stops scrolling.
    /// </param>
    public static (Anchor9 Anchor, double OffsetXPct, double OffsetYPct) Reanchor(
        SKSizeI canvas, SKRect box, float margin, Anchor9? current = null)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0) return (current ?? Anchor9.Center, 0, 0);

        var w = box.Width;
        var h = box.Height;
        var spansWidth = current is not null && w >= canvas.Width - 1;
        var spansHeight = current is not null && h >= canvas.Height - 1;
        var col = spansWidth ? GridOf(current!.Value).Col : NearestColumn(canvas, box.Left, w, margin);
        var row = spansHeight ? GridOf(current!.Value).Row : NearestRow(canvas, box.Top, h, margin);
        var anchor = AnchorOf(col, row);
        var offX = (box.Left - BaseLeft(canvas, w, anchor, margin)) * 100.0 / canvas.Width;
        var offY = (box.Top - BaseTop(canvas, h, anchor, margin)) * 100.0 / canvas.Height;
        return (anchor, Clamp(offX), Clamp(offY));
    }

    private static int NearestColumn(SKSizeI canvas, float left, float w, float margin)
    {
        var best = 1;
        var bestDistance = float.MaxValue;
        for (var col = 0; col < 3; col++)
        {
            var candidate = AnchorOf(col, 0);
            var distance = Math.Abs(left - BaseLeft(canvas, w, candidate, margin));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = col;
            }
        }
        return best;
    }

    private static int NearestRow(SKSizeI canvas, float top, float h, float margin)
    {
        var best = 1;
        var bestDistance = float.MaxValue;
        for (var row = 0; row < 3; row++)
        {
            var candidate = AnchorOf(0, row);
            var distance = Math.Abs(top - BaseTop(canvas, h, candidate, margin));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = row;
            }
        }
        return best;
    }

    /// <summary>
    /// The nudge that puts the box's top-left on these canvas pixels, for the anchor it has. The
    /// pages' pixel fields write through this, so typing a number and dragging mean the same thing.
    /// </summary>
    public static (double OffsetXPct, double OffsetYPct) NudgeForTopLeft(
        SKSizeI canvas, float w, float h, Anchor9 anchor, double leftPx, double topPx, float margin)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0) return (0, 0);
        return (
            Clamp((leftPx - BaseLeft(canvas, w, anchor, margin)) * 100.0 / canvas.Width),
            Clamp((topPx - BaseTop(canvas, h, anchor, margin)) * 100.0 / canvas.Height));
    }

    /// <summary>The nudge sliders' own limits — a place outside the canvas is still a place, but not an unbounded one.</summary>
    public static double Clamp(double pct) => Math.Clamp(pct, -100, 100);
}
