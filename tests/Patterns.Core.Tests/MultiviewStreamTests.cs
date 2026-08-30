using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class StreamMrlTests
{
    private static StreamConfig Config() => new()
    {
        Width = 1280, Height = 720, Fps = 30, VideoKbps = 4500,
    };

    private static readonly SKRectI Screen = SKRectI.Create(1920, 0, 1920, 1080);

    [Fact]
    public void SingleRtmpDestinationBuildsTranscodeAndFlv()
    {
        var plan = StreamMrl.Build(Config(), Screen, new[] { "rtmp://a.rtmp.youtube.com/live2/KEY" });
        Assert.NotNull(plan);
        Assert.Equal("screen://", plan!.Mrl);
        Assert.Contains(":screen-fps=30", plan.Options);
        Assert.Contains(":screen-left=1920", plan.Options);
        Assert.Contains(":screen-top=0", plan.Options);
        Assert.Contains(":screen-width=1920", plan.Options);
        Assert.Contains(":screen-height=1080", plan.Options);

        var sout = Assert.Single(plan.Options, o => o.StartsWith(":sout="));
        Assert.Contains("vcodec=h264", sout);
        Assert.Contains("venc=x264{preset=veryfast,tune=zerolatency,keyint=60}", sout);
        Assert.Contains("vb=4500,width=1280,height=720", sout);
        Assert.Contains("std{access=rtmp,mux=ffmpeg{mux=flv},dst=rtmp://a.rtmp.youtube.com/live2/KEY}", sout);
        Assert.DoesNotContain("duplicate", sout);
        Assert.DoesNotContain("acodec", sout); // no audio device = video only
    }

    [Fact]
    public void TwoDestinationsShareOneEncodeViaDuplicate()
    {
        var plan = StreamMrl.Build(Config(), Screen, new[] { "rtmp://one/x", "srt://host:9000", "rtmp://three/ignored" });
        var sout = Assert.Single(plan!.Options, o => o.StartsWith(":sout="));
        Assert.Contains("duplicate{dst=std{access=rtmp,mux=ffmpeg{mux=flv},dst=rtmp://one/x},dst=std{access=srt,mux=ts,dst=host:9000}}", sout);
        Assert.DoesNotContain("three", sout); // capped at two
        Assert.Equal(1, sout.Split("transcode").Length - 1); // encoded once
    }

    [Fact]
    public void AudioDeviceAddsSlaveInputAndAacEncode()
    {
        var cfg = Config();
        cfg.AudioDevice = "Line In (VB-Audio)";
        var plan = StreamMrl.Build(cfg, Screen, new[] { "udp://239.1.1.1:5000" });
        Assert.Contains(":input-slave=dshow://", plan!.Options);
        Assert.Contains(":dshow-adev=Line In (VB-Audio)", plan.Options);
        var sout = Assert.Single(plan.Options, o => o.StartsWith(":sout="));
        Assert.Contains("acodec=mp4a,ab=160,channels=2,samplerate=48000", sout);
        Assert.Contains("std{access=udp,mux=ts,dst=239.1.1.1:5000}", sout);
    }

    [Fact]
    public void NoDestinationsMeansNoPlan()
    {
        Assert.Null(StreamMrl.Build(Config(), Screen, Array.Empty<string>()));
        Assert.Null(StreamMrl.Build(Config(), Screen, new[] { "  " }));
    }

    [Theory]
    [InlineData("STREAM ON", RemoteCommandKind.StreamOn)]
    [InlineData("stream off", RemoteCommandKind.StreamOff)]
    public void StreamCommandsParse(string line, RemoteCommandKind kind)
        => Assert.Equal(kind, ControlProtocol.Parse(line).Kind);
}

public class MultiviewRenderTests
{
    private static ShowSnapshot Snap(ShowState state, bool live = true)
        => new() { State = JsonUtil.Clone(state), Version = 1, OutputsLive = live };

    private static ShowState RedProgram()
    {
        var state = new ShowState();
        state.Transition.Enabled = false;
        state.Pattern.Kind = PatternKind.FlatField;
        state.Pattern.FlatField.Color = "#FF0000";
        state.Pattern.FlatField.ShowLabel = false;
        state.Pattern.Canvas.FollowOutput = true;
        return state;
    }

    private static SKBitmap Render(ShowSnapshot snap, MultiviewOptions opts, int w = 320, int h = 180)
    {
        var engine = new PatternEngine();
        using var sink = new SinkState();
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(w, h),
            ReferenceSize = new SKSizeI(w, h),
            Time = 5.0,
            Now = new DateTime(2026, 8, 30, 12, 0, 0),
            UtcNow = new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc),
            Sink = SinkKind.Thumbnail,
            SinkIndex = 0,
            SinkLabel = "mv-test",
        };
        var frame = new PatternFrame
        {
            Snapshot = snap,
            Config = snap.State.Pattern,
            Ctx = ctx,
            Sink = sink,
            Canvas = new SKSizeI(w, h),
            Palette = Palette.Resolve(snap),
        };
        engine.RenderMultiview(surface.Canvas, in frame, sink, opts);
        surface.Canvas.Flush();
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    [Fact]
    public void ProgramTileShowsTheProgramAndClockTileStaysDark()
    {
        var opts = new MultiviewOptions();
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Program });
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Clock });

        using var bmp = Render(Snap(RedProgram()), opts);
        var program = bmp.GetPixel(80, 75);
        Assert.True(program.Red > 200 && program.Blue < 40, $"program tile should be red, got {program}");
        var clock = bmp.GetPixel(310, 40); // inside the clock card, away from the digits
        Assert.True(clock.Red < 60 && clock.Green < 60, $"clock tile should be a dark card, got {clock}");
    }

    [Fact]
    public void ScreenTileFollowsThatScreensOwnPattern()
    {
        var state = RedProgram();
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "s1", UseCustomPattern = true });
        state.Independent.Add(new OutputAssignment { ScreenId = "s1" });
        var custom = state.Independent[0].Pattern;
        custom.Kind = PatternKind.FlatField;
        custom.FlatField.Color = "#0000FF";
        custom.FlatField.ShowLabel = false;
        custom.Canvas.FollowOutput = true;

        var opts = new MultiviewOptions { ShowLabels = false, ShowTally = false };
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Program });
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = "s1" });

        using var bmp = Render(Snap(state), opts);
        var left = bmp.GetPixel(80, 90);
        var right = bmp.GetPixel(240, 90);
        Assert.True(left.Red > 200 && left.Blue < 40, $"program tile red, got {left}");
        Assert.True(right.Blue > 200 && right.Red < 40, $"screen tile blue, got {right}");
    }

    [Fact]
    public void NestedMultiviewDrawsASlateInsteadOfRecursing()
    {
        var state = RedProgram();
        state.Pattern.Kind = PatternKind.Multiview; // program IS the multiview
        var opts = new MultiviewOptions();
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Program });

        using var bmp = Render(Snap(state), opts); // must complete without stack overflow
        Assert.True(bmp.Width > 0);
    }

    [Fact]
    public void SnapshotCarriesTheLiveFlag()
    {
        var bus = new SnapshotBus(new ShowState());
        bus.OutputsLive = true;
        bus.Publish(new ShowState());
        Assert.True(bus.Current.OutputsLive);
    }
}
