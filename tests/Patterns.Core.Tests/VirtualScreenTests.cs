using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The feeds' own screens: every NDI sender owns one, the stream owns one while set to its own;
/// they never join a canvas; a feed's frame keeps a mirrored target's shape; the raw frame feed
/// and the rendered stream plan behind the engine-fed stream.
/// </summary>
public class VirtualScreenTests
{
    [Fact]
    public void EverySenderOwnsAScreenAndTheStreamOwnsOneWhileSetToIt()
    {
        var state = new ShowState();
        var sender = new NdiSenderConfig { Name = "Feed", Width = 1280, Height = 720 };
        state.Ndi.Senders.Add(sender);

        Assert.True(VirtualScreens.Sync(state));
        var p = state.Output.Placements.Single(x => x.ScreenId == sender.OwnScreenId);
        Assert.Equal("ndi:" + sender.Id, sender.OwnScreenId);
        Assert.True(p.Planned && p.IsVirtual && !p.IsPlannedDisplay && p.Enabled);
        Assert.Equal("NDI", p.VirtualKind);
        Assert.Equal((1280, 720), (p.PlannedWidth, p.PlannedHeight));
        Assert.Equal("NDI · Feed", p.CustomLabel);
        Assert.False(VirtualScreens.Sync(state)); // in step: nothing to do

        // The screen follows the feed's size and name; a name the operator gave stays.
        sender.Width = 1920;
        sender.Name = "Wide";
        Assert.True(VirtualScreens.Sync(state));
        Assert.Equal((1920, 720), (p.PlannedWidth, p.PlannedHeight));
        Assert.Equal("NDI · Wide", p.CustomLabel);
        p.CustomLabel = "Lobby feed";
        sender.Name = "Other";
        VirtualScreens.Sync(state);
        Assert.Equal("Lobby feed", p.CustomLabel);

        // The stream's screen exists while the stream is set to its own screen.
        Assert.False(state.Stream.UsesOwnScreen);
        state.Stream.SourceScreenId = StreamConfig.OwnScreenId;
        Assert.True(state.Stream.UsesOwnScreen);
        Assert.True(VirtualScreens.Sync(state));
        var s = state.Output.Placements.Single(x => x.ScreenId == StreamConfig.OwnScreenId);
        Assert.Equal("STREAM", s.VirtualKind);
        Assert.Equal((state.Stream.Width, state.Stream.Height), (s.PlannedWidth, s.PlannedHeight));
        Assert.NotEqual(p.X, s.X); // side by side, never on top of each other
        state.Stream.SourceScreenId = "";
        Assert.True(VirtualScreens.Sync(state));
        Assert.DoesNotContain(state.Output.Placements, x => x.ScreenId == StreamConfig.OwnScreenId);

        // A feed that goes takes its screen and that screen's own content with it.
        state.Independent.Add(new OutputAssignment { ScreenId = sender.OwnScreenId });
        state.Ndi.Senders.Clear();
        Assert.True(VirtualScreens.Sync(state));
        Assert.Empty(state.Output.Placements);
        Assert.Empty(state.Independent);

        Assert.True(VirtualScreens.IsVirtualId("ndi:abc"));
        Assert.True(VirtualScreens.IsVirtualId(StreamConfig.OwnScreenId));
        Assert.False(VirtualScreens.IsVirtualId("planned:abc"));
        Assert.False(VirtualScreens.IsVirtualId("0:1920x1080@0,0"));
        Assert.Equal("stream", VirtualScreens.OwnerOf(StreamConfig.OwnScreenId));
        Assert.Equal("", VirtualScreens.OwnerOf(null));

        // An older show file: the senders get their screens on load, sized to the sender.
        var older = new ShowState();
        older.Ndi.Senders.Add(new NdiSenderConfig { Name = "Old", Width = 960, Height = 540 });
        SettingsStore.Migrate(older);
        Assert.Contains(older.Output.Placements, x => x.IsVirtual && x.PlannedWidth == 960 && x.CustomLabel == "NDI · Old");
    }

