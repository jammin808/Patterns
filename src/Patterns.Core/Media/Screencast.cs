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
/// <summary>How the browser's screencast is doing, judged from what arrived — never "healthy forever after one frame".</summary>
public enum ScreencastLiveness
{
    /// <summary>Not asked for, or refused: the screenshot poll carries the picture.</summary>
    Off,
    /// <summary>Asked for, nothing yet, within its grace.</summary>
    Starting,
    /// <summary>A frame arrived lately.</summary>
    Delivering,
    /// <summary>No frame lately and the page reports no media playing: a still page sends nothing, and that is fine.</summary>
    Static,
    /// <summary>No frame lately while the page reports media playing, or the acks keep failing: the stream has stalled and is restarted.</summary>
    Stalled,
}

/// <summary>The screencast's liveness rule: pure, so a stall is a table and not a feeling.</summary>
public static class ScreencastHealth
{
    /// <summary>A stream that sent nothing this long after starting is not delivering.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    /// <summary>A frame within this long is "lately".</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(2);

    /// <summary>No frame for this long while the page's media plays: stalled.</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(3);

    /// <summary>Acks failed in a row that mean the protocol session is gone.</summary>
    public const int AckFailuresForStall = 5;

    /// <summary>Restarts tried before the screenshot poll is left in charge for good (until the next navigation).</summary>
    public const int MaxRestarts = 3;

    public static ScreencastLiveness Judge(bool on, long framesSinceStart, double msSinceStart, double msSinceLastFrame, bool pageMediaPlaying, int consecutiveAckFailures)
    {
        if (!on) return ScreencastLiveness.Off;
        if (consecutiveAckFailures >= AckFailuresForStall) return ScreencastLiveness.Stalled;
        if (framesSinceStart == 0) return msSinceStart < Grace.TotalMilliseconds ? ScreencastLiveness.Starting : pageMediaPlaying ? ScreencastLiveness.Stalled : ScreencastLiveness.Static;
        if (msSinceLastFrame < FreshFor.TotalMilliseconds) return ScreencastLiveness.Delivering;
        if (pageMediaPlaying && msSinceLastFrame >= StallAfter.TotalMilliseconds) return ScreencastLiveness.Stalled;
        return ScreencastLiveness.Static;
    }

    /// <summary>Whether the screencast is the picture's carrier in this state (else the screenshot poll stands in).</summary>
    public static bool Carries(ScreencastLiveness liveness) => liveness is ScreencastLiveness.Starting or ScreencastLiveness.Delivering or ScreencastLiveness.Static;

