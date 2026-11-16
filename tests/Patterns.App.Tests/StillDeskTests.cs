using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A still desk rebuilds nothing: its baseline is taken once the desk has taken its own first readings (<see cref="StillDesk"/>), and a
/// reading that moves within one light — the process's count of contained errors, Patterns' CPU — moves no word the Eye hashes, while
/// a suggestion appearing still does.
/// </summary>
public sealed class StillDeskTests
{
    private static MetricSample Calm(int i) => new()
    {
        Utc = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc).AddSeconds(i),
        CpuAppPct = 8, CpuSystemPct = 20, RamAppMB = 600, RamSystemPct = 45, RamTotalMB = 32768,
        VramUsedMB = 800, VramTotalMB = 8192, OutputFps = 0, DiskFreeGB = 120, Threads = 40, Handles = 900,
    };

    [AvaloniaFact]
    public void AStillDeskWithFiveErrorsAlreadyCountedRebuildsNothingOnceItsBaselineFollowsItsOwnReadings()
    {
        var b = TestApp.Boot();
        try
        {
            // Five contained errors stand when the desk first samples: the advisor's amber row is there from its first reading. A
            // baseline taken before that reading counts the row's arrival as a rebuild; the helper's is taken after it.
            for (var i = 0; i < 5; i++) HealthMonitor.Record($"a still desk's planted error {i + 1}");
            var still = StillDesk.Baseline(b.Services);
            Assert.Contains(b.Services.AttentionRows(), r => r.Section == "ADVICE" && r.Item == "Errors caught and contained" && r.Light == CheckLight.Amber);
            for (var tick = 0; tick < 10; tick++)
            {
                Thread.Sleep(100);
                Dispatcher.UIThread.RunJobs();
                b.Services.Eye.Refresh();
            }
            // The window holds the reading the fence is about: the desk's first sample after the baseline, counting the same five.
            var next = StillDesk.NextSample(b.Services, still);
            Assert.True(next.Faults == still.SampleFaults,
                $"the first sample after the baseline counted {next.Faults} contained errors, the baseline's {still.SampleFaults} (last: {HealthMonitor.LastFault ?? "none"})");
            Dispatcher.UIThread.RunJobs();
            b.Services.Eye.Refresh();
            StillDesk.AssertStill(b.Services, still, 10, "a still desk with five errors counted");
        }
        finally
        {
            b.Dispose();
            HealthMonitor.Reset();
        }
    }

    [AvaloniaFact]
    public void ACountOrAReadingMovingWithinOneLightRebuildsNothingAndASuggestionAppearingDoes()
    {
        var b = TestApp.Boot();
        var services = b.Services;
        try
        {
            // The sampler stopped: the samples are the test's, and nothing pumps the desk's clocks between two refreshes.
            services.Metrics.Live = false;
            services.Metrics.Ingest(Calm(0) with { Faults = 5 });
            services.Eye.Refresh();
            var builds = services.Eye.Cost.Total;
            var before = services.Eye.Graph;
            services.Metrics.Ingest(Calm(0) with { Faults = 6 });
            services.Eye.Refresh();
            Assert.True(builds == services.Eye.Cost.Total, $"a sixth contained error rebuilt the Eye — {StillDesk.Moved(before, services.Eye.Graph)}");
            // The reading still reaches the person: the Super Check's value and the suggestion said whole.
            Assert.Contains(services.AttentionRows(), r => r.Item == "Errors caught and contained" && r.Value.StartsWith("6 this session", StringComparison.Ordinal));
            Assert.Contains(services.Metrics.Suggestions, s => s.Said == "Errors caught and contained: 6 this session");

            // Patterns' CPU over the advisor's minute: 70 % then 71 %, one light.
            for (var i = 1; i <= 60; i++) services.Metrics.Ingest(Calm(i) with { CpuAppPct = 70 });
            services.Eye.Refresh();
            builds = services.Eye.Cost.Total;
            before = services.Eye.Graph;
            for (var i = 61; i <= 120; i++) services.Metrics.Ingest(Calm(i) with { CpuAppPct = 71 });
            services.Eye.Refresh();
            Assert.True(builds == services.Eye.Cost.Total, $"Patterns' CPU moving 70 → 71 % rebuilt the Eye — {StillDesk.Moved(before, services.Eye.Graph)}");
            Assert.Contains(services.Metrics.Suggestions, s => s.Id == "cpu-app-high" && s.Reading == "71%");

            // A suggestion appearing is a fact: four contained errors are advice in green, the fifth turns it amber and the Eye rebuilds once.
            for (var i = 121; i <= 180; i++) services.Metrics.Ingest(Calm(i) with { Faults = 4 });
            services.Eye.Refresh();
            builds = services.Eye.Cost.Total;
            services.Metrics.Ingest(Calm(181) with { Faults = 5 });
            services.Eye.Refresh();
            Assert.Equal(builds + 1, services.Eye.Cost.Total);
        }
        finally
        {
            b.Dispose();
        }
    }
}
