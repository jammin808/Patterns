using System.Globalization;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 84: the Eye's replay on the desk — the record read from the desk's own journal and metrics file,
/// the page relit at an instant from the wire and the page while the rail stays live, STATE's row saying
/// so, EYE AT answering the record without moving the page, the scrub bar and the steps, the menu's two
/// lines, the verb desk-only, an empty record refused, and NOW putting the picture of now back.
/// </summary>
public class EyeReplayAppTests
{
    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static string Csv(AppServices services) => Path.Combine(services.Store.BaseDirectory, "patterns.metrics.csv");

    /// <summary>The record is read on a worker (round 85): runs the desk until the read has landed on the page.</summary>
    private static void Read(AppServices services)
    {
        if (services.Eye.ReplayRead is { } read) TestApp.Pump(read.ContinueWith(_ => true, TaskScheduler.Default));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheReplayRelightsThePageFromTheRecordAndLeavesTheRailLive()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        try
        {
            var router = new CommandRouter(services);
            var t0 = new DateTime(DateTime.UtcNow.AddMinutes(-10).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);   // whole seconds: the stamp round-trips

            // The record: rows the desk wrote, samples the metrics service wrote.
            services.Journal.Record(new ShowLogEntry(t0, "Desk", "Take", "", "Done", "the wall took the preview", "OutputsLive", "Changed"));
            services.Journal.Record(new ShowLogEntry(t0.AddSeconds(10), "wire 10.0.0.5", "CueGo", "Keynote", "Failed", "the encoder refused"));
            services.Journal.Record(new ShowLogEntry(t0.AddSeconds(20), "Desk", "ApplyLook", "Walk-in", "Done", ""));
            File.WriteAllLines(Csv(services), new[]
            {
                MetricsCsv.Header,
                MetricsCsv.Line(new MetricSample { Utc = t0, P95FrameMs = 12, OutputFps = 60, WorstFrameMs = 15, CpuAppPct = 10, CpuSystemPct = 20, RamAppMB = 500, RamSystemPct = 40 }),
                MetricsCsv.Line(new MetricSample { Utc = t0.AddSeconds(30), P95FrameMs = 40, SlowFrames = 3, OutputFps = 58, WorstFrameMs = 70, CpuAppPct = 30, CpuSystemPct = 50, RamAppMB = 700, RamSystemPct = 60 }),
            });

            services.Eye.Refresh();
            Assert.True(services.Eye.Graph.Nodes.Count > 0);
            var liveLine = services.Eye.RailLine;
            var liveWord = services.Eye.RailWord;
            Assert.False(services.Eye.Replaying);
            Assert.Same(services.Eye.Graph, services.Eye.Shown);

            // EYE REPLAY <time>: the record opens there; the page's picture is the record's, the rail's is the desk's.
            var at30 = ReplayTime.Stamp(t0.AddSeconds(30));
            var reply = Send(router, "EYE REPLAY " + at30);
            Assert.StartsWith("OK Reading the record", reply);                                                        // the files are read on a worker
            Assert.True(services.Eye.Replaying);
            Read(services);
            Assert.False(services.Eye.Reading);
            Assert.Contains("rows in the 30 s before", vm.EyeReplayWords);
            Assert.Equal(t0.AddSeconds(30), services.Eye.ReplayAtUtc);
            Assert.NotSame(services.Eye.Graph, services.Eye.Shown);
            Assert.Equal(services.Eye.Graph.Nodes.Select(n => n.Id), services.Eye.Shown.Nodes.Select(n => n.Id));     // the structure of now
            var desk = services.Eye.Shown.Find(EyeGraph.DeskId)!;
            Assert.Equal(CheckLight.Red, desk.Light);                                                                  // the cue that failed in the window
            Assert.StartsWith("p95 frame 40 ms · 3 slow frames · ApplyLook Walk-in Done", desk.Sub);                  // the sample at 30 s, the last row
            Assert.Contains(desk.Words, w => w.Contains("CueGo Keynote — Failed: the encoder refused", StringComparison.Ordinal));
            Assert.Contains(desk.Words, w => w.Contains("ApplyLook Walk-in — Done", StringComparison.Ordinal));
            Assert.DoesNotContain(desk.Words, w => w.Contains("Take — Done", StringComparison.Ordinal));                // at the window's edge, outside it
            Assert.All(services.Eye.Shown.Nodes.Where(n => n.Id != EyeGraph.DeskId), n => Assert.Equal(EyeReplay.NoRecord, n.Sub));

            // The rail stays on now; the page reads the record.
            Assert.Equal(liveLine, services.Eye.RailLine);
            Assert.Equal(liveWord, services.Eye.RailWord);
            Assert.Equal(services.Eye.Graph.Headline, vm.EyeLine);
            Assert.True(vm.EyeReplaying);
            Assert.Equal("NOW", vm.EyeReplayButton);
            Assert.Equal(services.Eye.Shown.Headline, vm.EyeHeadline);
            Assert.Contains("2 rows in the 30 s before", vm.EyeReplayWords);
            Assert.Contains("machine amber: p95 frame 40 ms · 3 slow frames", vm.EyeReplayWords);            // the sample's light; the desk node adds the failed cue and is red
            Assert.True(vm.HasEyeReplayRows);
            Assert.StartsWith(t0.AddSeconds(20).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " ApplyLook Walk-in — Done", vm.EyeReplayText);   // newest first
            Assert.Contains("→", vm.EyeReplaySpan, StringComparison.Ordinal);
            Assert.Equal(services.Eye.Record.Position(t0.AddSeconds(30)), vm.EyeReplayPosition, 6);

            // STATE's eye row carries replay and replayAt at the end of the row.
            var row = JsonUtil.SerializeCompact(services.Eye.Row());
            Assert.EndsWith($",\"replay\":true,\"replayAt\":\"{at30}\"}}", row);

            // EYE is the picture of now; EYE AT answers the record at an instant without moving the page.
            using (var live = JsonDocument.Parse(Send(router, "EYE")[3..]))
            {
                Assert.False(live.RootElement.GetProperty("replay").GetBoolean());
                Assert.Equal("", live.RootElement.GetProperty("replayAt").GetString());
            }
            var at15 = ReplayTime.Stamp(t0.AddSeconds(15));
            var answer = Send(router, "EYE AT " + at15);
            Assert.StartsWith("OK {", answer);
            using (var doc = JsonDocument.Parse(answer[3..]))
            {
                Assert.True(doc.RootElement.GetProperty("replay").GetBoolean());
                Assert.Equal(at15, doc.RootElement.GetProperty("replayAt").GetString());
                Assert.Contains("2 rows in the 30 s before", doc.RootElement.GetProperty("moment").GetString());        // Take at 0 and CueGo at 10, in (−15, 15]
                var deskNode = doc.RootElement.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == EyeGraph.DeskId);
                Assert.Equal("red", deskNode.GetProperty("light").GetString());
                Assert.StartsWith("steady · CueGo Keynote Failed", deskNode.GetProperty("sub").GetString());            // the sample at 0 was steady
            }
            Assert.Equal(t0.AddSeconds(30), services.Eye.ReplayAtUtc);                                                  // the page did not move
            Assert.StartsWith("ERR 'yesterday' is not a time", Send(router, "EYE AT yesterday"));
            Assert.StartsWith("ERR 'someday' is not a time", Send(router, "EYE REPLAY someday"));
            Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("EYE AT").Kind);

