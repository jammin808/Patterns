using System.Runtime.CompilerServices;
using Avalonia.Headless;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering.Media;

namespace Patterns.App.Tests;

/// <summary>
/// The lifecycle census's cycle and its judgement, one copy for <see cref="LifecycleTests"/> and
/// the census's own tests (<see cref="LifecycleCensusTests"/>), so the tests that prove the
/// judgement prove the one the lifecycle test runs. The cycles are judged against a mark taken
/// where <see cref="FirstChange"/> is called, after its own drain and collection. What was there at
/// the mark is another test's (or the warm-up's), and its leaving is no cycle's change; what a
/// cycle makes or starts and leaves is counted, whenever its object was made. In the desks,
/// timers and pictures rows the baseline is therefore nought, so a cycle that leaves one reads
/// "was 0, now 1"; every other row is the whole process's, as it always was.
/// </summary>
public static class DeskLifecycle
{
    /// <summary>
    /// The first cycle whose census differed from the baseline: which cycle of how many, the
    /// baseline and the census that cycle read (both against the mark, so nought at the baseline
    /// in the desks, timers and pictures rows), the rows that differed in words
    /// ("desks: was 0, now 1"), and the words a failure says.
    /// </summary>
    public sealed record Change(int Cycle, int Cycles, LifetimeCensus Baseline, LifetimeCensus Census, IReadOnlyList<string> Differences, string Words);

    /// <summary>The census after the closed desks' last continuations have run and a full collection; the whole process's unless a mark is given.</summary>
    public static LifetimeCensus Take(CensusMark? since = null)
    {
        DrainAndCollect();
        return DeskCensus.Take(since: since);
    }

    /// <summary>The closed desks' last continuations run to their end, then a full collection: what every census here is read after.</summary>
    private static void DrainAndCollect()
    {
        TestApp.DrainDispatcher();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// One cycle: the desk boots, opens the Run layout (the monitor's pipeline), walks two pages,
    /// builds and shuts a node, and closes. <paramref name="inCycle"/> runs with the desk up,
    /// after the walk and before the close, with the cycle's number (0 for the warm-up).
    /// </summary>
    public static void Cycle(int index = 0, Action<int, TestApp.Booted>? inCycle = null,
                             [CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        var b = TestApp.Boot(callerFile: file, callerMember: member);       // the memlog's line names the test, not this helper
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
            inCycle?.Invoke(index, b);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>The warm-up: one cycle fills the process-wide caches once; returns the whole process's census after it.</summary>
    public static LifetimeCensus Warm([CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        Cycle(0, null, file, member);
        return Take();
    }

    /// <summary>
    /// Marks what is there once the closed desks' last continuations have run and a full
    /// collection has taken what it can — the state every cycle's census is read in, so the rows
    /// that stay the whole process's start from no uncollected garbage whatever the caller did
    /// just before — then runs <paramref name="cycles"/> cycles and judges each against the mark:
    /// the first cycle whose census differs from the baseline, or null when none did. The
    /// baseline is read at the mark with nothing between, so the desks, timers and pictures rows
    /// read nought there. The words are built only on a difference, and carry the timers running
    /// and the pictures held that were not at the mark, and the whole process's census beside the
    /// baseline. <paramref name="afterCycle"/> runs after each cycle judged unchanged.
    /// </summary>
    public static Change? FirstChange(int cycles, Action<int, TestApp.Booted>? inCycle = null, Action<int>? afterCycle = null,
                                      [CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        DrainAndCollect();
        var mark = DeskCensus.Mark();
        var baseline = DeskCensus.Take(since: mark);
        for (var i = 1; i <= cycles; i++)
        {
            Cycle(i, inCycle, file, member);
            var census = Take(mark);
            var differences = census.Differences(baseline);
            if (differences.Count > 0)
            {
                var words = $"cycle {i} of {cycles}: {string.Join("; ", differences)}"
                          + $" — timers running since the mark: [{string.Join(", ", DeskTimers.RunningOwnersNotIn(mark.Timers))}]"
                          + $" — pictures held since the mark: [{string.Join(", ", ImageCache.PathsNotIn(mark.Pictures))}]"
                          + $" — the process: {DeskCensus.Take().Describe()} — baseline {baseline.Describe()}";
                return new Change(i, cycles, baseline, census, differences, words);
            }
            afterCycle?.Invoke(i);
        }
        return null;
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
