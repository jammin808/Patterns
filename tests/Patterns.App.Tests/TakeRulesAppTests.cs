using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 67: the take rules enforced on a live desk — LOCKED means locked (the tile's own TAKE too),
/// FOCUSED on a held tile is a refusal that says why, nothing armed is a refusal and never a silent
/// take, a tick set prepared for a fade survives an ALL ARMED take, and the wall's words, the snapshot
/// and the multiview's NEXT TAKE line all read the one plan.
/// </summary>
public class TakeRulesAppTests
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
        b.Vm.RebuildSwitcherTiles(fakes);
        Dispatcher.UIThread.RunJobs();
    }

    private static SwitcherTile Tile(MainViewModel vm, string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);

    [AvaloniaFact]
    public void LockedMeansLockedAndAHeldScopeIsARefusalThatSaysWhy()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.Grid;
            services.Sandbox.SendAll();                                  // Grid everywhere is the programme
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.LedWall;                 // the next picture, in the preview
            Dispatcher.UIThread.RunJobs();
            static void StillGrid(AppServices s)
            {
                foreach (var id in new[] { "a", "b", "c" }) Assert.Equal(PatternKind.Grid, s.Bus.Current.PatternFor(id).Kind);
                Assert.Equal(PatternKind.Grid, s.AirState.Pattern.Kind);
            }

            // The tile's own TAKE on a locked screen is refused — the lock is not a suggestion.
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenLock, ActionOrigin.Desk, "b").Ok);
            var tileTake = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.False(tileTake.Ok);
            Assert.Contains("locked", tileTake.Message);
            Assert.Contains("LOCK", tileTake.Message);
            StillGrid(services);

            // FOCUSED on that locked tile: refused with the same reason; nothing moved, EDIT SAFE still open.
            vm.SelectTileCommand.Execute(Tile(vm, "b"));
            vm.SelectedTakeScope = vm.TakeScopes[1];
            Assert.True(vm.TakePlanIsRefusal);
            Assert.Contains("locked", vm.TakePlanText);
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("locked", vm.StatusMessage);
            StillGrid(services);
            Assert.True(services.Sandbox.Active);

            // FOCUSED on an un-armed tile: refused, and the words say what to press.
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenUnlock, ActionOrigin.Desk, "b").Ok);
            services.Arming.Set("b", false);
            vm.SelectTileCommand.Execute(Tile(vm, "b"));
            Assert.Contains("not armed", vm.TakePlanText);
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("ARM it", vm.StatusMessage);
            StillGrid(services);

            // Nothing armed at all: ALL ARMED is a refusal, never a take that changed nothing.
            vm.SelectedTakeScope = vm.TakeScopes[0];
            services.Arming.Set("a", false);
            services.Arming.Set("c", false);
            Assert.True(vm.TakePlanIsRefusal);
            Assert.StartsWith("Nothing is armed", vm.TakePlanText);
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("Nothing is armed", vm.StatusMessage);
            StillGrid(services);

            // Armed again: the plan says what the press will do, and the press does exactly that.
            services.Arming.ArmAll();
            Assert.False(vm.TakePlanIsRefusal);
            Assert.StartsWith("→ ", vm.TakePlanText);
            var taken = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, vm.SelectedTakeScope.Words);
            Dispatcher.UIThread.RunJobs();
            Assert.True(taken.Ok, $"{taken.Message} · sandbox {services.Sandbox.Active} · preview {vm.State.Pattern.Kind} · edit target {vm.EditTarget}");
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("c").Kind);
            // The lock pinned the right screen's picture as its own, and unlocking leaves it OWN with that
            // picture — a take never overwrites a screen's own picture; OWN off or Follow the programme does.
            Assert.True(ContentTargets.UsesOwnPattern(services.AirState, "b"));
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);
            Assert.True(Tile(vm, "b").IsOwn);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheWallTheSnapshotAndTheMultiviewReadOnePlanAndATickSurvivesATakeThatNeverReadIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.Grid;
            services.Sandbox.SendAll();
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();

            // ALL ARMED: every screen; the snapshot's held set is empty and the multiview names them all.
            vm.SelectedTakeScope = vm.TakeScopes[0];
            Assert.Equal("→ 1 · Left, 2 · Right, 3 · Lobby", vm.TakePlanText);
            Assert.NotNull(services.Bus.Current.TakeHeld);
            Assert.Empty(services.Bus.Current.TakeHeld!);
            Assert.Equal("NEXT TAKE → 1 · 2 · 3", MultiviewTally.PreviewTargets(services.Bus.Current, services.Bus.Sandbox));

            // FOCUSED on the right screen: the words, the held set and the multiview follow the click at once.
            vm.SelectTileCommand.Execute(Tile(vm, "b"));
            vm.SelectedTakeScope = vm.TakeScopes[1];
            Assert.Equal("→ 2 · Right · 2 outside the scope keep their picture", vm.TakePlanText);
            Assert.Equal(new[] { "a", "c" }, services.Bus.Current.TakeHeld!.OrderBy(x => x));
            Assert.Equal("NEXT TAKE → 2", MultiviewTally.PreviewTargets(services.Bus.Current, services.Bus.Sandbox));

            // TICKED follows the ticks as they are set — and a tick set is what a fade will use too.
            vm.SelectedTakeScope = vm.TakeScopes[2];
            Assert.Equal("Tick the wall tiles first.", vm.TakePlanText);
            Tile(vm, "a").IsSendTarget = true;
            Assert.Equal("→ 1 · Left · 2 outside the scope keep their picture", vm.TakePlanText);
            Assert.Equal(new[] { "b", "c" }, services.Bus.Current.TakeHeld!.OrderBy(x => x));

            // An ALL ARMED take leaves that tick exactly where the operator put it.
            vm.SelectedTakeScope = vm.TakeScopes[0];
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("c").Kind);
            Assert.True(Tile(vm, "a").IsSendTarget, "a tick the take never read is kept for the fade it was set for");

            // A TICKED take reads the ticks and spends them.
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            vm.SelectedTakeScope = vm.TakeScopes[2];
            vm.CutCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("b").Kind);
            Assert.False(Tile(vm, "a").IsSendTarget);
        }
        finally
        {
            b.Dispose();
        }
    }
}
