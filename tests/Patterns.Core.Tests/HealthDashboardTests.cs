using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The machine at a glance: twelve tiles with a light each from the super-check's facts and the
/// live sample (the sample wins where it has a reading), every tile's own reasons to go amber or
/// red, the worst light over the wall, and the verdict naming the tiles that set it and counting
/// the advice below.
/// </summary>
public class HealthDashboardTests
{
    private static CheckFacts Facts(bool live = true, double fps = 60, int ndiConfigured = 1, int ndiActive = 1, bool ndiRuntime = true,
        bool streamOn = false, string streamStatus = "", int audio = 2, bool sync = true, double lag = 0.5, bool watchdog = true,
        int restarts = 0, bool listening = false, string watch = "", double disk = 90, double ramPct = 48, bool remote = true,
        double cpu = 22, bool battery = false)
        => new()
        {
            CpuThreads = 12,
            CpuSystemPct = cpu,
            CpuAppPct = 9,
            RamTotalMB = 16 * 1024,
            RamUsedPct = ramPct,
            DiskFreeGB = disk,
            OnBattery = battery,
            BatteryPct = battery ? 63 : -1,
            OutputsLive = live,
            OutputWindows = 2,
            OutputFps = fps,
            TargetFps = 60,
            WorstFrameMs = 12,
            SlowFrames = 0,
            Faults = 0,
            WatchdogEnabled = watchdog,
            WatchdogRestarts = restarts,
            BeaconListening = listening,
            BeaconWatch = watch,
            NdiRuntime = ndiRuntime,
            NdiSendersConfigured = ndiConfigured,
            NdiSendersActive = ndiActive,
            StreamActive = streamOn,
            StreamDestinations = 1,
            StreamStatus = streamStatus,
            AudioOutputDevices = audio,
            SyncLock = sync,
            SyncWorstLagMs = lag,
            RemoteEnabled = remote,
            RemoteUrl = "http://10.0.0.5:8080/",
            UptimeSeconds = 7500,
        };

    private static DashboardTile Tile(IReadOnlyList<DashboardTile> tiles, string id) => tiles.Single(t => t.Id == id);

    private static DashboardTile Tile(CheckFacts facts, string id) => Tile(HealthDashboard.Tiles(facts), id);

    private static readonly HealthSuggestion AllClear = new("all-clear", HealthSeverity.Info, "All clear", "");

