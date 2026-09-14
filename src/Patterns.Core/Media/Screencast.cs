using System.Buffers;
using System.Globalization;

namespace Patterns.Core.Media;

/// <summary>
/// The browser's own screencast, as a frame source reads it.
///
/// Chromium's DevTools protocol (<c>Page.startScreencast</c>) pushes a <c>Page.screencastFrame</c>
/// event for every frame its compositor produces while something on the page moves — a video at
/// the video's own rate, a still page not at all — with the picture inside the event as base64
/// text. That is a different animal from a screenshot taken twenty times a second: a screenshot
/// stops the compositor, reads the whole page back and encodes it while the desk waits, and on a
/// 1080p page that is a good part of a frame's time every time, which is why a YouTube video used
/// to come out at a slideshow's rate. The screencast is the compositor handing over what it just
/// drew, encoded on a thread of its own, and every frame has to be acknowledged before the next
/// one is sent, so a slow reader is never buried.
///
/// The frame is sliced out of the event's text without a second copy of that text: the text is
/// the one allocation the protocol makes per frame, and at thirty frames a second a second copy of
/// a quarter-megabyte string is the large-object heap churning for the length of a show. Pure —
/// the parse, the ack and the rate meter are tested without a browser.
/// </summary>
public static class ScreencastFrame
{
    /// <summary>
    /// The JPEG quality asked for. A video site's picture is already compressed harder than this,
    /// so 70 keeps its grain rather than blocks while the encode in the browser, the text across
    /// the process boundary and the decode here all cost less than at 80 — and at 1080p those
    /// three are what set the rate.
    /// </summary>
    public const int Quality = 70;

    /// <summary>The DevTools event the frames arrive on.</summary>
    public const string EventName = "Page.screencastFrame";

    /// <summary>The start parameters for a page of this size: JPEG at <see cref="Quality"/>, never scaled below the page, every frame.</summary>
    public static string StartParameters(int width, int height, int quality = Quality)
    {
        var w = Math.Max(1, width).ToString(CultureInfo.InvariantCulture);
        var h = Math.Max(1, height).ToString(CultureInfo.InvariantCulture);
        var q = Math.Clamp(quality, 1, 100).ToString(CultureInfo.InvariantCulture);
        return "{\"format\":\"jpeg\",\"quality\":" + q + ",\"maxWidth\":" + w + ",\"maxHeight\":" + h + ",\"everyNthFrame\":1}";
    }

    /// <summary>The ack for a frame — the browser sends the next one only after it.</summary>
    public static string AckParameters(int sessionId)
        => "{\"sessionId\":" + sessionId.ToString(CultureInfo.InvariantCulture) + "}";

    /// <summary>
    /// The frame's session id (what the ack names) and where in the text its base64 picture is.
    /// False for text that is not a screencast frame. The picture is the first field and by far
    /// the longest, so the id is looked for from the end.
    /// </summary>
    public static bool TryParse(string? json, out int sessionId, out Range base64)
    {
        sessionId = 0;
        base64 = default;
        if (string.IsNullOrEmpty(json)) return false;
        var at = json.IndexOf("\"data\":\"", StringComparison.Ordinal);
        if (at < 0) return false;
        var start = at + 8;
        var end = json.IndexOf('"', start);
        if (end < 0) return false;
        var idAt = json.LastIndexOf("\"sessionId\":", StringComparison.Ordinal);
        if (idAt < 0) return false;
        var p = idAt + 12;
        while (p < json.Length && json[p] == ' ') p++;
        var digits = p;
        while (digits < json.Length && char.IsAsciiDigit(json[digits])) digits++;
        if (digits == p || !int.TryParse(json.AsSpan(p, digits - p), NumberStyles.None, CultureInfo.InvariantCulture, out sessionId)) return false;
        base64 = new Range(start, end);
        return true;
    }

    /// <summary>
    /// The picture's bytes — the JPEG — decoded from the text into a rented buffer, which the
    /// caller returns to <see cref="ArrayPool{T}.Shared"/> once decoded. Null when the text is
    /// not base64.
    /// </summary>
    public static byte[]? Rent(string json, Range base64, out int length)
    {
        length = 0;
        var (offset, count) = base64.GetOffsetAndLength(json.Length);
        if (count == 0) return null;
        var chars = json.AsSpan(offset, count);
        var buffer = ArrayPool<byte>.Shared.Rent(count / 4 * 3 + 3);
        if (Convert.TryFromBase64Chars(chars, buffer, out length)) return buffer;
        ArrayPool<byte>.Shared.Return(buffer);
        length = 0;
        return null;
    }
}

/// <summary>
/// Frames per second as a count over the last second — the status line's number, and the
/// difference between "smooth" and "a slideshow" said in a way an operator can read off the desk.
/// Safe for one writer and any number of readers.
/// </summary>
public sealed class FrameRateMeter
{
    private const int Capacity = 256;
    private readonly long[] _ticks = new long[Capacity];
    private int _next;
    private int _count;

    /// <summary>The span the rate is counted over.</summary>
    public static readonly long WindowTicks = TimeSpan.TicksPerSecond;

    /// <summary>A frame arrived at this instant (UTC ticks).</summary>
    public void Tick(long utcTicks)
    {
        lock (_ticks)
        {
            _ticks[_next] = utcTicks;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Frames in the second before <paramref name="nowUtcTicks"/>; 0 when nothing moved.</summary>
    public double Rate(long nowUtcTicks)
    {
        lock (_ticks)
        {
            var since = nowUtcTicks - WindowTicks;
            var n = 0;
            for (var i = 0; i < _count; i++)
            {
                var t = _ticks[(_next - 1 - i + Capacity * 2) % Capacity];
                if (t < since) break;   // the ring is in arrival order: the first older one ends it
                n++;
            }
            return n;
        }
    }

    /// <summary>When the last frame arrived (UTC ticks); 0 for never.</summary>
    public long LastTicks
    {
        get
        {
            lock (_ticks)
            {
                return _count == 0 ? 0 : _ticks[(_next - 1 + Capacity) % Capacity];
            }
        }
    }
}
