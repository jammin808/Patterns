using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Views.Controls;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The pages warmed with the headroom the desk has: the pages a show reaches for first; a wait
/// whenever the desk says it has no room, and the builds carrying on after; every build's time
/// remembered; and a small machine building the likely pages alone.
/// </summary>
public class WarmUpAppTests
{
    private static void Idle(int turns = 1)
    {
        for (var i = 0; i < turns; i++) Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ThePagesWarmInTheShowsOrderAndWaitWhileTheDeskHasNoHeadroom()
    {
        var wasAuto = LazyPage.AutoWarmUp;
        var wasGb = LazyPage.MachineGB;
        LazyPage.AutoWarmUp = false;
        LazyPage.MachineGB = 32;
        var paused = true;
        LazyPage.PauseOverride = () => paused;
        var order = new List<string>();
        Action<string> onBuilt = order.Add;
        LazyPage.PageBuilt += onBuilt;
        var b = TestApp.Boot();
        try
        {
            var window = b.Window;
            Idle(3);
            order.Clear();
            var builtBefore = LazyPage.In(window).Count(p => p.IsBuilt);
            Assert.DoesNotContain(LazyPage.In(window).Where(p => p.IsBuilt), p => p.Page == "Cues");

            // No headroom: the warm-up waits rather than builds.
            var pauses = LazyPage.WarmUpPauses;
            LazyPage.WarmUp(window);
            Idle(3);
            Assert.Empty(order);
            Assert.Equal(builtBefore, LazyPage.In(window).Count(p => p.IsBuilt));
            Assert.Equal(pauses + 1, LazyPage.WarmUpPauses);
            Assert.True(LazyPage.WarmUpPending > 0);
            Assert.Contains("warm-up paused", LazyPage.Words(window));

            // Headroom again: the builds carry on, the show's pages first, then the rest.
            paused = false;
            LazyPage.ContinueWarmUp();
            Idle(3);
            Assert.Equal(0, LazyPage.WarmUpPending);
            Assert.Equal(new[] { "Cues", "Screens", "Media", "Interactive", "Machine", "Looks" }, order.Take(6));
            Assert.True(order.Count > 6, "the rest followed");
            Assert.True(LazyPage.BuildTimes.ContainsKey("Cues"));
            Assert.True(LazyPage.BuildTimes.ContainsKey(order[^1]));
            Assert.StartsWith("Pages:", LazyPage.Words(window));
            Assert.Contains("slowest", LazyPage.Words(window));
            Assert.Equal(LazyPage.In(window).Count, LazyPage.In(window).Count(p => p.IsBuilt));
        }
        finally
        {
            LazyPage.PageBuilt -= onBuilt;
            LazyPage.PauseOverride = null;
            LazyPage.AutoWarmUp = wasAuto;
            LazyPage.MachineGB = wasGb;
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ASmallMachineBuildsTheLikelyPagesAheadAndLeavesTheRestToTheClick()
    {
        var wasAuto = LazyPage.AutoWarmUp;
        var wasGb = LazyPage.MachineGB;
        LazyPage.AutoWarmUp = false;
        LazyPage.PauseOverride = () => false;
        var b = TestApp.Boot();                                                                       // the boot assumes a desk-class machine
        try
        {
            var window = b.Window;
            Idle(3);
            LazyPage.MachineGB = 4;                                                                   // this one is small
            var left = LazyPage.WarmUpLeftToDemand;
            LazyPage.WarmUp(window);
            Idle(3);
            var built = LazyPage.In(window).Where(p => p.IsBuilt).Select(p => p.Page).ToHashSet();
            Assert.Contains("Cues", built);
            Assert.Contains("Machine", built);
            Assert.DoesNotContain("Fractals", built);                                                  // left to the click that wants it
            Assert.True(LazyPage.WarmUpLeftToDemand > left);
            Assert.Contains("left to demand", LazyPage.Words(window));
            Assert.Equal(0, LazyPage.WarmUpPending);
        }
        finally
        {
            LazyPage.PauseOverride = null;
            LazyPage.AutoWarmUp = wasAuto;
            LazyPage.MachineGB = wasGb;
            b.Dispose();
        }
    }
}