    [Fact]
    public void AHealthyMachineIsAllGreenAndTheVerdictSaysSo()
    {
        var tiles = HealthDashboard.Tiles(Facts());
        Assert.Equal(12, tiles.Count);
        Assert.Equal(12, tiles.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(tiles, t => Assert.True(t.Light is CheckLight.Green or CheckLight.Grey, t.Id));

        Assert.Equal("2 live", Tile(tiles, "outputs").Value);
        Assert.Equal("60 of 60 fps", Tile(tiles, "outputs").Detail);
        Assert.Equal(1, Tile(tiles, "outputs").Fraction, 3);
        Assert.Equal("12 ms", Tile(tiles, "render").Value);
        Assert.Equal("22%", Tile(tiles, "cpu").Value);
        Assert.Equal(0.22, Tile(tiles, "cpu").Fraction, 3);
        Assert.Contains("this app 9%", Tile(tiles, "cpu").Detail);
        Assert.Equal("48%", Tile(tiles, "memory").Value);
        Assert.Contains("16.0 GB", Tile(tiles, "memory").Detail);
        Assert.Equal(CheckLight.Grey, Tile(tiles, "gpu").Light);                    // no counters in the facts
        Assert.Equal("1 of 1", Tile(tiles, "ndi").Value);
        Assert.Equal(CheckLight.Green, Tile(tiles, "ndi").Light);
        Assert.Equal("off", Tile(tiles, "stream").Value);
        Assert.Equal(CheckLight.Grey, Tile(tiles, "stream").Light);
        Assert.Equal("2 devices", Tile(tiles, "audio").Value);
        Assert.Contains("0.5 ms", Tile(tiles, "audio").Detail);
        Assert.Equal("on", Tile(tiles, "remote").Value);
        Assert.Contains("10.0.0.5", Tile(tiles, "remote").Detail);
        Assert.Equal("on", Tile(tiles, "watchdog").Value);
        Assert.Equal("mains", Tile(tiles, "power").Value);
        Assert.Equal("90 GB", Tile(tiles, "disk").Value);
        Assert.False(Tile(tiles, "remote").HasBar);
        Assert.True(Tile(tiles, "cpu").HasBar);

        Assert.Equal(CheckLight.Green, HealthDashboard.Overall(tiles));
        var verdict = HealthDashboard.Verdict(tiles, new[] { AllClear });
        Assert.Equal(CheckLight.Green, verdict.Light);
        Assert.Equal("All clear", verdict.Headline);
        Assert.Contains("2 live", verdict.Detail);
        Assert.Contains("60 of 60 fps", verdict.Detail);

        Assert.Equal("up 2 h 05 min", HealthDashboard.Uptime(7500));
        Assert.Equal("up 12 min", HealthDashboard.Uptime(725));
        Assert.Equal("up 40 s", HealthDashboard.Uptime(40));
        Assert.Equal("", HealthDashboard.Uptime(-1));
    }

    [Fact]
    public void TheLiveSampleOverridesTheFactsAndTroubleLightsTheTiles()
    {
        var now = new MetricSample
        {
            CpuSystemPct = 96, CpuAppPct = 70, RamSystemPct = 91, RamTotalMB = 16 * 1024, RamAppMB = 2300,
            OutputFps = 40, OutputWindows = 2, WorstFrameMs = 80, SlowFrames = 5, OnBattery = true, BatteryPct = 41,
            DiskFreeGB = 1.5, Faults = 3,
        };
        var tiles = HealthDashboard.Tiles(Facts(), now);

        Assert.Equal(CheckLight.Red, Tile(tiles, "cpu").Light);
        Assert.Equal("96%", Tile(tiles, "cpu").Value);
        Assert.Contains("this app 70%", Tile(tiles, "cpu").Detail);
        Assert.Equal(CheckLight.Red, Tile(tiles, "memory").Light);
        Assert.Equal("91%", Tile(tiles, "memory").Value);
        Assert.Contains("this app 2.2 GB", Tile(tiles, "memory").Detail);
        Assert.Equal(CheckLight.Red, Tile(tiles, "outputs").Light);
        Assert.Equal("40 of 60 fps", Tile(tiles, "outputs").Detail);
        Assert.InRange(Tile(tiles, "outputs").Fraction, 0.66, 0.67);
        Assert.Equal(CheckLight.Amber, Tile(tiles, "render").Light);
        Assert.Equal("80 ms", Tile(tiles, "render").Value);
        Assert.Contains("5 slow", Tile(tiles, "render").Detail);
        Assert.Contains("3 faults", Tile(tiles, "render").Detail);
        Assert.Equal(CheckLight.Red, Tile(tiles, "power").Light);
        Assert.Equal("battery 41%", Tile(tiles, "power").Value);
        Assert.Equal(0.41, Tile(tiles, "power").Fraction, 3);
        Assert.Equal(CheckLight.Red, Tile(tiles, "disk").Light);
        Assert.Equal("1.5 GB", Tile(tiles, "disk").Value);
        Assert.Equal(CheckLight.Red, HealthDashboard.Overall(tiles));

        var advice = new[]
        {
            new HealthSuggestion("battery", HealthSeverity.Warning, "Running on battery (41%)", ""),
            new HealthSuggestion("cpu-high", HealthSeverity.Warning, "The computer's CPU is at 96%", ""),
            new HealthSuggestion("fps-low", HealthSeverity.Advice, "Outputs are averaging 40 fps", ""),
        };
        var verdict = HealthDashboard.Verdict(tiles, advice);
        Assert.Equal(CheckLight.Red, verdict.Light);
        Assert.StartsWith("Attention needed — ", verdict.Headline);
        Assert.Contains("CPU", verdict.Headline);
        Assert.Contains("POWER", verdict.Headline);
        Assert.Contains("OUTPUTS", verdict.Headline);
        Assert.Contains("2 warnings and 1 suggestion below", verdict.Detail);

        // A sample with no reading leaves the fact in charge.
        var quiet = HealthDashboard.Tiles(Facts(cpu: 30), new MetricSample());
        Assert.Equal("30%", Tile(quiet, "cpu").Value);
    }

    [Fact]
    public void EveryTileHasItsOwnReasonsToGoAmberOrRed()
    {
        var closed = Tile(Facts(live: false), "outputs");
        Assert.Equal(CheckLight.Grey, closed.Light);
        Assert.Equal("closed", closed.Value);
        Assert.Contains("closed", HealthDashboard.Verdict(HealthDashboard.Tiles(Facts(live: false)), new[] { AllClear }).Detail);
        Assert.Equal("idle", Tile(Facts(live: false), "render").Value);
        Assert.Equal(CheckLight.Amber, Tile(Facts(fps: 55), "outputs").Light);
        Assert.Equal(CheckLight.Red, Tile(Facts(fps: 40), "outputs").Light);

        Assert.Equal(CheckLight.Amber, Tile(Facts(ndiConfigured: 2, ndiActive: 0), "ndi").Light);
        Assert.Equal("0 of 2", Tile(Facts(ndiConfigured: 2, ndiActive: 0), "ndi").Value);
        Assert.Equal(CheckLight.Red, Tile(Facts(ndiConfigured: 2, ndiRuntime: false), "ndi").Light);
        Assert.Equal("no runtime", Tile(Facts(ndiConfigured: 2, ndiRuntime: false), "ndi").Value);
        Assert.Equal("none", Tile(Facts(ndiConfigured: 0), "ndi").Value);
        Assert.Equal(CheckLight.Grey, Tile(Facts(ndiConfigured: 0), "ndi").Light);

        Assert.Equal(CheckLight.Red, Tile(Facts(streamOn: true, streamStatus: "failed: connection refused"), "stream").Light);
        Assert.Equal("trouble", Tile(Facts(streamOn: true, streamStatus: "failed: connection refused"), "stream").Value);
        Assert.Equal(CheckLight.Green, Tile(Facts(streamOn: true, streamStatus: "streaming 2.1 Mbit/s"), "stream").Light);
        Assert.Equal("on", Tile(Facts(streamOn: true, streamStatus: "streaming 2.1 Mbit/s"), "stream").Value);

        Assert.Equal(CheckLight.Amber, Tile(Facts(audio: 0), "audio").Light);
        Assert.Equal("none", Tile(Facts(audio: 0), "audio").Value);
        Assert.Equal(CheckLight.Grey, Tile(Facts(audio: -1), "audio").Light);
        Assert.Equal(CheckLight.Amber, Tile(Facts(sync: false), "audio").Light);
        Assert.Contains("free-run", Tile(Facts(sync: false), "audio").Detail);
        Assert.Equal(CheckLight.Amber, Tile(Facts(lag: 8), "audio").Light);
        Assert.Equal("1 device", Tile(Facts(audio: 1), "audio").Value);

        Assert.Equal(CheckLight.Amber, Tile(Facts(watchdog: false), "watchdog").Light);
        Assert.Equal("off", Tile(Facts(watchdog: false), "watchdog").Value);
        Assert.Equal(CheckLight.Amber, Tile(Facts(restarts: 2), "watchdog").Light);
        Assert.Equal("2 restarts", Tile(Facts(restarts: 2), "watchdog").Value);
        Assert.Equal(CheckLight.Red, Tile(Facts(listening: true, watch: "MAIN MACHINE SILENT for 6 s — take over?"), "watchdog").Light);
        Assert.Equal("MAIN SILENT", Tile(Facts(listening: true, watch: "MAIN MACHINE SILENT for 6 s — take over?"), "watchdog").Value);
        Assert.Contains("listening", Tile(Facts(listening: true), "watchdog").Detail);

        Assert.Equal(CheckLight.Amber, Tile(Facts(disk: 5), "disk").Light);
        Assert.Equal("5.0 GB", Tile(Facts(disk: 5), "disk").Value);
        Assert.Equal(CheckLight.Grey, Tile(Facts(disk: -1), "disk").Light);
        Assert.Equal(CheckLight.Amber, Tile(Facts(ramPct: 80), "memory").Light);
        Assert.Equal(0.8, Tile(Facts(ramPct: 80), "memory").Fraction, 3);
        Assert.Equal(CheckLight.Amber, Tile(Facts(cpu: 70), "cpu").Light);
        Assert.Equal(CheckLight.Grey, Tile(Facts(cpu: -1), "cpu").Light);
        Assert.Equal(CheckLight.Grey, Tile(Facts(remote: false), "remote").Light);
        Assert.Equal("off", Tile(Facts(remote: false), "remote").Value);
        Assert.Equal(CheckLight.Red, Tile(Facts(battery: true), "power").Light);
        Assert.Equal("battery 63%", Tile(Facts(battery: true), "power").Value);

        // The verdict: amber from the tiles alone, amber from advice alone, and the worst wins.
        var cautions = HealthDashboard.Verdict(HealthDashboard.Tiles(Facts(disk: 5, ndiConfigured: 2, ndiActive: 1)), new[] { AllClear });
        Assert.Equal(CheckLight.Amber, cautions.Light);
        Assert.Equal("Ready, with cautions — NDI, DISK", cautions.Headline);
        Assert.Contains("amber tile", cautions.Detail);
        var advised = HealthDashboard.Verdict(HealthDashboard.Tiles(Facts()), new[] { new HealthSuggestion("igpu", HealthSeverity.Advice, "Rendering on the integrated GPU", "") });
        Assert.Equal(CheckLight.Amber, advised.Light);
        Assert.Equal("Ready, with cautions", advised.Headline);
        Assert.Equal("1 suggestion below.", advised.Detail);
        var leak = HealthDashboard.Verdict(HealthDashboard.Tiles(Facts()), new[] { new HealthSuggestion("ram-leak", HealthSeverity.Warning, "Patterns' memory grew 600 MB in 20 min", "") });
        Assert.Equal(CheckLight.Red, leak.Light);
        Assert.Equal("Attention needed", leak.Headline);
        Assert.Contains("1 warning below", leak.Detail);
    }
}
