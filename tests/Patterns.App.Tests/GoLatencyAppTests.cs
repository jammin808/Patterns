using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Rendering;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The GO on the clock, on a desk: a cue that changes the screens is pressed, the preview draws
/// the publish it made, the poll closes the GO on that frame, and the glance line, the Machine
/// page, the super-check and the CSV read it.
/// </summary>
public class GoLatencyAppTests
{
    [AvaloniaFact]
    public void AGoIsTimedToThePreviewsFrameWhenNoOutputIsOpenAndTheLinesReadIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var stack = CueStacks.Caller(vm.State);
            stack.Cues.Add(new RunCueConfig { Number = "01", Name = "Black", Actions = { new CueActionConfig { Kind = ShowActionKind.BlackoutOn } } });
            Dispatcher.UIThread.RunJobs();
            services.CueStack.SetArmed(true, ActionOrigin.Desk);
            Assert.Equal("Black", services.CueStack.StandbyCue?.Name);
            Assert.Equal("GO to first drawn frame: no GO yet.", services.CueStack.GoClock.Describe());

            var versionBefore = services.Bus.Current.Version;
            var go = services.CueStack.Go(ActionOrigin.Desk);
            Assert.True(go.Ok, go.Message);
            Assert.True(services.Bus.Current.Version > versionBefore, "the cue's step published");
            Assert.True(services.CueStack.GoClock.IsOpen);
            Assert.Equal(services.Bus.Current.Version, services.CueStack.GoClock.PendingVersion);

            // The preview draws the publish: the GO's frame, with no output open. The desk's own preview pane
            // may be drawing too (headless, it may not); the poll closes the GO once every preview drawing now
            // has shown it, or after the give-up with the one that did.
            using (var pipeline = new RenderPipeline(services.Bus, PipelineViewport.Preview))
            {
                var info = new SKImageInfo(64, 48, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                pipeline.Render(surface.Canvas, 64, 48, 1.0);
                Assert.True(pipeline.Budget.FirstShown(services.Bus.Current.Version) is not null, "the preview's budget saw the publish");
                Assert.True(pipeline.Budget.Read(ShowClock.Seconds).LagMs >= 0);
                var deadline = Environment.TickCount64 + 4000;
                while (services.CueStack.GoClock.IsOpen && Environment.TickCount64 < deadline)
                {
                    Dispatcher.UIThread.RunJobs();
                    services.CueStack.Poll();
                    Thread.Sleep(20);
                }
            }
            Assert.False(services.CueStack.GoClock.IsOpen, "the GO closed on a frame, or on the give-up");
            var last = services.CueStack.GoClock.Last!.Value;
            Assert.Equal("01", last.Cue);
            Assert.True(last.FrameMs >= 0, last.Words);
            Assert.Equal("PVW", last.Sink);
            Assert.StartsWith("GO 01 ", last.Words);
            Assert.Contains("to PVW's frame", last.Words);

            vm.PollNow();
            Assert.StartsWith("GO to first drawn frame ", vm.GoText);
            Assert.Contains("in the last sixty", vm.GoText);
            var facts = services.Metrics.GatherFacts();
            Assert.True(facts.GoWorstMs >= 0, facts.GoWorstMs.ToString());
            Assert.NotEmpty(facts.GoWorstWords);
            var row = Assert.Single(SuperCheck.Run(facts).Rows, r => r.Item == "GO to frame");
            Assert.Contains("worst GO 01", row.Value);
            Assert.EndsWith(",goWorstMs,lagWorstMs,renderFaults", MetricsCsv.Header);
        }
        finally
        {
            b.Dispose();
        }
    }
}
