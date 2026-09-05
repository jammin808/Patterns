using System.Globalization;

namespace Patterns.Core.Services;

/// <summary>
/// OSC addresses onto the one-line protocol: every address starts /patterns/, the rest names
/// the verb the way the TCP line does, and a number, a name or a switch rides either as the
/// next address segment or as the first argument. Pure — what comes out is a line
/// <see cref="ControlProtocol.Parse"/> reads, so OSC has exactly the TCP protocol's meaning,
/// checks and answers.
/// </summary>
public static class OscMap
{
    public const string Prefix = "/patterns/";

    /// <summary>The addresses, for the docs and the page: what to send, and what it means.</summary>
    public static readonly IReadOnlyList<(string Address, string Means)> Reference = new[]
    {
        ("/patterns/outputs 1|0", "OUTPUTS ON / OFF (also /patterns/outputs/on, /off)"),
        ("/patterns/blackout [1|0]", "BLACKOUT ON / OFF; no argument toggles (also /on, /off, /toggle)"),
        ("/patterns/identify", "IDENTIFY"),
        ("/patterns/look <n|name>", "LOOK n / LOOK name (also /patterns/look/<n>)"),
        ("/patterns/look/index <n>", "LOOK #n — the nth look in the show's order, whatever its name or F-key (also /patterns/look/index/<n>, /patterns/look/bank/<n>)"),
        ("/patterns/next, /patterns/prev", "NEXT / PREV — the clicker list"),
        ("/patterns/screen/<n> [1|0]", "SCREEN n ON / OFF; no argument toggles"),
        ("/patterns/lock/<n> [1|0]", "LOCK n ON / OFF; no argument toggles"),
        ("/patterns/group/<letter> 1|0", "GROUP A ON / OFF — a joined canvas"),
        ("/patterns/audio/play, /patterns/audio/stop", "AUDIO PLAY / STOP — the audio track"),
        ("/patterns/music/play [n|name]", "MUSIC PLAY — break music (Spotify), an entry by number or name"),
        ("/patterns/music/pause, /patterns/music/next", "MUSIC PAUSE / NEXT"),
        ("/patterns/music/volume <level>", "MUSIC VOL: an integer is percent, a float from 0.0 to 1.0 is a fader"),
        ("/patterns/tone 1|0", "TONE ON / OFF"),
        ("/patterns/duck [1|0]", "DUCK ON / OFF; no argument toggles"),
        ("/patterns/stinger <n|name>", "STINGER n / name (also /patterns/stinger/<n>); /patterns/stinger/stop"),
        ("/patterns/vog <n|name>, /patterns/sting <n|name>", "VOG / STING — kind-checked, like the TCP verbs"),
        ("/patterns/lowerthird <n|name> [person]", "LOWERTHIRD n / name, with a library entry when a second argument names one (also /patterns/lt)"),
        ("/patterns/lowerthird/off", "LOWERTHIRD OFF"),
        ("/patterns/lowerthird/preview <n|name> [person]", "LOWERTHIRD PREVIEW — the design (with a library entry) into the preview for a sign-off (also /lowerthird/preview/<n>/<person>)"),
        ("/patterns/lowerthird/preview/off", "LOWERTHIRD PREVIEW OFF — the preview's lower third leaves"),
        ("/patterns/lowerthird/take", "LOWERTHIRD TAKE — the lower third in the preview goes to air"),
        ("/patterns/lowerthird/update", "LOWERTHIRD UPDATE — the design on air replaced by the design as it is now, in place"),
        ("/patterns/person <n|name>", "PERSON — a library entry into the lower third on air (else the show's default design)"),
        ("/patterns/web/key <key|action> [page]", "WEB KEY — a key chord (ArrowRight, Space, Ctrl+Shift+F5) or a page action (next, play, present…) to the web page on air, or to the page a second argument names (also /patterns/web/key/<key>)"),
        ("/patterns/web/next, /prev, /first, /last, /present, /exit, /play, /pause, /mute, /restart, /black, /white… [page]", "WEB <action> — the page actions as addresses of their own"),
        ("/patterns/web/click <x> <y>", "WEB CLICK — a click at a point in percent of the page (also /patterns/web/click/50/50; floats up to 1.0 are fractions)"),
        ("/patterns/web/type <text>", "WEB TYPE — text into the field that has the page's focus"),
        ("/patterns/web/reload [page]", "WEB RELOAD"),
        ("/patterns/web/open <address> [page]", "WEB OPEN — the page's browser sent to another address"),
        ("/patterns/deck/next, /patterns/deck/prev", "DECK NEXT / PREV — the deck (PDF) on air turns a page"),
        ("/patterns/deck/first, /patterns/deck/last", "DECK FIRST / LAST"),
        ("/patterns/deck/page <n>", "DECK PAGE n — the deck on air turns to page n (also /patterns/deck/page/<n>, /patterns/deck <n>)"),
        ("/patterns/section <n|name>", "SECTION — a playlist part"),
        ("/patterns/device/<name> <text>", "DEVICE name text — a line to a device of the Interactive area (also /patterns/device \"name\" \"text\", /patterns/send/…; * is the first device)"),
        ("/patterns/announce <name|words>", "ANNOUNCE — an announcement of the Install page by name, else the words as a free-text announcement (also /patterns/announce/<name>); /patterns/announce/off ends it"),
        ("/patterns/advert <name|n>", "ADVERT — an advert of the Install page plays now (also /patterns/advert/<name>, /patterns/advert/<n>); /patterns/advert/off ends it"),
        ("/patterns/schedule 1|0", "SCHEDULE ON / OFF — the install's clock runs the site, or stops (also /on, /off)"),
        ("/patterns/stream 1|0", "STREAM ON / OFF"),
        ("/patterns/cue/go [id]", "CUE GO — the standby id you last saw, or none"),
        ("/patterns/cue/standby/next, /patterns/cue/standby/prev", "CUE STANDBY NEXT / PREV"),
        ("/patterns/cue/standby <number|name>", "CUE STANDBY — a cue by number or name"),
        ("/patterns/cue/hold 1|0", "CUE HOLD ON / OFF"),
        ("/patterns/cue/arm 1|0", "CUE ARM ON / OFF — only while the Remote page allows remotes to arm"),
        ("/patterns/review [1|0]", "REVIEW ON / OFF — the preview full-frame on every multiview; no argument toggles"),
        ("/patterns/freeze [1|0]", "FREEZE ON / OFF — every output holds its frame; no argument toggles"),
        ("/patterns/fade [seconds]", "FADE — blackout with a fade of that many seconds (none: the show's transition time); /fade/up [seconds] lifts it"),
        ("/patterns/lookback", "LOOKBACK — the look that was on air before the current one, back on air"),
        ("/patterns/stopall", "STOPALL"),
        ("/patterns/ping", "PING — answered with /patterns/pong to the sender"),
    };

