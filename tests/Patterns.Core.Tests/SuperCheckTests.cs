using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The super-check's rules: facts to lights, the level from the hardware, the text of the report.</summary>
public class SuperCheckTests
{
    private static readonly GpuAdapterInfo Rtx = new("NVIDIA GeForce RTX 4070", GpuAdapterInfo.VendorNvidia, 1, 12 * 1024, 7, false);
    private static readonly GpuAdapterInfo Iris = new("Intel Iris Xe", GpuAdapterInfo.VendorIntel, 2, 128, 8, false);
    private static readonly GpuAdapterInfo Warp = new("Microsoft Basic Render Driver", GpuAdapterInfo.VendorMicrosoft, 3, 0, 9, true);

    private static CheckFacts Strong() => new()
    {
        AppVersion = "Patterns 1.0.0.0",
        Os = "Windows 11",
        Machine = "SHOW-PC",
        CpuName = "Intel Core i9",
        CpuThreads = 16,
        RamTotalMB = 32 * 1024,
        RamUsedPct = 40,
        DiskFreeGB = 120,
        UptimeSeconds = 3900,
        CpuSystemPct = 20,
        CpuAppPct = 8,
        Gpus = new[] { Rtx, Iris },
        ActiveGpu = Rtx.Name,
        UsingBestGpu = true,
        VramUsedMB = 1200,
        VramTotalMB = 12000,
        GpuBusyPct = 30,
        Displays = new[]
        {
            new CheckDisplay("Screen 1", 1920, 1080, 1, true, false, false, 60),
            new CheckDisplay("Screen 2", 1920, 1080, 1, false, true, false, 60),
        },
        OutputsLive = true,
        OutputWindows = 1,
        OutputFps = 59.6,
        TargetFps = 60,
        WorstFrameMs = 18,
        SlowFrames = 0,
        Faults = 0,
        WatchdogEnabled = true,
        NdiRuntime = true,
        NdiSendersConfigured = 1,
        NdiSendersActive = 1,
        NdiSenderLines = new[] { "Patterns · 1920×1080 · sending" },
        StreamDestinations = 1,
        AudioOutputDevices = 2,
        RemoteEnabled = true,
        RemoteUrl = "http://10.0.0.5:9696/",
        VideoPlayback = true,
        Advice = new[] { new HealthSuggestion("all-clear", HealthSeverity.Info, "All clear", "Nothing needs attention.") },
    };

    [Fact]
    public void AStrongMachineReadsAllClearAndABigShow()
    {
        var report = SuperCheck.Run(Strong(), new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc));
        Assert.Equal(CheckLight.Green, report.Overall);
        Assert.StartsWith("All clear — Big show", report.Headline);
        Assert.Equal("Big show", report.Level.Name);
        Assert.True(report.Level.Score >= 8, $"score {report.Level.Score}");
        Assert.Empty(report.Level.Reasons);
        Assert.DoesNotContain(report.Rows, r => r.Light is CheckLight.Red or CheckLight.Amber);

        // Every section is there, the renderer card is named, the display rows say the mode.
        foreach (var section in new[] { "MACHINE", "GRAPHICS", "DISPLAYS", "SHOW", "NDI", "STREAM", "AUDIO", "REMOTE", "VIDEO", "ADVICE", "LEVEL" })
        {
            Assert.Contains(report.Rows, r => r.Section == section);
        }
        var card = report.Rows.Single(r => r.Item == "Best card");
        Assert.Equal(CheckLight.Green, card.Light);
        Assert.Contains("renderer", card.Value);
        Assert.Contains(report.Rows, r => r.Section == "DISPLAYS" && r.Value.Contains("1920×1080 @ 60 Hz") && r.Value.Contains("output on"));
        Assert.Equal("59.6 of 60 fps", report.Rows.Single(r => r.Item == "Frame rate").Value);
        Assert.Contains(report.Rows, r => r.Item == "Stream" && r.Light == CheckLight.Grey && r.Value.Contains("off"));
        Assert.Contains(report.Rows, r => r.Item == "Expected show" && r.Value == "Big show");