            // The scrub bar and the steps move the instant inside the record.
            var first = services.Eye.Record.FirstUtc!.Value;
            var last = services.Eye.Record.LastUtc!.Value;
            vm.EyeReplayPosition = 0;
            Assert.Equal(first, services.Eye.ReplayAtUtc);
            Assert.Equal(0, vm.EyeReplayPosition, 6);
            vm.EyeReplayStepCommand.Execute("30");
            Assert.Equal(services.Eye.Record.Clamp(first.AddSeconds(30)), services.Eye.ReplayAtUtc);
            vm.EyeReplayStepCommand.Execute("-3600");
            Assert.Equal(first, services.Eye.ReplayAtUtc);                                                              // held at the first stamp
            vm.EyeReplayPosition = 1;
            Assert.Equal(last, services.Eye.ReplayAtUtc);

            // The eye verbs walk the replayed picture while it is open.
            Assert.True(services.Eye.Next().Ok);
            Assert.Equal(EyeGraph.DeskId, services.Eye.FocusId);                                                        // the one problem of the record
            Assert.Contains("Eye on", vm.EyeFocusWords);
            services.Eye.Reset();

            // The brief says the page is replaying and keeps the picture of now.
            var brief = services.Eye.BriefLines();
            Assert.StartsWith("The Eye page is replaying the record at " + ReplayTime.Stamp(last), brief[0]);
            Assert.Equal(services.Eye.Graph.Headline, brief[1]);

