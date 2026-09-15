namespace Patterns.Core.Geometry;

/// <summary>
/// The show core's own pixel geometry — the integer points, sizes and rectangles the rig, the
/// decks, the stream plan and the wall's gap map are argued in, with no drawing library in
/// sight. The render side converts to its canvas types at the edge (a call, never a copy of
/// the rules), so the core that plans a raster never links the library that paints it. The
/// semantics follow the canvas rectangle they replaced, so nothing that was true of a plan
/// changed when the type did: a rectangle is left, top, right, bottom; its width and height are
/// differences; <see cref="RasterRect.IsEmpty"/> is the all-zero rectangle, as the canvas type
/// read it; a union is the plain bounding box; an intersection of rectangles that do not
/// overlap is empty.
/// </summary>
public readonly record struct RasterPoint(int X, int Y)
{
    public static readonly RasterPoint Zero = default;

    public static RasterPoint operator +(RasterPoint a, RasterPoint b) => new(a.X + b.X, a.Y + b.Y);

    public static RasterPoint operator -(RasterPoint a, RasterPoint b) => new(a.X - b.X, a.Y - b.Y);

    public override string ToString() => $"({X}, {Y})";
}

/// <summary>A width and a height in pixels.</summary>
public readonly record struct RasterSize(int Width, int Height)
{
    public static readonly RasterSize Empty = default;

    /// <summary>The all-zero size — what an unplanned target or an unknown display reads as.</summary>
    public bool IsEmpty => Width == 0 && Height == 0;

    /// <summary>Whether a picture of this size has any pixels at all.</summary>
    public bool HasArea => Width > 0 && Height > 0;

    public long Area => HasArea ? (long)Width * Height : 0;

    /// <summary>The same size on its side: a rotated display.</summary>
    public RasterSize Transposed => new(Height, Width);

    public override string ToString() => $"{Width}×{Height}";
}

/// <summary>A rectangle by its edges; width and height are differences, so a rectangle at (x, y) of w × h is <see cref="Create(int, int, int, int)"/>.</summary>
public readonly record struct RasterRect(int Left, int Top, int Right, int Bottom)
{
    public static readonly RasterRect Empty = default;

    public static RasterRect Create(int x, int y, int width, int height) => new(x, y, x + width, y + height);

    public static RasterRect Create(RasterPoint origin, RasterSize size) => Create(origin.X, origin.Y, size.Width, size.Height);

    /// <summary>The rectangle a size fills from the origin.</summary>
    public static RasterRect Of(RasterSize size) => new(0, 0, size.Width, size.Height);

    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public RasterSize Size => new(Width, Height);

    public RasterPoint Location => new(Left, Top);

    /// <summary>The all-zero rectangle, as the canvas type read it: "no region".</summary>
    public bool IsEmpty => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;

    /// <summary>Whether the rectangle covers any pixels.</summary>
    public bool HasArea => Right > Left && Bottom > Top;

    public int MidX => Left + Width / 2;

    public int MidY => Top + Height / 2;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public bool Contains(RasterPoint p) => Contains(p.X, p.Y);

    public bool Contains(RasterRect r) => r.Left >= Left && r.Right <= Right && r.Top >= Top && r.Bottom <= Bottom;

    public bool IntersectsWith(RasterRect r) => Left < r.Right && r.Left < Right && Top < r.Bottom && r.Top < Bottom;

    public RasterRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public RasterRect Offset(RasterPoint by) => Offset(by.X, by.Y);

    /// <summary>The bounding box of both, as the canvas type computed it — an empty rectangle at the origin still pulls the box to the origin.</summary>
    public static RasterRect Union(RasterRect a, RasterRect b)
        => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    /// <summary>The overlap, or <see cref="Empty"/> when there is none.</summary>
    public static RasterRect Intersect(RasterRect a, RasterRect b)
        => a.IntersectsWith(b)
            ? new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom))
            : Empty;

    public override string ToString() => $"[{Left}, {Top}, {Right}, {Bottom}]";
}
