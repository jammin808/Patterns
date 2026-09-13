namespace Patterns.Core.Play;

/// <summary>
/// A QR code for the wall — byte mode, error correction M, versions 1 to 10 (up to 213 bytes:
/// a join URL and its room code many times over) — as a table of dark modules. No library: the
/// Reed–Solomon over GF(256), the eight masks and their penalties, the format and version bits are
/// all here, a page each, and a test reads the codewords back off the matrix.
/// </summary>
public sealed class QrCode
{
    public int Version { get; }
    public int Size { get; }
    public int Mask { get; }
    private readonly bool[,] _dark;

    private QrCode(int version, int mask, bool[,] dark)
    {
        Version = version;
        Size = 17 + 4 * version;
        Mask = mask;
        _dark = dark;
    }

    public bool this[int x, int y] => _dark[y, x];

    // ---- tables: error correction M -----------------------------------------------------------

    private static readonly int[] TotalCodewords = { 0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346 };
    private static readonly int[] EcPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
    private static readonly int[] Group1Blocks = { 0, 1, 1, 1, 2, 2, 4, 4, 2, 3, 4 };
    private static readonly int[] Group1Data = { 0, 16, 28, 44, 32, 43, 27, 31, 38, 36, 43 };
    private static readonly int[] Group2Blocks = { 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 1 };
    private static readonly int[] Group2Data = { 0, 0, 0, 0, 0, 0, 0, 0, 39, 37, 44 };
    private static readonly int[][] AlignmentCentres =
    {
        Array.Empty<int>(), Array.Empty<int>(), new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 },
        new[] { 6, 34 }, new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
    };

    /// <summary>How many bytes a version holds at level M.</summary>
    public static int DataCapacity(int version)
    {
        var codewords = Group1Blocks[version] * Group1Data[version] + Group2Blocks[version] * Group2Data[version];
        var countBits = version >= 10 ? 16 : 8;
        return codewords - (4 + countBits + 7) / 8;   // the mode, the count, rounded up to a byte
    }

    /// <summary>The smallest version that holds the text, or null past version 10.</summary>
    public static int? VersionFor(int bytes)
    {
        for (var v = 1; v <= 10; v++) if (DataCapacity(v) >= bytes) return v;
        return null;
    }

    public static QrCode? Encode(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text ?? "");
        if (VersionFor(bytes.Length) is not { } version) return null;
        var codewords = Codewords(bytes, version);
        var size = 17 + 4 * version;
        var best = (Mask: 0, Penalty: int.MaxValue, Matrix: (bool[,]?)null);
        for (var mask = 0; mask < 8; mask++)
        {
            var (dark, reserved) = Blank(version, size);
            Place(dark, reserved, codewords, size, mask);
            WriteFormat(dark, size, mask);
            if (version >= 7) WriteVersion(dark, size, version);
            var penalty = Penalty(dark, size);
            if (penalty < best.Penalty) best = (mask, penalty, dark);
        }
        return new QrCode(version, best.Mask, best.Matrix!);
    }

    // ---- the data: mode, count, bytes, padding, error correction, interleaving -----------------

    private static byte[] Codewords(byte[] data, int version)
    {
        var dataCodewords = Group1Blocks[version] * Group1Data[version] + Group2Blocks[version] * Group2Data[version];
        var bits = new List<bool>(dataCodewords * 8);
        void Push(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }
        Push(0b0100, 4);
        Push(data.Length, version >= 10 ? 16 : 8);
        foreach (var b in data) Push(b, 8);
        var capacityBits = dataCodewords * 8;
        for (var i = 0; i < 4 && bits.Count < capacityBits; i++) bits.Add(false);
        while (bits.Count % 8 != 0) bits.Add(false);
        var pad = false;
        while (bits.Count < capacityBits)
        {
            Push(pad ? 0x11 : 0xEC, 8);
            pad = !pad;
        }
        var all = new byte[dataCodewords];
        for (var i = 0; i < dataCodewords; i++)
        {
            var b = 0;
            for (var k = 0; k < 8; k++) b = (b << 1) | (bits[i * 8 + k] ? 1 : 0);
            all[i] = (byte)b;
        }

        // Blocks: group 1 then group 2, each with its own error correction.
        var blocks = new List<byte[]>();
        var ecs = new List<byte[]>();
        var at = 0;
        var ec = EcPerBlock[version];
        for (var g = 0; g < 2; g++)
        {
            var count = g == 0 ? Group1Blocks[version] : Group2Blocks[version];
            var len = g == 0 ? Group1Data[version] : Group2Data[version];
            for (var b = 0; b < count; b++)
            {
                var block = new byte[len];
                Array.Copy(all, at, block, 0, len);
                at += len;
                blocks.Add(block);
                ecs.Add(ReedSolomon.Encode(block, ec));
            }
        }
        var result = new List<byte>(TotalCodewords[version]);
        var longest = blocks.Max(b => b.Length);
        for (var i = 0; i < longest; i++) foreach (var block in blocks) if (i < block.Length) result.Add(block[i]);
        for (var i = 0; i < ec; i++) foreach (var e in ecs) result.Add(e[i]);
        return result.ToArray();
    }

    // ---- the matrix ---------------------------------------------------------------------------

    private static (bool[,] Dark, bool[,] Reserved) Blank(int version, int size)
    {
        var dark = new bool[size, size];
        var reserved = new bool[size, size];
        void Finder(int x0, int y0)
        {
            for (var dy = -1; dy <= 7; dy++)
            {
                for (var dx = -1; dx <= 7; dx++)
                {
                    var x = x0 + dx;
                    var y = y0 + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    var ring = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                    dark[y, x] = ring <= 1 || ring == 3;
                    reserved[y, x] = true;
                }
            }
        }
        Finder(0, 0);
        Finder(size - 7, 0);
        Finder(0, size - 7);
        for (var i = 8; i < size - 8; i++)
        {
            dark[6, i] = i % 2 == 0;
            dark[i, 6] = i % 2 == 0;
            reserved[6, i] = true;
            reserved[i, 6] = true;
        }
        var centres = AlignmentCentres[version];
        foreach (var cy in centres)
        {
            foreach (var cx in centres)
            {
                if (reserved[cy, cx]) continue;               // over a finder: none there
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        dark[cy + dy, cx + dx] = ring != 1;
                        reserved[cy + dy, cx + dx] = true;
                    }
                }
            }
        }
        // The format areas, the dark module, the version areas: reserved now, written after masking.
        for (var i = 0; i < 9; i++) { reserved[8, i] = true; reserved[i, 8] = true; }
        for (var i = 0; i < 8; i++) { reserved[8, size - 1 - i] = true; reserved[size - 1 - i, 8] = true; }
        dark[size - 8, 8] = true;
        reserved[size - 8, 8] = true;
        if (version >= 7)
        {
            for (var i = 0; i < 6; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    reserved[i, size - 11 + j] = true;
                    reserved[size - 11 + j, i] = true;
                }
            }
        }
        return (dark, reserved);
    }

    /// <summary>The codewords into the free modules, two columns at a time from the bottom right, masked as they land.</summary>
    private static void Place(bool[,] dark, bool[,] reserved, byte[] codewords, int size, int mask)
    {
        var bit = 0;
        var total = codewords.Length * 8;
        var up = true;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;                            // the timing column is skipped whole
            for (var step = 0; step < size; step++)
            {
                var y = up ? size - 1 - step : step;
                for (var k = 0; k < 2; k++)
                {
                    var x = right - k;
                    if (reserved[y, x]) continue;
                    var value = bit < total && ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
                    bit++;
                    dark[y, x] = value ^ MaskBit(mask, x, y);
                }
            }
            up = !up;
        }
    }

    public static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (y / 2 + x / 3) % 2 == 0,
        5 => (x * y) % 2 + (x * y) % 3 == 0,
        6 => ((x * y) % 2 + (x * y) % 3) % 2 == 0,
        _ => ((x + y) % 2 + (x * y) % 3) % 2 == 0,
    };

    /// <summary>The fifteen format bits: level M (00) and the mask, BCH-coded and masked with 0x5412, in both places.</summary>
    public static int FormatBits(int mask)
    {
        var data = (0b00 << 3) | mask;
        var value = data << 10;
        for (var i = 14; i >= 10; i--) if (((value >> i) & 1) != 0) value ^= 0b10100110111 << (i - 10);
        return ((data << 10) | value) ^ 0x5412;
    }

    private static void WriteFormat(bool[,] dark, int size, int mask)
    {
        var bits = FormatBits(mask);
        for (var i = 0; i < 15; i++)
        {
            var b = ((bits >> i) & 1) != 0;
            // Around the top-left finder.
            if (i < 6) dark[i, 8] = b;
            else if (i < 8) dark[i + 1, 8] = b;
            else if (i == 8) dark[8, 7] = b;
            else dark[8, 14 - i] = b;
            // Along the other two finders.
            if (i < 8) dark[8, size - 1 - i] = b;
            else dark[size - 15 + i, 8] = b;
        }
    }

    /// <summary>The eighteen version bits (versions 7 up): the version and its BCH remainder, in both places.</summary>
    public static int VersionBits(int version)
    {
        var value = version << 12;
        for (var i = 17; i >= 12; i--) if (((value >> i) & 1) != 0) value ^= 0b1111100100101 << (i - 12);
        return (version << 12) | value;
    }

    private static void WriteVersion(bool[,] dark, int size, int version)
    {
        var bits = VersionBits(version);
        for (var i = 0; i < 18; i++)
        {
            var b = ((bits >> i) & 1) != 0;
            dark[i / 3, size - 11 + i % 3] = b;
            dark[size - 11 + i % 3, i / 3] = b;
        }
    }

    /// <summary>The four penalty rules of the standard, so the least troublesome mask wins.</summary>
    private static int Penalty(bool[,] dark, int size)
    {
        var penalty = 0;
        // Runs of five or more in a row or a column.
        for (var y = 0; y < size; y++)
        {
            var run = 1;
            for (var x = 1; x < size; x++)
            {
                if (dark[y, x] == dark[y, x - 1]) { run++; if (run == 5) penalty += 3; else if (run > 5) penalty++; }
                else run = 1;
            }
        }
        for (var x = 0; x < size; x++)
        {
            var run = 1;
            for (var y = 1; y < size; y++)
            {
                if (dark[y, x] == dark[y - 1, x]) { run++; if (run == 5) penalty += 3; else if (run > 5) penalty++; }
                else run = 1;
            }
        }
        // 2×2 blocks of one colour.
        for (var y = 0; y < size - 1; y++)
            for (var x = 0; x < size - 1; x++)
                if (dark[y, x] == dark[y, x + 1] && dark[y, x] == dark[y + 1, x] && dark[y, x] == dark[y + 1, x + 1]) penalty += 3;
        // Finder-like runs 1:1:3:1:1 with four light modules either side.
        var pattern = new[] { true, false, true, true, true, false, true };
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x <= size - 7; x++)
            {
                var hit = true;
                for (var k = 0; k < 7 && hit; k++) hit = dark[y, x + k] == pattern[k];
                if (!hit) continue;
                var before = x >= 4 && !dark[y, x - 1] && !dark[y, x - 2] && !dark[y, x - 3] && !dark[y, x - 4];
                var after = x + 10 < size && !dark[y, x + 7] && !dark[y, x + 8] && !dark[y, x + 9] && !dark[y, x + 10];
                if (before || after) penalty += 40;
            }
        }
        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y <= size - 7; y++)
            {
                var hit = true;
                for (var k = 0; k < 7 && hit; k++) hit = dark[y + k, x] == pattern[k];
                if (!hit) continue;
                var before = y >= 4 && !dark[y - 1, x] && !dark[y - 2, x] && !dark[y - 3, x] && !dark[y - 4, x];
                var after = y + 10 < size && !dark[y + 7, x] && !dark[y + 8, x] && !dark[y + 9, x] && !dark[y + 10, x];
                if (before || after) penalty += 40;
            }
        }
        // The balance of dark to light.
        var darkCount = 0;
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) if (dark[y, x]) darkCount++;
        var percent = darkCount * 100 / (size * size);
        var lower = Math.Abs(percent / 5 * 5 - 50) / 5;
        var upper = Math.Abs((percent / 5 * 5 + 5) - 50) / 5;
        penalty += Math.Min(lower, upper) * 10;
        return penalty;
    }

    /// <summary>The modules as rows of '#' and '.', for a test or a log.</summary>
    public string Ascii()
    {
        var sb = new System.Text.StringBuilder();
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++) sb.Append(_dark[y, x] ? '#' : '.');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The codewords read back off the matrix in placement order, unmasked — a test's proof the placement and the mask agree.</summary>
    public byte[] ReadBack()
    {
        var (_, reserved) = Blank(Version, Size);
        var total = TotalCodewords[Version];
        var bytes = new byte[total];
        var bit = 0;
        var up = true;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var step = 0; step < Size; step++)
            {
                var y = up ? Size - 1 - step : step;
                for (var k = 0; k < 2; k++)
                {
                    var x = right - k;
                    if (reserved[y, x]) continue;
                    if (bit < total * 8)
                    {
                        var value = _dark[y, x] ^ MaskBit(Mask, x, y);
                        if (value) bytes[bit >> 3] |= (byte)(0x80 >> (bit & 7));
                    }
                    bit++;
                }
            }
            up = !up;
        }
        return bytes;
    }

    /// <summary>The interleaved codewords the text encodes to — what <see cref="ReadBack"/> should return.</summary>
    public static byte[] CodewordsFor(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text ?? "");
        return VersionFor(bytes.Length) is { } v ? Codewords(bytes, v) : Array.Empty<byte>();
    }
}

