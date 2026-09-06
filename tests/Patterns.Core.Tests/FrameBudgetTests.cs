using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The engine's frame budget — the render-side twin of the desk's tick budget: one sink's last
/// minute with the worst frame and the stage that took it, the registry the desk reads, the
/// super-check row with its advice, the RENDER tile, and the start-up budget with its phases.
/// </summary>
public class FrameBudgetTests
{
    [Fact]
    public void ABudgetKeepsTheLastMinuteWithTheWorstFrameAndItsStage()
    {
        var b = new FrameBudget(SinkKind.Output, 1, "Main");
        Assert.Equal(-1, b.Read(10).WorstMs);
        Assert.Equal(-1, b.Read(10).AverageMs);
        Assert.Equal(0, b.Read(10).FramesInWindow);

        // Second 10: three frames, one of them slow on the fractal; second 11: two more.
        b.Record(4, "pattern:Grid", 10.1);
        b.Record(31, "pattern:Fractal", 10.5);
        b.Record(5, "layers", 10.9);
        b.Record(6, "overlays", 11.2);
        b.Record(7, "lower third", 11.8);

        var r = b.Read(11.9);
        Assert.Equal(5, r.FramesInWindow);
        Assert.Equal(5, r.Frames);
        Assert.Equal(1, r.SlowFrames);
        Assert.Equal(31, r.WorstMs);
        Assert.Equal("pattern:Fractal", r.WorstStage);
        Assert.Equal((4 + 31 + 5 + 6 + 7) / 5.0, r.AverageMs, 6);
        Assert.Equal(3, r.Fps);                                   // the one complete second held three frames
        Assert.Equal("Output 1 (Main)", r.Name);
        Assert.Equal("Output 1 (Main) 10.6 ms avg at 3 fps · worst 31.0 ms (the Fractal pattern)", r.Words);
        Assert.Equal(7, b.LastMs);
        Assert.Equal(31, b.WorstEverMs);
        Assert.Equal("pattern:Fractal", b.WorstEverStage);

        // A minute on (the window is sixty seconds inclusive, so 12–71): both busy seconds have rolled
        // out and their buckets are reused; the session counts stay.
        b.Record(3, "pattern:Grid", 71.5);
        var later = b.Read(71.5);
        Assert.Equal(1, later.FramesInWindow);
        Assert.Equal(3, later.WorstMs);
        Assert.Equal("pattern:Grid", later.WorstStage);
        Assert.Equal(-1, later.Fps);                              // the current second is not complete
        Assert.Equal(6, later.Frames);
        Assert.Equal(1, later.SlowFrames);
        Assert.Equal(31, b.WorstEverMs);

        b.Reset();
        Assert.Equal(0, b.Frames);
        Assert.Equal(-1, b.Read(71.5).WorstMs);
        Assert.Equal(-1, b.WorstEverMs);
    }

    [Fact]
    public void TheNameFollowsTheSinkAndTheRelabel()
    {
        var b = new FrameBudget(SinkKind.Preview, 0, "Preview");
        Assert.Equal("Preview", b.Read(1).Name);
        b.Relabel(SinkKind.Output, 2, "Output 2");
        Assert.Equal("Output 2", b.Read(1).Name);
        b.Relabel(SinkKind.Output, 2, "");
        Assert.Equal("Output 2", b.Read(1).Name);
        b.Relabel(SinkKind.Monitor, 0, "PGM");
        Assert.Equal("Monitor PGM", b.Read(1).Name);
        b.Relabel(SinkKind.Ndi, 0, "NDI 1");
        Assert.Equal("NDI 1", b.Read(1).Name);
        b.Relabel(SinkKind.Ndi, 0, "");
        Assert.Equal("Ndi", b.Read(1).Name);
        b.Record(-5, "", 1);                                      // a negative reading is a zero, never a throw
        Assert.Equal(0, b.LastMs);
    }

