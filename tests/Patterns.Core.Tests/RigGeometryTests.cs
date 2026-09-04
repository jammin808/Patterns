using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The rig's pixel geometry: which placements have a size behind them, which join into
/// canvases, and therefore the size, slice and name of every content target. The wall, the
/// output windows and the multiview all read this one table, so its answers are the contract.
/// </summary>
public class RigGeometryTests
{
    private static readonly string Key = CanvasNameConfig.KeyFor(new[] { "a", "b" });

    /// <summary>a and b flush (one canvas), c standing alone — the rig WallTests uses.</summary>
    private static ShowState Rig()
    {
        var state = new ShowState();
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", X = 0, Y = 0, Enabled = true });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", X = 1920, Y = 0, Enabled = true });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "c", X = 6000, Y = 0, Enabled = true });
        return state;
    }

    private static Dictionary<string, ScreenGeometry> Displays() => new(StringComparer.Ordinal)
    {
        ["a"] = new ScreenGeometry(1920, 1080, "Left"),
        ["b"] = new ScreenGeometry(1920, 1080, "Right"),
        ["c"] = new ScreenGeometry(1920, 1080, "Lobby"),
    };

    [Fact]
    public void TargetsAreCanvasesFirstThenStandAloneScreensInWallOrder()
    {
        var geo = RigGeometry.Build(Rig(), Displays());

        Assert.Equal(new[] { Key, "c" }, geo.Targets);
        Assert.Equal(new[] { "a", "b", "c" }, geo.Screens.Select(s => s.Id));
        Assert.Equal("A", geo.LetterOf(Key));
        Assert.Equal(new[] { "a", "b" }, geo.MembersOf(Key));
        Assert.Equal(Key, geo.TargetOf("a"));
        Assert.Equal("c", geo.TargetOf("c"));
        Assert.Equal(3, geo.NumberOf("c"));
        Assert.Equal("Lobby", geo.DisplayLabel("c"));
    }

    [Fact]
    public void TheProgramTakesTheFirstTargetsShapeAndSizesFollowTheUnion()
    {
        var geo = RigGeometry.Build(Rig(), Displays());

        Assert.Equal(new SKSizeI(3840, 1080), geo.SizeOf(null));   // the program = the first target
        Assert.Equal(new SKSizeI(3840, 1080), geo.SizeOf(Key));
        Assert.Equal(new SKSizeI(1920, 1080), geo.SizeOf("c"));
        Assert.Equal(new SKSizeI(1920, 1080), geo.SizeOf("a"));    // a member still has its own size
    }

    [Fact]
    public void AnEmptyRigAnswersSixteenByNineForEveryQuestionWithoutThrowing()
    {
        var geo = RigGeometry.Empty;

        Assert.Equal(new SKSizeI(1920, 1080), geo.SizeOf(null));
        Assert.Equal(new SKSizeI(1920, 1080), geo.SizeOf(""));
        Assert.Equal(new SKSizeI(1920, 1080), geo.SizeOf("ghost"));
        Assert.Equal(new SKSizeI(1920, 1080), geo.SizeOf("x+y"));
        Assert.Empty(geo.Targets);
        Assert.Empty(geo.Screens);
        Assert.Equal("", geo.LetterOf("x+y"));
        Assert.Equal("", geo.DisplayLabel("ghost"));
        Assert.Empty(geo.MembersOf("x+y"));
        Assert.Equal(0, geo.NumberOf("ghost"));
        Assert.Equal(16f / 9f, geo.ViewportForTile("").Aspect, 4);
    }

    [Fact]
    public void APlannedScreenIsSizedFromTheShowFileWithNoDisplayTable()
    {
        // Pre-programming at the desk, nothing plugged in: the model carries the sizes.
        var state = new ShowState();
        state.Output.Placements.Add(new ScreenPlacement
        {
            ScreenId = "planned:1", X = 0, Y = 0, Planned = true, PlannedWidth = 2560, PlannedHeight = 1440,
        });
        state.Output.Placements.Add(new ScreenPlacement
        {
            ScreenId = "planned:2", X = 2560, Y = 0, Planned = true, PlannedWidth = 2560, PlannedHeight = 1440,
        });

        var geo = RigGeometry.Build(state, RigGeometry.NoDisplays);
        var key = CanvasNameConfig.KeyFor(new[] { "planned:1", "planned:2" });

        Assert.Equal(new[] { key }, geo.Targets);
        Assert.Equal(new SKSizeI(5120, 1440), geo.SizeOf(key));
        Assert.Equal(new SKSizeI(2560, 1440), geo.SizeOf("planned:1"));
        Assert.Equal(new SKSizeI(5120, 1440), geo.SizeOf(null));
    }

    [Fact]
    public void AMemberOfAJoinedCanvasRendersItsOwnSliceOfTheCanvas()
    {
        var geo = RigGeometry.Build(Rig(), Displays());

        var b = geo.ViewportForTile("b");
        Assert.Equal(Key, b.TargetId);
        Assert.Equal(new SKSizeI(3840, 1080), b.ReferenceSize);
        Assert.Equal(new SKPointI(1920, 0), b.Origin);
        Assert.Equal(new SKSizeI(1920, 1080), b.ViewportSize);

        var a = geo.ViewportForTile("a");
        Assert.Equal(Key, a.TargetId);
        Assert.Equal(new SKPointI(0, 0), a.Origin);

        var canvas = geo.ViewportForTile(Key);
        Assert.Equal(Key, canvas.TargetId);
        Assert.Equal(default, canvas.Origin);
        Assert.Equal(new SKSizeI(3840, 1080), canvas.ViewportSize);
        Assert.Equal(3840f / 1080f, canvas.Aspect, 4);

        // An empty id is the program, drawn whole.
        var program = geo.ViewportForTile("");
        Assert.Null(program.TargetId);
        Assert.Equal(new SKSizeI(3840, 1080), program.ViewportSize);
    }

    [Fact]
    public void RotationSwapsAScreensShapeAndTheCanvasUnionFollows()
    {
        var state = Rig();
        state.Output.Placements.First(p => p.ScreenId == "c").Rotation = OutputRotation.Rot90;
        var geo = RigGeometry.Build(state, Displays());
        Assert.Equal(new SKSizeI(1080, 1920), geo.SizeOf("c"));

        state.Output.Placements.First(p => p.ScreenId == "b").Rotation = OutputRotation.Rot270;
        geo = RigGeometry.Build(state, Displays());
        Assert.Equal(new SKSizeI(1080, 1920), geo.SizeOf("b"));
        Assert.Equal(new SKSizeI(3000, 1920), geo.SizeOf(Key)); // 0..1920 plus 1920..3000, tallest wins

        // One rotation rule, shared with the output windows.
        var portrait = new ScreenPlacement { Rotation = OutputRotation.Rot90 };
        Assert.Equal(new SKSizeI(1080, 1920), RigGeometry.EffectiveSize(portrait, new SKSizeI(1920, 1080)));
        var upright = new ScreenPlacement { Rotation = OutputRotation.Rot180 };
        Assert.Equal(new SKSizeI(1920, 1080), RigGeometry.EffectiveSize(upright, new SKSizeI(1920, 1080)));
    }

    [Fact]
    public void EnabledIsNotGeometryAndAnUnknownIdFallsBackWithoutThrowing()
    {
        var state = Rig();
        foreach (var p in state.Output.Placements) p.Enabled = false;
        var geo = RigGeometry.Build(state, Displays());

        // The wall draws switched-off targets too, so they keep their place and their shape.
        Assert.Equal(new[] { Key, "c" }, geo.Targets);
        Assert.Equal(new SKSizeI(3840, 1080), geo.SizeOf(Key));

        Assert.Equal(RigGeometry.FallbackTargetSize, geo.SizeOf("ghost"));
        Assert.Equal("ghost", geo.ViewportForTile("ghost").TargetId);
        Assert.Equal(RigGeometry.FallbackTargetSize, geo.ViewportForTile("ghost").ViewportSize);
        Assert.Equal(RigGeometry.FallbackTargetSize, geo.SizeOf("x+y"));
        Assert.Equal("", geo.LetterOf("x+y"));
    }

    [Fact]
    public void NoTargetSizeIsEverZeroSoAMiniatureCannotDivideByZero()
    {
        var state = new ShowState();
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "zero", X = 0, Y = 0 });
        state.Output.Placements.Add(new ScreenPlacement
        {
            ScreenId = "floor", X = 9000, Y = 0, Planned = true, PlannedWidth = 1, PlannedHeight = 1,
        });
        var displays = new Dictionary<string, ScreenGeometry>(StringComparer.Ordinal)
        {
            ["zero"] = new ScreenGeometry(0, 0, "Broken"),
        };

        var geo = RigGeometry.Build(state, displays);
        var ids = new[] { "zero", "floor", "ghost", "z+f", "" };
        foreach (var id in ids)
        {
            var size = geo.SizeOf(id);
            Assert.True(size.Width >= 1 && size.Height >= 1, $"{id} sized {size}");
            var vp = geo.ViewportForTile(id);
            Assert.True(vp.ViewportSize.Width >= 1 && vp.ViewportSize.Height >= 1, $"{id} viewport {vp.ViewportSize}");
        }
        Assert.True(geo.SizeOf(null).Width >= 1 && geo.SizeOf(null).Height >= 1);
        Assert.Equal(new SKSizeI(160, 160), geo.SizeOf("floor")); // PlannedWidth/Height clamp at 160

        foreach (var id in ids)
        {
            var vp = RigGeometry.Empty.ViewportForTile(id);
            Assert.True(vp.ViewportSize.Width >= 1 && vp.ViewportSize.Height >= 1);
        }
    }

    [Fact]
    public void LabelsAreTheWallsWordsAndFallBackToTodaysRuleWithoutGeometry()
    {
        var state = Rig();
        var geo = RigGeometry.Build(state, Displays());

        Assert.Equal("A · Canvas A", geo.LabelFor(state, Key));
        Assert.Equal("3 · Lobby", geo.LabelFor(state, "c"));      // the display's own name

        state.Output.CanvasNames.Add(new CanvasNameConfig { MemberKey = Key, Name = "Main wall" });
        Assert.Equal("A · Main wall", geo.LabelFor(state, Key));

        state.Output.Placements.First(p => p.ScreenId == "c").CustomLabel = "Foyer";
        Assert.Equal("3 · Foyer", geo.LabelFor(state, "c"));      // the operator's name wins

        // No display table at all: the older rule, verbatim, so a headless render is unchanged.
        var plain = Rig();
        var none = RigGeometry.Build(plain, RigGeometry.NoDisplays);
        Assert.Equal("SCREEN 1", none.LabelFor(plain, "a"));
        plain.Output.Placements.First(p => p.ScreenId == "a").CustomLabel = "Foyer";
        Assert.Equal("1 · Foyer", none.LabelFor(plain, "a"));
        Assert.Equal("SCREEN", none.LabelFor(plain, "ghost"));
        Assert.Equal("Canvas", none.LabelFor(plain, Key));        // a key with no letter behind it
    }

    [Fact]
    public void ASplitCanvasKeyStillMeasuresTheBoundingBoxOfItsMembers()
    {
        var state = Rig();
        state.Output.Placements.First(p => p.ScreenId == "b").X = 4000; // dragged apart
        var geo = RigGeometry.Build(state, Displays());

        Assert.Equal(new[] { "a", "b", "c" }, geo.Targets);
        Assert.Equal("", geo.LetterOf(Key));
        Assert.Empty(geo.MembersOf(Key));
        Assert.Equal(new SKSizeI(5920, 1080), geo.SizeOf(Key)); // 0 .. 4000+1920
        Assert.Equal("a", geo.TargetOf("a"));
    }
}
