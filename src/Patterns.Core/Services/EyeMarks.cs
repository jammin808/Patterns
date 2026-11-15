namespace Patterns.Core.Services;

/// <summary>
/// The shape a light is drawn with beside its colour — the Eye's dot, its links' midpoints, the side card's rows and the Nodes
/// cards — so no light is colour alone (Show Bus 1.3 §13.16.1's ✓ △ ✕ ○, the desk's ■ and i). Six shapes, no two alike.
/// </summary>
public enum EyeMark
{
    /// <summary>✓ green — drawn as a filled disc.</summary>
    Ok,

    /// <summary>△ amber — drawn as a triangle.</summary>
    Warn,

    /// <summary>✕ red — drawn as a cross.</summary>
    Fault,

    /// <summary>■ armed — drawn as a filled square. No light of the Eye derives it: it is the desk's shape for a thing armed, kept in the set so the six stay apart.</summary>
    Armed,

    /// <summary>○ grey, nothing known — drawn as a ring.</summary>
    Off,

    /// <summary>i grey that is information, not a gap — drawn as a dot over a bar.</summary>
    Info,
}

/// <summary>
/// The marks, derived: a thing's or a link's mark is a function of its light (and, for a grey thing that is information, of that
/// flag) — never a field of its own, so a replay that relights a thing relights its mark with it and no mark is ever stale.
/// </summary>
public static class EyeMarks
{
    /// <summary>The mark a light is drawn with; a grey <paramref name="info"/> thing is i, any other light ignores the flag.</summary>
    public static EyeMark Of(CheckLight light, bool info = false) => light switch
    {
        CheckLight.Green => EyeMark.Ok,
        CheckLight.Amber => EyeMark.Warn,
        CheckLight.Red => EyeMark.Fault,
        _ => info ? EyeMark.Info : EyeMark.Off,
    };

    /// <summary>The mark's glyph, as words carry it (the same ✓ △ ✕ ○ a link's record writes, <see cref="LinkRecord.MarkOf"/>).</summary>
    public static string Glyph(EyeMark mark) => mark switch
    {
        EyeMark.Ok => "✓",
        EyeMark.Warn => "△",
        EyeMark.Fault => "✕",
        EyeMark.Armed => "■",
        EyeMark.Info => "i",
        _ => "○",
    };

    /// <summary>A light's glyph — what a row of words shows beside the light's colour.</summary>
    public static string Glyph(CheckLight light, bool info = false) => Glyph(Of(light, info));

    /// <summary>
    /// Words without a leading <paramref name="mark"/> of their own (a link record's words begin with its mark): a row that puts
    /// the mark first already shows it, so it is said once.
    /// </summary>
    public static string Unmarked(string words, EyeMark mark)
    {
        var glyph = Glyph(mark);
        return words.StartsWith(glyph + " ", StringComparison.Ordinal) ? words[(glyph.Length + 1)..] : words;
    }

    /// <summary>
    /// The glyph a claim carries beside its mark — a link one app's context draws to another node's things or says of a third — so a
    /// claim is told from a fact by a shape as well as by its dash, at every scale where marks are drawn (the Eye design's D4).
    /// </summary>
    public const string SaysGlyph = "❝";

    /// <summary>
    /// Where a link comes from, as a row says it beside its verb: "" for this desk's own and a link's record (the verb and words say
    /// it), "&lt;app&gt; says" for a claim, "told by &lt;app&gt;" for a thing's own context row, and "heard only" for a hello.
    /// </summary>
    public static string SourceWords(EyeEdge e) => e.Claim.Length > 0 ? $"{SaysGlyph} {e.Claim} says"
        : IsClaim(e) ? $"{SaysGlyph} another app says (relayed)"
        : e.Origin switch
        {
            EyeOrigin.Told => "told by its app",
            EyeOrigin.Heard => "heard only",
            _ => "",
        };

    /// <summary>
    /// Whether a link is a claim — one app's word about things that are not its own, named (<see cref="EyeEdge.Claim"/>) or relayed
    /// through another — and so drawn dotted with <see cref="SaysGlyph"/>, said with it on the side card and carrying no verb: the one
    /// rule the canvas, the card and the menus read.
    /// </summary>
    public static bool IsClaim(EyeEdge e) => e.Claim.Length > 0 || e.Origin == EyeOrigin.Relayed;
}

/// <summary>
/// The Eye's drawing in numbers, kept apart from the canvas so it is tested without one: where a link's segment sits when two things
/// share several links, and whether a segment meets the view at all.
/// </summary>
public static class EyeGeometry
{
    /// <summary>The space between the segments of links that join the same two things, in world units.</summary>
    public const double ParallelGap = 10;

    /// <summary>
    /// How far the <paramref name="index"/>-th of <paramref name="count"/> links between one pair of things is set off its
    /// centre line, in world units: centred on the line, <see cref="ParallelGap"/> apart; 0 for a link alone.
    /// </summary>
    public static double Offset(int index, int count) => count <= 1 ? 0 : (index - (count - 1) / 2.0) * ParallelGap;

