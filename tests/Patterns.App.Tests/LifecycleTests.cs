using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 64: closing a surface releases its lifetime, proved by counting. The desk boots, opens
/// the Run layout (the monitor's pipeline), walks two pages, builds and shuts a node, and closes;
/// after a full collection the census — desks alive, nodes, render seats, budgets, pools,
/// frames, pictures, ledger owners, mounts — reads exactly what it read after the first such
/// boot, cycle after cycle, and the managed heap has no slope. Round 62's leak (every closed
/// desk rooted through one off-tree pipeline) would fail this in its first cycle. The cycle
/// count is PATTERNS_LIFECYCLE_CYCLES (CI's lifecycle job runs 100); a dozen in the suite.
/// </summary>
public class LifecycleTests
{
    private static LifetimeCensus Take()
    {
        TestApp.DrainDispatcher();                                          // the closed desk's last continuations run to their end first
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return DeskCensus.Take();
    }

    private static void Cycle()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.SelectRunCommand.Execute(null);                              // the Run layout: the monitor's tile makes its pipeline
            Settle();
            vm.SelectPage(Shell.IndexOf("Screens"));
            Settle();
            using (var node = NodeHost.Build(NodeKind.Timer, new SettingsStore(Path.Combine(b.Dir, "node"))))
            {
                Dispatcher.UIThread.RunJobs();
            }
            vm.SelectPage(Shell.IndexOf("Pattern"));
            Settle();
        }
        finally
        {
            b.Dispose();
        }
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    [AvaloniaFact]
    public void TheDeskBootsAndClosesRepeatedlyWithTheCensusBackAtBaselineAndNoHeapSlope()
    {
        Cycle();                                                            // the first boot fills the process-wide caches once
        var baseline = Take();
        // Run alone (CI's lifecycle job sets PATTERNS_LIFECYCLE_STRICT) the baseline is nought
        // everywhere: a closed desk is reclaimed whole, nothing static points at it. In the
        // whole suite another test's leftovers may sit in the counts; the cycles are then judged
        // against that baseline, and the memlog's per-test lines name the leftovers' owners.
        if (Environment.GetEnvironmentVariable("PATTERNS_LIFECYCLE_STRICT") == "1")
        {
            foreach (var name in new[] { "desks", "nodes", "timers", "pipelines", "frameBudgets", "ledgerOwners", "inputMounts", "openFrames" })
            {
                Assert.True(baseline[name] == 0, $"{name} = {baseline[name]} after a closed boot — timers running: {string.Join(", ", DeskTimers.RunningOwners)}");
            }
        }

        var cycles = int.TryParse(Environment.GetEnvironmentVariable("PATTERNS_LIFECYCLE_CYCLES"), out var n) && n > 0 ? n : 12;
        var managed = new List<long>();
        for (var i = 1; i <= cycles; i++)
        {
            Cycle();
            var census = Take();
            var differences = census.Differences(baseline);
            Assert.True(differences.Count == 0, $"cycle {i} of {cycles}: {string.Join("; ", differences)} — timers running: {string.Join(", ", DeskTimers.RunningOwners)} — baseline {baseline.Describe()}");
            managed.Add(GC.GetTotalMemory(true));
        }

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
        var b = TestApp.Boot();
        var services = b.Services;
        b.Dispose();
        TestApp.DrainDispatcher();
        var before = DeskTimers.Running;                                    // another test's leftovers, if any, are not this test's
        services.State.Countdown.Label = "after the close";                 // a tracked change: the publish, the save debounce, the pages' refreshes, the wire's push
        services.PublishRuntime();                                          // a runtime flag: the wire's push alone
        services.SaveInBackground();
        TestApp.DrainDispatcher();
        Assert.True(DeskTimers.Running == before, $"timers armed after the close: {string.Join(", ", DeskTimers.RunningOwners)}");
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
