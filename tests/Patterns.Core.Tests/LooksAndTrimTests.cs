using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class LookServiceTests
{
    [Fact]
    public void CaptureApplyRoundTripsContentState()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.ColorBars;
        state.Pattern.FlatField.Color = "#123456";
        state.Overlays.Message.Enabled = true;
        state.Overlays.Message.Text = "WALK-IN";
        state.Countdown.Enabled = true;
        state.Countdown.TargetTime = "19:00";
        state.Blackout = true;
        state.Independent.Add(new OutputAssignment { ScreenId = "scr-2" });
        state.Independent[0].Pattern.Kind = PatternKind.Grid;

        var json = LookService.Capture(state);

        // The operator moves on…
        state.Pattern.Kind = PatternKind.Motion;
        state.Overlays.Message.Text = "SOMETHING ELSE";
        state.Countdown.Enabled = false;
        state.Blackout = false;
        state.Independent.Clear();

        // …and the look brings everything back, in place.
        var pattern = state.Pattern;
        var message = state.Overlays.Message;
        Assert.True(LookService.Apply(json, state));

        Assert.Same(pattern, state.Pattern);   // bindings keep their references
        Assert.Same(message, state.Overlays.Message);
        Assert.Equal(PatternKind.ColorBars, state.Pattern.Kind);
        Assert.Equal("#123456", state.Pattern.FlatField.Color);
        Assert.Equal("WALK-IN", state.Overlays.Message.Text);
        Assert.True(state.Countdown.Enabled);
        Assert.Equal("19:00", state.Countdown.TargetTime);
        Assert.True(state.Blackout);
        var assignment = Assert.Single(state.Independent);
        Assert.Equal("scr-2", assignment.ScreenId);
        Assert.Equal(PatternKind.Grid, assignment.Pattern.Kind);
    }

    [Fact]
    public void LooksLeaveTheRigAlone()
    {
        var state = new ShowState();
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", X = 42, Rotation = OutputRotation.Rot90 });
        state.Ndi.Senders.Add(new NdiSenderConfig { Name = "Feed" });
        var json = LookService.Capture(state);

        state.Output.Placements[0].X = 99;
        state.Ndi.Senders[0].Name = "Renamed";
        Assert.True(LookService.Apply(json, state));

        // Screen arrangement, rotation and NDI infrastructure are not part of a look.
        Assert.Equal(99, state.Output.Placements[0].X);
        Assert.Equal(OutputRotation.Rot90, state.Output.Placements[0].Rotation);
        Assert.Equal("Renamed", state.Ndi.Senders[0].Name);
    }

    [Fact]
    public void BrokenJsonIsRejected()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Focus;
        Assert.False(LookService.Apply("{not json", state));
        Assert.False(LookService.Apply("", state));
        Assert.Equal(PatternKind.Focus, state.Pattern.Kind);
    }

    [Fact]
    public void CueFiresOnItsMinuteOncePerDay()
    {
        var cue = new CueConfig { Enabled = true, Time = "18:00", LookName = "Walk-in" };
        var at = new DateTime(2026, 8, 30, 18, 0, 20);

        Assert.False(LookService.ShouldFire(cue, at.AddMinutes(-1)));
        Assert.True(LookService.ShouldFire(cue, at));

        cue.LastFiredDate = at.Date;
        Assert.False(LookService.ShouldFire(cue, at.AddSeconds(10)));   // fired already today
        Assert.True(LookService.ShouldFire(cue, at.AddDays(1)));        // fresh tomorrow

        Assert.False(LookService.ShouldFire(new CueConfig { Enabled = false, Time = "18:00", LookName = "x" }, at));
        Assert.False(LookService.ShouldFire(new CueConfig { Enabled = true, Time = "18:00", LookName = "" }, at));
        Assert.False(LookService.ShouldFire(new CueConfig { Enabled = true, Time = "25:99", LookName = "x" }, at));
    }

    [Fact]
    public void NextCueRollsPastMidnight()
    {
        var cues = new[]
        {
            new CueConfig { Enabled = true, Time = "09:00", LookName = "Morning" },
            new CueConfig { Enabled = true, Time = "18:00", LookName = "Evening" },
            new CueConfig { Enabled = false, Time = "12:00", LookName = "Off" },
        };

        var midday = new DateTime(2026, 8, 30, 12, 0, 0);
        var next = LookService.NextCue(cues, midday);
        Assert.Equal("Evening", next!.Value.Cue.LookName);
        Assert.Equal(new DateTime(2026, 8, 30, 18, 0, 0), next.Value.At);

        var night = new DateTime(2026, 8, 30, 21, 0, 0);
        next = LookService.NextCue(cues, night);
        Assert.Equal("Morning", next!.Value.Cue.LookName);
        Assert.Equal(new DateTime(2026, 8, 31, 9, 0, 0), next.Value.At);

        Assert.Null(LookService.NextCue(Array.Empty<CueConfig>(), midday));
    }
}

public class TrimTableTests
{
    [Fact]
    public void NeutralSettingsAreAnIdentityTable()
    {
        var t = TrimTable.Build(100, 1.0, 100);
        for (var i = 0; i < 256; i++)
        {
            Assert.Equal((byte)i, t[i]);
        }
    }

