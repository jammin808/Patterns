using Avalonia.Headless.XUnit;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The live input age on a desk: a camera's frame drawn by an output is aged from its arrival in
/// the decoder to the end of the frame that drew it, and the number reaches the sink's budget and
/// the glance, the metrics sample and the CSV, the super-check's Live input row and STATE's
/// machine row. A file's frame is not live, and a sink that drew none has no age.
/// </summary>
public class LiveInputAgeAppTests
{
    [AvaloniaFact]
    public void ACamerasFrameIsAgedFromTheDecoderToTheFrameAndTheDeskSaysSoEverywhere()
    {
        var b = TestApp.Boot();
        var cam = new LiveSource();
        try
        {
            var (services, vm, _) = b;
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Capture;
            vm.State.Pattern.Media.CaptureDevice = "IMAG cam";
            services.Bus.Publish(vm.State);
            InputBus.Mount(InputKeys.Capture("IMAG cam"), cam);                                         // after the publish: the engine's own reconcile has been and gone

            var output = new PipelineViewport(SinkKind.Output, new SKSizeI(64, 36), default, null, 1, "Main");
            using var pipeline = new RenderPipeline(services.Bus, output);
            using var surface = SKSurface.Create(new SKImageInfo(64, 36));
            cam.FrameClock = ShowClock.Seconds - 0.033;                                                 // a frame that arrived 33 ms ago
            pipeline.Render(surface.Canvas, 64, 36, 1);
            Assert.True(cam.Drawn >= 1, "the output drew the camera");
            var reading = pipeline.Budget.Read(ShowClock.Seconds);
            Assert.InRange(reading.LiveAgeMs, 33, 5000);                                                // the 33 ms it had waited at least, the frame's own time on top
            Assert.Contains("· live ", Glance.SinkWords(reading));
            Assert.Contains("live input age worst", reading.Words);

            services.Metrics.Poll();
            Assert.True(services.Metrics.Current!.LiveAgeWorstMs >= 33, "the sample carries the outputs' worst");
            var csv = MetricsCsv.Line(services.Metrics.Current!);
            Assert.Matches(@",\d+(\.\d+)?$", csv);                                                       // the last column is the age

            var facts = services.Metrics.GatherFacts();
            Assert.True(facts.LiveAgeWorstMs >= 33);
            var row = SuperCheck.Run(facts).Rows.Single(r => r.Item == "Live input");
            Assert.Contains("decoder to frame", row.Value);

            var machine = System.Text.Json.JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("machine");
            Assert.True(machine.GetProperty("liveAgeMs").GetDouble() >= 33);

            // A file is not live: a fresh sink drawing the same source with the live word off has no age, and the row goes with it.
            cam.Live = false;
            using var other = new RenderPipeline(services.Bus, new PipelineViewport(SinkKind.Output, new SKSizeI(64, 36), default, null, 2, "Side"));
            other.Render(surface.Canvas, 64, 36, 1);
            Assert.True(cam.Drawn >= 2);
            Assert.Equal(-1, other.Budget.Read(ShowClock.Seconds).LiveAgeMs);
            Assert.DoesNotContain("live", Glance.SinkWords(other.Budget.Read(ShowClock.Seconds)));
            Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Item == "Live input");
        }
        finally
        {
            InputBus.Clear();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheDesksPickerStoresTheProfileForTheActiveCaptureDevice()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.ActivePattern.Kind = PatternKind.Media;
            vm.ActivePattern.Media.Source = MediaSource.Capture;
            vm.ActivePattern.Media.CaptureDevice = "IMAG cam";
            vm.CaptureFormat.Refresh(force: true);
            Assert.False(vm.CaptureFormat.LowLatency);
            vm.CaptureFormat.LowLatency = true;
            Assert.True(vm.State.CaptureLowLatencyFor("IMAG cam"));
            var wanted = MediaLocator.FindWantedInputs(services.Bus.Current).Where(w => w.Kind == MediaLocator.WantedKind.Capture).ToList();
            Assert.Contains(wanted, w => w.Target == "IMAG cam" && w.LowLatency);                        // the republish carried it to the engine's wanted list
            vm.CaptureFormat.LowLatency = false;
            Assert.False(vm.State.CaptureLowLatencyFor("IMAG cam"));
            Assert.Empty(vm.State.CaptureFormats);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>A capture source the way the decoders present one: timed frames, live or not as the test says.</summary>
    private sealed class LiveSource : IVideoFrameSource
    {
        public double FrameClock { get; set; } = -1;
        public bool Live { get; set; } = true;
        public int Drawn;

        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
        {
            canvas.DrawRect(dest, paint ?? new SKPaint { Color = SKColors.DarkSlateGray });
            Drawn++;
            return true;
        }

        public SKSizeI? FrameSize => new SKSizeI(16, 9);
        public bool IsPlaying => true;
        public bool IsEnded => false;
        public double DurationSeconds => 0;
        public string StatusText => "live test";
        public bool IsLive => Live;
    }
}
