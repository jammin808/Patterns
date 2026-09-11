using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 17: "a feature to send any screen / canvas to PGM for editing." → PVW on a wall tile
/// loads the picture that target shows on air — its own pattern, its source's when it repeats one,
/// else the program — into the sandboxed preview, through the action layer; the air is untouched,
/// and EDIT SAFE opens first when it was off. Then SEND puts it back on a screen, or TAKE.
/// </summary>
public class TileToPreviewTests
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

    private static SwitcherTile Tile(MainViewModel vm, string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);

    /// <summary>Screen b with its own picture on air (Color bars) over a Grid program, the preview holding an edit of its own (Focus).</summary>
    private static void AirWithOwnPictureOnB(TestApp.Booted b)
    {
        var vm = b.Vm;
        vm.State.Pattern.Kind = PatternKind.Grid;
        vm.IsSandboxActive = true;
        Tile(vm, "b").IsOwn = true;                     // a copy of the program, and the editors on b
        vm.ActivePattern.Kind = PatternKind.ColorBars;  // b's own picture
        vm.SandboxSendAllCommand.Execute(null);         // to air: program Grid, b Color bars
        Dispatcher.UIThread.RunJobs();
        vm.EditTarget = vm.EditTargets[0];
        vm.State.Pattern.Kind = PatternKind.Focus;      // an edit in the preview, not on air
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.State.Pattern.Kind);
        Assert.Equal(PatternKind.ColorBars, b.Services.Bus.Current.PatternFor("b").Kind);
        Assert.Equal(PatternKind.Focus, vm.State.Pattern.Kind);
    }

    [AvaloniaFact]
    public void ATilesOwnPictureLoadsIntoThePreviewAndTheAirIsUntouched()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            AirWithOwnPictureOnB(b);
            vm.SelectTileCommand.Execute(Tile(vm, "c"));
            var air = services.Bus.Current.Version;

            Tile(vm, "b").ToPreviewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);                     // b's picture is the preview now
            Assert.Equal(PatternKind.ColorBars, services.Bus.Sandbox!.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);        // the air did not move
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);
            Assert.True(vm.IsSandboxActive);
            Assert.Null(vm.EditTarget.ScreenId);                                            // the editors work on the preview, not on b
            // The tile the picture came from stays focused, so the way back is the way it came:
            // SEND stages it there and CUT / TAKE with FOCUSED puts it up on that tile alone.
            // Clearing the focus here used to widen a FOCUSED take to the whole rig.
            Assert.Equal("b", vm.SelectedTargetId);
            Assert.Equal("", services.PreviewLookId);                                       // an edit, not a look
            Assert.Contains("in the preview", vm.StatusMessage);
            Assert.DoesNotContain("EDIT SAFE opened", vm.StatusMessage);

            // The picture is a copy: editing the preview leaves b's own picture on air alone.
            vm.State.Pattern.Kind = PatternKind.Geometry;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("b").Kind);

            // And the way back: SEND stages it on screen a, and a FOCUSED take puts it up there.
            Tile(vm, "a").SendHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("a", vm.SelectedTargetId);
            Assert.True(services.Sandbox.IsStaged("a"));
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);      // not on air yet
            Assert.True(services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "focused").Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Geometry, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Geometry, vm.State.Pattern.Kind);
            Assert.True(services.Bus.Current.Version > air);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AScreenOnTheProgramLoadsTheProgramsPictureAndARepeaterItsSources()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            AirWithOwnPictureOnB(b);

            // c follows the program: the program's picture on air (Grid), not the preview's edit (Focus).
            Tile(vm, "c").ToPreviewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);

            // c repeats b: b's own picture. (→ PVW reads the air, so the repeater goes there with a TAKE first —
            // the preview holds the program's Grid, so the program does not change.)
            vm.State.Output.Placements.First(p => p.ScreenId == "c").MirrorOf = "b";
            vm.SandboxSendAllCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            vm.RebuildSwitcherTiles();
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsSandboxActive);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("c").Kind);
            vm.State.Pattern.Kind = PatternKind.Focus;   // an edit in the fresh preview
            Tile(vm, "c").ToPreviewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);

            // Through the action layer: a screen by its wall number too, and a screen that is not there is refused.
            vm.State.Pattern.Kind = PatternKind.Focus;
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenToPreview, ActionOrigin.Desk, "3").Ok);
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
            var refused = services.Actions.Execute(ShowActionKind.ScreenToPreview, ActionOrigin.Desk, "nowhere");
            Assert.False(refused.Ok);
            Assert.Contains("No screen", refused.Message);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void WithEditSafeOffTheLoadOpensTheSandboxFirstSoNothingGoesLive()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            services.State.Switcher.EditSafeByDefault = false;
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Tile(vm, "b").IsOwn = true;                     // live: b's own picture, edited live
            vm.ActivePattern.Kind = PatternKind.ColorBars;
            vm.EditTarget = vm.EditTargets[0];
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsSandboxActive);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("b").Kind);

            Tile(vm, "b").ToPreviewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsSandboxActive);                                                // opened first
            Assert.NotNull(services.Bus.Sandbox);
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);                     // the preview holds b's picture
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);        // the program on air never saw it
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);
            Assert.Contains("EDIT SAFE opened", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheWallCarriesThePreviewButtonOnEveryScreenTileAndSendOnlyWhileTheSandboxIsOpen()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            window.Width = 1600;
            window.Height = 1000;
            Settle();

            var wall = window.GetVisualDescendants().OfType<WallView>().Single(w => w.IsEffectivelyVisible);
            var toPreview = wall.GetVisualDescendants().OfType<Button>().Where(x => x.Name == "TileToPreview").ToList();
            var send = wall.GetVisualDescendants().OfType<Button>().Where(x => x.Name == "TileSend").ToList();
            Assert.Equal(4, toPreview.Count);
            Assert.Equal(4, send.Count);
            foreach (var button in toPreview)
            {
                var tile = (SwitcherTile)button.DataContext!;
                // Every tile, PGM included: bringing what the room is watching back into the
                // preview is the one picture this could not reach before.
                Assert.True(button.IsEffectivelyVisible);
                Assert.Same(tile.ToPreviewCommand, button.Command);
            }
            Assert.All(send, x => Assert.False(x.IsEffectivelyVisible));          // nothing to send with the sandbox closed

            vm.IsSandboxActive = true;
            Settle();
            foreach (var button in send)
            {
                var tile = (SwitcherTile)button.DataContext!;
                Assert.Equal(!tile.IsProgramTile, button.IsEffectivelyVisible);   // SEND on every screen tile now
            }

            vm.IsSandboxActive = false;
            Settle();
            Assert.All(send, x => Assert.False(x.IsEffectivelyVisible));
        }
        finally
        {
            b.Dispose();
        }
    }

    [Fact]
    public void TheKindIsTheDesksOwnWithItsWords()
    {
        Assert.Equal((TargetKind.Screen, ValueKind.None), ActionSpec.For(ShowActionKind.ScreenToPreview));
        Assert.DoesNotContain(ShowActionKind.ScreenToPreview, ActionSpec.CueKinds);
        Assert.Contains("preview", ActionSpec.DeskOnly(ShowActionKind.ScreenToPreview));
        Assert.Contains("preview", ActionSpec.Label(ShowActionKind.ScreenToPreview));
        Assert.False(ActionSpec.ChangesContent(ShowActionKind.ScreenToPreview));
        Assert.Null(CueSheet.ParseKind(ActionSpec.Label(ShowActionKind.ScreenToPreview)));   // never a cue's step
        Assert.Contains("→ PVW on a tile", HelpBodies.Switcher);
    }

    private static void Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
