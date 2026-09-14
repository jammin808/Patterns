using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The desk's second in lanes, and the pages warmed with the headroom the desk has.</summary>
public class TickSchedulerTests
{
    private static TickScheduler Desk() => new TickScheduler()
        .Add("screens", TickLane.Critical).Add("cues", TickLane.Critical).Add("clock", TickLane.Critical)
        .Add("quality", TickLane.Steady).Add("devices", TickLane.Steady)
        .Add("machine", TickLane.Housekeeping).Add("remote", TickLane.Housekeeping).Add("pickers", TickLane.Housekeeping).Add("places", TickLane.Housekeeping);

    [Fact]
    public void TheLanesKeepTheirOrderAndTheHousekeepingTakesTurns()
    {
        var s = Desk();
        Assert.Equal(new[] { "screens", "cues", "clock" }, s.Critical.Select(a => a.Name));
        Assert.Equal(new[] { "quality", "devices" }, s.Steady.Select(a => a.Name));
        Assert.Equal(new[] { "machine", "remote", "pickers", "places" }, s.HousekeepingOrder().Select(a => a.Name));
        Assert.Equal(0, s.Settle(Array.Empty<TickScheduler.Area>()));
        Assert.Equal(new[] { "remote", "pickers", "places", "machine" }, s.HousekeepingOrder().Select(a => a.Name));   // the start moved round
        s.Settle(Array.Empty<TickScheduler.Area>());
        Assert.Equal(new[] { "pickers", "places", "machine", "remote" }, s.HousekeepingOrder().Select(a => a.Name));
    }

    [Fact]
    public void WhatDidNotFitRunsFirstNextTickAndNoneStarves()
    {
        var s = Desk();
        var order = s.HousekeepingOrder();
        // The budget ran out after two: the last two are carried.
        Assert.Equal(2, s.Settle(order.Skip(2).ToList()));
        Assert.Equal(2, s.Carried);
        var next = s.HousekeepingOrder().Select(a => a.Name).ToList();
        Assert.Equal(new[] { "pickers", "places" }, next.Take(2));                                       // the carried first
        Assert.Equal(4, next.Count);                                                                      // then the rest, each once, from the start that moved round
        Assert.Equal(new[] { "remote", "machine" }, next.Skip(2));
        Assert.Equal(0, s.Settle(Array.Empty<TickScheduler.Area>()));
        Assert.Equal(0, s.Carried);

        // Over many ticks with room for one area each, every area runs equally often.
        var runs = new Dictionary<string, int>();
        for (var tick = 0; tick < 40; tick++)
        {
            var due = s.HousekeepingOrder();
            runs[due[0].Name] = runs.GetValueOrDefault(due[0].Name) + 1;
            s.Settle(due.Skip(1).ToList());
        }
        Assert.All(runs.Values, n => Assert.Equal(10, n));
    }

    [Fact]
    public void TheBudgetIsAFewMillisecondsAndTheFitIsStrict()
    {
        Assert.Equal(4, TickScheduler.HousekeepingBudgetMs);
        Assert.True(TickScheduler.Fits(0));
        Assert.True(TickScheduler.Fits(3.9));
        Assert.False(TickScheduler.Fits(4));
        Assert.False(TickScheduler.Fits(12));
        Assert.True(TickScheduler.Fits(7, budgetMs: 8));
    }

    [Fact]
    public void ThePagesWarmInTheOrderAShowReachesForThemAndOnlyWithHeadroom()
    {
        var rail = new[] { "Panel", "Cues", "Looks", "Install", "Pattern", "Media", "Overlays", "Screens", "Interactive", "Machine", "Help" };
        Assert.Equal(new[] { "Cues", "Screens", "Media", "Interactive", "Machine", "Looks", "Panel", "Install", "Pattern", "Overlays", "Help" }, WarmUpPlan.Order(rail));
        Assert.Empty(WarmUpPlan.Order(Array.Empty<string>()));
        Assert.False(WarmUpPlan.ShouldPause(armedAndLive: false, switchOpen: false, lastTickMs: 3));
        Assert.True(WarmUpPlan.ShouldPause(armedAndLive: true, switchOpen: false, lastTickMs: 3));      // the show is on
        Assert.True(WarmUpPlan.ShouldPause(armedAndLive: false, switchOpen: true, lastTickMs: 3));      // a switch's frame first
        Assert.True(WarmUpPlan.ShouldPause(armedAndLive: false, switchOpen: false, lastTickMs: 40));    // a stressed tick
        Assert.True(WarmUpPlan.BuildAhead("Fractals", machineGB: 32));
        Assert.False(WarmUpPlan.BuildAhead("Fractals", machineGB: 4));                                   // a small machine builds the likely pages alone
        Assert.True(WarmUpPlan.BuildAhead("Cues", machineGB: 4));
        Assert.Equal(TimeSpan.FromSeconds(1), WarmUpPlan.Pause);
    }
}