    /// <summary>
    /// A segment moved sideways by <paramref name="offset"/> (positive to the left of its direction, read with y down) — the same
    /// side whichever end it is drawn from, so a pair's segments never cross.
    /// </summary>
    public static (double X1, double Y1, double X2, double Y2) Shift(double x1, double y1, double x2, double y2, double offset)
    {
        if (offset == 0) return (x1, y1, x2, y2);
        // The normal is taken from the pair in a fixed order (the smaller end first), so A→B and B→A share a side of the line.
        var (ax, ay, bx, by) = (x1, y1).CompareTo((x2, y2)) <= 0 ? (x1, y1, x2, y2) : (x2, y2, x1, y1);
        var dx = bx - ax;
        var dy = by - ay;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9) return (x1, y1, x2, y2);
        var nx = -dy / length * offset;
        var ny = dx / length * offset;
        return (x1 + nx, y1 + ny, x2 + nx, y2 + ny);
    }

    /// <summary>
    /// Where a link between two things is drawn before any lane's offset: from the right edge to the left edge when one is wholly to
    /// the other's side, else centre to centre between the facing top and bottom edges. The canvas draws it and the hit-test reads it,
    /// so a click lands on the segment the eye sees.
    /// </summary>
    public static (double X1, double Y1, double X2, double Y2) Ends(EyeRect a, EyeRect b)
    {
        if (a.Right <= b.X) return (a.Right, a.CenterY, b.X, b.CenterY);
        if (b.Right <= a.X) return (a.X, a.CenterY, b.Right, b.CenterY);
        return (a.CenterX, a.Bottom <= b.Y ? a.Bottom : a.Y, b.CenterX, a.Bottom <= b.Y ? b.Y : b.Bottom);
    }

    /// <summary>The <paramref name="index"/>-th of <paramref name="count"/> links between two things, as drawn: <see cref="Ends"/> set off its lane.</summary>
    public static (double X1, double Y1, double X2, double Y2) Segment(EyeRect a, EyeRect b, int index, int count)
    {
        var (x1, y1, x2, y2) = Ends(a, b);
        return Shift(x1, y1, x2, y2, Offset(index, count));
    }

    /// <summary>The distance from a point to a segment (to its nearer end where the point lies past one).</summary>
    public static double Distance(double px, double py, double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var length2 = dx * dx + dy * dy;
        var t = length2 < 1e-12 ? 0 : Math.Clamp(((px - x1) * dx + (py - y1) * dy) / length2, 0, 1);
        var cx = x1 + t * dx - px;
        var cy = y1 + t * dy - py;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    /// <summary>How near, in view pixels, a click must land to a link's segment to name it (the Eye design's 6 px), after the things.</summary>
    public const double EdgeReach = 6;

    /// <summary>
    /// The link a point names, by its index in <see cref="EyeGraph.Edges"/>, or -1: the nearest drawn segment within
    /// <paramref name="reach"/> world units of (<paramref name="wx"/>, <paramref name="wy"/>), among the links whose two ends are on
    /// <paramref name="lens"/> and placed. A thing under the point is the caller's to prefer: the hit-test runs after the things. Of
    /// two segments equally near, the first in the picture's order wins, so the same picture names the same link.
    /// </summary>
    public static int EdgeAt(EyeGraph g, EyePlacement p, EyeLens lens, double wx, double wy, double reach)
    {
        var best = -1;
        var bestD = double.PositiveInfinity;
        for (var i = 0; i < g.Edges.Count; i++)
        {
            var e = g.Edges[i];
            var a = g.Find(e.From);
            var b = g.Find(e.To);
            if (a is null || b is null || !g.Visible(a, lens) || !g.Visible(b, lens)) continue;
            var ra = p.Of(a.Id);
            var rb = p.Of(b.Id);
            if (ra.IsEmpty || rb.IsEmpty) continue;
            var (index, count) = g.Lane(i);
            var (x1, y1, x2, y2) = Segment(ra, rb, index, count);
            var d = Distance(wx, wy, x1, y1, x2, y2);
            if (d <= reach && d < bestD)
            {
                best = i;
                bestD = d;
            }
        }
        return best;
    }

    /// <summary>
    /// Whether a segment meets a rectangle (Liang–Barsky): a link whose whole segment lies outside the view is not drawn. Exact,
    /// so a long link that crosses the view with both ends outside it is drawn.
    /// </summary>
    public static bool Meets(double x1, double y1, double x2, double y2, EyeRect r)
    {
        if (r.IsEmpty) return false;
        var dx = x2 - x1;
        var dy = y2 - y1;
        double t0 = 0, t1 = 1;
        bool Clip(double p, double q)
        {
            if (p == 0) return q >= 0;
            var t = q / p;
            if (p < 0)
            {
                if (t > t1) return false;
                if (t > t0) t0 = t;
            }
            else
            {
                if (t < t0) return false;
                if (t < t1) t1 = t;
            }
            return true;
        }
        return Clip(-dx, x1 - r.X) && Clip(dx, r.Right - x1) && Clip(-dy, y1 - r.Y) && Clip(dy, r.Bottom - y1) && t0 <= t1;
    }
}
