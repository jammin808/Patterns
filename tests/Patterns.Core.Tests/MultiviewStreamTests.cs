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
    private static ShowSnapshot Snap(ShowState state, bool live = true,
        IReadOnlyDictionary<string, ScreenGeometry>? displays = null)
    {
        var clone = JsonUtil.Clone(state);
        return new()
        {
            State = clone, Version = 1, OutputsLive = live,
            Rig = RigGeometry.Build(clone, displays ?? RigGeometry.NoDisplays),
        };
    }

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

    private static SKBitmap Render(ShowSnapshot snap, MultiviewOptions opts, int w = 320, int h = 180,
        SinkKind sinkKind = SinkKind.Thumbnail, SinkState? reuse = null)
    {
        var engine = new PatternEngine();
        var owned = reuse is null ? new SinkState() : null;
        var sink = reuse ?? owned!;
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(w, h),
                ReferenceSize = new SKSizeI(w, h),
                Time = 5.0,
                Now = new DateTime(2026, 8, 30, 12, 0, 0),
                UtcNow = new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc),
                Sink = sinkKind,
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
        finally
        {
            owned?.Dispose();
        }
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

    private static readonly SKColor MultiviewBg = new(0x06, 0x07, 0x0A);
    private static readonly SKColor SlateFill = new(0x11, 0x13, 0x1A);

    /// <summary>a and b flush (canvas a+b), c standing alone; every display 1920×1080.</summary>
    private static Dictionary<string, ScreenGeometry> ThreeDisplays() => new(StringComparer.Ordinal)
    {
        ["a"] = new ScreenGeometry(1920, 1080, "Left"),
        ["b"] = new ScreenGeometry(1920, 1080, "Right"),
        ["c"] = new ScreenGeometry(1920, 1080, "Lobby"),
    };

    private static void ThreeScreens(ShowState state)
    {
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", X = 0, Y = 0, Enabled = true });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", X = 1920, Y = 0, Enabled = true });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "c", X = 6000, Y = 0, Enabled = true });
    }

    [Fact]
    public void AScreenTileIsATrueMiniatureOfThatScreenNotAReLayoutAtSixteenByNine()
    {
        var state = RedProgram();
        state.Output.Placements.Add(new ScreenPlacement
        {
            ScreenId = "p", X = 0, Y = 0, Enabled = true,
            Planned = true, PlannedWidth = 1080, PlannedHeight = 1920,
        });

        var opts = new MultiviewOptions { Columns = 1, ShowLabels = false, ShowTally = false };
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = "p" });

        using var bmp = Render(Snap(state), opts);

        // A portrait screen is a tall box in the middle of its cell, letterboxed either side —
        // at 16:9 the picture would have spanned the whole width.
        var inside = bmp.GetPixel(160, 90);
        Assert.True(inside.Red > 200 && inside.Blue < 40, $"tile centre should be the screen's picture, got {inside}");
        Assert.Equal(MultiviewBg, bmp.GetPixel(20, 90));
    }

    [Fact]
    public void AJoinedCanvasIsATileAndAMemberTileRendersItsSliceOfIt()
    {
        var state = RedProgram();
        ThreeScreens(state);
        var key = CanvasNameConfig.KeyFor(new[] { "a", "b" });
        var canvasPattern = ContentTargets.EnsureAssignment(state, key).Pattern;
        ContentTargets.SetOwnPattern(state, key, true);
        canvasPattern.Kind = PatternKind.FlatField;
        canvasPattern.FlatField.Color = "#0000FF";
        canvasPattern.FlatField.ShowLabel = false;
        canvasPattern.Canvas.FollowOutput = true;

        var opts = new MultiviewOptions { Columns = 3, ShowLabels = false, ShowTally = false };
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = key });
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = "a" });
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = "c" });

        using var bmp = Render(Snap(state, displays: ThreeDisplays()), opts, 960, 180);

        var canvas = bmp.GetPixel(161, 90);
        Assert.True(canvas.Blue > 200 && canvas.Red < 40, $"the canvas tile shows the canvas, got {canvas}");
        // A 32:9 strip cannot fill a 16:9-ish cell: the aspect, proved by colour.
        Assert.Equal(MultiviewBg, bmp.GetPixel(161, 10));

        var member = bmp.GetPixel(480, 90);
        Assert.True(member.Blue > 200 && member.Red < 40, $"a member shows its half of the canvas, got {member}");

        var lobby = bmp.GetPixel(798, 90);
        Assert.True(lobby.Red > 200 && lobby.Blue < 40, $"a stand-alone screen follows the program, got {lobby}");
    }

    [Fact]
    public void ACanvasTileIsOnAirOnlyWhileEveryMemberIsEnabled()
    {
        var state = RedProgram();
        state.Pattern.FlatField.Color = "#0000FF"; // blue content, so a red border is unmistakable
        ThreeScreens(state);
        var key = CanvasNameConfig.KeyFor(new[] { "a", "b" });

        var opts = new MultiviewOptions { Columns = 1, ShowTally = true };
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = key });

        // The top tally border sits at y ≈ 33 (video rect 3.84 tall strip inside a 320×180 frame).
        static int TopBorderRed(SKBitmap bmp)
        {
            var best = 0;
            for (var y = 30; y <= 37; y++) best = Math.Max(best, bmp.GetPixel(160, y).Red);
            return best;
        }

        using (var on = Render(Snap(state, displays: ThreeDisplays()), opts))
        {
            Assert.True(TopBorderRed(on) > 150, $"both members on = red tally, got {TopBorderRed(on)}");
        }

        state.Output.Placements.First(p => p.ScreenId == "b").Enabled = false;
        using var off = Render(Snap(state, displays: ThreeDisplays()), opts);
        Assert.True(TopBorderRed(off) < 90, $"half a canvas is not on air, got {TopBorderRed(off)}");
    }

    [Fact]
    public void ATileNamingNothingOrAGhostDrawsASlateNotTheProgram()
    {
        var opts = new MultiviewOptions { ShowLabels = false, ShowTally = false };
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = "" });
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = "ghost" });

        using var bmp = Render(Snap(RedProgram()), opts);

        // Above the slate's centred caption, inside each tile's video rect.
        Assert.Equal(SlateFill, bmp.GetPixel(80, 60));
        Assert.Equal(SlateFill, bmp.GetPixel(240, 60));
    }

    [Fact]
    public void AnIdentifyBadgeNeverLandsInsideAMultiviewTile()
    {
        var snap = new ShowSnapshot
        {
            State = JsonUtil.Clone(RedProgram()),
            Version = 1,
            OutputsLive = true,
            IdentifyUntilUtc = new DateTime(2026, 8, 30, 10, 0, 3, DateTimeKind.Utc),
        };
        var opts = new MultiviewOptions { Columns = 1, ShowLabels = false, ShowTally = false };
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Program });

        using var bmp = Render(snap, opts, sinkKind: SinkKind.Output);

        var centre = bmp.GetPixel(160, 90);
        Assert.True(centre.Red > 200, $"a tile is a monitor, not an output — no identify card, got {centre}");
    }

    [Fact]
    public void AThumbnailMultiviewKeepsItsTilesFreeOfPipAndToneChips()
    {
        var state = JsonUtil.Clone(RedProgram());
        var opts = new MultiviewOptions { Columns = 1, ShowLabels = false, ShowTally = false };
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Program });

        using var quiet = Render(new ShowSnapshot { State = state, Version = 1 }, opts);
        using var tone = Render(new ShowSnapshot { State = state, Version = 1, ToneIndicator = "L+R" }, opts);

        Assert.Equal(quiet.Bytes, tone.Bytes); // /mv.jpg keeps exactly the picture it had
    }

    [Fact]
    public void ADenseGridRendersWithoutFailingAndSkipsCollapsedCells()
    {
        var opts = new MultiviewOptions { Columns = 0 };
        for (var i = 0; i < 200; i++) opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Program });

        using var sink = new SinkState();
        using var bmp = Render(Snap(RedProgram()), opts, reuse: sink);

        Assert.NotNull(bmp);
        Assert.Empty(sink.Failed);
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
