using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class ControlProtocolTests
{
    // A verb on the wire is a show action: the parser speaks the one vocabulary, nothing maps it later.
    [Theory]
    [InlineData("GO", ShowActionKind.OutputsOn)]          // frozen alias: outputs, never a cue
    [InlineData("stop", ShowActionKind.OutputsOff)]
    [InlineData("OUTPUTS ON", ShowActionKind.OutputsOn)]
    [InlineData("outputs off", ShowActionKind.OutputsOff)]
    [InlineData("  Identify  ", ShowActionKind.Identify)]
    [InlineData("NEXT", ShowActionKind.PresenterNext)]
    [InlineData("prev", ShowActionKind.PresenterPrev)]
    [InlineData("BACK", ShowActionKind.PresenterPrev)]
    [InlineData("BLACKOUT ON", ShowActionKind.BlackoutOn)]
    [InlineData("CUE GO", ShowActionKind.CueGo)]
    [InlineData("cue go 0123abcd", ShowActionKind.CueGo)]
    [InlineData("CUE STANDBY NEXT", ShowActionKind.CueStandby)]
    [InlineData("CUE STANDBY back", ShowActionKind.CueStandby)]
    [InlineData("CUE STANDBY 03.020", ShowActionKind.CueStandby)]
    [InlineData("CUE STANDBY Five-minute call", ShowActionKind.CueStandby)]
    [InlineData("CUE HOLD ON", ShowActionKind.CueHoldOn)]
    [InlineData("CUE HOLD off", ShowActionKind.CueHoldOff)]
    [InlineData("CUE ARM ON", ShowActionKind.ListArm)]
    [InlineData("CUE ARM OFF", ShowActionKind.ListDisarm)]
    [InlineData("STOPALL", ShowActionKind.StopAll)]
    [InlineData("STOP ALL", ShowActionKind.OutputsOff)]      // the frozen alias: an older build's STOP
    [InlineData("blackout off", ShowActionKind.BlackoutOff)]
    [InlineData("BLACKOUT", ShowActionKind.BlackoutToggle)]
    [InlineData("BLACKOUT TOGGLE", ShowActionKind.BlackoutToggle)]
    [InlineData("AUDIO PLAY", ShowActionKind.AudioPlay)]
    [InlineData("AUDIO STOP", ShowActionKind.AudioStop)]
    [InlineData("TONE ON", ShowActionKind.ToneOn)]
    [InlineData("TONE OFF", ShowActionKind.ToneOff)]
    [InlineData("DUCK ON", ShowActionKind.DuckOn)]
    [InlineData("duck off", ShowActionKind.DuckOff)]
    [InlineData("DUCK TOGGLE", ShowActionKind.DuckToggle)]
    [InlineData("DUCK", ShowActionKind.DuckToggle)]
    [InlineData("MUSIC PLAY", ShowActionKind.SpotifyPlay)]
    [InlineData("MUSIC PAUSE", ShowActionKind.SpotifyPause)]
    [InlineData("SPOTIFY NEXT", ShowActionKind.SpotifyNext)]      // the frozen alias
    [InlineData("MUSIC VOL 40", ShowActionKind.SpotifyVolume)]
    public void ParsesVerbs(string line, ShowActionKind kind)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.True(cmd.IsAction, line);
        Assert.Equal(kind, cmd.Action.Kind);
    }

    // The wire's own words — a query, a greeting, a line it cannot read — are the only commands that are not actions.
    [Theory]
    [InlineData("OUTPUTS", RemoteCommandKind.Unknown)]
    [InlineData("STATUS", RemoteCommandKind.Status)]
    [InlineData("PING", RemoteCommandKind.Ping)]
    [InlineData("CUE STANDBY", RemoteCommandKind.Unknown)]
    [InlineData("CUE LIST", RemoteCommandKind.CueList)]
    [InlineData("CUE NONSENSE", RemoteCommandKind.Unknown)]
    [InlineData("HELLO FOH deck", RemoteCommandKind.Hello)]
    [InlineData("HELLO", RemoteCommandKind.Unknown)]
    [InlineData("MUSIC", RemoteCommandKind.Unknown)]
    public void ParsesTheWiresOwnWords(string line, RemoteCommandKind kind)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.False(cmd.IsAction, line);
        Assert.Equal(kind, cmd.Kind);
        Assert.Equal(ShowActionKind.Unknown, cmd.Action.Kind);
    }

    [Fact]
    public void CueVerbsCarryTheirArguments()
    {
        Assert.Equal(new ShowAction(ShowActionKind.CueGo, "0123abcd"), ControlProtocol.Parse("CUE GO 0123abcd").Action);
        Assert.Equal(new ShowAction(ShowActionKind.CueGo), ControlProtocol.Parse("CUE GO").Action);
        Assert.Equal(new ShowAction(ShowActionKind.CueStandby, "03.020"), ControlProtocol.Parse("CUE STANDBY 03.020").Action);
        Assert.Equal(new ShowAction(ShowActionKind.CueStandby, "Five-minute call"), ControlProtocol.Parse("CUE STANDBY Five-minute call").Action);
        Assert.Equal(new ShowAction(ShowActionKind.CueStandby, "next"), ControlProtocol.Parse("CUE STANDBY NEXT").Action);
        Assert.Equal(new ShowAction(ShowActionKind.CueStandby, "prev"), ControlProtocol.Parse("CUE STANDBY back").Action);
        Assert.Equal(new ShowAction(ShowActionKind.ListArm, "caller"), ControlProtocol.Parse("CUE ARM ON").Action);
        Assert.Equal(new ShowAction(ShowActionKind.ListDisarm, "caller"), ControlProtocol.Parse("CUE ARM OFF").Action);
        Assert.Equal(new ShowAction(ShowActionKind.CueHoldOff), ControlProtocol.Parse("CUE HOLD OFF").Action);
        Assert.Equal("FOH deck", ControlProtocol.Parse("HELLO FOH deck").Text);
    }

    [Fact]
    public void ParsesLookBySlotAndName()
    {
        Assert.Equal(new ShowAction(ShowActionKind.ApplyLookHotkey, "7"), ControlProtocol.Parse("LOOK 7").Action);
        Assert.Equal(new ShowAction(ShowActionKind.ApplyLook, "Walk-in loop"), ControlProtocol.Parse("look Walk-in loop").Action);
    }

    [Fact]
    public void ParsesScreensAndGroups()
    {
        Assert.Equal(new ShowAction(ShowActionKind.ScreenOn, "2"), ControlProtocol.Parse("SCREEN 2 ON").Action);
        Assert.Equal(ShowActionKind.ScreenOff, ControlProtocol.Parse("screen 3 off").Action.Kind);
        Assert.Equal(ShowActionKind.ScreenToggle, ControlProtocol.Parse("SCREEN 1").Action.Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN x ON").Kind);

        Assert.Equal(new ShowAction(ShowActionKind.CanvasOn, "B"), ControlProtocol.Parse("GROUP b ON").Action);
        Assert.Equal(ShowActionKind.CanvasOff, ControlProtocol.Parse("GROUP A OFF").Action.Kind);
    }

    [Fact]
    public void UnknownAndEmptyAreSafe()
    {
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("FROBNICATE 12").Kind);
        Assert.Equal("ERR nope", ControlProtocol.Err("nope"));
        Assert.Equal("OK", ControlProtocol.Ok());
        Assert.Equal("OK x", ControlProtocol.Ok("x"));
    }
}

