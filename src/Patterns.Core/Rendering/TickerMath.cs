namespace Patterns.Core.Rendering;

/// <summary>
/// The message ticker's travel line: how far the text has scrolled, in pixels, as a straight
/// line of the show clock. It lives on the snapshot, not on a sink, so every sink — both halves
/// of a span, an NDI sender, a monitor tile, an output opened late — reads the same distance at
/// the same clock and draws the same train of copies. A speed change re-anchors the line at the
/// publish clock, so the join is continuous: the copy crossing the screen keeps its place and
/// simply moves at the new rate.
/// </summary>
public readonly record struct TickerLine(double AnchorClock, double AnchorPx, double Speed)
{
    /// <summary>A line through the origin at the given speed — the show started with this rate.</summary>
    public static TickerLine From(double speed) => new(0, 0, speed);

    /// <summary>Pixels travelled at <paramref name="clock"/>. Never wrapped; the renderer wraps.</summary>
    public double DistanceAt(double clock) => AnchorPx + (clock - AnchorClock) * Speed;

    /// <summary>
    /// The same line with a new slope from <paramref name="clock"/> on. The distance is continuous
    /// at the join, so no sink sees a jump. The line is returned unchanged when the speed is unchanged.
    /// </summary>
    public TickerLine WithSpeed(double speed, double clock)
        => Speed == speed ? this : new TickerLine(clock, DistanceAt(clock), speed);
}

/// <summary>Pure ticker layout: where the copies of the text sit for a given travel distance.</summary>
public static class TickerMath
{
    /// <summary>Gap between two copies of the text, as a fraction of the canvas width.</summary>
    public const double GapFraction = 0.25;

    /// <summary>Distance from one copy's left edge to the next: the text plus a quarter-canvas gap.</summary>
    public static double Period(double textWidth, double canvasWidth) => textWidth + canvasWidth * GapFraction;

    /// <summary>Wraps a distance into [0, period) — a true modulo, safe for negative inputs.</summary>
    public static double Wrap(double distance, double period)
    {
        if (period <= 0) return 0;
        var r = distance - Math.Floor(distance / period) * period;
        return r >= period ? 0 : r;
    }

    /// <summary>
    /// Left edge of the lead copy — the one furthest right that is still on or at the canvas edge —
    /// for text that entered from the right and has travelled <paramref name="distance"/> px leftward.
    /// Always in (canvasWidth − period, canvasWidth]. The train is periodic, so the phase is taken
    /// modulo the copy period: wrapping shifts the train by exactly one copy, which is invisible.
    /// (The old code wrapped modulo canvas + period, and every wrap snapped the train by the
    /// remainder — the "jump every few seconds" a room full of people can see.)
    /// </summary>
    public static float LeadX(double distance, double period, double canvasWidth)
        => (float)(canvasWidth - Wrap(distance, period));

    /// <summary>
    /// The x positions of every copy that touches the canvas: the lead copy and each earlier copy
    /// behind it, stepping left by one period until a copy is completely off the left edge.
    /// </summary>
    public static IEnumerable<float> CopyPositions(double distance, double period, double textWidth, double canvasWidth)
    {
        if (period <= 0 || textWidth <= 0) yield break;
        for (var x = LeadX(distance, period, canvasWidth); x + textWidth > 0; x -= (float)period)
        {
            yield return x;
        }
    }
}
