using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

public class RigDayAppTests
{
    [AvaloniaFact]
    public void RigDaysGamesAreOffUntilAskedAndTheAlignmentGameDrivesALatticeOntoTheSolversTargets()
    {
        var b = TestApp.Boot("patterns-tests-rigday-");
        try
        {
            var (services, vm, _) = b;
            var router = new CommandRouter(services);
            string Wire(string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));
            var rig = services.RigDay;

            // Off by default: nothing shows, and the game refuses.
            Assert.False(rig.Enabled);
            Assert.Contains("\"enabled\":false", Wire("RIGDAY STATUS"));
            Assert.Equal("", rig.ReadyWords);
            Assert.Equal("", vm.RigDayWords);
            Assert.StartsWith("ERR", Wire("ALIGN START"));
            Assert.Contains("RIGDAY ON first", Wire("ALIGN START"));
            rig.RecordGo(TimeSpan.Zero);
            Assert.False(vm.HasStreak);

            // On: the bar from the facts — outputs closed, no joins on this rig, projectors uncalibrated, no lock, no check.
            Assert.StartsWith("OK", Wire("RIGDAY ON"));
            Assert.True(rig.Enabled);
            Assert.True(vm.State.Install.RigDayGames);
            rig.Poll();
            Assert.StartsWith("show-ready ", rig.ReadyWords);
            Assert.NotNull(rig.Ready);
            Assert.False(rig.Ready!.IsFull);
            Assert.Contains(rig.Ready.Steps, s => s.Name == "Outputs" && !s.Done);
            Assert.Contains(rig.Ready.Steps, s => s.Name == "Calibration" && !s.Done);
            Assert.Contains("show-ready", vm.RigDayWords);
            Assert.Contains("\"enabled\":true", Wire("RIGDAY STATUS"));
            Assert.StartsWith("Blend Quest:", vm.QuestWords);

            // The streak counts a person's GO against the plan.
            rig.RecordGo(TimeSpan.FromSeconds(4));
            rig.RecordGo(TimeSpan.FromSeconds(-10));
            Assert.Equal("2 on time in a row · best 2", vm.StreakWords);
            Assert.True(vm.HasStreak);

            // A calibration (the demo) gives every projector a solved mesh: the game's targets.
            Assert.StartsWith("OK", Wire("CALIBRATE DEMO"));
            Assert.NotNull(services.Calibration.Solution);
            var projector = services.Calibration.Projectors()[0];
            Assert.StartsWith("ERR", Wire("ALIGN START nowhere"));
            var started = Wire($"ALIGN START {projector.ScreenId}");
            Assert.StartsWith("OK", started);
            var game = rig.Align!;
            Assert.NotNull(game);
            Assert.Equal(projector.ScreenId, game.ScreenId);
            Assert.Equal(projector.ScreenId, services.RigEditor.LatticeOn);
            Assert.Equal(game.Picked, services.RigEditor.LatticePoint);
            Assert.Equal(game.NodeCount, services.RigEditor.LatticeTargets!.Count);
            Assert.False(game.IsDone);                                                   // the solved mesh is not the rest mesh
            var status = Wire("ALIGN STATUS");
            Assert.Contains("\"align\":{", status);
            Assert.Contains($"\"screen\":\"{projector.ScreenId}\"", status);

            // A nudge moves the placement's mesh; a snap locks the node and the game walks on; the keys are the same verbs.
            var picked = game.Picked;
            var before = projector.WarpMesh;
            Assert.StartsWith("OK", Wire("ALIGN NUDGE 3 0"));
            Assert.NotEqual(before, projector.WarpMesh);
            Assert.Equal(WarpGrid.Parse(projector.WarpMesh, projector.WarpMeshColumns, projector.WarpMeshRows)[picked * 2], game.Current[picked * 2], 0.01f);
            Assert.StartsWith("ERR", Wire("ALIGN NUDGE x"));
            var lockedBefore = game.LockedCount;
            Assert.StartsWith("OK", Wire("ALIGN SNAP"));
            Assert.True(game.IsLocked(picked));
            Assert.Equal(lockedBefore + 1, game.LockedCount);
            Assert.NotEqual(picked, game.Picked);                                        // walked on to the next open node
            Assert.True(vm.IsAligning);
            vm.SelectPage(Shell.IndexOf("Screens"));
            Assert.True(vm.IsScreensPage);
            Assert.True(vm.AlignKey(Key.Right, shift: true));
            Assert.True(vm.AlignKey(Key.Tab, shift: false));
            Assert.False(vm.AlignKey(Key.F1, shift: false));
            Assert.StartsWith("OK", Wire("ALIGN PREV"));
            Assert.StartsWith("OK", Wire("ALIGN NEXT"));
            Assert.Contains("locked", vm.AlignWords);

            // Stop: the lattice and its targets leave the projector; off: everything goes.
            Assert.StartsWith("OK", Wire("ALIGN STOP"));
            Assert.Null(rig.Align);
            Assert.Equal("", services.RigEditor.LatticeOn);
            Assert.Null(services.RigEditor.LatticeTargets);
            Assert.StartsWith("ERR", Wire("ALIGN STOP"));
            Assert.StartsWith("OK", Wire("RIGDAY OFF"));
            Assert.False(rig.Enabled);
            Assert.Equal("", rig.ReadyWords);
            Assert.Equal("", vm.StreakWords);
            Assert.Contains("\"enabled\":false", Wire("RIGDAY STATUS"));
        }
        finally
        {
            b.Dispose();
        }
    }
}