        var text = SuperCheck.ToText(report);
        Assert.Contains("PATTERNS SUPER-CHECK", text);
        Assert.Contains("[GREEN] All clear — Big show", text);
        Assert.Contains("MACHINE", text);
        Assert.Contains("[GREEN] Frame rate: 59.6 of 60 fps", text);
        Assert.Contains("running 1 h 5 min", text);
        foreach (var row in report.Rows) Assert.Contains(row.Item, text);
    }

    [Fact]
    public void ALaptopOnBatteryWithAnIdleCardReadsAmberAndASmallerShow()
    {
        var facts = new CheckFacts
        {
            CpuThreads = 8,
            RamTotalMB = 16 * 1024,
            RamUsedPct = 50,
            DiskFreeGB = 40,
            OnBattery = true,
            BatteryPct = 63,
            Gpus = new[] { Iris, Rtx },
            ActiveGpu = Iris.Name,
            UsingBestGpu = false,
            Displays = new[] { new CheckDisplay("Laptop", 1920, 1200, 1.25, true, true, false, 60) },
            OutputsLive = false,
            WatchdogEnabled = false,
            AudioOutputDevices = 1,
            VideoPlayback = true,
        };
        var report = SuperCheck.Run(facts);
        Assert.Equal(CheckLight.Red, report.Overall); // the battery is red: Windows throttles the card
        Assert.StartsWith("Attention needed — ", report.Headline);
        Assert.Contains("running on battery", report.Level.Reasons);
        Assert.Contains(report.Level.Reasons, r => r.Contains("not the renderer"));
        Assert.Equal("Full show", report.Level.Name); // 2 + 2 + 3 − 1 − 1 = 5
        Assert.Equal(CheckLight.Red, report.Rows.Single(r => r.Item == "Power").Light);
        Assert.Equal(CheckLight.Amber, report.Rows.Single(r => r.Item == "Best card").Light);
        Assert.Equal(CheckLight.Amber, report.Rows.Single(r => r.Item == "Watchdog").Light);
        Assert.Contains(report.Rows, r => r.Item == "Outputs" && r.Light == CheckLight.Grey);
        Assert.Contains(report.Rows, r => r.Item == "Laptop" && r.Value.Contains("125% scaling"));

        // Plugged in with the card driving the show, the same laptop is a big show.
        var plugged = SuperCheck.Grade(new CheckFacts { CpuThreads = 8, RamTotalMB = 16 * 1024, Gpus = new[] { Iris, Rtx }, UsingBestGpu = true });
        Assert.Equal("Big show", plugged.Name);
        Assert.Equal(8, plugged.Score);
    }

    [Fact]
    public void TheRedRowsNameWhatWouldStopAShow()
    {
        var facts = new CheckFacts
        {
            CpuThreads = 2,
            RamTotalMB = 4 * 1024,
            RamUsedPct = 90,
            DiskFreeGB = 0.8,
            Gpus = new[] { Warp },
            ActiveGpu = Warp.Name,
            Displays = Array.Empty<CheckDisplay>(),
            OutputsLive = true,
            OutputWindows = 2,
            OutputFps = 20,
            TargetFps = 60,
            WorstFrameMs = 140,
            SlowFrames = 30,
            Faults = 3,
            NdiRuntime = false,
            NdiSendersConfigured = 2,
            StreamActive = true,
            StreamDestinations = 1,
            StreamStatus = "Failed: connection refused",
            AudioOutputDevices = 0,
            VideoPlayback = false,
            Advice = new[] { new HealthSuggestion("cpu-high", HealthSeverity.Warning, "The computer's CPU is at 95%", "Close other programs.") },
        };
        var report = SuperCheck.Run(facts);
        Assert.Equal(CheckLight.Red, report.Overall);
        Assert.Equal("Rehearsal", report.Level.Name);
        Assert.Contains("few CPU threads", report.Level.Reasons);
        Assert.Contains("under 8 GB of memory", report.Level.Reasons);
        Assert.Contains(report.Level.Reasons, r => r.Contains("software rendering"));
        string[] red = { "CPU", "Memory", "Disk free", "Best card", "Frame rate", "Sends", "Stream", "The computer's CPU is at 95%" };
        foreach (var item in red)
        {
            Assert.Equal(CheckLight.Red, report.Rows.Single(r => r.Item == item).Light);
        }
        Assert.Equal(CheckLight.Amber, report.Rows.Single(r => r.Item == "Connected").Light);
        Assert.Equal(CheckLight.Amber, report.Rows.Single(r => r.Item == "Worst frame").Light);
        Assert.Equal(CheckLight.Amber, report.Rows.Single(r => r.Item == "Render faults").Light);
        Assert.Equal(CheckLight.Amber, report.Rows.Single(r => r.Item == "Output devices").Light);
        Assert.Equal(CheckLight.Amber, report.Rows.Single(r => r.Item == "Playback").Light);
        Assert.Contains("[RED]", SuperCheck.ToText(report));
    }

    [Fact]
    public void UnknownFactsStayGreyAndNeverDecideTheLight()
    {
        var report = SuperCheck.Run(new CheckFacts());
        Assert.Contains(report.Rows, r => r.Item == "Memory" && r.Light == CheckLight.Grey && r.Value == "unknown");
        Assert.Contains(report.Rows, r => r.Item == "Graphics card" && r.Light == CheckLight.Grey);
        Assert.Contains(report.Rows, r => r.Item == "Output devices" && r.Light == CheckLight.Grey);
        Assert.Contains(report.Rows, r => r.Item == "Sends" && r.Light == CheckLight.Grey);
        // Nothing attached and no video are the only things it can say for sure.
        Assert.Equal(CheckLight.Amber, report.Overall);
        Assert.Equal(CheckLight.Green, SuperCheck.Overall(new[] { new CheckRow("A", "b", CheckLight.Grey, "") }));
        Assert.Equal(CheckLight.Red, SuperCheck.Overall(new[] { new CheckRow("A", "b", CheckLight.Amber, ""), new CheckRow("A", "c", CheckLight.Red, "") }));

        // A display slower than the show's rate is called out; a planned one waits for adoption.
        var slow = SuperCheck.Run(new CheckFacts
        {
            TargetFps = 60,
            Displays = new[] { new CheckDisplay("Projector", 1920, 1080, 1, false, true, false, 30), new CheckDisplay("Wall", 3840, 1080, 1, false, true, true) },
        });
        var projector = slow.Rows.Single(r => r.Item == "Projector");
        Assert.Equal(CheckLight.Amber, projector.Light);
        Assert.Contains("30 Hz but the show asks for 60", projector.Note);
        Assert.Contains(slow.Rows, r => r.Item == "Wall" && r.Light == CheckLight.Amber && r.Value.Contains("planned"));
    }
}
