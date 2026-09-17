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
/// Round 72 on a live desk: a TAKE under a video sting is a transaction. The press freezes a ticket — what it
/// promised, where, under which sting — that STATE and the Eye show while the clip runs; the landing runs that
/// ticket, never a fresh plan: a tick or a screen that arrived during the clip is the next press's, a lock since
/// holds its screen with the reason, and a landing with nothing left puts the show back and says why. A press
/// that fails keeps its one-shot; a sting gone from the library clears it, and says so.
/// </summary>
public class TakeTicketAppTests
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

    private static StingerItemConfig Whoosh(TestApp.Booted b)
    {
        var sting = new StingerItemConfig { Kind = StingerKind.Sting, Path = AudioFakes.TempFile("whoosh.mp4"), Name = "Whoosh" };
        b.Vm.State.Stingers.Items.Add(sting);
        return sting;
    }

    private static JsonElement Landing(CommandRouter router) => JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("take").GetProperty("landing");

    [AvaloniaFact]
    public void ATakeUnderAStingLandsWhatThePressPromisedAndNeverMore()
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
            Whoosh(b);
            var router = new CommandRouter(services);
            SwitcherTile Tile(string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);

            // TICKED with one tile ticked: the press promises that tile alone, and spends the one-shot as it fires.
            vm.SelectedTakeScope = vm.TakeScopes[2];
            Tile("a").IsSendTarget = true;
            Assert.StartsWith("OK", Send(router, "TAKE NEXT STING Whoosh"));
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Stingers.ClipActive, vm.StatusMessage);
            var ticket = services.Stingers.SessionTicket;
            Assert.NotNull(ticket);
            Assert.Equal(new[] { "a" }, ticket!.Taken);
            Assert.Equal("TICKED", ticket.Scope);
            Assert.Equal("Whoosh", ticket.Cover);
            Assert.False(ticket.Tile);
            Assert.EndsWith("when 'Whoosh' ends", ticket.Words);
            Assert.Contains("Left", ticket.Words);
            Assert.Null(services.NextTake.Pending);

            // STATE and the Eye show the promise while the clip runs.
            var landing = Landing(router);
            Assert.Equal("Whoosh", landing.GetProperty("sting").GetString());
            Assert.Equal("TICKED", landing.GetProperty("scope").GetString());
            Assert.Equal(new[] { "a" }, landing.GetProperty("targets").EnumerateArray().Select(t => t.GetString()));
            Assert.Equal(ticket.Words, landing.GetProperty("words").GetString());
            services.Eye.Refresh();                                                                    // false when the facts already carried it: the take refreshed the Eye itself
            Assert.Contains(services.Eye.Graph.Find("desk")!.Words, w => w.StartsWith("Landing → ", StringComparison.Ordinal) && w.Contains("Whoosh"));

            // During the clip the operator ticks the other tile: the next press's, not this one's.
            Tile("b").IsSendTarget = true;
            clip.Ended = true;
            services.Stingers.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal("Sting done — the show moved on.", services.Stingers.Status);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);               // never more than the press promised
            Assert.Null(services.Stingers.SessionTicket);
            Assert.Equal(JsonValueKind.Null, Landing(router).ValueKind);
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == "Take" && e.Message.Contains("as pressed under the sting 'Whoosh'"));
            services.Eye.Refresh();                                                                    // false when the facts already carried it: the take refreshed the Eye itself
            Assert.DoesNotContain(services.Eye.Graph.Find("desk")!.Words, w => w.StartsWith("Landing", StringComparison.Ordinal));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ALockSinceThePressHoldsItsScreenAndEverythingLockedSinceIsARefusalThatSaysWhy()
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
            Whoosh(b);
            var router = new CommandRouter(services);

            // Every armed screen under a sting; the right screen is locked while the clip runs.
            vm.SelectedTakeScope = vm.TakeScopes[0];
            Assert.StartsWith("OK", Send(router, "TAKE NEXT STING Whoosh"));
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Stingers.ClipActive, vm.StatusMessage);
            Assert.Equal(new[] { "a", "b" }, services.Stingers.SessionTicket!.Taken);
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenLock, ActionOrigin.Desk, "b").Ok);
            clip.Ended = true;
            services.Stingers.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal("Sting done — the show moved on.", services.Stingers.Status);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);               // LOCKED means locked, whenever the lock came
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == "Take" && e.Message.Contains("held since the press: ") && e.Message.Contains("(locked since the press)"));

            // Everything the press promised locked since: nothing lands, the show comes back, and the words say why.
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenUnlock, ActionOrigin.Desk, "b").Ok);
            clip = new FakeClip();
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("OK", Send(router, "TAKE NEXT STING Whoosh"));
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Stingers.ClipActive, vm.StatusMessage);
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenLock, ActionOrigin.Desk, "a").Ok);
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenLock, ActionOrigin.Desk, "b").Ok);
            clip.Ended = true;
            services.Stingers.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal("Sting could not move the show on — previous content back.", services.Stingers.Status);
            Assert.NotEqual(PatternKind.LedWall, services.Bus.Current.PatternFor("a").Kind);
            Assert.NotEqual(PatternKind.LedWall, services.Bus.Current.PatternFor("b").Kind);
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == "Take" && e.Message.StartsWith("Nothing lands — ", StringComparison.Ordinal) && e.Message.Contains("(locked since the press)"));
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// Round 75: a target that is not what the press saw is held. The right screen is made a repeater of the left
    /// while the clip runs; the landing holds it with what it is now, lands the left, and the journal's TAKE row says so.
    /// </summary>
    [AvaloniaFact]
    public void AScreenMadeARepeaterDuringTheClipIsHeldAsChangedSinceThePress()
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
            Whoosh(b);
            var router = new CommandRouter(services);

            vm.SelectedTakeScope = vm.TakeScopes[0];
            Assert.StartsWith("OK", Send(router, "TAKE NEXT STING Whoosh"));
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Stingers.ClipActive, vm.StatusMessage);
            var ticket = services.Stingers.SessionTicket!;
            Assert.Equal(new[] { "a", "b" }, ticket.Taken);
            Assert.Equal(new[] { "a screen of its own", "a screen of its own" }, ticket.Shapes);

            // The right screen becomes a repeater of the left while the clip runs: not what the press saw.
            vm.State.Output.Placements.First(p => p.ScreenId == "b").MirrorOf = "a";
            Dispatcher.UIThread.RunJobs();
            clip.Ended = true;
            services.Stingers.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal("Sting done — the show moved on.", services.Stingers.Status);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("a").Kind);
            var take = Assert.Single(services.Journal.Tail(20), e => e.Kind == "Take" && e.Message.Contains("held since the press: ", StringComparison.Ordinal));
            Assert.Contains("(changed since the press — now a repeater of ", take.Message, StringComparison.Ordinal);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// Round 75 (72.3's open item): a LOCK during a running clip pins the picture beneath the clip — never the
    /// clip. With EDIT SAFE open the edited state pins the show the sting covers while the clip plays on for the
    /// audience; with EDIT SAFE closed the lock holds the show beneath once the clip ends. Either way the screen is
    /// locked and shows the show, never a frozen transition.
    /// </summary>
    [AvaloniaFact]
    public void ALockDuringTheClipPinsThePictureBeneathTheClipNeverTheClip()
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
            Whoosh(b);
            var router = new CommandRouter(services);

            // EDIT SAFE open: a whole cover under a TAKE; LOCK the right screen while the clip runs.
            vm.SelectedTakeScope = vm.TakeScopes[0];
            Assert.StartsWith("OK", Send(router, "TAKE NEXT STING Whoosh"));
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Stingers.ClipActive, vm.StatusMessage);
            Assert.Equal(PatternKind.Grid, services.Stingers.PictureUnder("b")!.Kind);
            Assert.Null(services.Stingers.PictureUnder("nowhere"));
            Assert.Equal(PatternKind.Media, LookService.Shown(services.AirState, "b").Kind);            // the audience sees the clip
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenLock, ActionOrigin.Desk, "b").Ok);
            Assert.Equal(PatternKind.Grid, LookService.Shown(vm.State, "b").Kind);                    // the edited state pins the show beneath, never the clip
            Assert.Equal(PatternKind.Media, LookService.Shown(services.AirState, "b").Kind);            // the clip plays on
            clip.Ended = true;
            services.Stingers.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, LookService.Shown(vm.State, "b").Kind);
            Assert.True(ScreenRoles.IsLocked(vm.State, "b"));
            Assert.Null(services.Stingers.PictureUnder("b"));                                          // no clip, no picture beneath: the air is the truth

            // EDIT SAFE closed: the right screen follows the programme again; a sting fired over the programme; LOCK
            // during the clip; when the clip ends the lock holds the show beneath, not the clip's last frame.
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenUnlock, ActionOrigin.Desk, "b").Ok);
            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();
            ContentTargets.SetOwnPattern(vm.State, "b", false);
            services.RepublishNow();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, LookService.Shown(vm.State, "b").Kind);
            clip = new FakeClip();
            Assert.True(services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, "Whoosh").Ok, vm.StatusMessage);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Stingers.ClipActive, services.Stingers.Status);
            Assert.Equal(PatternKind.Media, LookService.Shown(vm.State, "b").Kind);
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenLock, ActionOrigin.Desk, "b").Ok);
            clip.Ended = true;
            services.Stingers.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("b").Kind);   // the show beneath the clip, held by the lock
            Assert.Equal(PatternKind.ColorBars, LookService.Shown(vm.State, "b").Kind);
            Assert.True(ScreenRoles.IsLocked(vm.State, "b"));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATilesTakeUnderAStingLandsOnTheTileAloneAndALockSinceHoldsIt()
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
            Whoosh(b);
            var router = new CommandRouter(services);

            Assert.StartsWith("OK", Send(router, "TAKE NEXT STING Whoosh"));
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            var pressed = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "a");
            Assert.Equal(ActionStatus.Requested, pressed.Status);
            var ticket = services.Stingers.SessionTicket!;
            Assert.True(ticket.Tile);
            Assert.Equal(new[] { "a" }, ticket.Taken);
            Assert.Equal("TILE a", ticket.Scope);
            Assert.Null(services.NextTake.Pending);
            Assert.Equal(PatternKind.Media, services.Bus.Current.PatternFor("a").Kind);               // the clip covers the tile alone
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);

            // The tile lands as pressed.
            clip.Ended = true;
            services.Stingers.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Sting done — the show moved on.", services.Stingers.Status);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == "ScreenTake" && e.Message.Contains("alone"));

            // A lock after the press holds the tile: nothing lands, and the words say it was locked after the press.
            clip = new FakeClip();
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("OK", Send(router, "TAKE NEXT STING Whoosh"));
            Assert.Equal(ActionStatus.Requested, services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b").Status);
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenLock, ActionOrigin.Desk, "b").Ok);
            clip.Ended = true;
            services.Stingers.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Sting could not move the show on — previous content back.", services.Stingers.Status);
            Assert.NotEqual(PatternKind.LedWall, services.Bus.Current.PatternFor("b").Kind);
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == "ScreenTake" && e.Message.Contains("was locked after the press"));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void APressThatFailsKeepsItsOneShotAndAStingGoneFromTheLibraryClearsIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            AudioFakes.Install(b);
            Rig(b);
            Programme(b, PatternKind.Grid);
            var ghost = new StingerItemConfig { Kind = StingerKind.Sting, Path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-no-such-clip.mp4"), Name = "Ghost" };
            vm.State.Stingers.Items.Add(ghost);
            var router = new CommandRouter(services);

            // The clip's file is missing: the sting cannot fire, nothing moves, and the one-shot stays for the next press.
            Assert.StartsWith("OK", Send(router, "TAKE NEXT STING Ghost"));
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            var failed = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "");
            Assert.Equal(ActionStatus.Failed, failed.Status);
            Assert.Contains("File missing", failed.Message);
            Assert.EndsWith("The one-shot stays for the next TAKE.", failed.Message);
            Assert.Equal("STING Ghost", services.NextTake.Pending!.Words);
            Assert.False(services.Stingers.ClipActive);
            Assert.Null(services.Stingers.SessionTicket);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);

            // The sting leaves the library: the one-shot cannot ever run — it is cleared, and the refusal says so.
            vm.State.Stingers.Items.Remove(ghost);
            var gone = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "");
            Assert.Equal(ActionStatus.Refused, gone.Status);
            Assert.Contains("the one-shot is cleared", gone.Message);
            Assert.Null(services.NextTake.Pending);

            // The next press is the show's own, and it happens.
            var plain = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "");
            Assert.Equal(ActionStatus.Done, plain.Status);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("a").Kind);

            // A CUT never spends a one-shot; a plain TAKE spends it as it happens.
            Assert.StartsWith("OK", Send(router, "TAKE NEXT dip"));
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ActionStatus.Done, services.Actions.Execute(ShowActionKind.Cut, ActionOrigin.Desk, "").Status);
            Assert.Equal("DIP", services.NextTake.Pending!.Words);
            vm.State.Pattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ActionStatus.Done, services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "").Status);
            Assert.Null(services.NextTake.Pending);
        }
        finally
        {
            b.Dispose();
        }
    }
}
