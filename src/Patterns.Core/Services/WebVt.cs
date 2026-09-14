using System.Globalization;
using System.Text.Json;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// What a page's player says when asked: where it is, how long it is, whether it is paused,
/// whether an advert is showing over it. <see cref="Ok"/> is false when the page has no player
/// (yet) — a dashboard, a deck, a video site still loading.
/// </summary>
public readonly record struct WebPlayerReading(bool Ok, double Position, double Duration, bool Paused, bool AdShowing, bool Muted)
{
    public static readonly WebPlayerReading None = default;

    /// <summary>"1:23 / 4:56" — the desk's clock for the page's video; "" without a player.</summary>
    public string ClockText => !Ok ? "" : Duration > 0 ? $"{WebVt.TimeText(Position)} / {WebVt.TimeText(Duration)}" : WebVt.TimeText(Position);
}

/// <summary>
/// The arm on one page: set when the operator (or the look) said "play from here when it goes to
/// air", spent the moment the page reaches the room. Runtime only — a show opens with nothing armed.
/// </summary>
public readonly record struct WebArm(bool Armed, double StartSeconds, bool ByLook, DateTime? ArmedUtc, DateTime? PlayedUtc, double PlayedFrom)
{
    public static readonly WebArm None = new(false, 0, false, null, null, 0);

    /// <summary>The operator's arm at a point: theirs over the look's, and the mark is theirs.</summary>
    public WebArm ArmedBy(bool byLook, double seconds, DateTime nowUtc)
        => this with { Armed = true, StartSeconds = Math.Max(0, seconds), ByLook = byLook, ArmedUtc = nowUtc, PlayedUtc = null };

    /// <summary>Spent: it played from its mark at this instant.</summary>
    public WebArm Fired(DateTime nowUtc) => new(false, StartSeconds, ByLook, ArmedUtc, nowUtc, StartSeconds);

    public WebArm Cleared() => new(false, StartSeconds, false, null, null, PlayedFrom);
}

