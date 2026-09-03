using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The wall: every content target (a screen, or a joined canvas by its member key) has a tile
/// with its own picture, OWN / ARM / MON, tally, and the big panes follow the selected one.
/// </summary>
public class WallTests
{
    private static readonly string CanvasKey = CanvasNameConfig.KeyFor(new[] { "a", "b" });

    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new Avalonia.PixelRect(1920, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new Avalonia.PixelRect(4400, 0, 1920, 1080), 1.0, false, 2),
    };

    /// <summary>The rig the app sees: a+b flush = canvas A; c stands alone. All three enabled.</summary>
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

    [AvaloniaFact]
    public void TheWallHasATilePerContentTargetWithItsRealShape()
    {
        var b = TestApp.Boot();
        try
        {
            Rig(b);
            Assert.Equal(3, b.Vm.SwitcherTiles.Count);
            var pgm = b.Vm.SwitcherTiles[0];
            var canvas = b.Vm.SwitcherTiles[1];
            var lobby = b.Vm.SwitcherTiles[2];

            Assert.True(pgm.IsProgramTile);
            Assert.Null(pgm.TargetId);
            Assert.Equal(new SKSizeI(3840, 1080), pgm.Size); // the program takes the first target's shape
            Assert.Equal(CanvasKey, canvas.TargetId);
            Assert.Equal(new SKSizeI(3840, 1080), canvas.Size);
            Assert.Equal(new SKSizeI(1920, 1080), lobby.Size);
            Assert.Equal(3840.0 / 1080.0, canvas.Ratio, 3);

            // Each tile monitors its own target on both sides, at its true size.
            Assert.Equal(CanvasKey, canvas.PgmViewport.ScreenId);
            Assert.Equal(SinkKind.Monitor, canvas.PgmViewport.Kind);
            Assert.True(canvas.PgmViewport.FitReference);
            Assert.False(canvas.PgmViewport.UsePreviewSnapshot);
            Assert.True(canvas.PvwViewport.UsePreviewSnapshot);
            Assert.Equal(new SKSizeI(3840, 1080), canvas.PvwViewport.ReferenceSize);

            // Everything opens armed, monitored and following the program.
            Assert.All(b.Vm.SwitcherTiles, t => Assert.True(t.IsArmed));
            Assert.All(b.Vm.SwitcherTiles, t => Assert.True(t.IsMonitored));
            Assert.All(b.Vm.SwitcherTiles, t => Assert.False(t.IsOwn));
            Assert.Equal(0, b.Vm.HeldCount);

            // The output windows render a joined canvas through its key — the same target the tile shows.
            var viewports = OutputWindowManager.BuildViewports(b.Vm.State.Output.Placements, b.Services.Screens.All);
            Assert.Equal(CanvasKey, viewports.First(x => x.Screen.Id == "a").Viewport.ScreenId);
            Assert.Equal(CanvasKey, viewports.First(x => x.Screen.Id == "b").Viewport.ScreenId);
            Assert.Equal("c", viewports.First(x => x.Screen.Id == "c").Viewport.ScreenId);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AJoinedCanvasCanHoldItsOwnPattern()
    {
        var b = TestApp.Boot();
        try
        {
            Rig(b);
            b.Vm.IsSandboxActive = false;
            b.Vm.State.Pattern.Kind = PatternKind.Grid;

            b.Vm.SwitcherTiles[1].IsOwn = true; // the OWN button on canvas A
            Dispatcher.UIThread.RunJobs();

            Assert.True(ContentTargets.UsesOwnPattern(b.Vm.State, CanvasKey));
            Assert.Contains(b.Vm.EditTargets, t => t.ScreenId == CanvasKey);
            Assert.Equal(CanvasKey, b.Vm.EditTarget.ScreenId); // the editors work on it now
            Assert.StartsWith("EDITING: CANVAS A", b.Vm.EditTargetBanner);
            Assert.Equal(CanvasKey, b.Vm.SelectedTargetId);
            Assert.True(b.Vm.SwitcherTiles[1].IsOwn);
            Assert.Equal(PatternKind.Grid, b.Vm.ActivePattern.Kind); // starts as a copy — nothing jumps

            b.Vm.ActivePattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();

            var air = b.Services.Bus.Current;
            Assert.Equal(PatternKind.ColorBars, air.PatternFor(CanvasKey).Kind); // the canvas shows its own
            Assert.Equal(PatternKind.Grid, air.PatternFor("c").Kind);            // the lobby still follows the program
            Assert.Equal(PatternKind.Grid, air.PatternFor("a").Kind);            // a member id alone never resolves the canvas
            Assert.Contains(CanvasKey, ContentTargets.ActiveCustomTargets(b.Vm.State));

            // A look captures the canvas flag, so rehearsal and the show agree.
            var json = LookService.Capture(b.Vm.State);
            b.Vm.SwitcherTiles[1].IsOwn = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.PatternFor(CanvasKey).Kind);
            Assert.Null(b.Vm.EditTarget.ScreenId);
            Assert.Equal(CanvasKey, b.Vm.SelectedTargetId); // still the selected tile, just following the program
            LookService.Apply(json, b.Vm.State);
            Dispatcher.UIThread.RunJobs();
            Assert.True(ContentTargets.UsesOwnPattern(b.Vm.State, CanvasKey));
            Assert.Equal(PatternKind.ColorBars, b.Services.Bus.Current.PatternFor(CanvasKey).Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ScopedTakeKeepsAnUnarmedTargetsPictureAndTheNextFullTakeLiftsThePin()
    {
        var b = TestApp.Boot();
        try
        {
            Rig(b);
            b.Vm.IsSandboxActive = false;
            b.Vm.State.Pattern.Kind = PatternKind.Grid;
            b.Vm.IsSandboxActive = true;
            b.Vm.State.Pattern.Kind = PatternKind.ColorBars;

            var lobby = b.Vm.SwitcherTiles[2];
            Assert.Equal("c", lobby.TargetId);
            lobby.IsArmed = false; // hold the lobby through the next send
            Assert.True(lobby.IsHeld);
            Assert.Equal(1, b.Vm.HeldCount);
            Assert.Equal("1 held", b.Vm.TakeScopeText);

            b.Vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var air = b.Services.Bus.Current;
            Assert.Equal(PatternKind.ColorBars, air.State.Pattern.Kind);         // the program moved
            Assert.Equal(PatternKind.ColorBars, air.PatternFor(CanvasKey).Kind); // the armed canvas followed
            Assert.Equal(PatternKind.Grid, air.PatternFor("c").Kind);            // the lobby kept its picture
            var pin = b.Vm.State.Independent.First(x => x.ScreenId == "c");
            Assert.True(pin.PinnedByTake);
            Assert.True(b.Vm.State.Output.Placements.First(p => p.ScreenId == "c").UseCustomPattern);
            Assert.True(b.Vm.SwitcherTiles[2].IsOwn);
            Assert.False(b.Vm.SwitcherTiles[2].IsArmed); // holding is sticky until the operator arms again
            Assert.Contains("kept", b.Vm.StatusMessage);

            // Arm everything, build the next look, TAKE: the pin lifts and the lobby follows again.
            b.Vm.ArmAllCommand.Execute(null);
            Assert.Equal(0, b.Vm.HeldCount);
            Assert.True(b.Vm.IsSandboxActive); // EDIT SAFE re-armed after the send
            b.Vm.State.Pattern.Kind = PatternKind.Focus;
            b.Vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            air = b.Services.Bus.Current;
            Assert.Equal(PatternKind.Focus, air.PatternFor("c").Kind);
            Assert.False(b.Vm.State.Output.Placements.First(p => p.ScreenId == "c").UseCustomPattern);
            Assert.DoesNotContain(b.Vm.State.Independent, x => x.ScreenId == "c");
            Assert.False(b.Vm.SwitcherTiles[2].IsOwn);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AnOwnPatternTheOperatorChoseSurvivesAFullTake()
    {
        var b = TestApp.Boot();
        try
        {
            Rig(b);
            b.Vm.IsSandboxActive = false;
            b.Vm.State.Pattern.Kind = PatternKind.Grid;
            b.Vm.SwitcherTiles[2].IsOwn = true; // the lobby gets its own picture, deliberately
            b.Vm.ActivePattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();

            b.Vm.IsSandboxActive = true;
            b.Vm.State.Pattern.Kind = PatternKind.ColorBars;
            b.Vm.TakeCommand.Execute(null); // everything armed
            Dispatcher.UIThread.RunJobs();

            var air = b.Services.Bus.Current;
            Assert.Equal(PatternKind.ColorBars, air.State.Pattern.Kind);
            Assert.Equal(PatternKind.Focus, air.PatternFor("c").Kind); // a chosen own pattern is not a pin
            Assert.False(b.Vm.State.Independent.First(x => x.ScreenId == "c").PinnedByTake);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void SelectingATileShapesThePanesAndPointsThePreviewAtIt()
    {
        var b = TestApp.Boot();
        try
        {
            Rig(b);
            Assert.Null(b.Vm.SelectedTargetId);
            Assert.True(b.Vm.SwitcherTiles[0].IsSelected);

            b.Vm.SelectTileCommand.Execute(b.Vm.SwitcherTiles[1]); // canvas A, following the program
            Assert.Equal(CanvasKey, b.Vm.SelectedTargetId);
            Assert.Equal(new SKSizeI(3840, 1080), b.Vm.SelectedTargetSize);
            Assert.Equal(3840.0 / 1080.0, b.Vm.SelectedTargetRatio, 3);
            Assert.Equal("3840×1080", b.Vm.SelectedTargetSizeText);
            Assert.Equal(CanvasKey, b.Services.PreviewScreenId); // the preview resolves it to the program
            Assert.Null(b.Vm.EditTarget.ScreenId);                // no own pattern: the editors stay on the program
            Assert.True(b.Vm.SwitcherTiles[1].IsSelected);
            Assert.False(b.Vm.SwitcherTiles[0].IsSelected);
            Assert.StartsWith("A ·", b.Vm.SelectedTargetLabel);

            b.Vm.SelectTileCommand.Execute(b.Vm.SwitcherTiles[2]); // the lobby
            Assert.Equal("c", b.Vm.SelectedTargetId);
            Assert.Equal(new SKSizeI(1920, 1080), b.Vm.SelectedTargetSize);

            b.Vm.SelectTileCommand.Execute(b.Vm.SwitcherTiles[0]); // PGM
            Assert.Null(b.Vm.SelectedTargetId);
            Assert.Null(b.Services.PreviewScreenId);
            Assert.Equal("PGM", b.Vm.SelectedTargetLabel);
            Assert.True(b.Vm.SwitcherTiles[0].IsSelected);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AMonitorIsATrueMiniatureOfItsTargetOnBothSides()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.IsSandboxActive = false;
            b.Vm.State.Transition.Enabled = false;
            b.Vm.State.Pattern.Kind = PatternKind.FlatField;
            b.Vm.State.Pattern.FlatField.Color = "#FF0000";
            b.Vm.State.Pattern.FlatField.ShowLabel = false;
            b.Vm.State.Pattern.Canvas.FollowOutput = true;
            Dispatcher.UIThread.RunJobs();
            b.Vm.IsSandboxActive = true;
            b.Vm.State.Pattern.FlatField.Color = "#0000FF"; // the next look, not on air
            Dispatcher.UIThread.RunJobs();

            SKColor[] Render(PipelineViewport viewport)
            {
                using var pipeline = new RenderPipeline(b.Services.Bus, viewport);
                var info = new SKImageInfo(200, 100, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                pipeline.Render(surface.Canvas, 200, 100, 1.0);
                surface.Canvas.Flush();
                using var image = surface.Snapshot();
                using var bmp = SKBitmap.FromImage(image);
                return new[] { bmp.GetPixel(100, 50), bmp.GetPixel(10, 50), bmp.GetPixel(190, 50) };
            }

            // A square target in a 2:1 control: the picture sits in the middle at its own shape,
            // with letterbox either side — never stretched to the control.
            var pgm = Render(PipelineViewport.Monitor(null, new SKSizeI(1000, 1000), "PGM", previewSide: false));
            Assert.True(pgm[0].Red > 240 && pgm[0].Blue < 15, $"PGM centre should be the air colour, got {pgm[0]}");
            Assert.True(pgm[1].Red < 30 && pgm[1].Green < 30 && pgm[1].Blue < 40, $"left should be letterbox, got {pgm[1]}");
            Assert.True(pgm[2].Red < 30 && pgm[2].Green < 30 && pgm[2].Blue < 40, $"right should be letterbox, got {pgm[2]}");

            var pvw = Render(PipelineViewport.Monitor(null, new SKSizeI(1000, 1000), "PVW", previewSide: true));
            Assert.True(pvw[0].Blue > 240 && pvw[0].Red < 15, $"PVW centre should be the sandbox colour, got {pvw[0]}");
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TallyFollowsTheOutputsBlackoutAndHolding()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.RebuildSwitcherTiles();
            Dispatcher.UIThread.RunJobs();
            var pgm = b.Vm.SwitcherTiles[0];
            Assert.False(pgm.IsOnAir); // outputs closed

            b.Services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Services.Outputs.IsLive);
            Assert.True(pgm.IsOnAir);

            b.Services.Actions.Execute(ShowActionKind.BlackoutOn, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.False(pgm.IsOnAir); // black is not on air

            b.Services.Actions.Execute(ShowActionKind.BlackoutOff, ActionOrigin.Desk);
            b.Services.Actions.Execute(ShowActionKind.OutputsOff, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.False(pgm.IsOnAir);

            // Holding only means something while a send is being built.
            var screen = b.Vm.SwitcherTiles.FirstOrDefault(t => !t.IsProgramTile);
            if (screen is not null)
            {
                b.Vm.IsSandboxActive = false;
                screen.IsArmed = false;
                Assert.False(screen.IsHeld);
                b.Vm.IsSandboxActive = true;
                Assert.True(screen.IsHeld);
                screen.IsArmed = true;
                Assert.False(screen.IsHeld);
            }
        }
        finally
        {
            b.Dispose();
        }
    }
}
