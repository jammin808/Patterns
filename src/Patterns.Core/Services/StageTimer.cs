using Patterns.Core.Model;

namespace Patterns.Core.Services;

public enum StageTimerPhase
{
    /// <summary>No countdown armed.</summary>
    Idle,
    Running,
    Paused,
    /// <summary>Past zero: red, and counting up how far over.</summary>
    Over,
}

/// <summary>What the stage page draws: the phase, the seconds left (negative past zero), the colour word, the progress.</summary>
public readonly record struct StageTime(StageTimerPhase Phase, double RemainingSeconds, string Colour, double Progress01)
{
    public static readonly StageTime Idle = new(StageTimerPhase.Idle, 0, "off", 0);

    /// <summary>"12:34", "-0:42" past zero, "--:--" idle.</summary>
    public string Text => Phase == StageTimerPhase.Idle ? "--:--" : StageTimer.Format(RemainingSeconds);
}

/// <summary>
/// The stage timer's arithmetic on the countdown's own clock, pure so the wall, the stage page, the
/// caller and the tests read the same second: the thresholds that colour it, a pause that keeps
/// what is left, a nudge of seconds either way.
/// </summary>
public static class StageTimer
{
    public static StageTime Evaluate(CountdownConfig countdown, StageConfig stage, DateTime localNow, DateTime utcNow)
    {
        if (stage.Paused) return new StageTime(StageTimerPhase.Paused, stage.PausedRemainingSeconds, Colour(stage.PausedRemainingSeconds, stage), 0);
        if (!countdown.Enabled) return StageTime.Idle;
        var status = CountdownService.Evaluate(countdown, localNow, utcNow);
        switch (status.Phase)
        {
            case CountdownPhase.Idle:
                return StageTime.Idle;
            case CountdownPhase.Over:
            {
                // Past zero: how far over, from the moment it ended — a duration knows; a time of day does.
                var over = OverBy(countdown, localNow, utcNow);
                return new StageTime(StageTimerPhase.Over, -over, "red", 1);
            }
            default:
                return new StageTime(StageTimerPhase.Running, status.Remaining.TotalSeconds, Colour(status.Remaining.TotalSeconds, stage), status.Progress01);
        }
    }

    private static double OverBy(CountdownConfig countdown, DateTime localNow, DateTime utcNow)
    {
        if (countdown.TargetKind == CountdownTargetKind.Duration && countdown.ArmedAtUtc is { } armed)
        {
            return Math.Max(0, (utcNow - armed).TotalSeconds - countdown.DurationMinutes * 60);
        }
        if (CountdownService.TryParseTime(countdown.TargetTime, out var target))
        {
            var over = localNow.TimeOfDay - target;
            if (over < TimeSpan.Zero) over += TimeSpan.FromDays(1);
            return over.TotalSeconds;
        }
        return 0;
    }

    /// <summary>"green" with time in hand, "amber" inside the first threshold, "red" inside the second or past zero.</summary>
    public static string Colour(double remainingSeconds, StageConfig stage)
        => remainingSeconds <= stage.RedSeconds ? "red" : remainingSeconds <= stage.AmberSeconds ? "amber" : "green";

    /// <summary>"12:34"; "1:02:03" over an hour; "-0:42" past zero.</summary>
    public static string Format(double seconds)
    {
        var sign = seconds < 0 ? "-" : "";
        var t = TimeSpan.FromSeconds(Math.Abs(Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{sign}{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{sign}{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>PAUSE: what is left is kept and the clock stops; false when nothing is running.</summary>
    public static bool Pause(CountdownConfig countdown, StageConfig stage, DateTime localNow, DateTime utcNow)
    {
        var now = Evaluate(countdown, stage, localNow, utcNow);
        if (now.Phase != StageTimerPhase.Running) return false;
        stage.PausedRemainingSeconds = now.RemainingSeconds;
        stage.Paused = true;
        countdown.Enabled = false;
        return true;
    }

    /// <summary>RESUME: what was left runs again from now, as a duration; false when nothing was paused.</summary>
    public static bool Resume(CountdownConfig countdown, StageConfig stage, DateTime utcNow)
    {
        if (!stage.Paused) return false;
        countdown.TargetKind = CountdownTargetKind.Duration;
        countdown.DurationMinutes = Math.Max(0.1 / 60, stage.PausedRemainingSeconds / 60);
        countdown.ArmedAtUtc = utcNow;
        countdown.Enabled = true;
        stage.Paused = false;
        stage.PausedRemainingSeconds = 0;
        return true;
    }

    /// <summary>+60 / −30: seconds onto what is left — a paused timer's kept remainder, a duration's length, a time of day moved.</summary>
    public static bool Add(CountdownConfig countdown, StageConfig stage, double seconds, DateTime localNow, DateTime utcNow)
    {
        if (stage.Paused)
        {
            stage.PausedRemainingSeconds = Math.Max(0, stage.PausedRemainingSeconds + seconds);
            return true;
        }
        if (!countdown.Enabled) return false;
        if (countdown.TargetKind == CountdownTargetKind.Duration)
        {
            countdown.DurationMinutes = Math.Max(0.1 / 60, countdown.DurationMinutes + seconds / 60);
            return true;
        }
        if (!CountdownService.TryParseTime(countdown.TargetTime, out var target)) return false;
        var moved = target + TimeSpan.FromSeconds(seconds);
        if (moved < TimeSpan.Zero) moved += TimeSpan.FromDays(1);
        if (moved >= TimeSpan.FromDays(1)) moved -= TimeSpan.FromDays(1);
        countdown.TargetTime = $"{moved.Hours:00}:{moved.Minutes:00}";
        return true;
    }

    /// <summary>
    /// The longest span a wire word may name, either way: a week. Longer than any show day and far inside what a
    /// <see cref="TimeSpan"/>, an int of seconds and the show file's numbers can hold — a slip, a nudge or a timer's
    /// seconds past it is not a time but a probe, and is refused rather than carried into the show.
    /// </summary>
    public const double MaxWireSeconds = 7 * 24 * 3600;

    /// <summary>"+60", "-30", "90": seconds, either sign, finite and no longer than <see cref="MaxWireSeconds"/>; null for anything else.</summary>
    public static double? ParseSeconds(string text)
    {
        var t = (text ?? "").Trim().Replace(" ", "");
        if (t.Length == 0) return null;
        var sign = 1.0;
        if (t[0] == '+') t = t[1..];
        else if (t[0] == '-' || t[0] == '\u2212') { sign = -1; t = t[1..]; }
        if (t.EndsWith("s", StringComparison.OrdinalIgnoreCase)) t = t[..^1];
        var minutes = false;
        if (t.EndsWith("m", StringComparison.OrdinalIgnoreCase) || t.EndsWith("min", StringComparison.OrdinalIgnoreCase))
        {
            minutes = true;
            t = t.EndsWith("min", StringComparison.OrdinalIgnoreCase) ? t[..^3] : t[..^1];
        }
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || double.IsNaN(n) || n <= 0) return null;
        var seconds = minutes ? n * 60 : n;
        return double.IsFinite(seconds) && seconds <= MaxWireSeconds ? sign * seconds : null;
    }
}
