using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Rendering;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The area of interest from the desk: a box picked on the PREVIEW pane, refined by a second pick, the presets, the clear, and the Media page's block.</summary>
public class InputCropAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void RenderPane(RenderPipeline pipeline, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        pipeline.Render(surface.Canvas, width, height, 1.0);
    }

    private static Point OnPane(in PaneMap map, float canvasX, float canvasY)
    {
        var tx = canvasX * map.CanvasScale + map.CanvasOffset.X;
        var ty = canvasY * map.CanvasScale + map.CanvasOffset.Y;
        return new Point(tx * map.Scale + map.Dx, ty * map.Scale + map.Dy);
    }

    private static string Still(string dir)
    {
        var path = Path.Combine(dir, "still.png");
        using var bmp = new SKBitmap(400, 200);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Orange);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaFact]
    public void ABoxOnThePreviewPaneBecomesTheAreaOfInterestAndASecondPickRefinesIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Image;
            vm.State.Pattern.Media.ImagePath = Still(b.Dir);
            vm.State.Pattern.Media.Fit = FitMode.Stretch;
            Settle(window);

            var pipeline = window.PreviewPipeline!;
            RenderPane(pipeline, 800, 450);
            var map = pipeline.LastMap!.Value;
            var picture = pipeline.LastHits.Single(h => h.Kind == HitKind.MediaPicture).Rect;

            // Not picking: a press on the picture is what it always was (nothing to take hold of).
            Assert.False(window.BeginCropPick(OnPane(in map, picture.MidX, picture.MidY)));

            // Picking: the box from a quarter in to three quarters in keeps the middle half.
            vm.CropPickActive = true;
            Assert.Contains("Drag a box", vm.StatusMessage);
            var from = OnPane(in map, picture.Left + picture.Width * 0.25f, picture.Top + picture.Height * 0.25f);
            var to = OnPane(in map, picture.Left + picture.Width * 0.75f, picture.Top + picture.Height * 0.75f);
            Assert.True(window.BeginCropPick(from));
            window.MoveCropPick(to);
            Assert.True(window.EndCropPick(to));
            var m = vm.State.Pattern.Media;
            Assert.Equal(25, m.CropLeftPct, 0);
            Assert.Equal(25, m.CropTopPct, 0);
            Assert.Equal(25, m.CropRightPct, 0);
            Assert.Equal(25, m.CropBottomPct, 0);
            Assert.False(vm.CropPickActive);
            Assert.Contains("Keeps 50% × 50%", vm.CropSummary);
            Assert.StartsWith("Area of interest set", vm.StatusMessage);

            // A second pick works on the picture as it shows now: the left half of the kept part.
            Settle(window);
            RenderPane(pipeline, 800, 450);
            var shown = pipeline.LastHits.Single(h => h.Kind == HitKind.MediaPicture);
            Assert.Equal(new Patterns.Core.Media.FrameCrop(25, 25, 25, 25), shown.Crop);
            vm.CropPickActive = true;
            Assert.True(window.BeginCropPick(OnPane(in map, shown.Rect.Left, shown.Rect.Top)));
            Assert.True(window.EndCropPick(OnPane(in map, shown.Rect.MidX, shown.Rect.Bottom)));
            Assert.Equal(25, m.CropLeftPct, 0);
            Assert.Equal(50, m.CropRightPct, 0);
            Assert.Equal(25, m.CropTopPct, 0);
            Assert.Equal(25, m.CropBottomPct, 0);

            // A box too small to mean anything is ignored; a backwards drag is fine.
            vm.CropPickActive = true;
            Assert.True(window.BeginCropPick(from));
            Assert.False(window.EndCropPick(new Point(from.X + 1, from.Y + 1)));
            Assert.Equal(50, m.CropRightPct, 0);
            Assert.True(vm.CropPickActive);
            vm.CropPickActive = false;

            // The presets and the clear.
            vm.ClearCropCommand.Execute(null);
            Assert.Equal((0d, 0d, 0d, 0d), (m.CropLeftPct, m.CropTopPct, m.CropRightPct, m.CropBottomPct));
            Assert.Equal("The whole picture.", vm.CropSummary);
            vm.CropPresetCommand.Execute("right:25");
            Assert.Equal(25, m.CropRightPct);
            vm.CropPresetCommand.Execute("centre:80");
            Assert.Equal((10d, 10d, 10d, 10d), (m.CropLeftPct, m.CropTopPct, m.CropRightPct, m.CropBottomPct));
            m.RotateQuarters = 1;
            m.FlipHorizontal = true;
            vm.PollNow();
            Assert.Contains("Turned 90°", vm.CropSummary);
            Assert.Contains("Mirrored", vm.CropSummary);

            // The Media page carries the block.
            var page = new Window { DataContext = vm, Width = 900, Height = 3000, Content = new ScrollViewer { Content = new MediaSection() } };
            page.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(page.GetVisualDescendants().OfType<ToggleButton>(), x => x.Content as string == "PICK ON PREVIEW");
            Assert.Contains(page.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "Side panel off");
            Assert.Contains(page.GetVisualDescendants().OfType<CheckBox>(), x => x.Content as string == "Mirror");
            page.Close();
        }
        finally
        {
            b.Dispose();
        }
    }
}