            // The page's menu offers the replay both ways, with the wire lines; the verb is the desk's alone.
            Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.EyeReplay));
            var menu = Send(router, "MENU PAGE Eye");
            Assert.Contains("\"eye.replay\"", menu);
            Assert.Contains("EYE REPLAY ON", menu);
            Assert.Contains("\"eye.live\"", menu);
            Assert.Contains("EYE REPLAY OFF", menu);

            // The tick rebuilding the structure keeps the lights of then on it: a device that joins now is in the replay, grey, with the reason.
            Assert.StartsWith("OK Replay ", Send(router, "EYE REPLAY " + at30));                                          // the record is read: at once
            Assert.Equal(CheckLight.Red, services.Eye.Shown.Find(EyeGraph.DeskId)!.Light);
            var device = DeviceProfiles.Preset(DeviceProfile.Companion, 1);
            device.Name = "Replay Deck";
            vm.State.Interactive.Devices.Add(device);
            vm.State.Interactive.Enabled = true;
            Assert.True(services.Eye.Refresh());
            Assert.True(services.Eye.Replaying);
            var joined = services.Eye.Shown.Nodes.Single(n => n.Kind == EyeKind.Device && n.Label == "Replay Deck");
            Assert.Equal(CheckLight.Grey, joined.Light);
            Assert.Equal(EyeReplay.NoRecord, joined.Sub);
            Assert.Equal(CheckLight.Red, services.Eye.Shown.Find(EyeGraph.DeskId)!.Light);
            Assert.Equal(t0.AddSeconds(30), services.Eye.ReplayAtUtc);

            // NOW: the picture of now.
            vm.EyeReplayCommand.Execute(null);
            Assert.False(services.Eye.Replaying);
            Assert.Same(services.Eye.Graph, services.Eye.Shown);
            Assert.Equal("The picture of now.", vm.StatusMessage);
            Assert.False(vm.EyeReplaying);
            Assert.Equal("REPLAY", vm.EyeReplayButton);
            Assert.Equal("", vm.EyeReplayWords);
            Assert.Equal("OK The picture of now — the replay was not open.", Send(router, "EYE REPLAY OFF"));
            Assert.EndsWith(",\"replay\":false,\"replayAt\":\"\"}", JsonUtil.SerializeCompact(services.Eye.Row()));
            Assert.DoesNotContain("replaying", services.Eye.BriefLines()[0], StringComparison.Ordinal);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AnEmptyRecordIsRefusedAndOnOpensAtTheRecordsLastStamp()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        try
        {
            var router = new CommandRouter(services);
            if (File.Exists(services.Journal.Path)) File.Delete(services.Journal.Path);
            if (File.Exists(Csv(services))) File.Delete(Csv(services));
            Assert.Equal("ERR Nothing to replay — the journal and the metrics file hold no rows yet.", Send(router, "EYE REPLAY ON"));
            Assert.False(services.Eye.Replaying);

            // One sample alone is a record (the refusal above is a journal row now, too); ON opens at the record's last stamp.
            var t = new DateTime(2026, 9, 24, 19, 0, 0, DateTimeKind.Utc);
            File.WriteAllLines(Csv(services), new[] { MetricsCsv.Header, MetricsCsv.Line(new MetricSample { Utc = t, P95FrameMs = 9, OutputFps = 60, WorstFrameMs = 12, RenderFaults = 1 }) });
            Assert.StartsWith("OK Reading the record", Send(router, "EYE REPLAY ON"));
            Assert.True(services.Eye.Replaying);
            Read(services);
            Assert.False(services.Eye.Reading);
            Assert.Equal(services.Eye.Record.LastUtc, services.Eye.ReplayAtUtc);
            Assert.Contains(services.Eye.Record.Samples, s => s.Utc == t);

            // At the sample's own instant the desk wears it: a render fault with no sample before it is red. The record is read, so the time lands at once.
            Assert.StartsWith("OK Replay " + t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), Send(router, "EYE REPLAY " + ReplayTime.Stamp(t)));
            Assert.Equal(t, services.Eye.ReplayAtUtc);
            Assert.Equal(CheckLight.Red, services.Eye.Shown.Find(EyeGraph.DeskId)!.Light);
            Assert.Equal("1 render fault", services.Eye.Shown.Find(EyeGraph.DeskId)!.Sub);
            vm.EyeReplayCommand.Execute(null);
            Assert.False(services.Eye.Replaying);
        }
        finally
        {
            b.Dispose();
        }
    }
}
