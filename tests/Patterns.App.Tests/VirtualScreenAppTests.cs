using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Adding a send creates its screen on the desk; the stream gets one when set to its own; the engine feeds the stream.</summary>
public class VirtualScreenAppTests
{
    [AvaloniaFact]
    public void AddingASendCreatesItsScreenAndRemovingItTakesItAway()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.IsSandboxActive = false;
            var realCount = b.Services.Screens.Real.Count;
            vm.AddNdiSenderCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var sender = vm.State.Ndi.Senders.Last();
            var placement = vm.State.Output.Placements.Single(p => p.ScreenId == sender.OwnScreenId);
            Assert.True(placement.IsVirtual);
            Assert.Equal("NDI · " + sender.Name, placement.CustomLabel);

            // A screen everywhere the operator works — the wall, the edit targets, the pickers — and never a window.
            var info = b.Services.Screens.All.Single(s => s.Id == sender.OwnScreenId);
            Assert.True(info.IsVirtual && info.IsPlanned);
            Assert.Equal((sender.Width, sender.Height), (info.Bounds.Width, info.Bounds.Height));
            Assert.Contains("own screen", info.Description);
            Assert.Equal(realCount, b.Services.Screens.Real.Count);
            vm.PollNow();
            Assert.Contains(vm.SwitcherTiles, t => t.MemberIds.Contains(sender.OwnScreenId));
            Assert.Contains(vm.NdiSources, t => t.ScreenId == sender.OwnScreenId && t.Label.Contains("own screen"));
            Assert.DoesNotContain(vm.NdiSources, t => t.ScreenId == sender.OwnScreenId && t.Label.StartsWith("Screen "));
            Assert.DoesNotContain(
                OutputWindowManager.BuildViewports(vm.State.Output.Placements, b.Services.Screens.All),
                x => x.Screen.Id == sender.OwnScreenId);
            Assert.Equal(0, vm.PlannedScreenCount);
            Assert.Equal(1, vm.VirtualScreenCount);

            // Never adopted, never removed on its own; a look of its own like any screen.
            Assert.False(vm.AdoptPlannedScreen(placement, b.Services.Screens.Real.FirstOrDefault()?.Id ?? "0:1x1@0,0"));
            vm.RemovePlannedScreen(placement);
            Assert.Contains(placement, vm.State.Output.Placements);
            vm.SelectedPlacement = placement;
            vm.SelectedUseCustom = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(vm.EditTargets, t => t.ScreenId == sender.OwnScreenId);
            sender.SourceScreenId = sender.OwnScreenId;
            Assert.True(sender.UsesOwnScreen);

            // Resizing the send resizes its screen; a renamed send renames a default label.
            sender.Width = 1280;
            sender.Height = 720;
            sender.Name = "Lobby";
            vm.PollNow();
            Assert.Equal((1280, 720), (placement.PlannedWidth, placement.PlannedHeight));
            Assert.Equal("NDI · Lobby", placement.CustomLabel);

            vm.RemoveNdiSenderCommand.Execute(sender);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(vm.State.Output.Placements, p => p.ScreenId == sender.OwnScreenId);
            Assert.DoesNotContain(b.Services.Screens.All, s => s.Id == sender.OwnScreenId);
            Assert.DoesNotContain(vm.State.Independent, a => a.ScreenId == sender.OwnScreenId);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheStreamGetsItsOwnScreenWhenSetToItAndTheEngineFeedsIt()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.IsSandboxActive = false;
            vm.ActivePattern.Kind = PatternKind.ColorBars;
            Assert.Contains(vm.StreamSources, t => t.ScreenId == StreamConfig.OwnScreenId);
            Assert.Contains(vm.StreamSources, t => t.ScreenId == "" && t.Label.Contains("desktop capture"));
            Assert.False(b.Services.Stream.IsRendered(""));
            Assert.True(b.Services.Stream.IsRendered(StreamConfig.OwnScreenId));

            vm.State.Stream.SourceScreenId = StreamConfig.OwnScreenId;
            vm.PollNow();
            var placement = vm.State.Output.Placements.Single(p => p.ScreenId == StreamConfig.OwnScreenId);
            Assert.Equal("STREAM", placement.VirtualKind);
            Assert.Equal((vm.State.Stream.Width, vm.State.Stream.Height), (placement.PlannedWidth, placement.PlannedHeight));
            Assert.Contains(b.Services.Screens.All, s => s.Id == StreamConfig.OwnScreenId && s.IsVirtual);
            Assert.Contains(vm.NdiSources, t => t.ScreenId == StreamConfig.OwnScreenId);

            // The engine renders the stream's screen into raw frames the encoder pulls.
            b.Services.RepublishNow();
            Dispatcher.UIThread.RunJobs();
            using var renderer = new StreamRenderer(b.Services.Bus, StreamConfig.OwnScreenId, 320, 180, 30);
            using var surface = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var sink = new SinkState();
            Assert.True(renderer.RenderOnce(surface, sink, 0));
            Assert.Equal(1, renderer.FramesRendered);
            Assert.Equal(320 * 180 * 4, renderer.Feed.FrameBytes);
            var frame = new byte[renderer.Feed.FrameBytes];
            var got = 0;
            while (got < frame.Length)
            {
                var n = renderer.Feed.Read(frame.AsSpan(got), timeoutMs: 200);
                Assert.True(n > 0, "the feed ran dry");
                got += n;
            }
            Assert.Contains(frame, x => x != 0); // colour bars, not black

            // Set back to a display: the stream's screen goes.
            vm.State.Stream.SourceScreenId = "";
            vm.PollNow();
            Assert.DoesNotContain(vm.State.Output.Placements, p => p.ScreenId == StreamConfig.OwnScreenId);
        }
        finally
        {
            b.Dispose();
        }
    }
}
