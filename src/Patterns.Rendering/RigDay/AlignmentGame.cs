using Patterns.Core.RigDay;
using Patterns.Rendering;
using SkiaSharp;

namespace Patterns.Rendering.RigDay;

/// <summary>
/// The alignment game: after a camera calibration, the projector shows the solver's target ring at
/// each lattice node; the operator drives the picked node with the arrows or a pad; the residual in
/// pixels is the score, a node locks within one, the lattice fills in as they lock. A real tool —
/// assisted manual alignment where a hand beats a homography at the edges. Pure: the desk applies
/// each nudge to the placement and hands the mesh back.
/// </summary>
public sealed class AlignmentGame
{
    public const float LockPx = 1f;
    public const float MaxStepPx = 20f;

    private readonly float[] _target;
    private float[] _current;
    private SKPoint[] _targetNodes;
    private SKPoint[] _currentNodes;

    public AlignmentGame(string screenId, string name, int columns, int rows, float width, float height, IReadOnlyList<float> target, IReadOnlyList<float> current)
    {
        ScreenId = screenId;
        Name = name;
        Columns = Math.Max(2, columns);
        Rows = Math.Max(2, rows);
        Width = width;
        Height = height;
        _target = Sized(target);
        _current = Sized(current);
        _targetNodes = WarpGrid.Nodes(Columns, Rows, Width, Height, _target);
        _currentNodes = WarpGrid.Nodes(Columns, Rows, Width, Height, _current);
        Picked = FirstOpen(0);
    }

    public string ScreenId { get; }
    public string Name { get; }
    public int Columns { get; }
    public int Rows { get; }
    public float Width { get; }
    public float Height { get; }
    public int NodeCount => Columns * Rows;
    public int Picked { get; private set; }
    public IReadOnlyList<float> Target => _target;
    public IReadOnlyList<float> Current => _current;
    public IReadOnlyList<SKPoint> TargetNodes => _targetNodes;
    public IReadOnlyList<SKPoint> CurrentNodes => _currentNodes;
    public int Nudges { get; private set; }

    private float[] Sized(IReadOnlyList<float> offsets)
    {
        var arr = new float[NodeCount * 2];
        for (var i = 0; i < arr.Length && i < offsets.Count; i++) arr[i] = offsets[i];
        return arr;
    }

    /// <summary>The game for a placement against the solver's mesh, resampled to the placement's density when they differ.</summary>
    public static AlignmentGame From(string screenId, string name, int columns, int rows, float width, float height, string? currentMesh, string? targetMesh, int targetColumns, int targetRows)
    {
        var current = WarpGrid.Parse(currentMesh, columns, rows);
        var targetLine = targetColumns == columns && targetRows == rows ? targetMesh : WarpGrid.Resampled(targetMesh, targetColumns, targetRows, columns, rows);
        var target = WarpGrid.Parse(targetLine, columns, rows);
        return new AlignmentGame(screenId, name, columns, rows, width, height, target, current);
    }

    /// <summary>The mesh as the desk now has it (after a nudge landed, or an edit by hand).</summary>
    public void SetCurrent(IReadOnlyList<float> offsets)
    {
        _current = Sized(offsets);
        _currentNodes = WarpGrid.Nodes(Columns, Rows, Width, Height, _current);
    }

    public float Residual(int node)
    {
        if (node < 0 || node >= NodeCount) return float.MaxValue;
        var a = _currentNodes[node];
        var b = _targetNodes[node];
        return MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    public bool IsLocked(int node) => Residual(node) <= LockPx;

    public int LockedCount
    {
        get
        {
            var n = 0;
            for (var i = 0; i < NodeCount; i++) if (IsLocked(i)) n++;
            return n;
        }
    }

    public float WorstResidual
    {
        get
        {
            var worst = 0f;
            for (var i = 0; i < NodeCount; i++) worst = MathF.Max(worst, Residual(i));
            return worst;
        }
    }

    public bool IsDone => LockedCount == NodeCount;
    public double Progress => (double)LockedCount / NodeCount;

    private int FirstOpen(int from)
    {
        for (var k = 0; k < NodeCount; k++)
        {
            var i = (from + k) % NodeCount;
            if (!IsLocked(i)) return i;
        }
        return Math.Clamp(from, 0, NodeCount - 1);
    }

    /// <summary>The next node still open (wrapping); false when every node is locked.</summary>
    public bool Next()
    {
        if (IsDone) return false;
        Picked = FirstOpen(Picked + 1);
        return true;
    }

    public bool Prev()
    {
        if (IsDone) return false;
        for (var k = 1; k <= NodeCount; k++)
        {
            var i = ((Picked - k) % NodeCount + NodeCount) % NodeCount;
            if (!IsLocked(i)) { Picked = i; return true; }
        }
        return false;
    }

    public void Pick(int node)
    {
        if (node >= 0 && node < NodeCount) Picked = node;
    }

    /// <summary>The move for the picked node, clamped to a sane step; the desk applies it to the placement and calls <see cref="SetCurrent"/>.</summary>
    public (int Node, float Dx, float Dy) Nudge(float dx, float dy)
    {
        Nudges++;
        return (Picked, Math.Clamp(dx, -MaxStepPx, MaxStepPx), Math.Clamp(dy, -MaxStepPx, MaxStepPx));
    }

    /// <summary>The move that would land the picked node on its target in one — the "snap" for a pad's button.</summary>
    public (int Node, float Dx, float Dy) SnapMove()
    {
        var a = _currentNodes[Picked];
        var b = _targetNodes[Picked];
        return (Picked, b.X - a.X, b.Y - a.Y);
    }

    /// <summary>"node 5/25 · 3.2 px · 12 of 25 locked", or the done line.</summary>
    public string Words => IsDone
        ? $"{Name}: every node within a pixel — {NodeCount} locked, done in {Nudges} nudge{(Nudges == 1 ? "" : "s")}"
        : $"{Name}: node {Picked + 1}/{NodeCount} · {Residual(Picked):0.0} px · {LockedCount} of {NodeCount} locked · worst {WorstResidual:0.0} px";
}
