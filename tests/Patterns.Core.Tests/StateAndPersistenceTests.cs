using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class ColorUtilTests
{
    [Theory]
    [InlineData("#FFFFFF", 255, 255, 255, 255)]
    [InlineData("FFFFFF", 255, 255, 255, 255)]
    [InlineData("#3EC1F3", 62, 193, 243, 255)]
    [InlineData("#F0A", 255, 0, 170, 255)]
    [InlineData("#80FF0000", 255, 0, 0, 128)]
    public void ParsesHexForms(string hex, byte r, byte g, byte b, byte a)
    {
        Assert.True(ColorUtil.TryParse(hex, out var c));
        Assert.Equal(new SKColor(r, g, b, a), c);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#GGGGGG")]
    [InlineData("#12345")]
    [InlineData("hello")]
    public void RejectsBadHex(string hex) => Assert.False(ColorUtil.TryParse(hex, out _));

    [Fact]
    public void ListParsingSkipsJunkAndNeverReturnsEmpty()
    {
        var list = ColorUtil.ParseList("#FF0000, junk ;#00FF00", SKColors.White);
        Assert.Equal(2, list.Length);
        Assert.Equal(SKColors.Red, list[0]);

        var fallback = ColorUtil.ParseList("junk", SKColors.White);
        Assert.Single(fallback);
        Assert.Equal(SKColors.White, fallback[0]);
    }
}

public class SnapshotBusTests
{
    [Fact]
    public void SnapshotsAreIsolatedFromLaterEdits()
    {
        var state = new ShowState();
        state.Pattern.Grid.CellSize = 100;
        var bus = new SnapshotBus(state);

        bus.Publish(state);
        var snap = bus.Current;
        state.Pattern.Grid.CellSize = 999;

        Assert.Equal(100, snap.State.Pattern.Grid.CellSize);
        bus.Publish(state);
        Assert.Equal(999, bus.Current.State.Pattern.Grid.CellSize);
        Assert.True(bus.Current.Version > snap.Version);
    }

    [Fact]
    public void PatternForHonoursCustomFlagAndFallsBackToProgram()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Grid;
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "s2", UseCustomPattern = true });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "s3", UseCustomPattern = false });
        var a2 = new OutputAssignment { ScreenId = "s2" };
        a2.Pattern.Kind = PatternKind.Focus;
        state.Independent.Add(a2);
        var a3 = new OutputAssignment { ScreenId = "s3" };
        a3.Pattern.Kind = PatternKind.Motion;
        state.Independent.Add(a3);
        var bus = new SnapshotBus(state);
        bus.Publish(state);

        Assert.Equal(PatternKind.Focus, bus.Current.PatternFor("s2").Kind);
        // Assignment exists but the custom flag is off — program wins.
        Assert.Equal(PatternKind.Grid, bus.Current.PatternFor("s3").Kind);
        Assert.Equal(PatternKind.Grid, bus.Current.PatternFor("unknown").Kind);
        Assert.Equal(PatternKind.Grid, bus.Current.PatternFor(null).Kind);
    }
}

public class ChangeTrackerTests
{
    [Fact]
    public void DeepChangesBubbleUp()
    {
        var state = new ShowState();
        var hits = 0;
        _ = new ChangeTracker(state, () => hits++);

        state.Pattern.LedWall.Columns = 12;
        Assert.Equal(1, hits);

        state.Overlays.Clock.Enabled = true;
        Assert.Equal(2, hits);

        state.Independent.Add(new OutputAssignment { ScreenId = "x" });
        Assert.Equal(3, hits);

        // Items added later are wired too.
        state.Independent[0].Pattern.Grid.CellSize = 7;
        Assert.True(hits >= 4);
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "patterns-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void RoundTripsFullState()
    {
        var store = new SettingsStore(_dir);
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.ProjectionBlend;
        state.Pattern.Blend.OverlapPx = 512;
        state.Brand.PrimaryColor = "#123456";
        state.Countdown.TargetTime = "18:45";
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "0:1920x1080@0,0", X = 40, Y = 8, Enabled = false, UseCustomPattern = true });
        state.Blackout = true; // runtime-only: must NOT persist

