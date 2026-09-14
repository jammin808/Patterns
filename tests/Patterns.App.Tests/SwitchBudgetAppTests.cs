using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.ViewModels;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The switch on the clock: every press on the rail is timed and kept, the Machine page's line and
/// the super-check row read it, the CSV carries it, and the work a page wants on arrival runs
/// below the frame and still runs.
/// </summary>
public class SwitchBudgetAppTests
{
    [AvaloniaFact]
    public void EveryPageSwitchIsTimedAndThePagesArrivalWorkRunsBelowTheFrame()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var before = services.Switches.Switches;
            vm.SelectPage(Shell.IndexOf("Machine"));
            Dispatcher.UIThread.RunJobs();
            vm.SelectPage(Shell.IndexOf("Pattern"));
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Switches.Switches >= before + 1, services.Switches.Describe());
            var last = services.Switches.Last!.Value;
            Assert.Contains(last.Page, new[] { "Machine", "Pattern" });
            Assert.True(last.HandlerMs >= 0);
            Assert.True(last.TotalMs >= last.HandlerMs);
            vm.PollNow();
            Assert.StartsWith("Page switch ", vm.SwitchText);
            Assert.Contains("in the last sixty", vm.SwitchText);
            // The Pattern page's read of the presets folder ran after the switch, below the frame — and ran.
            Assert.Contains("No presets yet", vm.PresetHint);
            // The super-check and the CSV carry it.
            var facts = services.Metrics.GatherFacts();
            Assert.True(facts.SwitchWorstMs >= 0, facts.SwitchWorstMs.ToString());
            Assert.NotEmpty(facts.SwitchWorstWords);
            var report = SuperCheck.Run(facts);
            var row = Assert.Single(report.Rows, r => r.Item == "Page switch");
            Assert.Contains("worst", row.Value);
            Assert.Contains("switchWorstMs", MetricsCsv.Header);
            Assert.Contains(",switchWorstMs,slowSwitches", MetricsCsv.Header);
        }
        finally
        {
            b.Dispose();
        }
    }
}
