using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A still desk rebuilds nothing: its baseline is taken once the desk has taken its own first readings (<see cref="StillDesk"/>), and a
/// reading that moves within one light — the process's count of contained errors, Patterns' CPU, the desk's tick under its pinned red
/// light — moves no word the Eye hashes, while a suggestion appearing, an area fault or the Desk tick row changing its light still does.
/// </summary>
public sealed class StillDeskTests
{
    /// <summary>The tick run 336 saw cross the amber bound inside a still window: 27.3 ms, its slowest area audio.</summary>
    private const double Sighted = 27.3;

    /// <summary>The desk's last minute of ticks recorded fast by hand, so the Desk tick row stands green on recorded facts alone and no tick has to be pumped for a baseline.</summary>
    private static void FastMinute(Patterns.App.Services.AppServices services)
    {
        for (var t = 0; t < TickBudget.Window; t++) services.DeskTick.Record(1);
        Assert.True(services.DeskTick.Faults == 0 && services.DeskTick.WorstMs <= TickBudget.SlowMs,
            $"the desk's minute of ticks is not green: {StillDesk.DeskWords(services)}");
        Assert.DoesNotContain(services.AttentionRows(), r => r.Item == "Desk tick");
    }

    private static CheckRow DeskTickRow(Patterns.App.Services.AppServices services)
        => Assert.Single(services.AttentionRows(), r => r.Item == "Desk tick");