    [Fact]
    public void TheStagesKeepTheSlowestAndTheWordsReadForTheDesk()
    {
        var s = new FrameStages();
        Assert.Equal("", s.SlowestStage);
        Assert.Equal(-1, s.SlowestMs);
        s.Note("pattern:Grid", 2.0);
        s.Note("lower third", 9.5);
        s.Note("overlays", 1.0);
        Assert.Equal("lower third", s.SlowestStage);
        Assert.Equal(9.5, s.SlowestMs);
        Assert.Equal(3, s.Noted);
        s.Begin();
        Assert.Equal("", s.SlowestStage);
        Assert.Equal(0, s.Noted);
        s.Note("fade", FrameStages.Now());
        Assert.Equal("fade", s.SlowestStage);
        Assert.True(s.SlowestMs >= 0);

        Assert.Equal("pattern:Fractal", FrameStage.PatternOf(PatternKind.Fractal));
        Assert.Equal("the Fractal pattern", FrameStage.Words("pattern:Fractal"));
        Assert.Equal("the lower third", FrameStage.Words(FrameStage.LowerThird));
        Assert.Equal("the crossfade", FrameStage.Words(FrameStage.Fade));
        Assert.Equal("the layers", FrameStage.Words(FrameStage.Layers));
        Assert.Equal("the frozen frame", FrameStage.Words(FrameStage.Freeze));
        Assert.Equal("", FrameStage.Words(""));
        Assert.Equal("odd", FrameStage.Words("odd"));
    }

    [Fact]
    public void TheRegistryReadsEverySinkThatDrewAndNamesTheWorst()
    {
        FrameBudgets.Clear();
        try
        {
            var preview = new FrameBudget(SinkKind.Preview, 0, "Preview");
            var output = new FrameBudget(SinkKind.Output, 1, "Main");
            var idle = new FrameBudget(SinkKind.Output, 2, "Idle");
            FrameBudgets.Attach(preview);
            FrameBudgets.Attach(output);
            FrameBudgets.Attach(idle);
            FrameBudgets.Attach(output);                          // twice is once
            Assert.Equal("Render frame: not measured yet — the preview and the outputs report as they draw.", FrameBudgets.Describe(5));

            preview.Record(2, "pattern:Grid", 4.2);
            preview.Record(3, "pattern:Grid", 4.8);
            output.Record(4, "pattern:Grid", 4.5);
            output.Record(31, "lower third", 4.9);
            output.Record(60, "lower third", 5.1);

            var readings = FrameBudgets.Readings(5.5);
            Assert.Equal(new[] { "Preview", "Output 1 (Main)" }, readings.Select(r => r.Name));   // attach order; a sink that drew nothing is left out
            var worst = FrameBudgets.Worst(readings)!;
            Assert.Equal("Output 1 (Main)", worst.Name);
            Assert.Equal(60, worst.WorstMs);
            Assert.Equal(2, FrameBudgets.SlowFrames(readings));
            var words = FrameBudgets.Describe(readings);
            Assert.StartsWith("Render frame worst 60.0 ms (the lower third) on Output 1 (Main) in the last minute", words);
            Assert.Contains("Preview 2.5 ms avg at 2 fps", words);
            Assert.Contains("Output 1 (Main) 31.7 ms avg at 2 fps", words);
            Assert.EndsWith("2 past 25 ms this session", words);
            Assert.Equal(words, FrameBudgets.Describe(5.5));

            FrameBudgets.Detach(output);
            Assert.Single(FrameBudgets.Readings(5.5));
            Assert.Null(FrameBudgets.Worst(Array.Empty<FrameBudgetReading>()));
        }
        finally
        {
            FrameBudgets.Clear();
        }
    }

    [Fact]
    public void TheSuperCheckRowNamesTheStageAndSaysWhatToLower()
    {
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Item == "Render frame");

        var green = SuperCheck.Run(new CheckFacts { RenderWorstMs = 9, RenderAverageMs = 3.2, RenderWorstStage = "pattern:Grid", RenderWorstSink = "Output 1 (Main)", RenderSinks = 2, RenderSlowFrames = 0 })
            .Rows.Single(r => r.Item == "Render frame");
        Assert.Equal(CheckLight.Green, green.Light);
        Assert.Equal("3.2 ms · worst 9.0 ms (the Grid pattern, Output 1 (Main))", green.Value);
        Assert.Equal("2 sinks in the last minute", green.Note);

        var amber = SuperCheck.Run(new CheckFacts { RenderWorstMs = 31, RenderAverageMs = 6, RenderWorstStage = "pattern:Fractal", RenderWorstSink = "Preview", RenderSinks = 1, RenderSlowFrames = 3 })
            .Rows.Single(r => r.Item == "Render frame");
        Assert.Equal(CheckLight.Amber, amber.Light);
        Assert.Contains("3 past 25 ms", amber.Value);
        Assert.Contains("a hitch the room can see", amber.Note);
        Assert.Contains("the fractal", amber.Note);

