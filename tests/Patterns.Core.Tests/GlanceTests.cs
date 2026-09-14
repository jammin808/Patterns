using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The glance line's facts: the pacer counts the slots the room did not get, the budget keeps a
/// p95 and the drops, the words read as a caller reads them, a slip parses as the wire says it,
/// and the plan verbs and the countdown's follow parse.
/// </summary>
public class GlanceTests
{
    [Fact]
    public void ThePacerCountsTheSlotsThatWentByUnpresented()
    {
        long slot = -1;
        Assert.True(FramePacer.ShouldPresent(0.000, 60, ref slot, out var missed));
        Assert.Equal(0, missed);                                                    // the first frame misses nothing
        Assert.False(FramePacer.ShouldPresent(0.010, 60, ref slot, out missed));   // the same slot: a wait, not a drop
        Assert.Equal(0, missed);
        Assert.True(FramePacer.ShouldPresent(0.017, 60, ref slot, out missed));    // the next slot: nothing missed
        Assert.Equal(0, missed);
        Assert.True(FramePacer.ShouldPresent(0.070, 60, ref slot, out missed));    // slot 4 after slot 1: two slots the room did not get
        Assert.Equal(2, missed);
        Assert.True(FramePacer.ShouldPresent(1.0, 0, ref slot, out missed));       // unlimited: never a drop
        Assert.Equal(0, missed);
    }

    [Fact]
    public void TheBudgetKeepsAP95AndTheDropsOfTheLastMinute()
    {
        var b = new FrameBudget(SinkKind.Output, 1, "Main");
        for (var i = 0; i < 95; i++) b.Record(4.2, "layers", 10 + i * 0.01);   // 95 frames at ~4 ms
        for (var i = 0; i < 5; i++) b.Record(30, "pattern:Fractal", 11 + i * 0.01);   // 5 slow ones
        b.RecordMissed(3, 11.5);
        var r = b.Read(11.9);
        Assert.Equal(100, r.FramesInWindow);
        Assert.Equal(4.5, r.P95Ms);                                                 // the 95th frame's bin, its upper edge
        Assert.Equal(3, r.Missed);
        Assert.Equal(3, b.Missed);
        Assert.Contains("p95 4.5 ms", r.Words);
        Assert.Contains("3 slots missed", r.Words);
        Assert.Equal(-1, new FrameBudget(SinkKind.Preview, 0, "").Read(1).P95Ms);
        // Past the window the drops and the histogram go with the buckets.
        Assert.Equal(0, b.Read(200).Missed);
        Assert.Equal(-1, b.Read(200).P95Ms);
        b.Reset();
        Assert.Equal(0, b.Missed);
    }

    [Fact]
    public void TheGlanceLineReadsTheOutputsFirstThenTheWordsEachServiceGave()
    {
        var out1 = new FrameBudgetReading(SinkKind.Output, 1, "Main", 100, 0, 100, 4, 8, "", 59.9, 5, 8.1, 0);
        var out2 = new FrameBudgetReading(SinkKind.Output, 2, "Side", 100, 0, 100, 4, 8, "", 60, 5, 7.6, 2);
        var pvw = new FrameBudgetReading(SinkKind.Preview, 0, "", 50, 0, 50, 2, 3, "", 30, 2, 2.5, 0);
        var idle = new FrameBudgetReading(SinkKind.Output, 3, "", 0, 0, 0, -1, -1, "", -1);
        Assert.Equal("OUT 1 60 fps · p95 8.1 ms", Glance.SinkWords(out1));
        Assert.Equal("OUT 2 60 fps · p95 7.6 ms · 2 slots missed", Glance.SinkWords(out2));
        Assert.Equal("PVW 30 fps · p95 2.5 ms", Glance.SinkWords(pvw));
        Assert.Equal("OUT 3 idle", Glance.SinkWords(idle));
        var lagged = new FrameBudgetReading(SinkKind.Output, 1, "Main", 100, 0, 100, 4, 8, "", 59.9, 5, 8.1, 0, 21.4, 12);
        Assert.Equal("OUT 1 60 fps · p95 8.1 ms · lag 21 ms", Glance.SinkWords(lagged));
        Assert.Equal("OUT 1", Glance.SinkName(SinkKind.Output, 1));
        Assert.Equal("PVW", Glance.SinkName(SinkKind.Preview, 0));
        var line = Glance.Line(new[] { pvw, out2, out1 }, "TWIN main · 1 standby in step", "", "DEVICE: Proj: INPUT HDMI 1 — rejected: INPT: out of parameter", "LATE +2:14", "LOCK ON");
        Assert.Equal("OUT 1 60 fps · p95 8.1 ms · OUT 2 60 fps · p95 7.6 ms · 2 slots missed · PVW 30 fps · p95 2.5 ms · TWIN main · 1 standby in step · DEVICE: Proj: INPUT HDMI 1 — rejected: INPT: out of parameter · LATE +2:14 · LOCK ON", line);
        Assert.Equal("", Glance.Line(Array.Empty<FrameBudgetReading>(), "", "", "", "", ""));
        Assert.Equal("OUT 1 60 fps · p95 8.1 ms · LATE +2:14 · GO→frame 34 ms · LOCK ON", Glance.Line(new[] { out1 }, "", "", "", "LATE +2:14", "LOCK ON", "GO→frame 34 ms"));
        Assert.Equal("LATE +2 min", Glance.PlanWords("+2 min", isLate: true));
        Assert.Equal("ON PLAN ON TIME", Glance.PlanWords("ON TIME", isLate: false));
        Assert.Equal("", Glance.PlanWords("", false));
    }

