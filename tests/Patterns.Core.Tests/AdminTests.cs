using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

public class GpuSelectorTests
{
    private static GpuAdapterInfo Gpu(string name, uint vendor, long vramMB, long luid = 0, bool software = false)
        => new(name, vendor, 0x1234, vramMB, luid, software);

    private static readonly GpuAdapterInfo Igpu = Gpu("Intel(R) UHD Graphics 770", GpuAdapterInfo.VendorIntel, 128, luid: 11);
    private static readonly GpuAdapterInfo Dgpu = Gpu("NVIDIA GeForce RTX 3060", GpuAdapterInfo.VendorNvidia, 12288, luid: 22);
    private static readonly GpuAdapterInfo Soft = Gpu("Microsoft Basic Render Driver", GpuAdapterInfo.VendorMicrosoft, 0, luid: 33, software: true);

    [Fact]
    public void BestPrefersDiscreteOverIntegrated()
    {
        // Optimus laptop: iGPU enumerates first, but the show should render on the dGPU.
        var gpus = new[] { Igpu, Dgpu, Soft };
        Assert.Equal(1, GpuSelector.ChooseBest(gpus));
    }

    [Fact]
    public void BestPrefersBigIntegratedArcOverSmallDiscrete()
    {
        // VRAM outranks the vendor bonus: a 16 GB Intel Arc beats a 4 GB GTX.
        var arc = Gpu("Intel(R) Arc(TM) A770", GpuAdapterInfo.VendorIntel, 16384);
        var gtx = Gpu("NVIDIA GeForce GTX 1650", GpuAdapterInfo.VendorNvidia, 4096);
        Assert.Equal(0, GpuSelector.ChooseBest(new[] { arc, gtx }));
    }

    [Fact]
    public void SoftwareAdapterLosesToAnyHardwareButWinsAlone()
    {
        Assert.Equal(0, GpuSelector.ChooseBest(new[] { Igpu, Soft }));
        Assert.Equal(0, GpuSelector.ChooseBest(new[] { Soft }));
        Assert.Equal(-1, GpuSelector.ChooseBest(Array.Empty<GpuAdapterInfo>()));
    }

    [Fact]
    public void PowerSavingPicksTheIntegratedCard()
    {
        var gpus = new[] { Dgpu, Igpu, Soft };
        Assert.Equal(1, GpuSelector.ChoosePowerSaving(gpus));
    }

    [Fact]
    public void ResolveHonoursEachPreference()
    {
        var gpus = new[] { Igpu, Dgpu };
        Assert.Equal(1, GpuSelector.Resolve(new GraphicsConfig(), gpus)); // default = best
        Assert.Equal(0, GpuSelector.Resolve(new GraphicsConfig { Preference = GpuPreferenceKind.PowerSaving }, gpus));
        Assert.Equal(-1, GpuSelector.Resolve(new GraphicsConfig { Preference = GpuPreferenceKind.LetWindowsDecide }, gpus));
        Assert.Equal(0, GpuSelector.Resolve(
            new GraphicsConfig { Preference = GpuPreferenceKind.Specific, AdapterName = "intel(r) uhd graphics 770" }, gpus));
    }

    [Fact]
    public void SpecificAdapterNoLongerPresentFallsBackToBest()
    {
        var config = new GraphicsConfig { Preference = GpuPreferenceKind.Specific, AdapterName = "Old Card From Another PC" };
        Assert.Equal(1, GpuSelector.Resolve(config, new[] { Igpu, Dgpu }));
    }

    [Fact]
    public void LuidMatchFindsTheAdapterHandedToAvalonia()
    {
        var gpus = new[] { Igpu, Dgpu };
        Assert.Equal(1, GpuSelector.MatchLuid(BitConverter.GetBytes(22L), gpus));
        Assert.Equal(-1, GpuSelector.MatchLuid(BitConverter.GetBytes(99L), gpus));
        Assert.Equal(-1, GpuSelector.MatchLuid(null, gpus));
        Assert.Equal(-1, GpuSelector.MatchLuid(new byte[4], gpus));
    }

