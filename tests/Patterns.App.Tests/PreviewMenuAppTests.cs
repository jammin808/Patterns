using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Rendering;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.Core.Model;
using Patterns.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 63: "right-click context menus should also be on the Preview in the lower area … options
/// to change settings, options or inputs for overlays, countdown, layers, etc if they are
/// right-clicked on in Preview." The pane answers what is under the pointer from the boxes its
/// last frame drew, and that thing's own menu opens; the bare picture opens the preview's, whose
/// SOURCE group changes what the picture is.
/// </summary>
public class PreviewMenuAppTests
{
    private static SKPoint? Find(RenderPipeline pipeline, Func<HitRect?, bool> wanted)
    {
        var map = pipeline.LastMap!.Value;
        for (var y = 4; y < 446; y += 6)
        {
            for (var x = 4; x < 796; x += 6)
            {
                var hit = HitTester.Find(pipeline.LastHits, in map, new SKPoint(x, y));
                if (wanted(hit)) return new SKPoint(x, y);
            }
        }
        return null;
    }

    private static void Draw(RenderPipeline pipeline)
    {
        using var surface = SKSurface.Create(new SKImageInfo(800, 450, SKColorType.Bgra8888, SKAlphaType.Premul));
        pipeline.Render(surface.Canvas, 800, 450, 1.0);
        surface.Canvas.Flush();
    }

    [AvaloniaFact]
    public void ARightClickOnTheClockInThePreviewOpensTheClocksMenuAndOnThePictureThePreviewsWithSource()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.State.Overlays.Clock.Enabled = true;
            vm.State.Overlays.Badge.Enabled = false;
            Dispatcher.UIThread.RunJobs();
            var pipeline = window.PreviewPipeline!;
            Draw(pipeline);
            Assert.Contains(pipeline.LastHits, h => h.Kind == HitKind.Clock);

            // The pane names what is under the pointer from the frame it drew.
            var onClock = Find(pipeline, h => h?.Kind == HitKind.Clock);
            Assert.NotNull(onClock);
            var subject = window.PreviewSubjectAt(new Point(onClock!.Value.X, onClock.Value.Y));
            Assert.Equal(HitKind.Clock, Assert.IsType<HitRect>(subject).Kind);
            var bare = Find(pipeline, h => h is null);
            Assert.NotNull(bare);
            Assert.Null(window.PreviewSubjectAt(new Point(bare!.Value.X, bare.Value.Y)));

            // Through the menus' own path: the clock's menu on the clock, the preview's on the picture.
            var canvas = window.GetVisualDescendants().OfType<SkiaCanvasControl>().Single(c => c.Name == "PreviewCanvas");
            Assert.Equal("preview", Menus.GetKind(canvas));
            Assert.True(Menus.Prepare(canvas, new Point(onClock.Value.X, onClock.Value.Y)));
            var clockMenu = Assert.IsType<DeskMenuVm>(Assert.IsType<DeskMenuControl>(Menus.FlyoutOf(canvas)!.Content).DataContext);
            Assert.Equal("overlay", clockMenu.Menu.Kind);
            Assert.Equal("clock", clockMenu.Menu.Subject);
            Assert.NotNull(clockMenu.Find("overlay.anchor"));

            Assert.True(Menus.Prepare(canvas, new Point(bare.Value.X, bare.Value.Y)));
            var previewMenu = Assert.IsType<DeskMenuVm>(Assert.IsType<DeskMenuControl>(Menus.FlyoutOf(canvas)!.Content).DataContext);
            Assert.Equal("preview", previewMenu.Menu.Kind);
            Assert.Equal("SOURCE", previewMenu.Groups[0].Heading);

            // Choosing a source makes the preview a media picture of it — under EDIT SAFE, opened by the choice.
            previewMenu.Find("media.source:Web")!.ChooseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsSandboxActive);
            Assert.Equal(PatternKind.Media, vm.State.Pattern.Kind);
            Assert.Equal(MediaSource.Web, vm.State.Pattern.Media.Source);
            Assert.Equal(PatternKind.Grid, services.AirState.Pattern.Kind);   // nothing reached the air
            Assert.Contains("web page", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ALayerUnderThePointerOpensTheLayersMenuAndTheMenuKeyStillAsksTheControlsOwnSubject()
    {
        var b = TestApp.Boot();
        Window? window = null;
        try
        {
            var vm = b.Vm;
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.State.Pattern.Layer2.Enabled = true;
            vm.State.Pattern.Layer2.XPct = 10;
            vm.State.Pattern.Layer2.YPct = 10;
            vm.State.Pattern.Layer2.WPct = 40;
            vm.State.Pattern.Layer2.HPct = 40;
            Dispatcher.UIThread.RunJobs();
            var pipeline = b.Window.PreviewPipeline!;
            Draw(pipeline);
            var onLayer = Find(pipeline, h => h?.Kind == HitKind.Layer2);
            Assert.NotNull(onLayer);
            var layerMenu = vm.MenuFor("preview", b.Window.PreviewSubjectAt(new Point(onLayer!.Value.X, onLayer.Value.Y)));
            Assert.NotNull(layerMenu);
            Assert.Equal("layer", layerMenu!.Menu.Kind);
            Assert.Equal("layer2", layerMenu.Menu.Subject);
            Assert.NotNull(layerMenu.Find("layer.source"));

            // A subject read from the pointer, on any control: the right button asks it with its point; the menu key has no point and asks the control's own.
            var border = new Border { Width = 240, Height = 80, Background = Brushes.DimGray, Focusable = true };
            Menus.SetKind(border, "overlay");
            Menus.SetSubject(border, "logo");
            Menus.SetSubjectAt(border, p => p.X < 120 ? "clock" : "message");
            window = new Window { Width = 600, Height = 400, DataContext = vm, Content = new StackPanel { Children = { border } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var left = border.TranslatePoint(new Point(20, 20), window)!.Value;
            window.MouseMove(left);
            window.MouseDown(left, MouseButton.Right);
            window.MouseUp(left, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("clock", Assert.IsType<DeskMenuVm>(Assert.IsType<DeskMenuControl>(Menus.FlyoutOf(border)!.Content).DataContext).Menu.Subject);
            Menus.FlyoutOf(border)!.Hide();
            var right = border.TranslatePoint(new Point(200, 20), window)!.Value;
            window.MouseMove(right);
            window.MouseDown(right, MouseButton.Right);
            window.MouseUp(right, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("message", Assert.IsType<DeskMenuVm>(Assert.IsType<DeskMenuControl>(Menus.FlyoutOf(border)!.Content).DataContext).Menu.Subject);
            Menus.FlyoutOf(border)!.Hide();
            window.Activate();
            border.Focus();
            window.KeyPress(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
            window.KeyRelease(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("logo", Assert.IsType<DeskMenuVm>(Assert.IsType<DeskMenuControl>(Menus.FlyoutOf(border)!.Content).DataContext).Menu.Subject);
        }
        finally
        {
            window?.Close();
            b.Dispose();
        }
    }
}
