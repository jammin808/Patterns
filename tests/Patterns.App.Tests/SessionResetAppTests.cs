using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 72: the operator's session on the last show is the last show's. A show loaded, restored or mirrored
/// starts as a show opens — no one-shot, no sting waiting to land, no ticks, the programme focused and edited,
/// every target armed, nothing staged — and the programme the audience has is the loaded show, EDIT SAFE open or
/// not. And a canvas's role changes as one edit, never a publish per member.
/// </summary>
public class SessionResetAppTests
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

    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(1920, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(4400, 0, 1920, 1080), 1.0, false, 2),
    };

    private static void Rig(TestApp.Booted b, bool joined = false)
    {
        var fakes = joined ? ThreeScreens() : TwoScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        if (joined)
        {
            // a and b flush: canvas A; c stands alone.
            var a = b.Vm.State.Output.Placements.First(p => p.ScreenId == "a");
            var bb = b.Vm.State.Output.Placements.First(p => p.ScreenId == "b");
            var c = b.Vm.State.Output.Placements.First(p => p.ScreenId == "c");
            a.X = 0; a.Y = 0;
            bb.X = 1920; bb.Y = 0;
            c.X = 6000; c.Y = 0;
        }
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
    public void AReplacedShowStartsAsAShowOpensAndIsTheProgrammeTheAudienceHas()
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

            // The session of the last show: a tick, a tile focused and edited, a tile un-armed, a preview edit, and a TAKE waiting under a sting.
            Tile("a").IsSendTarget = true;
            vm.SelectTileCommand.Execute(Tile("b"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("b", vm.EditTarget.ScreenId);
            Assert.NotEqual("PGM", vm.SelectedTargetLabel);
            services.Arming.Set("a", false);
            Assert.NotEmpty(services.Arming.Unarmed);
            vm.SelectedTakeScope = vm.TakeScopes[0];
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("OK", Send(router, "TAKE NEXT STING Whoosh"));
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Stingers.ClipActive, vm.StatusMessage);
            Assert.NotNull(services.Stingers.SessionTicket);
            Assert.StartsWith("OK", Send(router, "TAKE NEXT dip"));
            Assert.NotNull(services.NextTake.Pending);
            Assert.True(services.Sandbox.Active);

            // Another show replaces it, with EDIT SAFE open.
            var loaded = new ShowState { Name = "Awards" };
            loaded.Pattern.Kind = PatternKind.LedWall;
            var path = Path.Combine(b.Dir, "awards.patshow.json");
            services.Store.SaveJsonTo(path, JsonUtil.Serialize(loaded));
            Assert.Equal("Show loaded: awards.patshow.json", TestApp.Pump(vm.LoadShowFromAsync(path)));
            Dispatcher.UIThread.RunJobs();

            // Nothing of the last session survives: no one-shot, no sting waiting, no ticks, every target armed, the programme focused and edited.
            Assert.Null(services.NextTake.Pending);
            Assert.False(services.Stingers.ClipActive);
            Assert.Null(services.Stingers.SessionTicket);
            Assert.Contains(services.Journal.Tail(30), e => e.Message.Contains("Show replaced"));
            Assert.All(vm.SwitcherTiles, t => Assert.False(t.IsSendTarget));
            Assert.Empty(services.Arming.Unarmed);
            Assert.Null(vm.EditTarget.ScreenId);
            Assert.Equal("PGM", vm.SelectedTargetLabel);
            Assert.Equal("Awards", vm.State.Name);

            // The programme the audience has is the loaded show — EDIT SAFE stays open over it, and a discard changes nothing.
            Assert.True(services.Sandbox.Active);
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(PatternKind.LedWall, services.AirState.Pattern.Kind);
            Assert.Equal(PatternKind.LedWall, vm.State.Pattern.Kind);
            services.Sandbox.Discard();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.LedWall, vm.State.Pattern.Kind);
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.State.Pattern.Kind);

            // The clip's end, had it come, lands nothing: the stinger's poll is quiet.
            clip.Ended = true;
            services.Stingers.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("a").Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AReplacedShowWithEditSafeClosedIsOnAirAtOnceAndTheSessionIsReset()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            var router = new CommandRouter(services);
            SwitcherTile Tile(string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);
            Tile("b").IsSendTarget = true;
            services.Arming.Set("b", false);
            Assert.StartsWith("OK", Send(router, "TAKE NEXT wipe left 800"));

            var loaded = new ShowState { Name = "Gala" };
            loaded.Pattern.Kind = PatternKind.Focus;
            var path = Path.Combine(b.Dir, "gala.patshow.json");
            services.Store.SaveJsonTo(path, JsonUtil.Serialize(loaded));
            Assert.Equal("Show loaded: gala.patshow.json", TestApp.Pump(vm.LoadShowFromAsync(path)));
            Dispatcher.UIThread.RunJobs();

            Assert.False(services.Sandbox.Active);
            Assert.Equal(PatternKind.Focus, services.Bus.Current.State.Pattern.Kind);
            Assert.Null(services.NextTake.Pending);
            Assert.All(vm.SwitcherTiles, t => Assert.False(t.IsSendTarget));
            Assert.Empty(services.Arming.Unarmed);
            Assert.Equal("TAKE", vm.TakeButtonText);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ACanvasRoleChangeIsOneEditOfTheStateAndOneOfTheProgramme()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b, joined: true);
            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();
            var key = CanvasNameConfig.KeyFor(new[] { "a", "b" });
            Assert.True(ContentTargets.IsInRig(vm.State, key), "a and b flush make canvas A");
            var a = vm.State.Output.Placements.First(p => p.ScreenId == "a");
            var bb = vm.State.Output.Placements.First(p => p.ScreenId == "b");
            var c = vm.State.Output.Placements.First(p => p.ScreenId == "c");
            Assert.True(a.FollowsCues && bb.FollowsCues && c.FollowsCues);

            // One screen's role: the verb's one edit, then the desk's own follow-on (the NEXT TAKE line through the snapshot).
            var before = services.Bus.Current.Version;
            var one = services.Actions.Execute(ShowActionKind.ScreenRole, ActionOrigin.Desk, "c", "info");
            Assert.True(one.Ok, one.Message);
            Assert.False(c.FollowsCues);
            var perScreen = services.Bus.Current.Version - before;
            Assert.InRange(perScreen, 1, 2);

            // Info on the canvas: both screens take the role and the lock the role picks — and it costs exactly what one screen does.
            before = services.Bus.Current.Version;
            var result = services.Actions.Execute(ShowActionKind.ScreenRole, ActionOrigin.Desk, key, "info");
            Assert.True(result.Ok, result.Message);
            Assert.Equal(ScreenRole.Info, a.Role);
            Assert.Equal(ScreenRole.Info, bb.Role);
            Assert.False(a.FollowsCues);
            Assert.False(bb.FollowsCues);
            Assert.Contains("every screen is", result.Message);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(result.Message, "locked — it keeps its picture"));
            Assert.Equal(perScreen, services.Bus.Current.Version - before);

            // Main again, with EDIT SAFE open: the edited state and the frozen programme both move, once each.
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            var air = services.AirState;
            Assert.NotSame(vm.State, air);
            result = services.Actions.Execute(ShowActionKind.ScreenRole, ActionOrigin.Desk, key, "main");
            Assert.True(result.Ok, result.Message);
            Assert.True(a.FollowsCues && bb.FollowsCues);
            Assert.All(air.Output.Placements.Where(p => p.ScreenId is "a" or "b"), p => { Assert.Equal(ScreenRole.Main, p.Role); Assert.True(p.FollowsCues); });
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(result.Message, "follows looks, cues and TAKE again"));

            // The same role again moves nothing and says so plainly.
            result = services.Actions.Execute(ShowActionKind.ScreenRole, ActionOrigin.Desk, key, "main");
            Assert.True(result.Ok);
            Assert.DoesNotContain("follows looks", result.Message);
        }
        finally
        {
            b.Dispose();
        }
    }
}
