namespace Patterns.Core.Services;

public enum RemoteCommandKind
{
    Unknown,
    OutputsOn,   // "OUTPUTS ON" (and the frozen alias "GO" — outputs, never a cue)
    OutputsOff,  // "OUTPUTS OFF" (alias "STOP")
    BlackoutOn,
    BlackoutOff,
    BlackoutToggle,
    Identify,
    Look,        // by slot (IntArg 1-12) or name (TextArg)
    Next,        // presenter forward
    Prev,        // presenter back
    ScreenOn,    // IntArg = screen number (1-based, arrangement order)
    ScreenOff,
    ScreenToggle,
    GroupOn,     // TextArg = canvas letter A/B/…
    GroupOff,
    /// <summary>"AUDIO PLAY [n|name]" — the audio playlist plays: a track by its place (IntArg) or its name / file (TextArg); empty resumes or starts the list.</summary>
    AudioPlay,
    AudioStop,
    /// <summary>"AUDIO NEXT" (SKIP) / "AUDIO PREV" (BACK) — the audio playlist steps a track.</summary>
    AudioNext,
    AudioPrev,
    /// <summary>"AUDIO VOL 80" — the audio playlist's volume, 0–125 (TextArg carries the number, like MUSIC VOL).</summary>
    AudioVolume,
    ToneOn,
    ToneOff,
    Stinger,     // by number (IntArg 1-based) or name (TextArg)
    StingerStop,
    PlaylistSection, // by number (IntArg 1-based) or name (TextArg)
    StreamOn,
    StreamOff,
    Status,
    Ping,
    /// <summary>"CUE GO [id]" — GO on the caller's stack; TextArg = the standby id the sender saw ("" skips the fence).</summary>
    CueGo,
    CueStandbyNext,
    CueStandbyPrev,
    /// <summary>"CUE STANDBY 03.020" or a cue name — TextArg.</summary>
    CueStandby,
    CueHoldOn,
    CueHoldOff,
    /// <summary>Accepted only when the Remote tab allows remotes to arm.</summary>
    CueArmOn,
    CueArmOff,
    CueList,
    /// <summary>"STOPALL" as one token: an older build parses "STOP ALL" as STOP and closes the outputs.</summary>
    StopAll,
    /// <summary>"HELLO FOH deck" — the connection names itself; history reads "GO from FOH deck".</summary>
    Hello,
    /// <summary>"MUSIC PLAY [n|name]" — break music; empty arg resumes what is loaded.</summary>
    MusicPlay,
    MusicPause,
    MusicNext,
    /// <summary>"MUSIC VOL 40" — 0–100; the level rides TextArg (IntArg == 0 is the "no number" sentinel).</summary>
    MusicVolume,
    /// <summary>"VOG n" / "VOG name" — fires it only if it really is a VOG.</summary>
    Vog,
    /// <summary>"STING n" / "STING name" — fires it only if it really is a stinger.</summary>
    Sting,
    /// <summary>"DUCK ON" / "DUCK OFF" / "DUCK TOGGLE" (bare "DUCK" toggles) — the live duck for an announcement from the room.</summary>
    DuckOn,
    DuckOff,
    DuckToggle,
    /// <summary>"LOWERTHIRD n" / "LOWERTHIRD name" (alias "LT") — the design goes on air; by number (IntArg, Lower thirds page order) or name (TextArg).</summary>
    LowerThirdShow,
    /// <summary>"LOWERTHIRD OFF" (or "HIDE") — the one on air leaves.</summary>
    LowerThirdHide,
    /// <summary>"LOCK n ON" / "LOCK n OFF" / "LOCK n TOGGLE" (bare "LOCK n" toggles) — screen n keeps its picture through looks, cues and TAKE, or follows them again.</summary>
    ScreenLock,
    ScreenUnlock,
    ScreenLockToggle,
    /// <summary>
    /// "LT &lt;design&gt; WITH &lt;person&gt;" / "PERSON &lt;n|name&gt;" — a library entry (Extra: number, Lower thirds
    /// page order, or name) fills the design (TextArg: number or name; "" = the one on air, else the first) and it goes on air.
    /// </summary>
    LowerThirdPerson,
    /// <summary>"REVIEW ON" / "REVIEW OFF" / "REVIEW TOGGLE" (bare "REVIEW" toggles) — the preview fills every multiview, or the tiles come back.</summary>
    ReviewOn,
    ReviewOff,
    ReviewToggle,
    /// <summary>"FREEZE ON" / "FREEZE OFF" / "FREEZE TOGGLE" (bare "FREEZE" toggles) — every output holds its frame, or moves again.</summary>
    FreezeOn,
    FreezeOff,
    FreezeToggle,
    /// <summary>"FADE [seconds]" — blackout with a fade of that many seconds (IntArg: milliseconds; 0 = the show's transition time).</summary>
    FadeToBlack,
    /// <summary>"FADE UP [seconds]" / "FADEUP [seconds]" — the blackout lifted with a fade (IntArg: milliseconds; 0 = the show's).</summary>
    FadeUp,
    /// <summary>"LOOKBACK [cut|ms]" — the look that was on air before the current one, back on air.</summary>
    LookBack,
    /// <summary>
    /// "LT PREVIEW &lt;n|name&gt; [WITH &lt;person&gt;]" / "LT PREVIEW WITH &lt;person&gt;" (alias PVW) — the design (IntArg or
    /// TextArg; "" = the one in the preview, else on air, else the show's default), with a library entry (Extra), into the preview.
    /// </summary>
    LowerThirdPreview,
    /// <summary>"LT PREVIEW OFF" (or CLEAR) — the preview's lower third leaves.</summary>
    LowerThirdPreviewOff,
    /// <summary>"LT TAKE" — the lower third in the preview goes to air and the preview clears.</summary>
    LowerThirdTake,
    /// <summary>"LT UPDATE" — the design on air is replaced by the design as it is now, in place.</summary>
    LowerThirdUpdate,
    /// <summary>
    /// "WEB KEY &lt;key|action&gt; [ON &lt;page&gt;]" and "WEB NEXT" / "WEB PLAY" / "WEB PRESENT"… — a key chord or a page
    /// action (TextArg) to the web page on air, or to the page Extra names (a nickname, an address or a word of it). PAGE is an alias of WEB.
    /// </summary>
    WebKey,
    /// <summary>"WEB CLICK &lt;x&gt; &lt;y&gt; [ON &lt;page&gt;]" — a click at a point in percent of the page (TextArg = "x y").</summary>
    WebClick,
    /// <summary>"WEB TYPE &lt;text&gt;" — the text (TextArg) typed into the field that has the page's focus.</summary>
    WebType,
    /// <summary>"WEB RELOAD [&lt;page&gt;]" — the page reloaded (Extra names it).</summary>
    WebReload,
    /// <summary>"WEB OPEN &lt;address&gt; [ON &lt;page&gt;]" — the page's browser sent to another address (TextArg).</summary>
    WebOpen,
    /// <summary>"DECK NEXT" — the deck on air turns to its next page (PDF and SLIDES are aliases of DECK).</summary>
    DeckNext,
    /// <summary>"DECK PREV" (or BACK) — the deck on air turns back a page.</summary>
    DeckPrev,
    /// <summary>"DECK PAGE &lt;n&gt;" / "DECK &lt;n&gt;" / "DECK FIRST" / "DECK LAST" — the deck on air turns to a page (IntArg, or TextArg first / last).</summary>
    DeckPage,
    /// <summary>"DEVICE &lt;name|*&gt; &lt;text&gt;" (alias SEND) — a line (TextArg) to a device of the Interactive area (Extra: its name, or * for the first).</summary>
    DeviceSend,
    /// <summary>"ANNOUNCE &lt;name or words&gt;" — an announcement of the Install page by name, else the words as a free-text announcement (TextArg).</summary>
    Announce,
    /// <summary>"ANNOUNCE OFF" (STOP / END) — the announcement on ends now.</summary>
    AnnounceOff,
    /// <summary>"ADVERT &lt;name|n&gt;" (alias AD) — an advert of the Install page plays now (TextArg or IntArg).</summary>
    AdvertPlay,
    /// <summary>"ADVERT OFF" (STOP / SKIP / END) — the advert on ends now.</summary>
    AdvertOff,
    /// <summary>"SCHEDULE ON" — the install's schedule runs.</summary>
    ScheduleOn,
    /// <summary>"SCHEDULE OFF" — it stops; what is on stays.</summary>
    ScheduleOff,
    /// <summary>"UPDATE APPLY &lt;passcode&gt;" — the staged update applied by the watchdog (TextArg: the admin passcode).</summary>
    UpdateApply,
    /// <summary>"RESTART &lt;passcode&gt;" — the app restarted under the watchdog with the show put back (TextArg: the admin passcode).</summary>
    Restart,
    /// <summary>"SCREEN &lt;n&gt; LOOK &lt;name&gt;" — the look's picture (TextArg) on screen n (IntArg) alone, as its own pattern.</summary>
    ScreenLook,
    /// <summary>"SCREEN &lt;n&gt; PROGRAM" (PGM / FOLLOW) — screen n shows the program again.</summary>
    ScreenProgram,
    /// <summary>"VIDEO END [seconds]" (VT / CLIP are aliases; LAST / OUT too) — the clip on air jumps to its last seconds (IntArg: milliseconds; 0 = ten seconds).</summary>
    VideoToEnd,
    /// <summary>"VIDEO RESTART" (START / TOP / REWIND) — the clip on air plays again from its start.</summary>
    VideoRestart,
}

