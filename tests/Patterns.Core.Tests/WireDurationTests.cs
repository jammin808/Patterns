using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// A span of time on the wire is a finite number no longer than a week, or it is refused. A slip past a TimeSpan's
/// range threw out of the parser and closed the connection with nothing logged; an hour count past an int's range
/// wrapped and landed as a small slip (+1193047:00:00 moved the plan by 31:44); and "+Infinity" became a stage timer's
/// remainder that no later save or publish of the show could serialise.
/// </summary>
public class WireDurationTests
{
    [Theory]
    [InlineData("PLAN SHIFT +99999999999999999999")]
    [InlineData("PLAN SHIFT 1e308")]
    [InlineData("PLAN SHIFT +Infinity")]
    [InlineData("PLAN SHIFT -Infinity")]
    [InlineData("PLAN SHIFT NaN")]
    [InlineData("PLAN SHIFT +1193047:00:00")]
    [InlineData("PLAN SHIFT +1193046:30:00")]
    [InlineData("PLAN SHIFT +35791394:59")]
    [InlineData("PLAN +1e308")]
    [InlineData("TIMER ADD +Infinity")]
    [InlineData("TIMER ADD Infinity")]
    [InlineData("TIMER ADD 1e308")]
    [InlineData("TIMER ADD -1e308")]
    [InlineData("TIMER MINUS Infinity")]
    [InlineData("TIMER +1e308")]
    public void ASpanNoClockCouldHoldIsRefusedNeverThrownNorWrapped(string line)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.Equal(RemoteCommandKind.Unknown, cmd.Kind);
    }

    [Theory]
    [InlineData("PLAN SHIFT +2:00", "+2:00")]
    [InlineData("PLAN SHIFT -0:30", "-0:30")]
    [InlineData("PLAN SHIFT +1:02:03", "+1:02:03")]
    [InlineData("PLAN SHIFT +90", "+1:30")]
    [InlineData("PLAN SHIFT +168:00:00", "+168:00:00")]
    [InlineData("PLAN -2m", "-2:00")]
    public void AnOrdinarySlipStillLandsExactly(string line, string value)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.Equal(ShowActionKind.PlanShift, cmd.Action.Kind);
        Assert.Equal(value, cmd.Action.Value);
    }

    [Theory]
    [InlineData("TIMER ADD +60", "+60")]
    [InlineData("TIMER ADD 2m", "+120")]
    [InlineData("TIMER MINUS 30", "-30")]
    [InlineData("TIMER ADD +604800", "+604800")]
    public void AnOrdinaryNudgeStillLands(string line, string value)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.Equal(ShowActionKind.TimerAdd, cmd.Action.Kind);
        Assert.Equal(value, cmd.Action.Value);
    }

    [Fact]
    public void TheCeilingIsAWeekEitherWayAndInclusive()
    {
        Assert.Equal(StageTimer.MaxWireSeconds, StageTimer.ParseSeconds("+604800"));
        Assert.Equal(-StageTimer.MaxWireSeconds, StageTimer.ParseSeconds("-604800"));
        Assert.Equal(StageTimer.MaxWireSeconds, StageTimer.ParseSeconds("10080m"));
        Assert.Null(StageTimer.ParseSeconds("+604801"));
        Assert.Null(StageTimer.ParseSeconds("10081m"));
        Assert.Null(StageTimer.ParseSeconds("Infinity"));
        Assert.Null(StageTimer.ParseSeconds("-Infinity"));
        Assert.Null(StageTimer.ParseSeconds("NaN"));
        Assert.Null(StageTimer.ParseSeconds("1e308m"));

        Assert.Equal(TimeSpan.FromDays(7), CueTiming.ParseDelta("+168:00:00"));
        Assert.Equal(TimeSpan.FromDays(-7), CueTiming.ParseDelta("-10080:00"));
        Assert.Null(CueTiming.ParseDelta("+168:00:01"));
        Assert.Null(CueTiming.ParseDelta("+2147483647:59:59"));
        Assert.Null(CueTiming.ParseDelta("+2147483647:59"));
    }
}
