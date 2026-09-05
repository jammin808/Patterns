namespace Patterns.Core.Effects;

/// <summary>
/// The sync check: while on, every sink flashes white for a frame or two on the master clock's
/// two-second grid, and the tone output clicks at the same instants. Film a screen with a
/// phone, or watch the stream and the room together, and the gap between the flash and the
/// click is the delay to dial in. One static channel, like the effect pulses: every sink reads
/// it from its own frame's show time.
/// </summary>
public static class SyncMarks
{
    public const double PeriodSeconds = 2.0;
    public const double FlashSeconds = 0.05;

    private static volatile bool _enabled;

    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>True while a flash is up at this show time.</summary>
    public static bool IsFlash(double time) => _enabled && time >= 0 && time % PeriodSeconds < FlashSeconds;

    /// <summary>The next mark strictly after this show time.</summary>
    public static double NextMark(double time) => (Math.Floor(Math.Max(0, time) / PeriodSeconds) + 1) * PeriodSeconds;

    /// <summary>The mark this show time belongs to (the one at or before it).</summary>
    public static double MarkBefore(double time) => Math.Floor(Math.Max(0, time) / PeriodSeconds) * PeriodSeconds;
}
