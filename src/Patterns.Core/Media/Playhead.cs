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
}
