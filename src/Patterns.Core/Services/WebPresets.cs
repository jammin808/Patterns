using System.Globalization;
using System.Text.RegularExpressions;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>The service a page comes from, when Patterns knows it well enough to help.</summary>
public enum PageService
{
    Page,
    YouTube,
    Vimeo,
    GoogleSlides,
    PowerPoint,
}

/// <summary>
/// Something a page does — "next", "play", "present" — as the key it takes, or a line of script
/// when the page's own player is the surer way in. A cue, a phone, a Stream Deck key or the wire
/// says the action; the page's service decides the key.
/// </summary>
public sealed record WebPageAction(string Id, string Label, string Chord, string Script = "", string Hint = "")
{
    public bool IsScript => Script.Length > 0;
}

/// <summary>What Patterns knows about a service: its name, what FULL FRAME does to an address, and the actions its pages take.</summary>
public sealed record WebPreset(PageService Service, string Name, string FullFrameNote, IReadOnlyList<WebPageAction> Actions)
{
    /// <summary>An action by id or label, ignoring case; null when the service has none by that name.</summary>
    public WebPageAction? Find(string idOrLabel)
    {
        var t = (idOrLabel ?? "").Trim();
        if (t.Length == 0) return null;
        return Actions.FirstOrDefault(a => a.Id.Equals(t, StringComparison.OrdinalIgnoreCase))
               ?? Actions.FirstOrDefault(a => a.Label.Equals(t, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// YouTube, Vimeo, Google Slides and PowerPoint for the web, made to behave on a show screen:
/// the address that shows the player or the deck alone (FULL FRAME), and the keys and script
/// their pages answer to, so "next", "play" and "present" mean the right thing on each. Pure.
/// </summary>
public static class WebPresets
{
    private const string YouTubeScriptHead =
        "(function(){var p=document.getElementById('movie_player')||document.querySelector('.html5-video-player');if(!p||!p.getPlayerState)return 0;";

    private static readonly IReadOnlyList<WebPageAction> PageActions = new[]
    {
        new WebPageAction("next", "Next", "ArrowRight", Hint: "The right arrow — the next slide of most decks"),
        new WebPageAction("prev", "Previous", "ArrowLeft"),
        new WebPageAction("first", "First", "Home"),
        new WebPageAction("last", "Last", "End"),
        new WebPageAction("down", "Page down", "PageDown"),
        new WebPageAction("up", "Page up", "PageUp"),
        new WebPageAction("enter", "Enter", "Enter"),
        new WebPageAction("exit", "Escape", "Escape"),
        new WebPageAction("play", "Play / pause", "Space", Hint: "The space bar — most players toggle on it"),
        new WebPageAction("mute", "Mute", "m"),
        new WebPageAction("fullscreen", "Full screen", "f"),
    };

    private static readonly IReadOnlyList<WebPageAction> YouTubeActions = new[]
    {
        new WebPageAction("play", "Play / pause", "k", YouTubeScriptHead + "if(p.getPlayerState()===1)p.pauseVideo();else p.playVideo();return 1;})()", "Through the player itself, so it works whether or not the page has focus"),
        new WebPageAction("pause", "Pause", "k", YouTubeScriptHead + "p.pauseVideo();return 1;})()"),
        new WebPageAction("mute", "Mute / unmute", "m", YouTubeScriptHead + "if(p.isMuted())p.unMute();else p.mute();return 1;})()"),
        new WebPageAction("restart", "Restart", "0", YouTubeScriptHead + "p.seekTo(0,true);p.playVideo();return 1;})()"),
        new WebPageAction("forward", "+10 s", "l", YouTubeScriptHead + "p.seekTo(p.getCurrentTime()+10,true);return 1;})()"),
        new WebPageAction("rewind", "−10 s", "j", YouTubeScriptHead + "p.seekTo(Math.max(0,p.getCurrentTime()-10),true);return 1;})()"),
        new WebPageAction("next", "Next video", "Shift+N", YouTubeScriptHead + "if(p.nextVideo)p.nextVideo();return 1;})()", "The next video of a playlist"),
        new WebPageAction("prev", "Previous video", "Shift+P", YouTubeScriptHead + "if(p.previousVideo)p.previousVideo();return 1;})()"),
        new WebPageAction("captions", "Captions", "c"),
        new WebPageAction("fullscreen", "Full screen", "f"),
    };

    private static readonly IReadOnlyList<WebPageAction> VimeoActions = new[]
    {
        new WebPageAction("play", "Play / pause", "Space"),
        new WebPageAction("mute", "Mute", "m"),
        new WebPageAction("forward", "Forward", "ArrowRight"),
        new WebPageAction("rewind", "Back", "ArrowLeft"),
        new WebPageAction("fullscreen", "Full screen", "f"),
    };

    private static readonly IReadOnlyList<WebPageAction> SlidesActions = new[]
    {
        new WebPageAction("next", "Next slide", "ArrowRight"),
        new WebPageAction("prev", "Previous slide", "ArrowLeft"),
        new WebPageAction("first", "First slide", "Home"),
        new WebPageAction("last", "Last slide", "End"),
        new WebPageAction("present", "Present", "Ctrl+Shift+F5", Hint: "Start presenting from the first slide (a deck opened in its editor)"),
        new WebPageAction("exit", "Exit", "Escape", Hint: "Leave present mode"),
        new WebPageAction("black", "Black", "b", Hint: "A black slide — any key brings the deck back"),
        new WebPageAction("white", "White", "w"),
        new WebPageAction("notes", "Speaker notes", "s"),
        new WebPageAction("captions", "Captions", "Ctrl+Shift+c"),
        new WebPageAction("fullscreen", "Full screen", "f"),
    };

    private static readonly IReadOnlyList<WebPageAction> PowerPointActions = new[]
    {
        new WebPageAction("present", "Present", "F5", Hint: "Start the slide show from the first slide"),
        new WebPageAction("resume", "Present from here", "Shift+F5"),
        new WebPageAction("next", "Next slide", "ArrowRight"),
        new WebPageAction("prev", "Previous slide", "ArrowLeft"),
        new WebPageAction("first", "First slide", "Home"),
        new WebPageAction("last", "Last slide", "End"),
        new WebPageAction("black", "Black", "b"),
        new WebPageAction("white", "White", "w"),
        new WebPageAction("exit", "End the show", "Escape"),
    };

    private static readonly WebPreset PagePreset = new(PageService.Page, "Web page", "", PageActions);

    private static readonly WebPreset YouTubePreset = new(PageService.YouTube, "YouTube",
        "FULL FRAME shows the player alone: autoplay, no controls, no related videos. A video whose owner blocks embedding needs its watch link and the area of interest instead.",
        YouTubeActions);

    private static readonly WebPreset VimeoPreset = new(PageService.Vimeo, "Vimeo",
        "FULL FRAME shows the player alone: autoplay, no controls, no title.", VimeoActions);

    private static readonly WebPreset SlidesPreset = new(PageService.GoogleSlides, "Google Slides",
        "FULL FRAME shows the deck alone — a published deck as the embed with its control bar hidden, your own deck in present mode (sign in once with KEYS → PAGE).",
        SlidesActions);

    private static readonly WebPreset PowerPointPreset = new(PageService.PowerPoint, "PowerPoint for the web",
        "Sign in once with KEYS → PAGE, then PRESENT (F5) starts the show; FULL FRAME asks for the embedded view where the link carries an action.",
        PowerPointActions);

    /// <summary>Every action id any service knows — for the docs, Companion's list and a check with no page to ask.</summary>
    public static IReadOnlyList<string> AllActionIds { get; } = new[] { PageActions, YouTubeActions, VimeoActions, SlidesActions, PowerPointActions }
        .SelectMany(a => a).Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static WebPreset For(PageService service) => service switch
    {
        PageService.YouTube => YouTubePreset,
        PageService.Vimeo => VimeoPreset,
        PageService.GoogleSlides => SlidesPreset,
        PageService.PowerPoint => PowerPointPreset,
        _ => PagePreset,
    };

    public static WebPreset For(string url) => For(Detect(url));

    /// <summary>Which service an address belongs to, from its host and path.</summary>
    public static PageService Detect(string url)
    {
        if (!TryUri(url, out var u)) return PageService.Page;
        var host = u.Host.ToLowerInvariant();
        var path = u.AbsolutePath.ToLowerInvariant();
        if (host.EndsWith("youtube.com") || host.EndsWith("youtube-nocookie.com") || host == "youtu.be") return PageService.YouTube;
        if (host.EndsWith("vimeo.com")) return PageService.Vimeo;
        if (host == "docs.google.com" && path.StartsWith("/presentation/")) return PageService.GoogleSlides;
        if (host.EndsWith("onedrive.live.com") || host == "1drv.ms" || host.EndsWith("sharepoint.com") || host.EndsWith("officeapps.live.com")
            || host.EndsWith("office.com") || host.EndsWith("microsoft365.com") || host.EndsWith("powerpoint.com"))
        {
            return PageService.PowerPoint;
        }
        return PageService.Page;
    }

    /// <summary>The address that shows the player or the deck alone; the address unchanged (normalised) when there is nothing to do.</summary>
    public static string FullFrame(string url)
    {
        var s = WebAddress.Normalize(url);
        if (!TryUri(s, out var u)) return s;
        return Detect(s) switch
        {
            PageService.YouTube => YouTubeFullFrame(u) ?? s,
            PageService.Vimeo => VimeoFullFrame(u) ?? s,
            PageService.GoogleSlides => SlidesFullFrame(u) ?? s,
            PageService.PowerPoint => PowerPointFullFrame(u) ?? s,
            _ => s,
        };
    }

    /// <summary>True while FULL FRAME would change the address.</summary>
    public static bool CanFullFrame(string url)
    {
        var s = WebAddress.Normalize(url);
        return s.Length > 0 && !string.Equals(FullFrame(s), s, StringComparison.Ordinal);
    }

    /// <summary>The desk's line under an address: the service and what FULL FRAME does; "" for a page Patterns knows nothing special about.</summary>
    public static string Note(string url)
    {
        var preset = For(url);
        if (preset.Service == PageService.Page) return "";
        if (CanFullFrame(url)) return $"{preset.Name} — {preset.FullFrameNote}";
        return preset.Service switch
        {
            PageService.YouTube or PageService.Vimeo => $"{preset.Name} — the player alone, full frame. PLAY, MUTE and the rest are under PAGE CONTROLS, on the phone and in cues.",
            PageService.GoogleSlides => $"{preset.Name} — the deck alone. NEXT, PREVIOUS, BLACK and the rest are under PAGE CONTROLS, on the phone and in cues; the clicker's keys reach it too while it is on air.",
            _ => $"{preset.Name} — PRESENT (F5) starts the show; NEXT, PREVIOUS, BLACK and the rest are under PAGE CONTROLS, on the phone and in cues.",
        };
    }

    /// <summary>The words for a WebKey value in a summary: an action's label, a key's chord, else the text as written.</summary>
    public static string LabelFor(string value)
    {
        var t = (value ?? "").Trim();
        if (t.Length == 0) return "";
        if (t.Equals("reload", StringComparison.OrdinalIgnoreCase)) return "reload";
        foreach (var preset in new[] { SlidesPreset, PowerPointPreset, YouTubePreset, VimeoPreset, PagePreset })
        {
            if (preset.Find(t) is { } action) return action.Label.ToLowerInvariant();
        }
        var chord = WebKeys.Normalize(t);
        return chord.Length > 0 ? "key " + chord : t;
    }

    /// <summary>A WebKey value is an action some service knows, "reload", or a key chord.</summary>
    public static bool IsActionOrKey(string value)
    {
        var t = (value ?? "").Trim();
        if (t.Length == 0) return false;
        if (t.Equals("reload", StringComparison.OrdinalIgnoreCase)) return true;
        return AllActionIds.Contains(t, StringComparer.OrdinalIgnoreCase) || WebKeys.TryParse(t, out _);
    }

    /// <summary>"50 50", "50,50", "50%, 50%" or "0.5 0.5" as a point in percent of the page.</summary>
    public static bool TryParsePoint(string? value, out double xPct, out double yPct)
    {
        xPct = yPct = 0;
        var parts = (value ?? "").Split(new[] { ' ', ',', ';', '×', 'x' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (!Number(parts[0], out var x) || !Number(parts[1], out var y)) return false;
        // Two fractions read as fractions; anything else is percent.
        if (x is >= 0 and <= 1 && y is >= 0 and <= 1 && (parts[0].Contains('.') || parts[1].Contains('.')))
        {
            x *= 100;
            y *= 100;
        }
        if (x < 0 || x > 100 || y < 0 || y > 100) return false;
        xPct = x;
        yPct = y;
        return true;

        static bool Number(string s, out double v)
            => double.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v);
    }

    /// <summary>The web pages a state shows — the program's pattern and layers, each independent screen's — as mount keys and addresses, once each.</summary>
    public static List<(string Key, string Url)> PagesIn(ShowState state)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string url)
        {
            var normalized = WebAddress.Normalize(url);
            var key = Media.InputKeys.Web(normalized);
            if (key.Length > 0 && seen.Add(key)) list.Add((key, normalized));
        }
        void FromPattern(PatternConfig p)
        {
            if (p.Kind == PatternKind.Media && p.Media.Source == MediaSource.Web) Add(p.Media.WebUrl);
            foreach (var l in new[] { p.Layer1, p.Layer2 })
            {
                if (l.Enabled && l.Source == LayerSource.Web) Add(l.WebUrl);
            }
        }
        FromPattern(state.Pattern);
        foreach (var a in state.Independent) FromPattern(a.Pattern);
        return list;
    }

    /// <summary>Whether a target names a page: its key, its address, its nickname, its host, or a word of its address.</summary>
    public static bool Matches(string key, string url, string nickname, string target)
    {
        var t = (target ?? "").Trim();
        if (t.Length == 0) return false;
        if (t.Equals(key, StringComparison.OrdinalIgnoreCase) || t.Equals(url, StringComparison.OrdinalIgnoreCase)) return true;
        if (WebAddress.Normalize(t).Equals(url, StringComparison.OrdinalIgnoreCase)) return true;
        if (nickname.Length > 0 && nickname.Equals(t, StringComparison.OrdinalIgnoreCase)) return true;
        var host = WebAddress.ShortName(url);
        if (host.Equals(t, StringComparison.OrdinalIgnoreCase) || host.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        return url.Contains(t, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the addresses -------------------------------------------------------------------------

    private static string? YouTubeFullFrame(Uri u)
    {
        var q = Query(u);
        var path = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var host = u.Host.ToLowerInvariant();
        var id = q.GetValueOrDefault("v") ?? "";
        if (id.Length == 0 && host == "youtu.be" && path.Length > 0) id = path[0];
        if (id.Length == 0 && path.Length >= 2 && path[0] is "shorts" or "embed" or "live" or "v") id = path[1];
        var list = q.GetValueOrDefault("list") ?? "";
        if (id == "videoseries") id = "";
        if (id.Length == 0 && list.Length == 0) return null;
        var already = host.EndsWith("youtube-nocookie.com") && path.Length >= 1 && path[0] == "embed" && q.ContainsKey("controls");
        if (already) return u.ToString();

        var sb = new System.Text.StringBuilder("https://www.youtube-nocookie.com/embed/");
        sb.Append(id.Length > 0 ? Uri.EscapeDataString(id) : "videoseries");
        sb.Append("?autoplay=1&controls=0&rel=0&modestbranding=1&playsinline=1&iv_load_policy=3&fs=0");
        if (list.Length > 0) sb.Append("&list=").Append(Uri.EscapeDataString(list));
        var start = Seconds(q.GetValueOrDefault("t") ?? q.GetValueOrDefault("start") ?? "");
        if (start > 0) sb.Append("&start=").Append(start.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string? VimeoFullFrame(Uri u)
    {
        var q = Query(u);
        var path = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var host = u.Host.ToLowerInvariant();
        string id = "", hash = "";
        if (host == "player.vimeo.com")
        {
            if (path.Length >= 2 && path[0] == "video") id = path[1];
            hash = q.GetValueOrDefault("h") ?? "";
            if (id.Length > 0 && q.ContainsKey("controls")) return u.ToString();
        }
        else
        {
            for (var i = 0; i < path.Length; i++)
            {
                if (!path[i].All(char.IsAsciiDigit)) continue;
                id = path[i];
                if (i + 1 < path.Length && Regex.IsMatch(path[i + 1], "^[0-9a-f]{6,}$")) hash = path[i + 1];
                break;
            }
        }
        if (id.Length == 0) return null;
        var address = $"https://player.vimeo.com/video/{id}?autoplay=1&controls=0&title=0&byline=0&portrait=0";
        return hash.Length > 0 ? address + "&h=" + Uri.EscapeDataString(hash) : address;
    }

    private static string? SlidesFullFrame(Uri u)
    {
        var path = u.AbsolutePath;
        var published = Regex.Match(path, "^/presentation/d/e/([^/]+)/(pub|embed)", RegexOptions.IgnoreCase);
        if (published.Success)
        {
            var q = Query(u);
            if (published.Groups[2].Value.Equals("embed", StringComparison.OrdinalIgnoreCase) && q.GetValueOrDefault("rm") == "minimal") return u.ToString();
            return $"https://docs.google.com/presentation/d/e/{published.Groups[1].Value}/embed?start=false&loop=false&delayms=60000&rm=minimal";
        }
        var own = Regex.Match(path, "^/presentation/d/([^/]+)(?:/([^/]*))?", RegexOptions.IgnoreCase);
        if (!own.Success) return null;
        var mode = own.Groups[2].Value.ToLowerInvariant();
        var slide = Regex.Match(u.Fragment + "&" + u.Query, "slide=([^&#]+)", RegexOptions.IgnoreCase);
        var address = $"https://docs.google.com/presentation/d/{own.Groups[1].Value}/present";
        if (slide.Success) address += "?slide=" + slide.Groups[1].Value;
        if (mode == "present" && string.Equals(address, u.ToString().TrimEnd('/'), StringComparison.Ordinal)) return u.ToString();
        return address;
    }

    private static string? PowerPointFullFrame(Uri u)
    {
        if (u.Query.Length == 0) return null;
        var replaced = Regex.Replace(u.Query, "(?<=[?&]action=)(edit|view|default|interactivepreview)(?=&|$)", "embedview", RegexOptions.IgnoreCase);
        if (replaced == u.Query) return null;
        return u.GetLeftPart(UriPartial.Path) + replaced + u.Fragment;
    }

    private static bool TryUri(string url, out Uri uri)
    {
        var s = WebAddress.Normalize(url);
        if (Uri.TryCreate(s, UriKind.Absolute, out var u) && !u.IsFile && u.Host.Length > 0)
        {
            uri = u;
            return true;
        }
        uri = null!;
        return false;
    }

    private static Dictionary<string, string> Query(Uri u)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = u.Query.TrimStart('?');
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            dict[name] = value;
        }
        return dict;
    }

    /// <summary>"90", "90s", "1m30s", "1h2m" → seconds; 0 for nothing.</summary>
    private static int Seconds(string t)
    {
        if (t.Length == 0) return 0;
        if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var plain)) return plain;
        var total = 0;
        foreach (Match m in Regex.Matches(t, "(\\d+)([hms])", RegexOptions.IgnoreCase))
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += m.Groups[2].Value.ToLowerInvariant() switch { "h" => n * 3600, "m" => n * 60, _ => n };
        }
        return total;
    }
}