    /// <summary>The protocol line for a message, or null when the address is not one Patterns knows.</summary>
    public static string? ToLine(OscMessage m)
    {
        if (!m.Address.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = m.Address[Prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var verb = parts[0].ToLowerInvariant();
        var seg = parts.Length > 1 ? parts[1] : "";
        var seg2 = parts.Length > 2 ? parts[2] : "";
        var seg3 = parts.Length > 3 ? parts[3] : "";
        switch (verb)
        {
            case "outputs": return "OUTPUTS " + Switch(m, seg, "ON", toggles: false);
            case "blackout": return "BLACKOUT " + Switch(m, seg, "TOGGLE", toggles: true);
            case "identify": return "IDENTIFY";
            case "look":
                // /patterns/look/index/3 · /patterns/look/index 3 · /patterns/look/bank/3: the third look in the show's order.
                if (seg.ToLowerInvariant() is "index" or "bank")
                {
                    var index = seg2.Length > 0 ? seg2 : m.Number() is { } n ? ((int)Math.Round(n)).ToString(CultureInfo.InvariantCulture) : m.Text() ?? "";
                    return int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i > 0 ? $"LOOK #{i}" : null;
                }
                return Named("LOOK", m, seg);
            case "next": return "NEXT";
            case "prev": case "back": return "PREV";
            case "screen": return Numbered("SCREEN", seg, m, seg2, "TOGGLE", toggles: true);
            case "lock": return Numbered("LOCK", seg, m, seg2, "TOGGLE", toggles: true);
            case "group":
                if (seg.Length == 0) return null;
                return $"GROUP {seg.ToUpperInvariant()} {Switch(m, seg2, "ON", toggles: false)}";
            case "audio":
                return Sub(m, seg) switch { "play" or "on" => "AUDIO PLAY", "stop" or "off" => "AUDIO STOP", _ => null };
            case "music":
            {
                var what = seg.ToLowerInvariant();
                switch (what)
                {
                    case "play":
                    case "resume":
                    {
                        var pick = seg2.Length > 0 ? seg2 : m.Text() ?? "";
                        return pick.Length == 0 ? "MUSIC PLAY" : "MUSIC PLAY " + pick;
                    }
                    case "pause": case "stop": return "MUSIC PAUSE";
                    case "next": case "skip": return "MUSIC NEXT";
                    case "volume": case "vol": case "level":
                    {
                        var level = Level(m, seg2);
                        return level is null ? null : "MUSIC VOL " + level.Value.ToString(CultureInfo.InvariantCulture);
                    }
                    default:
                        return what.Length > 0 ? "MUSIC PLAY " + seg : null;
                }
            }
            case "tone": return "TONE " + Switch(m, seg, "ON", toggles: false);
            case "duck": return "DUCK " + Switch(m, seg, "TOGGLE", toggles: true);
            case "stinger":
                if (seg.Equals("stop", StringComparison.OrdinalIgnoreCase)) return "STINGER STOP";
                return Named("STINGER", m, seg);
            case "vog":
                if (seg.Equals("stop", StringComparison.OrdinalIgnoreCase)) return "VOG STOP";
                return Named("VOG", m, seg);
            case "sting":
                if (seg.Equals("stop", StringComparison.OrdinalIgnoreCase)) return "STING STOP";
                return Named("STING", m, seg);
            case "lowerthird":
            case "lt":
            {
                if (seg.Equals("off", StringComparison.OrdinalIgnoreCase) || seg.Equals("hide", StringComparison.OrdinalIgnoreCase)) return "LOWERTHIRD OFF";
                if (seg.Equals("take", StringComparison.OrdinalIgnoreCase)) return "LOWERTHIRD TAKE";
                if (seg.Equals("update", StringComparison.OrdinalIgnoreCase)) return "LOWERTHIRD UPDATE";
                if (seg.Equals("preview", StringComparison.OrdinalIgnoreCase) || seg.Equals("pvw", StringComparison.OrdinalIgnoreCase))
                {
                    // /lowerthird/preview/<design>/<person>, /lowerthird/preview <design> [person], /lowerthird/preview/off.
                    var pDesign = seg2.Length > 0 ? seg2 : m.Text() ?? "";
                    if (pDesign.Length == 0) return null;
                    if (pDesign.Equals("off", StringComparison.OrdinalIgnoreCase) || pDesign.Equals("clear", StringComparison.OrdinalIgnoreCase)) return "LOWERTHIRD PREVIEW OFF";
                    var pPerson = seg3.Length > 0 ? seg3 : seg2.Length > 0 ? m.Text() ?? "" : m.Text(1) ?? "";
                    return pPerson.Length == 0 ? "LOWERTHIRD PREVIEW " + pDesign : $"LOWERTHIRD PREVIEW {pDesign} WITH {pPerson}";
                }
                var design = seg.Length > 0 ? seg : m.Text() ?? "";
                if (design.Length == 0) return null;
                if (design.Equals("off", StringComparison.OrdinalIgnoreCase) || design.Equals("hide", StringComparison.OrdinalIgnoreCase)) return "LOWERTHIRD OFF";
                // The person rides the next segment, or the argument after the design — or the first when the design came from the address.
                var person = seg2.Length > 0 ? seg2 : seg.Length > 0 ? m.Text() ?? "" : m.Text(1) ?? "";
                return person.Length == 0 ? "LOWERTHIRD " + design : $"LOWERTHIRD {design} WITH {person}";
            }
            case "person": return Named("PERSON", m, seg);
            // /patterns/web/key <key|action> [page] · /patterns/web/next [page] (any action word) · /patterns/web/click x y ·
            // /patterns/web/type "text" · /patterns/web/reload [page] · /patterns/web/open "address" [page]
            case "web":
            case "page":
            {
                var what = seg.ToLowerInvariant();
                if (what.Length == 0) return null;
                switch (what)
                {
                    case "key":
                    case "press":
                    case "action":
                    {
                        var key = seg2.Length > 0 ? seg2 : m.Text() ?? "";
                        if (key.Length == 0) return null;
                        var page = seg3.Length > 0 ? seg3 : seg2.Length > 0 ? m.Text() ?? "" : m.Text(1) ?? "";
                        return page.Length == 0 ? "WEB KEY " + key : $"WEB KEY {key} ON {page}";
                    }
                    case "click":
                    {
                        var x = seg2.Length > 0 ? seg2 : Coordinate(m, 0);
                        var y = seg3.Length > 0 ? seg3 : Coordinate(m, seg2.Length > 0 ? 0 : 1);
                        return x is null || y is null ? null : $"WEB CLICK {x} {y}";
                    }
                    case "type":
                    {
                        var text = m.Text() ?? "";
                        return text.Length == 0 ? null : "WEB TYPE " + text;
                    }
                    case "reload":
                    case "refresh":
                    {
                        var page = seg2.Length > 0 ? seg2 : m.Text() ?? "";
                        return page.Length == 0 ? "WEB RELOAD" : "WEB RELOAD " + page;
                    }
                    case "open":
                    case "go":
                    case "navigate":
                    {
                        var address = m.Text() ?? "";
                        if (address.Length == 0) return null;
                        var page = m.Text(1) ?? "";
                        return page.Length == 0 ? "WEB OPEN " + address : $"WEB OPEN {address} ON {page}";
                    }
                    default:
                    {
                        var page = seg2.Length > 0 ? seg2 : m.Text() ?? "";
                        return page.Length == 0 ? "WEB KEY " + what.ToUpperInvariant() : $"WEB KEY {what.ToUpperInvariant()} ON {page}";
                    }
                }
            }
            case "section": return Named("SECTION", m, seg);
            // /patterns/deck/next · /prev · /first · /last · /page 5 · /page/5 · /patterns/deck 5
            case "deck":
            case "pdf":
            case "slides":
            {
                var what = seg.Length > 0 ? seg : m.Text() ?? m.Number()?.ToString(CultureInfo.InvariantCulture) ?? "";
                if (what.Equals("page", StringComparison.OrdinalIgnoreCase))
                {
                    what = seg2.Length > 0 ? seg2 : m.Text() ?? m.Number()?.ToString(CultureInfo.InvariantCulture) ?? "";
                    return what.Length == 0 ? null : "DECK PAGE " + what;
                }
                return what.Length == 0 ? null : "DECK " + what.ToUpperInvariant();
            }
            case "stream": return "STREAM " + Switch(m, seg, "ON", toggles: false);
            // /patterns/device/Arduino "RELAY 1" · /patterns/device "Arduino" "RELAY 1" · /patterns/device/Arduino/RELAY 1: a line to a device.
            case "device":
            case "send":
            {
                var name = seg.Length > 0 ? seg : m.Text() ?? "";
                var text = seg.Length > 0
                    ? (seg2.Length > 0 ? string.Join(" ", parts.Skip(2)) + (m.Args.Count > 0 ? " " + string.Join(" ", m.Args.Select(Word)) : "") : string.Join(" ", m.Args.Select(Word)))
                    : string.Join(" ", m.Args.Skip(1).Select(Word));
                text = text.Trim();
                return name.Length == 0 || text.Length == 0 ? null : $"DEVICE {name} {text}";
            }
            case "cue":
            {
                var what = seg.ToLowerInvariant();
                switch (what)
                {
                    case "go": return "CUE GO" + (seg2.Length > 0 ? " " + seg2 : m.Text() is { Length: > 0 } id ? " " + id : "");
                    case "standby":
                    {
                        var which = seg2.Length > 0 ? seg2 : m.Text() ?? "";
                        if (which.Length == 0) return null;
                        return which.ToLowerInvariant() switch
                        {
                            "next" => "CUE STANDBY NEXT",
                            "prev" or "back" => "CUE STANDBY PREV",
                            _ => "CUE STANDBY " + which,
                        };
                    }
                    case "hold": return "CUE HOLD " + Switch(m, seg2, "ON", toggles: false);
                    case "arm": return "CUE ARM " + Switch(m, seg2, "ON", toggles: false);
                    case "list": return "CUE LIST";
                    default: return null;
                }
            }
            // The Install page: /patterns/announce "The store closes in 15 minutes" · /patterns/announce/Closing · /patterns/announce/off ·
            // /patterns/advert "Lunch offer" · /patterns/advert/2 · /patterns/advert/off · /patterns/schedule 1|0 (also /on, /off).
            case "announce":
            case "announcement":
            {
                var what = seg.Length > 0 ? string.Join(" ", parts.Skip(1)) : string.Join(" ", m.Args.Select(Word)).Trim();
                if (what.Length == 0) return null;
                return what.ToLowerInvariant() is "off" or "stop" or "end" ? "ANNOUNCE OFF" : "ANNOUNCE " + what;
            }
            case "advert":
            case "ad":
            {
                var what = seg.Length > 0 ? string.Join(" ", parts.Skip(1)) : m.Text() ?? m.Number()?.ToString(CultureInfo.InvariantCulture) ?? "";
                if (what.Length == 0) return null;
                return what.ToLowerInvariant() is "off" or "stop" or "skip" or "end" ? "ADVERT OFF" : "ADVERT " + what;
            }
            case "schedule": return "SCHEDULE " + Switch(m, seg, "ON", toggles: false);
            case "review": return "REVIEW " + Switch(m, seg, "TOGGLE", toggles: true);
            case "freeze": return "FREEZE " + Switch(m, seg, "TOGGLE", toggles: true);
            // /patterns/fade 2 · /patterns/fade/up 2 · /patterns/fade/down: the seconds as the argument or the next segment.
            case "fade":
            {
                var word = seg.ToLowerInvariant();
                var up = word is "up" or "in";
                var secs = word is "up" or "in" or "down" or "out" or "black" ? seg2 : seg;
                if (secs.Length == 0 && m.Number() is { } n) secs = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return (up ? "FADEUP" : "FADE") + (secs.Length > 0 ? " " + secs : "");
            }
            case "lookback": case "look-back": return "LOOKBACK";
            case "stopall": case "stop-all": return "STOPALL";
            case "ping": return "PING";
            case "status": return "STATUS";
            default: return null;
        }
    }

    /// <summary>An argument as a device would read it: a string as it is, a number in plain digits, a bool as 1 or 0.</summary>
    private static string Word(object? arg) => arg switch
    {
        null => "",
        string s => s,
        bool b => b ? "1" : "0",
        float f => f.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => arg.ToString() ?? "",
    };

    /// <summary>"VERB x" where x is the segment after the verb, else the first argument; null without either.</summary>
    private static string? Named(string verb, OscMessage m, string seg)
    {
        var x = seg.Length > 0 ? seg : m.Text() ?? "";
        return x.Length == 0 ? null : $"{verb} {x}";
    }

    /// <summary>"VERB n SWITCH" for /verb/n [switch].</summary>
    private static string? Numbered(string verb, string seg, OscMessage m, string seg2, string whenMissing, bool toggles)
    {
        if (!int.TryParse(seg, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
        return $"{verb} {n} {Switch(m, seg2, whenMissing, toggles)}";
    }

    /// <summary>The sub-verb: a segment, else the first argument as text, lower-cased.</summary>
    private static string Sub(OscMessage m, string seg) => (seg.Length > 0 ? seg : m.Text() ?? "").ToLowerInvariant();

    /// <summary>
    /// ON / OFF / TOGGLE from a segment ("on", "off", "toggle") or the first argument (a number:
    /// above a half is on; a bool; the words), else what a bare address means for this verb.
    /// </summary>
    private static string Switch(OscMessage m, string seg, string whenMissing, bool toggles)
    {
        var word = seg.Length > 0 ? seg : m.Text() ?? "";
        switch (word.ToLowerInvariant())
        {
            case "on": case "true": case "yes": return "ON";
            case "off": case "false": case "no": return "OFF";
            case "toggle": return toggles ? "TOGGLE" : whenMissing;
            case "": return whenMissing;
        }
        var number = m.Number();
        if (number is { } v) return v > 0.5 ? "ON" : "OFF";
        return whenMissing;
    }

    /// <summary>A coordinate in percent from an argument: an integer as it is, a float up to 1.0 as a fraction (× 100); null without one.</summary>
    private static string? Coordinate(OscMessage m, int index)
    {
        if (index >= m.Args.Count) return null;
        var value = m.Args[index] switch
        {
            int i => i,
            long l => (double)l,
            float f => f is >= 0 and <= 1 ? f * 100 : f,
            double d => d is >= 0 and <= 1 ? d * 100 : d,
            string s => double.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN,
            _ => double.NaN,
        };
        return double.IsNaN(value) ? null : Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A level in percent: an integer as it is, a float between 0.0 and 1.0 as a fader (× 100), a larger float rounded.</summary>
    private static int? Level(OscMessage m, string seg)
    {
        if (seg.Length > 0)
        {
            return int.TryParse(seg, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        if (m.Args.Count == 0) return null;
        return m.Args[0] switch
        {
            int i => i,
            long l => (int)Math.Clamp(l, 0, 100),
            float f => f <= 1.0f && f >= 0 ? (int)Math.Round(f * 100) : (int)Math.Round(f),
            double d => d <= 1.0 && d >= 0 ? (int)Math.Round(d * 100) : (int)Math.Round(d),
            string s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null,
            _ => null,
        };
    }
}