    [Fact]
    public void RegistryValueMatchesTheChoice()
    {
        var gpus = new[] { Igpu, Dgpu };
        Assert.Equal("GpuPreference=2;", GpuSelector.RegistryValue(new GraphicsConfig(), gpus));
        Assert.Equal("GpuPreference=1;", GpuSelector.RegistryValue(
            new GraphicsConfig { Preference = GpuPreferenceKind.PowerSaving }, gpus));
        Assert.Equal("", GpuSelector.RegistryValue(
            new GraphicsConfig { Preference = GpuPreferenceKind.LetWindowsDecide }, gpus));
        Assert.Equal("GpuPreference=2;", GpuSelector.RegistryValue(
            new GraphicsConfig { Preference = GpuPreferenceKind.Specific, AdapterName = Dgpu.Name }, gpus));
        Assert.Equal("GpuPreference=1;", GpuSelector.RegistryValue(
            new GraphicsConfig { Preference = GpuPreferenceKind.Specific, AdapterName = Igpu.Name }, gpus));

        // A middle adapter that is neither the best nor the power pick maps to "let Windows decide".
        var mid = Gpu("AMD Radeon RX 6600", GpuAdapterInfo.VendorAmd, 8192, luid: 44);
        Assert.Equal("GpuPreference=0;", GpuSelector.RegistryValue(
            new GraphicsConfig { Preference = GpuPreferenceKind.Specific, AdapterName = mid.Name },
            new[] { Igpu, mid, Dgpu }));
    }
}

public class RestartRequestTests
{
    private static readonly DateTime T0 = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RequestedRestartHasNoBackoffDelay()
    {
        var policy = new SupervisorPolicy();
        var verdict = policy.OnExit(SupervisorPolicy.RestartRequestExitCode, false, TimeSpan.FromMinutes(1), T0);
        Assert.Equal(SupervisorAction.Restart, verdict.Action);
        Assert.Equal(TimeSpan.Zero, verdict.Delay);
    }

    [Fact]
    public void RequestedRestartDoesNotGrowTheCrashBackoff()
    {
        var policy = new SupervisorPolicy();
        policy.OnExit(SupervisorPolicy.RestartRequestExitCode, false, TimeSpan.FromSeconds(30), T0);
        var crash = policy.OnExit(1, false, TimeSpan.FromSeconds(30), T0.AddMinutes(1));
        Assert.Equal(2, crash.Delay.TotalSeconds); // still the first-crash delay
    }

    [Fact]
    public void RestartStormStillHitsTheLoopBreaker()
    {
        // A bug that exits 82 over and over must not flap the outputs forever.
        var policy = new SupervisorPolicy();
        SupervisorVerdict last = default;
        for (var i = 0; i < 8; i++)
        {
            last = policy.OnExit(SupervisorPolicy.RestartRequestExitCode, false, TimeSpan.FromSeconds(5), T0.AddSeconds(i * 10));
        }
        Assert.Equal(SupervisorAction.GiveUp, last.Action);
    }
}

public class MetricsHistoryTests
{
    private static MetricSample Sample(int second, double cpuApp = 10, double ramApp = 500, int slow = 0, double worst = 5)
        => new()
        {
            Utc = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc).AddSeconds(second),
            CpuAppPct = cpuApp,
            CpuSystemPct = 30,
            RamAppMB = ramApp,
            RamSystemPct = 50,
            SlowFrames = slow,
            WorstFrameMs = worst,
            OutputFps = 60,
            Handles = 900,
        };

    [Fact]
    public void RecentIsCappedAndOrdered()
    {
        var h = new MetricsHistory();
        for (var i = 0; i < MetricsHistory.RecentCapacity + 50; i++) h.Add(Sample(i));
        Assert.Equal(MetricsHistory.RecentCapacity, h.Recent.Count);
        Assert.True(h.Recent[0].Utc < h.Recent[^1].Utc);
    }

    [Fact]
    public void AggregatesEveryThirtySamples()
    {
        var h = new MetricsHistory();
        for (var i = 0; i < 60; i++) h.Add(Sample(i, cpuApp: i < 30 ? 10 : 50, slow: 1, worst: i == 40 ? 80 : 5));
        Assert.Equal(2, h.LongTerm.Count);
        Assert.Equal(10, h.LongTerm[0].CpuAppPct, 1);
        Assert.Equal(50, h.LongTerm[1].CpuAppPct, 1);
        Assert.Equal(30, h.LongTerm[0].SlowFrames);   // summed
        Assert.Equal(80, h.LongTerm[1].WorstFrameMs); // max
    }

    [Fact]
    public void AverageSkipsUnknownReadings()
    {
        var h = new MetricsHistory();
        h.Add(Sample(0) with { GpuBusyPct = -1 });
        h.Add(Sample(1) with { GpuBusyPct = 40 });
        Assert.Equal(40, h.AvgRecent(60, s => s.GpuBusyPct));
        Assert.Equal(-1, h.AvgRecent(60, s => s.VramUsedMB)); // never sampled
    }

    [Fact]
    public void TailReturnsOldestFirstWindow()
    {
        var h = new MetricsHistory();
        for (var i = 0; i < 10; i++) h.Add(Sample(i, cpuApp: i));
        var tail = h.Tail(3, s => s.CpuAppPct);
        Assert.Equal(new double[] { 7, 8, 9 }, tail);
    }
}