        var red = SuperCheck.Run(new CheckFacts { RenderWorstMs = 80, RenderAverageMs = 12, RenderWorstStage = FrameStage.LowerThird, RenderWorstSink = "Output 1 (Main)", RenderSinks = 1, RenderSlowFrames = 9 })
            .Rows.Single(r => r.Item == "Render frame");
        Assert.Equal(CheckLight.Red, red.Light);
        Assert.Contains("the room sees a stutter", red.Note);
        Assert.Contains("the lower third", red.Note);

        Assert.Contains("the clip's decode", SuperCheck.FrameAdvice("pattern:Media", CheckLight.Amber));
        Assert.Contains("the particles", SuperCheck.FrameAdvice("pattern:Particles", CheckLight.Amber));
        Assert.Contains("the multiview", SuperCheck.FrameAdvice("pattern:Multiview", CheckLight.Red));
        Assert.Contains("a layer's source", SuperCheck.FrameAdvice(FrameStage.Layers, CheckLight.Amber));
        Assert.Contains("crossfade", SuperCheck.FrameAdvice(FrameStage.Fade, CheckLight.Amber));
        Assert.Contains("the overlays", SuperCheck.FrameAdvice(FrameStage.Overlays, CheckLight.Amber));
        Assert.Contains("extra monitors", SuperCheck.FrameAdvice("", CheckLight.Amber));
        Assert.Contains("Render frame", SuperCheck.ToText(SuperCheck.Run(new CheckFacts { RenderWorstMs = 4 })));
    }

    [Fact]
    public void TheStartupRowAndTheBudgetBehindIt()
    {
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Item == "Start-up");
        var green = SuperCheck.Run(new CheckFacts { StartupSeconds = 2.3, StartupPhases = "runtime 400 ms · settings 40 ms", StartupComplete = true }).Rows.Single(r => r.Item == "Start-up");
        Assert.Equal(CheckLight.Green, green.Light);
        Assert.Equal("2.3 s", green.Value);
        Assert.Equal("runtime 400 ms · settings 40 ms", green.Note);
        var amber = SuperCheck.Run(new CheckFacts { StartupSeconds = 12, StartupPhases = "services 9.8 s", StartupComplete = true }).Rows.Single(r => r.Item == "Start-up");
        Assert.Equal(CheckLight.Amber, amber.Light);
        Assert.StartsWith("a slow start", amber.Note);
        var red = SuperCheck.Run(new CheckFacts { StartupSeconds = 25, StartupPhases = "services 23.1 s", StartupComplete = false }).Rows.Single(r => r.Item == "Start-up");
        Assert.Equal(CheckLight.Red, red.Light);
        Assert.Equal("25.0 s and still starting", red.Value);
        Assert.Contains("waited on something", red.Note);
        Assert.Contains("services 23.1 s", red.Note);

        var b = new StartupBudget();
        Assert.Equal("Start-up: measuring…", b.Describe());
        Assert.Equal(-1, b.TotalMs);
        Assert.False(b.Complete);
        b.Begin();
        Assert.True(b.Mark(StartupBudget.Settings));
        Assert.False(b.Mark(StartupBudget.Settings));            // a phase is marked once
        Assert.True(b.Mark(StartupBudget.Services));
        Assert.Equal(new[] { StartupBudget.Settings, StartupBudget.Services }, b.Phases.Select(p => p.Phase));
        Assert.True(b.TotalMs >= 0);
        Assert.False(b.Complete);
        Assert.StartsWith("Start-up ", b.Describe());
        Assert.Contains("(still starting)", b.Describe());
        Assert.Contains("settings ", b.Describe());
        Assert.True(b.Mark(StartupBudget.FirstFrame));
        Assert.True(b.Complete);
        Assert.DoesNotContain("still starting", b.Describe());
        Assert.Equal("1.8 s", StartupBudget.Span(1800));
        Assert.Equal("420 ms", StartupBudget.Span(420));

        // The origin: a mark before Begin begins now; a second Begin never moves the origin.
        var c = new StartupBudget();
        Assert.True(c.Mark("x"));
        Assert.True(c.Phases[0].Ms < 1000);
        var d = new StartupBudget();
        d.Begin(1);                                               // a tick from the machine's own start: everything since counts
        d.Begin(System.Diagnostics.Stopwatch.GetTimestamp());
        d.Mark("y");
        Assert.True(d.Phases[0].Ms > 1000);
    }

    [Fact]
    public void TheRenderTileReadsTheBudgetWhenThereIsOne()
    {
        var amber = HealthDashboard.Tiles(new CheckFacts { OutputsLive = false, Faults = 0, RenderWorstMs = 31, RenderWorstStage = FrameStage.LowerThird, RenderWorstSink = "Preview", RenderSlowFrames = 2 })
            .Single(t => t.Id == "render");
        Assert.Equal(CheckLight.Amber, amber.Light);
        Assert.Equal("31 ms", amber.Value);
        Assert.Contains("worst frame in the last minute (the lower third, Preview)", amber.Detail);
        Assert.Contains("2 slow", amber.Detail);
        Assert.Contains("no faults", amber.Detail);
        Assert.True(amber.HasBar);

        var red = HealthDashboard.Tiles(new CheckFacts { OutputsLive = true, Faults = 0, RenderWorstMs = 75, RenderWorstStage = "pattern:Fractal", RenderWorstSink = "Output 1 (Main)" })
            .Single(t => t.Id == "render");
        Assert.Equal(CheckLight.Red, red.Light);
        Assert.Equal(1, red.Fraction);

        var green = HealthDashboard.Tiles(new CheckFacts { OutputsLive = true, Faults = 0, RenderWorstMs = 8, RenderWorstStage = "pattern:Grid", RenderWorstSink = "Output 1 (Main)" })
            .Single(t => t.Id == "render");
        Assert.Equal(CheckLight.Green, green.Light);
        Assert.Equal("8 ms", green.Value);

        // Without a budget the tile is what it was: idle with the outputs closed.
        Assert.Equal("idle", HealthDashboard.Tiles(new CheckFacts { OutputsLive = false, Faults = 0 }).Single(t => t.Id == "render").Value);
    }
}

