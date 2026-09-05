using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The beacon both ways, what a listener makes of it, the advisor's new eyes, the stand-down note, the super-check rows.</summary>
public class BeaconTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 7, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ABeaconRoundTripsAndAStrayPacketIsNotOne()
    {
        var b = new Beacon
        {
            Machine = "MAIN", Instance = "i1", Seq = 3, Utc = Now, Up = 120.5, Live = true, Blackout = false, Program = "Walk-in",
            Armed = true, Standby = "01.020 Welcome", Last = "01.010", Health = "Up 2m 00s · no faults", Faults = 0, Restarts = 1,
            Fps = 59.9, Windows = 2, Stream = true, Show = "Day 1",
        };
        var json = b.ToJson();
        Assert.Contains("\"patterns\":1", json);
        Assert.Contains("\"machine\":\"MAIN\"", json);
        Assert.DoesNotContain("summary", json, StringComparison.OrdinalIgnoreCase);
        var back = Beacon.Parse(json);
        Assert.Equal(b, back);
        Assert.Equal(b, Beacon.Parse(b.ToBytes()));
        Assert.Equal("live · Walk-in · armed · standby 01.020 Welcome · streaming", b.Summary);
        Assert.Equal("outputs off", new Beacon { Machine = "X" }.Summary);
        Assert.Equal("BLACKOUT · Holding", new Beacon { Machine = "X", Live = true, Blackout = true, Program = "Holding" }.Summary);
        Assert.Equal("standby 02.010 Lunch", new Beacon { Machine = "X", Live = false, Standby = "02.010 Lunch" }.Summary.Replace("outputs off · ", ""));
        Assert.Equal("watchdog gave-up", new Beacon { Machine = "X", Event = "gave-up" }.Summary);

        Assert.Null(Beacon.Parse("{\"foo\":1}"));
        Assert.Null(Beacon.Parse("{\"patterns\":1}"));
        Assert.Null(Beacon.Parse("GET / HTTP/1.1"));
        Assert.Null(Beacon.Parse(""));
        Assert.Null(Beacon.Parse(new byte[] { 0xFF, 0xFE, 0x00 }));
        // A newer field is ignored; a missing one reads its default.
        var newer = Beacon.Parse("{\"patterns\":2,\"machine\":\"NEW\",\"future\":true,\"live\":true}");
        Assert.NotNull(newer);
        Assert.True(newer!.Live);
        Assert.Equal("", newer.Standby);
    }

    [Fact]
    public void TheWatchSaysWaitingSeenSilentOrDown()
    {
        Assert.Equal(BeaconLevel.Waiting, BeaconWatch.Level(null, null, Now));
        Assert.StartsWith("Listening for the main machine", BeaconWatch.Describe(null, null, Now));

        var b = new Beacon { Machine = "MAIN", Live = true, Program = "Walk-in" };
        Assert.Equal(BeaconLevel.Ok, BeaconWatch.Level(b, Now.AddSeconds(-1), Now));
        Assert.Equal("Main machine MAIN seen just now: live · Walk-in.", BeaconWatch.Describe(b, Now.AddSeconds(-1), Now));
        Assert.Equal("Main machine MAIN seen 3 s ago: live · Walk-in.", BeaconWatch.Describe(b, Now.AddSeconds(-3), Now));
        Assert.False(BeaconWatch.IsSilent(Now.AddSeconds(-4), Now));
        Assert.True(BeaconWatch.IsSilent(Now.AddSeconds(-6), Now));
        Assert.Equal(BeaconLevel.Warning, BeaconWatch.Level(b, Now.AddSeconds(-6), Now));
        var silent = BeaconWatch.Describe(b, Now.AddSeconds(-6), Now);
        Assert.StartsWith("MAIN MACHINE MAIN SILENT for 6 s", silent);
        Assert.EndsWith("Take over?", silent);

        var down = new Beacon { Machine = "MAIN", Event = "gave-up" };
        Assert.Equal(BeaconLevel.Warning, BeaconWatch.Level(down, Now, Now));
        var text = BeaconWatch.Describe(down, Now, Now);
        Assert.Contains("MAIN MACHINE MAIN: its watchdog gave up", text);
        Assert.EndsWith("Take over?", text);
    }

    private static MetricSample Sample(int i, double fps) => new()
    {
        Utc = Now.AddSeconds(i),
        CpuAppPct = 8, CpuSystemPct = 20, RamAppMB = 600, RamSystemPct = 45, RamTotalMB = 32768,
        VramUsedMB = 800, VramTotalMB = 8192, OutputFps = fps, DiskFreeGB = 120, Threads = 40, Handles = 900,
    };

    private static MetricsHistory History(int seconds, double fps)
    {
        var h = new MetricsHistory();
        for (var i = 0; i < seconds; i++) h.Add(Sample(i, fps));
        return h;
    }

    private static IReadOnlyList<HealthSuggestion> Advise(MetricsHistory h, AdvisorContext ctx) => HealthAdvisor.Advise(h, ctx);

    [Fact]
    public void TheAdvisorSeesFrozenOutputsAndAStoppedStream()
    {
        var moving = new AdvisorContext { OutputsLive = true, ContentContinuous = true };
        var frozen = Advise(History(40, 0), moving);
        var row = Assert.Single(frozen, s => s.Id == "outputs-frozen");
        Assert.Equal(HealthSeverity.Warning, row.Severity);
        Assert.Contains("RESTART APP", row.Detail);
        Assert.DoesNotContain(frozen, s => s.Id == "fps-low");

        Assert.DoesNotContain(Advise(History(10, 0), moving), s => s.Id == "outputs-frozen");   // too soon after OUTPUTS ON to say
        Assert.DoesNotContain(Advise(History(40, 60), moving), s => s.Id == "outputs-frozen");
        Assert.DoesNotContain(Advise(History(40, 0), new AdvisorContext { OutputsLive = true, ContentContinuous = false }), s => s.Id == "outputs-frozen"); // a still draws nothing, rightly
        Assert.Contains(Advise(History(90, 30), moving), s => s.Id == "fps-low");

        var stopped = Advise(History(90, 60), new AdvisorContext { StreamError = "the encoder reported an error" });
        var stream = Assert.Single(stopped, s => s.Id == "stream-stopped");
        Assert.Equal(HealthSeverity.Warning, stream.Severity);
        Assert.Contains("the encoder reported an error", stream.Title);
        Assert.DoesNotContain(Advise(History(90, 60), new AdvisorContext()), s => s.Id == "stream-stopped");
    }

    [Fact]
    public void TheStandDownNoteIsReadOnceAndReachesTheHealthLine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-beacon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal("", WatchdogMarker.ReadAndClear(dir));
            WatchdogMarker.Write(dir, "The watchdog gave up at 14:02 after 7 restarts in a short window — see patterns.watchdog.log");
            Assert.True(File.Exists(Path.Combine(dir, WatchdogMarker.FileName)));
            var note = WatchdogMarker.ReadAndClear(dir);
            Assert.StartsWith("The watchdog gave up at 14:02", note);
            Assert.False(File.Exists(Path.Combine(dir, WatchdogMarker.FileName)));
            Assert.Equal("", WatchdogMarker.ReadAndClear(dir));

            HealthMonitor.WatchdogNote = note;
            try
            {
                Assert.Contains("The watchdog gave up at 14:02", HealthMonitor.Summary(DateTime.UtcNow));
            }
            finally
            {
                HealthMonitor.WatchdogNote = "";
            }
            Assert.DoesNotContain("gave up", HealthMonitor.Summary(DateTime.UtcNow));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void TheSuperCheckShowsTheBeaconAndTheMainMachine()
    {
        var quiet = SuperCheck.Run(new CheckFacts());
        Assert.DoesNotContain(quiet.Rows, r => r.Item is "Beacon" or "Main machine");

        var sending = SuperCheck.Run(new CheckFacts { BeaconSending = true });
        Assert.Contains(sending.Rows, r => r.Item == "Beacon" && r.Light == CheckLight.Green);

        var waiting = SuperCheck.Run(new CheckFacts { BeaconListening = true, BeaconWatch = "Listening for the main machine — nothing heard yet." });
        Assert.Contains(waiting.Rows, r => r.Item == "Main machine" && r.Light == CheckLight.Amber && r.Value == "not heard yet");
        var alive = SuperCheck.Run(new CheckFacts { BeaconListening = true, BeaconWatch = "Main machine MAIN seen just now: live · Walk-in." });
        Assert.Contains(alive.Rows, r => r.Item == "Main machine" && r.Light == CheckLight.Green && r.Value == "alive");
        var silent = SuperCheck.Run(new CheckFacts { BeaconListening = true, BeaconWatch = "MAIN MACHINE MAIN SILENT for 9 s — last seen live. Take over?" });
        var red = Assert.Single(silent.Rows, r => r.Item == "Main machine");
        Assert.Equal(CheckLight.Red, red.Light);
        Assert.Equal("silent", red.Value);
        Assert.Contains("Take over?", red.Note);
    }
}