public class HealthAdvisorTests
{
    private static MetricsHistory History(Func<int, MetricSample> make, int seconds = 90)
    {
        var h = new MetricsHistory();
        for (var i = 0; i < seconds; i++) h.Add(make(i));
        return h;
    }

    private static MetricSample Healthy(int i) => new()
    {
        Utc = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc).AddSeconds(i),
        CpuAppPct = 8,
        CpuSystemPct = 20,
        RamAppMB = 600,
        RamSystemPct = 45,
        RamTotalMB = 32768,
        VramUsedMB = 800,
        VramTotalMB = 8192,
        OutputFps = 60,
        DiskFreeGB = 120,
        Threads = 40,
        Handles = 900,
    };

    private static readonly AdvisorContext Calm = new();

    private static IReadOnlyList<string> Ids(MetricsHistory h, AdvisorContext ctx)
        => HealthAdvisor.Advise(h, ctx).Select(s => s.Id).ToList();

    [Fact]
    public void HealthySystemGetsAllClearOnly()
    {
        var ids = Ids(History(Healthy), Calm);
        Assert.Equal(new[] { "all-clear" }, ids);
    }

    [Fact]
    public void SustainedSystemCpuWarns()
    {
        var h = History(i => Healthy(i) with { CpuSystemPct = 95 });
        var advice = HealthAdvisor.Advise(h, Calm);
        Assert.Equal("cpu-high", advice[0].Id);
        Assert.Equal(HealthSeverity.Warning, advice[0].Severity);
    }

    [Fact]
    public void HeavyAppCpuAdvisesWhenMachineIsOtherwiseFine()
    {
        var h = History(i => Healthy(i) with { CpuAppPct = 70, CpuSystemPct = 75 });
        Assert.Contains("cpu-app-high", Ids(h, Calm));
    }

    [Fact]
    public void MemoryPressureAndLowDiskWarn()
    {
        var h = History(i => Healthy(i) with { RamSystemPct = 95, DiskFreeGB = 1.2 });
        var ids = Ids(h, Calm);
        Assert.Contains("ram-high", ids);
        Assert.Contains("disk-low", ids);
    }

    [Fact]
    public void SteadyMemoryGrowthFlagsALeak()
    {
        // 600+ samples so ≥20 aggregates exist; app RAM climbs 1 MB per second.
        var h = History(i => Healthy(i) with { RamAppMB = 500 + i }, seconds: 660);
        Assert.Contains("ram-leak", Ids(h, Calm));
    }

    [Fact]
    public void HandleGrowthFlagsALeak()
    {
        var h = History(i => Healthy(i) with { Handles = 1000 + i * 10 }, seconds: 660);
        Assert.Contains("handles-growth", Ids(h, Calm));
    }

    [Fact]
    public void LowFpsAdvisesOnlyWhenLiveWithContinuousContent()
    {
        var h = History(i => Healthy(i) with { OutputFps = 38 });
        Assert.DoesNotContain("fps-low", Ids(h, Calm));
        Assert.Contains("fps-low", Ids(h, new AdvisorContext { OutputsLive = true, ContentContinuous = true }));
    }

    [Fact]
    public void SlowFrameSpikesGetAnInfoWhenRateIsFine()
    {
        var h = History(i => Healthy(i) with { SlowFrames = 1 });
        Assert.Contains("frame-spikes", Ids(h, new AdvisorContext { OutputsLive = true, ContentContinuous = true }));
    }

    [Fact]
    public void BatteryVramAndIntegratedGpuRulesFire()
    {
        var h = History(i => Healthy(i) with { OnBattery = true, BatteryPct = 34, VramUsedMB = 7600 });
        var ids = Ids(h, new AdvisorContext
        {
            DiscreteGpuPresent = true,
            UsingDiscreteGpu = false,
            BestGpuName = "NVIDIA GeForce RTX 3060",
        });
        Assert.Contains("battery", ids);
        Assert.Contains("vram-high", ids);
        Assert.Contains("igpu", ids);
    }

    [Fact]
    public void WatchdogFaultsAndRestartsSurface()
    {
        var h = History(i => Healthy(i) with { Faults = 6 });
        var advice = HealthAdvisor.Advise(h, new AdvisorContext { WatchdogEnabled = false, WatchdogRestarts = 2 });
        var ids = advice.Select(s => s.Id).ToList();
        Assert.Contains("watchdog-off", ids);
        Assert.Contains("restarts", ids);
        var faults = advice.Single(s => s.Id == "faults");
        Assert.Equal(HealthSeverity.Advice, faults.Severity); // ≥5 escalates from Info
    }

    [Fact]
    public void WarningsSortAheadOfInfo()
    {
        var h = History(i => Healthy(i) with { OnBattery = true });
        var advice = HealthAdvisor.Advise(h, new AdvisorContext { WatchdogEnabled = false });
        Assert.Equal(HealthSeverity.Warning, advice[0].Severity);
        Assert.True(advice[^1].Severity <= advice[0].Severity);
    }
}