/// <summary>Reed–Solomon over GF(256) with the QR polynomial 0x11D: the error-correction codewords for a block.</summary>
public static class ReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] LogTable = new byte[256];

    static ReedSolomon()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            LogTable[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }
        for (var i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    public static byte Multiply(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[LogTable[a] + LogTable[b]];

    /// <summary>The generator polynomial for <paramref name="degree"/> codewords: (x − α^0)(x − α^1)…</summary>
    public static byte[] Generator(int degree)
    {
        var g = new byte[] { 1 };
        for (var i = 0; i < degree; i++)
        {
            var next = new byte[g.Length + 1];
            for (var j = 0; j < g.Length; j++)
            {
                next[j] ^= g[j];
                next[j + 1] ^= Multiply(g[j], Exp[i]);
            }
            g = next;
        }
        return g;
    }

    public static byte[] Encode(byte[] data, int degree)
    {
        var gen = Generator(degree);
        var buffer = new byte[data.Length + degree];
        Array.Copy(data, buffer, data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            var coef = buffer[i];
            if (coef == 0) continue;
            for (var j = 1; j < gen.Length; j++) buffer[i + j] ^= Multiply(gen[j], coef);
        }
        var ec = new byte[degree];
        Array.Copy(buffer, data.Length, ec, 0, degree);
        return ec;
    }

    /// <summary>The polynomial (data then error correction) at α^power — zero for every power below the degree when the codewords are whole.</summary>
    public static byte Syndrome(byte[] codewords, int power)
    {
        byte acc = 0;
        var xPow = Exp[power];
        foreach (var c in codewords) acc = (byte)(Multiply(acc, xPow) ^ c);
        return acc;
    }
}
