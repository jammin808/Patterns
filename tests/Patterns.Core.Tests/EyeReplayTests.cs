using System.Globalization;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 84: the Eye replayed — the record sorted and spanned, a moment's sample and window, the structure
/// relit from the rows alone with everything else grey, a canvas row lighting its members, the desk's
/// health from the sample in force, the metrics file read back by column name, and the time a replay
/// verb names.
/// </summary>
public class EyeReplayTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 19, 0, 0, DateTimeKind.Utc);

    private static DateTime S(double seconds) => T0.AddSeconds(seconds);

    private static EyeFacts Rig() => new()
    {
        MachineName = "SHOW-PC",
        OutputsLive = true,
        Health = CheckLight.Green,
        Displays = new[]
        {
            new EyeDisplay("d1", "\\\\.\\DISPLAY1", 1920, 1080, 60, true, false, false),
            new EyeDisplay("d2", "\\\\.\\DISPLAY2", 1920, 1080, 60, false, false, false),
        },
        Screens = new[]
        {
            new EyeScreen { Id = "s1", Number = "1", Label = "Main wall", DisplayId = "d1", OnAir = true, Contract = "1920x1080 60 RGB 8", Verdict = "MATCH" },
            new EyeScreen { Id = "s2", Number = "2", Label = "Projector 1", DisplayId = "d2", OnAir = true, Contract = "1920x1080 60 RGB 8", Verdict = "MATCH" },
            new EyeScreen { Id = "s3", Number = "3", Label = "Confidence", OnAir = false },
        },
        Devices = new[]
        {
            new EyeDevice("dev2", "Lights", "Lines", "10.0.0.30:9000", true, true, "answering", CheckLight.Green, ""),
        },
    };

    private static ShowLogEntry Row(double seconds, string kind, string target, string outcome, string message = "", string? visibility = null, string? effect = null)
        => new(S(seconds), "Desk", kind, target, outcome, message, visibility, effect);

    private static readonly ShowLogEntry[] Rows =
    {
        Row(27, "TileCut", "s1+s3", "Done"),
        Row(10, "TileTake", "s1", "Done", "Main wall took the preview", "OutputsLive", "Changed"),
        Row(-5, "Take", "", "Refused", "nothing would change"),
        Row(26, "DeviceReceipt", "dev2", "Failed", "no link"),
        Row(20, "ScreenPattern", "2", "Failed", "no such pattern"),
        Row(25, "CueGo", "Keynote", "Done"),
    };

    private static readonly MetricSample[] Samples =
    {
        new() { Utc = S(30), P95FrameMs = 31, SlowFrames = 2, OutputFps = 59.8, WorstFrameMs = 44, CpuAppPct = 20, CpuSystemPct = 35, RamAppMB = 640, RamSystemPct = 42 },
        new() { Utc = S(0), P95FrameMs = 12.3, OutputFps = 60, WorstFrameMs = 18, CpuAppPct = 12.5, CpuSystemPct = 30, RamAppMB = 512, RamSystemPct = 40 },
    };

    private static ReplayRecord Record() => ReplayRecord.From(Rows, Samples);

    [Fact]
    public void Record_sorts_rows_and_samples_and_spans_them()
    {
        var r = Record();
        Assert.Equal(new[] { -5.0, 10, 20, 25, 26, 27 }, r.Rows.Select(x => (x.AtUtc - T0).TotalSeconds));
        Assert.Equal(new[] { 0.0, 30 }, r.Samples.Select(x => (x.Utc - T0).TotalSeconds));
        Assert.Equal(S(-5), r.FirstUtc);
        Assert.Equal(S(30), r.LastUtc);
        Assert.Equal(TimeSpan.FromSeconds(35), r.Span);
        Assert.False(r.IsEmpty);
        Assert.Equal(0, r.Position(S(-5)));
        Assert.Equal(1, r.Position(S(30)));
        Assert.Equal(1, r.Position(S(400)));                       // clamped
        Assert.Equal(0.5, r.Position(S(12.5)), 6);
        Assert.Equal(S(12.5), r.AtPosition(0.5, S(999)));
        Assert.Equal(S(-5), r.AtPosition(-3, S(999)));
        Assert.Equal(S(30), r.AtPosition(7, S(999)));
        Assert.Equal(S(30), r.Clamp(S(100)));
        Assert.Equal(S(-5), r.Clamp(S(-100)));
        Assert.Equal(S(3), r.Clamp(S(3)));

        var empty = ReplayRecord.Empty;
        Assert.True(empty.IsEmpty);
        Assert.Null(empty.FirstUtc);
        Assert.Equal(TimeSpan.Zero, empty.Span);
        Assert.Equal(1, empty.Position(T0));
        Assert.Equal(S(7), empty.AtPosition(0.3, S(7)));
        Assert.Equal(S(7), empty.Clamp(S(7)));
        Assert.Null(empty.LastRowAt(T0));
        Assert.Equal((null, null), empty.SamplesAt(T0));
    }

    [Fact]
    public void At_reads_the_sample_in_force_and_the_rows_of_the_window()
    {
        var r = Record();

        var m = EyeReplay.At(r, S(29));
        Assert.Equal(S(0), m.Sample!.Utc);                          // the last sample at or before the instant
        Assert.Null(m.Previous);
        Assert.Equal(TimeSpan.FromSeconds(29), m.SampleAge);
        Assert.Equal(new[] { 10.0, 20, 25, 26, 27 }, m.Recent.Select(x => (x.AtUtc - T0).TotalSeconds));   // (−1, 29]: the −5 row is outside
        Assert.Equal(S(27), m.Last!.AtUtc);

        m = EyeReplay.At(r, S(30));
        Assert.Equal(S(30), m.Sample!.Utc);
        Assert.Equal(S(0), m.Previous!.Utc);
        Assert.Equal(TimeSpan.Zero, m.SampleAge);

        m = EyeReplay.At(r, S(-10));
        Assert.Null(m.Sample);
        Assert.Null(m.SampleAge);
        Assert.Empty(m.Recent);
        Assert.Null(m.Last);

        m = EyeReplay.At(r, S(70));
        Assert.Empty(m.Recent);                                     // (40, 70] holds nothing
        Assert.Equal(S(27), m.Last!.AtUtc);                         // but the last row before the instant is still known
        Assert.Equal(TimeSpan.FromSeconds(40), m.SampleAge);

        Assert.Equal(new[] { 26.0, 27 }, r.RowsBetween(S(-100), S(100), 2).Select(x => (x.AtUtc - T0).TotalSeconds));   // the newest survive the bound
    }

    [Fact]
    public void Apply_lights_the_things_the_rows_name_and_greys_the_rest()
    {
        var structure = EyeGraph.Build(Rig());
        var replay = EyeReplay.Apply(structure, EyeReplay.At(Record(), S(30)));

        Assert.Equal(structure.Nodes.Select(n => n.Id), replay.Nodes.Select(n => n.Id));       // the same things in the same order
        Assert.Equal(structure.Edges.Count, replay.Edges.Count);
        Assert.All(replay.Edges, e => Assert.Equal(CheckLight.Grey, e.Light));                 // the record holds things, not links
        Assert.Equal(structure.Edges.Select(e => (e.From, e.To, e.Kind)), replay.Edges.Select(e => (e.From, e.To, e.Kind)));

        var s1 = replay.Find("screen:s1")!;
        Assert.Equal(CheckLight.Green, s1.Light);
        Assert.Equal("TileCut Done", s1.Sub);                                                  // the last row that named it
        Assert.Contains(s1.Words, w => w.EndsWith("TileTake s1 — Done: Main wall took the preview · OutputsLive · Changed", StringComparison.Ordinal));
        Assert.Contains(s1.Words, w => w.Contains("TileCut s1+s3 — Done", StringComparison.Ordinal));

        var s2 = replay.Find("screen:s2")!;
        Assert.Equal(CheckLight.Red, s2.Light);                                                // SCREEN 2 by its wire number
        Assert.Equal("ScreenPattern Failed · no such pattern", s2.Sub);

        var s3 = replay.Find("screen:s3")!;
        Assert.Equal(CheckLight.Green, s3.Light);                                              // a canvas row lights every member
        Assert.Equal("TileCut Done", s3.Sub);

        var dev = replay.Find("device:dev2")!;
        Assert.Equal(CheckLight.Red, dev.Light);
        Assert.Equal("DeviceReceipt Failed · no link", dev.Sub);

        var desk = replay.Find(EyeGraph.DeskId)!;
        Assert.Equal(CheckLight.Amber, desk.Light);                                            // the sample at 30 s: p95 31 ms and 2 slow frames
        Assert.StartsWith("p95 frame 31 ms · 2 slow frames · CueGo Keynote Done", desk.Sub, StringComparison.Ordinal);
        Assert.Contains(desk.Words, w => w.StartsWith("Sample ", StringComparison.Ordinal) && w.EndsWith("0 s before this moment", StringComparison.Ordinal));
        Assert.Contains(desk.Words, w => w.Contains("CueGo Keynote — Done", StringComparison.Ordinal));
        Assert.DoesNotContain(desk.Words, w => w.Contains("Refused", StringComparison.Ordinal));   // the −5 s row is outside the window

        var display = replay.Find("display:d1")!;
        Assert.Equal(CheckLight.Grey, display.Light);
        Assert.Equal(EyeReplay.NoRecord, display.Sub);
        Assert.Empty(display.Words);

        Assert.Equal("screen:s2", replay.Problems[0]);                                        // the picture's own queue, from the replayed lights
        Assert.Contains("device:dev2", replay.Problems);
        Assert.Equal(2, replay.Red);
        Assert.Contains("2 red", replay.Headline, StringComparison.Ordinal);

        Assert.Equal(CheckLight.Green, structure.Find("screen:s2")!.Light);                    // the live structure is untouched
        Assert.Equal("screen:s2", replay.Resolve("2"));                                       // the numbers ride along
    }

    [Fact]
    public void Apply_before_the_rows_shows_only_the_desk_and_what_it_refused()
    {
        var replay = EyeReplay.Apply(EyeGraph.Build(Rig()), EyeReplay.At(Record(), S(5)));
        var desk = replay.Find(EyeGraph.DeskId)!;
        Assert.Equal(CheckLight.Amber, desk.Light);                                            // steady sample, one Refused row on the desk
        Assert.Equal("steady · Take Refused", desk.Sub);
        Assert.Equal(CheckLight.Grey, replay.Find("screen:s1")!.Light);
        Assert.Equal(0, replay.Red);
        Assert.Equal(1, replay.Amber);
    }

    [Fact]
    public void Apply_on_an_empty_record_is_grey_with_the_reason()
    {
        var replay = EyeReplay.Apply(EyeGraph.Build(Rig()), EyeReplay.At(ReplayRecord.Empty, T0));
        Assert.All(replay.Nodes, n => Assert.Equal(CheckLight.Grey, n.Light));
        Assert.Equal("no sample at this time", replay.Find(EyeGraph.DeskId)!.Sub);
        Assert.Empty(replay.Problems);
    }

    [Fact]
    public void Health_reads_the_sample_in_force()
    {
        var none = EyeReplay.Health(null, null, null);
        Assert.Equal(CheckLight.Grey, none.Light);
        Assert.Equal("no sample at this time", none.Sub);

        var steady = new MetricSample { Utc = T0, P95FrameMs = 12, OutputFps = 60, WorstFrameMs = 17, CpuAppPct = 10, CpuSystemPct = 20, RamAppMB = 500, RamSystemPct = 40 };
        var h = EyeReplay.Health(steady, null, TimeSpan.FromSeconds(12));
        Assert.Equal(CheckLight.Green, h.Light);
        Assert.Equal("steady", h.Sub);
        Assert.StartsWith("Sample ", h.Words[0], StringComparison.Ordinal);
        Assert.EndsWith("12 s before this moment", h.Words[0], StringComparison.Ordinal);
        Assert.Contains(h.Words, w => w == "Output 60 fps · worst frame 17 ms · p95 12 ms");
        Assert.Contains(h.Words, w => w == "CPU 10% (machine 20%) · RAM 500 MB (machine 40%)");

        var stale = EyeReplay.Health(steady, null, TimeSpan.FromMinutes(2));
        Assert.Equal(CheckLight.Grey, stale.Light);
        Assert.Equal("the last sample is 2 min 0 s before this moment", stale.Sub);

        Assert.Equal(CheckLight.Amber, EyeReplay.Health(steady with { P95FrameMs = 30 }, null, TimeSpan.Zero).Light);
        Assert.Equal("p95 frame 30 ms", EyeReplay.Health(steady with { P95FrameMs = 30 }, null, TimeSpan.Zero).Sub);
        Assert.Equal(CheckLight.Red, EyeReplay.Health(steady with { P95FrameMs = 60 }, null, TimeSpan.Zero).Light);
        Assert.Equal(CheckLight.Amber, EyeReplay.Health(steady with { OnBattery = true }, null, TimeSpan.Zero).Light);
        Assert.Equal("on battery", EyeReplay.Health(steady with { OnBattery = true }, null, TimeSpan.Zero).Sub);
        Assert.Equal(CheckLight.Amber, EyeReplay.Health(steady with { SlowFrames = 1 }, null, TimeSpan.Zero).Light);
        Assert.Equal("1 slow frame", EyeReplay.Health(steady with { SlowFrames = 1 }, null, TimeSpan.Zero).Sub);

        var faulted = EyeReplay.Health(steady with { RenderFaults = 2 }, steady, TimeSpan.Zero);
        Assert.Equal(CheckLight.Red, faulted.Light);
        Assert.Equal("2 render faults", faulted.Sub);
        Assert.Equal(CheckLight.Red, EyeReplay.Health(steady with { MissedSlots = 1 }, null, TimeSpan.Zero).Light);

        // A counter that counts since the desk started reads by its rise since the sample before.
        var starvedBefore = steady with { PoolStarved = 5 };
        Assert.Equal(CheckLight.Green, EyeReplay.Health(starvedBefore, starvedBefore, TimeSpan.Zero).Light);
        var starvedMore = EyeReplay.Health(steady with { PoolStarved = 6 }, starvedBefore, TimeSpan.Zero);
        Assert.Equal(CheckLight.Red, starvedMore.Light);
        Assert.Equal("pool starved 1×", starvedMore.Sub);
        Assert.Contains(EyeReplay.Health(steady with { Faults = 3 }, null, TimeSpan.Zero).Words, w => w == "3 faults since the desk started");   // words, never a light: the count is the session's
        Assert.Equal(CheckLight.Green, EyeReplay.Health(steady with { Faults = 3 }, null, TimeSpan.Zero).Light);
    }

    [Fact]
    public void Words_and_lines_say_the_moment()
    {
        var r = Record();
        var words = EyeReplay.Words(EyeReplay.At(r, S(30)));
        Assert.Contains("5 rows in the 30 s before", words, StringComparison.Ordinal);
        Assert.Contains("machine amber: p95 frame 31 ms · 2 slow frames", words, StringComparison.Ordinal);
        Assert.StartsWith(S(30).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), words, StringComparison.Ordinal);

        words = EyeReplay.Words(EyeReplay.At(r, S(70)));
        Assert.Contains("no rows in the 30 s before", words, StringComparison.Ordinal);
        Assert.Contains("last row " + S(27).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " (43 s before): TileCut s1+s3 Done", words, StringComparison.Ordinal);

        Assert.Equal(S(20).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " ScreenPattern 2 — Failed: no such pattern", EyeReplay.Line(Rows[4]));
        Assert.Equal(S(-5).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " Take — Refused: nothing would change", EyeReplay.Line(Rows[2]));
    }

    [Fact]
    public void NodesFor_finds_a_screen_by_id_number_or_label_a_device_and_falls_back_to_the_desk()
    {
        var g = EyeGraph.Build(Rig());
        Assert.Equal(new[] { "screen:s1" }, EyeReplay.NodesFor(g, Row(0, "TileTake", "s1", "Done")));
        Assert.Equal(new[] { "screen:s2" }, EyeReplay.NodesFor(g, Row(0, "ScreenPattern", "2", "Done")));
        Assert.Equal(new[] { "screen:s2" }, EyeReplay.NodesFor(g, Row(0, "ScreenPattern", "projector 1", "Done")));
        Assert.Equal(new[] { "device:dev2" }, EyeReplay.NodesFor(g, Row(0, "DeviceReceipt", "dev2", "Done")));
        Assert.Equal(new[] { "screen:s1", "screen:s3" }, EyeReplay.NodesFor(g, Row(0, "TileCut", "s1+s3", "Done")));
        Assert.Equal(new[] { "desk" }, EyeReplay.NodesFor(g, Row(0, "TileCut", "s9+s8", "Done")));      // a canvas of screens the picture has not
        Assert.Equal(new[] { "desk" }, EyeReplay.NodesFor(g, Row(0, "CueGo", "Keynote", "Done")));
        Assert.Equal(new[] { "desk" }, EyeReplay.NodesFor(g, Row(0, "Take", "", "Done")));
        Assert.Equal(new[] { "desk" }, EyeReplay.NodesFor(g, Row(0, "Take", "desk", "Done")));
        Assert.Empty(EyeReplay.NodesFor(EyeGraph.Empty, Row(0, "Take", "", "Done")));
    }

    [Fact]
    public void Outcomes_light_by_their_word_and_an_unknown_one_is_grey()
    {
        Assert.Equal(CheckLight.Green, EyeReplay.LightOf("Done"));
        Assert.Equal(CheckLight.Green, EyeReplay.LightOf("Requested"));
        Assert.Equal(CheckLight.Amber, EyeReplay.LightOf("Refused"));
        Assert.Equal(CheckLight.Amber, EyeReplay.LightOf("DoneWithWarnings"));
        Assert.Equal(CheckLight.Red, EyeReplay.LightOf("Failed"));
        Assert.Equal(CheckLight.Red, EyeReplay.LightOf("FailedLate"));
        Assert.Equal(CheckLight.Grey, EyeReplay.LightOf("Perhaps"));
        Assert.Equal(CheckLight.Grey, EyeReplay.LightOf(""));
    }

    [Fact]
    public void MetricsCsv_reads_its_own_lines_back_and_tolerates_a_torn_or_older_line()
    {
        var s = new MetricSample
        {
            Utc = T0, CpuAppPct = 12.5, CpuSystemPct = 30, RamAppMB = 512, RamSystemPct = 40, VramUsedMB = 900, GpuBusyPct = 55, OutputFps = 59.9, WorstFrameMs = 18.2,
            SlowFrames = 3, Threads = 41, Handles = 812, OnBattery = true, Faults = 2, P95FrameMs = 12.3, MissedSlots = 1, SwitchWorstMs = 9.5, SlowSwitches = 0,
            GoWorstMs = 34, LagWorstMs = 20, RenderFaults = 4, PrivateMB = 700, ManagedMB = 210, LiveAgeWorstMs = 66.6, RetiringMB = 12, PoolStarved = 7,
        };
        var lines = new[]
        {
            MetricsCsv.Header,
            MetricsCsv.Line(s),
            "2026-09-24T19:00:30Z,1.5,2",                     // a torn line: what it has, the rest not measured
            "garbage,,,",                                     // no stamp: skipped
            "",
            "utc,cpuAppPct,p95FrameMs",                       // an older build's header
            "2026-09-24T19:01:00Z,7,33.5",
            "not-a-date,1,2",
        };
        var read = MetricsCsv.Parse(lines);
        Assert.Equal(3, read.Count);

        var a = read[0];
        Assert.Equal(T0, a.Utc);
        Assert.Equal(DateTimeKind.Utc, a.Utc.Kind);
        Assert.Equal(12.5, a.CpuAppPct);
        Assert.Equal(30, a.CpuSystemPct);
        Assert.Equal(512, a.RamAppMB);
        Assert.Equal(55, a.GpuBusyPct);
        Assert.Equal(59.9, a.OutputFps);
        Assert.Equal(18.2, a.WorstFrameMs);
        Assert.Equal(3, a.SlowFrames);
        Assert.Equal(41, a.Threads);
        Assert.True(a.OnBattery);
        Assert.Equal(2, a.Faults);
        Assert.Equal(12.3, a.P95FrameMs);
        Assert.Equal(1, a.MissedSlots);
        Assert.Equal(9.5, a.SwitchWorstMs);
        Assert.Equal(34, a.GoWorstMs);
        Assert.Equal(4, a.RenderFaults);
        Assert.Equal(700, a.PrivateMB);
        Assert.Equal(210, a.ManagedMB);
        Assert.Equal(66.6, a.LiveAgeWorstMs);
        Assert.Equal(12, a.RetiringMB);
        Assert.Equal(7, a.PoolStarved);

        var torn = read[1];
        Assert.Equal(S(30), torn.Utc);
        Assert.Equal(1.5, torn.CpuAppPct);
        Assert.Equal(2, torn.CpuSystemPct);
        Assert.Equal(-1, torn.RamAppMB);
        Assert.Equal(-1, torn.P95FrameMs);
        Assert.Equal(0, torn.SlowFrames);
        Assert.False(torn.OnBattery);

        var older = read[2];
        Assert.Equal(S(60), older.Utc);
        Assert.Equal(7, older.CpuAppPct);
        Assert.Equal(33.5, older.P95FrameMs);
        Assert.Equal(-1, older.CpuSystemPct);

        Assert.Empty(MetricsCsv.Parse(new[] { MetricsCsv.Line(s) }));                       // a line before any header is not trusted to a shape
        Assert.Empty(MetricsCsv.Parse(Array.Empty<string>()));
    }

    [Fact]
    public void ReplayTime_reads_a_clock_time_and_a_stamp_and_refuses_the_rest()
    {
        var nowUtc = T0;
        var nowLocal = nowUtc.ToLocalTime();

        Assert.True(ReplayTime.TryParse("20:14", nowUtc, out var at));
        Assert.Equal(DateTimeKind.Utc, at.Kind);
        Assert.Equal(new TimeSpan(20, 14, 0), at.ToLocalTime().TimeOfDay);
        Assert.True(at <= nowUtc.AddMinutes(1) && at > nowUtc.AddDays(-1).AddMinutes(-1));   // today, or yesterday when it lies ahead

        Assert.True(ReplayTime.TryParse(" 9:05:07 ", nowUtc, out at));
        Assert.Equal(new TimeSpan(9, 5, 7), at.ToLocalTime().TimeOfDay);

        var ahead = nowLocal.AddHours(2);
        if (ahead.Date == nowLocal.Date)
        {
            Assert.True(ReplayTime.TryParse(ahead.ToString("HH:mm", CultureInfo.InvariantCulture), nowUtc, out at));
            var expected = new DateTime(ahead.Year, ahead.Month, ahead.Day, ahead.Hour, ahead.Minute, 0, DateTimeKind.Local).AddDays(-1).ToUniversalTime();
            Assert.Equal(expected, at);                                                       // a time ahead of now is yesterday's
        }

        Assert.True(ReplayTime.TryParse("2026-09-24T20:14:03Z", nowUtc, out at));
        Assert.Equal(new DateTime(2026, 9, 24, 20, 14, 3, DateTimeKind.Utc), at);
        Assert.True(ReplayTime.TryParse("2026-09-24T20:14:03+02:00", nowUtc, out at));
        Assert.Equal(new DateTime(2026, 9, 24, 18, 14, 3, DateTimeKind.Utc), at);
        Assert.True(ReplayTime.TryParse("2026-09-24T20:14:03", nowUtc, out at));
        Assert.Equal(new DateTime(2026, 9, 24, 20, 14, 3, DateTimeKind.Local).ToUniversalTime(), at);      // no zone: the desk's
        Assert.True(ReplayTime.TryParse("2026-09-24 20:14", nowUtc, out at));
        Assert.Equal(new DateTime(2026, 9, 24, 20, 14, 0, DateTimeKind.Local).ToUniversalTime(), at);

        var stamp = ReplayTime.Stamp(new DateTime(2026, 9, 24, 20, 14, 3, DateTimeKind.Utc));
        Assert.Equal("2026-09-24T20:14:03Z", stamp);
        Assert.True(ReplayTime.TryParse(stamp, nowUtc, out at));
        Assert.Equal(new DateTime(2026, 9, 24, 20, 14, 3, DateTimeKind.Utc), at);

        foreach (var bad in new[] { "", "  ", "yesterday", "25:00", "20:14:03:99", "ON", "5 minutes ago", "24/09/2026", "2026-09-24T20:14:03 please" })
        {
            Assert.False(ReplayTime.TryParse(bad, nowUtc, out _));
        }
    }
}