/// <summary>A parsed remote command (TCP line, HTTP /api/cmd, or the Companion module); Extra is a second text argument, rarely used.</summary>
public readonly record struct RemoteCommand(RemoteCommandKind Kind, int IntArg, string TextArg, string Extra = "");

/// <summary>
/// The text command protocol shared by the TCP port (Bitfocus Companion generic TCP and the
/// Patterns Companion module) and the web remote. One command per line; responses are
/// "OK", "OK &lt;json&gt;" or "ERR &lt;reason&gt;". Pure parsing — unit tested.
/// </summary>
public static class ControlProtocol
{
    /// <summary>"2", "2.5", "0", "1500ms", "" (the show's own time, 0): seconds into milliseconds; false for words.</summary>
    public static bool TryParseSeconds(string text, out int ms)
    {
        ms = 0;
        var t = text.Trim();
        if (t.Length == 0) return true;
        var inMs = t.EndsWith("ms", StringComparison.OrdinalIgnoreCase);
        if (inMs) t = t[..^2].Trim();
        else if (t.EndsWith("s", StringComparison.OrdinalIgnoreCase)) t = t[..^1].Trim();
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0 || v > 600_000) return false;
        ms = (int)Math.Round(inMs ? v : v * 1000);
        return true;
    }

    public static RemoteCommand Parse(string line)
    {
        var s = line.Trim();
        if (s.Length == 0) return new RemoteCommand(RemoteCommandKind.Unknown, 0, "");
        var parts = s.Split(' ', 2, StringSplitOptions.TrimEntries);
        var verb = parts[0].ToUpperInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";

        switch (verb)
        {
            // GO and STOP stay accepted for the outputs so existing Generic-TCP buttons keep
            // working; new integrations should send OUTPUTS ON / OFF. Bare GO never fires a cue.
            case "GO": return new(RemoteCommandKind.OutputsOn, 0, "");
            case "STOP": return new(RemoteCommandKind.OutputsOff, 0, "");
            case "OUTPUTS":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => new(RemoteCommandKind.OutputsOn, 0, ""),
                    "OFF" => new(RemoteCommandKind.OutputsOff, 0, ""),
                    _ => new(RemoteCommandKind.Unknown, 0, s),
                };
            case "IDENTIFY": return new(RemoteCommandKind.Identify, 0, "");
            case "NEXT": return new(RemoteCommandKind.Next, 0, "");
            case "PREV": case "BACK": return new(RemoteCommandKind.Prev, 0, "");
            case "STATUS": return new(RemoteCommandKind.Status, 0, "");
            case "PING": return new(RemoteCommandKind.Ping, 0, "");
            case "STOPALL": return new(RemoteCommandKind.StopAll, 0, "");
            case "HELLO": return arg.Length == 0 ? new(RemoteCommandKind.Unknown, 0, s) : new(RemoteCommandKind.Hello, 0, arg);

            case "CUE":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                var what = sub[0].ToUpperInvariant();
                var rest = sub.Length > 1 ? sub[1] : "";
                switch (what)
                {
                    case "GO": return new(RemoteCommandKind.CueGo, 0, rest);
                    case "STANDBY":
                        return rest.ToUpperInvariant() switch
                        {
                            "" => new(RemoteCommandKind.Unknown, 0, s),
                            "NEXT" => new(RemoteCommandKind.CueStandbyNext, 0, ""),
                            "PREV" or "BACK" => new(RemoteCommandKind.CueStandbyPrev, 0, ""),
                            _ => new(RemoteCommandKind.CueStandby, 0, rest),
                        };
                    case "HOLD":
                        return rest.ToUpperInvariant() switch
                        {
                            "ON" => new(RemoteCommandKind.CueHoldOn, 0, ""),
                            "OFF" => new(RemoteCommandKind.CueHoldOff, 0, ""),
                            _ => new(RemoteCommandKind.Unknown, 0, s),
                        };
                    case "ARM":
                        return rest.ToUpperInvariant() switch
                        {
                            "ON" => new(RemoteCommandKind.CueArmOn, 0, ""),
                            "OFF" => new(RemoteCommandKind.CueArmOff, 0, ""),
                            _ => new(RemoteCommandKind.Unknown, 0, s),
                        };
                    case "LIST": return new(RemoteCommandKind.CueList, 0, "");
                    default: return new(RemoteCommandKind.Unknown, 0, s);
                }
            }

            // MUSIC is canonical; SPOTIFY is a frozen alias for the same reason GO/STOP are aliases
            // for OUTPUTS ON/OFF — a saved button must keep working.
            case "MUSIC":
            case "SPOTIFY":
            {
                if (arg.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                var what = sub[0].ToUpperInvariant();
                var rest = sub.Length > 1 ? sub[1] : "";
                switch (what)
                {
                    case "PLAY":
                    case "RESUME":
                        if (rest.Length == 0) return new(RemoteCommandKind.MusicPlay, 0, "");
                        return int.TryParse(rest, out var pick)
                            ? new(RemoteCommandKind.MusicPlay, pick, "")
                            : new(RemoteCommandKind.MusicPlay, 0, rest);
                    case "PAUSE":
                    case "STOP":
                        return new(RemoteCommandKind.MusicPause, 0, "");
                    case "NEXT":
                    case "SKIP":
                        return new(RemoteCommandKind.MusicNext, 0, "");
                    case "VOL":
                    case "VOLUME":
                        // The level rides TextArg, not IntArg: IntArg == 0 is the "no number"
                        // sentinel CommandRouter's byNumberOrName relies on, and MUSIC VOL 0 is a
                        // real request. The range is checked by the executor so the operator reads
                        // a sentence, not "unknown command".
                        return rest.Length == 0
                            ? new(RemoteCommandKind.Unknown, 0, s)
                            : new(RemoteCommandKind.MusicVolume, 0, rest);
                    default:
                        // "MUSIC 3" / "MUSIC Interval bed" — by number or name, like STINGER.
                        return int.TryParse(what, out var n)
                            ? new(RemoteCommandKind.MusicPlay, n, "")
                            : new(RemoteCommandKind.MusicPlay, 0, arg);
                }
            }

            case "BLACKOUT":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => new(RemoteCommandKind.BlackoutOn, 0, ""),
                    "OFF" => new(RemoteCommandKind.BlackoutOff, 0, ""),
                    _ => new(RemoteCommandKind.BlackoutToggle, 0, ""),
                };

            case "LOOK":
                if (arg.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                // "LOOK #3": the third look in the show's order (Extra "#") — a bank key that follows the
                // list as looks are made, whatever their names and F-keys.
                if (arg.StartsWith('#') && int.TryParse(arg[1..], out var index) && index > 0)
                {
                    return new(RemoteCommandKind.Look, index, "", "#");
                }
                return int.TryParse(arg, out var slot)
                    ? new(RemoteCommandKind.Look, slot, "")
                    : new(RemoteCommandKind.Look, 0, arg);

            case "SCREEN":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (sub.Length < 1 || !int.TryParse(sub[0], out var n)) return new(RemoteCommandKind.Unknown, 0, s);
                var rest = sub.Length > 1 ? sub[1] : "";
                // "SCREEN 2 LOOK Sponsor": the look on that screen alone; "SCREEN 2 PROGRAM": the program again.
                if (rest.StartsWith("LOOK ", StringComparison.OrdinalIgnoreCase))
                {
                    var look = rest[5..].Trim();
                    return look.Length == 0 ? new(RemoteCommandKind.Unknown, 0, s) : new(RemoteCommandKind.ScreenLook, n, look);
                }
                var action = rest.ToUpperInvariant();
                return action switch
                {
                    "ON" => new(RemoteCommandKind.ScreenOn, n, ""),
                    "OFF" => new(RemoteCommandKind.ScreenOff, n, ""),
                    "PROGRAM" or "PGM" or "FOLLOW" => new(RemoteCommandKind.ScreenProgram, n, ""),
                    "LOOK" => new(RemoteCommandKind.Unknown, 0, s),
                    _ => new(RemoteCommandKind.ScreenToggle, n, ""),
                };
            }

            case "LOCK":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (sub.Length < 1 || !int.TryParse(sub[0], out var n)) return new(RemoteCommandKind.Unknown, 0, s);
                var action = sub.Length > 1 ? sub[1].ToUpperInvariant() : "TOGGLE";
                return action switch
                {
                    "ON" => new(RemoteCommandKind.ScreenLock, n, ""),
                    "OFF" => new(RemoteCommandKind.ScreenUnlock, n, ""),
                    _ => new(RemoteCommandKind.ScreenLockToggle, n, ""),
                };
            }

            case "GROUP":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (sub.Length < 2 || sub[0].Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                var letter = sub[0].ToUpperInvariant();
                return sub[1].ToUpperInvariant() switch
                {
                    "ON" => new(RemoteCommandKind.GroupOn, 0, letter),
                    "OFF" => new(RemoteCommandKind.GroupOff, 0, letter),
                    _ => new(RemoteCommandKind.Unknown, 0, s),
                };
            }

            // The audio playlist: "AUDIO PLAY", "AUDIO PLAY 3", "AUDIO PLAY Walk-in", "AUDIO NEXT", "AUDIO PREV", "AUDIO VOL 80", "AUDIO STOP".
            case "AUDIO":
            case "TRACK":
            {
                var words = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var what = words.Length > 0 ? words[0].ToUpperInvariant() : "";
                var rest = words.Length > 1 ? words[1] : "";
                switch (what)
                {
                    case "PLAY":
                    case "RESUME":
                    case "START":
                        if (rest.Length == 0) return new(RemoteCommandKind.AudioPlay, 0, "");
                        return int.TryParse(rest, out var track) && track > 0
                            ? new(RemoteCommandKind.AudioPlay, track, "")
                            : new(RemoteCommandKind.AudioPlay, 0, rest);
                    case "STOP":
                    case "OFF":
                        return new(RemoteCommandKind.AudioStop, 0, "");
                    case "NEXT":
                    case "SKIP":
                        return new(RemoteCommandKind.AudioNext, 0, "");
                    case "PREV":
                    case "PREVIOUS":
                    case "BACK":
                        return new(RemoteCommandKind.AudioPrev, 0, "");
                    case "VOL":
                    case "VOLUME":
                    case "LEVEL":
                        return int.TryParse(rest, out var level) && level is >= 0 and <= 125
                            ? new(RemoteCommandKind.AudioVolume, 0, rest)
                            : new(RemoteCommandKind.Unknown, 0, s);
                    default:
                        return new(RemoteCommandKind.Unknown, 0, s);
                }
            }

            case "TONE":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => new(RemoteCommandKind.ToneOn, 0, ""),
                    "OFF" => new(RemoteCommandKind.ToneOff, 0, ""),
                    _ => new(RemoteCommandKind.Unknown, 0, s),
                };

            // The live duck is a latch like BLACKOUT: ON / OFF are explicit, anything else toggles.
            case "DUCK":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => new(RemoteCommandKind.DuckOn, 0, ""),
                    "OFF" => new(RemoteCommandKind.DuckOff, 0, ""),
                    _ => new(RemoteCommandKind.DuckToggle, 0, ""),
                };

            // A lower third by number (Lower thirds page order) or name; OFF / HIDE takes the one on air off;
            // "LT <design> WITH <person>" fills it from the library first.
            case "LOWERTHIRD":
            case "LT":
            {
                if (arg.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                if (arg.Equals("OFF", StringComparison.OrdinalIgnoreCase) || arg.Equals("HIDE", StringComparison.OrdinalIgnoreCase))
                {
                    return new(RemoteCommandKind.LowerThirdHide, 0, "");
                }
                if (arg.Equals("TAKE", StringComparison.OrdinalIgnoreCase)) return new(RemoteCommandKind.LowerThirdTake, 0, "");
                if (arg.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)) return new(RemoteCommandKind.LowerThirdUpdate, 0, "");
                // The sign-off flow: "LT PREVIEW <design> [WITH <person>]", "LT PREVIEW WITH <person>", "LT PREVIEW OFF".
                var preview = arg.StartsWith("PREVIEW", StringComparison.OrdinalIgnoreCase) ? 7
                            : arg.StartsWith("PVW", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
                if (preview > 0 && (arg.Length == preview || arg[preview] == ' '))
                {
                    var rest = arg[preview..].Trim();
                    if (rest.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                    if (rest.Equals("OFF", StringComparison.OrdinalIgnoreCase) || rest.Equals("CLEAR", StringComparison.OrdinalIgnoreCase) || rest.Equals("HIDE", StringComparison.OrdinalIgnoreCase))
                    {
                        return new(RemoteCommandKind.LowerThirdPreviewOff, 0, "");
                    }
                    if (rest.Equals("WITH", StringComparison.OrdinalIgnoreCase)) return new(RemoteCommandKind.Unknown, 0, s);
                    if (rest.StartsWith("WITH ", StringComparison.OrdinalIgnoreCase))
                    {
                        var who = rest[5..].Trim();
                        return who.Length == 0 ? new(RemoteCommandKind.Unknown, 0, s) : new(RemoteCommandKind.LowerThirdPreview, 0, "", who);
                    }
                    if (rest.EndsWith(" WITH", StringComparison.OrdinalIgnoreCase)) return new(RemoteCommandKind.Unknown, 0, s);
                    var pw = rest.IndexOf(" WITH ", StringComparison.OrdinalIgnoreCase);
                    var pDesign = pw >= 0 ? rest[..pw].Trim() : rest;
                    var pPerson = pw >= 0 ? rest[(pw + 6)..].Trim() : "";
                    if (pDesign.Length == 0 || (pw >= 0 && pPerson.Length == 0)) return new(RemoteCommandKind.Unknown, 0, s);
                    return int.TryParse(pDesign, out var pn)
                        ? new(RemoteCommandKind.LowerThirdPreview, pn, "", pPerson)
                        : new(RemoteCommandKind.LowerThirdPreview, 0, pDesign, pPerson);
                }
                if (arg.EndsWith(" WITH", StringComparison.OrdinalIgnoreCase)) return new(RemoteCommandKind.Unknown, 0, s);
                var with = arg.IndexOf(" WITH ", StringComparison.OrdinalIgnoreCase);
                if (with >= 0)
                {
                    var design = arg[..with].Trim();
                    var person = arg[(with + 6)..].Trim();
                    return design.Length == 0 || person.Length == 0
                        ? new(RemoteCommandKind.Unknown, 0, s)
                        : new(RemoteCommandKind.LowerThirdPerson, 0, design, person);
                }
                return int.TryParse(arg, out var lower)
                    ? new(RemoteCommandKind.LowerThirdShow, lower, "")
                    : new(RemoteCommandKind.LowerThirdShow, 0, arg);
            }

            // The web page on air — or the one "ON <page>" names: a key chord or a page action, a click, typed
            // text, a reload, another address. "WEB NEXT" is "WEB KEY NEXT"; PAGE is an alias of WEB.
            case "WEB":
            case "PAGE":
            {
                if (arg.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                var what = sub[0].ToUpperInvariant();
                var rest = sub.Length > 1 ? sub[1] : "";
                switch (what)
                {
                    case "KEY":
                    case "PRESS":
                    case "ACTION":
                    {
                        var (value, page) = SplitOn(rest);
                        return value.Length == 0 ? new(RemoteCommandKind.Unknown, 0, s) : new(RemoteCommandKind.WebKey, 0, value, page);
                    }
                    case "CLICK":
                    {
                        var (value, page) = SplitOn(rest);
                        return value.Length == 0 ? new(RemoteCommandKind.Unknown, 0, s) : new(RemoteCommandKind.WebClick, 0, value, page);
                    }
                    case "TYPE":
                        // The text is the text, spaces and all — no "ON <page>" here: typing goes to the page on air.
                        return rest.Length == 0 ? new(RemoteCommandKind.Unknown, 0, s) : new(RemoteCommandKind.WebType, 0, rest, "");
                    case "RELOAD":
                    case "REFRESH":
                    {
                        var page = rest.StartsWith("ON ", StringComparison.OrdinalIgnoreCase) ? rest[3..].Trim() : rest;
                        return new(RemoteCommandKind.WebReload, 0, "", page);
                    }
                    case "OPEN":
                    case "GO":
                    case "NAVIGATE":
                    {
                        var (value, page) = SplitOn(rest);
                        return value.Length == 0 ? new(RemoteCommandKind.Unknown, 0, s) : new(RemoteCommandKind.WebOpen, 0, value, page);
                    }
                    default:
                    {
                        // "WEB NEXT", "WEB PLAY ON youtube", "WEB Ctrl+Shift+F5": an action word or a key as the verb.
                        var (value, page) = SplitOn(arg);
                        return value.Length == 0 ? new(RemoteCommandKind.Unknown, 0, s) : new(RemoteCommandKind.WebKey, 0, value, page);
                    }
                }
            }

            // The deck on air: "DECK NEXT", "DECK PREV", "DECK FIRST", "DECK LAST", "DECK PAGE 5", "DECK 5". PDF / SLIDES are aliases.
            case "DECK":
            case "PDF":
            case "SLIDES":
            {
                if (arg.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                var word = arg.StartsWith("PAGE ", StringComparison.OrdinalIgnoreCase) ? arg[5..].Trim() : arg;
                if (!Decks.TryParsePage(word, out var page, out var which)) return new(RemoteCommandKind.Unknown, 0, s);
                return which switch
                {
                    "next" => new(RemoteCommandKind.DeckNext, 0, ""),
                    "prev" => new(RemoteCommandKind.DeckPrev, 0, ""),
                    "first" or "last" => new(RemoteCommandKind.DeckPage, 0, which),
                    _ => new(RemoteCommandKind.DeckPage, page, ""),
                };
            }

            // The clip on air: "VIDEO END", "VIDEO END 5", "VT END", "VIDEO RESTART", "CLIP START" — the rehearsal's skip and the top.
            case "VIDEO":
            case "VT":
            case "CLIP":
            {
                if (arg.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                var words = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                switch (words[0].ToUpperInvariant())
                {
                    case "END":
                    case "LAST":
                    case "OUT":
                    case "TAIL":
                    {
                        var rest = words.Length > 1 ? words[1] : "";
                        if (!TryParseSeconds(rest, out var ms)) return new(RemoteCommandKind.Unknown, 0, s);
                        return new(RemoteCommandKind.VideoToEnd, ms, "");
                    }
                    case "RESTART":
                    case "START":
                    case "TOP":
                    case "BEGIN":
                    case "REWIND":
                        return new(RemoteCommandKind.VideoRestart, 0, "");
                    default:
                        return new(RemoteCommandKind.Unknown, 0, s);
                }
            }

            // A line to a device of the Interactive area: "DEVICE Arduino RELAY 1", "DEVICE * PING", "SEND pi SHOW 3".
            case "DEVICE":
            case "SEND":
            {
                var split = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (split.Length < 2) return new(RemoteCommandKind.Unknown, 0, s);
                return new(RemoteCommandKind.DeviceSend, 0, split[1], split[0]);
            }

            // The Install page: an announcement by name or as words, an advert by name, the schedule's switch.
            case "ANNOUNCE":
            case "ANNOUNCEMENT":
            {
                if (arg.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                return arg.ToUpperInvariant() is "OFF" or "STOP" or "END"
                    ? new(RemoteCommandKind.AnnounceOff, 0, "")
                    : new(RemoteCommandKind.Announce, 0, arg);
            }
            case "ADVERT":
            case "AD":
            {
                if (arg.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                if (arg.ToUpperInvariant() is "OFF" or "STOP" or "SKIP" or "END") return new(RemoteCommandKind.AdvertOff, 0, "");
                return int.TryParse(arg, out var advert)
                    ? new(RemoteCommandKind.AdvertPlay, advert, "")
                    : new(RemoteCommandKind.AdvertPlay, 0, arg);
            }
            case "SCHEDULE":
                return arg.ToUpperInvariant() switch
                {
                    "ON" or "START" or "RUN" => new(RemoteCommandKind.ScheduleOn, 0, ""),
                    "OFF" or "STOP" or "HOLD" => new(RemoteCommandKind.ScheduleOff, 0, ""),
                    _ => new(RemoteCommandKind.Unknown, 0, s),
                };

            // Remote administration: the passcode rides the line, the gate decides.
            case "UPDATE":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (sub[0].ToUpperInvariant() != "APPLY") return new(RemoteCommandKind.Unknown, 0, s);
                return new(RemoteCommandKind.UpdateApply, 0, sub.Length > 1 ? sub[1] : "");
            }
            case "RESTART":
                return new(RemoteCommandKind.Restart, 0, arg);

            // The review latch: the preview full-frame on every multiview. ON / OFF explicit, anything else toggles.
            case "REVIEW":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => new(RemoteCommandKind.ReviewOn, 0, ""),
                    "OFF" => new(RemoteCommandKind.ReviewOff, 0, ""),
                    _ => new(RemoteCommandKind.ReviewToggle, 0, ""),
                };

            case "FREEZE":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => new(RemoteCommandKind.FreezeOn, 0, ""),
                    "OFF" => new(RemoteCommandKind.FreezeOff, 0, ""),
                    _ => new(RemoteCommandKind.FreezeToggle, 0, ""),
                };

            // "FADE", "FADE 2", "FADE 2.5", "FADE UP", "FADE UP 3", "FADEUP 3", "FADE DOWN 1": seconds, or the show's own time.
            case "FADE":
            case "FADEUP":
            case "FADEDOWN":
            {
                var up = verb == "FADEUP";
                var rest = arg;
                if (verb == "FADE" && rest.Length > 0)
                {
                    var words = rest.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    var word = words[0].ToUpperInvariant();
                    if (word is "UP" or "IN") { up = true; rest = words.Length > 1 ? words[1] : ""; }
                    else if (word is "DOWN" or "OUT" or "BLACK") { rest = words.Length > 1 ? words[1] : ""; }
                }
                if (!TryParseSeconds(rest, out var ms)) return new(RemoteCommandKind.Unknown, 0, s);
                return new(up ? RemoteCommandKind.FadeUp : RemoteCommandKind.FadeToBlack, ms, "");
            }

            case "LOOKBACK":
                return new(RemoteCommandKind.LookBack, 0, arg);

            // A person from the library into the lower third on air (else the first design), and on air.
            case "PERSON":
                return arg.Length == 0 ? new(RemoteCommandKind.Unknown, 0, s) : new(RemoteCommandKind.LowerThirdPerson, 0, "", arg);

            case "STREAM":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => new(RemoteCommandKind.StreamOn, 0, ""),
                    "OFF" => new(RemoteCommandKind.StreamOff, 0, ""),
                    _ => new(RemoteCommandKind.Unknown, 0, s),
                };

            case "SECTION":
                if (arg.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                return int.TryParse(arg, out var part)
                    ? new(RemoteCommandKind.PlaylistSection, part, "")
                    : new(RemoteCommandKind.PlaylistSection, 0, arg);

            case "STINGER":
                if (arg.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                if (arg.Equals("STOP", StringComparison.OrdinalIgnoreCase))
                {
                    return new(RemoteCommandKind.StingerStop, 0, "");
                }
                return int.TryParse(arg, out var sting)
                    ? new(RemoteCommandKind.Stinger, sting, "")
                    : new(RemoteCommandKind.Stinger, 0, arg);

            // VOG and STING name the same library by the same number — they only assert which kind
            // they expect, so a button that says VOG can never fire a stinger. STINGER stays
            // kind-agnostic and untouched: every saved preset and phone bookmark keeps working.
            case "VOG":
                if (arg.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                if (arg.Equals("STOP", StringComparison.OrdinalIgnoreCase))
                {
                    return new(RemoteCommandKind.StingerStop, 0, "");
                }
                return int.TryParse(arg, out var vogN)
                    ? new(RemoteCommandKind.Vog, vogN, "")
                    : new(RemoteCommandKind.Vog, 0, arg);

            case "STING":
                if (arg.Length == 0) return new(RemoteCommandKind.Unknown, 0, s);
                if (arg.Equals("STOP", StringComparison.OrdinalIgnoreCase))
                {
                    return new(RemoteCommandKind.StingerStop, 0, "");
                }
                return int.TryParse(arg, out var stingN)
                    ? new(RemoteCommandKind.Sting, stingN, "")
                    : new(RemoteCommandKind.Sting, 0, arg);

            default:
                return new(RemoteCommandKind.Unknown, 0, s);
        }
    }

    /// <summary>"value ON page" → (value, page); no ON → (text, ""). The last ON wins, so a value with the word in it survives.</summary>
    private static (string Value, string Page) SplitOn(string text)
    {
        var t = text.Trim();
        var i = t.LastIndexOf(" ON ", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? (t, "") : (t[..i].Trim(), t[(i + 4)..].Trim());
    }

    public static string Ok(string? payload = null) => payload is null ? "OK" : "OK " + payload;

    public static string Err(string reason) => "ERR " + reason;
}

/// <summary>Presenter step arithmetic — pure so the clicker behaviour is unit tested.</summary>
public static class PresenterLogic
{
    /// <summary>Next index after a click; null = no move (empty list, or at an end without loop).</summary>
    public static int? Advance(int current, int count, int delta, bool loop)
    {
        if (count <= 0) return null;
        if (current < 0) return delta >= 0 ? 0 : loop ? count - 1 : null;
        var target = current + delta;
        if (target >= count) return loop ? 0 : null;
        if (target < 0) return loop ? count - 1 : null;
        return target;
    }
}
