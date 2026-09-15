using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 60: the right-click menus on the desk. Built by the window's view model for a tile, a
/// cue row, a look, a lower third, a layer, an overlay; every picture change lands in the preview
/// with EDIT SAFE opened first and the air untouched; the cue stack's edits land on the stack;
/// a page opens with its item; and the flyout of the desk's own builds at the right-click and
/// closes on a choice.
/// </summary>
public class DeskMenuAppTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new Avalonia.PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new Avalonia.PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
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

    private static LookConfig SaveLook(MainViewModel vm, string name, PatternKind kind)
    {
        vm.ActivePattern.Kind = kind;
        vm.Show.NewLookName = name;
        vm.Show.SaveLookCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return LookService.Find(vm.State, name) ?? throw new InvalidOperationException($"look '{name}' was not saved");
    }

    private static PatternKind? OwnKind(ShowState state, string screenId)
        => state.Independent.FirstOrDefault(a => a.ScreenId == screenId)?.Pattern.Kind;

    private static TestApp.Booted BootLive()
    {
        var b = TestApp.Boot();
        b.Vm.IsSandboxActive = false;
        b.Vm.State.Transition.Enabled = false;
        b.Vm.State.Switcher.EditSafeByDefault = false;
        Rig(b);
        return b;
    }

    private static void Choose(DeskMenuVm menu, string id)
    {
        var entry = menu.Find(id) ?? throw new InvalidOperationException($"no entry '{id}' in the {menu.Menu.Kind} menu");
        Assert.True(entry.IsEnabled, $"{id}: {entry.Because}");
        entry.ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheTileMenuStagesInThePreviewTakesSwitchesTheTileAndGoesToItsPage()
    {
        var b = BootLive();
        try
        {
            var (services, vm, _) = b;
            var walkIn = SaveLook(vm, "Walk-in", PatternKind.Grid);
            Assert.True(services.Actions.ApplyLook(walkIn, ActionOrigin.Desk, cut: true).Ok);
            Dispatcher.UIThread.RunJobs();
            var tile = vm.SwitcherTiles.First(t => t.TargetId == "b");
            var airBefore = LookService.Fingerprint(services.AirState);

            var menu = vm.MenuFor("tile", tile)!;
            Assert.Equal("screen", menu.Menu.Kind);
            Assert.StartsWith("2 · ", menu.Title);
            Assert.Equal(4, menu.Groups.Count);
            Assert.False(menu.Find("stage.reset")!.IsEnabled); // it shows the look exactly as asked
            Assert.Contains("exactly as the look asked", menu.Find("stage.reset")!.Because);
            var closed = false;
            menu.Chosen += () => closed = true;

            // A kind of picture: staged on the tile's PVW, EDIT SAFE opened, the air untouched, the tile focused for a FOCUSED take.
            Choose(menu, "stage.kind:ColorBars");
            Assert.True(closed);
            Assert.True(services.Sandbox.Active);
            Assert.True(services.Sandbox.IsStaged("b"));
            Assert.Equal(PatternKind.ColorBars, OwnKind(services.State, "b"));
            Assert.Equal(airBefore, LookService.Fingerprint(services.AirState));
            Assert.True(tile.IsSelected);
            Assert.Contains("staged", vm.StatusMessage);

            // The PGM tile's menu: TAKE, enabled now that EDIT SAFE is open, is what puts it up.
            var pgm = vm.SwitcherTiles.First(t => t.IsProgramTile);
            var pm = vm.MenuFor("tile", pgm)!;
            Assert.Equal("program", pm.Menu.Kind);
            Assert.Contains("Walk-in", pm.Subtitle);
            Choose(pm, "take");
            Assert.Equal(PatternKind.ColorBars, OwnKind(services.AirState, "b"));
            Assert.Equal(PatternKind.Grid, services.AirState.Pattern.Kind);

            // The look back on air, then screen 2 sent its own way live (a cue, a SEND, a remote's SCREEN 2 PATTERN):
            // off the look now, RESET is offered, and it puts the look's own picture back on the PVW only.
            Assert.True(services.Actions.ApplyLook(walkIn, ActionOrigin.Desk, cut: true).Ok);
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.ScreenPattern, "2", "ColorBars"), ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.LookTally.IsOffLook("b"));
            var again = vm.MenuFor("tile", tile)!;
            Assert.Contains("off the look", again.Subtitle);
            Assert.True(again.Find("stage.reset")!.IsEnabled);
            Choose(again, "stage.reset");
            Assert.True(services.Sandbox.Active);
            Assert.Equal(PatternKind.Grid, OwnKind(services.State, "b"));
            Assert.Equal(PatternKind.ColorBars, OwnKind(services.AirState, "b"));

            // The tile's own switches are live: LOCK, then ARM says why it cannot.
            Choose(vm.MenuFor("tile", tile)!, "tile.lock");
            Assert.True(ScreenRoles.IsLocked(vm.State, "b"));
            tile = vm.SwitcherTiles.First(t => t.TargetId == "b"); // the wall may have been rebuilt under the menu; the fact is the state's
            Assert.True(tile.IsLocked);
            var locked = vm.MenuFor("tile", tile)!;
            Assert.False(locked.Find("tile.arm")!.IsEnabled);
            Assert.Contains("locked tile", locked.Find("tile.arm")!.Because);
            Assert.Contains("Unlock", locked.Find("tile.lock")!.Text);

            // GO TO: the Screens page with this screen selected.
            Choose(locked, "go.screens");
            Assert.Equal(Shell.IndexOf("Screens"), vm.SelectedPageIndex);
            Assert.Equal("b", vm.Screens.SelectedPlacement?.ScreenId);

            // ASK without a key says so and is disabled; with none saved the desk never sends.
            Assert.Contains("API key", vm.MenuFor("tile", tile)!.Find("ask.screen")!.Because);

            // The wire line on every staged entry is the desk's own verb.
            Assert.Equal("SCREEN 2 PVW PATTERN ColorBars", vm.MenuFor("tile", tile)!.Find("stage.kind:ColorBars")!.Wire);
        }
        finally
        {
            b.Window.Close();
        }
    }

    [AvaloniaFact]
    public void TheCueMenuEditsTheStackFromTheRunSurfaceAndTheCuesPage()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var design = vm.NewLowerThird("Clean");
            var look = SaveLook(vm, "Walk-in", PatternKind.Grid);
            var first = vm.Cues.AddCue();
            first.Name = "Doors";
            var second = vm.Cues.AddCue();
            second.Name = "Keynote";
            vm.Run.Refresh();
            Dispatcher.UIThread.RunJobs();
            var row = vm.Run.Rows.First(r => ReferenceEquals(r.Cue, second));

            var menu = vm.MenuFor("cue", row)!;
            Assert.Equal("cue", menu.Menu.Kind);
            Assert.Contains("Keynote", menu.Title);
            Choose(menu, $"cue.look:{look.Id}");
            Assert.Equal(look.Id, CueMenuEdits.LookStep(second)!.Target);
            Assert.Contains("recalls 'Walk-in'", vm.StatusMessage);

            Choose(vm.MenuFor("cue", row)!, "cue.transition:cut");
            Assert.Equal("cut", CueMenuEdits.Transition(second));
            Choose(vm.MenuFor("cue", row)!, $"cue.overlay:{ShowActionKind.ClockOn}");
            Assert.True(CueMenuEdits.HasStep(second, ShowActionKind.ClockOn));
            Choose(vm.MenuFor("cue", row)!, $"cue.lt:{design.Id}:3:8");
            Assert.Equal((design.Id, 3d, (double?)8d), CueMenuEdits.LowerThird(second));
            Assert.Contains("in after 3 s, out 8 s later", vm.MenuFor("cue", row)!.Find("cue.lt")!.Text);
            Choose(vm.MenuFor("cue", row)!, "cue.follow:5");
            Assert.Equal(5, second.FollowSeconds);
            Assert.Contains("AUTO", row.FollowTag);

            // The run verbs: standby moves; the editor opens on the cue.
            Choose(vm.MenuFor("cue", row)!, "cue.standby");
            Assert.Same(second, services.CueStack.StandbyCue);
            Assert.False(vm.MenuFor("cue", row)!.Find("cue.standby")!.IsEnabled);
            Choose(vm.MenuFor("cue", row)!, "cue.edit");
            Assert.Equal(Shell.IndexOf("Cues"), vm.SelectedPageIndex);
            Assert.Same(second, vm.Cues.SelectedCue);

            // The Cues page's rows carry the same menu.
            var cueRow = vm.Cues.Rows.First(r => ReferenceEquals(r.Cue, first));
            Choose(vm.MenuFor("cue", cueRow)!, "cue.mark:Break");
            Assert.Equal(CueMark.Break, first.Mark);
            Choose(vm.MenuFor("cue", cueRow)!, "cue.skip");
            Assert.False(first.Enabled);
            Assert.Contains("Back in the run", vm.MenuFor("cue", cueRow)!.Find("cue.skip")!.Text);
        }
        finally
        {
            b.Window.Close();
        }
    }

    [AvaloniaFact]
    public void TheLookLowerThirdLayerAndOverlayMenusEditTheShowAndThePreviewOnly()
    {
        var b = BootLive();
        try
        {
            var (services, vm, _) = b;
            var a = SaveLook(vm, "Walk-in", PatternKind.Grid);
            var k = SaveLook(vm, "Keynote", PatternKind.ColorBars); // the air is ColorBars now

            Choose(vm.MenuFor("look", a)!, "look.hotkey:5");
            Assert.Equal(5, a.Hotkey);
            Choose(vm.MenuFor("look", k)!, "look.hotkey:5");
            Assert.Equal(5, k.Hotkey);
            Assert.Equal(0, a.Hotkey); // the key moved

            Choose(vm.MenuFor("look", a)!, "look.preview");
            Assert.True(services.Sandbox.Active);
            Assert.Equal(a.Id, services.PreviewLookId);
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);          // the preview
            Assert.Equal(PatternKind.ColorBars, services.AirState.Pattern.Kind); // the air
            Assert.True(vm.MenuFor("look", a)!.Find("look.preview")!.IsOn);

            var d1 = vm.NewLowerThird("Clean");
            var d2 = vm.NewLowerThird("Clean");
            Choose(vm.MenuFor("lowerthird", d2)!, "lt.default");
            Assert.Equal(d2.Id, vm.State.LowerThirds.DefaultDesignId);
            Assert.False(vm.MenuFor("lowerthird", d2)!.Find("lt.default")!.IsEnabled);
            Assert.True(vm.MenuFor("lowerthird", d1)!.Find("lt.default")!.IsEnabled);
            Choose(vm.MenuFor("lowerthird", d1)!, "lt.preview");
            Assert.True(services.LowerThirdInPreview());
            Assert.False(services.AirState.LowerThirds.IsShowing);

            // A layer and an overlay: the preview's, never the air's.
            Choose(vm.MenuFor("layer", "layer1")!, "layer.source:1:Video");
            Assert.Equal(LayerSource.Video, vm.ActivePattern.Layer1.Source);
            Assert.True(vm.ActivePattern.Layer1.Enabled);
            Assert.False(services.AirState.Pattern.Layer1.Enabled);
            Choose(vm.MenuFor("overlay", "clock")!, "overlay.on:clock");
            Assert.True(vm.State.Overlays.Clock.Enabled);
            Assert.False(services.AirState.Overlays.Clock.Enabled);
            // …and the one red entry does go to air, saying so.
            var clock = vm.MenuFor("overlay", "clock")!;
            Assert.Equal("On air now", clock.Find("overlay.air:clock")!.Text);
            Assert.Equal("CLOCK ON", clock.Find("overlay.air:clock")!.Wire);
            Choose(clock, "overlay.air:clock");
            Assert.True(services.AirState.Overlays.Clock.Enabled);
            Choose(vm.MenuFor("countdown", null)!, "countdown.start:5");
            Assert.True(vm.State.Countdown.Enabled);
            Assert.Equal(5, vm.State.Countdown.DurationMinutes);
            Assert.False(services.AirState.Countdown.Enabled);
        }
        finally
        {
            b.Window.Close();
        }
    }

    [AvaloniaFact]
    public void TheFlyoutAttachesBuildsAtTheRightClickAndClosesOnAChoice()
    {
        var b = TestApp.Boot();
        Window? window = null;
        try
        {
            var vm = b.Vm;
            Rig(b);
            var tile = vm.SwitcherTiles.First(t => t.TargetId == "b");
            var border = new Border { DataContext = tile };
            Menus.SetKind(border, "tile");
            var flyout = Menus.FlyoutOf(border)!;
            Assert.Null(border.ContextFlyout);
            var other = new Border { DataContext = "nothing the desk has a menu for" };
            Menus.SetKind(other, "tile");
            window = new Window { DataContext = vm, Content = new StackPanel { Children = { border, other } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Nothing is built until the right-click asks; then the menu is the tile's.
            Assert.Null(flyout.Content);
            Assert.True(Menus.Prepare(border));
            var view = Assert.IsType<DeskMenuControl>(flyout.Content);
            var menu = Assert.IsType<DeskMenuVm>(view.DataContext);
            Assert.Equal("screen", menu.Menu.Kind);
            flyout.ShowAt(border);
            Dispatcher.UIThread.RunJobs();
            Assert.True(flyout.IsOpen);

            // A drawer opens beside the menu; a choice runs and closes the flyout.
            menu.Find("stage.kind")!.ChooseCommand.Execute(null);
            Assert.True(menu.HasOpen);
            Assert.Equal("Pattern", menu.OpenHeading);
            Assert.True(flyout.IsOpen);
            menu.Find("go.looks")!.ChooseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(flyout.IsOpen);
            Assert.Equal(Shell.IndexOf("Looks"), vm.SelectedPageIndex);

            // A thing the host has no menu for opens nothing.
            Assert.False(Menus.Prepare(other));
            Assert.Null(Menus.FlyoutOf(other)!.Content);
        }
        finally
        {
            window?.Close();
            b.Window.Close();
        }
    }
}
