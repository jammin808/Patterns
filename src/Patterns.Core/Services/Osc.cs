using System.Buffers.Binary;
using System.Text;

namespace Patterns.Core.Services;

/// <summary>One OSC message: an address pattern and its typed arguments (int, float, double, long, string, byte[], bool, null, or <see cref="OscImpulse"/>).</summary>
public sealed record OscMessage(string Address, IReadOnlyList<object?> Args)
{
    public static OscMessage Of(string address, params object?[] args) => new(address, args);

    /// <summary>The first argument as a number, if there is one (an int, a float, a double, a long, or a bool as 1/0).</summary>
    public double? Number(int index = 0)
    {
        if (index >= Args.Count) return null;
        return Args[index] switch
        {
            int i => i,
            float f => f,
            double d => d,
            long l => l,
            bool b => b ? 1 : 0,
            _ => null,
        };
    }

    /// <summary>The first argument as text, if there is one (a string, or a number written out).</summary>
    public string? Text(int index = 0)
    {
        if (index >= Args.Count) return null;
        return Args[index] switch
        {
            string s => s,
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            _ => null,
        };
    }

    public override string ToString() => Args.Count == 0 ? Address : $"{Address} {string.Join(' ', Args.Select(a => a switch { null => "nil", byte[] b => $"blob[{b.Length}]", OscImpulse => "bang", _ => a.ToString() }))}";
}

/// <summary>The OSC impulse ("bang") argument.</summary>
public sealed class OscImpulse
{
    public static readonly OscImpulse Instance = new();

    private OscImpulse()
    {
    }

    public override string ToString() => "bang";
}

/// <summary>
/// The OSC 1.0 wire format, pure: messages and bundles (flattened on the way in), big-endian,
/// four-byte padding, the common types (i f s b d h T F N I). A packet that does not parse
/// yields no messages rather than an exception — a stray datagram must never take the port down.
/// </summary>
public static class OscCodec
{
    public static byte[] Encode(OscMessage m)
    {
        var buffer = new List<byte>(64);
        WriteString(buffer, m.Address);
        var tags = new StringBuilder(",");
        foreach (var a in m.Args) tags.Append(TagOf(a));
        WriteString(buffer, tags.ToString());
        foreach (var a in m.Args) WriteArg(buffer, a);
        return buffer.ToArray();
    }

