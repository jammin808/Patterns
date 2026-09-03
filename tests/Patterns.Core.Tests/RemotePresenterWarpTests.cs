using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class ControlProtocolTests
{
    [Theory]
    [InlineData("GO", RemoteCommandKind.OutputsOn)]          // frozen alias: outputs, never a cue
    [InlineData("stop", RemoteCommandKind.OutputsOff)]
    [InlineData("OUTPUTS ON", RemoteCommandKind.OutputsOn)]
    [InlineData("outputs off", RemoteCommandKind.OutputsOff)]
    [InlineData("OUTPUTS", RemoteCommandKind.Unknown)]
    [InlineData("  Identify  ", RemoteCommandKind.Identify)]
    [InlineData("NEXT", RemoteCommandKind.Next)]
    [InlineData("prev", RemoteCommandKind.Prev)]
    [InlineData("BACK", RemoteCommandKind.Prev)]
    [InlineData("STATUS", RemoteCommandKind.Status)]
    [InlineData("PING", RemoteCommandKind.Ping)]
    [InlineData("BLACKOUT ON", RemoteCommandKind.BlackoutOn)]
    [InlineData("blackout off", RemoteCommandKind.BlackoutOff)]
    [InlineData("BLACKOUT", RemoteCommandKind.BlackoutToggle)]
    [InlineData("BLACKOUT TOGGLE", RemoteCommandKind.BlackoutToggle)]
    [InlineData("AUDIO PLAY", RemoteCommandKind.AudioPlay)]
    [InlineData("AUDIO STOP", RemoteCommandKind.AudioStop)]
    [InlineData("TONE ON", RemoteCommandKind.ToneOn)]
    [InlineData("TONE OFF", RemoteCommandKind.ToneOff)]
    public void ParsesVerbs(string line, RemoteCommandKind kind)
        => Assert.Equal(kind, ControlProtocol.Parse(line).Kind);

    [Fact]
    public void ParsesLookBySlotAndName()
    {
        var slot = ControlProtocol.Parse("LOOK 7");
        Assert.Equal(RemoteCommandKind.Look, slot.Kind);
        Assert.Equal(7, slot.IntArg);

        var name = ControlProtocol.Parse("look Walk-in loop");
        Assert.Equal(RemoteCommandKind.Look, name.Kind);
        Assert.Equal(0, name.IntArg);
        Assert.Equal("Walk-in loop", name.TextArg);
    }

    [Fact]
    public void ParsesScreensAndGroups()
    {
        Assert.Equal((RemoteCommandKind.ScreenOn, 2), (ControlProtocol.Parse("SCREEN 2 ON").Kind, ControlProtocol.Parse("SCREEN 2 ON").IntArg));
        Assert.Equal(RemoteCommandKind.ScreenOff, ControlProtocol.Parse("screen 3 off").Kind);
        Assert.Equal(RemoteCommandKind.ScreenToggle, ControlProtocol.Parse("SCREEN 1").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN x ON").Kind);

        var g = ControlProtocol.Parse("GROUP b ON");
        Assert.Equal(RemoteCommandKind.GroupOn, g.Kind);
        Assert.Equal("B", g.TextArg);
        Assert.Equal(RemoteCommandKind.GroupOff, ControlProtocol.Parse("GROUP A OFF").Kind);
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
    private static ShowSnapshot Snap(ShowState state, long version)
        => new() { State = JsonUtil.Clone(state), Version = version };

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
