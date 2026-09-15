using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Rendering;

/// <summary>
/// One output's geometry as numbers: the lattice's density and its line, the four bends, the
/// keystone's eight corner offsets, the rotation, the two sizes (the picture's and the window's)
/// and the black pedestal's inputs. A value — equal when every number is equal — so a frame can
/// ask "is this still the geometry I built?" in one comparison with no allocation: the mesh
/// line is held by reference from the viewport and compares in one step while it is the same
/// string. What is not here (the lattice's picked point, the game's targets, a celebration) is
/// not geometry, and a viewport rebuilt for it keeps the build.
/// </summary>
public readonly record struct WarpGeometrySpec(
    int Columns, int Rows, string Mesh,
    int TopBow, int RightBow, int BottomBow, int LeftBow,
    int Tlx, int Tly, int Trx, int Try, int Blx, int Bly, int Brx, int Bry,
    OutputRotation Rotation,
    SKSizeI Effective, SKSizeI Physical,
    BlendWidths Blend, double BlackPct, double BlendGamma)
{
    public bool HasWarp => Tlx != 0 || Tly != 0 || Trx != 0 || Try != 0 || Blx != 0 || Bly != 0 || Brx != 0 || Bry != 0;

    public bool HasBend => TopBow != 0 || RightBow != 0 || BottomBow != 0 || LeftBow != 0;

    public bool HasMesh => Mesh.Length > 0;

    /// <summary>A plain output's spec: the picture straight, at one size, nothing bent, pulled or blended.</summary>
    public static WarpGeometrySpec Plain(SKSizeI size, int columns = 5, int rows = 5)
        => new(columns, rows, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, OutputRotation.None, size, size, BlendWidths.None, 0, 1.0);
}

/// <summary>
/// Everything the pipeline derives from an output's geometry, built once and read every frame:
/// the lattice's nodes, one Coons patch per cell (the twelve control points and the four texture
/// corners, in the arrays Skia takes), the lattice's lines, the single patch of the edge bends,
/// the black pedestal's cells with their levels, and the keystone and rotation matrices with
/// their composition and its inverse. Nothing here is parsed or allocated in a frame; a 17 × 17
/// mesh is 256 patches, 512 arrays, once. Pure and unit tested; the pipeline draws what this holds.
/// </summary>
public sealed class WarpGeometry
{
    public WarpGeometrySpec Spec { get; }

    /// <summary>The mesh's offsets, two per point, as the line reads.</summary>
    public float[] Offsets { get; }

    /// <summary>Every point of the lattice as it stands — rest, the mesh's pull, the bends' bows — row by row, in the picture's pixels.</summary>
    public SKPoint[] Nodes { get; }

    /// <summary>One twelve-point array per cell, row by row: the Coons patch's control points as Skia takes them.</summary>
    public SKPoint[][] PatchCubics { get; }

    /// <summary>The four texture corners of each cell, in the same order as <see cref="PatchCubics"/>.</summary>
    public SKPoint[][] PatchTextures { get; }

    /// <summary>The lattice's lines as point pairs, for the overlay.</summary>
    public (SKPoint A, SKPoint B)[] Lines { get; }

    /// <summary>The bends alone as one patch over the whole picture (the path an output without a mesh takes).</summary>
    public SKPoint[] BendCubics { get; }

    public SKPoint[] BendTexture { get; }

    /// <summary>The black pedestal's cells that add light — rect and 8-bit level — none when the pedestal is off.</summary>
    public (SKRectI Rect, byte Level)[] Pedestal { get; }

    /// <summary>The keystone's homography over the window's pixels; identity when the corners rest.</summary>
    public SKMatrix Keystone { get; }

    /// <summary>The physical rotation over the window's pixels; identity when upright.</summary>
    public SKMatrix Rotation { get; }

    /// <summary>The picture's pixels to the window's: the keystone over the rotation, as the pipeline concatenates them.</summary>
    public SKMatrix ToPhysical { get; }

    /// <summary>The window's pixels back to the picture's (a hit on the projector to the lattice); identity when the keystone folds the picture flat.</summary>
    public SKMatrix ToLocal { get; }

    public bool Invertible { get; }

