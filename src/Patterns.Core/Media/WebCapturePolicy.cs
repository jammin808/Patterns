using Patterns.Core.Services;

namespace Patterns.Core.Media;

/// <summary>What the browser is asked to hand over, and how the frames are smoothed, for one page on this machine.</summary>
public readonly record struct WebCapturePlan(int MaxWidth, int MaxHeight, int JpegQuality, int EveryNthFrame, FrameSmoother.Bounds Smoothing)
{
    /// <summary>"captured at 1280×720 · q60 · every 2nd frame".</summary>
    public string Words
    {
        get
        {
            var nth = EveryNthFrame > 1 ? $" · every {EveryNthFrame}{(EveryNthFrame == 2 ? "nd" : EveryNthFrame == 3 ? "rd" : "th")} frame" : "";
            return $"captured at {MaxWidth}×{MaxHeight} · q{JpegQuality}{nth}";
        }
    }
}

/// <summary>
/// The capture policy for a web page (round 68): the screencast is asked for the size the page is
/// drawn at, never larger — the picture was scaled to that size anyway, and every pixel asked for
/// is encoded in the browser, carried as text, decoded here and uploaded per sink — at a JPEG
/// quality by the machine class, skipping frames only when the quality ladder has stepped the
/// effects down and the page runs faster than the show needs. The page lays itself out at its own
/// viewport as before: the site still sees 1920×1080 and picks its stream for it. Pure.
/// </summary>
public static class WebCapturePolicy
{
    /// <summary>On a small machine with the ladder below full, the capture never exceeds 720p.</summary>
    public const int SmallMachineCapWidth = 1280;
    public const int SmallMachineCapHeight = 720;

    /// <summary>The quality ladder's level from which a fast page is captured every second frame (Economy).</summary>
    public const int SkipFramesFromLevel = 2;

    /// <summary>A page delivering at this rate or more is a fast page (a 60 fps page; a 30 fps video never is, whatever its wobble).</summary>
    public const double FastPageFps = 45;

    public static int QualityFor(MachineClass machine) => machine switch
    {
        MachineClass.Small => 60,
        MachineClass.Big => 80,
        _ => ScreencastFrame.Quality,
    };

    /// <summary>
    /// The plan for a page with this viewport, drawn at most <paramref name="drawnWidth"/> ×
    /// <paramref name="drawnHeight"/> (0 = unknown: the viewport), delivering <paramref name="pageFps"/>
    /// (0 = unknown), on a machine of this class with the ladder at this level (0 = full).
    /// </summary>
    public static WebCapturePlan Plan(int viewportWidth, int viewportHeight, int drawnWidth, int drawnHeight, MachineClass machine, int ladderLevel, double pageFps)
    {
        var w = Math.Max(1, viewportWidth);
        var h = Math.Max(1, viewportHeight);
        if (drawnWidth > 0 && drawnHeight > 0)
        {
            // Fit the viewport's aspect inside the drawn size: the browser scales the frame, so the aspect is the page's.
            var scale = Math.Min(1.0, Math.Min(drawnWidth / (double)w, drawnHeight / (double)h));
            w = Math.Max(1, (int)Math.Round(w * scale));
            h = Math.Max(1, (int)Math.Round(h * scale));
        }
        if (machine == MachineClass.Small && ladderLevel > 0 && (w > SmallMachineCapWidth || h > SmallMachineCapHeight))
        {
            var scale = Math.Min(SmallMachineCapWidth / (double)w, SmallMachineCapHeight / (double)h);
            w = Math.Max(1, (int)Math.Round(w * scale));
            h = Math.Max(1, (int)Math.Round(h * scale));
        }
        var nth = ladderLevel >= SkipFramesFromLevel && pageFps >= FastPageFps ? 2 : 1;
        return new WebCapturePlan(w, h, QualityFor(machine), nth, FrameSmoother.Bounds.For(machine));
    }
}
