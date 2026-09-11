using System.Diagnostics;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Patterns.App.Tests;

/// <summary>
/// The desk's one tick a second: measured, guarded area by area, raising only what changed, and
/// read on the Machine page. The benchmark prints its numbers so a slower tick is seen in the log
/// before it is felt at the desk.
/// </summary>
public class DeskPollTests
{
    private readonly ITestOutputHelper _out;

    public DeskPollTests(ITestOutputHelper output) => _out = output;

    /// <summary>A show of a size a corporate desk carries: looks, stingers, a stack of cues, break music, a playlist.</summary>
    internal static void FillShow(TestApp.Booted b)
    {
        var vm = b.Vm;
        var state = vm.State;
        var kinds = new[] { PatternKind.Grid, PatternKind.ColorBars, PatternKind.Checkerboard, PatternKind.Ramp, PatternKind.Focus, PatternKind.Geometry };
        for (var i = 0; i < 12; i++)
        {
            vm.ActivePattern.Kind = kinds[i % kinds.Length];
            vm.NewLookName = $"Look {i + 1}";
            vm.SaveLookCommand.Execute(null);
        }
        for (var i = 0; i < 6; i++)
        {
            state.Stingers.Items.Add(new StingerItemConfig { Name = $"Sting {i + 1}", Path = $"C:\\show\\sting{i + 1}.wav", Kind = i % 2 == 0 ? StingerKind.Vog : StingerKind.Sting });
        }
        for (var i = 0; i < 4; i++)
        {
            state.Spotify.Items.Add(new SpotifyItemConfig { Name = $"Bed {i + 1}", Uri = $"spotify:playlist:{i + 1:D22}" });
        }
        var stack = CueStacks.Caller(state);
        for (var i = 0; i < 40; i++)
        {
            var cue = new RunCueConfig { Number = $"{i / 10 + 1:00}.{i % 10 * 10 + 10:000}", Name = $"Cue {i + 1}", PlannedSeconds = 120 };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ApplyLook, Target = state.LooksAndCues.Looks[i % 12].Id });
            stack.Cues.Add(cue);
        }
        for (var i = 0; i < 20; i++)
        {
            vm.ActivePattern.Media.Playlist.Sections[0].Items.Add(new PlaylistItemConfig { Path = $"C:\\show\\item{i + 1}.png" });
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void AFailingAreaIsCarriedPastCountedAndToldOnceAMinute()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var faultsBefore = HealthMonitor.Faults;
            // Deltas, not totals. The desk's own one-second timer is running from the moment it is
            // built, so on a slow machine a tick or two lands during the boot and before this test
            // has said anything — which would fail it for being slow rather than for being wrong.
            var ticksBefore = b.Services.DeskTick.Ticks;
            var tickFaultsBefore = b.Services.DeskTick.Faults;
            var seen = new List<string>();
            vm.PollAreaProbe = area =>
            {
                seen.Add(area);
                if (area == "audio") throw new InvalidOperationException("the sound card went away");
            };
            vm.PollNow();
            vm.PollNow();
            vm.PollNow();

            // Every area after the broken one still ran, three times over.
            Assert.Equal(3, seen.Count(a => a == "audio"));
            Assert.Equal(3, seen.Count(a => a == "cues"));
            Assert.Equal(3, seen.Count(a => a == "clock"));
            Assert.Equal(3, b.Services.DeskTick.Ticks - ticksBefore);
            Assert.StartsWith("Up ", vm.HealthText);                       // the health area, after audio, ran

            // Three failures, one story: counted every time, told to the health line once a minute.
            Assert.Equal(3, b.Services.DeskTick.Faults - tickFaultsBefore);
            Assert.Equal(1, HealthMonitor.Faults - faultsBefore);
            Assert.Contains("desk poll · audio: the sound card went away", vm.HealthText);
            Assert.Contains("3 areas failed and the tick carried on", vm.DeskTickText);

            // Mended: nothing more is counted.
            vm.PollAreaProbe = null;
            vm.PollNow();
            Assert.Equal(3, b.Services.DeskTick.Faults - tickFaultsBefore);
            Assert.Equal(1, HealthMonitor.Faults - faultsBefore);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheRemoteAddressesAreKeptForHalfAMinuteAndFollowThePort()
    {
        var b = TestApp.Boot();
        try
        {
            var control = b.Services.Control;
            var first = control.RemoteUrls();
            Assert.StartsWith("http://localhost:", first[0]);
            Assert.Same(first, control.RemoteUrls());                     // the second's read asks nothing of the resolver
            control.ForgetRemoteUrls();
            Assert.NotSame(first, control.RemoteUrls());
            b.Vm.State.Control.HttpPort = 9123;
            var moved = control.RemoteUrls();
            Assert.NotSame(first, moved);
            Assert.Equal("http://localhost:9123/", moved[0]);
            Assert.Same(moved, control.RemoteUrls());
            Assert.Equal(TimeSpan.FromSeconds(30), ControlService.RemoteUrlsKeptFor);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheDeskTickIsReadOnTheMachinePageTheSuperCheckAndTheDashboard()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            Assert.Equal("", vm.DeskTickText);
            for (var i = 0; i < 6; i++) vm.PollNow();
            var budget = b.Services.DeskTick;
            Assert.Equal(6, budget.Ticks);
            Assert.True(budget.WorstMs >= 0 && budget.AverageMs >= 0);
            Assert.NotEqual("", budget.WorstArea);
            Assert.StartsWith("Desk tick ", vm.DeskTickText);
            Assert.Contains("in the last minute", vm.DeskTickText);

            var facts = b.Services.Metrics.GatherFacts();
            Assert.Equal(budget.WorstMs, facts.DeskTickWorstMs);
            Assert.Equal(budget.WorstArea, facts.DeskTickWorstArea);
            Assert.Contains("desk tick worst", HealthDashboard.Tiles(facts).Single(t => t.Id == "render").Detail);

            vm.RunSuperCheck();
            Dispatcher.UIThread.RunJobs();
            var row = vm.SuperCheckRows.Single(r => r.Item == "Desk tick");
            Assert.Contains("worst", row.Value);
            Assert.Contains($"({budget.WorstArea})", row.Value);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AQuietSecondRaisesOnlyTheClock()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            FillShow(b);
            vm.PollNow();                                                  // the first tick settles every "seen" value
            var raised = new List<string>();
            var runRaised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            vm.Run.PropertyChanged += (_, e) => runRaised.Add(e.PropertyName ?? "");
            vm.PollNow();
            vm.PollNow();
            vm.PollNow();
            Assert.Equal(3, raised.Count(n => n == "HeaderClock"));
            Assert.Equal(0, raised.Count(n => n == "CanvasInfo"));
            Assert.Equal(0, raised.Count(n => n == "CountdownPreview"));
            Assert.Equal(0, raised.Count(n => n == "DirectOutputSummary"));
            Assert.Equal(0, runRaised.Count(n => n == "IsDucked"));
            Assert.Equal(0, runRaised.Count(n => n == "RunningText"));
            Assert.Equal(0, runRaised.Count(n => n == "ScheduleSummary"));

            // What moves is raised: a cue runs and its clock reads on the strip; the edit target moves and the canvas words follow.
            b.Services.CueStack.SetArmed(true, ActionOrigin.Desk);
            Assert.True(vm.Run.Go(ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            runRaised.Clear();
            // Under load the status timer can fire inside RunJobs and see the fresh text first; the
            // elapsed second is moved on by hand so this tick has a change to raise whatever ran before.
            b.Services.CueStack.Runtime.LastGoUtc = DateTime.UtcNow.AddSeconds(-1.5);
            vm.PollNow();
            Assert.Contains("RunningText", runRaised);
            Assert.StartsWith("01.010  Cue 1", vm.Run.RunningText);
            raised.Clear();
            vm.ActivePattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("CanvasInfo", raised);
            Assert.StartsWith("Wall canvas", vm.CanvasInfo);
        }
        finally
        {
            b.Dispose();
        }
    }

    private double Measure(MainViewModel vm, string label)
    {
        for (var i = 0; i < 20; i++) vm.PollNow(); // warm: the JIT, the caches, the first reads
        const int ticks = 200;
        var samples = new double[ticks];
        var sw = new Stopwatch();
        for (var i = 0; i < ticks; i++)
        {
            sw.Restart();
            vm.PollNow();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        var avg = samples.Average();
        var p50 = samples[ticks / 2];
        var p95 = samples[(int)(ticks * 0.95)];
        var max = samples[^1];
        _out.WriteLine($"desk tick over {ticks} ({label}): avg {avg:0.00} ms · p50 {p50:0.00} ms · p95 {p95:0.00} ms · max {max:0.00} ms · {vm.Services.DeskTick.Describe()}");
        return avg;
    }

    [AvaloniaFact]
    public void TheTickStaysInsideItsBudgetOnACorporateShow()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            FillShow(b);
            vm.SelectPage(Shell.PanelPage);
            Dispatcher.UIThread.RunJobs();

            var quiet = Measure(vm, "remote off");
            vm.State.Control.Enabled = true;
            vm.State.Install.AdminPasscode = "4321"; // the Install page's ADMIN address reads the remote's addresses too
            Dispatcher.UIThread.RunJobs();
            var remote = Measure(vm, "remote on");

            // A fence against a catastrophic regression (a probe on the UI thread), not a speed contest.
            Assert.True(quiet < 100, $"the desk's tick averages {quiet:0.0} ms");
            Assert.True(remote < 100, $"the desk's tick with the remote on averages {remote:0.0} ms");
        }
        finally
        {
            b.Dispose();
        }
    }
}
