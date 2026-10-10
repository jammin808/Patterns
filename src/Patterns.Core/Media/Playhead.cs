namespace Patterns.Core.Media;

/// <summary>
/// What the show core asks of a playing source — where it is, how long it is, whether it plays,
/// has ended or can be seeked — without the canvas it draws on. The video clock, the cue timing
/// and the words on the desk read this; the render side's frame sources implement it beside
/// their drawing, so a clock rule never links a drawing library.
/// </summary>
public interface IPlayheadSource
{
    bool IsPlaying { get; }

    bool IsEnded { get; }

    double DurationSeconds { get; }

    double PositionSeconds => 0;

    bool CanSeek => false;

    /// <summary>The speed the decoder plays at (1 = real time). What is left in wall time is the remaining seconds divided by it.</summary>
    double Rate => 1;

    /// <summary>Paused by an operator (VIDEO PAUSE / STOP) — never the standby's pre-roll hold, which is the cue stack's.</summary>
    bool IsPaused => false;

    /// <summary>
    /// Pauses or resumes the source for an operator; false when this source cannot (a camera, a
    /// page, a clip held for the standby). Resuming an ended clip is not this — RESTART plays it again.
    /// </summary>
    bool SetPaused(bool paused) => false;
}