    [Fact]
    public void AFeedsScreenNeverJoinsACanvasHoweverItIsPlaced()
    {
        var state = new ShowState();
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", X = 0, Y = 0 });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", X = 1920, Y = 0 });
        var sender = new NdiSenderConfig { Name = "Feed", Width = 1920, Height = 1080 };
        state.Ndi.Senders.Add(sender);
        VirtualScreens.Sync(state);
        var feed = state.Output.Placements.Single(x => x.IsVirtual);
        feed.X = 3840; // dragged flush against b
        feed.Y = 0;

        var displays = new Dictionary<string, ScreenGeometry>
        {
            ["a"] = new(1920, 1080, "Left"),
            ["b"] = new(1920, 1080, "Right"),
        };
        var rig = RigGeometry.Build(state, displays);
        Assert.Equal(2, rig.Targets.Count);
        Assert.Contains("a+b", rig.Targets);
        Assert.Contains(sender.OwnScreenId, rig.Targets);
        Assert.Equal(new SKSizeI(1920, 1080), rig.SizeOf(sender.OwnScreenId));

        var solo = new ArrangedScreen("x", SKRectI.Create(0, 0, 100, 100), Solo: true);
        var next = new ArrangedScreen("y", SKRectI.Create(100, 0, 100, 100));
        Assert.False(ScreenLayout.Connected(solo, next));
        Assert.True(ScreenLayout.Connected(next with { Id = "z", Rect = SKRectI.Create(200, 0, 100, 100) }, next));
    }

    [Fact]
    public void AFeedsFrameKeepsAMirroredTargetsShapeAndShowsItsOwnScreensLook()
    {
        var exact = FrameFit.Compute(new SKSizeI(1920, 1080), new SKSizeI(1920, 1080));
        Assert.True(exact.IsExact);
        var portrait = FrameFit.Compute(new SKSizeI(1920, 1080), new SKSizeI(1080, 1920));
        Assert.Equal(0.5625f, portrait.Scale, 4);
        Assert.Equal((1920 - 1080 * 0.5625f) / 2, portrait.OffsetX, 3);
        Assert.Equal(0f, portrait.OffsetY, 3);
        var wide = FrameFit.Compute(new SKSizeI(1280, 720), new SKSizeI(3840, 1080));
        Assert.Equal(1 / 3f, wide.Scale, 4);
        Assert.Equal(180f, wide.OffsetY, 3);

        // A 4:3 planned screen mirrored into a 16:9 frame: bars at the sides, the picture in the middle.
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.ColorBars;
            s.Output.Placements.Add(new ScreenPlacement { ScreenId = "planned:p", Planned = true, PlannedWidth = 1440, PlannedHeight = 1080 });
        });
        var sender = new NdiSenderConfig { Name = "Feed", Width = 1280, Height = 720 };
        state.Ndi.Senders.Add(sender);
        VirtualScreens.Sync(state);
        ContentTargets.EnsureAssignment(state, sender.OwnScreenId).Pattern.Kind = PatternKind.Grid;
        ContentTargets.SetOwnPattern(state, sender.OwnScreenId, true);
        var snap = new ShowSnapshot { State = state, Version = 1, Rig = RigGeometry.Build(state, RigGeometry.NoDisplays) };
        var engine = new PatternEngine();

        using var mirror = Frame(engine, snap, "planned:p");
        Assert.Equal(SKColors.Black, mirror.GetPixel(20, 360));
        Assert.Equal(SKColors.Black, mirror.GetPixel(1260, 360));
        Assert.NotEqual(SKColors.Black, mirror.GetPixel(640, 360));
        Assert.NotEqual(new SKColor(0x14, 0x06, 0x06), mirror.GetPixel(640, 360));

        // The program fills the frame; the sender's own screen shows its own look, edge to edge.
        using var program = Frame(engine, snap, "");
        Assert.NotEqual(SKColors.Black, program.GetPixel(20, 360));
        using var own = Frame(engine, snap, sender.OwnScreenId);
        Assert.NotEqual(Signature(program), Signature(own));
        Assert.NotEqual(Signature(mirror), Signature(own));
        Assert.NotEqual(new SKColor(0x14, 0x06, 0x06), own.GetPixel(640, 360));
    }

    private static SKBitmap Frame(PatternEngine engine, ShowSnapshot snap, string sourceId)
    {
        using var sink = new SinkState();
        using var surface = SKSurface.Create(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul));
        NdiFrame.Render(engine, snap, sink, surface.Canvas, new SKSizeI(1280, 720), sourceId, SinkKind.Ndi, "NDI test", 0, 1.0);
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    private static string Signature(SKBitmap bmp)
    {
        var sb = new System.Text.StringBuilder();
        for (var y = 0; y < bmp.Height; y += 24)
        {
            for (var x = 0; x < bmp.Width; x += 40) sb.Append(bmp.GetPixel(x, y).ToString());
        }
        return sb.ToString();
    }

    [Fact]
    public void TheFrameFeedNeverTearsAFrameAndTheNewestWins()
    {
        var feed = new FrameFeed(8);
        static byte[] Bytes(byte from) => Enumerable.Range(from, 8).Select(i => (byte)i).ToArray();
        Assert.False(feed.Publish(new byte[3]));
        Assert.True(feed.Publish(Bytes(1)));
        Assert.True(feed.Publish(Bytes(11)));   // the reader never started the first: the newest wins
        Assert.Equal(1, feed.Dropped);

        var chunk = new byte[3];
        Assert.Equal(3, feed.Read(chunk));
        Assert.Equal(new byte[] { 11, 12, 13 }, chunk);
        Assert.True(feed.Publish(Bytes(21)));   // arrives mid-frame: waits its turn, the frame is never torn
        var rest = new byte[8];
        Assert.Equal(5, feed.Read(rest));
        Assert.Equal(new byte[] { 14, 15, 16, 17, 18 }, rest[..5]);
        Assert.Equal(8, feed.Read(rest));
        Assert.Equal(Bytes(21), rest);

        Assert.Equal(0, feed.Read(rest, timeoutMs: 20)); // nothing yet: the reader asks again
        Assert.Equal(3, feed.Published);
        feed.Close();
        Assert.True(feed.IsClosed);
        Assert.False(feed.Publish(Bytes(31)));
        Assert.Equal(0, feed.Read(rest));
    }

    [Fact]
    public void TheRenderedStreamPlanFeedsRawFramesIntoTheSameEncode()
    {
        var cfg = new StreamConfig { Width = 1280, Height = 720, Fps = 30, VideoKbps = 4500 };
        var plan = StreamMrl.BuildRendered(cfg, new[] { "rtmp://live.example/app/key" })!;
        Assert.Equal(StreamMrl.RenderedMrl, plan.Mrl);
        Assert.Contains(":demux=rawvideo", plan.Options);
        Assert.Contains(":rawvid-width=1280", plan.Options);
        Assert.Contains(":rawvid-height=720", plan.Options);
        Assert.Contains(":rawvid-chroma=RV32", plan.Options);
        Assert.Contains(":rawvid-fps=30", plan.Options);
        Assert.DoesNotContain(plan.Options, o => o.StartsWith(":screen-", StringComparison.Ordinal));
        Assert.Contains(plan.Options, o => o.StartsWith(":sout=#transcode{vcodec=h264", StringComparison.Ordinal) && o.Contains("vb=4500") && o.Contains("mux=flv"));
        Assert.Null(StreamMrl.BuildRendered(cfg, Array.Empty<string>()));
        Assert.Equal(1280 * 720 * 4, StreamMrl.FrameBytes(1280, 720));

        // The desktop capture plan reads as it always did.
        var capture = StreamMrl.Build(cfg, SKRectI.Create(0, 0, 1920, 1080), new[] { "rtmp://live.example/app/key" })!;
        Assert.Equal("screen://", capture.Mrl);
        Assert.Contains(":screen-width=1920", capture.Options);
        Assert.DoesNotContain(capture.Options, o => o.StartsWith(":rawvid-", StringComparison.Ordinal));
    }
}
