using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 64: closing a surface releases its lifetime, proved by counting. The desk boots, opens
/// the Run layout (the monitor's pipeline), walks two pages, builds and shuts a node, and closes
/// (<see cref="DeskLifecycle.Cycle"/>); after a full collection the census — desks alive, nodes,
/// render seats, budgets, pools, frames, pictures, ledger owners, mounts — reads exactly what it
/// read after the first such boot, cycle after cycle, and the managed heap has no slope. The
/// desks, timers and pictures are judged by what was not there at that reading (round 92): a
/// desk, timer or picture a cycle leaves is counted, another test's leaving is not. Round 62's
/// leak (every closed desk rooted through one off-tree pipeline) would fail this in its first
/// cycle. The cycle count is PATTERNS_LIFECYCLE_CYCLES (CI's lifecycle job runs 100); a dozen in
/// the suite.
/// </summary>
public class LifecycleTests
{
    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    [AvaloniaFact]
    public void TheDeskBootsAndClosesRepeatedlyWithTheCensusBackAtBaselineAndNoHeapSlope()
    {
        var process = DeskLifecycle.Warm();                                 // the first boot fills the process-wide caches once
        // Run alone (CI's lifecycle job sets PATTERNS_LIFECYCLE_STRICT) the whole process reads
        // nought after that boot: a closed desk is reclaimed whole, nothing static points at it.
        if (Environment.GetEnvironmentVariable("PATTERNS_LIFECYCLE_STRICT") == "1")
        {
            foreach (var name in new[] { "desks", "nodes", "timers", "pipelines", "frameBudgets", "ledgerOwners", "inputMounts", "openFrames" })
            {
                Assert.True(process[name] == 0, $"{name} = {process[name]} after a closed boot — timers running: {string.Join(", ", DeskTimers.RunningOwners)}");
            }
        }

        // The cycles are judged against a mark taken now. In the whole suite another test's
        // desks, timers and pictures may be there at the mark, and leave on their own clocks
        // (a late close's shutdown tail, the residency sweep of an idle picture): what was there
        // at the mark is not a cycle's, and its leaving is no change. What a cycle makes or starts
        // and leaves is counted, whenever its object was made, and every other row is the whole
        // process's against the baseline taken at the mark.
        var cycles = int.TryParse(Environment.GetEnvironmentVariable("PATTERNS_LIFECYCLE_CYCLES"), out var n) && n > 0 ? n : 12;
        var managed = new List<long>();
        var change = DeskLifecycle.FirstChange(cycles, afterCycle: _ => managed.Add(GC.GetTotalMemory(true)));
        Assert.True(change is null, change?.Words);

        // No slope: the later cycles' median sits no higher than the earlier ones' plus a margin.
        var third = Math.Max(1, cycles / 3);
        var early = Median(managed.Take(third));
        var late = Median(managed.TakeLast(third));
        const long margin = 48L * 1024 * 1024;
        Assert.True(late <= early + margin, $"the managed heap climbed from {early / 1_048_576} MB to {late / 1_048_576} MB over {cycles} boots");
    }

    /// <summary>
    /// A change that lands after the desk closed — a device's last line, a page's last edit, a
    /// runtime flag — arms no timer: the save and re-apply debounces, the wire's push and the
    /// Run and Cues pages' refreshes all refuse a closed desk. A running timer would root the
    /// whole desk (the census found the pattern under test: a debounce armed by the last
    /// publish, never ticking in a headless host, holding every closed desk).
    /// </summary>
    [AvaloniaFact]
    public void AChangeAfterTheDeskClosedArmsNoTimer()
    {
        using var home = TempRun.Folder("patterns-tests-");                // the save asked after the close writes into it
        var b = TestApp.Boot(dir: home);
        var services = b.Services;
        b.Dispose();
        try
        {
            TestApp.DrainDispatcher();
            var before = DeskTimers.Running;                                // another test's leftovers, if any, are not this test's
            services.State.Countdown.Label = "after the close";             // a tracked change: the publish, the save debounce, the pages' refreshes, the wire's push
            services.PublishRuntime();                                      // a runtime flag: the wire's push alone
            services.SaveInBackground();
            TestApp.DrainDispatcher();
            Assert.True(DeskTimers.Running == before, $"timers armed after the close: {string.Join(", ", DeskTimers.RunningOwners)}");
        }
        finally
        {
            TestApp.FlushFiles(services);                                   // that save lands before its folder goes
        }
    }

    [AvaloniaFact]
    public void TheCensusIsOnStateAndInTheSupportTicketWhileTheDeskRuns()
    {
        var b = TestApp.Boot();
        try
        {
            var state = System.Text.Json.JsonDocument.Parse(new CommandRouter(b.Services).StateJson()).RootElement.GetProperty("census");
            Assert.True(state.GetProperty("desks").GetInt64() >= 1);
            Assert.True(state.GetProperty("pipelines").GetInt64() >= 1);                  // the preview's seat at least
            Assert.Equal(5, state.GetProperty("ledgerOwners").GetInt64());
            Assert.Contains("Lifetime census: desks", b.Vm.BuildSupportInfo());
        }
        finally
        {
            b.Dispose();
        }
    }
}
