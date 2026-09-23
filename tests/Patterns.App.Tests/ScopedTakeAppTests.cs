using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 81, from the rig: "LOCK still means LOCK and no take can happen on a screen or screen tile
/// that has LOCK selected." Every way a take can reach a screen on a live desk — FOCUSED, the tile's
/// own TAKE and CUT, TICKED, TICKED GROUPS, a group by kind, SCREEN n on the wire, ALL ARMED — a
/// locked screen keeps its picture, the words name it with the reason, nothing is spent, and the
/// moment it is unlocked the same press lands. The rig: two main screens and a confidence screen.
/// </summary>
public class ScopedTakeAppTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    private static void Rig(TestApp.Booted b)
    {
        var fakes = ThreeScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.State.Output.Placements.First(p => p.ScreenId == "c").Role = ScreenRole.Confidence;
        b.Vm.RebuildSwitcherTiles();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static bool Pinned(AppServices services, string target)
        => services.AirState.Independent.FirstOrDefault(x => x.ScreenId == target) is { PinnedByTake: true };

    [AvaloniaFact]
    public void ALockedScreenIsNeverTakenByAnyScopeAndTheWordsSayWhy()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;          // the programme: a grid everywhere
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.ColorBars;     // the preview: bars
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Settle();

            SwitcherTile Tile(string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);
            const string lockedWords = "2 · Right is locked — it keeps its picture. Unlock it (LOCK on its tile";

            // LOCK on the second tile, through the action layer (the same verb LOCK 2 ON runs on the wire).
            Tile("b").IsLocked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(ScreenRoles.IsLocked(vm.State, "b"));
            Assert.True(Tile("b").IsLocked);

            // FOCUSED on the locked tile: refused with the reason and the way out; the air, the preview and EDIT SAFE untouched.
            vm.SelectTileCommand.Execute(Tile("b"));
            vm.SelectedTakeScope = vm.TakeScopes[1];
            var version = services.Bus.Current.Version;
            vm.TakeCommand.Execute(null);
            Assert.StartsWith(lockedWords, vm.StatusMessage);
            vm.CutCommand.Execute(null);
            Assert.StartsWith(lockedWords, vm.StatusMessage);
            Assert.Equal(version, services.Bus.Current.Version);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);
            Assert.True(Tile("b").IsOwn, "LOCK keeps what the screen shows: its picture is pinned as its own (round 67)");
            Assert.True(vm.IsSandboxActive);
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);

            // The tile's own TAKE and CUT are no way round the lock either (round 67), on the desk or as SCREEN 2 TAKE on the wire.
            Tile("b").TakeHereCommand.Execute(null);
            Assert.StartsWith(lockedWords, vm.StatusMessage);
            Tile("b").CutHereCommand.Execute(null);
            Assert.StartsWith(lockedWords, vm.StatusMessage);
            var wire = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.False(wire.Ok);
            Assert.StartsWith(lockedWords, wire.Message);
            Assert.Equal(version, services.Bus.Current.Version);

            // TICKED with the locked tile among the ticks: the other lands alone, as its own; the locked one is held by name,
            // its picture untouched and nothing pinned by the take.
            Tile("a").IsSendTarget = true;
            Tile("b").IsSendTarget = true;
            vm.SelectedTakeScope = vm.TakeScopes[2];
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var air = services.Bus.Current;
            Assert.Equal(PatternKind.ColorBars, air.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("c").Kind);
            Assert.Equal(PatternKind.Grid, air.State.Pattern.Kind);
            Assert.True(Tile("a").IsOwn);
            Assert.False(Pinned(services, "b"));
            Assert.Contains("on 1 · Left alone", vm.StatusMessage);
            Assert.Contains("held: 2 · Right (locked)", vm.StatusMessage);

            // MAIN SCREENS (a group by kind): the same — the unlocked main screen takes the new preview, the locked one is held.
            vm.SelectedTakeScope = vm.TakeScopes[4];
            vm.State.Pattern.Kind = PatternKind.Focus;
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            Assert.Equal(PatternKind.Focus, air.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("b").Kind);
            Assert.Contains("held: 2 · Right (locked)", vm.StatusMessage);

            // A group whose only screen is locked names nothing to take: CONFIDENCE SCREENS with the lobby locked, and TICKED
            // GROUPS with that lobby ticked, are refused with its name; the tick is not spent.
            Tile("c").IsLocked = true;
            Dispatcher.UIThread.RunJobs();
            vm.SelectedTakeScope = vm.TakeScopes[5];
            version = services.Bus.Current.Version;                                                // read after the scope change: the picker's plan is published for the multiview
            vm.TakeCommand.Execute(null);
            Assert.StartsWith("3 · Lobby is locked — it keeps its picture.", vm.StatusMessage);
            Assert.Equal(version, services.Bus.Current.Version);
            Tile("c").IsSendTarget = true;                                                          // a tick is published for the deck's lights: the air is what must not move
            vm.SelectedTakeScope = vm.TakeScopes[3];
            vm.TakeCommand.Execute(null);
            Assert.StartsWith("3 · Lobby is locked — it keeps its picture.", vm.StatusMessage);
            Assert.True(Tile("c").IsSendTarget, "a refused take spends no tick");
            Tile("c").IsSendTarget = false;
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("c").Kind);
            Tile("c").IsLocked = false;                                                             // unlocking never moves a picture: the lobby keeps the grid as its own
            Dispatcher.UIThread.RunJobs();
            Tile("c").ProgramCommand.Execute(null);                                                 // PROGRAM puts it back on the programme
            Dispatcher.UIThread.RunJobs();
            Assert.False(Tile("c").IsOwn);

            // SCREEN 2 as the wire and a cue write it: refused the same way.
            version = services.Bus.Current.Version;
            var screen = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "SCREEN 2");
            Assert.False(screen.Ok);
            Assert.StartsWith(lockedWords, screen.Message);
            Assert.Equal(version, services.Bus.Current.Version);

            // ALL ARMED: the programme moves and the screen that follows it moves; the locked one keeps its picture and the
            // OWN one keeps its own (round 80: said before the press).
            vm.SelectedTakeScope = vm.TakeScopes[0];
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            Assert.Equal(PatternKind.ColorBars, air.State.Pattern.Kind);
            Assert.Equal(PatternKind.ColorBars, air.PatternFor("c").Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Focus, air.PatternFor("a").Kind);

            // Unlocked, the same press lands. Unlocking never moves a picture (the grid stays as its own until PROGRAM puts
            // it back on the programme); then FOCUSED on the second tile puts the preview up there alone, as its own.
            Tile("b").IsLocked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(ScreenRoles.IsLocked(vm.State, "b"));
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);
            Tile("b").ProgramCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("b").Kind);
            Assert.False(Tile("b").IsOwn);
            vm.SelectTileCommand.Execute(Tile("b"));
            vm.SelectedTakeScope = vm.TakeScopes[1];
            vm.State.Pattern.Kind = PatternKind.Focus;
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            Assert.Equal(PatternKind.Focus, air.PatternFor("b").Kind);
            Assert.Equal(PatternKind.ColorBars, air.State.Pattern.Kind);
            Assert.Equal(PatternKind.ColorBars, air.PatternFor("c").Kind);
            Assert.True(Tile("b").IsOwn);
            Assert.StartsWith("TAKE — the preview fades up on 2 · Right alone, as its own picture; the programme and every other screen stay.", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }
}
