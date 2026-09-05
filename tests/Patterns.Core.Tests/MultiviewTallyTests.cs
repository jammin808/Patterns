using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The multiview's tally: what each tile says about its target — program, the next TAKE, held,
/// locked, own, off — and which output, screen or canvas it is; the words from the snapshot,
/// and the chips painted on the tiles.
/// </summary>
public class MultiviewTallyTests
{
    private const string One = "planned:1";
    private const string Two = "planned:2";
    private const string Lobby = "planned:3";

    private static string CanvasKey => CanvasNameConfig.KeyFor(new[] { One, Two });

    /// <summary>Two screens flush in a row (canvas A) and a lobby screen on its own: the rig the wall shows.</summary>
    private static ShowState Rig() => RenderTestHarness.State(s =>
    {
        s.Pattern.Kind = PatternKind.FlatField;
        s.Pattern.FlatField.Color = "#0000FF";
        s.Pattern.FlatField.ShowLabel = false;
        s.Pattern.Canvas.FollowOutput = true;
        s.Transition.Enabled = false;
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = One, X = 0, Y = 0, Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, Enabled = true });
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = Two, X = 1920, Y = 0, Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, Enabled = true });
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = Lobby, X = 0, Y = 4000, Planned = true, PlannedWidth = 1280, PlannedHeight = 720, Enabled = true, CustomLabel = "Lobby" });
    });

    private static SnapshotBus Bus(ShowState state, bool live = true)
    {
        var bus = new SnapshotBus(state) { OutputsLive = live };
        bus.Publish(state);
        return bus;
    }

    private static MultiviewTileConfig Screen(string id) => new() { Source = MultiviewSource.Screen, ScreenId = id };

    private static string[] Words(ShowSnapshot snap, MultiviewTileConfig tile) => MultiviewTally.Badges(snap, tile).Select(b => b.Text).ToArray();

    [Fact]
    public void TheBadgesSayProgramNextHeldLockedOwnAndOff()
    {
        var state = Rig();
        var bus = Bus(state);
        var snap = bus.Current;
        Assert.Equal(new[] { CanvasKey, Lobby }, snap.Rig.Targets);

        var program = new MultiviewTileConfig { Source = MultiviewSource.Program };
        var preview = new MultiviewTileConfig { Source = MultiviewSource.Preview };
        Assert.Equal(new[] { "PGM" }, Words(snap, program));
        Assert.Equal(new[] { "PGM" }, Words(snap, Screen(CanvasKey)));
        Assert.Equal(new[] { "PGM" }, Words(snap, Screen(Lobby)));
        Assert.Equal(new[] { "NO PREVIEW" }, Words(snap, preview));
        Assert.Empty(Words(snap, new MultiviewTileConfig { Source = MultiviewSource.Clock }));
        Assert.Equal("ON A · 2", MultiviewTally.ProgramTargets(snap));   // wall order: the left column first, so the lobby is screen 2
        Assert.Equal("EDIT SAFE OFF", MultiviewTally.PreviewTargets(snap));
        Assert.True(MultiviewTally.IsOnAir(snap, program));
        Assert.False(MultiviewTally.IsOnAir(snap, preview));

        // EDIT SAFE open with the lobby held (un-armed): the canvas is what the next TAKE changes.
        bus.PublishSandbox(state);
        bus.UnarmedTargets = new HashSet<string>(StringComparer.Ordinal) { Lobby };
        bus.Publish(state);
        snap = bus.Current;
        Assert.Equal(new[] { "PGM", "NEXT" }, Words(snap, Screen(CanvasKey)));
        Assert.Equal(new[] { "PGM", "HELD" }, Words(snap, Screen(Lobby)));
        Assert.Equal(new[] { "PVW" }, Words(snap, preview));
        Assert.Equal("NEXT TAKE → A", MultiviewTally.PreviewTargets(snap));
        bus.UnarmedTargets = Array.Empty<string>();
        bus.Publish(state);
        Assert.Equal("NEXT TAKE → A · 2", MultiviewTally.PreviewTargets(bus.Current));

        // A lock says it all — the take leaves the target alone, and the program is no longer on it.
        ScreenRoles.SetLocked(state, Lobby, true);
        bus.Publish(state);
        snap = bus.Current;
        Assert.Equal(new[] { "PGM", "LOCKED" }, Words(snap, Screen(Lobby)));
        Assert.Equal("ON A", MultiviewTally.ProgramTargets(snap));
        Assert.Equal("NEXT TAKE → A", MultiviewTally.PreviewTargets(snap));
        // Unlocked, the picture it kept stays its own: OWN, and the next TAKE reaches it again.
        ScreenRoles.SetLocked(state, Lobby, false);
        bus.Publish(state);
        snap = bus.Current;
        Assert.Equal(new[] { "PGM", "OWN", "NEXT" }, Words(snap, Screen(Lobby)));
        Assert.Equal("ON A", MultiviewTally.ProgramTargets(snap));
        ContentTargets.SetOwnPattern(state, Lobby, false);

        // A repeater draws another target's picture: REP, never NEXT.
        state.Output.Placements.First(p => p.ScreenId == Lobby).MirrorOf = One;
        bus.Publish(state);
        snap = bus.Current;
        Assert.Equal(new[] { "PGM", "REP 1" }, Words(snap, Screen(Lobby)));
        state.Output.Placements.First(p => p.ScreenId == Lobby).MirrorOf = "";

        // Off: the screen switched off, the outputs closed, a blackout, a freeze.
        state.Output.Placements.First(p => p.ScreenId == Lobby).Enabled = false;
        bus.Publish(state);
        Assert.Equal("OFF", Words(bus.Current, Screen(Lobby))[0]);
        state.Output.Placements.First(p => p.ScreenId == Lobby).Enabled = true;
        bus.OutputsLive = false;
        bus.Publish(state);
        Assert.Equal(new[] { "OUTPUTS OFF" }, Words(bus.Current, program));
        Assert.Equal("OUTPUTS OFF", Words(bus.Current, Screen(Lobby))[0]);
        bus.OutputsLive = true;
        state.Blackout = true;
        bus.Publish(state);
        Assert.Equal(new[] { "BLACK" }, Words(bus.Current, program));
        state.Blackout = false;
        bus.Frozen = true;
        bus.Publish(state);
        Assert.Equal(new[] { "PGM", "FROZEN" }, Words(bus.Current, program));
        Assert.Equal(new[] { "PGM", "FROZEN", "NEXT" }, Words(bus.Current, Screen(CanvasKey)));

        // A tile naming nothing, or something not in this rig.
        Assert.Equal(new[] { "NOT IN RIG" }, Words(bus.Current, Screen("ghost")));
        Assert.Equal(new[] { "NO TARGET" }, Words(bus.Current, Screen("")));
    }

    [Fact]
    public void TheCaptionsNameTheOutputTheScreenAndTheCanvas()
    {
        var state = Rig();
        state.Output.Placements.Add(new ScreenPlacement
        {
            ScreenId = "ndi-own", X = 0, Y = 8000, Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, Enabled = true, Virtual = "ndi:sender1",
        });
        state.Output.Placements.First(p => p.ScreenId == Lobby).Role = ScreenRole.Info;
        var snap = Bus(state).Current;

        Assert.Equal(("PROGRAM", "ON A · 2 · 3"), MultiviewTally.Caption(snap, new MultiviewTileConfig { Source = MultiviewSource.Program }));
        var (name, kind) = MultiviewTally.Caption(snap, Screen(CanvasKey));
        Assert.StartsWith("A · ", name);
        Assert.Equal("CANVAS A · 3840×1080 · 2 SCREENS", kind);
        (name, kind) = MultiviewTally.Caption(snap, Screen(Lobby));
        Assert.Equal("2 · Lobby", name);
        Assert.Equal("PLANNED SCREEN 2 · 1280×720 · INFO", kind);
        (_, kind) = MultiviewTally.Caption(snap, Screen("ndi-own"));
        Assert.Equal("NDI SEND 3 · 1920×1080", kind);
        Assert.Equal(("PREVIEW", "EDIT SAFE OFF"), MultiviewTally.Caption(snap, new MultiviewTileConfig { Source = MultiviewSource.Preview }));
        Assert.Equal(("CLOCK", ""), MultiviewTally.Caption(snap, new MultiviewTileConfig { Source = MultiviewSource.Clock }));
        Assert.Equal(("—", "PICK A SCREEN OR CANVAS"), MultiviewTally.Caption(snap, Screen("")));
        Assert.Equal("NOT IN THIS RIG", MultiviewTally.Caption(snap, Screen("ghost")).Kind);
        Assert.Equal(("NDI FEED", "NDI FEED"), MultiviewTally.Caption(snap, new MultiviewTileConfig { Source = MultiviewSource.NdiFeed }));
        Assert.Equal("CAPTURE", MultiviewTally.Caption(snap, new MultiviewTileConfig { Source = MultiviewSource.Capture }).Kind);
        var labelled = new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = Lobby, Label = "Foyer wall" };
        Assert.Equal(("Foyer wall", "PLANNED SCREEN 2 · 1280×720 · INFO"), MultiviewTally.Caption(snap, labelled));
        Assert.Equal("1", MultiviewTally.Short(snap, One));
        Assert.Equal("A", MultiviewTally.Short(snap, CanvasKey));
        Assert.Equal("ghost", MultiviewTally.Short(snap, "ghost"));
    }

    private static SKBitmap Render(ShowSnapshot snap, MultiviewOptions opts, int w, int h)
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
            Now = new DateTime(2026, 9, 5, 12, 0, 0),
            UtcNow = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc),
            Sink = SinkKind.Output,
            SinkIndex = 0,
            SinkLabel = "mv-tally",
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

    private static bool Has(SKBitmap bmp, int x0, int y0, int w, int h, SKColor c)
    {
        for (var y = y0; y < y0 + h; y += 2)
        {
            for (var x = x0; x < x0 + w; x += 2)
            {
                var px = bmp.GetPixel(x, y);
                if (Math.Abs(px.Red - c.Red) < 28 && Math.Abs(px.Green - c.Green) < 28 && Math.Abs(px.Blue - c.Blue) < 28) return true;
            }
        }
        return false;
    }

    [Fact]
    public void TheBadgesArePaintedOnTheTilesAndOnlyWithTheTally()
    {
        var state = Rig();
        var bus = Bus(state);
        bus.PublishSandbox(state);
        bus.UnarmedTargets = new HashSet<string>(StringComparer.Ordinal) { Lobby };
        bus.Publish(state);
        var opts = new MultiviewOptions { ShowLabels = true, ShowTally = true, Columns = 2 };
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Program });
        opts.Tiles.Add(Screen(CanvasKey));
        opts.Tiles.Add(Screen(Lobby));
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Preview });

        // Two by two on 640×360, the content blue: a chip's colour is found only where its tile is.
        using (var bmp = Render(bus.Current, opts, 640, 360))
        {
            Assert.True(Has(bmp, 0, 0, 320, 180, MultiviewTally.Program), "the PROGRAM tile wears a red PGM chip");
            Assert.True(Has(bmp, 320, 0, 320, 180, MultiviewTally.Preview), "the canvas tile wears a green NEXT chip");
            Assert.True(Has(bmp, 0, 180, 320, 180, MultiviewTally.Held), "the held lobby wears an amber HELD chip");
            Assert.False(Has(bmp, 320, 0, 320, 180, MultiviewTally.Held), "the canvas is armed: no amber on it");
            Assert.True(Has(bmp, 320, 180, 320, 180, MultiviewTally.Preview), "the PREVIEW tile is green");
        }

        // Tally off: no chips, no coloured border, the picture alone.
        opts.ShowTally = false;
        using (var plain = Render(bus.Current, opts, 640, 360))
        {
            Assert.False(Has(plain, 0, 0, 320, 180, MultiviewTally.Program));
            Assert.False(Has(plain, 0, 180, 320, 180, MultiviewTally.Held));
            Assert.False(Has(plain, 320, 0, 320, 180, MultiviewTally.Preview));
        }
    }
}
