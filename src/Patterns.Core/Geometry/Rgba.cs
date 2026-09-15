namespace Patterns.Core.Geometry;

/// <summary>
/// A colour as the show core holds it — four bytes, parsed once from the show's hex words (RGB,
/// RRGGBB or AARRGGBB, with or without the hash) and cached on the snapshot, so no renderer parses
/// a string in a frame and no rule about a colour needs a drawing library. The render side
/// converts to its own colour type at the edge.
/// </summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Rgba Black = new(0, 0, 0);
    public static readonly Rgba White = new(255, 255, 255);
    public static readonly Rgba Transparent = new(0, 0, 0, 0);

    /// <summary>The colour packed as AARRGGBB.</summary>
    public uint Argb => (uint)A << 24 | (uint)R << 16 | (uint)G << 8 | B;

    public Rgba WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>The hex word the show file would carry: "#RRGGBB", or "#AARRGGBB" when not opaque.</summary>
    public string ToHex() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();

    /// <summary>RGB, RRGGBB or AARRGGBB, a leading hash optional; black and false for anything else.</summary>
    public static bool TryParse(string? hex, out Rgba colour)
    {
        colour = Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length == 3)
        {
            s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
        }
        if (s.Length == 6)
        {
            if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return false;
            colour = new Rgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        if (s.Length == 8)
        {
            if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var argb)) return false;
            colour = new Rgba((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
            return true;
        }
        return false;
    }

    public static Rgba Parse(string? hex, Rgba fallback) => TryParse(hex, out var c) ? c : fallback;

    /// <summary>Splits a comma, semicolon or space separated hex list; guarantees at least one colour.</summary>
    public static Rgba[] ParseList(string? csv, Rgba fallback)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new[] { fallback };
        var parts = csv.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<Rgba>(parts.Length);
        foreach (var p in parts)
        {
            if (TryParse(p, out var c)) list.Add(c);
        }
        if (list.Count == 0) list.Add(fallback);
        return list.ToArray();
    }
}
