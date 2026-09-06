using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Rendering;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The quality ladder on a live desk: the worst output's last second drives it, the Machine
/// page's mode locks it, the facts carry it to the super-check, and the two new lines and the
/// block read on the page.
/// </summary>
public class QualityAppTests
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
    public void TheWorstOutputSecondDrivesTheLadderAndTheMachinePageLocksIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var ladder = services.Quality.Ladder;
            Assert.Equal(QualityMode.Auto, ladder.Mode);
            Assert.Equal(0, ladder.Level);

            // An output sink whose frames run past the hitch line: recorded straight into its budget, judged second by second.
            var output = new PipelineViewport(SinkKind.Output, new SKSizeI(320, 180), default, null, 1, "Main");
            using var pipeline = new RenderPipeline(services.Bus, output);
            var preview = new RenderPipeline(services.Bus, PipelineViewport.Preview);
            var changes = 0;
            services.Quality.Changed += () => changes++;
            var t0 = 1000.0;
            for (var s = 0; s < 3; s++)
            {
                pipeline.Budget.Record(40, "pattern:Fractal", t0 + s + 0.5);
                preview.Budget.Record(3, "pattern:Grid", t0 + s + 0.5);          // the desk is fine: the output is what counts
                services.Quality.Tick(FrameBudgets.Readings(t0 + s + 1), DateTime.UtcNow);
            }
            Assert.Equal(1, ladder.Level);
            Assert.Equal(1, changes);
            Assert.Equal("Output 1 (Main)", services.Quality.LastSink);
            Assert.Equal(40, services.Quality.LastWorstMs);
            Assert.Equal(0.7, QualityLadder.Shared.Factor);                        // what every renderer reads

            // A second with nothing drawn is no verdict: the level holds.
            services.Quality.Tick(FrameBudgets.Readings(t0 + 500), DateTime.UtcNow);
            Assert.Equal(1, ladder.Level);

            // The desk's poll writes the lines; the facts carry the ladder and the ceilings to the super-check.
            vm.PollNow();
            Assert.StartsWith("Auto, level 1 of 3", vm.QualityText);
            Assert.Contains("Output 1 (Main)", vm.QualityText);
            Assert.StartsWith("This app", vm.MemoryBudgetText);
            Assert.Contains("decoders 0 of 4", vm.MemoryBudgetText);
            var facts = services.Metrics.GatherFacts();
            Assert.Equal(1, facts.QualityLevel);
            Assert.Equal(QualityMode.Auto, facts.QualityMode);
            Assert.Equal(4, facts.DecoderCap);
            Assert.Equal(0, facts.Decoders);
            Assert.True(facts.ImagesCached >= 0);
            var report = SuperCheck.Run(facts);
            Assert.Contains(report.Rows, r => r.Item == "Quality ladder" && r.Value == "Auto: level 1 of 3 (70%)");
            Assert.Contains(report.Rows, r => r.Item == "Memory ceiling");

            // The Machine page's mode: Full goes back to full at once and holds it; Economy locks two down; Auto resumes from there.
            services.State.Admin.Quality = QualityMode.Full;
            Assert.Equal(0, ladder.Level);
            Assert.Equal(2, changes);
            for (var s = 0; s < 5; s++)
            {
                pipeline.Budget.Record(60, "pattern:Fractal", t0 + 100 + s + 0.5);
                services.Quality.Tick(FrameBudgets.Readings(t0 + 100 + s + 1), DateTime.UtcNow);
            }
            Assert.Equal(0, ladder.Level);
            vm.PollNow();
            Assert.StartsWith("Full: locked at full quality", vm.QualityText);

            services.State.Admin.Quality = QualityMode.Economy;
            Assert.Equal(2, ladder.Level);
            Assert.Equal(0.5, QualityLadder.Shared.Factor);
            services.State.Admin.Quality = QualityMode.Auto;
            Assert.Equal(2, ladder.Level);
            Assert.Equal(QualityMode.Auto, services.Metrics.GatherFacts().QualityMode);

            // The page shows the block: the choice, the line, and the memory line.
            window.Width = 1420;
            window.Height = 900;
            vm.SelectPage(Shell.IndexOf("Machine"));
            Settle(window);
            var page = window.GetVisualDescendants().OfType<AdminSection>().First();
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "QUALITY LADDER");
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "MEMORY CEILINGS");
            Assert.Contains(page.GetVisualDescendants().OfType<ComboBox>(), c => ReferenceEquals(c.ItemsSource, Lists.QualityModes));
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.QualityText);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.MemoryBudgetText);
            Assert.Equal(4, Lists.QualityModes.Length);
            preview.Dispose();
        }
        finally
        {
            QualityLadder.Shared.Reset();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePreviewStandsInWhenNoOutputDraws()
    {
        var b = TestApp.Boot();
        try
        {
            var services = b.Services;
            using var preview = new RenderPipeline(services.Bus, PipelineViewport.Preview);
            var t0 = 5000.0;
            for (var s = 0; s < 3; s++)
            {
                preview.Budget.Record(45, "lower third", t0 + s + 0.5);
                services.Quality.Tick(FrameBudgets.Readings(t0 + s + 1), DateTime.UtcNow);
            }
            Assert.Equal(1, services.Quality.Ladder.Level);
            Assert.Equal("Preview", services.Quality.LastSink);
        }
        finally
        {
            QualityLadder.Shared.Reset();
            b.Dispose();
        }
    }
}
