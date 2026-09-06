using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The round-14 report: a stinger that "does not end but is flagged as ended so it cannot be
/// stopped", the stings and VOGs after it going wrong, and after a restart a video sting that
/// "tries to fade in and immediately fades back off". Here every way a clip could be left on the
/// screens with nothing owning it, and every way back.
/// </summary>
public class StingerLifecycleTests
{
    private static readonly DateTime T0 = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A decoder whose readings throw until told to behave — a player mid-dispose, a device gone.</summary>
    private sealed class FaultySource : IMountedSource
    {
        public int FaultsLeft;
        public bool Ended;
        public int Faults;

        private bool Fault()
        {
            if (FaultsLeft <= 0) return false;
            FaultsLeft--;
            Faults++;
            throw new InvalidOperationException("the decoder is not there");
        }

        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint) => false;
        public SKSizeI? FrameSize => null;
        public bool IsPlaying => !Fault() && !Ended;
        public bool IsEnded => !Fault() && Ended;
        public double DurationSeconds => 10;
        public string StatusText => "faulty";
        public void SetAudio(bool mute, double volumePct) { }
        public void BeginFadeOut(DateTime nowUtc, int ms) { }
        public void Pump(DateTime nowUtc) { }
        public void Dispose() { }
    }

    private static StingerItemConfig Clip(TestApp.Booted b, string name)
    {
        var item = new StingerItemConfig { Path = AudioFakes.TempFile(name + ".mp4"), Name = name };
        b.Vm.State.Stingers.Items.Add(item);
        return item;
    }

    private static void Orphan(AppServices services, StingerItemConfig item)
    {
        // What a fault leaves behind: the clip as the program's content, no session owning it.
        services.EditAir(air =>
        {
            air.Pattern.Kind = PatternKind.Media;
            air.Pattern.Media.Source = MediaSource.Video;
            air.Pattern.Media.VideoPath = item.Path;
        });
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ATickFaultKeepsTheSessionAndTheNextTickReadsAgain()
    {
        var b = TestApp.Boot();
        try
        {
            AudioFakes.Install(b);
            var faulty = new FaultySource { FaultsLeft = 2 };
            b.Services.Video.SourceFactory = _ => faulty;
            b.Vm.State.Pattern.Kind = PatternKind.Grid;
            var item = Clip(b, "Faulty sting");

            Assert.True(b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            Assert.True(b.Services.Stingers.ClipActive);
            Assert.Equal(item.Id, b.Services.Stingers.SessionId);

            // Two ticks that throw inside the probe: the session is kept, the fault counted, the tally lit.
            b.Services.Stingers.Poll();
            b.Services.Stingers.Poll();
            Assert.Equal(2, b.Services.Stingers.TickFaults);
            Assert.Equal(2, faulty.Faults);
            Assert.True(b.Services.Stingers.ClipActive);
            Assert.Equal(item.Id, b.Services.Stingers.SessionId);
            Assert.Equal("Faulty sting", b.Vm.State.Stingers.PlayingName);
            Assert.Equal(item.Path, b.Services.AirState.Pattern.Media.VideoPath);

            // The decoder behaves again and the clip ends: the show comes back the normal way.
            faulty.Ended = true;
            b.Services.Stingers.Poll();
            Assert.False(b.Services.Stingers.ClipActive);
            Assert.Equal(PatternKind.Grid, b.Services.AirState.Pattern.Kind);
            Assert.Equal("Clip finished — previous content back.", b.Services.Stingers.Status);
            Assert.Equal(2, b.Services.Stingers.TickFaults);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void StopPutsBackAClipThatNothingOwns()
    {
        var b = TestApp.Boot();
        try
        {
            var fakes = AudioFakes.Install(b);
            b.Vm.State.Pattern.Kind = PatternKind.Grid;
            var item = Clip(b, "Winner sting");

            // One clean session first: the service now knows the show it was fired over.
            Assert.True(b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            fakes.Sources[0].Ended = true;
            b.Services.Stingers.Poll();
            Assert.Equal(PatternKind.Grid, b.Services.AirState.Pattern.Kind);

            // Then the fault: the clip is on the screens and no session owns it.
            Orphan(b.Services, item);
            Assert.False(b.Services.Stingers.ClipActive);
            Assert.True(StingerLibrary.IsClipOnAir(b.Services.AirState));
            Assert.Equal("", b.Vm.State.Stingers.PlayingName);

            // STOP means stop: the show comes back, and the journal says why.
            var result = b.Services.Actions.Execute(ShowActionKind.StingerStop, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(result.Ok);
            Assert.Equal(PatternKind.Grid, b.Services.AirState.Pattern.Kind);
            Assert.Contains("'Winner sting' was on the screens with nothing owning it", b.Services.Stingers.Status);
            Assert.Contains(b.Services.Journal.Tail(4), e => e.Kind == "StingerStop" && e.Message.Contains("nothing owning it"));

            // STOP with nothing up at all is the quiet "Ready."
            b.Services.Actions.Execute(ShowActionKind.StingerStop, ActionOrigin.Desk);
            Assert.Equal("Ready.", b.Services.Stingers.Status);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void StopSaysSoWhenNoShowIsKnownAndUsesTheLookOnAirWhenThereIsOne()
    {
        var b = TestApp.Boot();
        try
        {
            AudioFakes.Install(b);
            var item = Clip(b, "Walk-in sting");
            Orphan(b.Services, item);

            // A fresh desk that never fired anything: nothing to put back — said, not guessed.
            b.Services.Actions.Execute(ShowActionKind.StingerStop, ActionOrigin.Desk);
            Assert.Contains("no previous content known", b.Services.Stingers.Status);
            Assert.Equal(item.Path, b.Services.AirState.Pattern.Media.VideoPath);

            // The look recorded as on air is the show: STOP goes back to it.
            var grid = new ShowState();
            grid.Pattern.Kind = PatternKind.Grid;
            var look = new LookConfig { Name = "Walk-in", Json = LookService.Capture(grid) };
            b.Vm.State.LooksAndCues.Looks.Add(look);
            b.Services.AirLookId = look.Id;
            b.Services.Actions.Execute(ShowActionKind.StingerStop, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Grid, b.Services.AirState.Pattern.Kind);
            Assert.Contains("previous content back", b.Services.Stingers.Status);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AClipFiredOverADeadClipComesBackToTheShowNotToTheClip()
    {
        var b = TestApp.Boot();
        try
        {
            var fakes = AudioFakes.Install(b);
            b.Vm.State.Pattern.Kind = PatternKind.Grid;
            var first = Clip(b, "Opening");
            var second = Clip(b, "Sponsor");

            Assert.True(b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, first.Id).Ok);
            fakes.Sources[0].Ended = true;
            b.Services.Stingers.Poll();
            Assert.Equal(PatternKind.Grid, b.Services.AirState.Pattern.Kind);

            // The first clip is left dead on the screens; the second is fired over it. Before, the
            // dead clip was captured as "the previous content" and every sting after it came back
            // to a dead picture — and a crash pinned it as the show to recover.
            Orphan(b.Services, first);
            Assert.True(b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, second.Id).Ok);
            Assert.Equal(second.Path, b.Services.AirState.Pattern.Media.VideoPath);
            var sponsor = fakes.Sources.Last(s => s.Wanted.Target == second.Path);
            sponsor.Ended = true;
            b.Services.Stingers.Poll();
            Assert.False(b.Services.Stingers.ClipActive);
            Assert.Equal(PatternKind.Grid, b.Services.AirState.Pattern.Kind);
            Assert.NotEqual(first.Path, b.Services.AirState.Pattern.Media.VideoPath);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheSameClipPressedAgainPlaysFromTheTop()
    {
        var b = TestApp.Boot();
        try
        {
            var fakes = AudioFakes.Install(b);
            b.Vm.State.Pattern.Kind = PatternKind.Grid;
            var item = Clip(b, "Hit");

            Assert.True(b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            var source = fakes.Sources[0];
            source.Position = 3;
            Assert.Null(source.SeekedTo);

            // The press again: the same decoder, wound back to the top; the session stays the one it was.
            Assert.True(b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            Assert.Equal(0, source.SeekedTo);
            Assert.Single(fakes.Sources);
            Assert.True(b.Services.Stingers.ClipActive);
            Assert.Equal(item.Id, b.Services.Stingers.SessionId);

            source.Ended = true;
            b.Services.Stingers.Poll();
            Assert.Equal(PatternKind.Grid, b.Services.AirState.Pattern.Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ALeftoverEndedDecoderIsRestartedByThePressAndOneThatWillNotRollPutsTheShowBack()
    {
        var b = TestApp.Boot();
        try
        {
            var fakes = AudioFakes.Install(b);
            var clock = T0;
            b.Services.Stingers.NowUtc = () => clock;
            b.Vm.State.Pattern.Kind = PatternKind.Grid;
            var item = Clip(b, "Reveal");

            // EDIT SAFE open, and the preview left on the clip after an earlier play — the pool keeps
            // that decoder for the preview, ended. Fired again, the clip lands on the program and finds it.
            b.Services.Sandbox.Enter();
            b.Services.BulkEdit(() =>
            {
                b.Vm.State.Pattern.Kind = PatternKind.Media;
                b.Vm.State.Pattern.Media.Source = MediaSource.Video;
                b.Vm.State.Pattern.Media.VideoPath = item.Path;
                b.Vm.State.Pattern.Media.Loop = false; // as a stinger's press leaves a clip: the pool shares one decoder per file and loop
            });
            Dispatcher.UIThread.RunJobs();
            var leftover = Assert.Single(fakes.Sources);
            leftover.Ended = true;

            Assert.True(b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            Assert.Single(fakes.Sources);                 // the same decoder, not a second one
            Assert.Equal(0, leftover.SeekedTo);           // told to play from the top…
            Assert.False(leftover.Ended);                 // …and it rolled
            b.Services.Stingers.Poll();
            Assert.True(b.Services.Stingers.ClipActive);  // its old ending never counted
            leftover.Ended = true;
            b.Services.Stingers.Poll();
            Assert.False(b.Services.Stingers.ClipActive);
            Assert.Equal(PatternKind.Grid, b.Services.AirState.Pattern.Kind);
            Assert.Equal("Clip finished — previous content back.", b.Services.Stingers.Status);

            // A leftover that will not roll again: a couple of seconds' grace, then the show comes back.
            leftover.Seekable = false;
            leftover.Ended = true;
            clock = T0.AddSeconds(10);
            Assert.True(b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            b.Services.Stingers.Poll();
            Assert.True(b.Services.Stingers.ClipActive);
            clock = T0.AddSeconds(11);
            b.Services.Stingers.Poll();
            Assert.True(b.Services.Stingers.ClipActive);
            clock = T0.AddSeconds(13);
            b.Services.Stingers.Poll();
            Assert.False(b.Services.Stingers.ClipActive);
            Assert.Equal(PatternKind.Grid, b.Services.AirState.Pattern.Kind);
            Assert.Equal("Clip could not play again — previous content back.", b.Services.Stingers.Status);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AClipThatStopsMovingPutsTheShowBackAndAMovingOneNever()
    {
        var b = TestApp.Boot();
        try
        {
            var fakes = AudioFakes.Install(b);
            var clock = T0;
            b.Services.Stingers.NowUtc = () => clock;
            b.Vm.State.Pattern.Kind = PatternKind.Grid;
            var item = Clip(b, "Long clip");

            Assert.True(b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            var source = fakes.Sources[0];
            source.Length = 120;

            // Moving: a minute of polls with the position advancing never puts the show back.
            for (var s = 1; s <= 60; s += 3)
            {
                clock = T0.AddSeconds(s);
                source.Position = s;
                b.Services.Stingers.Poll();
                Assert.True(b.Services.Stingers.ClipActive);
            }

            // Stuck: the decoder still says it plays, the position stops — the show comes back, no after runs.
            clock = T0.AddSeconds(70);
            b.Services.Stingers.Poll();
            Assert.True(b.Services.Stingers.ClipActive);
            clock = T0.AddSeconds(70 + StingerService.StallSeconds + 1);
            b.Services.Stingers.Poll();
            Assert.False(b.Services.Stingers.ClipActive);
            Assert.Equal(PatternKind.Grid, b.Services.AirState.Pattern.Kind);
            Assert.Equal("Clip stalled — previous content back.", b.Services.Stingers.Status);
            Assert.Contains(b.Services.Journal.Tail(3), e => e.Kind == "StingerStop" && e.Outcome == "Failed");
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void RecoveryNeverPutsAClipBackAsTheShow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-cliprec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var clip = AudioFakes.TempFile("crash-sting.mp4");

        // The show as saved: a grid, with the clip in its library.
        var saved = new ShowState();
        saved.Pattern.Kind = PatternKind.Grid;
        saved.Stingers.Items.Add(new StingerItemConfig { Path = clip, Name = "Crash sting" });
        new SettingsStore(dir).Save(saved);

        // What a crash mid-clip could leave in the sidecar before this round: the clip as the air content.
        var onAir = JsonUtil.Clone(saved);
        onAir.Pattern.Kind = PatternKind.Media;
        onAir.Pattern.Media.Source = MediaSource.Video;
        onAir.Pattern.Media.VideoPath = clip;
        new RecoveryStore(dir).Write(live: true, audioPlaying: false, airLook: LookService.Capture(onAir));

        var services = new AppServices(new SettingsStore(dir));
        AppServices.Instance = services;
        var vm = new Patterns.App.ViewModels.MainViewModel(services);
        var window = new Patterns.App.Views.MainWindow { DataContext = vm };
        services.AttachMainWindow(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            AudioFakes.Install(new TestApp.Booted(services, vm, window, dir));
            services.TryRecover(vm);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);   // the show, never the clip
            Assert.True(services.Outputs.IsLive);                                       // the rest of the recovery still ran
        }
        finally
        {
            window.Close();
            services.Shutdown();
            File.Delete(clip);
        }
    }
}
