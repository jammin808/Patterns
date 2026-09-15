using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 65: the shutdown as phases on a report. Every step runs in its phase's order on a clean
/// close; a failure injected into every step in turn never stops the steps after it — the show
/// lock, the final save, recovery, ownership and the mutex run whatever failed before them; and an
/// autosave stuck on a dead share cannot hold the exit: the final save waits a bounded time, the
/// recovery record is kept, and the next start is told why.
/// </summary>
public class ShutdownPhaseTests
{
    private static readonly string[] Critical = { "show lock", "final save", "ownership", "instance mutex" };

    [AvaloniaFact]
    public void TheStepsRunInTheirPhasesOrderAndEveryOneRanOnACleanClose()
    {
        var b = TestApp.Boot();
        var services = b.Services;
        b.Dispose();
        var report = services.ShutdownReport;
        Assert.NotEmpty(report);
        Assert.All(report, s => Assert.Equal("ran", s.Outcome));
        var phases = (IList<string>)AppServices.ShutdownPhases;
        var order = report.Select(s => phases.IndexOf(s.Phase)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.OrderBy(i => i), order);
        foreach (var step in Critical) Assert.Contains(report, s => s.Step == step);
        Assert.Contains(report, s => s.Step == "recovery cleared");
        Assert.Equal("audience", report[0].Phase);
        Assert.Equal("process", report[^1].Phase);
    }

    [AvaloniaFact]
    public void AFailureInjectedIntoEveryStepInTurnNeverStopsTheStepsAfterIt()
    {
        var probe = TestApp.Boot();
        var probed = probe.Services;
        probe.Dispose();
        var steps = probed.ShutdownReport.Select(s => s.Step).ToList();
        Assert.True(steps.Count > 30, $"only {steps.Count} steps");
        try
        {
            foreach (var step in steps)
            {
                AppServices.FailShutdownStep = step;
                var b = TestApp.Boot();
                var services = b.Services;
                b.Dispose();
                var report = services.ShutdownReport;
                Assert.Contains(report, s => s.Step == step && s.Outcome.StartsWith("failed: injected", StringComparison.Ordinal));
                foreach (var other in steps.Where(s => s != step))
                {
                    if (other == "recovery cleared" && step == "final save")
                    {
                        Assert.Contains(report, s => s.Step == "recovery kept" && s.Outcome == "ran");   // no save landed: the record stays
                        continue;
                    }
                    Assert.True(report.Any(s => s.Step == other && s.Outcome == "ran"), $"with {step} failing, {other} did not run: {string.Join("; ", report.Select(s => $"{s.Step}={s.Outcome}"))}");
                }
            }
        }
        finally
        {
            AppServices.FailShutdownStep = null;
        }
    }

    [AvaloniaFact]
    public void AnAutosaveStuckOnADeadShareCannotHoldTheExitAndTheRecoveryRecordIsKept()
    {
        var b = TestApp.Boot();
        var services = b.Services;
        var gate = new ManualResetEventSlim(false);
        var wasWait = AppServices.ExitSaveWait;
        AppServices.ExitSaveWait = TimeSpan.FromMilliseconds(400);
        try
        {
            services.QueueFileWork("a share that never answers", () => gate.Wait());
            var sw = Stopwatch.StartNew();
            b.Dispose();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"the exit took {sw.Elapsed}");
            var report = services.ShutdownReport;
            Assert.Contains(report, s => s.Step == "final save" && s.Outcome == "ran");                  // the step ran; the save did not land
            Assert.Contains(report, s => s.Step == "recovery kept" && s.Outcome == "ran");
            Assert.DoesNotContain(report, s => s.Step == "recovery cleared");
            Assert.Contains(report, s => s.Step == "ownership" && s.Outcome == "ran");
            Assert.Contains(report, s => s.Step == "instance mutex" && s.Outcome == "ran");
            Assert.True(File.Exists(Path.Combine(b.Dir, WatchdogMarker.FileName)));
            Assert.Contains("could not be written at exit", WatchdogMarker.ReadAndClear(b.Dir));
        }
        finally
        {
            gate.Set();
            AppServices.ExitSaveWait = wasWait;
        }
    }
}
