using Patterns.Core.Services;
using System.Diagnostics;
using Patterns.Core.Model;

namespace Patterns.Rendering;

/// <summary>
/// One sink's stage timings for the frame being drawn: the engine notes each stage as it ends,
/// and the slowest one goes into the frame budget with the frame's time. Owned by the sink,
/// touched on its render thread only, nothing allocated per frame.
/// </summary>
public sealed class FrameStages
{
    /// <summary>The slowest stage of the frame so far; "" until one is noted.</summary>
    public string SlowestStage { get; private set; } = "";

    /// <summary>How long it took, ms; -1 until one is noted.</summary>
    public double SlowestMs { get; private set; } = -1;

    /// <summary>How many stages the frame noted (tests read it).</summary>
    public int Noted { get; private set; }

    /// <summary>The show clock of the oldest live picture this frame drew — a camera's, a feed's — or -1 when it drew none. The frame's live input age is read from it at the end.</summary>
    public double LiveFrameClock { get; private set; } = -1;

    /// <summary>The generation of that picture in its pool (0 when its source does not count): the diagnostics name the frame.</summary>
    public long LiveFrameGeneration { get; private set; }

    /// <summary>The start of a top-level frame: nested draws (a layer's screen, a fade source) keep noting into it.</summary>
    public void Begin()
    {
        SlowestStage = "";
        SlowestMs = -1;
        Noted = 0;
        LiveFrameClock = -1;
        LiveFrameGeneration = 0;
    }

    /// <summary>A live picture was drawn: the oldest one drawn this frame is the one that counts.</summary>
    public void NoteLive(double frameClock, long generation = 0)
    {
        if (frameClock < 0) return;
        if (LiveFrameClock < 0 || frameClock < LiveFrameClock)
        {
            LiveFrameClock = frameClock;
            LiveFrameGeneration = generation;
        }
    }

    /// <summary>The same for what a draw just drew: only a live, timed frame counts — the drawn frame's own clock, never the source's newest.</summary>
    public void NoteLive(in Media.DrawnFrame drawn)
    {
        if (drawn.Drew && drawn.IsLive) NoteLive(drawn.FrameClock, drawn.Generation);
    }

    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>A stage ended: how long since its start.</summary>
    public void Note(string stage, long startedAt) => Note(stage, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

    public void Note(string stage, double ms)
    {
        Noted++;
        if (ms > SlowestMs)
        {
            SlowestMs = ms;
            SlowestStage = stage;
        }
    }
}
