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
