using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Rendering;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.Core.LowerThirds;
using Patterns.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The sinks of a control that draws by hand on the compositor's render thread: nothing drawn once
/// the control has left the visual tree, a close that waits for the frame in progress, fresh sinks
/// when the control comes back — the rule the round-17 crash was missing (a page tab's preview drew
/// with a sink it had disposed on the way out, and a fractal element's disposed shader ended the desk).
/// </summary>
public class SinkGuardTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static SKColor At(SKSurface s, SKImageInfo i, int x, int y)
    {
        using var bmp = new SKBitmap(i);
        s.ReadPixels(i, bmp.GetPixels(), i.RowBytes, 0, 0);
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void TheGuardDrawsOnlyWhileOpenAndMakesFreshSinksAfterAClose()
    {
        using var guard = new SinkGuard();
        Assert.False(guard.IsOpen);
        Assert.False(guard.Draw(_ => Assert.Fail("closed: nothing draws")));

        guard.Open();
        Assert.True(guard.IsOpen);
        SinkState? first = null;
        Assert.True(guard.Draw(sinks =>
        {
            first = sinks("a");
            Assert.Same(first, sinks("a"));       // kept frame to frame
            Assert.NotSame(first, sinks("b"));    // one per key
        }));
        Assert.Equal(2, guard.Count);

        guard.Close();
        Assert.False(guard.IsOpen);
        Assert.Equal(0, guard.Count);
        Assert.False(guard.Draw(_ => Assert.Fail("closed again: nothing draws")));

        // Back on the tree: a fresh sink, never the one disposed on the way out.
        guard.Open();
        Assert.True(guard.Draw(sinks => Assert.NotSame(first, sinks("a"))));
        Assert.Equal(1, guard.Count);
        guard.Close();
        guard.Close();   // twice is nothing
        Assert.Equal(0, guard.Count);
    }

    [Fact]
    public async Task ACloseWaitsForTheFrameInProgressAndTheSinkLivesToTheEndOfIt()
    {
        var guard = new SinkGuard();
        guard.Open();
        var inFrame = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var drawnWithALiveSink = false;
        var frame = Task.Run(() => guard.Draw(sinks =>
        {
            var sink = sinks("stage");
            inFrame.Set();
            release.Wait();
            sink.Paints.Fill(SKColors.White);   // still alive: the close is waiting, not disposing under us
            drawnWithALiveSink = true;
        }));
        Assert.True(inFrame.Wait(5000), "the frame never started");
        var close = Task.Run(guard.Close);
        await Task.Delay(60);
        Assert.False(close.IsCompleted, "the close must wait for the frame");
        release.Set();
        Assert.True(await frame);
        await close;
        Assert.True(drawnWithALiveSink);
        Assert.False(guard.IsOpen);
        Assert.Equal(0, guard.Count);
    }

    /// <summary>
    /// The crash's own steps, headless: the Lower Thirds page's preview with a fractal element,
    /// drawn, its page left (the sink disposed), a late frame drawing nothing, the page re-entered
    /// and the preview drawing again — through a fresh sink, the fractal through a fresh shader.
    /// </summary>
    [AvaloniaFact]
    public void ThePreviewDrawsWithAFreshStageAfterItsPageIsLeftAndComeBackTo()
    {
        var b = TestApp.Boot();
        try
        {
            var state = b.Vm.State;
            var design = new LowerThirdDesign { Name = "Wave", Width = 1200, Height = 300 };
            design.Elements.Add(new LowerThirdElement { Name = "Bar", Kind = LowerThirdElementKind.Bar, X = 0, Y = 0, W = 1200, H = 300 });
            design.Elements.Add(new LowerThirdElement { Name = "Wave", Kind = LowerThirdElementKind.Fractal, X = 0, Y = 0, W = 1200, H = 300, CornerPx = 10 });
            state.LowerThirds.Designs.Add(design);
            var preview = new LowerThirdPreview { Design = design, State = state };
            var info = new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var at = design.InMs + 500;

            // Off the tree nothing draws: a draw op queued before the page was left, or run after, touches no sink.
            Assert.False(preview.IsStageOpen);
            Assert.False(preview.RenderTo(surface.Canvas, design, state, at, 640, 360));

            var host = new Window { DataContext = b.Vm, Width = 700, Height = 400, Content = preview };
            host.Show();
            Settle(host);
            Assert.True(preview.IsStageOpen);
            Assert.True(preview.RenderTo(surface.Canvas, design, state, at, 640, 360));
            surface.Canvas.Flush();
            var stage = new SKColor(0x0B, 0x0C, 0x10);
            Assert.NotEqual(stage, At(surface, info, 180, 306));   // the panel is on the stage

            // The page left: the stage's sink is gone, and a late draw op draws nothing at all.
            host.Content = null;
            Settle(host);
            Assert.False(preview.IsStageOpen);
            surface.Canvas.Clear(stage);
            Assert.False(preview.RenderTo(surface.Canvas, design, state, at, 640, 360));
            surface.Canvas.Flush();
            Assert.Equal(stage, At(surface, info, 180, 306));

            // The page back, quickly: a fresh stage, the fractal drawn again frame after frame — the steps that ended the desk.
            host.Content = preview;
            Settle(host);
            Assert.True(preview.IsStageOpen);
            for (var i = 0; i < 4; i++)
            {
                Assert.True(preview.RenderTo(surface.Canvas, design, state, at + i * 40, 640, 360));
            }
            surface.Canvas.Flush();
            Assert.NotEqual(stage, At(surface, info, 180, 306));
            host.Content = null;
            host.Close();
            Assert.False(preview.IsStageOpen);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// On the real desk, the crash's own route: the Lower Thirds page with a fractal design left for
    /// Pattern and re-entered — the stage closes as the page leaves the tree (its sink disposed) and
    /// opens again on the way back (a fresh one), as many times as the operator switches.
    /// </summary>
    [AvaloniaFact]
    public void OnTheDeskLeavingThePageClosesTheStageAndComingBackOpensAFreshOne()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var design = new LowerThirdDesign { Name = "Wave", Width = 1200, Height = 300 };
            design.Elements.Add(new LowerThirdElement { Name = "Wave", Kind = LowerThirdElementKind.Fractal, X = 0, Y = 0, W = 1200, H = 300 });
            vm.State.LowerThirds.Designs.Add(design);
            vm.SelectedLowerThird = design;
            vm.SelectPage(Shell.IndexOf("Lower thirds"));
            Settle(b.Window);
            var preview = b.Window.GetVisualDescendants().OfType<LowerThirdPreview>().First(p => p.Name == "DesignPreview");
            Assert.True(preview.IsStageOpen);
            for (var i = 0; i < 3; i++)
            {
                vm.SelectPage(Shell.IndexOf("Pattern"));
                Settle(b.Window);
                Assert.False(preview.IsStageOpen);   // the page left: the stage closed, a late frame draws nothing
                vm.SelectPage(Shell.IndexOf("Lower thirds"));
                Settle(b.Window);
                Assert.True(preview.IsStageOpen);    // back: a fresh stage, the design drawn again
            }
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>The Screens page's tiles keep their sinks behind the same guard: open on the tree, closed off it, open again on the way back.</summary>
    [AvaloniaFact]
    public void TheScreensPageTilesKeepTheirSinksBehindTheSameGuard()
    {
        var b = TestApp.Boot();
        try
        {
            var control = new ScreenArrangeControl { DataContext = b.Vm };
            Assert.False(control.IsStageOpen);
            var host = new Window { DataContext = b.Vm, Width = 700, Height = 400, Content = control };
            host.Show();
            Settle(host);
            Assert.True(control.IsStageOpen);
            host.Content = null;
            Settle(host);
            Assert.False(control.IsStageOpen);
            host.Content = control;
            Settle(host);
            Assert.True(control.IsStageOpen);
            host.Content = null;
            host.Close();
            Assert.False(control.IsStageOpen);
        }
        finally
        {
            b.Dispose();
        }
    }
}