/// <summary>The engine notes its stages on every frame, nested draws included.</summary>
[Collection("InputBus")]
public class FrameStageEngineTests
{
    [Fact]
    public void TheEngineNotesEveryStageOfAFrame()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.FlatField;
            s.Overlays.Clock.Enabled = true;
        });
        var d = new LowerThirdDesign { Name = "Bar", Width = 1200, Height = 300, InMs = 100, OutMs = 100 };
        d.Elements.Add(new LowerThirdElement { Name = "Bar", Kind = LowerThirdElementKind.Bar, X = 0, Y = 0, W = 1200, H = 300, FillColor = "primary" });
        state.LowerThirds.Designs.Add(d);
        state.LowerThirds.Show(d, ShowClock.UtcAt(1));

        var engine = new PatternEngine();
        using var sink = new SinkState();
        var snap = RenderTestHarness.Snap(state);
        var info = new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        RenderContext Ctx(double time, bool fadeSource = false) => new()
        {
            ViewportSize = new SKSizeI(640, 360), ReferenceSize = new SKSizeI(640, 360), Time = time,
            Now = new DateTime(2026, 9, 6, 12, 0, 0), UtcNow = RenderTestHarness.FixedUtcNow, Sink = SinkKind.Output, SinkIndex = 1, SinkLabel = "test",
            IsFadeSource = fadeSource,
        };
        var known = new[] { "pattern:FlatField", FrameStage.Overlays, FrameStage.LowerThird, FrameStage.Viewport };

        var top = Ctx(1.5);
        engine.Render(surface.Canvas, snap, in top, sink);
        Assert.Equal(4, sink.Stages.Noted);                       // the pattern, the overlays, the lower third, the chip and badges
        Assert.Contains(sink.Stages.SlowestStage, known);
        Assert.True(sink.Stages.SlowestMs >= 0);

        // A nested draw (a fade source) notes into the same frame rather than starting one.
        var nested = Ctx(1.6, fadeSource: true);
        engine.Render(surface.Canvas, snap, in nested, sink);
        Assert.Equal(8, sink.Stages.Noted);

        // The next top-level frame starts afresh.
        var next = Ctx(1.7);
        engine.Render(surface.Canvas, snap, in next, sink);
        Assert.Equal(4, sink.Stages.Noted);
        Assert.Contains(sink.Stages.SlowestStage, known);
    }
}