    private WarpGeometry(in WarpGeometrySpec spec)
    {
        Spec = spec;
        var columns = WarpGrid.ClampSize(spec.Columns);
        var rows = WarpGrid.ClampSize(spec.Rows);
        float w = spec.Effective.Width, h = spec.Effective.Height;
        Offsets = WarpGrid.Parse(spec.Mesh, columns, rows);
        Nodes = WarpGrid.Nodes(columns, rows, w, h, Offsets, spec.TopBow, spec.RightBow, spec.BottomBow, spec.LeftBow);
        var patches = WarpGrid.Patches(Nodes, columns, rows, w, h);
        PatchCubics = new SKPoint[patches.Count][];
        PatchTextures = new SKPoint[patches.Count][];
        for (var k = 0; k < patches.Count; k++)
        {
            PatchCubics[k] = patches[k].Cubics;
            PatchTextures[k] = patches[k].Texture;
        }
        Lines = WarpGrid.Lines(Nodes, columns, rows).ToArray();
        BendCubics = WarpMesh.Cubics(w, h, spec.TopBow, spec.RightBow, spec.BottomBow, spec.LeftBow);
        BendTexture = WarpMesh.TextureCorners(w, h);
        Pedestal = PedestalOf(spec);
        Keystone = spec.HasWarp ? KeystoneOf(spec) : SKMatrix.Identity;
        Rotation = RotationOf(spec.Rotation, spec.Physical);
        ToPhysical = Keystone.PreConcat(Rotation);
        Invertible = ToPhysical.TryInvert(out var toLocal);
        ToLocal = Invertible ? toLocal : SKMatrix.Identity;
    }

    public static WarpGeometry Build(in WarpGeometrySpec spec) => new(spec);

    /// <summary>The keystone: the window's rect onto its four displaced corners.</summary>
    public static SKMatrix KeystoneOf(in WarpGeometrySpec spec)
    {
        float w = spec.Physical.Width, h = spec.Physical.Height;
        return WarpMath.QuadWarp(w, h,
            new SKPoint(spec.Tlx, spec.Tly),
            new SKPoint(w + spec.Trx, spec.Try),
            new SKPoint(spec.Blx, h + spec.Bly),
            new SKPoint(w + spec.Brx, h + spec.Bry));
    }

    /// <summary>The physical rotation as a matrix over the window: content rendered upright, turned as it is blitted.</summary>
    public static SKMatrix RotationOf(OutputRotation rotation, SKSizeI physical) => rotation switch
    {
        OutputRotation.Rot90 => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateTranslation(physical.Width, 0)),
        OutputRotation.Rot180 => SKMatrix.CreateRotationDegrees(180).PostConcat(SKMatrix.CreateTranslation(physical.Width, physical.Height)),
        OutputRotation.Rot270 => SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateTranslation(0, physical.Height)),
        _ => SKMatrix.Identity,
    };

    private static (SKRectI Rect, byte Level)[] PedestalOf(in WarpGeometrySpec spec)
    {
        if (spec.BlackPct <= 0 || !spec.Blend.Any) return Array.Empty<(SKRectI, byte)>();
        var deepest = BlackLevel.MaxCoverage(spec.Blend);
        var cells = BlackLevel.Cells(spec.Effective.Width, spec.Effective.Height, spec.Blend);
        var lit = new List<(SKRectI, byte)>(cells.Count);
        foreach (var (rect, coverage) in cells)
        {
            var level = BlackLevel.Level(spec.BlackPct, coverage, deepest, spec.BlendGamma);
            if (level > 0) lit.Add((rect, level));
        }
        return lit.ToArray();
    }

    /// <summary>A point of the picture on the window: where a lattice node lands on the projector.</summary>
    public SKPoint MapToPhysical(SKPoint local) => ToPhysical.MapPoint(local);

    /// <summary>A point of the window in the picture: where a touch on the projector lands on the lattice.</summary>
    public SKPoint MapToLocal(SKPoint physical) => ToLocal.MapPoint(physical);
}

/// <summary>
/// The geometry a sink is drawing with, kept until its numbers change. A frame hands in the
/// spec it would draw with; the cache answers with the geometry it holds when the spec is the
/// same, and builds once when it is not — the operator's pull, a resize, a new density. One per
/// pipeline; used under the pipeline's own gate.
/// </summary>
public sealed class WarpGeometryCache
{
    private WarpGeometry? _current;

    /// <summary>How many builds so far: the change count, never the frame count.</summary>
    public int Builds { get; private set; }

    public WarpGeometry? Current => _current;

    public WarpGeometry For(in WarpGeometrySpec spec)
    {
        var current = _current;
        if (current is not null && current.Spec.Equals(spec)) return current;
        current = WarpGeometry.Build(spec);
        _current = current;
        Builds++;
        return current;
    }

    public void Clear() => _current = null;
}
