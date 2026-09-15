using Patterns.Core.Model;
using Patterns.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The mesh warp's lattice, pure: the line and its offsets, the points, the patches, the moves, the resample.</summary>
public class WarpGridTests
{
    [Fact]
    public void TheLineReadsAndWritesItsOffsetsAndAnEmptyLineIsALatticeAtRest()
    {
        Assert.All(WarpGrid.Parse("", 3, 3), v => Assert.Equal(0, v));
        Assert.Equal(18, WarpGrid.Parse(null, 3, 3).Length);
        var offsets = WarpGrid.Parse("0,0;10,-4;0,0;0,0;3.5,2", 3, 3);
        Assert.Equal(10, offsets[2]);
        Assert.Equal(-4, offsets[3]);
        Assert.Equal(3.5f, offsets[8]);
        Assert.Equal("", WarpGrid.Format(new float[18]));
        Assert.Equal("0,0;10,-4;0,0;0,0;3.5,2;0,0;0,0;0,0;0,0", WarpGrid.Format(offsets));
        Assert.Equal(offsets, WarpGrid.Parse(WarpGrid.Format(offsets), 3, 3));
        Assert.Equal(2, WarpGrid.ClampSize(1));
        Assert.Equal(17, WarpGrid.ClampSize(40));
        var p = new ScreenPlacement();
        Assert.False(p.HasMesh);
        Assert.False(WarpGrid.HasMesh(p));
        p.WarpMesh = "0,0;0,0;0,0;0,0";
        Assert.True(p.HasMesh);
        Assert.False(WarpGrid.HasMesh(p));                                  // a line of zeros pulls nothing
        p.WarpMeshColumns = 1;
        Assert.Equal(2, p.WarpMeshColumns);
    }

    [Fact]
    public void ThePointsRestOnTheLatticeTakeTheirOffsetsAndTheBendsBowTheEdges()
    {
        Assert.Equal(new SKPoint(0, 0), WarpGrid.Rest(0, 0, 3, 3, 1920, 1080));
        Assert.Equal(new SKPoint(960, 540), WarpGrid.Rest(1, 1, 3, 3, 1920, 1080));
        Assert.Equal(new SKPoint(1920, 1080), WarpGrid.Rest(2, 2, 3, 3, 1920, 1080));
        var offsets = WarpGrid.Parse("0,0;0,0;0,0;0,0;12,-8;0,0;0,0;0,0;0,0", 3, 3);
        var nodes = WarpGrid.Nodes(3, 3, 1920, 1080, offsets);
        Assert.Equal(9, nodes.Length);
        Assert.Equal(new SKPoint(972, 532), nodes[4]);
        Assert.Equal(new SKPoint(0, 0), nodes[0]);
        // The bends: the top edge's middle point takes the whole bow, the corners none, a quarter point three quarters.
        var bent = WarpGrid.Nodes(5, 3, 1920, 1080, new float[30], top: 40, right: 0, bottom: 20, left: -10);
        Assert.Equal(new SKPoint(960, -40), bent[2]);
        Assert.Equal(new SKPoint(0, 0), bent[0]);
        Assert.Equal(-30, bent[1].Y, 0.01);
        Assert.Equal(1080 + 20, bent[12].Y, 0.01);                          // the bottom edge's middle, out
        Assert.Equal(10, bent[5].X, 0.01);                                  // the left edge's middle, pulled in by a negative bow
        var p = new ScreenPlacement { WarpMeshColumns = 3, WarpMeshRows = 3, WarpMesh = "0,0;0,0;0,0;0,0;12,-8;0,0;0,0;0,0;0,0", WarpTopBow = 10 };
        Assert.Equal(new SKPoint(972, 532), WarpGrid.NodesOf(p, 1920, 1080)[4]);
        Assert.Equal(new SKPoint(960, -10), WarpGrid.NodesOf(p, 1920, 1080)[1]);
    }