    [Fact]
    public void BrightnessScalesLinearly()
    {
        var t = TrimTable.Build(50, 1.0, 100);
        Assert.Equal(0, t[0]);
        Assert.InRange(t[255], 127, 128);
        Assert.InRange(t[128], 63, 65);
    }

    [Fact]
    public void GammaBendsTheCurve()
    {
        var t = TrimTable.Build(100, 2.0, 100);
        Assert.Equal(0, t[0]);
        Assert.Equal(255, t[255]);          // endpoints pinned
        Assert.InRange(t[128], 63, 66);     // (0.502)^2 ≈ 0.252

        var lift = TrimTable.Build(100, 0.5, 100);
        Assert.True(lift[64] > 64);         // gamma < 1 lifts shadows
    }

    [Fact]
    public void TablesAreMonotonic()
    {
        foreach (var t in new[]
                 {
                     TrimTable.Build(150, 0.6, 120),
                     TrimTable.Build(35, 2.2, 60),
                 })
        {
            for (var i = 1; i < 256; i++)
            {
                Assert.True(t[i] >= t[i - 1]);
            }
        }
    }

    [Fact]
    public void ChannelGainOnlyTouchesItsChannel()
    {
        var p = new ScreenPlacement { ScreenId = "a", TrimRPct = 50 };
        var (r, g, b) = TrimTable.BuildRgb(p);
        Assert.InRange(r[255], 127, 128);
        Assert.Equal(255, g[255]);
        Assert.Equal(255, b[255]);
    }

    [Fact]
    public void KeyChangesWithAnySetting()
    {
        var a = new ScreenPlacement { ScreenId = "a" };
        var b = new ScreenPlacement { ScreenId = "b" };
        Assert.Equal(TrimTable.KeyOf(a), TrimTable.KeyOf(b));
        b.Gamma = 1.8;
        Assert.NotEqual(TrimTable.KeyOf(a), TrimTable.KeyOf(b));
    }

    [Fact]
    public void PlacementFlagsTrimsOnlyWhenNonNeutral()
    {
        var p = new ScreenPlacement { ScreenId = "a" };
        Assert.False(p.HasTrims);
        p.TrimBPct = 90;
        Assert.True(p.HasTrims);
    }
}

public class LedCustomMapTests
{
    private static LedWallOptions Map(params (int X, int Y, int W, int H)[] tiles)
    {
        var o = new LedWallOptions { UseCustomMap = true };
        foreach (var t in tiles)
        {
            o.CustomTiles.Add(new LedTileConfig { X = t.X, Y = t.Y, Width = t.W, Height = t.H });
        }
        return o;
    }

    [Fact]
    public void CanvasIsTheBoundingBoxOfAllPanels()
    {
        var o = Map((0, 0, 128, 128), (128, 0, 64, 128), (0, 128, 96, 96));
        Assert.Equal(new SKSizeI(192, 224), CanvasResolver.LedCustomCanvas(o));
    }

    [Fact]
    public void EmptyMapFallsBackToASaneCanvas()
        => Assert.Equal(new SKSizeI(256, 256), CanvasResolver.LedCustomCanvas(new LedWallOptions()));

    [Fact]
    public void ResolveUsesTheCustomCanvasWhenMapped()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.LedWall;
        state.Pattern.LedWall.UseCustomMap = true;
        state.Pattern.LedWall.CustomTiles.Add(new LedTileConfig { X = 0, Y = 0, Width = 200, Height = 100 });
        state.Pattern.LedWall.CustomTiles.Add(new LedTileConfig { X = 200, Y = 0, Width = 100, Height = 100 });

        Assert.Equal(new SKSizeI(300, 100), CanvasResolver.Resolve(state.Pattern, new SKSizeI(1920, 1080)));

        state.Pattern.LedWall.UseCustomMap = false;
        var grid = CanvasResolver.Led(state.Pattern.LedWall);
        Assert.Equal(grid.Canvas, CanvasResolver.Resolve(state.Pattern, new SKSizeI(1920, 1080)));
    }

    [Fact]
    public void IrregularMapRendersPanelsAndGaps()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.LedWall;
        var led = state.Pattern.LedWall;
        led.UseCustomMap = true;
        led.ShowTileBorders = true;
        led.AlternateTint = true;
        // Two panels with a 64 px gap between them.
        led.CustomTiles.Add(new LedTileConfig { X = 0, Y = 0, Width = 128, Height = 128 });
        led.CustomTiles.Add(new LedTileConfig { X = 192, Y = 0, Width = 128, Height = 128 });

        using var bmp = RenderTestHarness.Render(state, 320, 128);

        // Panel areas carry content; the gap stays background-dark.
        Assert.NotEqual(bmp.GetPixel(2, 2), bmp.GetPixel(160, 64));
        var colors = new HashSet<uint>();
        for (var x = 0; x < 320; x += 7)
        {
            for (var y = 0; y < 128; y += 7)
            {
                colors.Add((uint)bmp.GetPixel(x, y));
            }
        }
        Assert.True(colors.Count > 2, $"expected a drawn map, saw {colors.Count} colours");
    }
}
