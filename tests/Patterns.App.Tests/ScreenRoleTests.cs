using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Screens with a job: a locked confidence monitor keeps its picture through TAKE ALL and every
/// look, a repeater copies another target, the Screens page and the wall drive both, and SEND
/// puts the preview on one tile alone.
/// </summary>
public class ScreenRoleTests
{
    private static readonly string CanvasKey = CanvasNameConfig.KeyFor(new[] { "a", "b" });

    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new Avalonia.PixelRect(1920, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new Avalonia.PixelRect(4400, 0, 1920, 1080), 1.0, false, 2),
    };

    /// <summary>a+b flush = canvas A; c stands alone (tile 3). All three enabled.</summary>
    private static void Rig(TestApp.Booted b)
    {
        var fakes = ThreeScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        var a = b.Vm.State.Output.Placements.First(p => p.ScreenId == "a");
        var bb = b.Vm.State.Output.Placements.First(p => p.ScreenId == "b");
        var c = b.Vm.State.Output.Placements.First(p => p.ScreenId == "c");
        a.X = 0; a.Y = 0;
        bb.X = 1920; bb.Y = 0;
        c.X = 6000; c.Y = 0;
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles();
        Dispatcher.UIThread.RunJobs();
    }

    private static string LookOf(PatternKind kind)
    {
        var s = new ShowState();
        s.Pattern.Kind = kind;
        return LookService.Capture(s);
    }

    [AvaloniaFact]
    public void ALockedTileKeepsItsPictureThroughTakeAllAndEveryLookUntilItIsUnlocked()
    {
        var b = TestApp.Boot();
        try
        {
            Rig(b);
            var vm = b.Vm;
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            var lobby = vm.SwitcherTiles[2];
            Assert.Equal("c", lobby.TargetId);
            Assert.False(lobby.IsLocked);

            lobby.IsLocked = true; // LOCK on the tile, through the action layer
            Dispatcher.UIThread.RunJobs();
            var c = vm.State.Output.Placements.First(p => p.ScreenId == "c");
            Assert.False(c.FollowsCues);
            Assert.True(c.UseCustomPattern); // it took the program as its own picture
            Assert.True(vm.SwitcherTiles[2].IsLocked);
            Assert.True(vm.SwitcherTiles[2].IsOwn);
            Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.PatternFor("c").Kind);
            Assert.Contains("locked", vm.StatusMessage);

            // TAKE ALL: everything armed moves; the locked lobby keeps its picture and reads as held.
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.SwitcherTiles[2].IsHeld);
            Assert.Equal(1, vm.HeldCount);
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var air = b.Services.Bus.Current;
            Assert.Equal(PatternKind.ColorBars, air.State.Pattern.Kind);
            Assert.Equal(PatternKind.ColorBars, air.PatternFor(CanvasKey).Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("c").Kind);

            // A look recall to air leaves it alone too.
            vm.State.LooksAndCues.Looks.Add(new LookConfig { Name = "Focus", Json = LookOf(PatternKind.Focus) });
            Assert.True(b.Services.Actions.Execute(new ShowAction(ShowActionKind.ApplyLook, "Focus"), ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            air = b.Services.Bus.Current;
            Assert.Equal(PatternKind.Focus, air.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("c").Kind);

            // Unlocked, the next recall reaches it.
            vm.SwitcherTiles[2].IsLocked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(c.FollowsCues);
            Assert.Equal(0, vm.HeldCount);
            b.Services.Actions.Execute(new ShowAction(ShowActionKind.ApplyLook, "Focus"), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Focus, b.Services.Bus.Current.PatternFor("c").Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheScreensPageDrivesRolesLocksAndMirrorsAndARepeaterCopiesItsSource()
    {
        var b = TestApp.Boot();
        try
        {
            Rig(b);
            var vm = b.Vm;
            vm.IsSandboxActive = false;
            var c = vm.State.Output.Placements.First(p => p.ScreenId == "c");
            vm.SelectedPlacement = c;
            Assert.Equal(ScreenRole.Main, vm.SelectedRole);
            Assert.True(vm.SelectedFollowsCues);
            Assert.Equal("", vm.MirrorSources[0].ScreenId);
            Assert.Contains(vm.MirrorSources, t => t.ScreenId == CanvasKey);
            Assert.DoesNotContain(vm.MirrorSources, t => t.ScreenId == "c");

            // A confidence monitor is locked as it is chosen; the tile wears the badge and the lock.
            vm.SelectedRole = ScreenRole.Confidence;
            Assert.False(c.FollowsCues);
            Assert.False(vm.SelectedFollowsCues);
            Assert.Equal("CONF", vm.SwitcherTiles[2].RoleBadge);
            Assert.True(vm.SwitcherTiles[2].HasBadge);
            Assert.True(vm.SwitcherTiles[2].IsLocked);
            vm.SelectedFollowsCues = true; // the operator can still let it follow
            Assert.True(c.FollowsCues);
            Assert.False(vm.SwitcherTiles[2].IsLocked);

            // A repeater of a screen: its source cannot be told to repeat it back.
            vm.SelectedRole = ScreenRole.Repeater;
            vm.SelectedMirrorOf = "a";
            Assert.Equal("a", c.MirrorOf);
            Assert.Equal("REP", vm.SwitcherTiles[2].RoleBadge);
            vm.SelectedPlacement = vm.State.Output.Placements.First(p => p.ScreenId == "a");
            Assert.DoesNotContain(vm.MirrorSources, t => t.ScreenId == "c");
            Assert.DoesNotContain(vm.MirrorSources, t => t.ScreenId == "a");
            Assert.DoesNotContain(vm.MirrorSources, t => t.ScreenId == CanvasKey); // a is inside it

            // A repeater of the canvas draws the canvas's own picture, has none of its own, and says so on the wall.
            vm.SelectedPlacement = c;
            vm.SelectedMirrorOf = CanvasKey;
            Assert.Equal(CanvasKey, c.MirrorOf);
            Assert.False(c.UseCustomPattern);
            Assert.True(vm.SwitcherTiles[2].IsMirror);
            Assert.StartsWith("↳", vm.SwitcherTiles[2].FootText);
            vm.SwitcherTiles[1].IsOwn = true; // canvas A gets its own picture
            vm.ActivePattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Focus, b.Services.Bus.Current.PatternFor(CanvasKey).Kind);
            Assert.Equal(PatternKind.Focus, b.Services.Bus.Current.PatternFor("c").Kind);
            Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.State.Pattern.Kind);

            // Back to its own content: it follows the program again.
            vm.SelectedMirrorOf = "";
            Assert.Equal("", c.MirrorOf);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.PatternFor("c").Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ALockArrivesFromTheRemoteAndCompanionSeesIt()
    {
        var b = TestApp.Boot();
        try
        {
            Rig(b);
            var (services, vm, _) = b;
            var router = new CommandRouter(services);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LOCK 3 ON"))));
            var c = vm.State.Output.Placements.First(p => p.ScreenId == "c");
            Assert.False(c.FollowsCues);
            var json = router.StateJson();
            Assert.Contains("\"locked\":true", json);
            Assert.Contains("\"role\":\"main\"", json);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LOCK 3 OFF"))));
            Assert.True(c.FollowsCues);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LOCK 3"))));
            Assert.False(c.FollowsCues);
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LOCK 9 ON"))));

            // A cue action by id, and a canvas key, go through the same seam.
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.ScreenUnlock, "c"), ActionOrigin.Desk).Ok);
            Assert.True(c.FollowsCues);
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.ScreenLock, CanvasKey), ActionOrigin.Desk).Ok);
            Assert.True(ScreenRoles.IsLocked(vm.State, CanvasKey));
            Assert.True(vm.SwitcherTiles[1].IsLocked);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void SendOnATilePutsThePreviewThereAlone()
    {
        var b = TestApp.Boot();
        try
        {
            Rig(b);
            var vm = b.Vm;
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.Focus;
            vm.SwitcherTiles[2].SendHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsSandboxActive);                                  // EDIT SAFE re-armed
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);             // the program went back
            Assert.True(vm.State.Output.Placements.First(p => p.ScreenId == "c").UseCustomPattern);
            Assert.Equal(PatternKind.Focus, b.Services.Bus.Current.PatternFor("c").Kind);
            Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.PatternFor(CanvasKey).Kind);
            Assert.Contains("Sent to 3", vm.StatusMessage);

            // Without the sandbox, SEND explains itself instead of doing nothing.
            vm.IsSandboxActive = false;
            vm.SwitcherTiles[2].SendHereCommand.Execute(null);
            Assert.Contains("EDIT SAFE", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }
}
