using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 25: the monitor wall stops being one screen's pattern and becomes the show's.
///
/// The old shape had three faults an operator meets on a show day: a wall could only be
/// configured by making it the picture of whatever was being edited, so the programme could not be
/// a pattern and a wall at the same time; two outputs showing "the same" wall were two
/// configurations that drifted; and an even grid told the room that a confidence feed matters as
/// much as what is on air. These pin the shape that replaces it.
/// </summary>
public class MultiviewWallTests
{
    private static readonly SKRect Area = SKRect.Create(0, 0, 1000, 500);

    private static void AssertInside(IEnumerable<MultiviewCell> cells, SKRect area)
    {
        foreach (var c in cells)
        {
            Assert.True(c.Box.Left >= area.Left - 0.01f && c.Box.Top >= area.Top - 0.01f
                && c.Box.Right <= area.Right + 0.01f && c.Box.Bottom <= area.Bottom + 0.01f,
                $"tile {c.Index} at {c.Box} left the wall {area}");
            Assert.True(c.Box.Width > 0 && c.Box.Height > 0, $"tile {c.Index} has no area");
        }
    }

    private static void AssertNoOverlap(List<MultiviewCell> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            for (var j = i + 1; j < cells.Count; j++)
            {
                var a = cells[i].Box;
                var b = cells[j].Box;
                var over = a.Left < b.Right - 0.01f && b.Left < a.Right - 0.01f
                    && a.Top < b.Bottom - 0.01f && b.Top < a.Bottom - 0.01f;
                Assert.False(over, $"tiles {cells[i].Index} and {cells[j].Index} overlap: {a} / {b}");
            }
        }
    }

    [Fact]
    public void EveryLayoutFillsTheWallWithoutTilesOverlappingOrLeavingIt()
    {
        foreach (var layout in Enum.GetValues<MultiviewLayout>())
        {
            for (var count = 1; count <= 9; count++)
            {
                var cells = MultiviewLayoutPlan.Plan(layout, count, Area, 0, 6);
                Assert.Equal(count, cells.Count);
                Assert.Equal(Enumerable.Range(0, count), cells.Select(c => c.Index));
                AssertInside(cells, Area);
                AssertNoOverlap(cells);
            }
        }

        // Nothing to draw, or nowhere to draw it: no boxes, and no exception either.
        Assert.Empty(MultiviewLayoutPlan.Plan(MultiviewLayout.Grid, 0, Area, 0, 6));
        Assert.Empty(MultiviewLayoutPlan.Plan(MultiviewLayout.Grid, 4, SKRect.Create(0, 0, 0, 0), 0, 6));
    }

    [Fact]
    public void TheOnesThatDecideAnythingAreTheBigOnes()
    {
        // The default: what is on air and what is next, large, with the rest under them.
        var cells = MultiviewLayoutPlan.Plan(MultiviewLayout.ProgramAndPreview, 6, Area, 0, 6);
        Assert.Equal(2, cells.Count(c => c.Large));
        Assert.True(cells[0].Large && cells[1].Large);
        Assert.Equal(cells[0].Box.Top, cells[1].Box.Top, 2);                 // side by side
        Assert.Equal(cells[0].Box.Height, cells[1].Box.Height, 2);
        Assert.True(cells[0].Box.Right <= cells[1].Box.Left + 0.01f);        // in list order, left to right
        foreach (var small in cells.Skip(2))
        {
            Assert.False(small.Large);
            Assert.True(small.Box.Top >= cells[0].Box.Bottom - 0.01f, "the strip runs under the large row");
            Assert.True(small.Box.Height < cells[0].Box.Height, "a small tile is smaller than a large one");
        }
        // The strip is one row while it fits.
        Assert.Single(cells.Skip(2).Select(c => (int)Math.Round(c.Box.Top)).Distinct());

        // One large, the rest under it.
        var solo = MultiviewLayoutPlan.Plan(MultiviewLayout.Solo, 5, Area, 0, 6);
        Assert.Single(solo, c => c.Large);
        Assert.True(solo[0].Large);
        Assert.Equal(Area.Width, solo[0].Box.Width, 2);

        // One large down the left, the rest in a column on the right.
        var side = MultiviewLayoutPlan.Plan(MultiviewLayout.SideColumn, 4, Area, 0, 6);
        Assert.True(side[0].Large);
        Assert.Equal(Area.Height, side[0].Box.Height, 2);
        foreach (var small in side.Skip(1)) Assert.True(small.Box.Left >= side[0].Box.Right - 0.01f);

        // The grid says nothing matters more than anything else, so nothing is large.
        var grid = MultiviewLayoutPlan.Plan(MultiviewLayout.Grid, 6, Area, 3, 6);
        Assert.DoesNotContain(grid, c => c.Large);
        Assert.Equal(3, grid.Select(c => (int)Math.Round(c.Box.Left)).Distinct().Count());
        Assert.Equal(2, grid.Select(c => (int)Math.Round(c.Box.Top)).Distinct().Count());

        // Fewer tiles than the layout wants large: they are all large, in one row.
        var two = MultiviewLayoutPlan.Plan(MultiviewLayout.ProgramAndPreview, 2, Area, 0, 6);
        Assert.All(two, c => Assert.True(c.Large));
        Assert.Equal(Area.Height, two[0].Box.Height, 2);

        Assert.Equal(2, MultiviewLayoutPlan.LargeCount(MultiviewLayout.ProgramAndPreview, 9));
        Assert.Equal(1, MultiviewLayoutPlan.LargeCount(MultiviewLayout.ProgramAndPreview, 1));
        Assert.Equal(0, MultiviewLayoutPlan.LargeCount(MultiviewLayout.Grid, 9));
    }

    [Fact]
    public void TheStripWrapsRatherThanShrinkingToNothing()
    {
        // A wall of a dozen inputs: the strip takes what it was asked for, else one row up to the
        // cap, so a tile never becomes a sliver of pixels nobody can read.
        Assert.Equal(4, MultiviewLayoutPlan.StripColumns(12, 4));
        Assert.Equal(MultiviewLayoutPlan.MaxStripColumns, MultiviewLayoutPlan.StripColumns(12, 0));
        Assert.Equal(3, MultiviewLayoutPlan.StripColumns(3, 0));
        Assert.Equal(1, MultiviewLayoutPlan.StripColumns(0, 0));

        var cells = MultiviewLayoutPlan.Plan(MultiviewLayout.ProgramAndPreview, 14, Area, 0, 6);
        Assert.Equal(14, cells.Count);
        AssertInside(cells, Area);
        AssertNoOverlap(cells);
        Assert.Equal(2, cells.Skip(2).Select(c => (int)Math.Round(c.Box.Top)).Distinct().Count()); // it wrapped
    }

    // ---- the wall as a thing of the show's -----------------------------------------------------

    private static ShowState Rig()
    {
        var state = new ShowState();
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080 });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, X = 1920 });
        return state;
    }

    [Fact]
    public void AWallBelongsToTheShowAndAPatternOnlyNamesIt()
    {
        var state = Rig();
        Assert.Empty(state.Multiviews);

        var wall = Multiviews.Add(state);
        Assert.Single(state.Multiviews);
        Assert.Equal(32, wall.Id.Length);
        Assert.Equal("Multiview 1", Multiviews.NameOf(state, wall));
        // It arrives filled from the rig, with the two that matter at the top of the list.
        Assert.Equal(MultiviewSource.Program, wall.Tiles[0].Source);
        Assert.Equal(MultiviewSource.Preview, wall.Tiles[1].Source);
        Assert.Contains(wall.Tiles, t => t.Source == MultiviewSource.Screen && t.ScreenId == "a");
        Assert.Contains(wall.Tiles, t => t.Source == MultiviewSource.Screen && t.ScreenId == "b");
        Assert.Equal(MultiviewSource.Clock, wall.Tiles[^1].Source);
        Assert.Equal(MultiviewLayout.ProgramAndPreview, wall.Layout);

        var second = Multiviews.Add(state);
        Assert.Equal(2, state.Multiviews.Count);
        Assert.Equal("Multiview 2", Multiviews.NameOf(state, second));
        Assert.NotEqual(wall.Id, second.Id);
        // Two is what a rack has room for; a third press gives back the one that is there.
        Assert.Same(second, Multiviews.Add(state));
        Assert.Equal(Multiviews.Max, state.Multiviews.Count);

        // A pattern draws the wall it names; one that names nothing draws the tiles it carries,
        // which is exactly what a show made before the walls existed does.
        var cfg = new PatternConfig { Kind = PatternKind.Multiview, MultiviewId = wall.Id };
        Assert.Same(wall, Multiviews.For(state, cfg));
        cfg.MultiviewId = "";
        Assert.Same(cfg.Multiview, Multiviews.For(state, cfg));
        cfg.MultiviewId = "a-wall-this-show-no-longer-has";
        Assert.Same(cfg.Multiview, Multiviews.For(state, cfg));

        Assert.Same(wall, Multiviews.Primary(state));
        Assert.Same(second, Multiviews.Primary(state, 2));
        Assert.Same(second, Multiviews.Primary(state, 9));  // clamped to the last wall, never an exception
        Assert.Same(wall, Multiviews.Primary(state, 0));
    }

    [Fact]
    public void TickingAnOutputSetsUpTheWholeChainAndUntickingHandsItBack()
    {
        var state = Rig();
        state.Ndi.Senders.Add(new NdiSenderConfig { Name = "Gallery" });
        var sender = state.Ndi.Senders[0];
        var wall = Multiviews.Add(state);

        // A screen on the rig: its own pattern on, set to this wall.
        Multiviews.Show(state, "b", wall);
        Assert.True(Multiviews.IsShowing(state, "b", wall.Id));
        Assert.True(ContentTargets.UsesOwnPattern(state, "b"));
        var assignment = state.Independent.First(a => a.ScreenId == "b");
        Assert.Equal(PatternKind.Multiview, assignment.Pattern.Kind);
        Assert.Equal(wall.Id, assignment.Pattern.MultiviewId);
        Assert.False(Multiviews.IsShowing(state, "a", wall.Id));

        // An NDI sender: pointed at its own picture for the operator, so there is nothing left to
        // find on the NDI page.
        Multiviews.Show(state, sender.OwnScreenId, wall);
        Assert.True(sender.UsesOwnScreen);
        Assert.True(Multiviews.IsShowing(state, sender.OwnScreenId, wall.Id));
        Assert.Contains(state.Output.Placements, p => p.ScreenId == sender.OwnScreenId);

        // The stream, the same way round.
        Multiviews.Show(state, StreamConfig.OwnScreenId, wall);
        Assert.True(state.Stream.UsesOwnScreen);
        Assert.True(Multiviews.IsShowing(state, StreamConfig.OwnScreenId, wall.Id));

        Assert.Equal(new[] { "b", sender.OwnScreenId, StreamConfig.OwnScreenId }.OrderBy(x => x),
            Multiviews.TargetsShowing(state, wall.Id).OrderBy(x => x));

        // Unticked, each goes straight back to the programme.
        Multiviews.Clear(state, "b");
        Assert.False(Multiviews.IsShowing(state, "b", wall.Id));
        Assert.False(ContentTargets.UsesOwnPattern(state, "b"));
        Multiviews.Clear(state, sender.OwnScreenId);
        Assert.False(sender.UsesOwnScreen);
        Multiviews.Clear(state, StreamConfig.OwnScreenId);
        Assert.False(state.Stream.UsesOwnScreen);
        Assert.Empty(Multiviews.TargetsShowing(state, wall.Id));
    }

    [Fact]
    public void RemovingAWallNeverLeavesAScreenShowingSomethingNobodyCanConfigure()
    {
        var state = Rig();
        var wall = Multiviews.Add(state);
        Multiviews.Show(state, "a", wall);
        Multiviews.Show(state, "b", wall);

        Multiviews.Remove(state, wall);
        Assert.Empty(state.Multiviews);
        Assert.False(ContentTargets.UsesOwnPattern(state, "a"));
        Assert.False(ContentTargets.UsesOwnPattern(state, "b"));
    }

    [Fact]
    public void AnOlderShowsTilesBecomeAWallItHolds()
    {
        var state = Rig();
        // Two targets carrying identical tiles: what an operator had to keep level by hand.
        void Fill(PatternConfig cfg)
        {
            cfg.Kind = PatternKind.Multiview;
            cfg.Multiview.Columns = 3;
            cfg.Multiview.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Program });
            cfg.Multiview.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = "a" });
        }
        Fill(state.Pattern);
        var own = ContentTargets.EnsureAssignment(state, "b");
        own.Pattern.Multiview.Tiles.Clear();
        Fill(own.Pattern);

        // A third that is different, and a fourth drawing the automatic wall.
        var other = ContentTargets.EnsureAssignment(state, "a");
        other.Pattern.Multiview.Tiles.Clear();
        other.Pattern.Kind = PatternKind.Multiview;
        other.Pattern.Multiview.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Clock });

        Multiviews.Migrate(state);

        // The two that were the same are now one wall, and both point at it.
        Assert.Equal(2, state.Multiviews.Count);
        Assert.Equal(state.Pattern.MultiviewId, own.Pattern.MultiviewId);
        Assert.NotEqual(state.Pattern.MultiviewId, other.Pattern.MultiviewId);
        var wall = Multiviews.Find(state, state.Pattern.MultiviewId)!;
        Assert.Equal(2, wall.Tiles.Count);
        Assert.Equal(3, wall.Columns);
        // A show that already exists opens looking the way it was left, not rearranged.
        Assert.Equal(MultiviewLayout.Grid, wall.Layout);

        // Idempotent: loading the same file again changes nothing.
        var before = state.Multiviews.Count;
        Multiviews.Migrate(state);
        Assert.Equal(before, state.Multiviews.Count);

        // A pattern with no tiles of its own was drawing the automatic wall and still is.
        var plain = ContentTargets.EnsureAssignment(state, "c");
        plain.Pattern.Multiview.Tiles.Clear();
        plain.Pattern.MultiviewId = "";   // a copy of the programme brings its wall with it
        plain.Pattern.Kind = PatternKind.Multiview;
        Multiviews.Migrate(state);
        Assert.Equal("", plain.Pattern.MultiviewId);
    }

    [Fact]
    public void AWallIsNotPartOfWhatAPictureLooksLike()
    {
        // Two shows built the same way have to compare the same, or every sink crossfades on
        // nothing. The wall's id and name are the show's bookkeeping, not the picture.
        var a = new ShowState();
        var b = new ShowState();
        Assert.Equal(JsonUtil.SerializeIdentity(a.Pattern), JsonUtil.SerializeIdentity(b.Pattern));

        var wall = Multiviews.Add(a);
        wall.Name = "Stage left";
        Assert.Equal(JsonUtil.SerializeIdentity(a.Pattern), JsonUtil.SerializeIdentity(b.Pattern));

        // Which wall a pattern draws, though, is exactly what it looks like.
        a.Pattern.Kind = PatternKind.Multiview;
        b.Pattern.Kind = PatternKind.Multiview;
        a.Pattern.MultiviewId = wall.Id;
        Assert.NotEqual(JsonUtil.SerializeIdentity(a.Pattern), JsonUtil.SerializeIdentity(b.Pattern));
    }

    [Fact]
    public void TheWallTheEngineDrawsIsTheOneTheLayoutPlanned()
    {
        // The proof that the renderer is following the plan rather than a grid of its own: three
        // tiles of three colours, and the first two really are the pair across the top.
        var state = new ShowState();
        state.Transition.Enabled = false;
        state.Pattern.Kind = PatternKind.FlatField;
        state.Pattern.FlatField.Color = "#FF0000";
        state.Pattern.FlatField.ShowLabel = false;
        state.Pattern.Canvas.FollowOutput = true;
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", Enabled = true, UseCustomPattern = true });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", X = 1920, Enabled = true, UseCustomPattern = true });
        void Own(string id, string colour)
        {
            var cfg = ContentTargets.EnsureAssignment(state, id).Pattern;
            cfg.Kind = PatternKind.FlatField;
            cfg.FlatField.Color = colour;
            cfg.FlatField.ShowLabel = false;
            cfg.Canvas.FollowOutput = true;
        }
        Own("a", "#00FF00");
        Own("b", "#0000FF");

        var wall = new MultiviewOptions { Layout = MultiviewLayout.ProgramAndPreview, ShowLabels = false, ShowTally = false };
        wall.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Program });
        wall.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = "a" });
        wall.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = "b" });

        using var bmp = MultiviewRenderTests.RenderWall(state, wall, 640, 360);
        // Top-left large tile: the programme. Top-right: screen a. Both in the upper two thirds.
        Assert.True(bmp.GetPixel(160, 110).Red > 200, "the programme is the top-left large tile");
        Assert.True(bmp.GetPixel(480, 110).Green > 200, "the first screen is the top-right large tile");
        // The strip underneath carries the third.
        Assert.True(bmp.GetPixel(320, 300).Blue > 200, "the rest of the wall runs along the bottom");

        // The same three tiles as an even grid: three across, nothing large, nothing underneath.
        wall.Layout = MultiviewLayout.Grid;
        wall.Columns = 3;
        using var grid = MultiviewRenderTests.RenderWall(state, wall, 640, 360);
        Assert.True(grid.GetPixel(106, 180).Red > 200);
        Assert.True(grid.GetPixel(320, 180).Green > 200);
        Assert.True(grid.GetPixel(533, 180).Blue > 200);
    }

    [Fact]
    public void AScreenAdoptedAtTheVenueKeepsItsPlaceOnEveryWall()
    {
        var state = Rig();
        var wall = Multiviews.Add(state);
        Assert.Contains(wall.Tiles, t => t.ScreenId == "a");

        ContentTargets.RenameScreen(state, "a", "DISPLAY4");
        Assert.Contains(wall.Tiles, t => t.ScreenId == "DISPLAY4");
        Assert.DoesNotContain(wall.Tiles, t => t.ScreenId == "a");
    }
}
