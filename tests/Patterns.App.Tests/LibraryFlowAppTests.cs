using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 67.4: a library tile clicked is the editing target's picture at once — EDIT SAFE opens first so
/// the air never moves, the target tile's PVW shows it on the same publish, a target that followed the
/// programme is its own picture from that press, and the tile stays lit on the page.
/// </summary>
public class LibraryFlowAppTests
{
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

    [AvaloniaFact]
    public void ALibraryTileLandsInTheEditingTargetsPreviewUnderEditSafeAndLightsUp()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            var air = services.Bus.Current;
            Assert.Equal(PatternKind.Grid, air.PatternFor("b").Kind);

            // The right tile selected, EDIT SAFE off: a built-in pattern preset from the library.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles.Single(t => t.TargetId == "b"));
            var preset = vm.LibraryAll.First(i => i.Section == "Patterns" && i.ThumbConfig?.Invoke(vm.State)?.Kind is { } k && k != PatternKind.Grid);
            vm.ApplyPresetCommand.Execute(preset);
            Dispatcher.UIThread.RunJobs();

            var why = $"sandbox {services.Sandbox.Active} · own {ContentTargets.UsesOwnPattern(vm.State, "b")} · target {vm.EditTarget} · status '{vm.StatusMessage}' · item {preset.Section}/{preset.Name} · active kind {vm.ActivePattern.Kind}";
            Assert.True(vm.IsSandboxActive, "EDIT SAFE opened first: " + why);
            Assert.True(ContentTargets.UsesOwnPattern(vm.State, "b"), why);                    // its own from this press
            Assert.NotEqual(PatternKind.Grid, services.Bus.Sandbox!.PatternFor("b").Kind);      // the tile's PVW shows it
            Assert.Equal(PatternKind.Grid, services.Bus.Sandbox!.PatternFor("a").Kind);         // the other tile does not
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);                              // the programme's preview untouched
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);          // the air never moved
            Assert.Same(preset, vm.SelectedLibraryItem);
            Assert.True(preset.IsSelected);
            Assert.Contains(preset.Name, vm.StatusMessage);
            Assert.Contains("Right", vm.StatusMessage);
            Assert.Contains("CUT or TAKE", vm.StatusMessage);

            // Another tile chosen: the light moves.
            var other = vm.LibraryAll.First(i => i.Section == "Patterns" && !ReferenceEquals(i, preset) && i.ThumbConfig?.Invoke(vm.State)?.Kind is { } k2 && k2 != PatternKind.Grid);
            vm.ApplyPresetCommand.Execute(other);
            Dispatcher.UIThread.RunJobs();
            Assert.False(preset.IsSelected);
            Assert.True(other.IsSelected);

            // With the PGM tile selected the library lands on the programme's preview, and says so.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles.Single(t => t.IsProgramTile));
            vm.ApplyPresetCommand.Execute(preset);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("programme", vm.StatusMessage);
            Assert.NotEqual(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);           // still not on air
        }
        finally
        {
            b.Dispose();
        }
    }
}