public class SparklineAndCsvTests
{
    [Fact]
    public void PointsSpanTheBoxOldestLeft()
    {
        var pts = SparklinePath.Points(new double[] { 0, 50, 100 }, 200, 40);
        Assert.Equal(3, pts.Count);
        Assert.Equal(0, pts[0].X);
        Assert.Equal(200, pts[2].X);
        Assert.Equal(40, pts[0].Y, 1);          // zero sits on the baseline
        Assert.True(pts[2].Y < pts[1].Y);       // higher value = higher on the chart
    }

    [Fact]
    public void FixedMaxClampsSpikes()
    {
        var pts = SparklinePath.Points(new double[] { 50, 400 }, 100, 40, fixedMax: 100);
        Assert.Equal(0, pts[1].Y); // clamped to the top, not off the chart
    }

    [Fact]
    public void SingleValueDrawsAFlatLineAndEmptyDrawsNothing()
    {
        Assert.Equal(2, SparklinePath.Points(new double[] { 30 }, 100, 40).Count);
        Assert.Empty(SparklinePath.Points(Array.Empty<double>(), 100, 40));
    }

    [Fact]
    public void DownsampleAveragesBuckets()
    {
        var values = Enumerable.Range(0, 100).Select(i => (double)i).ToList();
        var down = SparklinePath.Downsample(values, 10);
        Assert.Equal(10, down.Count);
        Assert.Equal(4.5, down[0], 1);
        Assert.Equal(94.5, down[9], 1);
        Assert.Same(values, SparklinePath.Downsample(values, 200)); // short series pass through
    }

    [Fact]
    public void CsvLineMatchesHeaderShape()
    {
        var line = MetricsCsv.Line(new MetricSample
        {
            Utc = new DateTime(2026, 8, 30, 17, 30, 0, DateTimeKind.Utc),
            CpuAppPct = 12.34,
            CpuSystemPct = 45.6,
            RamAppMB = 512,
            RamSystemPct = 61,
            OutputFps = 59.94,
            WorstFrameMs = 8.25,
            SlowFrames = 2,
            Threads = 41,
            Handles = 903,
            OnBattery = true,
            Faults = 1,
        });
        Assert.Equal(MetricsCsv.Header.Split(',').Length, line.Split(',').Length);
        Assert.StartsWith("2026-08-30T17:30:00Z,12.3,45.6,512,61,", line);
        Assert.Contains(",1,1", line); // onBattery flag and fault count close the line
        Assert.DoesNotContain(",-1", line); // unknowns serialize as empty, not -1
    }
}

public class RenderStatsTests
{
    [Fact]
    public void DrainNormalisesPerWindowAndTracksWorstFrame()
    {
        RenderStats.Reset();
        for (var i = 0; i < 60; i++) RenderStats.Record(SinkKind.Output, 0, 5);
        for (var i = 0; i < 60; i++) RenderStats.Record(SinkKind.Output, 1, i == 0 ? 40 : 5);
        for (var i = 0; i < 30; i++) RenderStats.Record(SinkKind.Preview, 0, 2);
        RenderStats.Record(SinkKind.Ndi, 0, 500); // ignored — paced sink

        var (previewFps, outputFps, windows, worst, slow) = RenderStats.Drain(1.0);
        Assert.Equal(30, previewFps, 1);
        Assert.Equal(2, windows);
        Assert.Equal(60, outputFps, 1); // 120 frames across 2 windows
        Assert.Equal(40, worst, 1);
        Assert.Equal(1, slow);

        var after = RenderStats.Drain(1.0);
        Assert.Equal(0, after.OutputFps); // drained
    }
}