public class PresenterLogicTests
{
    [Fact]
    public void FirstClickStartsAtTheFirstStep()
    {
        Assert.Equal(0, PresenterLogic.Advance(-1, 5, +1, loop: false));
        Assert.Null(PresenterLogic.Advance(-1, 5, -1, loop: false));   // back before starting
        Assert.Equal(4, PresenterLogic.Advance(-1, 5, -1, loop: true));
    }

    [Fact]
    public void AdvancesAndStopsAtEndsWithoutLoop()
    {
        Assert.Equal(3, PresenterLogic.Advance(2, 5, +1, false));
        Assert.Null(PresenterLogic.Advance(4, 5, +1, false));
        Assert.Null(PresenterLogic.Advance(0, 5, -1, false));
    }

    [Fact]
    public void LoopsAtBothEnds()
    {
        Assert.Equal(0, PresenterLogic.Advance(4, 5, +1, true));
        Assert.Equal(4, PresenterLogic.Advance(0, 5, -1, true));
    }

    [Fact]
    public void EmptyListNeverMoves()
        => Assert.Null(PresenterLogic.Advance(-1, 0, +1, true));
}

public class WarpMathTests
{
    [Fact]
    public void UnwarpedCornersGiveIdentityMapping()
    {
        var p = new ScreenPlacement { ScreenId = "a" };
        var m = WarpMath.ForPlacement(p, 1920, 1080);
        AssertPoint(m.MapPoint(0, 0), 0, 0);
        AssertPoint(m.MapPoint(1920, 0), 1920, 0);
        AssertPoint(m.MapPoint(0, 1080), 0, 1080);
        AssertPoint(m.MapPoint(1920, 1080), 1920, 1080);
        AssertPoint(m.MapPoint(960, 540), 960, 540);
    }

    [Fact]
    public void CornersLandExactlyOnTheirOffsets()
    {
        var p = new ScreenPlacement
        {
            ScreenId = "a",
            WarpTlx = 40, WarpTly = 25,
            WarpTrx = -30, WarpTry = 10,
            WarpBlx = 5, WarpBly = -15,
            WarpBrx = -20, WarpBry = -35,
        };
        var m = WarpMath.ForPlacement(p, 1280, 720);
        AssertPoint(m.MapPoint(0, 0), 40, 25);
        AssertPoint(m.MapPoint(1280, 0), 1280 - 30, 10);
        AssertPoint(m.MapPoint(0, 720), 5, 720 - 15);
        AssertPoint(m.MapPoint(1280, 720), 1280 - 20, 720 - 35);
    }

