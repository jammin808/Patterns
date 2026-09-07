using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Rendering;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The engine's frame budget on a live desk: a pipeline feeds its sink's budget with the stage
/// the engine noted, the first preview frame closes the start-up budget, the desk's poll puts
/// both on the STABILITY lines, the facts carry them to the super-check and the RENDER tile —
/// and a fence against a catastrophic regression on the CPU raster path and the start.
/// </summary>
public class FrameBudgetAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void ThePreviewPipelineFeedsTheBudgetAndClosesTheStartup()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;

            // The start-up so far: the settings read, the services built, the view model, the window's XAML, the window opened (the runtime, graphics and Avalonia marks need Main).
            var phases = services.Startup.Phases.Select(p => p.Phase).ToList();
            Assert.Equal(new[] { StartupBudget.Settings, StartupBudget.Services, StartupBudget.ViewModel, StartupBudget.Pages, StartupBudget.Window }, phases.Take(5));
            Assert.True(services.Startup.TotalMs >= 0);

            var pipeline = new RenderPipeline(services.Bus, PipelineViewport.Preview);
            var info = new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 30; i++) pipeline.Render(surface.Canvas, 320, 180, 1);
            var perFrame = watch.Elapsed.TotalMilliseconds / 30;

            Assert.True(services.Startup.Complete, "the first preview frame is the start-up's last mark");
            Assert.Equal(StartupBudget.FirstFrame, services.Startup.Phases[^1].Phase);
            var reading = pipeline.Budget.Read(ShowClock.Seconds);
            Assert.Equal(30, reading.Frames);
            Assert.Equal("Preview", reading.Name);
            Assert.True(reading.WorstMs >= 0);
            Assert.True(reading.AverageMs >= 0);
            var known = new[] { FrameStage.PatternOf(services.State.Pattern.Kind), FrameStage.Overlays, FrameStage.LowerThird, FrameStage.Viewport, FrameStage.Layers, FrameStage.Fade };
            Assert.Contains(reading.WorstStage, known);

            // A fence against a catastrophic regression (a frame that waits on something), not a speed contest.
            Assert.True(perFrame < 100, $"a preview frame averages {perFrame:0.0} ms");
            Assert.True(services.Startup.TotalMs < 60_000, $"start-up {services.Startup.Describe()}");

            // The desk's poll reads the registry onto the STABILITY lines; the facts carry the same to the super-check and the tile.
            vm.PollNow();
            Assert.StartsWith("Render frame worst", vm.RenderBudgetText);
            Assert.Contains("Preview", vm.RenderBudgetText);
            Assert.StartsWith("Start-up ", vm.StartupText);
            Assert.DoesNotContain("still starting", vm.StartupText);
            Assert.Contains("first frame", vm.StartupText);

            var facts = services.Metrics.GatherFacts();
            Assert.True(facts.RenderWorstMs >= 0);
            Assert.True(facts.RenderSinks >= 1);
            Assert.True(facts.StartupComplete);
            Assert.True(facts.StartupSeconds >= 0);
            Assert.Contains("first frame", facts.StartupPhases);
            var report = SuperCheck.Run(facts);
            Assert.Contains(report.Rows, r => r.Item == "Render frame");
            Assert.Contains(report.Rows, r => r.Item == "Start-up");
            var tile = HealthDashboard.Tiles(facts).Single(t => t.Id == "render");
            Assert.Contains("worst frame in the last minute", tile.Detail);

            // The Machine page shows both lines.
            window.Width = 1420;
            window.Height = 900;
            vm.SelectPage(Shell.IndexOf("Machine"));
            Settle(window);
            var page = window.GetVisualDescendants().OfType<AdminSection>().First();
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.RenderBudgetText);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.StartupText);

            // Dispose detaches the sink from the registry.
            pipeline.Dispose();
            Assert.DoesNotContain(FrameBudgets.Readings(ShowClock.Seconds), r => r.Name == "Preview" && r.Frames == 30);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AnOutputPipelineIsNamedByItsScreenAndFollowsARelabel()
    {
        var b = TestApp.Boot();
        try
        {
            var services = b.Services;
            var viewport = new PipelineViewport(Patterns.Core.Model.SinkKind.Output, new SKSizeI(320, 180), default, null, 1, "Main");
            using var pipeline = new RenderPipeline(services.Bus, viewport);
            var info = new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            pipeline.Render(surface.Canvas, 320, 180, 1);
            Assert.Equal("Output 1 (Main)", pipeline.Budget.Read(ShowClock.Seconds).Name);
            Assert.Contains(FrameBudgets.Readings(ShowClock.Seconds), r => r.Name == "Output 1 (Main)");

            pipeline.Viewport = viewport with { Label = "Stage left", SinkIndex = 2 };
            pipeline.Render(surface.Canvas, 320, 180, 1);
            var reading = pipeline.Budget.Read(ShowClock.Seconds);
            Assert.Equal("Output 2 (Stage left)", reading.Name);
            Assert.Equal(2, reading.Frames);

            // Only a preview's first frame is the start-up's last mark — an output's frames never say so, and a preview says it once.
            var told = 0;
            var hook = RenderPipeline.FirstPreviewFrame;
            RenderPipeline.FirstPreviewFrame = () => told++;
            try
            {
                pipeline.Render(surface.Canvas, 320, 180, 1);
                Assert.Equal(0, told);
                using var preview = new RenderPipeline(services.Bus, PipelineViewport.Preview);
                preview.Render(surface.Canvas, 320, 180, 1);
                preview.Render(surface.Canvas, 320, 180, 1);
                Assert.Equal(1, told);
            }
            finally
            {
                RenderPipeline.FirstPreviewFrame = hook;
            }
        }
        finally
        {
            b.Dispose();
        }
    }
}
