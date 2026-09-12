using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Media;

/// <summary>
/// Where a live source's replaced frames go before they are freed. A frame a renderer fetched a
/// moment ago may still be inside a draw — the canvas flushes at the end of the frame — so a
/// superseded image is held a little and then disposed, never freed under a draw. One pool for
/// every source: a decoded clip, an NDI feed, a web page, a deck.
///
/// Held for <see cref="MemoryBudget.HeldFrameMs"/> — the number the Machine page prints — and
/// never more than <see cref="MaxHeld"/> frames at once, whatever the rate. Three copies of this
/// list used to disagree: the clip decoder held 400 ms, the NDI receiver and the frame slot two
/// seconds, and two seconds of 1080p60 is a hundred and twenty frames, a gigabyte per source,
/// with nothing but time to bound it. A draw is over within a frame or two; the cap is generous.
/// </summary>
public static class RetiredFrames
{
    /// <summary>The most frames the pool holds, whatever their age: a 4K frame is thirty-odd megabytes.</summary>
    public const int MaxHeld = 12;

    public static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(MemoryBudget.HeldFrameMs);

    private static readonly object Gate = new();
    private static readonly List<(SKImage Image, DateTime RetiredUtc)> Held = new();

    /// <summary>Frames held right now, across every source: the memory ceilings' number.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Held.Count;
            }
        }
    }

    /// <summary>Takes a replaced frame (null is nothing) and frees whatever is past its hold or beyond the cap.</summary>
    public static void Retire(SKImage? image)
    {
        lock (Gate)
        {
            if (image is not null) Held.Add((image, DateTime.UtcNow));
            SweepLocked(DateTime.UtcNow);
        }
    }

    /// <summary>Frees what is past its hold — for a source that stopped publishing, so its last frames do not wait for the next.</summary>
    public static void Sweep()
    {
        lock (Gate)
        {
            SweepLocked(DateTime.UtcNow);
        }
    }

    private static void SweepLocked(DateTime nowUtc)
    {
        var cutoff = nowUtc - Hold;
        // Oldest first: past the hold, or past the cap counting from the newest.
        for (var i = 0; i < Held.Count;)
        {
            var overCap = Held.Count - i > MaxHeld;
            if (overCap || Held[i].RetiredUtc < cutoff)
            {
                Held[i].Image.Dispose();
                Held.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }
}
