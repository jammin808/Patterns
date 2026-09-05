using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Patterns;

/// <summary>
/// LED wall mapping pattern: per-tile borders, numbering in data-run order, optional
/// pixel grid. Canvas is either columns×rows of tiles or a given raster with derived
/// (possibly partial) edge tiles — exactly like a real wall. On a screen whose raster this
/// wall is, and which has dead strips (the air between pillars), the tiles lay out across
/// the strips and the strips are drawn as what they are: nothing.
/// </summary>
public sealed class LedWallPattern : IPatternRenderer
{
    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.LedWall;
        if (o.UseCustomMap && o.CustomTiles.Count > 0)
        {
            RenderCustomMap(c, in f, o);
            return;
        }

        var layout = CanvasResolver.Led(o);
        var g = CanvasResolver.WallSpansGaps(f.Config, f.Gaps, layout.Canvas) ? f.Gaps : GapMap.Empty;
        var pc = f.Paints;
        int w = f.W, h = f.H;                       // the canvas: the surface with the strips put back when g has any
        int rw = layout.Canvas.Width, rh = layout.Canvas.Height;   // the raster the tiles are counted in

        c.Clear(f.Palette.Bg);

        // Subtle alternating tile tint.
        if (o.AlternateTint)
        {
            var tintA = f.Palette.Branded ? f.Palette.Accent.WithAlpha(0x24) : new SKColor(0x3E, 0xC1, 0xF3, 0x1E);
            var tintB = f.Palette.Branded ? f.Palette.Secondary.WithAlpha(0x24) : new SKColor(0xF0, 0x3E, 0xAE, 0x1E);
            for (var r = 0; r < layout.Rows; r++)
            {
                for (var col = 0; col < layout.Columns; col++)
                {
                    var rect = TileRect(layout, col, r, rw, rh, g);
                    if (rect.IsEmpty) continue;
                    c.DrawRect(rect, pc.Fill(((r + col) & 1) == 0 ? tintA : tintB));
                }
            }
        }

        // Faint pixel grid aligned to tile origins.
        if (o.ShowPixelGrid && o.PixelGridStep >= 2)
        {
            var faint = pc.Fill(new SKColor(0xFF, 0xFF, 0xFF, 0x28));
            for (var x = 0; x < w; x += o.PixelGridStep)
            {
                DrawUtil.LineV(c, x, 0, h, 1, faint);
            }
            for (var y = 0; y < h; y += o.PixelGridStep)
            {
                DrawUtil.LineH(c, y, 0, w, 1, faint);
            }
        }

