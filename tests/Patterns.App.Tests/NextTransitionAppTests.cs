using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using System.Text.Json;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 67.6 on a live desk: the one-shot set from the wire, shown on the TAKE key, in STATE and in the
/// Eye; spent by the TAKE it was given to and drawn that way on that publish alone while the show's
/// transition never moves; a CUT is a cut and keeps it; the tile's own TAKE spends it too; a video sting
/// covers the screens and the preview lands when the clip ends.
/// </summary>
public class NextTransitionAppTests
{
    private sealed class FakeClip : IMountedSource
    {
        public bool Ended;
        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint) => false;
        public SKSizeI? FrameSize => null;
        public bool IsPlaying => !Ended;
        public bool IsEnded => Ended;
        public double DurationSeconds => 10;
        public string StatusText => "fake clip";
        public void SetAudio(bool mute, double volumePct) { }
        public void BeginFadeOut(DateTime nowUtc, int ms) { }
        public void Pump(DateTime nowUtc) { }
        public void Dispose() { }
    }

    private static List<ScreenInfo> TwoScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
    };

    private static void Rig(TestApp.Booted b)
    {
        var fakes = TwoScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles(fakes);
        Dispatcher.UIThread.RunJobs();
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static void Programme(TestApp.Booted b, PatternKind kind)
    {
        b.Vm.IsSandboxActive = true;
        Dispatcher.UIThread.RunJobs();
        b.Vm.State.Pattern.Kind = kind;
        b.Services.Sandbox.SendAll();
        b.Vm.IsSandboxActive = true;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheOneShotIsSetShownSpentByTheTakeAndNeverMovesTheShowsTransition()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            Programme(b, PatternKind.Grid);
            var router = new CommandRouter(services);
            Assert.Equal(TransitionKind.Dissolve, vm.State.Transition.Kind);
            Assert.Equal("TAKE", vm.TakeButtonText);

            // Set from the wire: the key, STATE and the Eye say so; the show's setting does not move.
            Assert.StartsWith("OK", Send(router, "TAKE NEXT wipe left 800"));
            Assert.Equal("WIPE LEFT 800 ms", services.NextTake.Pending!.Words);
            Assert.Equal("TAKE · WIPE LEFT 800 ms", vm.TakeButtonText);
            Assert.Contains("one shot", vm.NextTakeWords);
            Assert.Equal(TransitionKind.Dissolve, vm.State.Transition.Kind);
            Assert.Equal(400, vm.State.Transition.DurationMs);
            using (var doc = JsonDocument.Parse(Send(router, "STATUS")[3..]))
            {
                var take = doc.RootElement.GetProperty("take");
                Assert.True(take.GetProperty("next").GetProperty("set").GetBoolean());
                Assert.Equal("WIPE LEFT 800 ms", take.GetProperty("next").GetProperty("words").GetString());
                Assert.Equal("TAKE NEXT wipe left 800", take.GetProperty("next").GetProperty("wire").GetString());
                Assert.Equal("on every armed screen", take.GetProperty("where").GetString());
            }
            Assert.Contains(services.Eye.Graph.Find(EyeGraph.DeskId)!.Words, w => w.StartsWith("Next take: WIPE LEFT 800 ms", StringComparison.Ordinal));

            // A CUT is a cut and keeps it for the TAKE.
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            var cut = services.Actions.Execute(ShowActionKind.Cut, ActionOrigin.Desk, "");
            Assert.True(cut.Ok, cut.Message);
            Assert.NotNull(services.NextTake.Pending);
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("a").Kind);

            // The TAKE spends it: that publish is drawn as a wipe at 800 ms, and the one after it the show's way.
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            var before = services.Bus.Current.Version;
            var taken = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "");
            Assert.True(taken.Ok, taken.Message);
            Assert.Contains("Arrived by WIPE LEFT 800 ms (one shot)", taken.Message);
            var snap = services.Bus.Current;
            Assert.Equal(PatternKind.ColorBars, snap.PatternFor("a").Kind);
            Assert.Equal(TransitionKind.Wipe, snap.TransitionOverride);
            Assert.Equal(TransitionDirection.Left, snap.TransitionDirectionOverride);
            Assert.True(snap.TransitionOverrideVersion > before);
            Assert.Equal(TransitionKind.Wipe, snap.TransitionKindFor(snap.TransitionOverrideVersion));
            Assert.Equal(0.8, snap.FadeSecondsFor(snap.TransitionOverrideVersion), 3);
            Assert.Null(services.NextTake.Pending);
            Assert.Equal("TAKE", vm.TakeButtonText);
            Assert.Equal(TransitionKind.Dissolve, vm.State.Transition.Kind);
            Assert.Equal(TransitionKind.Dissolve, services.AirState.Transition.Kind);
            Assert.DoesNotContain(services.Eye.Graph.Find(EyeGraph.DeskId)!.Words, w => w.StartsWith("Next take", StringComparison.Ordinal));

            var settled = services.Bus.Current.TransitionOverrideVersion;
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "").Ok);
            Assert.Equal(settled, services.Bus.Current.TransitionOverrideVersion);            // no new override: the show's way
            Assert.Equal(TransitionKind.Dissolve, services.Bus.Current.TransitionKindFor(services.Bus.Current.Version));

            // The tile's own TAKE spends it too; CLEAR takes it away; a bad word is refused and changes nothing.
            Assert.StartsWith("OK", Send(router, "TAKE NEXT push"));
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            var tile = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.True(tile.Ok, tile.Message);
            Assert.Contains("Arrived by PUSH", tile.Message);
            Assert.Equal(TransitionKind.Push, services.Bus.Current.TransitionOverride);
            Assert.Null(services.NextTake.Pending);
            Assert.StartsWith("OK", Send(router, "TAKE NEXT dip"));
            Assert.NotNull(services.NextTake.Pending);
            Assert.StartsWith("OK", Send(router, "TAKE NEXT CLEAR"));
            Assert.Null(services.NextTake.Pending);
            Assert.StartsWith("ERR", Send(router, "TAKE NEXT sideways"));
            Assert.Null(services.NextTake.Pending);
            Assert.StartsWith("ERR", Send(router, "TAKE NEXT STING nothing"));

            // The menus offer it on every TAKE: the PGM strip's and a tile's.
            var pgm = vm.MenuFor("program", null)!;
            Assert.NotNull(pgm.Find("take.next"));
            Assert.NotNull(pgm.Find("take.next:wipe-left"));
            var tileMenu = vm.MenuFor("tile", vm.SwitcherTiles.Single(t => t.TargetId == "b"))!;
            var choice = tileMenu.Find("take.next:dip")!;
            choice.ChooseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("DIP", services.NextTake.Pending!.Words);
            Assert.True(vm.MenuFor("program", null)!.Find("take.next:dip")!.IsOn);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AVideoStingCoversTheScreensAndThePreviewLandsWhenTheClipEnds()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            AudioFakes.Install(b);
            var clip = new FakeClip();
            services.Video.SourceFactory = _ => clip;
            Rig(b);
            Programme(b, PatternKind.Grid);
            var sting = new StingerItemConfig { Kind = StingerKind.Sting, Path = AudioFakes.TempFile("whoosh.mp4"), Name = "Whoosh" };
            vm.State.Stingers.Items.Add(sting);
            var router = new CommandRouter(services);

            Assert.StartsWith("OK", Send(router, "TAKE NEXT STING Whoosh"));
            Assert.Equal("STING Whoosh", services.NextTake.Pending!.Words);
            Assert.Equal("TAKE · STING Whoosh", vm.TakeButtonText);

            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            var taken = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "");
            Assert.Equal(ActionStatus.Requested, taken.Status);                              // sent — the take lands when the clip ends
            Assert.Contains("under the sting 'Whoosh'", taken.Message);
            Assert.True(services.Stingers.ClipActive);
            Assert.Equal(StingerAfter.Take, services.Stingers.SessionAfter);
            Assert.Equal("", services.Stingers.SessionTakeScope);
            Assert.Equal(sting.Path, services.AirState.Pattern.Media.VideoPath);              // the clip owns the screens
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);                       // the preview waits
            Assert.Null(services.NextTake.Pending);

            // The clip ends: the take runs, the clip goes, the show's transition never moved.
            clip.Ended = true;
            services.Stingers.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal("Sting done — the show moved on.", services.Stingers.Status);
            Assert.Equal(TransitionKind.Dissolve, vm.State.Transition.Kind);
            Assert.Contains(services.Journal.Tail(50), e => e.Kind == "Take" || e.Message.Contains("TAKE"));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATakeUnderAStingSpendsItsTicksAsItLandsAndLandsWhereItWasPressed()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            AudioFakes.Install(b);
            var clip = new FakeClip();
            services.Video.SourceFactory = _ => clip;
            Rig(b);
            Programme(b, PatternKind.Grid);
            vm.State.Stingers.Items.Add(new StingerItemConfig { Kind = StingerKind.Sting, Path = AudioFakes.TempFile("whoosh.mp4"), Name = "Whoosh" });
            var router = new CommandRouter(services);
            SwitcherTile Tile(string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);

            // TICKED under a sting: the press answers Requested, the tick is read — and spent — as the take lands.
            vm.SelectedTakeScope = vm.TakeScopes[2];
            Tile("b").IsSendTarget = true;
            Assert.StartsWith("OK", Send(router, "TAKE NEXT STING Whoosh"));
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Stingers.ClipActive, vm.StatusMessage);
            Assert.Equal("TICKED", services.Stingers.SessionTakeScope);
            Assert.True(Tile("b").IsSendTarget, "the tick is kept until the take lands");
            // The clip covers the take's screen alone: the other screen and the programme are untouched.
            Assert.Equal(PatternKind.Media, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, services.AirState.Pattern.Kind);
            // A one-shot set during the clip is the operator's next TAKE's — the landing never spends it.
            Assert.StartsWith("OK", Send(router, "TAKE NEXT dip"));
            clip.Ended = true;
            services.Stingers.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);
            Assert.False(Tile("b").IsSendTarget, "the tick is spent as the take lands");
            Assert.Equal("DIP", services.NextTake.Pending!.Words);
            Assert.NotEqual(TransitionKind.Dip, services.Bus.Current.TransitionOverride);
            Assert.StartsWith("OK", Send(router, "TAKE NEXT CLEAR"));

            // FOCUSED under a sting lands on the tile focused at the press — a click on another tile during the clip moves nothing.
            clip = new FakeClip();
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            vm.SelectTileCommand.Execute(Tile("a"));
            vm.SelectedTakeScope = vm.TakeScopes[1];
            Assert.StartsWith("OK", Send(router, "TAKE NEXT STING Whoosh"));
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Stingers.ClipActive, vm.StatusMessage);
            Assert.Contains("Left", vm.StatusMessage);
            Assert.Equal("ID a", services.Stingers.SessionTakeScope);
            Assert.Equal(PatternKind.Media, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("b").Kind);
            vm.SelectTileCommand.Execute(Tile("b"));                                        // a look at the other tile meanwhile
            Dispatcher.UIThread.RunJobs();
            clip.Ended = true;
            services.Stingers.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("b").Kind);
        }
        finally
        {
            b.Dispose();
        }
    }
}