/// <summary>
/// The armed web VT: a video on a web page — YouTube, Vimeo, any page with a video element —
/// set up before it goes to air and started by the take itself.
///
/// The fault this answers: a YouTube link put on a pattern plays when the browser opens, not when
/// the room sees it. The operator opens the page in the preview to check it — an advert plays,
/// the sound has to be heard, the wanted moment is a minute in — and by the time the look is
/// taken the video is somewhere else. So the page is set up first (the advert skipped, the sound
/// checked, the start point found), then ARMED: the player is put at the mark and paused, and the
/// engine plays it from there the moment the page reaches an output — a TAKE, a cue, the clicker's
/// NEXT. A look can carry the same instruction (play from a point when it goes to air) so a step
/// built before the day needs nobody at the desk. Pure: the scripts, the time words, the rule.
/// </summary>
public static class WebVt
{
    /// <summary>The time text a mark takes: "83", "1:23", "1:02:03", "1m23s", "90s".</summary>
    public static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return false;
        if (t is "start" or "top" or "beginning" or "0") return true;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) && plain >= 0 && !double.IsInfinity(plain))
        {
            seconds = plain;
            return true;
        }
        if (t.Contains(':'))
        {
            var parts = t.Split(':');
            if (parts.Length is < 2 or > 3) return false;
            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
                total = total * 60 + v;
            }
            seconds = total;
            return true;
        }
        var matched = false;
        double sum = 0;
        var number = "";
        foreach (var ch in t)
        {
            if (char.IsAsciiDigit(ch) || ch == '.')
            {
                number += ch;
                continue;
            }
            if (number.Length == 0 || !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return false;
            switch (ch)
            {
                case 'h': sum += n * 3600; break;
                case 'm': sum += n * 60; break;
                case 's': sum += n; break;
                default: return false;
            }
            number = "";
            matched = true;
        }
        if (number.Length > 0) return false;   // "1m30" — a trailing number with no unit is a mistake, not thirty seconds
        if (!matched) return false;
        seconds = sum;
        return true;
    }

    /// <summary>"1:23", "1:02:03", "0:00" — the mark as the desk writes it.</summary>
    public static string TimeText(double seconds)
    {
        var s = (long)Math.Floor(Math.Max(0, seconds));
        var h = s / 3600;
        var m = s % 3600 / 60;
        var sec = s % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m}:{sec:00}";
    }

    /// <summary>"off", "disarm", "clear", "none" — the value that clears an arm.</summary>
    public static bool IsOff(string? value)
    {
        var t = (value ?? "").Trim();
        return t.Equals("off", StringComparison.OrdinalIgnoreCase) || t.Equals("disarm", StringComparison.OrdinalIgnoreCase)
               || t.Equals("clear", StringComparison.OrdinalIgnoreCase) || t.Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A value an ARM or a MARK takes: empty (the player's own place), a time, or off.</summary>
    public static bool IsValidValue(string? value)
        => string.IsNullOrWhiteSpace(value) || IsOff(value) || TryParseTime(value, out _);

    // ---- the rule --------------------------------------------------------------------------------

    /// <summary>The arm fires when the page reaches an output it was not on: the take, the cue, the click.</summary>
    public static bool ShouldFire(in WebArm arm, bool wasOnAir, bool isOnAir) => arm.Armed && !wasOnAir && isOnAir;

    /// <summary>
    /// The page's address as the browser should open it when it opens straight onto the air with a
    /// start point: YouTube and Vimeo take the point in the address, so the first frame the room
    /// sees is the right one. Anything else opens as it is and is moved by script once its player
    /// answers. The mount key is never this — the pattern's own address stays the key.
    /// </summary>
    public static string AddressWithStart(string url, PageService service, double startSeconds)
    {
        var s = (long)Math.Floor(Math.Max(0, startSeconds));
        if (s <= 0 || string.IsNullOrEmpty(url)) return url;
        var text = s.ToString(CultureInfo.InvariantCulture);
        switch (service)
        {
            case PageService.YouTube:
                if (url.Contains("start=", StringComparison.OrdinalIgnoreCase)) return url;
                return url + (url.Contains('?') ? "&" : "?") + "start=" + text;
            case PageService.Vimeo:
                if (url.Contains("#t=", StringComparison.OrdinalIgnoreCase)) return url;
                return url + "#t=" + text + "s";
            default:
                return url;
        }
    }

    // ---- the scripts -----------------------------------------------------------------------------
    //
    // One function per service that finds the player and answers with the same shape, so the
    // engine reads every page the same way. YouTube's player is driven through its own API (the
    // embed and the watch page both expose it); anything else through the first video element.

    private const string YouTubeFind =
        "var p=document.getElementById('movie_player')||document.querySelector('.html5-video-player');if(!p||!p.getPlayerState)return null;";

    private const string VideoFind = "var v=document.querySelector('video');if(!v)return null;";

    /// <summary>Returns the player's reading as JSON ({"ok":true,"t":..,"d":..,"paused":..,"ad":..,"muted":..}), or null with no player.</summary>
    public static string StateScript(PageService service) => service == PageService.YouTube
        ? "(function(){" + YouTubeFind +
          "var s=p.getPlayerState();var ad=!!(p.classList&&p.classList.contains('ad-showing'));" +
          "return JSON.stringify({ok:true,t:p.getCurrentTime()||0,d:p.getDuration()||0,paused:s!==1&&s!==3,ad:ad,muted:!!(p.isMuted&&p.isMuted())});})()"
        : "(function(){" + VideoFind +
          "return JSON.stringify({ok:true,t:v.currentTime||0,d:isFinite(v.duration)?v.duration:0,paused:v.paused,ad:false,muted:!!v.muted});})()";

    /// <summary>The player put at the mark and paused — the armed picture, still, ready.</summary>
    public static string PrepareScript(PageService service, double seconds)
    {
        var t = Seconds(seconds);
        return service == PageService.YouTube
            ? "(function(){" + YouTubeFind + "p.seekTo(" + t + ",true);p.pauseVideo();return 1;})()"
            : "(function(){" + VideoFind + "v.currentTime=" + t + ";v.pause();return 1;})()";
    }

    /// <summary>The player playing from the mark — the take.</summary>
    public static string PlayFromScript(PageService service, double seconds)
    {
        var t = Seconds(seconds);
        return service == PageService.YouTube
            ? "(function(){" + YouTubeFind + "if(Math.abs((p.getCurrentTime()||0)-" + t + ")>0.75)p.seekTo(" + t + ",true);p.playVideo();return 1;})()"
            : "(function(){" + VideoFind + "if(Math.abs((v.currentTime||0)-" + t + ")>0.75)v.currentTime=" + t + ";var r=v.play();if(r&&r.catch)r.catch(function(){});return 1;})()";
    }

    /// <summary>The advert's skip button pressed when there is one — a page cannot skip what the site does not let it.</summary>
    public static string SkipAdScript(PageService service) => service == PageService.YouTube
        ? "(function(){var b=document.querySelector('.ytp-skip-ad-button,.ytp-ad-skip-button,.ytp-ad-skip-button-modern,button.ytp-ad-skip-button-slot');if(b){b.click();return 1;}return 0;})()"
        : "0";

    private static string Seconds(double seconds) => Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The reading back from <see cref="StateScript"/>: the browser answers a script with JSON, and a string result comes doubly encoded.</summary>
    public static WebPlayerReading ParseReading(string? result)
    {
        if (string.IsNullOrWhiteSpace(result) || result == "null") return WebPlayerReading.None;
        try
        {
            using var outer = JsonDocument.Parse(result);
            var root = outer.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (string.IsNullOrEmpty(inner) || inner == "null") return WebPlayerReading.None;
                using var doc = JsonDocument.Parse(inner);
                return Read(doc.RootElement);
            }
            return Read(root);
        }
        catch (JsonException)
        {
            return WebPlayerReading.None;
        }

        static WebPlayerReading Read(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return WebPlayerReading.None;
            if (!e.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return WebPlayerReading.None;
            return new WebPlayerReading(true, Number(e, "t"), Number(e, "d"), Flag(e, "paused"), Flag(e, "ad"), Flag(e, "muted"));
        }

        static double Number(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && !double.IsNaN(d) ? Math.Max(0, d) : 0;

        static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    // ---- the words -------------------------------------------------------------------------------

    /// <summary>
    /// One line for the desk, the phone, the deck and STATE: armed and where from, played and when,
    /// an advert showing, the clock. "" when the page has no player and nothing armed.
    /// </summary>
    public static string Words(in WebArm arm, in WebPlayerReading reading, DateTime nowUtc)
    {
        var parts = new List<string>(3);
        if (arm.Armed)
        {
            parts.Add($"ARMED at {TimeText(arm.StartSeconds)}{(arm.ByLook ? " by the look" : "")} — plays when it goes to air");
        }
        else if (arm.PlayedUtc is { } played)
        {
            var ago = nowUtc - played;
            parts.Add(ago < TimeSpan.FromMinutes(10)
                ? $"played from {TimeText(arm.PlayedFrom)} {(ago.TotalSeconds < 60 ? $"{(int)ago.TotalSeconds} s" : $"{(int)ago.TotalMinutes} min")} ago"
                : $"played from {TimeText(arm.PlayedFrom)}");
        }
        if (reading.Ok)
        {
            if (reading.AdShowing) parts.Add("ADVERT showing — skipped when the site allows");
            parts.Add($"{reading.ClockText}{(reading.Paused ? " · paused" : " · playing")}{(reading.Muted ? " · muted on the page" : "")}");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>The Show page's and the clicker's short form: "VT armed at 1:23" / "VT playing 1:30 / 4:56"; "" with nothing to say.</summary>
    public static string ShortWords(in WebArm arm, in WebPlayerReading reading)
    {
        if (arm.Armed) return $"VT armed at {TimeText(arm.StartSeconds)}";
        if (reading.Ok && !reading.Paused) return $"VT playing {reading.ClockText}";
        return "";
    }

    // ---- the looks -----------------------------------------------------------------------------

    /// <summary>
    /// The web pages a look would put up that ask to play from a point when they go to air — what
    /// the engine opens ahead of a cue (pre-rolled, prepared at the mark) so the take lands on the
    /// right frame. The key, the address, the service and the mark; pages that carry no such ask
    /// are live pages and are not opened early.
    /// </summary>
    public static IReadOnlyList<PreRoll.WebWant> AutoPlayPagesIn(string? lookJson)
    {
        if (string.IsNullOrWhiteSpace(lookJson)) return Array.Empty<PreRoll.WebWant>();
        LookData? data;
        try
        {
            data = JsonUtil.Deserialize<LookData>(lookJson);
        }
        catch
        {
            return Array.Empty<PreRoll.WebWant>();
        }
        if (data is null || data.Blackout) return Array.Empty<PreRoll.WebWant>();
        var list = new List<PreRoll.WebWant>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string url, PageServicePick pick, bool autoPlay, double start, bool mute, int width, int height, double zoom, bool clean)
        {
            if (!autoPlay || string.IsNullOrWhiteSpace(url)) return;
            var normalized = WebAddress.Normalize(url);
            var key = Media.InputKeys.Web(normalized);
            if (key.Length == 0 || !seen.Add(key)) return;
            list.Add(new PreRoll.WebWant(key, normalized, WebPresets.Resolve(normalized, pick), start, mute, $"{width}x{height}", zoom, WebPresets.CleanCss(normalized, pick, clean)));
        }
        var p = data.Pattern;
        if (p.Kind == PatternKind.Media && p.Media.Source == MediaSource.Web)
        {
            Add(p.Media.WebUrl, p.Media.WebService, p.Media.WebAutoPlay, p.Media.WebStartSeconds, p.Media.Mute, p.Media.WebWidth, p.Media.WebHeight, p.Media.WebZoomPct, p.Media.WebClean);
        }
        foreach (var l in new[] { p.Layer1, p.Layer2 })
        {
            if (l.Enabled && l.Source == LayerSource.Web) Add(l.WebUrl, l.WebService, l.WebAutoPlay, l.WebStartSeconds, l.Mute, l.WebWidth, l.WebHeight, l.WebZoomPct, l.WebClean);
        }
        return list;
    }
}