        if (o.ShowTileDiagonals)
        {
            var diag = pc.StrokeAA(new SKColor(0xFF, 0xFF, 0xFF, 0x50), 1);
            for (var r = 0; r < layout.Rows; r++)
            {
                for (var col = 0; col < layout.Columns; col++)
                {
                    var rect = TileRect(layout, col, r, rw, rh, g);
                    if (rect.IsEmpty) continue;
                    c.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, diag);
                    c.DrawLine(rect.Right, rect.Top, rect.Left, rect.Bottom, diag);
                }
            }
        }

        if (o.ShowTileBorders)
        {
            var line = pc.Fill(f.Palette.Line);
            if (g.IsEmpty)
            {
                // Tile borders as global 1px grid lines — the outermost lines sit on the canvas edge pixels.
                for (var col = 0; col <= layout.Columns; col++)
                {
                    var x = Math.Min(col * layout.TileWidth, w - 1);
                    DrawUtil.LineV(c, x, 0, h, 1, line);
                }
                for (var r = 0; r <= layout.Rows; r++)
                {
                    var y = Math.Min(r * layout.TileHeight, h - 1);
                    DrawUtil.LineH(c, y, 0, w, 1, line);
                }
            }
            else
            {
                // Past a strip the grid lines no longer share an x, so each tile draws its own border.
                for (var r = 0; r < layout.Rows; r++)
                {
                    for (var col = 0; col < layout.Columns; col++)
                    {
                        var rect = TileRect(layout, col, r, rw, rh, g);
                        if (rect.IsEmpty) continue;
                        DrawUtil.BorderInside(c, SKRectI.Round(rect), 1, line);
                    }
                }
            }
        }

        // Tile numbers (skipped when tiles are too small to stay legible).
        if (layout.TileWidth >= 24 && layout.TileHeight >= 16)
        {
            var font = pc.FontBold;
            font.Size = Math.Clamp(Math.Min(layout.TileWidth, layout.TileHeight) * 0.3f, 7, 64);
            for (var r = 0; r < layout.Rows; r++)
            {
                for (var col = 0; col < layout.Columns; col++)
                {
                    var rect = TileRect(layout, col, r, rw, rh, g);
                    if (rect.Width < 20 || rect.Height < 12) continue;
                    var label = TileLabel(o.Numbering, layout, col, r);
                    // Shadowed text — readable on any tint without a chip per tile.
                    DrawUtil.TextCentered(c, label, rect.MidX + 1, rect.MidY + 1, font, pc.Text(SKColors.Black));
                    DrawUtil.TextCentered(c, label, rect.MidX, rect.MidY, font, pc.Text(f.Palette.Text));
                }
            }
        }

        if (o.ShowCenterCross)
        {
            DrawUtil.Cross(c, w / 2, h / 2, Math.Min(w, h) / 6, 3, pc.Fill(f.Palette.Accent));
        }

        WallStrips.Draw(c, in f, g);

        if (o.ShowInfo)
        {
            var partial = o.DefineByCanvas &&
                          (layout.Columns * layout.TileWidth != rw || layout.Rows * layout.TileHeight != rh);
            var info = $"{layout.Columns} × {layout.Rows} tiles · {layout.TileWidth}×{layout.TileHeight} px · {rw}×{rh}{(partial ? " · partial edge tiles" : "")}{WallStrips.Words(g)}";
            DrawUtil.Chip(c, info, f.Canvas, Anchor9.BottomCenter, GridPattern.ChipText(f), pc,
                f.Palette.Text, f.Palette.ChipBg);
        }
    }

    /// <summary>Irregular wall: explicit tiles with mixed sizes, offsets and gaps.</summary>
    private static void RenderCustomMap(SKCanvas c, in PatternFrame f, LedWallOptions o)
    {
        var pc = f.Paints;
        c.Clear(f.Palette.Bg);

        var tintA = f.Palette.Branded ? f.Palette.Accent.WithAlpha(0x24) : new SKColor(0x3E, 0xC1, 0xF3, 0x1E);
        var tintB = f.Palette.Branded ? f.Palette.Secondary.WithAlpha(0x24) : new SKColor(0xF0, 0x3E, 0xAE, 0x1E);
        var line = pc.Fill(f.Palette.Line);

        for (var i = 0; i < o.CustomTiles.Count; i++)
        {
            var t = o.CustomTiles[i];
            var rect = new SKRectI(t.X, t.Y, t.X + t.Width, t.Y + t.Height);

            if (o.AlternateTint)
            {
                c.DrawRect(SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height),
                    pc.Fill((i & 1) == 0 ? tintA : tintB));
            }

            if (o.ShowTileDiagonals)
            {
                var diag = pc.StrokeAA(new SKColor(0xFF, 0xFF, 0xFF, 0x50), 1);
                c.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, diag);
                c.DrawLine(rect.Right, rect.Top, rect.Left, rect.Bottom, diag);
            }

            if (o.ShowTileBorders)
            {
                DrawUtil.BorderInside(c, rect, 1, line);
            }

            if (t.Width >= 24 && t.Height >= 16)
            {
                var label = string.IsNullOrWhiteSpace(t.Label) ? (i + 1).ToString() : t.Label;
                var font = pc.FontBold;
                font.Size = Math.Clamp(Math.Min(t.Width, t.Height) * 0.3f, 7, 64);
                DrawUtil.TextCentered(c, label, rect.MidX + 1, rect.MidY + 1, font, pc.Text(SKColors.Black));
                DrawUtil.TextCentered(c, label, rect.MidX, rect.MidY, font, pc.Text(f.Palette.Text));

                var sub = pc.FontRegular;
                sub.Size = Math.Clamp(Math.Min(t.Width, t.Height) * 0.12f, 6, 22);
                DrawUtil.TextCentered(c, $"{t.Width}×{t.Height}", rect.MidX, rect.MidY + font.Size * 0.75f, sub,
                    pc.Text(new SKColor(0xFF, 0xFF, 0xFF, 0xA0)));
            }
        }

        if (o.ShowCenterCross)
        {
            DrawUtil.Cross(c, f.W / 2, f.H / 2, Math.Min(f.W, f.H) / 6, 3, pc.Fill(f.Palette.Accent));
        }

        if (o.ShowInfo)
        {
            DrawUtil.Chip(c, $"{o.CustomTiles.Count} tiles · irregular map · {f.W}×{f.H}",
                f.Canvas, Anchor9.BottomCenter, GridPattern.ChipText(f), pc, f.Palette.Text, f.Palette.ChipBg);
        }
    }

    /// <summary>A tile's rect on the canvas: its raster place, moved past the strips before it when the wall has any. Shared with tests.</summary>
    public static SKRect TileRect(LedLayout l, int col, int row, int w, int h, GapMap g)
    {
        var x0 = col * l.TileWidth;
        var y0 = row * l.TileHeight;
        var x1 = Math.Min(x0 + l.TileWidth, w);
        var y1 = Math.Min(y0 + l.TileHeight, h);
        if (x1 <= x0 || y1 <= y0) return SKRect.Empty;
        if (g.IsEmpty) return new SKRect(x0, y0, x1, y1);
        var v = g.VirtualRect(new SKRectI(x0, y0, x1, y1));
        return new SKRect(v.Left, v.Top, v.Right, v.Bottom);
    }

    /// <summary>Label for a tile — shared with tests.</summary>
    public static string TileLabel(TileNumbering numbering, LedLayout l, int col, int row) => numbering switch
    {
        TileNumbering.Linear => (row * l.Columns + col + 1).ToString(),
        // Column-major snake: data runs top-down, next column bottom-up.
        TileNumbering.Serpentine => (col * l.Rows + ((col & 1) == 0 ? row : l.Rows - 1 - row) + 1).ToString(),
        _ => $"{row + 1}-{col + 1}",
    };
}