    /// <summary>A bundle of messages, time tag "immediately" — one datagram carrying a whole state.</summary>
    public static byte[] EncodeBundle(IEnumerable<OscMessage> messages)
    {
        var buffer = new List<byte>(256);
        WriteString(buffer, "#bundle");
        buffer.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }); // the time tag 1 = now
        var size = new byte[4];
        foreach (var m in messages)
        {
            var element = Encode(m);
            BinaryPrimitives.WriteInt32BigEndian(size, element.Length);
            buffer.AddRange(size);
            buffer.AddRange(element);
        }
        return buffer.ToArray();
    }

    /// <summary>Every message in a packet (a bundle's elements in order, nested bundles flattened); empty when the packet is not OSC.</summary>
    public static IReadOnlyList<OscMessage> Decode(ReadOnlySpan<byte> packet)
    {
        var list = new List<OscMessage>();
        try
        {
            DecodeInto(packet, list, 0);
        }
        catch
        {
            // Malformed: whatever was read before the fault stands; the rest is dropped.
        }
        return list;
    }

    private static void DecodeInto(ReadOnlySpan<byte> packet, List<OscMessage> into, int depth)
    {
        if (packet.Length < 4 || depth > 8) return;
        if (packet[0] == (byte)'#')
        {
            // "#bundle" + 8-byte time tag + (int32 size + element)*
            var pos = 0;
            var tag = ReadString(packet, ref pos);
            if (tag != "#bundle") return;
            pos += 8;
            while (pos + 4 <= packet.Length)
            {
                var size = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(pos, 4));
                pos += 4;
                if (size < 0 || pos + size > packet.Length) return;
                DecodeInto(packet.Slice(pos, size), into, depth + 1);
                pos += size;
            }
            return;
        }
        if (packet[0] != (byte)'/') return;
        var p = 0;
        var address = ReadString(packet, ref p);
        var args = new List<object?>();
        if (p < packet.Length && packet[p] == (byte)',')
        {
            var tags = ReadString(packet, ref p);
            for (var i = 1; i < tags.Length; i++)
            {
                switch (tags[i])
                {
                    case 'i':
                        args.Add(BinaryPrimitives.ReadInt32BigEndian(packet.Slice(p, 4)));
                        p += 4;
                        break;
                    case 'f':
                        args.Add(BinaryPrimitives.ReadSingleBigEndian(packet.Slice(p, 4)));
                        p += 4;
                        break;
                    case 'd':
                        args.Add(BinaryPrimitives.ReadDoubleBigEndian(packet.Slice(p, 8)));
                        p += 8;
                        break;
                    case 'h':
                        args.Add(BinaryPrimitives.ReadInt64BigEndian(packet.Slice(p, 8)));
                        p += 8;
                        break;
                    case 's':
                    case 'S':
                        args.Add(ReadString(packet, ref p));
                        break;
                    case 'b':
                    {
                        var size = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(p, 4));
                        p += 4;
                        if (size < 0 || p + size > packet.Length) throw new FormatException("blob runs past the packet");
                        args.Add(packet.Slice(p, size).ToArray());
                        p += Pad(size);
                        break;
                    }
                    case 'T':
                        args.Add(true);
                        break;
                    case 'F':
                        args.Add(false);
                        break;
                    case 'N':
                        args.Add(null);
                        break;
                    case 'I':
                        args.Add(OscImpulse.Instance);
                        break;
                    default:
                        // A type this build does not know: nothing after it can be sized, so stop here.
                        into.Add(new OscMessage(address, args));
                        return;
                }
            }
        }
        into.Add(new OscMessage(address, args));
    }

    private static string TagOf(object? a) => a switch
    {
        int => "i",
        float => "f",
        double => "d",
        long => "h",
        string => "s",
        byte[] => "b",
        bool b => b ? "T" : "F",
        null => "N",
        OscImpulse => "I",
        _ => "s",
    };

    private static void WriteArg(List<byte> buffer, object? a)
    {
        switch (a)
        {
            case int i:
            {
                Span<byte> b = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(b, i);
                buffer.AddRange(b.ToArray());
                break;
            }
            case float f:
            {
                Span<byte> b = stackalloc byte[4];
                BinaryPrimitives.WriteSingleBigEndian(b, f);
                buffer.AddRange(b.ToArray());
                break;
            }
            case double d:
            {
                Span<byte> b = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(b, d);
                buffer.AddRange(b.ToArray());
                break;
            }
            case long l:
            {
                Span<byte> b = stackalloc byte[8];
                BinaryPrimitives.WriteInt64BigEndian(b, l);
                buffer.AddRange(b.ToArray());
                break;
            }
            case string s:
                WriteString(buffer, s);
                break;
            case byte[] blob:
            {
                Span<byte> b = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(b, blob.Length);
                buffer.AddRange(b.ToArray());
                buffer.AddRange(blob);
                for (var i = blob.Length; i < Pad(blob.Length); i++) buffer.Add(0);
                break;
            }
            case bool:
            case null:
            case OscImpulse:
                break;
            default:
                WriteString(buffer, a.ToString() ?? "");
                break;
        }
    }

    private static void WriteString(List<byte> buffer, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        buffer.AddRange(bytes);
        // The terminator and then up to the four-byte boundary.
        var total = Pad(bytes.Length + 1);
        for (var i = bytes.Length; i < total; i++) buffer.Add(0);
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        var end = pos;
        while (end < data.Length && data[end] != 0) end++;
        if (end >= data.Length) throw new FormatException("string without a terminator");
        var s = Encoding.UTF8.GetString(data.Slice(pos, end - pos));
        pos += Pad(end - pos + 1);
        return s;
    }

    private static int Pad(int n) => (n + 3) & ~3;
}