    private static IReadOnlyList<string> DeskNodeWords(Patterns.App.Services.AppServices services)
        => services.Eye.Graph.Find(EyeGraph.DeskId)?.Words.ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>
    /// The baseline's pin holds the Desk tick row's words: from a green minute recorded by hand (nothing pumps the desk's clocks, so no
    /// real tick lands), the baseline pins the light red; a tick of the sighting's 27.3 ms and then one of 400 ms move the row's reading
    /// and neither its light nor a word the Eye hashes, so the Eye neither rebuilds nor moves its revision. An area that throws is a fact
    /// the pin does not cover: the row's note says it and the Eye rebuilds once. Without the pin the 27.3 ms tick turns the green row
    /// amber inside the window and is counted, as run 336 counted it.
    /// </summary>
    [AvaloniaFact]
    public void APinnedDeskTickLightKeepsItsWordsWhileItsReadingRunsOnAndAnAreaFaultStillMovesThem()
    {
        var b = TestApp.Boot();
        var services = b.Services;
        try
        {
            FastMinute(services);
            var still = StillDesk.Baseline(services);
            var pinned = DeskTickRow(services);
            Assert.Equal(CheckLight.Red, pinned.Light);
            Assert.Equal(services.DeskTick.Ticks, still.PinnedAtTick);
            var words = DeskNodeWords(services);
            Assert.Contains(EyeFacts.AttentionWords(pinned), words);

            var reading = pinned.Value;
            foreach (var ms in new[] { Sighted, 400.0 })
            {
                var at = StillDesk.At(services);
                var what = string.Create(CultureInfo.InvariantCulture, $"a {ms:0.0} ms tick under the pinned light");
                services.DeskTick.Record(ms, "audio", ms);
                Assert.False(services.Eye.Refresh(), $"the Eye rebuilt for {what} — {StillDesk.Moved(at.Graph, services.Eye.Graph)}");
                StillDesk.AssertUnmoved(services, at, what);
                StillDesk.AssertStill(services, still, 1, what);
                var row = DeskTickRow(services);
                Assert.Equal((CheckLight.Red, pinned.Note), (row.Light, row.Note));
                Assert.NotEqual(reading, row.Value);                                                   // the reading ran on, in the value
                Assert.Equal(words, DeskNodeWords(services));                                          // and no hashed word moved
                reading = row.Value;
            }
            Assert.Contains("worst 400.0 ms (audio)", reading, StringComparison.Ordinal);

            // An area that threw is a fact of the row's own: its note says so, and the Eye rebuilds once for it.
            var before = StillDesk.At(services);
            services.DeskTick.RecordFault();
            Assert.True(services.Eye.Refresh(), "the Eye did not rebuild for an area that failed inside the tick");
            Assert.Equal(before.Builds + 1, services.Eye.Cost.Total);
            var faulted = DeskTickRow(services);
            Assert.Equal(CheckLight.Red, faulted.Light);
            Assert.Contains("an area failed", faulted.Note, StringComparison.Ordinal);
            Assert.Contains("an area failed", StillDesk.Moved(before.Graph, services.Eye.Graph), StringComparison.Ordinal);

            // The pin holds while it is in the ring: a window ending at the ring's last tick with the pin in it passes the guard, and
            // the next tick, which pushes the pin out, is a window that outlived it and says so.
            var since = still with { Builds = services.Eye.Cost.Total };
            while (services.DeskTick.Ticks - still.PinnedAtTick < TickBudget.Window - 1) services.DeskTick.Record(1);
            Assert.False(services.Eye.Refresh(), $"the Eye rebuilt while the pin was in the ring — {StillDesk.DeskWords(services)}");
            StillDesk.AssertStill(services, since, TickBudget.Window - 1, "the ring's last tick with the pin in it");
            services.DeskTick.Record(1);
            var outlived = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => StillDesk.AssertStill(services, since, TickBudget.Window, "a window past the ring"));
            Assert.Contains("outlived the pinned Desk tick", outlived.Message, StringComparison.Ordinal);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// The pin's twin, the product's rule it stands beside: with no pin, a green Desk tick row turned amber by one tick past the slow bound
    /// is a light's change, and the Eye rebuilds once with the row's amber words among the desk's — the words run 336's still window
    /// counted. Recorded by hand from a green minute; nothing pumps the desk's clocks.
    /// </summary>
    [AvaloniaFact]
    public void UnpinnedOneSlowTickTurnsTheGreenDeskTickRowAmberAndTheEyeRebuildsOnceNamingItsWords()
    {
        var b = TestApp.Boot();
        var services = b.Services;
        try
        {
            FastMinute(services);
            services.Eye.Refresh();
            var at = StillDesk.At(services);
            Assert.DoesNotContain(DeskNodeWords(services), w => w.StartsWith("Desk tick:", StringComparison.Ordinal));
            services.DeskTick.Record(Sighted, "audio", Sighted);
            Assert.True(services.Eye.Refresh(), $"the Eye did not rebuild for the Desk tick row turning amber — {StillDesk.DeskWords(services)}");
            Assert.Equal(at.Builds + 1, services.Eye.Cost.Total);
            Assert.True(services.Eye.Rev > at.Rev, $"the rebuild left the picture's revision at {services.Eye.Rev}");
            var row = DeskTickRow(services);
            Assert.Equal(CheckLight.Amber, row.Light);
            var said = EyeFacts.AttentionWords(row);
            Assert.StartsWith("Desk tick: △ the desk skipped a frame in the last minute", said, StringComparison.Ordinal);
            Assert.Contains(said, DeskNodeWords(services));
            Assert.Contains($"new [{said}]", StillDesk.Moved(at.Graph, services.Eye.Graph), StringComparison.Ordinal);
        }
        finally
        {
            b.Dispose();
        }
    }

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
            var at = StillDesk.At(services);
            services.Metrics.Ingest(Calm(0) with { Faults = 6 });
            services.Eye.Refresh();
            StillDesk.AssertUnmoved(services, at, "a sixth contained error");
            // The reading still reaches the person: the Super Check's value and the suggestion said whole.
            Assert.Contains(services.AttentionRows(), r => r.Item == "Errors caught and contained" && r.Value.StartsWith("6 this session", StringComparison.Ordinal));
            Assert.Contains(services.Metrics.Suggestions, s => s.Said == "Errors caught and contained: 6 this session");

            // Patterns' CPU over the advisor's minute: 70 % then 71 %, one light.
            for (var i = 1; i <= 60; i++) services.Metrics.Ingest(Calm(i) with { CpuAppPct = 70 });
            services.Eye.Refresh();
            at = StillDesk.At(services);
            for (var i = 61; i <= 120; i++) services.Metrics.Ingest(Calm(i) with { CpuAppPct = 71 });
            services.Eye.Refresh();
            StillDesk.AssertUnmoved(services, at, "Patterns' CPU moving 70 → 71 %");
            Assert.Contains(services.Metrics.Suggestions, s => s.Id == "cpu-app-high" && s.Reading == "71%");

            // A suggestion appearing is a fact: four contained errors are advice in green, the fifth turns it amber and the Eye rebuilds once.
            for (var i = 121; i <= 180; i++) services.Metrics.Ingest(Calm(i) with { Faults = 4 });
            services.Eye.Refresh();
            var builds = services.Eye.Cost.Total;
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