    [Fact]
    public void ThePatchesTileTheCellsAndALatticeAtRestDrawsStraight()
    {
        var nodes = WarpGrid.Nodes(3, 3, 1920, 1080, new float[18]);
        var patches = WarpGrid.Patches(nodes, 3, 3, 1920, 1080);
        Assert.Equal(4, patches.Count);
        var (cubics, texture) = patches[0];
        Assert.Equal(12, cubics.Length);
        Assert.Equal(new SKPoint(0, 0), cubics[0]);
        Assert.Equal(new SKPoint(960, 0), cubics[3]);
        Assert.Equal(new SKPoint(960, 540), cubics[6]);
        Assert.Equal(new SKPoint(0, 540), cubics[9]);
        Assert.Equal(new SKPoint(320, 0), cubics[1]);                       // a third of the way along a straight edge
        Assert.Equal(new SKPoint(640, 0), cubics[2]);
        Assert.Equal(new[] { new SKPoint(0, 0), new SKPoint(960, 0), new SKPoint(960, 540), new SKPoint(0, 540) }, texture);
        var last = patches[3];
        Assert.Equal(new SKPoint(960, 540), last.Cubics[0]);
        Assert.Equal(new SKPoint(1920, 1080), last.Cubics[6]);
        Assert.Equal(new SKPoint(1920, 1080), last.Texture[2]);
        // A point pulled: the cells around it curve towards it, and the handles follow the neighbours' tangents.
        var pulled = WarpGrid.Nodes(3, 3, 1920, 1080, WarpGrid.Parse("0,0;0,0;0,0;0,0;100,0;0,0;0,0;0,0;0,0", 3, 3));
        var cell = WarpGrid.Patches(pulled, 3, 3, 1920, 1080)[0];
        Assert.Equal(new SKPoint(1060, 540), cell.Cubics[6]);
        Assert.True(cell.Cubics[7].X < 1060 && cell.Cubics[7].X > 700);
        Assert.Equal(12, WarpGrid.Lines(nodes, 3, 3).Count);
    }

    [Fact]
    public void APointMovesNudgesAndTheLatticeResamplesKeepingItsShape()
    {
        var moved = WarpGrid.Moved("", 3, 3, 4, 20, -6);
        Assert.Equal("0,0;0,0;0,0;0,0;20,-6;0,0;0,0;0,0;0,0", moved);
        Assert.Equal("0,0;0,0;0,0;0,0;21,-16;0,0;0,0;0,0;0,0", WarpGrid.Nudged(moved, 3, 3, 4, 1, -10));
        Assert.Equal(moved, WarpGrid.Moved(moved, 3, 3, 40, 1, 1));           // no such point: nothing changes
        Assert.Equal("0,0;0,0;0,0;0,0;4096,-6;0,0;0,0;0,0;0,0", WarpGrid.Moved(moved, 3, 3, 4, 9000, -6));
        var nodes = WarpGrid.Nodes(3, 3, 1920, 1080, WarpGrid.Parse(moved, 3, 3));
        Assert.Equal(4, WarpGrid.Nearest(nodes, new SKPoint(975, 536), 30));
        Assert.Equal(-1, WarpGrid.Nearest(nodes, new SKPoint(500, 300), 30));
        // Coarse to fine and back: the pulled centre keeps its pull; the fine lattice's new points take a share.
        var fine = WarpGrid.Resampled(moved, 3, 3, 5, 5);
        var fineOffsets = WarpGrid.Parse(fine, 5, 5);
        Assert.Equal(20, fineOffsets[(2 * 5 + 2) * 2]);
        Assert.Equal(10, fineOffsets[(2 * 5 + 1) * 2]);                     // halfway between the centre and a rest point
        Assert.Equal(0, fineOffsets[0]);
        Assert.Equal(moved, WarpGrid.Resampled(fine, 5, 5, 3, 3));
        Assert.Equal("", WarpGrid.Resampled("", 3, 3, 9, 9));
        Assert.StartsWith("point 2,2 of 3×3 — pulled 20 px right, 6 px up", WarpGrid.Describe(4, 3, 3, WarpGrid.Parse(moved, 3, 3)));
        Assert.StartsWith("point 1,1 of 3×3 — at rest", WarpGrid.Describe(0, 3, 3, WarpGrid.Parse(moved, 3, 3)));
        Assert.StartsWith("3×3 lattice — click a point", WarpGrid.Describe(-1, 3, 3, WarpGrid.Parse(moved, 3, 3)));
    }
}