/// <summary>
/// Video wall of standard-resolution display elements with bezel visualisation. On a screen
/// (or a joined canvas) whose raster this wall is, and which has dead strips — the bezels the
/// Screens page compensates — the elements lay out past the strips, and a ring and the wall's
/// diagonals are drawn across all of them: round and straight in the room when the wall is
/// compensated, stepped at every bezel when it is not.
/// </summary>
public sealed class VideoWallPattern : IPatternRenderer
{
    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.VideoWall;
        var pc = f.Paints;
        int w = f.W, h = f.H;
        var (ew, eh) = o.Portrait ? (o.ElementHeight, o.ElementWidth) : (o.ElementWidth, o.ElementHeight);
        var g = CanvasResolver.WallSpansGaps(f.Config, f.Gaps, CanvasResolver.VideoWall(o)) ? f.Gaps : GapMap.Empty;

        c.Clear(f.Palette.Bg);

        var num = 1;
        for (var r = 0; r < o.Rows; r++)
        {
            for (var col = 0; col < o.Columns; col++, num++)
            {
                var rect = ElementRect(col, r, ew, eh, g);

                if (o.ShowDiagonals)
                {
                    var diag = pc.StrokeAA(new SKColor(0xFF, 0xFF, 0xFF, 0x46), 1);
                    c.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, diag);
                    c.DrawLine(rect.Right, rect.Top, rect.Left, rect.Bottom, diag);
                }

                if (o.ShowCenters)
                {
                    var cxE = rect.MidX;
                    var cyE = rect.MidY;
                    c.DrawCircle(cxE, cyE, Math.Min(ew, eh) * 0.3f, pc.StrokeAA(f.Palette.Line, 2));
                    DrawUtil.Cross(c, (int)cxE, (int)cyE, Math.Min(ew, eh) / 10, 1, pc.Fill(f.Palette.Line));
                }

                // Bezel loss zone, hatched inside each edge.
                if (o.BezelPx > 0)
                {
                    var hatchPaint = pc.StrokeAA(new SKColor(0xFF, 0xB0, 0x20, 0x66), 1);
                    var b = Math.Min(o.BezelPx, Math.Min(ew, eh) / 4);
                    DrawUtil.Hatch(c, new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + b), 8, hatchPaint);
                    DrawUtil.Hatch(c, new SKRect(rect.Left, rect.Bottom - b, rect.Right, rect.Bottom), 8, hatchPaint);
                    DrawUtil.Hatch(c, new SKRect(rect.Left, rect.Top + b, rect.Left + b, rect.Bottom - b), 8, hatchPaint);
                    DrawUtil.Hatch(c, new SKRect(rect.Right - b, rect.Top + b, rect.Right, rect.Bottom - b), 8, hatchPaint);
                }

                if (o.ShowBorders)
                {
                    DrawUtil.BorderInside(c, rect, 2, pc.Fill(f.Palette.Line));
                }

