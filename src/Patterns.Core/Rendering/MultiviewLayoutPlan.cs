using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>One tile's box on the wall, and whether it is one of the large ones.</summary>
public readonly record struct MultiviewCell(int Index, SKRect Box, bool Large);

/// <summary>
/// Where a monitor wall's tiles go.
///
/// A multiview is read at a glance from across a room, usually by someone who is doing something
/// else at the time. An equal grid says every picture matters the same amount, which is never
/// true: what is on air and what is going on air next decide things, and a confidence feed or an
/// input is a glance. So the layouts here are one or two large tiles with the rest small beneath
/// or beside them, and the large ones are simply the first in the list — an operator arranges the
/// wall by dragging tiles into the order they want, not by learning a rule.
///
/// It is pure geometry: no Skia state, no snapshot, nothing drawn. That makes the arrangement
/// something a test can read in numbers rather than in pixels, and it keeps the renderer to one
/// job — drawing a tile in a box it was handed.
/// </summary>
public static class MultiviewLayoutPlan
{
    /// <summary>How much of the height the large row takes when there is a strip under it.</summary>
    public const float LargeShare = 0.66f;

    /// <summary>How much of the width the large tile takes in the side-column layout.</summary>
    public const float SideShare = 0.72f;

    /// <summary>The most tiles a strip puts in one row before it wraps.</summary>
    public const int MaxStripColumns = 8;

    /// <summary>How many of the tiles this layout draws large, given how many there are.</summary>
    public static int LargeCount(MultiviewLayout layout, int count) => layout switch
    {
        MultiviewLayout.Grid => 0,
        MultiviewLayout.ProgramAndPreview => Math.Min(2, count),
        _ => Math.Min(1, count),
    };

    /// <summary>
    /// The boxes, in tile order. <paramref name="columns"/> is the operator's setting (0 =
    /// automatic): it sets the whole wall on Grid and the small strip everywhere else.
    /// </summary>
    public static List<MultiviewCell> Plan(MultiviewLayout layout, int count, SKRect area, int columns, float gap)
    {
        var cells = new List<MultiviewCell>(Math.Max(0, count));
        if (count <= 0 || area.Width <= 0 || area.Height <= 0) return cells;

        var large = LargeCount(layout, count);
        // Every tile is large: there is no strip, so it is one row of them however it was asked for.
        if (large >= count || layout == MultiviewLayout.Grid)
        {
            var cols = layout == MultiviewLayout.Grid
                ? (columns > 0 ? columns : (int)Math.Ceiling(Math.Sqrt(count)))
                : count;
            AddGrid(cells, 0, count, area, cols, gap, large: layout != MultiviewLayout.Grid);
            return cells;
        }

        if (layout == MultiviewLayout.SideColumn)
        {
            var w = (area.Width - gap) * SideShare;
            AddGrid(cells, 0, 1, SKRect.Create(area.Left, area.Top, w, area.Height), 1, gap, large: true);
            var rest = SKRect.Create(area.Left + w + gap, area.Top, area.Width - w - gap, area.Height);
            AddGrid(cells, 1, count - 1, rest, 1, gap, large: false);
            return cells;
        }

        // Program-and-preview and solo: the large ones across the top, the rest in a strip under.
        var topH = (area.Height - gap) * LargeShare;
        AddGrid(cells, 0, large, SKRect.Create(area.Left, area.Top, area.Width, topH), large, gap, large: true);
        var strip = SKRect.Create(area.Left, area.Top + topH + gap, area.Width, area.Height - topH - gap);
        AddGrid(cells, large, count - large, strip, StripColumns(count - large, columns), gap, large: false);
        return cells;
    }

    /// <summary>How many small tiles go across: what was asked for, else one row of them up to the cap.</summary>
    public static int StripColumns(int count, int columns)
    {
        if (columns > 0) return columns;
        return Math.Max(1, Math.Min(MaxStripColumns, count));
    }

    private static void AddGrid(List<MultiviewCell> into, int first, int count, SKRect area, int cols, float gap, bool large)
    {
        if (count <= 0 || area.Width <= 0 || area.Height <= 0) return;
        cols = Math.Max(1, Math.Min(cols, count));
        var rows = (int)Math.Ceiling(count / (double)cols);
        var w = (area.Width - gap * (cols - 1)) / cols;
        var h = (area.Height - gap * (rows - 1)) / rows;
        if (w <= 0 || h <= 0) return;
        for (var i = 0; i < count; i++)
        {
            var col = i % cols;
            var row = i / cols;
            into.Add(new MultiviewCell(
                first + i,
                SKRect.Create(area.Left + col * (w + gap), area.Top + row * (h + gap), w, h),
                large));
        }
    }
}
