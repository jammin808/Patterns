using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 15: "Unless a screen is selected, the default editing target should be Program. At the
/// moment sometimes the editing target is blank and I have to reselect it." The EDITING TARGET
/// picker on the Pattern page is bound to a list the desk rebuilds on every rig change; a rebuild
/// that kept the same target re-added it as an equal record, the setter saw no change and never
/// told the picker, and the picker — emptied by the rebuild — stayed blank.
/// </summary>
public class EditTargetAppTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new Avalonia.PixelRect(1920, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new Avalonia.PixelRect(4400, 0, 1920, 1080), 1.0, false, 2),
    };

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
        bb.X = 3000; bb.Y = 0;   // apart: three single screens, no canvas
        c.X = 6000; c.Y = 0;
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static ComboBox Picker(Window window, MainViewModel vm)
        => window.GetVisualDescendants().OfType<ComboBox>().First(c => ReferenceEquals(c.ItemsSource, vm.EditTargets));

    [AvaloniaFact]
    public void TheEditingTargetPickerNeverGoesBlankOnARebuild()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.SelectPage(Shell.IndexOf("Pattern"));
            Settle(window);

            // Program alone: the picker is hidden and the target is Program.
            Assert.False(vm.ShowEditTargets);
            Assert.Null(vm.EditTarget.ScreenId);
            Assert.StartsWith("EDITING: PROGRAM", vm.EditTargetBanner);

            // OWN on the lobby: it becomes the target and the picker shows it.
            var lobby = vm.SwitcherTiles.First(t => t.TargetId == "c");
            lobby.IsOwn = true;
            Settle(window);
            Assert.True(vm.ShowEditTargets);
            Assert.Equal("c", vm.EditTarget.ScreenId);
            var picker = Picker(window, vm);
            Assert.True(picker.IsEffectivelyVisible);
            Assert.Same(vm.EditTarget, picker.SelectedItem);

            // A rebuild that keeps the target — a lock on another tile, a label typed for a screen — used to
            // empty the picker: the target is the same, so nothing was raised, and the picker stayed blank.
            vm.SwitcherTiles.First(t => t.TargetId == "a").IsLocked = true;
            Settle(window);
            Assert.Equal("c", vm.EditTarget.ScreenId);
            Assert.NotNull(picker.SelectedItem);
            Assert.Same(vm.EditTarget, picker.SelectedItem);
            Assert.Contains(vm.EditTargets, t => ReferenceEquals(t, vm.EditTarget));   // the target is the list's own instance

            vm.SelectedPlacement = vm.State.Output.Placements.First(p => p.ScreenId == "c");
            vm.SelectedScreenLabel = "Foyer wall";
            Settle(window);
            Assert.Equal("c", vm.EditTarget.ScreenId);
            Assert.Contains("Foyer wall", vm.EditTarget.Label);                        // the fresh label
            Assert.Same(vm.EditTarget, picker.SelectedItem);
            Assert.Contains("FOYER WALL", vm.EditTargetBanner.ToUpperInvariant());

            // A second own screen and a rebuild that keeps the lobby: still the lobby, still shown.
            vm.SwitcherTiles.First(t => t.TargetId == "b").IsOwn = true;
            Settle(window);
            Assert.Equal("b", vm.EditTarget.ScreenId);                                  // OWN hands the new one to the editors
            Assert.Same(vm.EditTarget, picker.SelectedItem);
            picker.SelectedItem = vm.EditTargets.First(t => t.ScreenId == "c");           // the operator picks the lobby back
            Settle(window);
            Assert.Equal("c", vm.EditTarget.ScreenId);
            vm.SwitcherTiles.First(t => t.TargetId == "a").IsLocked = false;
            Settle(window);
            Assert.Equal("c", vm.EditTarget.ScreenId);
            Assert.Same(vm.EditTarget, picker.SelectedItem);

            // The target loses its own pattern: the editors fall back to Program, never to nothing.
            lobby.IsOwn = false;
            Settle(window);
            Assert.Null(vm.EditTarget.ScreenId);
            Assert.StartsWith("EDITING: PROGRAM", vm.EditTargetBanner);
            Assert.True(vm.ShowEditTargets);                                            // b still has its own
            Assert.Same(vm.EditTarget, picker.SelectedItem);
            Assert.Equal("Program", ((EditTarget)picker.SelectedItem!).Label);

            // Every own pattern gone (the lock gave the first screen its own, and keeps it after the unlock):
            // Program, the picker hidden, the target never null.
            foreach (var tile in vm.SwitcherTiles.Where(t => t.IsOwn).ToList()) tile.IsOwn = false;
            Settle(window);
            Assert.False(vm.ShowEditTargets);
            Assert.Single(vm.EditTargets);
            Assert.NotNull(vm.EditTarget);
            Assert.Null(vm.EditTarget.ScreenId);
            Assert.Same(vm.EditTargets[0], vm.EditTarget);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>The picker's binding writes its empty selection back while the list is rebuilt: the desk refuses it and keeps the target.</summary>
    [AvaloniaFact]
    public void AnEmptySelectionFromThePickerIsRefused()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.SwitcherTiles.First(t => t.TargetId == "c").IsOwn = true;
            Settle(window);
            Assert.Equal("c", vm.EditTarget.ScreenId);

            var before = vm.EditTarget;
            vm.EditTarget = null!;
            Assert.Same(before, vm.EditTarget);
            Assert.Equal("c", vm.EditTarget.ScreenId);
        }
        finally
        {
            b.Dispose();
        }
    }
}
