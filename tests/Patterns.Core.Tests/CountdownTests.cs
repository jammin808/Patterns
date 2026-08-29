using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

public class CountdownTests
{
    private static CountdownConfig TimeOfDay(string target) => new()
    {
        Enabled = true,
        TargetKind = CountdownTargetKind.TimeOfDay,
        TargetTime = target,
    };

    [Theory]
    [InlineData("19:30", true)]
    [InlineData("7:05", true)]
    [InlineData("00:00", true)]
    [InlineData("23:59", true)]
    [InlineData("19:30:45", true)]
    [InlineData("24:00", false)]
    [InlineData("späti", false)]
    [InlineData("", false)]
    public void ParsesTimes(string text, bool ok)
        => Assert.Equal(ok, CountdownService.TryParseTime(text, out _));

    [Fact]
    public void CountsDownToLaterToday()
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0);
        var s = CountdownService.Evaluate(TimeOfDay("19:30"), now, now.ToUniversalTime());
        Assert.Equal(CountdownPhase.Running, s.Phase);
        Assert.Equal(TimeSpan.FromHours(7.5), s.Remaining);
    }

    [Fact]
    public void RecentlyPassedReadsAsOver()
    {
        var now = new DateTime(2026, 8, 29, 13, 45, 0);
        var s = CountdownService.Evaluate(TimeOfDay("13:00"), now, now.ToUniversalTime());
        Assert.Equal(CountdownPhase.Over, s.Phase);
    }

    [Fact]
    public void CrossesMidnightForward()
    {
        // 23:00 targeting 00:30 → 1.5 h, not "over".
        var now = new DateTime(2026, 8, 29, 23, 0, 0);
        var s = CountdownService.Evaluate(TimeOfDay("00:30"), now, now.ToUniversalTime());
        Assert.Equal(CountdownPhase.Running, s.Phase);
        Assert.Equal(TimeSpan.FromMinutes(90), s.Remaining);
    }

    [Fact]
    public void DurationCountsFromArm()
    {
        var armed = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var cfg = new CountdownConfig
        {
            Enabled = true,
            TargetKind = CountdownTargetKind.Duration,
            DurationMinutes = 15,
            ArmedAtUtc = armed,
        };
        var s = CountdownService.Evaluate(cfg, DateTime.Now, armed.AddMinutes(5));
        Assert.Equal(CountdownPhase.Running, s.Phase);
        Assert.Equal(TimeSpan.FromMinutes(10), s.Remaining);
        Assert.Equal(1.0 / 3.0, s.Progress01, 3);

        var over = CountdownService.Evaluate(cfg, DateTime.Now, armed.AddMinutes(16));
        Assert.Equal(CountdownPhase.Over, over.Phase);
    }

    [Fact]
    public void DisarmedDurationIsIdle()
    {
        var cfg = new CountdownConfig { Enabled = true, TargetKind = CountdownTargetKind.Duration, ArmedAtUtc = null };
        Assert.Equal(CountdownPhase.Idle, CountdownService.Evaluate(cfg, DateTime.Now, DateTime.UtcNow).Phase);
    }

    [Theory]
    [InlineData(0, 0, 30, "00:30")]
    [InlineData(0, 12, 5, "12:05")]
    [InlineData(2, 3, 4, "2:03:04")]
    [InlineData(26, 0, 0, "26:00:00")]
    public void FormatsRemaining(int h, int m, int s, string expected)
        => Assert.Equal(expected, CountdownService.Format(new TimeSpan(h, m, s)));
}
