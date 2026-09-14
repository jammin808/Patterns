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
/// Memory on a live desk: the Machine page's line places the app's memory by owner, the facts
/// and STATE carry the new numbers, the assistant's brief has a memory line before anything is
/// wrong, and a sink's frame advances the render fence so a pooled frame it drew is free again.
/// </summary>
public class MemoryAppTests
{
    [AvaloniaFact]
    public void TheMachinePageStateAndTheBriefPlaceTheMemory()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            services.Metrics.Poll();
            vm.PollNow();
            Assert.StartsWith("This app", vm.MemoryBudgetText);
            Assert.Contains("placed:", vm.MemoryBudgetText);
            Assert.Contains("managed heap", vm.MemoryBudgetText);
            Assert.Contains("private", vm.AdminMemText);

            var facts = services.Metrics.GatherFacts();
            Assert.True(facts.PrivateMB > 0, "private bytes read");
            Assert.True(facts.ManagedMB > 0, "the managed heap read");
            Assert.True(facts.PictureBytes >= 0);
            Assert.Equal(0, facts.FramePools);                                                          // no live source here
            var row = SuperCheck.Run(facts).Rows.Single(r => r.Item == "Memory ceiling");
            Assert.StartsWith("This app", row.Value);

            var state = System.Text.Json.JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("memory");
            Assert.True(state.GetProperty("privateMB").GetDouble() > 0);
            Assert.True(state.GetProperty("managedMB").GetDouble() > 0);
            Assert.Contains("managed heap", state.GetProperty("placed").GetString());
            Assert.Equal(0, state.GetProperty("framePoolMB").GetDouble());
            Assert.Equal(0, state.GetProperty("starved").GetInt32());                                   // the fence's health, in STATE
            Assert.Equal(0, state.GetProperty("forcedFrees").GetInt64());
            Assert.True(state.GetProperty("liveSinks").GetInt32() >= 0);
            Assert.True(state.GetProperty("retiringMB").GetDouble() >= 0);
            Assert.True(state.GetProperty("fenceOldestMs").GetDouble() >= -1);
            var fence = SuperCheck.Run(facts).Rows.Single(r => r.Item == "Frame fence");
            Assert.Equal(CheckLight.Green, fence.Light);
            Assert.Contains("on the sinks' evidence alone", fence.Note);

            var (health, _) = services.DeskHealthWords();
            Assert.Contains(health, line => line.StartsWith("Memory: This app", StringComparison.Ordinal));
            Assert.Contains("privateMB,managedMB", MetricsCsv.Header);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ASinksFrameAdvancesTheFenceSoAPooledFrameItDrewIsFreeAgain()
    {
        var b = TestApp.Boot();
        var cam = new PooledSource();
        try
        {
            var (services, vm, _) = b;
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Capture;
            vm.State.Pattern.Media.CaptureDevice = "Fence Cam";
            services.Bus.Publish(vm.State);
            InputBus.Mount(InputKeys.Capture("Fence Cam"), cam);                                        // after the publish: the engine's own reconcile has been and gone

            using var pipeline = new RenderPipeline(services.Bus, PipelineViewport.Preview);
            using var surface = SKSurface.Create(new SKImageInfo(64, 36));
            var pool = cam.Pool;
            pool.Publish(pool.Acquire());                                                               // frame 0 on show
            pipeline.Render(surface.Canvas, 64, 36, 1);                                                 // the sink draws it: the pool notes the sink's running frame
            Assert.True(cam.Drawn >= 1, "the preview drew the capture");
            pool.Publish(pool.Acquire());                                                               // frame 1: 0 retires behind that frame
            Assert.Equal(1, pool.Retired);
            Assert.Equal(2, pool.Acquire());                                                            // the free ones first
            Assert.Equal(3, pool.Acquire());
            Assert.Equal(-1, pool.Acquire());                                                           // 0 waits: the frame that drew it has not been followed by another
            pipeline.Render(surface.Canvas, 64, 36, 1);                                                 // a new frame: the fence moves — the boot's own sinks drew nothing of this pool and are not waited for
            Assert.Equal(0, pool.Acquire());
            Assert.True(RenderFence.LiveSinks >= 1, "the sink that just drew is live on the fence");
        }
        finally
        {
            InputBus.Clear();
            cam.Pool.Dispose();
            b.Dispose();
        }
    }

    /// <summary>A capture source over a frame pool, the way the decoders draw: the newest pooled frame, and the pool told which sink drew it.</summary>
    private sealed class PooledSource : IVideoFrameSource
    {
        public FramePool Pool { get; } = new(new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Opaque), 16, 4);
        public int Drawn;

        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
        {
            if (!Pool.TryLease(out var lease)) return false;                                            // the lease notes this sink under the pool's lock
            canvas.DrawImage(lease.Image, dest, paint);
            Drawn++;
            return true;
        }

        public SKSizeI? FrameSize => new SKSizeI(4, 2);
        public bool IsPlaying => true;
        public bool IsEnded => false;
        public double DurationSeconds => 0;
        public string StatusText => "fence test";
    }
}
