using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Rendering;
using Patterns.App.Services;
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

            // An output sink whose frames run past its budget: recorded straight into its budget, judged second by second.
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
    public void EachOutputIsJudgedAgainstItsOwnRateAndTheOnePressingHardestIsTheJudge()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, _, _) = b;
            var ladder = services.Quality.Ladder;
            var slow = new PipelineViewport(SinkKind.Output, new SKSizeI(320, 180), default, null, 1, "Cinema") { TargetFps = 30 };
            var fast = new PipelineViewport(SinkKind.Output, new SKSizeI(320, 180), default, null, 2, "Wall") { TargetFps = 60 };
            using var cinema = new RenderPipeline(services.Bus, slow);
            using var wall = new RenderPipeline(services.Bus, fast);
            Assert.Equal(30, cinema.Budget.TargetFps);
            Assert.Equal(60, wall.Budget.TargetFps);

            // Twenty-millisecond frames: within a 30 fps output's budget, past a 60 fps one's.
            var t0 = 2000.0;
            for (var s = 0; s < 5; s++)
            {
                for (var f = 0; f < 30; f++) cinema.Budget.Record(20, "pattern:Fractal", t0 + s + f / 30.0);
                services.Quality.Tick(FrameBudgets.Readings(t0 + s + 1), DateTime.UtcNow);
            }
            Assert.Equal(0, ladder.Level);
            Assert.Equal("Output 1 (Cinema)", services.Quality.LastSink);
            Assert.Equal(30, services.Quality.LastSecond.TargetFps);
            Assert.True(services.Quality.LastSecond.P95Ms <= 20.5 && services.Quality.LastSecond.P95Ms > 19);
            Assert.Contains("against a 28.3 ms budget at 30 fps", services.Quality.Describe());

            for (var s = 5; s < 8; s++)
            {
                for (var f = 0; f < 30; f++) cinema.Budget.Record(20, "pattern:Fractal", t0 + s + f / 30.0);
                for (var f = 0; f < 60; f++) wall.Budget.Record(20, "pattern:Fractal", t0 + s + f / 60.0);
                services.Quality.Tick(FrameBudgets.Readings(t0 + s + 1), DateTime.UtcNow);
            }
            Assert.Equal(1, ladder.Level);                                                               // the wall pressed hardest: judged, and it stepped
            Assert.Equal("Output 2 (Wall)", services.Quality.LastSink);
            Assert.StartsWith("Output 2 (Wall): p95 20.5 ms against a 14.2 ms budget at 60 fps for 3 s", ladder.Cause);
            Assert.Contains("Last second: Output 2 (Wall) p95 20.5 ms, worst 20 ms against a 14.2 ms budget at 60 fps.", services.Quality.Describe());

            // Slots missed count on their own: a second of quick frames that missed three slots presses too.
            var t1 = t0 + 100;
            for (var s = 0; s < 3; s++)
            {
                for (var f = 0; f < 60; f++) wall.Budget.Record(4, "pattern:Grid", t1 + s + f / 60.0);
                wall.Budget.RecordMissed(3, t1 + s + 0.5);
                services.Quality.Tick(FrameBudgets.Readings(t1 + s + 1), DateTime.UtcNow);
            }
            Assert.Equal(2, ladder.Level);
            Assert.StartsWith("Output 2 (Wall): 3 slots missed in a second at 60 fps for 3 s", ladder.Cause);
            Assert.Equal(3, services.Quality.LastSecond.Missed);
            Assert.Contains("3 slots missed", services.Quality.Describe());
        }
        finally
        {
            QualityLadder.Shared.Reset();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AutoStartsWhereTheLastSessionOnThisMachineSettledAndWritesWhereItEnds()
    {
        // The profile this machine left last time: level 2 — and Auto begins there, with the reason on the line.
        var b = TestApp.Boot(prepare: dir => new QualityProfileStore(dir).Write(new QualityProfile(QualityService.MachineKey(), 2, 4, DateTime.UtcNow)));
        try
        {
            var (services, vm, _) = b;
            var ladder = services.Quality.Ladder;
            Assert.Equal(QualityMode.Auto, ladder.Mode);
            Assert.Equal(2, ladder.Level);
            Assert.Equal(2, services.Quality.StartedAt);
            Assert.Equal(0, ladder.StepsDown);
            Assert.Equal("where the last session on this machine settled", ladder.Cause);
            vm.PollNow();
            Assert.StartsWith("Auto, level 2 of 3", vm.QualityText);
            Assert.Contains("where the last session on this machine settled", vm.QualityText);

            // Thirty clean seconds climb a level, and the profile follows on the file lane.
            for (var i = 0; i < 30; i++) ladder.Observe(2, "Output 1", DateTime.UtcNow);
            Assert.Equal(1, ladder.Level);
            services.Quality.SaveProfile();
            TestApp.FlushFiles(services);
            var written = new QualityProfileStore(b.Dir).Read();
            Assert.NotNull(written);
            Assert.Equal(1, written!.Level);
            Assert.Equal(QualityService.MachineKey(), written.Machine);

            // The desk ending writes it too.
            for (var i = 0; i < 30; i++) ladder.Observe(2, "Output 1", DateTime.UtcNow);
            Assert.Equal(0, ladder.Level);
            b.Dispose();
            Assert.Equal(0, new QualityProfileStore(b.Dir).Read()!.Level);
        }
        finally
        {
            QualityLadder.Shared.Reset();
            b.Dispose();
        }

        // Another machine's profile says nothing about this one: full, as always.
        var other = TestApp.Boot(prepare: dir => new QualityProfileStore(dir).Write(new QualityProfile("some other box", 3, 9, DateTime.UtcNow)));
        try
        {
            Assert.Equal(0, other.Services.Quality.Ladder.Level);
            Assert.Equal(0, other.Services.Quality.StartedAt);
        }
        finally
        {
            QualityLadder.Shared.Reset();
            other.Dispose();
        }

        // A small machine with no profile starts a level down until thirty clean seconds prove it (the boot pins a desk-class machine; the folder's hook runs after the pin).
        var small = TestApp.Boot(prepare: _ => QualityService.MachineGB = 4);
        try
        {
            Assert.Equal(1, small.Services.Quality.Ladder.Level);
            Assert.Equal(1, small.Services.Quality.StartedAt);
            Assert.Contains("small machine", small.Services.Quality.Ladder.Cause);
        }
        finally
        {
            QualityService.MachineGB = 32;
            QualityLadder.Shared.Reset();
            small.Dispose();
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
