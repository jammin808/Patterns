using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The side effects follow the sections an edit touched: a countdown edit makes no decoder, box,
/// port or link reconsider itself; a box's edit reaches the devices alone; a picture's edit the
/// input pool alone; one bulk edit of both runs each once; a forced republish and a show loaded
/// whole run everything; and the budget on the Machine page says what each pass cost.
/// </summary>
public class SideEffectDispatchTests
{
    private static Dictionary<string, (long Runs, long Skipped)> Tally(ReconcileBudget budget)
        => budget.Lines().ToDictionary(l => l.Name, l => (l.Runs, l.Skipped));

    private static (long Runs, long Skipped) Delta(Dictionary<string, (long Runs, long Skipped)> before, ReconcileBudget budget, string name)
    {
        var now = budget.Of(name) ?? new ReconcileBudget.Line(name, 0, 0, -1, -1, 0);
        var was = before.TryGetValue(name, out var b) ? b : (0, 0);
        return (now.Runs - was.Runs, now.Skipped - was.Skipped);
    }

    [AvaloniaFact]
    public void AnEditReachesOnlyTheSystemsThatReadItsSections()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var budget = services.Reconciles;
            Dispatcher.UIThread.RunJobs();

            // A countdown edit: nothing that mounts, opens, listens or links looks at itself.
            var before = Tally(budget);
            var passes = budget.Passes;
            vm.State.Countdown.Enabled = !vm.State.Countdown.Enabled;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(passes + 1, budget.Passes);
            Assert.Equal((0, 1), Delta(before, budget, "inputs"));
            Assert.Equal((0, 1), Delta(before, budget, "devices"));
            Assert.Equal((0, 1), Delta(before, budget, "wire"));
            Assert.Equal((0, 1), Delta(before, budget, "beacon"));
            Assert.Equal((0, 1), Delta(before, budget, "twin"));
            Assert.Equal("Countdown", budget.LastSections);
            Assert.Equal(0, budget.LastRan);

            // A box's edit: the devices, and only the devices.
            before = Tally(budget);
            vm.State.Interactive.Enabled = !vm.State.Interactive.Enabled;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((1, 0), Delta(before, budget, "devices"));
            Assert.Equal((0, 1), Delta(before, budget, "inputs"));
            Assert.Equal((0, 1), Delta(before, budget, "twin"));
            Assert.Equal("Interactive", budget.LastSections);

            // A picture's edit: the input pool, and not the boxes.
            before = Tally(budget);
            vm.State.Pattern.Kind = vm.State.Pattern.Kind == PatternKind.Grid ? PatternKind.ColorBars : PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((1, 0), Delta(before, budget, "inputs"));
            Assert.Equal((0, 1), Delta(before, budget, "devices"));
            Assert.Equal((0, 1), Delta(before, budget, "beacon"));

            // One bulk edit of both: one pass, each system once.
            before = Tally(budget);
            passes = budget.Passes;
            services.BulkEdit(() =>
            {
                vm.State.Pattern.Kind = PatternKind.TestCard;
                vm.State.Interactive.Enabled = !vm.State.Interactive.Enabled;
                vm.State.Countdown.Enabled = !vm.State.Countdown.Enabled;
            });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(passes + 1, budget.Passes);
            Assert.Equal((1, 0), Delta(before, budget, "inputs"));
            Assert.Equal((1, 0), Delta(before, budget, "devices"));
            Assert.Equal((0, 1), Delta(before, budget, "twin"));
            Assert.Equal("Countdown, Interactive, Pattern", budget.LastSections);

            // A forced republish runs everything, as it always did.
            before = Tally(budget);
            var full = budget.FullPasses;
            services.RepublishNow();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(full + 1, budget.FullPasses);
            Assert.Equal((1, 0), Delta(before, budget, "twin"));
            Assert.Equal((1, 0), Delta(before, budget, "inputs"));
            Assert.Equal("everything", budget.LastSections);

            // The Machine page's line and the assistant's brief read the budget.
            vm.PollNow();
            Assert.StartsWith("Side effects:", vm.SideEffectsText);
            Assert.Contains(" run", vm.SideEffectsText);                                     // the costliest systems named with their runs
            Assert.Contains(services.DeskHealthWords().Health, l => l.StartsWith("Side effects:", StringComparison.Ordinal));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AnEditInTheSandboxFollowsItsSectionsToo()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            var budget = services.Reconciles;
            var before = Tally(budget);
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((1, 0), Delta(before, budget, "inputs"));       // the preview's inputs follow the sandbox
            Assert.Equal((0, 1), Delta(before, budget, "devices"));
            Assert.Equal("Pattern", budget.LastSections);
        }
        finally
        {
            b.Dispose();
        }
    }
}
