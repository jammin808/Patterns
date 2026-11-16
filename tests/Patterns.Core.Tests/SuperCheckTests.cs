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
        RemoteToken = true,        // a strong rig is paired: its remotes present the show's token (round 65)
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
        Assert.Equal(CheckLight.Amber, report.Rows.Single(r => r.Item == "Contained errors").Light);
        Assert.Equal(CheckLight.Amber, report.Rows.Single(r => r.Item == "Output devices").Light);
        Assert.Equal(CheckLight.Amber, report.Rows.Single(r => r.Item == "Playback").Light);
        Assert.Contains("[RED]", SuperCheck.ToText(report));
    }

    [Fact]
    public void TheDeskTickRowNamesTheAreaThatBlocksTheDesk()
    {
        // Unknown: no row at all.
        Assert.DoesNotContain(SuperCheck.Run(Strong()).Rows, r => r.Item == "Desk tick");

        static CheckRow Row(double worst, string area, double avg = 1.2, int slow = 0, int faults = 0)
            => SuperCheck.Run(new CheckFacts { DeskTickAverageMs = avg, DeskTickWorstMs = worst, DeskTickWorstArea = area, DeskSlowTicks = slow, DeskTickFaults = faults })
                .Rows.Single(r => r.Item == "Desk tick");

        var fine = Row(4, "tallies");
        Assert.Equal(CheckLight.Green, fine.Light);
        Assert.Equal("SHOW", fine.Section);
        Assert.Equal("1.2 ms · worst 4.0 ms (tallies)", fine.Value);
        Assert.Equal("", fine.Note);

        var skipped = Row(30, "install", slow: 3);
        Assert.Equal(CheckLight.Amber, skipped.Light);
        Assert.Equal("1.2 ms · worst 30.0 ms (install) · 3 past 16 ms", skipped.Value);
        Assert.Contains("skipped a frame", skipped.Note);

        var blocked = Row(140, "remote", avg: 9, slow: 12);
        Assert.Equal(CheckLight.Red, blocked.Light);
        Assert.Contains("stutters", blocked.Note);
        Assert.Contains("(remote)", blocked.Value);
        Assert.DoesNotContain("(remote)", blocked.Note);   // the worst tick's area is a reading: it rides in the value the Eye does not hash

        // An area that threw turns a green row amber and says so; the light past that is the timing's.
        var failed = Row(4, "audio", faults: 2);
        Assert.Equal(CheckLight.Amber, failed.Light);
        Assert.Equal("an area failed and the tick carried on — see the log", failed.Note);
        Assert.EndsWith(" · 2 areas failed", failed.Value);
        var failedAndBlocked = Row(140, "remote", faults: 1);
        Assert.Equal(CheckLight.Red, failedAndBlocked.Light);
        Assert.Contains("stutters", failedAndBlocked.Note);
        Assert.Contains("1 area failed", failedAndBlocked.Value);
        Assert.Contains("Desk tick", SuperCheck.ToText(SuperCheck.Run(new CheckFacts { DeskTickWorstMs = 4 })));
    }

    /// <summary>The Super Check's rows that ask for attention, as the Eye's desk hashes them (AppServices.AttentionRows, EyeFacts.AttentionWords).</summary>
    private static IReadOnlyList<string> AttentionWords(CheckFacts facts)
        => SuperCheck.Run(facts).Rows.Where(r => r.Light is CheckLight.Amber or CheckLight.Red).Select(EyeFacts.AttentionWords).ToList();

    private static MetricsHistory Minute(Func<int, MetricSample> make)
    {
        var h = new MetricsHistory();
        for (var i = 0; i < 90; i++) h.Add(make(i));
        return h;
    }

    /// <summary>Ten minutes of samples: the long-term aggregates' twenty, the first half at one reading and the second at another.</summary>
    private static MetricsHistory TenMinutes(Func<int, MetricSample> make)
    {
        var h = new MetricsHistory();
        for (var i = 0; i < 20 * MetricsHistory.AggregateEvery; i++) h.Add(make(i));
        return h;
    }

    private static MetricSample Calm(int i) => new()
    {
        Utc = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc).AddSeconds(i),
        CpuAppPct = 8, CpuSystemPct = 20, RamAppMB = 600, RamSystemPct = 45, RamTotalMB = 32768,
        VramUsedMB = 800, VramTotalMB = 8192, OutputFps = 60, DiskFreeGB = 120, Threads = 40, Handles = 900,
    };

    [Fact]
    public void AReadingMovingWithinOneLightMovesNoAttentionWordAndARowAppearingDoes()
    {
        var live = new AdvisorContext { OutputsLive = true, ContentContinuous = true, TargetFps = 60 };
        CheckFacts Advised(Func<int, MetricSample> make, AdvisorContext? ctx = null)
            => new() { Advice = HealthAdvisor.Advise(Minute(make), ctx ?? new AdvisorContext()) };
        CheckFacts Grown(Func<int, MetricSample> make) => new() { Advice = HealthAdvisor.Advise(TenMinutes(make), new AdvisorContext()) };
        const int Half = 10 * MetricsHistory.AggregateEvery;

        // Each leg: the same suggestion at two readings of one light. The words the Eye hashes stand; the value a person reads moves.
        var legs = new (string Name, CheckFacts A, CheckFacts B)[]
        {
            ("faults 5 then 6", Advised(i => Calm(i) with { Faults = 5 }), Advised(i => Calm(i) with { Faults = 6 })),
            ("Patterns' CPU 70 then 71", Advised(i => Calm(i) with { CpuAppPct = 70 }), Advised(i => Calm(i) with { CpuAppPct = 71 })),
            ("the computer's CPU 90 then 95", Advised(i => Calm(i) with { CpuSystemPct = 90 }), Advised(i => Calm(i) with { CpuSystemPct = 95 })),
            ("memory 92 then 95", Advised(i => Calm(i) with { RamSystemPct = 92 }), Advised(i => Calm(i) with { RamSystemPct = 95 })),
            ("graphics memory 87 then 90", Advised(i => Calm(i) with { VramUsedMB = 0.87 * 8192 }), Advised(i => Calm(i) with { VramUsedMB = 0.90 * 8192 })),
            ("fps 40 then 41", Advised(i => Calm(i) with { OutputFps = 40, OutputWindows = 1 }, live), Advised(i => Calm(i) with { OutputFps = 41, OutputWindows = 1 }, live)),
            ("disk 1.5 then 1.2 GB", Advised(i => Calm(i) with { DiskFreeGB = 1.5 }), Advised(i => Calm(i) with { DiskFreeGB = 1.2 })),
            ("battery 40 then 35", Advised(i => Calm(i) with { OnBattery = true, BatteryPct = 40 }), Advised(i => Calm(i) with { OnBattery = true, BatteryPct = 35 })),
            ("the desk tick's failed areas 1 then 2", new CheckFacts { DeskTickWorstMs = 4, DeskTickFaults = 1 }, new CheckFacts { DeskTickWorstMs = 4, DeskTickFaults = 2 }),
            ("the stuttering tick's area remote then audio", new CheckFacts { DeskTickWorstMs = 140, DeskTickWorstArea = "remote" }, new CheckFacts { DeskTickWorstMs = 140, DeskTickWorstArea = "audio" }),
            ("Patterns' memory grew 450 then 500 MB", Grown(i => Calm(i) with { RamAppMB = i < Half ? 600 : 1050 }), Grown(i => Calm(i) with { RamAppMB = i < Half ? 600 : 1100 })),
            ("handles 900 → 3000 then 900 → 3200", Grown(i => Calm(i) with { Handles = i < Half ? 900 : 3000 }), Grown(i => Calm(i) with { Handles = i < Half ? 900 : 3200 })),
            // A rename guard: the SHOW row's count already rode in its value; the leg holds the row's new name to the same rule.
            ("contained errors 1 then 2", new CheckFacts { Faults = 1 }, new CheckFacts { Faults = 2 }),
        };
        foreach (var (name, a, b) in legs)
        {
            var (wordsA, wordsB) = (AttentionWords(a), AttentionWords(b));
            Assert.True(wordsA.Count > 0, $"{name}: the leg asks for attention");
            Assert.True(wordsA.SequenceEqual(wordsB), $"{name}: the Eye's words moved — {string.Join(" | ", wordsA)} → {string.Join(" | ", wordsB)}");
            var (valuesA, valuesB) = (SuperCheck.Run(a).Rows.Select(r => r.Value).ToList(), SuperCheck.Run(b).Rows.Select(r => r.Value).ToList());
            Assert.False(valuesA.SequenceEqual(valuesB), $"{name}: the reading a person reads is in the value, and it moved");
        }

        // The faults suggestion's reading reaches every reader a person reads: the row's value and the suggestion said whole.
        Assert.Contains(Grown(i => Calm(i) with { RamAppMB = i < Half ? 600 : 1050 }).Advice, a => a.Id == "ram-leak" && a.Reading == "grew 450 MB in 10 min");
        Assert.Contains(Grown(i => Calm(i) with { Handles = i < Half ? 900 : 3200 }).Advice, a => a.Id == "handles-growth" && a.Reading == "900 → 3200");

        var five = Assert.Single(HealthAdvisor.Advise(Minute(i => Calm(i) with { Faults = 5 }), new AdvisorContext()), s => s.Id == "faults");
        Assert.Equal("Errors caught and contained: 5 this session", five.Said);
        var row = Assert.Single(SuperCheck.Run(new CheckFacts { Advice = new[] { five } }).Rows, r => r.Section == "ADVICE");
        Assert.Equal(("Errors caught and contained", CheckLight.Amber), (row.Item, row.Light));
        Assert.StartsWith("5 this session — Every error the log recorded", row.Value, StringComparison.Ordinal);

        // A row appearing, going or changing its light is a fact, and it still moves the words: faults 4 (advice, green) then 5 (amber);
        // a green tick that then skips a frame; no contained error, then one.
        Assert.False(AttentionWords(Advised(i => Calm(i) with { Faults = 4 })).SequenceEqual(AttentionWords(Advised(i => Calm(i) with { Faults = 5 }))));
        Assert.False(AttentionWords(new CheckFacts { DeskTickWorstMs = 4 }).SequenceEqual(AttentionWords(new CheckFacts { DeskTickWorstMs = 30 })));
        Assert.False(AttentionWords(new CheckFacts { DeskTickWorstMs = 30 }).SequenceEqual(AttentionWords(new CheckFacts { DeskTickWorstMs = 140 })));
        Assert.False(AttentionWords(new CheckFacts { Faults = 0 }).SequenceEqual(AttentionWords(new CheckFacts { Faults = 1 })));
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