    /// <summary>The page's own answer to "is any media playing": a script that returns true or false.</summary>
    public const string MediaPlayingScript = "(function(){try{var m=document.querySelectorAll('video,audio');for(const e of m){if(!e.paused&&!e.ended&&e.readyState>2)return true;}return false;}catch(x){return false;}})()";
}

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

    /// <summary>
    /// The start parameters for a page: JPEG at <paramref name="quality"/> (default <see cref="Quality"/>), scaled
    /// by the browser to fit <paramref name="width"/> × <paramref name="height"/> (the page's own size by default;
    /// round 68's capture plan hands a smaller box on a small machine), and every frame unless
    /// <paramref name="everyNthFrame"/> asks the browser to send only every nth (2 halves a 60 fps page).
    /// </summary>
    public static string StartParameters(int width, int height, int quality = Quality, int everyNthFrame = 1)
    {
        var w = Math.Max(1, width).ToString(CultureInfo.InvariantCulture);
        var h = Math.Max(1, height).ToString(CultureInfo.InvariantCulture);
        var q = Math.Clamp(quality, 1, 100).ToString(CultureInfo.InvariantCulture);
        var n = Math.Clamp(everyNthFrame, 1, 10).ToString(CultureInfo.InvariantCulture);
        return "{\"format\":\"jpeg\",\"quality\":" + q + ",\"maxWidth\":" + w + ",\"maxHeight\":" + h + ",\"everyNthFrame\":" + n + "}";
    }

    /// <summary>The bare start (round 77): JPEG at <paramref name="quality"/> and nothing else asked — the page's own size, every frame.</summary>
    public static string MinimalParameters(int quality = Quality)
        => "{\"format\":\"jpeg\",\"quality\":" + Math.Clamp(quality, 1, 100).ToString(CultureInfo.InvariantCulture) + "}";

    /// <summary>The barest start there is: the format alone.</summary>
    public const string BareParameters = "{\"format\":\"jpeg\"}";

    /// <summary>
    /// Round 77: what a start tries, in order, when the browser refuses. The field saw
    /// Page.startScreencast answered with E_INVALIDARG two to five seconds after a YouTube page
    /// opened, every time, and never asked again — the screenshot poll carried a 20 fps ceiling for
    /// the rest of the page's life. A refusal of the full ask (size, quality, every nth frame) is
    /// followed by the ask without the size, then the format alone; a browser that refuses all
    /// three is asked again later (<see cref="ScreencastRetry"/>). Distinct: a ladder whose rungs
    /// are the same text is one rung.
    /// </summary>
    public static IReadOnlyList<string> StartLadder(int width, int height, int quality = Quality, int everyNthFrame = 1)
    {
        var rungs = new List<string>(3) { StartParameters(width, height, quality, everyNthFrame) };
        var minimal = MinimalParameters(quality);
        if (!rungs.Contains(minimal)) rungs.Add(minimal);
        if (!rungs.Contains(BareParameters)) rungs.Add(BareParameters);
        return rungs;
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
    private readonly object _tickGate = new();
    private int _next;
    private int _count;

    /// <summary>The span the rate is counted over.</summary>
    public static readonly long WindowTicks = TimeSpan.TicksPerSecond;

    /// <summary>A frame arrived at this instant (UTC ticks).</summary>
    public void Tick(long utcTicks)
    {
        lock (_tickGate)
        {
            _ticks[_next] = utcTicks;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Frames in the second before <paramref name="nowUtcTicks"/>; 0 when nothing moved.</summary>
    public double Rate(long nowUtcTicks)
    {
        lock (_tickGate)
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
            lock (_tickGate)
            {
                return _count == 0 ? 0 : _ticks[(_next - 1 + Capacity) % Capacity];
            }
        }
    }
}

/// <summary>
/// Round 77: when a refused screencast is asked for again. A refusal is not the page's last word —
/// the compositor it belongs to may not be ready, the video element may not exist yet — so the
/// source asks again on a backoff: a second, two, four, eight, fifteen, then every thirty seconds
/// for as long as the page is up. The clock and the words are here, pure; the capture tick reads them.
/// </summary>
public static class ScreencastRetry
{
    /// <summary>The waits between asks, by how many times the browser has refused so far.</summary>
    public static readonly int[] DelaysMs = { 1000, 2000, 4000, 8000, 15000, 30000 };

    /// <summary>How long after the <paramref name="refusals"/>th refusal the next ask comes (capped at the last delay).</summary>
    public static int DelayMs(int refusals)
        => refusals <= 1 ? DelaysMs[0] : DelaysMs[Math.Min(refusals - 1, DelaysMs.Length - 1)];

    /// <summary>Whether an ask is due: the refusal's tick plus its delay is behind now (UTC ticks); never before a first refusal.</summary>
    public static bool Due(int refusals, long refusedAtTicks, long nowTicks)
        => refusals > 0 && refusedAtTicks > 0 && nowTicks - refusedAtTicks >= DelayMs(refusals) * TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// The status line's clause: "screenshot poll (the screencast was refused ×3; asking again)"
    /// while the poll stands in, "screencast (after 3 refusals)" once a later ask was taken; ""
    /// when nothing was ever refused.
    /// </summary>
    public static string StatusWords(bool screencastOn, int refusals)
    {
        if (refusals <= 0) return "";
        var times = refusals == 1 ? "once" : "×" + refusals.ToString(CultureInfo.InvariantCulture);
        return screencastOn
            ? $"screencast (after {(refusals == 1 ? "1 refusal" : refusals.ToString(CultureInfo.InvariantCulture) + " refusals")})"
            : $"screenshot poll (the screencast was refused {times}; asking again)";
    }
}
