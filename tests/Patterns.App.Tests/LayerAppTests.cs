using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Rendering;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A drag on the PREVIEW pane moves a layer or an overlay; the designer's stage picks and drags
/// an element; the pages carry the editors; a layer rides with a look.
/// </summary>
public class LayerAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Draws the pane into an offscreen surface at its size, so its last frame's boxes and maths exist.</summary>
    private static void RenderPane(RenderPipeline pipeline, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        pipeline.Render(surface.Canvas, width, height, 1.0);
    }

    /// <summary>A canvas point as a point on the pane (DIPs; the headless window's scaling is 1).</summary>
    private static Point OnPane(in PaneMap map, float canvasX, float canvasY)
    {
        var tx = canvasX * map.CanvasScale + map.CanvasOffset.X;
        var ty = canvasY * map.CanvasScale + map.CanvasOffset.Y;
        return new Point(tx * map.Scale + map.Dx, ty * map.Scale + map.Dy);
    }

    [AvaloniaFact]
    public void DraggingOnThePreviewPaneMovesALayerAndAnOverlay()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            vm.IsSandboxActive = false;
            vm.State.Pattern.Layer1.Enabled = true;   // no picture yet — the desk still sees its box
            vm.State.Overlays.Clock.Enabled = true;
            vm.State.Overlays.Clock.Anchor = Anchor9.TopLeft;
            Settle(window);

            var pipeline = window.PreviewPipeline!;
            RenderPane(pipeline, 800, 450);
            Assert.NotNull(pipeline.LastMap);
            var map = pipeline.LastMap!.Value;
            var layer = pipeline.LastHits.First(h => h.Kind == HitKind.Layer1);
            var centre = OnPane(in map, layer.Rect.MidX, layer.Rect.MidY);

            Assert.True(window.BeginPreviewDrag(centre));
            window.MovePreviewDrag(new Point(centre.X + 40, centre.Y));
            window.EndPreviewDrag();
            var expected = 5 + 40 / map.Scale / map.CanvasScale * 100 / map.Canvas.Width;
            Assert.Equal(expected, vm.State.Pattern.Layer1.XPct, 2);
            Assert.Equal(5, vm.State.Pattern.Layer1.YPct, 2);
            Assert.Contains("Layer 1 placed", vm.StatusMessage);

            // The clock: a drag nudges it from its anchor, as a share of the canvas.
            Settle(window);
            RenderPane(pipeline, 800, 450);
            var clock = pipeline.LastHits.First(h => h.Kind == HitKind.Clock);
            var at = OnPane(in map, clock.Rect.MidX, clock.Rect.MidY);
            Assert.True(window.BeginPreviewDrag(at));
            window.MovePreviewDrag(new Point(at.X, at.Y + 30));
            window.EndPreviewDrag();
            Assert.Equal(30 / map.Scale / map.CanvasScale * 100 / map.Canvas.Height, vm.State.Overlays.Clock.OffsetYPct, 2);
            Assert.Equal(0, vm.State.Overlays.Clock.OffsetXPct, 2);
            Assert.Contains("The clock placed", vm.StatusMessage);

            // The picture itself is not a handle; a tiny move is not a drag.
            Assert.False(window.BeginPreviewDrag(new Point(790, 440)));
            RenderPane(pipeline, 800, 450);
            var again = pipeline.LastHits.First(h => h.Kind == HitKind.Layer1);
            var x = vm.State.Pattern.Layer1.XPct;
            var hold = OnPane(in map, again.Rect.MidX, again.Rect.MidY);
            Assert.True(window.BeginPreviewDrag(hold));
            window.MovePreviewDrag(new Point(hold.X + 1, hold.Y));
            window.EndPreviewDrag();
            Assert.Equal(x, vm.State.Pattern.Layer1.XPct, 6);

            // The nudged clock now sits over the layer's centre: the one on top wins the hit.
            RenderPane(pipeline, 800, 450);
            var over = pipeline.LastHits.First(h => h.Kind == HitKind.Layer1);
            Assert.True(window.BeginPreviewDrag(OnPane(in map, over.Rect.MidX, over.Rect.MidY)));
            window.EndPreviewDrag();
            Assert.Contains("clock", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

            // With the sandbox open, the drag lands in the preview and the words say so.
            vm.State.Overlays.Clock.Enabled = false;
            vm.IsSandboxActive = true;
            Settle(window);
            Assert.Same(vm.State.Pattern, vm.PreviewPattern);
            RenderPane(pipeline, 800, 450);
            var boxed = pipeline.LastHits.First(h => h.Kind == HitKind.Layer1);
            var p = OnPane(in map, boxed.Rect.MidX, boxed.Rect.MidY);
            Assert.True(window.BeginPreviewDrag(p));
            window.MovePreviewDrag(new Point(p.X, p.Y + 40));
            window.EndPreviewDrag();
            Assert.Contains("CUT or TAKE", vm.StatusMessage);
            Assert.Equal(5, b.Services.Bus.Current.State.Pattern.Layer1.YPct, 2);   // the air did not move
            Assert.True(vm.State.Pattern.Layer1.YPct > 5);                          // the preview did
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheDesignerPicksAndDragsAnElementOnItsStage()
    {
        var design = new LowerThirdDesign();
        var e = new LowerThirdElement { X = 100, Y = 100, W = 400, H = 80 };
        design.Elements.Add(e);
        var (scale, _, _) = LowerThirdPreview.Stage(480, 270);
        Assert.Equal(0.25f, scale);
        var box = LowerThirdPreview.BoxOnStage(design, e, 480, 270);
        Assert.Same(e, LowerThirdPreview.HitElement(design, box.Center, 480, 270));
        Assert.Null(LowerThirdPreview.HitElement(design, new Point(box.Right + 40, box.Top - 40), 480, 270));

        LowerThirdPreview.DragBy(design, e, (100, 100), new Point(40, 0), 480, 270);
        LowerThirdRenderer.BoxOf(design, new SKSizeI(1920, 1080), out var designScale);
        Assert.Equal(Math.Round(100 + 40 / (0.25 * designScale)), e.X);
        Assert.Equal(100, e.Y);

        // The later element draws on top, so it wins the hit; a disabled one is never picked.
        var top = new LowerThirdElement { X = e.X, Y = 100, W = 400, H = 80 };
        design.Elements.Add(top);
        Assert.Same(top, LowerThirdPreview.HitElement(design, LowerThirdPreview.BoxOnStage(design, top, 480, 270).Center, 480, 270));
        top.Enabled = false;
        Assert.Same(e, LowerThirdPreview.HitElement(design, LowerThirdPreview.BoxOnStage(design, e, 480, 270).Center, 480, 270));

        // The page binds the stage's selection to the editor's.
        var b = TestApp.Boot();
        try
        {
            b.Vm.SelectPage(Shell.IndexOf("Lower thirds"));
            Settle(b.Window);
            var stage = b.Window.GetVisualDescendants().OfType<LowerThirdPreview>().FirstOrDefault();
            Assert.NotNull(stage);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePagesCarryTheEditorsAndALayerRidesWithALook()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            vm.SelectPage(Shell.IndexOf("Media"));
            Settle(window);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "LAYER 2");
            vm.SelectPage(Shell.IndexOf("Overlays"));
            Settle(window);
            Assert.True(window.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == "Nudge X / Y (%)") >= 4);

            vm.IsSandboxActive = false;
            vm.State.Pattern.Layer1.Enabled = true;
            vm.State.Pattern.Layer1.Source = LayerSource.NdiFeed;
            vm.State.Pattern.Layer1.NdiSourceName = "CAM 7";
            vm.State.Pattern.Layer1.XPct = 42;
            vm.NewLookName = "Layered";
            vm.SaveLookCommand.Execute(null);
            vm.State.Pattern.Layer1.Enabled = false;
            vm.State.Pattern.Layer1.XPct = 5;
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.ApplyLook, "Layered"), ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Pattern.Layer1.Enabled);
            Assert.Equal("CAM 7", vm.State.Pattern.Layer1.NdiSourceName);
            Assert.Equal(42, vm.State.Pattern.Layer1.XPct);
            Assert.Contains(MediaLocator.FindWantedInputs(services.Bus.Current), w => w.Key == "ndi:CAM 7");
        }
        finally
        {
            b.Dispose();
        }
    }
}
