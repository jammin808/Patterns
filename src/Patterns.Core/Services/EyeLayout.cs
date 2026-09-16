namespace Patterns.Core.Services;

/// <summary>A rectangle of the Eye's world, in its own units (doubles: the camera scales them).</summary>
public readonly record struct EyeRect(double X, double Y, double W, double H)
{
    public static readonly EyeRect Empty = new(0, 0, 0, 0);

    public double Right => X + W;
    public double Bottom => Y + H;
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
    public bool IsEmpty => W <= 0 || H <= 0;

    public bool Contains(double px, double py) => px >= X && px < Right && py >= Y && py < Bottom;

    public bool Intersects(EyeRect o) => !IsEmpty && !o.IsEmpty && X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public EyeRect Union(EyeRect o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        var x = Math.Min(X, o.X);
        var y = Math.Min(Y, o.Y);
        return new EyeRect(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
    }

    public EyeRect Inflate(double d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);
}

/// <summary>One band of the picture: its plane, its word and where it is drawn.</summary>
public sealed record EyeBand(EyePlane Plane, string Label, EyeRect Rect);

/// <summary>Where everything is: a rectangle per thing, the bands, and the whole picture's bounds.</summary>
public sealed class EyePlacement
{
    private readonly Dictionary<string, EyeRect> _rects;
    private readonly List<KeyValuePair<string, EyeRect>> _ordered;

    public EyePlacement(Dictionary<string, EyeRect> rects, IReadOnlyList<EyeBand> bands, EyeRect bounds)
    {
        _rects = rects;
        _ordered = rects.ToList();
        Bands = bands;
        Bounds = bounds;
    }

    public static EyePlacement Empty { get; } = new(new Dictionary<string, EyeRect>(StringComparer.Ordinal), Array.Empty<EyeBand>(), EyeRect.Empty);

    public IReadOnlyDictionary<string, EyeRect> Rects => _rects;
    public IReadOnlyList<EyeBand> Bands { get; }
    public EyeRect Bounds { get; }

    public EyeRect Of(string id) => _rects.TryGetValue(id, out var r) ? r : EyeRect.Empty;

    /// <summary>The thing under a world point, or null — rectangle maths, no tree needed at the Eye's sizes.</summary>
    public string? At(double wx, double wy)
    {
        foreach (var (id, rect) in _ordered)
        {
            if (rect.Contains(wx, wy)) return id;
        }
        return null;
    }

    /// <summary>The rectangle around a thing and what it links to — what a focus fits.</summary>
    public EyeRect Around(string id, EyeGraph graph)
    {
        var r = Of(id);
        foreach (var n in graph.Neighbours(id)) r = r.Union(Of(n));
        return r;
    }
}

/// <summary>
/// The Eye's layout (round 66): a deterministic grid, never a force simulation. Bands top to
/// bottom in <see cref="BandOrder"/>; within a band the tiers are columns in the direction the
/// signal travels; within a cell the things stack in a stable order and the stack is centred on
/// the band. The same show always draws the same picture, a thing keeps its place while its
/// neighbours change, and nothing overlaps by construction — O(n), rebuilt with the graph.
/// </summary>
public static class EyeLayout
{
    public const double NodeW = 200;
    public const double NodeH = 52;
    public const double ColW = 280;
    public const double RowGap = 16;
    public const double BandGap = 72;
    public const double BandPad = 28;
    public const int Tiers = 5;

    public static readonly IReadOnlyList<EyePlane> BandOrder = new[] { EyePlane.Control, EyePlane.Video, EyePlane.Audio, EyePlane.Room };

    public static string BandLabel(EyePlane plane) => plane switch
    {
        EyePlane.Control => "CONTROL",
        EyePlane.Video => "VIDEO",
        EyePlane.Audio => "AUDIO",
        _ => "ROOM",
    };

    public static EyePlacement Place(EyeGraph graph)
    {
        var rects = new Dictionary<string, EyeRect>(StringComparer.Ordinal);
        var bands = new List<EyeBand>();
        var bounds = EyeRect.Empty;
        var width = Tiers * ColW + BandPad;
        double y = 0;
        foreach (var plane in BandOrder)
        {
            var cells = new List<EyeNode>[Tiers];
            var any = false;
            foreach (var n in graph.Nodes)
            {
                if (n.Plane != plane) continue;
                var tier = Math.Clamp(n.Tier, 0, Tiers - 1);
                (cells[tier] ??= new List<EyeNode>()).Add(n);
                any = true;
            }
            if (!any) continue;
            var deepest = cells.Max(c => c?.Count ?? 0);
            var inner = deepest * NodeH + (deepest - 1) * RowGap;
            var bandRect = new EyeRect(0, y, width, inner + 2 * BandPad);
            bands.Add(new EyeBand(plane, BandLabel(plane), bandRect));
            for (var tier = 0; tier < Tiers; tier++)
            {
                var cell = cells[tier];
                if (cell is null) continue;
                cell.Sort(static (a, b) =>
                {
                    var byLabel = string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
                    return byLabel != 0 ? byLabel : string.CompareOrdinal(a.Id, b.Id);
                });
                var stack = cell.Count * NodeH + (cell.Count - 1) * RowGap;
                var top = y + BandPad + (inner - stack) / 2;
                var x = BandPad + tier * ColW;
                for (var i = 0; i < cell.Count; i++)
                {
                    var rect = new EyeRect(x, top + i * (NodeH + RowGap), NodeW, NodeH);
                    rects[cell[i].Id] = rect;
                    bounds = bounds.Union(rect);
                }
            }
            bounds = bounds.Union(bandRect);
            y += bandRect.H + BandGap;
        }
        return new EyePlacement(rects, bands, bounds);
    }
}
