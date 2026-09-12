using Patterns.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Camera calibration, pure: the Gray codes and their sequence, the decode against a room that is
/// not there, the homography fit, and the solver — each projector's place, its mesh putting the
/// right canvas point at the right pixel, its blend mask adding to one across the overlap.
/// </summary>
public class CalibrationTests
{
    [Fact]
    public void TheGrayCodesChangeOneBitBetweenNeighboursAndTheSequenceCarriesEveryBitBothWays()
    {
        for (var n = 0; n < 500; n++)
        {
            Assert.Equal(n, GrayCode.Decode(GrayCode.Encode(n)));
            if (n > 0) Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)(GrayCode.Encode(n) ^ GrayCode.Encode(n - 1))));
        }
        Assert.Equal(11, GrayCode.Bits(1920));
        Assert.Equal(11, GrayCode.Bits(1080));
        Assert.Equal(1, GrayCode.Bits(1));
        var sequence = GrayCode.Sequence(1920, 1080);
        Assert.Equal(2 + 22 + 22, sequence.Count);
        Assert.Equal(CalStep.White, sequence[0].Step);
        Assert.Equal(CalStep.Black, sequence[1].Step);
        Assert.Equal(new CalPattern(CalStep.Column, 10, false), sequence[2]);
        Assert.Equal(new CalPattern(CalStep.Column, 10, true), sequence[3]);
        Assert.Equal("columns bit 10 inverse", sequence[3].Name);
        Assert.Equal(new CalPattern(CalStep.Row, 0, true), sequence[^1]);
        var top = new CalPattern(CalStep.Column, 10, false);
        Assert.False(GrayCode.IsLit(top, 0, 0));
        Assert.True(GrayCode.IsLit(top, 1919, 0));                                  // the top bit lights the right half
        Assert.True(GrayCode.IsLit(new CalPattern(CalStep.Column, 10, true), 0, 0));
        var runs = GrayCode.Runs(new CalPattern(CalStep.Column, 0, false), 8);
        Assert.Equal(new[] { (1, 2), (5, 2) }, runs);                                // bit 0 of the Gray code: 0110 0110
        Assert.Equal(new[] { (0, 8) }, GrayCode.Runs(new CalPattern(CalStep.White, 0, false), 8));
        Assert.Empty(GrayCode.Runs(new CalPattern(CalStep.Black, 0, false), 8));
    }

    private static CalibrationSimulator TwoProjectors()
    {
        // A 640×360 camera on two 1280×720 projectors side by side, the right one keystoned a little, overlapping by about a tenth.
        var room = new CalibrationSimulator(640, 360);
        room.AddProjector("pj1", "PJ 1", 1280, 720, new SKPoint(40, 60), new SKPoint(340, 62), new SKPoint(338, 300), new SKPoint(42, 298));
        room.AddProjector("pj2", "PJ 2", 1280, 720, new SKPoint(300, 70), new SKPoint(600, 58), new SKPoint(604, 304), new SKPoint(302, 292));
        return room;
    }

    [Fact]
    public void TheDecodeReadsBackWhichProjectorPixelLitEachCameraPixel()
    {
        var room = TwoProjectors();
        var truth = room.Analytic(0);
        var sample = room.Samples()[0];
        Assert.Equal(truth.ValidCount, sample.Map.ValidCount);
        var off = 0;
        var checkedPixels = 0;
        for (var k = 0; k < truth.Valid.Length; k++)
        {
            if (!truth.Valid[k]) continue;
            checkedPixels++;
            if (Math.Abs(truth.Px[k] - sample.Map.Px[k]) > 1 || Math.Abs(truth.Py[k] - sample.Map.Py[k]) > 1) off++;
        }
        Assert.True(checkedPixels > 50000, $"{checkedPixels} camera pixels lit");
        Assert.True(off == 0, $"{off} camera pixels decoded more than a pixel off");
        Assert.InRange(sample.Map.CoverageFraction, 0.25, 0.4);
        var bounds = sample.Map.Bounds;
        Assert.InRange(bounds.Left, 38, 43);
        Assert.InRange(bounds.Right, 338, 342);
        Assert.NotNull(sample.Map.Lookup(new SKPoint(200, 180)));
        Assert.Null(sample.Map.Lookup(new SKPoint(500, 180)));
    }

    [Fact]
    public void TheHomographyFitRecoversAKnownMappingThroughNoise()
    {
        var truth = Homography.UnitSquareTo(new SKPoint(40, 60), new SKPoint(340, 62), new SKPoint(338, 300), new SKPoint(42, 298));
        var rnd = new Random(7);
        var pairs = new List<(SKPoint, SKPoint)>();
        for (var i = 0; i < 400; i++)
        {
            var u = new SKPoint((float)rnd.NextDouble(), (float)rnd.NextDouble());
            var c = truth.Map(u);
            pairs.Add((u, new SKPoint(c.X + (float)(rnd.NextDouble() - 0.5), c.Y + (float)(rnd.NextDouble() - 0.5))));
        }
        var fit = Homography.Fit(pairs);
        foreach (var u in new[] { new SKPoint(0, 0), new SKPoint(1, 0), new SKPoint(0.5f, 0.5f), new SKPoint(0.2f, 0.9f) })
        {
            var a = truth.Map(u);
            var b = fit.Map(u);
            Assert.InRange(Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y), 0, 0.5);
        }
        var back = fit.Inverse().Map(fit.Map(new SKPoint(0.3f, 0.7f)));
        Assert.InRange(Math.Abs(back.X - 0.3f) + Math.Abs(back.Y - 0.7f), 0, 1e-3);
        Assert.Equal(Homography.Identity.M, Homography.Fit(new List<(SKPoint, SKPoint)>()).M);
    }

    [Fact]
    public void TheSolverPlacesEachProjectorMeshesItOntoTheCanvasAndBlendsTheOverlapToOne()
    {
        var room = TwoProjectors();
        var samples = room.Samples();
        var canvas = Calibrator.AutoCanvas(samples);
        Assert.InRange(canvas.TL.X, 38, 44);
        Assert.InRange(canvas.BR.X, 600, 606);
        var solution = Calibrator.Solve(samples, canvas, meshColumns: 9, meshRows: 9, maskWidth: 128, maskHeight: 72);
        Assert.Equal(2, solution.Projectors.Count);
        // The canvas: the larger projector one to one across it — about 1280 / (300/560) ≈ 2390 wide.
        Assert.InRange(solution.CanvasWidth, 2200, 2800);
        Assert.InRange(solution.CanvasHeight, 700, 1400);
        var left = solution.Projectors[0];
        var right = solution.Projectors[1];
        Assert.InRange(left.X, -20, 20);
        Assert.True(right.X > solution.CanvasWidth * 0.4 && right.X < solution.CanvasWidth * 0.55, $"PJ 2 at {right.X} of {solution.CanvasWidth}");
        Assert.True(left.FitResidualPx < 1.5, $"residual {left.FitResidualPx}");
        Assert.True(left.LocalNodes > 40, $"{left.LocalNodes} local nodes");   // the lattice inside the light reads from the camera; the edge from the fit
        Assert.Contains("(excellent)", left.Words);

        // The mesh: every lattice point's content — the canvas pixel at its rest in the projector's box — lands where that canvas point is on the wall.
        var toCamera = canvas.FromUnit;
        for (var index = 0; index < 2; index++)
        {
            var sol = solution.Projectors[index];
            var offsets = WarpGrid.Parse(sol.Mesh, sol.MeshColumns, sol.MeshRows);
            var worst = 0.0;
            for (var j = 0; j < sol.MeshRows; j++)
            {
                for (var i = 0; i < sol.MeshColumns; i++)
                {
                    var rest = WarpGrid.Rest(i, j, sol.MeshColumns, sol.MeshRows, 1280, 720);
                    var unit = new SKPoint((sol.X + rest.X) / solution.CanvasWidth, (sol.Y + rest.Y) / solution.CanvasHeight);
                    var expected = room.ToCamera(index).Inverse().Map(toCamera.Map(unit));
                    var k = (j * sol.MeshColumns + i) * 2;
                    var got = new SKPoint(rest.X + offsets[k], rest.Y + offsets[k + 1]);
                    worst = Math.Max(worst, Math.Abs(got.X - expected.X) + Math.Abs(got.Y - expected.Y));
                }
            }
            Assert.True(worst < 6, $"projector {index}: a lattice point is {worst:0.0} px from where the canvas point lands");
        }

        // The blend: in the overlap the two weights add to one; deep inside one projector it alone is one; at its edge it fades to nothing.
        double At(GreyFrame mask, int index, float rx, float ry)
        {
            var mx = (int)(rx / 1280 * mask.Width);
            var my = (int)(ry / 720 * mask.Height);
            return mask.At(Math.Clamp(mx, 0, mask.Width - 1), Math.Clamp(my, 0, mask.Height - 1)) / 255.0;
        }
        Assert.InRange(At(left.BlendMask, 0, 300, 360), 0.97, 1.0);
        Assert.InRange(At(right.BlendMask, 1, 1000, 360), 0.97, 1.0);
        Assert.InRange(At(left.BlendMask, 0, 8, 360), 0.97, 1.0);                     // alone at the canvas's edge: full light to the edge
        Assert.InRange(At(left.BlendMask, 0, 1272, 360), 0, 0.2);                     // its right edge lies in PJ 2's light: faded out
        Assert.InRange(At(right.BlendMask, 1, 8, 360), 0, 0.2);                       // and PJ 2's left edge in PJ 1's
        // A camera point in the overlap: the left projector's raster point and the right's for it, and their weights.
        var overlapCamera = new SKPoint(320, 180);
        var pl = room.ToCamera(0).Inverse().Map(overlapCamera);
        var pr = room.ToCamera(1).Inverse().Map(overlapCamera);
        var sum = At(left.BlendMask, 0, pl.X - left.X + left.X, pl.Y) + At(right.BlendMask, 1, pr.X, pr.Y);
        Assert.InRange(sum, 0.85, 1.15);
        Assert.Contains("a canvas of", solution.Report);
        Assert.Contains("PJ 1 and PJ 2 overlap over", solution.Report);
        Assert.DoesNotContain("reach no projector", solution.Report);

        // A canvas bigger than the light: the corners nobody reaches are named.
        var wider = Calibrator.Solve(samples, CanvasQuad.Rect(0, 0, 640, 360), meshColumns: 3, meshRows: 3, maskWidth: 16, maskHeight: 9);
        Assert.Contains("reach no projector", wider.Report);
        // A projector the camera did not see is said, not thrown.
        var blind = new ProjectorSample("pj3", "PJ 3", 1280, 720, new Correspondence(640, 360, 1280, 720));
        var partial = Calibrator.Solve(new[] { samples[0], blind }, canvas, 3, 3, 16, 9);
        Assert.Contains("the camera saw too little of it", partial.Projectors[1].Words);
        Assert.Equal("", partial.Projectors[1].Mesh);
    }
}
