using System.Diagnostics;

namespace Patterns.Core.Services;

/// <summary>
/// The master clock: one monotonic clock shared by every sink so animations stay phase-locked
/// across preview, all output windows, NDI and the stream — and, through <see cref="UtcNow"/>,
/// by every ramp, fade and duck, so a wall-clock step (NTP, a time-zone change, a manual set)
/// can never stall a fade or jump a sync. The audio outputs lock to it through the sample-rate
/// converters; the sync check flashes and clicks on its grid.
/// </summary>
public static class ShowClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    // The wall time the clock started at, captured once: from here on wall time is derived from
    // the monotonic clock rather than read, so it can only ever move forward at one rate.
    private static readonly DateTime BaseUtc = DateTime.UtcNow - Watch.Elapsed;

    public static double Seconds => Watch.Elapsed.TotalSeconds;

    /// <summary>Wall time as the master clock sees it: the start instant plus the monotonic elapsed. Never steps, never runs backwards.</summary>
    public static DateTime UtcNow => BaseUtc + Watch.Elapsed;

    /// <summary>The master instant a wall time corresponds to (seconds on this clock).</summary>
    public static double SecondsAt(DateTime utc) => (utc - BaseUtc).TotalSeconds;

    /// <summary>The wall time a master instant corresponds to.</summary>
    public static DateTime UtcAt(double seconds) => BaseUtc + TimeSpan.FromSeconds(seconds);

    /// <summary>The master clock in ticks (100 ns), monotonic: what <see cref="ShowClockTime"/> stamps with.</summary>
    public static long Ticks => Watch.Elapsed.Ticks;
}

/// <summary>
/// The master clock as a <see cref="TimeProvider"/>: its wall time is <see cref="ShowClock.UtcNow"/>, which never steps, and its
/// timestamps are the master clock's ticks, so a reader that takes its time through the provider reads the clock every sink reads.
/// A link takes one as its seam: the desk runs on this one, and a test hands it another.
/// </summary>
public sealed class ShowClockTime : TimeProvider
{
    public static readonly ShowClockTime Instance = new();

    private ShowClockTime()
    {
    }

    public override DateTimeOffset GetUtcNow() => new(ShowClock.UtcNow, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => ShowClock.Ticks;
}
