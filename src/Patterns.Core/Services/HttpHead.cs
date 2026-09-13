using System.Text;

namespace Patterns.Core.Services;

/// <summary>
/// How much of a request a port reads before it stops reading, and how long it waits for it:
/// the head's bytes and lines, the body's bytes, the seconds for each. A phone's request is a
/// few hundred bytes and arrives at once; so is a tablet's. Anything past these is not a phone.
/// </summary>
public sealed record HttpLimits(
    int MaxHeadBytes = 16 * 1024,
    int MaxHeaderLines = 64,
    int MaxBodyBytes = 64 * 1024,
    double HeadSeconds = 10,
    double BodySeconds = 10)
{
    /// <summary>The control port's: a tablet, Companion, the desk's own pages.</summary>
    public static readonly HttpLimits Control = new();

    /// <summary>The audience port's: tighter, because nobody vouches for what is on the other end.</summary>
    public static readonly HttpLimits Audience = new(MaxHeadBytes: 8 * 1024, MaxHeaderLines: 48, MaxBodyBytes: 16 * 1024, HeadSeconds: 5, BodySeconds: 5);
}

/// <summary>
/// A request's head, parsed from the bytes before the blank line: the method, the path, the
/// content length, whether the desk's own client header was there — or the fault, with the
/// status that answers it. Pure, so every shape a port can be sent is a unit test.
/// </summary>
public sealed record HttpHead(string Method, string Path, int ContentLength, bool ClientHeader, int HeaderCount, string Fault = "", string Status = "")
{
    public bool Ok => Fault.Length == 0;

    /// <summary>Where the blank line that ends a head is in <paramref name="bytes"/>: its index and its length (4 for CRLF CRLF, 2 for LF LF), or -1.</summary>
    public static (int Index, int Length) EndOfHead(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            if (i >= 3 && bytes[i - 1] == (byte)'\r' && bytes[i - 2] == (byte)'\n' && bytes[i - 3] == (byte)'\r') return (i - 3, 4);
            if (i >= 1 && bytes[i - 1] == (byte)'\n') return (i - 1, 2);
        }
        return (-1, 0);
    }

    public static HttpHead Parse(ReadOnlySpan<byte> head, HttpLimits limits) => Parse(Encoding.UTF8.GetString(head), limits);

    public static HttpHead Parse(string head, HttpLimits limits)
    {
        var lines = head.Replace("\r\n", "\n").Split('\n');
        var request = lines.Length > 0 ? lines[0].Trim() : "";
        var parts = request.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0].Length == 0 || parts[0].Length > 16 || !parts[0].All(char.IsAsciiLetterUpper) || parts[1][0] != '/')
        {
            return Bad("not an HTTP request line", "400 Bad Request");
        }
        var method = parts[0];
        var path = parts[1];
        var contentLength = 0;
        var clientHeader = false;
        var headers = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            if (++headers > limits.MaxHeaderLines) return Bad("too many headers", "431 Request Header Fields Too Large");
            var colon = line.IndexOf(':');
            if (colon <= 0) return Bad("a header with no name", "400 Bad Request");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, out var len) || len < 0) return Bad("a content length that is not a number", "400 Bad Request");
                if (len > limits.MaxBodyBytes) return Bad($"a body of {len} bytes; {limits.MaxBodyBytes} is the most", "413 Content Too Large");
                contentLength = (int)len;
            }
            else if (name.Equals("X-Patterns-Client", StringComparison.OrdinalIgnoreCase))
            {
                clientHeader = true;
            }
        }
        return new HttpHead(method, path, contentLength, clientHeader, headers);

        static HttpHead Bad(string fault, string status) => new("", "", 0, false, 0, fault, status);
    }
}