    [Fact]
    public void PureKeystoneKeepsHorizontalEdgesStraight()
    {
        // Pull both top corners inward — a classic projector keystone.
        var m = WarpMath.QuadWarp(1000, 500,
            new SKPoint(100, 0), new SKPoint(900, 0), new SKPoint(0, 500), new SKPoint(1000, 500));
        var mid = m.MapPoint(500, 0);
        Assert.Equal(500, mid.X, 1);
        Assert.Equal(0, mid.Y, 1);
    }

    private static void AssertPoint(SKPoint actual, float x, float y)
    {
        Assert.Equal(x, actual.X, 1);
        Assert.Equal(y, actual.Y, 1);
    }
}

public class TransitionTests
{
    private static ShowSnapshot Snap(ShowState state, long version, bool isTake = true)
        => new() { State = JsonUtil.Clone(state), Version = version, IsTake = isTake };

    private static ShowState FlatState(string color)
    {
        var state = new ShowState();
        state.Transition.Enabled = true;
        state.Transition.DurationMs = 1000;
        state.Pattern.Kind = PatternKind.FlatField;
        state.Pattern.FlatField.Color = color;
        state.Pattern.FlatField.ShowLabel = false;
        state.Pattern.Canvas.FollowOutput = true;
        return state;
    }

    [Fact]
    public void KeyChangesOnContentNotOnMemoisedRepeat()
    {
        var snap = Snap(FlatState("#FF0000"), 1);
        Assert.Equal(snap.TransitionKeyFor(null), snap.TransitionKeyFor(null));

        var blue = Snap(FlatState("#0000FF"), 2);
        Assert.NotEqual(snap.TransitionKeyFor(null), blue.TransitionKeyFor(null));

        var black = FlatState("#FF0000");
        black.Blackout = true;
        Assert.NotEqual(snap.TransitionKeyFor(null), Snap(black, 3).TransitionKeyFor(null));
    }

    [Fact]
    public void CrossfadeBlendsThenSettles()
    {
        var engine = new PatternEngine();
        using var sink = new SinkState();
        var info = new SKImageInfo(160, 120, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);

        SKColor Render(ShowSnapshot snap, double time)
        {
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(160, 120),
                ReferenceSize = new SKSizeI(160, 120),
                Time = time,
                Now = new DateTime(2026, 8, 30, 12, 0, 0),
                UtcNow = new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc),
                Sink = SinkKind.Output,
                SinkIndex = 1,
                SinkLabel = "t",
            };
            engine.Render(surface.Canvas, snap, in ctx, sink);
            surface.Canvas.Flush();
            using var image = surface.Snapshot();
            using var bmp = SKBitmap.FromImage(image);
            return bmp.GetPixel(80, 60);
        }

        var red = Snap(FlatState("#FF0000"), 1);
        var blue = Snap(FlatState("#0000FF"), 2);

        var first = Render(red, 5.0);
        Assert.True(first.Red > 240 && first.Blue < 15);

        // The change begins: old content still fully on top at t=0…
        var start = Render(blue, 6.0);
        Assert.True(start.Red > 240, $"start of fade should still look red, got {start}");

        // …a real mix midway…
        var mid = Render(blue, 6.5);
        Assert.InRange(mid.Red, 60, 200);
        Assert.InRange(mid.Blue, 60, 200);

        // …and pure new content after the duration.
        var end = Render(blue, 7.2);
        Assert.True(end.Blue > 240 && end.Red < 15, $"after the fade expected blue, got {end}");
        Assert.Null(sink.TransitionFrom);
    }

    [Fact]
    public void DisabledTransitionsCutInstantly()
    {
        var engine = new PatternEngine();
        using var sink = new SinkState();
        var info = new SKImageInfo(80, 60, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);

        var redState = FlatState("#FF0000");
        redState.Transition.Enabled = false;
        var blueState = FlatState("#0000FF");
        blueState.Transition.Enabled = false;

        void Render(ShowSnapshot snap, double time)
        {
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(80, 60), ReferenceSize = new SKSizeI(80, 60),
                Time = time, Now = DateTime.Now, UtcNow = DateTime.UtcNow,
                Sink = SinkKind.Output, SinkIndex = 1, SinkLabel = "t",
            };
            engine.Render(surface.Canvas, snap, in ctx, sink);
        }

        Render(Snap(redState, 1), 1.0);
        Render(Snap(blueState, 2), 1.01);
        surface.Canvas.Flush();
        using var image = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(image);
        var c = bmp.GetPixel(40, 30);
        Assert.True(c.Blue > 240 && c.Red < 15);
    }
}