    [Theory]
    [InlineData("+2:00", 120)]
    [InlineData("-0:30", -30)]
    [InlineData("+90", 90)]
    [InlineData("90", 90)]
    [InlineData("-2m", -120)]
    [InlineData("1:02:03", 3723)]
    [InlineData("−1:00", -60)]
    public void ASlipParsesAsTheWireSaysIt(string text, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), CueTiming.ParseDelta(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("soon")]
    [InlineData("0")]
    [InlineData("1:xx")]
    public void WordsThatAreNotASlipAreNotOne(string text)
    {
        Assert.Null(CueTiming.ParseDelta(text));
    }

    [Fact]
    public void ThePlanVerbsAndTheCountdownsFollowParseOnTheWire()
    {
        Assert.Equal(new ShowAction(ShowActionKind.PlanShift, "", "+2:00"), ControlProtocol.Parse("PLAN SHIFT +2:00").Action);
        Assert.Equal(new ShowAction(ShowActionKind.PlanShift, "", "-0:30"), ControlProtocol.Parse("plan slip -30").Action);
        Assert.Equal(new ShowAction(ShowActionKind.PlanShift, "", "+1:30"), ControlProtocol.Parse("PLAN +90").Action);
        Assert.Equal(new ShowAction(ShowActionKind.PlanResume, "", ""), ControlProtocol.Parse("PLAN RESUME").Action);
        Assert.Equal(new ShowAction(ShowActionKind.PlanCatchUp, "", ""), ControlProtocol.Parse("PLAN CATCHUP").Action);
        Assert.False(ControlProtocol.Parse("PLAN SHIFT soon").IsAction);
        Assert.False(ControlProtocol.Parse("PLAN").IsAction);
        Assert.Equal(new ShowAction(ShowActionKind.CountdownFollow, "", "on"), ControlProtocol.Parse("COUNTDOWN FOLLOW ON").Action);
        Assert.Equal(new ShowAction(ShowActionKind.CountdownFollow, "", "on"), ControlProtocol.Parse("TIMER FOLLOW").Action);
        Assert.Equal(new ShowAction(ShowActionKind.CountdownFollow, "", "off"), ControlProtocol.Parse("COUNTDOWN FOLLOW OFF").Action);
        Assert.Contains("p95FrameMs", MetricsCsv.Header);
        Assert.Contains("missedSlots", MetricsCsv.Header);
        Assert.Contains("renderFaults", MetricsCsv.Header);
        Assert.EndsWith(",switchWorstMs,slowSwitches,goWorstMs,lagWorstMs,renderFaults,privateMB,managedMB,liveAgeWorstMs", MetricsCsv.Header);
        Assert.EndsWith(",42.4,3,,,0,,,", MetricsCsv.Line(new MetricSample { Utc = DateTime.UnixEpoch, SwitchWorstMs = 42.4, SlowSwitches = 3 }));
        Assert.EndsWith(",34.5,21.4,0,,,", MetricsCsv.Line(new MetricSample { Utc = DateTime.UnixEpoch, GoWorstMs = 34.5, LagWorstMs = 21.4 }));
        Assert.Equal(MetricsCsv.Header.Split(',').Length, MetricsCsv.Line(new MetricSample { P95FrameMs = 8.1, MissedSlots = 2 }).Split(',').Length);
        Assert.Equal("+2:00", CueTiming.FormatDeltaExact(TimeSpan.FromMinutes(2)));
        Assert.Equal("-0:30", CueTiming.FormatDeltaExact(TimeSpan.FromSeconds(-30)));
        Assert.Equal("+1:02:03", CueTiming.FormatDeltaExact(TimeSpan.FromSeconds(3723)));
        Assert.EndsWith(",8.1,2,,0,,,0,,,", MetricsCsv.Line(new MetricSample { P95FrameMs = 8.1, MissedSlots = 2 }));
    }
}