        store.Save(state);
        var loaded = store.Load();

        Assert.Equal(PatternKind.ProjectionBlend, loaded.Pattern.Kind);
        Assert.Equal(512, loaded.Pattern.Blend.OverlapPx);
        Assert.Equal("#123456", loaded.Brand.PrimaryColor);
        Assert.Equal("18:45", loaded.Countdown.TargetTime);
        var placement = Assert.Single(loaded.Output.Placements);
        Assert.Equal(40, placement.X);
        Assert.False(placement.Enabled);
        Assert.True(placement.UseCustomPattern);
        // Blackout persists in the file; the app deliberately resets it at startup.
        Assert.True(loaded.Blackout);
    }

    [Fact]
    public void CorruptFileQuarantinesAndFallsBackToBak()
    {
        var store = new SettingsStore(_dir);
        var state = new ShowState();
        state.Pattern.Grid.CellSize = 111;
        store.Save(state);
        state.Pattern.Grid.CellSize = 222;
        store.Save(state); // now main=222, bak=111

        File.WriteAllText(store.SettingsPath, "{ this is not json !!!");
        var loaded = store.Load();
        Assert.Equal(111, loaded.Pattern.Grid.CellSize); // recovered from .bak
        Assert.Contains(Directory.EnumerateFiles(_dir), f => f.Contains(".corrupt-"));
    }

    [Fact]
    public void TotalLossYieldsDefaultsNotCrash()
    {
        var store = new SettingsStore(_dir);
        File.WriteAllText(store.SettingsPath, "garbage");
        File.WriteAllText(store.SettingsPath + ".bak", "also garbage");
        var loaded = store.Load();
        Assert.Equal(PatternKind.Grid, loaded.Pattern.Kind);
    }

    [Fact]
    public void PresetsRoundTrip()
    {
        var store = new SettingsStore(_dir);
        var pattern = new PatternConfig { Kind = PatternKind.LedWall };
        pattern.LedWall.TileWidth = 168;
        store.SavePreset("Main wall: FOH", pattern);

        var presets = store.ListPresets();
        var entry = Assert.Single(presets);
        var loaded = store.LoadPreset(entry.Path);
        Assert.NotNull(loaded);
        Assert.Equal(PatternKind.LedWall, loaded!.Kind);
        Assert.Equal(168, loaded.LedWall.TileWidth);
    }
}

public class ModelCopierTests
{
    [Fact]
    public void CopyPreservesTargetReferencesAndValues()
    {
        var src = new ShowState();
        src.Pattern.Kind = PatternKind.Motion;
        src.Pattern.Motion.SpeedPxPerSec = 777;
        src.Independent.Add(new OutputAssignment { ScreenId = "a" });
        src.Brand.CompanyName = "ACME";

        var dst = new ShowState();
        var patternRef = dst.Pattern;
        var brandRef = dst.Brand;

        ModelCopier.Copy(src, dst);

        Assert.Same(patternRef, dst.Pattern);
        Assert.Same(brandRef, dst.Brand);
        Assert.Equal(PatternKind.Motion, dst.Pattern.Kind);
        Assert.Equal(777, dst.Pattern.Motion.SpeedPxPerSec);
        Assert.Equal("ACME", dst.Brand.CompanyName);
        Assert.Single(dst.Independent);
        Assert.NotSame(src.Independent[0], dst.Independent[0]);
    }

    [Fact]
    public void CopiedGraphIsJsonEquivalent()
    {
        var src = new ShowState();
        src.Pattern.Particles.ColorsCsv = "#111111,#222222";
        src.Ndi.Senders.Add(new NdiSenderConfig { Name = "Main", TenBit = true });
        var dst = new ShowState();
        ModelCopier.Copy(src, dst);
        Assert.Equal(JsonUtil.Serialize(src), JsonUtil.Serialize(dst));
    }
}
