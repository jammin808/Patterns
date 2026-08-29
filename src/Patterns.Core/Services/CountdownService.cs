using Patterns.Core.Model;

namespace Patterns.Core.Services;

public enum CountdownPhase
{
    /// <summary>Counting down; <see cref="CountdownStatus.Remaining"/> is positive.</summary>
    Running,
    /// <summary>Reached zero.</summary>
    Over,
    /// <summary>Not armed / target invalid.</summary>
    Idle,
}

public readonly record struct CountdownStatus(CountdownPhase Phase, TimeSpan Remaining, double Progress01)
{
    public static readonly CountdownStatus Idle = new(CountdownPhase.Idle, TimeSpan.Zero, 0);
}

/// <summary>Pure countdown arithmetic — fully unit tested, no clock dependency.</summary>
public static class CountdownService
{
    /// <summary>
    /// Time-of-day rule: the target is today's HH:mm. A target that passed less than
    /// 12 h ago reads as “over” (the operator meant today); one further in the past
    /// rolls to tomorrow (so “countdown to 00:30” armed at 23:00 counts 1.5 h).
    /// </summary>
    public static CountdownStatus Evaluate(CountdownConfig cfg, DateTime localNow, DateTime utcNow)
    {
        if (!cfg.Enabled) return CountdownStatus.Idle;

        if (cfg.TargetKind == CountdownTargetKind.Duration)
        {
            if (cfg.ArmedAtUtc is not { } armed) return CountdownStatus.Idle;
            var total = TimeSpan.FromMinutes(cfg.DurationMinutes);
            var elapsed = utcNow - armed;
            var remaining = total - elapsed;
            if (remaining <= TimeSpan.Zero) return new CountdownStatus(CountdownPhase.Over, TimeSpan.Zero, 1);
            return new CountdownStatus(CountdownPhase.Running, remaining, Clamp01(elapsed / total));
        }

        if (!TryParseTime(cfg.TargetTime, out var tod)) return CountdownStatus.Idle;

        var target = localNow.Date + tod;
        var delta = target - localNow;
        if (delta < TimeSpan.FromHours(-12))
        {
            target = target.AddDays(1);
            delta = target - localNow;
        }

        if (delta <= TimeSpan.Zero) return new CountdownStatus(CountdownPhase.Over, TimeSpan.Zero, 1);

        // Progress for time-of-day counts within the final hour (or the full span if shorter).
        var span = TimeSpan.FromTicks(Math.Max(delta.Ticks, TimeSpan.FromHours(1).Ticks));
        return new CountdownStatus(CountdownPhase.Running, delta, Clamp01(1 - delta / span));
    }

    public static bool TryParseTime(string? text, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        string[] formats = { @"h\:m", @"h\:m\:s", @"hhmm" };
        foreach (var f in formats)
        {
            if (TimeSpan.TryParseExact(s, f, null, out var ts) && ts < TimeSpan.FromDays(1))
            {
                timeOfDay = ts;
                return true;
            }
        }
        return false;
    }

    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