                if (o.ShowNumbers)
                {
                    var font = pc.FontBold;
                    font.Size = Math.Clamp(Math.Min(ew, eh) * 0.28f, 10, 300);
                    DrawUtil.TextCentered(c, num.ToString(), rect.MidX + 2, rect.MidY + 2, font, pc.Text(SKColors.Black));
                    DrawUtil.TextCentered(c, num.ToString(), rect.MidX, rect.MidY, font, pc.Text(f.Palette.Accent));

                    var sub = pc.FontRegular;
                    sub.Size = Math.Clamp(Math.Min(ew, eh) * 0.06f, 8, 40);
                    DrawUtil.TextCentered(c, $"{ew}×{eh}", rect.MidX, rect.MidY + font.Size * 0.72f, sub, pc.Text(f.Palette.Text));
                }
            }
        }

        if (!g.IsEmpty)
        {
            // The compensation check: one ring and two diagonals across the whole wall. Drawn on
            // the surface with the bezels put back, so on a compensated wall they read as one
            // round ring and two straight lines through every display in the room.
            var ring = pc.StrokeAA(f.Palette.Accent, 3);
            c.DrawCircle(w / 2f, h / 2f, Math.Min(w, h) * 0.42f, ring);
            var across = pc.StrokeAA(f.Palette.Accent.WithAlpha(0xB0), 2);
            c.DrawLine(0, 0, w, h, across);
            c.DrawLine(w, 0, 0, h, across);
        }

        DrawUtil.Cross(c, w / 2, h / 2, Math.Min(w, h) / 10, 3, pc.Fill(f.Palette.Accent));

        WallStrips.Draw(c, in f, g);

        if (o.ShowInfo)
        {
            var bezel = o.BezelPx > 0 ? $" · bezel {o.BezelPx}px" : "";
            var raster = CanvasResolver.VideoWall(o);
            DrawUtil.Chip(c, $"{o.Columns} × {o.Rows} displays · {ew}×{eh}{bezel} · {raster.Width}×{raster.Height}{WallStrips.Words(g)}",
                f.Canvas, Anchor9.BottomCenter, GridPattern.ChipText(f), pc, f.Palette.Text, f.Palette.ChipBg);
        }
    }

    /// <summary>An element's rect on the canvas: its raster place, moved past the strips before it when the wall has any. Shared with tests.</summary>
    public static SKRectI ElementRect(int col, int row, int ew, int eh, GapMap g)
    {
        var rect = new SKRectI(col * ew, row * eh, (col + 1) * ew, (row + 1) * eh);
        return g.IsEmpty ? rect : g.VirtualRect(rect);
    }
}

/// <summary>The dead strips of a wall as a test pattern draws them: black, hatched, named — nothing the room will show, and the desk sees why.</summary>
internal static class WallStrips
{
    private static readonly SKColor StripFill = new(0x03, 0x04, 0x06);
    private static readonly SKColor StripHatch = new(0xFF, 0xB0, 0x20, 0x50);
    private static readonly SKColor StripText = new(0xFF, 0xB0, 0x20, 0xC0);

    public static void Draw(SKCanvas c, in PatternFrame f, GapMap g)
    {
        if (g.IsEmpty) return;
        var pc = f.Paints;
        var fill = pc.Fill(StripFill);
        var hatch = pc.StrokeAA(StripHatch, 1);
        var font = pc.FontBold;
        foreach (var strip in g.StripsIn(SKRectI.Create(0, 0, f.W, f.H)))
        {
            c.DrawRect(strip, fill);
            DrawUtil.Hatch(c, strip, 12, hatch);
            var vertical = strip.Width < strip.Height;
            var size = vertical ? strip.Width : strip.Height;
            var words = $"GAP {size} px";
            font.Size = Math.Clamp(Math.Min(strip.Width, strip.Height) * 0.5f, 8, 28);
            if (vertical && strip.Width >= 40 && strip.Width < 110 || !vertical && strip.Height >= 40 && strip.Height < 110)
            {
                // A narrow strip reads its words along its length.
                c.Save();
                c.Translate(strip.MidX, strip.MidY);
                if (vertical) c.RotateDegrees(-90);
                DrawUtil.TextCentered(c, words, 0, font.Size * 0.35f, font, pc.Text(StripText));
                c.Restore();
            }
            else if (strip.Width >= 110 && strip.Height >= 20 || strip.Height >= 110 && strip.Width >= 20)
            {
                DrawUtil.TextCentered(c, words, strip.MidX, strip.MidY + font.Size * 0.35f, font, pc.Text(StripText));
            }
        }
    }

    /// <summary>The chip's words for a wall with strips.</summary>
    public static string Words(GapMap g)
        => g.IsEmpty ? "" : $" · {g.Count} gap{(g.Count == 1 ? "" : "s")} · {g.Virtual.Width}×{g.Virtual.Height} laid out";
}
