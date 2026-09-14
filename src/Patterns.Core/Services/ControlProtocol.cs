using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// What a wire line is once parsed: a show action — the one vocabulary the desk's keys and a cue's
/// steps speak, run by the one executor — or one of the few things a wire says that is not an
/// action: a handshake (PING, HELLO), a query (STATUS, CUE LIST), or a line nobody understands.
/// The wire has no vocabulary of its own: a verb is a spelling of a <see cref="ShowActionKind"/>.
/// </summary>
public enum RemoteCommandKind
{
    /// <summary>A line the parser does not know; Text carries it for the ERR reply.</summary>
    Unknown,
    /// <summary>A show action; <see cref="RemoteCommand.Action"/> carries it.</summary>
    Action,
    Ping,
    /// <summary>STATUS — the show's state as JSON.</summary>
    Status,
    /// <summary>"HELLO FOH deck" — the connection names itself (Text); history reads "GO from FOH deck". Nothing runs.</summary>
    Hello,
    /// <summary>CUE LIST — the caller's stack as JSON rows.</summary>
    CueList,
    /// <summary>TWIN STATUS — the twin link's role, phase and words as JSON.</summary>
    TwinStatus,
    /// <summary>SHOWLOCK STATUS — what the show lock holds, as JSON.</summary>
    ShowLockStatus,
    /// <summary>CALIBRATE STATUS — the camera calibration: running, progress, solved, applied, the solution, as JSON.</summary>
    CalibrationStatus,
    /// <summary>NODES — every other Patterns heard on the beacon and what is linked, as JSON.</summary>
    NodesStatus,
    /// <summary>STAGE STATUS — the stage timer and the messages with their receipts, as JSON.</summary>
    StageStatus,
    /// <summary>ARCADE STATUS / GAMES / SCORES: the arcade node's state, its catalogue, its board — from the node itself, or through the desk from the nodes it hears.</summary>
    ArcadeStatus,
    /// <summary>PLAY STATUS / RESULTS / QUEUE: the audience room — from the hub itself, or through the desk from the hub it hears.</summary>
    PlayStatus,
    /// <summary>ASSISTANT ASK &lt;words&gt;: the desk's assistant, one question, the reply as JSON — a node's way to the one key on the desk.</summary>
    AssistantAsk,
    /// <summary>RIGDAY STATUS / ALIGN STATUS: the show-ready bar, the alignment game, Blend Quest, the streak — as JSON.</summary>
    RigDayStatus,
}

/// <summary>
/// A parsed remote command (a TCP line, HTTP /api/cmd, OSC through its map, the Companion module,
/// a device of the Interactive area, the management server): an action, a handshake or a query, or
/// the line as Unknown.
/// </summary>
public readonly record struct RemoteCommand(RemoteCommandKind Kind, ShowAction Action, string Text = "")
{
    public bool IsAction => Kind == RemoteCommandKind.Action;

    public static RemoteCommand Of(ShowAction action) => new(RemoteCommandKind.Action, action);
}

/// <summary>
/// The text command protocol shared by the TCP port (Bitfocus Companion generic TCP and the
/// Patterns Companion module), the web remote, OSC (through its map) and the devices. One command
/// per line; responses are "OK", "OK &lt;json&gt;" or "ERR &lt;reason&gt;". Pure parsing — unit
/// tested — straight into the show's own vocabulary: every verb here is a <see cref="ShowAction"/>
/// with its target and value as the executor reads them, so nothing between the wire and the
/// executor translates.
/// </summary>
public static class ControlProtocol
{
    /// <summary>
    /// The words after FADE / FADE UP: the seconds and the scope in either order, or one of them, or
    /// nothing. "2 SCREEN 2" and "SCREEN 2 2" both read as two seconds on screen 2; "SCREEN 2" alone
    /// is the show's time on screen 2 (the scope is tried whole before its last word is read as
    /// seconds, so the screen's number is never mistaken for a time); "2" alone is the rig. False
    /// when neither reading makes sense ("slowly", "SCREEN", three words that mean nothing).
    /// </summary>
    public static bool TryParseFadeWords(string text, out int ms, out FadeScope scope)
    {
        ms = 0;
        scope = FadeScope.Everything;
        var t = text.Trim();
        if (t.Length == 0) return true;
        var words = t.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // Seconds first, the scope after.
        if (TryParseSeconds(words[0], out var first) && FadeScope.Parse(string.Join(' ', words.Skip(1))) is { } after)
        {
            ms = first;
            scope = after;
            return true;
        }
        // The scope whole (SCREEN 2, GROUP A, FOCUSED…), the show's own time.
        if (FadeScope.Parse(t) is { } whole && !(words.Length == 1 && TryParseSeconds(words[0], out _)))
        {
            scope = whole;
            return true;
        }
        // The scope, then the seconds.
        if (words.Length >= 2 && TryParseSeconds(words[^1], out var last) && FadeScope.Parse(string.Join(' ', words[..^1])) is { } before)
        {
            ms = last;
            scope = before;
            return true;
        }
        return false;
    }

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

    /// <summary>"on" / "off" / "toggle" from ON / SHOW / 1 / TRUE, OFF / HIDE / 0 / FALSE, or nothing (a bare verb toggles).</summary>
    public static string SwitchWord(string text) => OverlayControl.SwitchWord(text);

    /// <summary>"5", "2.5", "2:30" (minutes:seconds), "90s", "5m", "5 min": minutes as a decimal; false for words, nothing, zero or over a day.</summary>
    public static bool TryParseMinutes(string text, out double minutes)
    {
        minutes = 0;
        var t = text.Trim();
        if (t.Length == 0) return false;
        if (t.Contains(':'))
        {
            var parts = t.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var m)
                || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sec)
                || sec >= 60) return false;
            minutes = m + sec / 60.0;
            return minutes > 0 && minutes <= 24 * 60;
        }
        var inSeconds = false;
        foreach (var (suffix, seconds) in new[] { ("mins", false), ("min", false), ("m", false), ("secs", true), ("sec", true), ("s", true) })
        {
            if (!t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            t = t[..^suffix.Length].Trim();
            inSeconds = seconds;
            break;
        }
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v <= 0) return false;
        minutes = inSeconds ? v / 60.0 : v;
        return minutes <= 24 * 60;
    }

    /// <summary>Minutes as the wire carries them: "5", "2.5", "1.333".</summary>
    private static string Minutes(double minutes) => minutes.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Milliseconds the parser read as the action's seconds ("2", "1.5"); none for the show's own time.</summary>
    private static string Seconds(int ms) => ms > 0 ? (ms / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "";

    private static RemoteCommand Act(ShowActionKind kind, string target = "", string value = "") => RemoteCommand.Of(new ShowAction(kind, target, value));

    private static RemoteCommand Act(ShowActionKind kind, int number, string value = "")
        => Act(kind, number.ToString(System.Globalization.CultureInfo.InvariantCulture), value);

    private static RemoteCommand Query(RemoteCommandKind kind, string text = "") => new(kind, ShowAction.None, text);

    private static bool ArcadeGameWord(string word) => word is "PONG" or "SNAKE" or "BREAKOUT" or "1" or "2" or "3";

    private static RemoteCommand Unknown(string line) => Query(RemoteCommandKind.Unknown, line);

    /// <summary>
    /// One line into the show's vocabulary. The grammar is the wire's — GO and STOP kept as the
    /// outputs' frozen aliases, MUSIC with SPOTIFY as its alias, a bare CLOCK toggling — and what
    /// comes out is a <see cref="ShowAction"/> exactly as the desk's keys and a cue's steps make
    /// them: a number as its digits, a fade's length in seconds, a page's name as the target.
    /// </summary>
    public static RemoteCommand Parse(string line)
    {
        var s = line.Trim();
        if (s.Length == 0) return Unknown("");
        var parts = s.Split(' ', 2, StringSplitOptions.TrimEntries);
        var verb = parts[0].ToUpperInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";

        switch (verb)
        {
            // GO and STOP stay accepted for the outputs so existing Generic-TCP buttons keep
            // working; new integrations should send OUTPUTS ON / OFF. Bare GO never fires a cue.
            case "GO": return Act(ShowActionKind.OutputsOn);
            case "STOP": return Act(ShowActionKind.OutputsOff);
            case "OUTPUTS":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => Act(ShowActionKind.OutputsOn),
                    "OFF" => Act(ShowActionKind.OutputsOff),
                    _ => Unknown(s),
                };
            case "IDENTIFY": return Act(ShowActionKind.Identify);
            case "NEXT": return Act(ShowActionKind.PresenterNext);
            case "PREV": case "BACK": return Act(ShowActionKind.PresenterPrev);
            case "STATUS": return Query(RemoteCommandKind.Status);
            case "PING": return Query(RemoteCommandKind.Ping);
            // "STOPALL" as one token: an older build parses "STOP ALL" as STOP and closes the outputs.
            case "STOPALL": return Act(ShowActionKind.StopAll);
            case "HELLO": return arg.Length == 0 ? Unknown(s) : Query(RemoteCommandKind.Hello, arg);

            // The caller's stack: GO through the gate (the standby id the sender saw fences a stale
            // press), the standby moved or set, HOLD, ARM (a remote arms only when the Remote page
            // allows it — the executor's rule), and the list as a query.
            case "CUE":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                var what = sub[0].ToUpperInvariant();
                var rest = sub.Length > 1 ? sub[1] : "";
                switch (what)
                {
                    case "GO": return Act(ShowActionKind.CueGo, rest);
                    case "STANDBY":
                        return rest.ToUpperInvariant() switch
                        {
                            "" => Unknown(s),
                            "NEXT" => Act(ShowActionKind.CueStandby, "next"),
                            "PREV" or "BACK" => Act(ShowActionKind.CueStandby, "prev"),
                            _ => Act(ShowActionKind.CueStandby, rest),
                        };
                    case "HOLD":
                        return rest.ToUpperInvariant() switch
                        {
                            "ON" => Act(ShowActionKind.CueHoldOn),
                            "OFF" => Act(ShowActionKind.CueHoldOff),
                            _ => Unknown(s),
                        };
                    case "ARM":
                        return rest.ToUpperInvariant() switch
                        {
                            "ON" => Act(ShowActionKind.ListArm, "caller"),
                            "OFF" => Act(ShowActionKind.ListDisarm, "caller"),
                            _ => Unknown(s),
                        };
                    case "LIST": return Query(RemoteCommandKind.CueList);
                    default: return Unknown(s);
                }
            }

            // MUSIC is canonical; SPOTIFY is a frozen alias for the same reason GO/STOP are aliases
            // for OUTPUTS ON/OFF — a saved button must keep working.
            case "MUSIC":
            case "SPOTIFY":
            {
                if (arg.Length == 0) return Unknown(s);
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                var what = sub[0].ToUpperInvariant();
                var rest = sub.Length > 1 ? sub[1] : "";
                switch (what)
                {
                    case "PLAY":
                    case "RESUME":
                        if (rest.Length == 0) return Act(ShowActionKind.SpotifyPlay);
                        return int.TryParse(rest, out var pick)
                            ? Act(ShowActionKind.SpotifyPlay, pick)
                            : Act(ShowActionKind.SpotifyPlay, rest);
                    case "PAUSE":
                    case "STOP":
                        return Act(ShowActionKind.SpotifyPause);
                    case "NEXT":
                    case "SKIP":
                        return Act(ShowActionKind.SpotifyNext);
                    case "VOL":
                    case "VOLUME":
                        // The level rides the value as words — MUSIC VOL 0 is a real request; the range is
                        // checked by the executor so the operator reads a sentence, not "unknown command".
                        return rest.Length == 0 ? Unknown(s) : Act(ShowActionKind.SpotifyVolume, "", rest);
                    default:
                        // "MUSIC 3" / "MUSIC Interval bed" — by number or name, like STINGER.
                        return int.TryParse(what, out var n)
                            ? Act(ShowActionKind.SpotifyPlay, n)
                            : Act(ShowActionKind.SpotifyPlay, arg);
                }
            }

            case "BLACKOUT":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => Act(ShowActionKind.BlackoutOn),
                    "OFF" => Act(ShowActionKind.BlackoutOff),
                    _ => Act(ShowActionKind.BlackoutToggle),
                };

            case "LOOK":
                if (arg.Length == 0) return Unknown(s);
                // "LOOK #3": the third look in the show's order — a bank key that follows the list as
                // looks are made, whatever their names and F-keys. The executor counts; "#0" is a name.
                if (arg.StartsWith('#') && int.TryParse(arg[1..], out var index) && index > 0)
                {
                    return Act(ShowActionKind.ApplyLook, arg);
                }
                return int.TryParse(arg, out var slot)
                    ? Act(ShowActionKind.ApplyLookHotkey, slot)
                    : Act(ShowActionKind.ApplyLook, arg);

            case "SCREEN":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (sub.Length < 1 || !int.TryParse(sub[0], out var n)) return Unknown(s);
                var rest = sub.Length > 1 ? sub[1] : "";
                // "SCREEN 2 LOOK Sponsor": the look on that screen alone; "SCREEN 2 PROGRAM": the program again.
                if (rest.StartsWith("LOOK ", StringComparison.OrdinalIgnoreCase))
                {
                    var look = rest[5..].Trim();
                    return look.Length == 0 ? Unknown(s) : Act(ShowActionKind.ScreenLook, n, look);
                }
                // "SCREEN 2 PRESET Walk-in": a saved pattern on that screen alone, live.
                if (rest.StartsWith("PRESET ", StringComparison.OrdinalIgnoreCase))
                {
                    var preset = rest[7..].Trim();
                    return preset.Length == 0 ? Unknown(s) : Act(ShowActionKind.ScreenPreset, n, preset);
                }
                // "SCREEN 2 ROLE confidence": what the screen is for; "SCREEN 2 LABEL Stage left": its name on the desk (bare LABEL clears it).
                if (rest.StartsWith("ROLE ", StringComparison.OrdinalIgnoreCase))
                {
                    var role = rest[5..].Trim();
                    return role.Length == 0 ? Unknown(s) : Act(ShowActionKind.ScreenRole, n, role);
                }
                if (rest.StartsWith("LABEL ", StringComparison.OrdinalIgnoreCase) || rest.Equals("LABEL", StringComparison.OrdinalIgnoreCase))
                {
                    return Act(ShowActionKind.ScreenLabel, n, rest.Length > 6 ? rest[6..].Trim() : "");
                }
                var action = rest.ToUpperInvariant();
                return action switch
                {
                    "ON" => Act(ShowActionKind.ScreenOn, n),
                    "OFF" => Act(ShowActionKind.ScreenOff, n),
                    "PROGRAM" or "PGM" or "FOLLOW" => Act(ShowActionKind.ScreenProgram, n),
                    "LOOK" or "PRESET" or "ROLE" => Unknown(s),
                    _ => Act(ShowActionKind.ScreenToggle, n),
                };
            }

            case "LOCK":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (sub.Length < 1 || !int.TryParse(sub[0], out var n)) return Unknown(s);
                var action = sub.Length > 1 ? sub[1].ToUpperInvariant() : "TOGGLE";
                return action switch
                {
                    "ON" => Act(ShowActionKind.ScreenLock, n),
                    "OFF" => Act(ShowActionKind.ScreenUnlock, n),
                    _ => Act(ShowActionKind.ScreenLockToggle, n),
                };
            }

            case "GROUP":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (sub.Length < 2 || sub[0].Length == 0) return Unknown(s);
                var letter = sub[0].ToUpperInvariant();
                return sub[1].ToUpperInvariant() switch
                {
                    "ON" => Act(ShowActionKind.CanvasOn, letter),
                    "OFF" => Act(ShowActionKind.CanvasOff, letter),
                    _ => Unknown(s),
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
                        if (rest.Length == 0) return Act(ShowActionKind.AudioPlay);
                        return int.TryParse(rest, out var track) && track > 0
                            ? Act(ShowActionKind.AudioPlay, track)
                            : Act(ShowActionKind.AudioPlay, rest);
                    case "STOP":
                    case "OFF":
                        return Act(ShowActionKind.AudioStop);
                    case "NEXT":
                    case "SKIP":
                        return Act(ShowActionKind.AudioNext);
                    case "PREV":
                    case "PREVIOUS":
                    case "BACK":
                        return Act(ShowActionKind.AudioPrev);
                    case "VOL":
                    case "VOLUME":
                    case "LEVEL":
                        return int.TryParse(rest, out var level) && level is >= 0 and <= 125
                            ? Act(ShowActionKind.AudioVolume, "", rest)
                            : Unknown(s);
                    // The routing matrix: "AUDIO ROUTING ON|OFF|TOGGLE", "AUDIO ROUTE music TO Info HDMI [AT -6]",
                    // "AUDIO UNROUTE music FROM Info HDMI", "AUDIO VOG Info HDMI REPLACE|DUCK|LEAVE".
                    case "ROUTING":
                    case "MATRIX":
                        return rest.ToUpperInvariant() is "ON" or "OFF" or "TOGGLE" ? Act(ShowActionKind.AudioRouting, "", rest.ToLowerInvariant()) : Unknown(s);
                    case "ROUTE":
                    {
                        var to = rest.IndexOf(" TO ", StringComparison.OrdinalIgnoreCase);
                        if (to <= 0) return Unknown(s);
                        var source = rest[..to].Trim();
                        var value = rest[(to + 4)..].Trim();
                        if (source.Length == 0 || value.Length == 0) return Unknown(s);
                        var (_, db) = AudioRouting.ParseRouteValue(value);
                        return double.IsNaN(db) ? Unknown(s) : Act(ShowActionKind.AudioRoute, source, value);
                    }
                    case "UNROUTE":
                    {
                        var from = rest.IndexOf(" FROM ", StringComparison.OrdinalIgnoreCase);
                        if (from <= 0) return Unknown(s);
                        var source = rest[..from].Trim();
                        var destination = rest[(from + 6)..].Trim();
                        return source.Length == 0 || destination.Length == 0 ? Unknown(s) : Act(ShowActionKind.AudioUnroute, source, destination);
                    }
                    case "VOG":
                    {
                        var cut = rest.LastIndexOf(' ');
                        if (cut <= 0) return Unknown(s);
                        var destination = rest[..cut].Trim();
                        var mode = rest[(cut + 1)..].Trim();
                        return AudioRouting.TryParseVogMode(mode, out _) ? Act(ShowActionKind.AudioVogMode, destination, mode.ToLowerInvariant()) : Unknown(s);
                    }
                    default:
                        return Unknown(s);
                }
            }

            case "TONE":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => Act(ShowActionKind.ToneOn),
                    "OFF" => Act(ShowActionKind.ToneOff),
                    _ => Unknown(s),
                };

            // The live duck is a latch like BLACKOUT: ON / OFF are explicit, anything else toggles.
            case "DUCK":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => Act(ShowActionKind.DuckOn),
                    "OFF" => Act(ShowActionKind.DuckOff),
                    _ => Act(ShowActionKind.DuckToggle),
                };

            // A lower third by number (Lower thirds page order) or name; OFF / HIDE takes the one on air off;
            // "LT <design> WITH <person>" fills it from the library first (the person is the value).
            case "LOWERTHIRD":
            case "LT":
            {
                if (arg.Length == 0) return Unknown(s);
                if (arg.Equals("OFF", StringComparison.OrdinalIgnoreCase) || arg.Equals("HIDE", StringComparison.OrdinalIgnoreCase))
                {
                    return Act(ShowActionKind.LowerThirdHide);
                }
                if (arg.Equals("TAKE", StringComparison.OrdinalIgnoreCase)) return Act(ShowActionKind.LowerThirdTake);
                if (arg.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)) return Act(ShowActionKind.LowerThirdUpdate);
                // The sign-off flow: "LT PREVIEW <design> [WITH <person>]", "LT PREVIEW WITH <person>", "LT PREVIEW OFF".
                var preview = arg.StartsWith("PREVIEW", StringComparison.OrdinalIgnoreCase) ? 7
                            : arg.StartsWith("PVW", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
                if (preview > 0 && (arg.Length == preview || arg[preview] == ' '))
                {
                    var rest = arg[preview..].Trim();
                    if (rest.Length == 0) return Unknown(s);
                    if (rest.Equals("OFF", StringComparison.OrdinalIgnoreCase) || rest.Equals("CLEAR", StringComparison.OrdinalIgnoreCase) || rest.Equals("HIDE", StringComparison.OrdinalIgnoreCase))
                    {
                        return Act(ShowActionKind.LowerThirdPreviewOff);
                    }
                    if (rest.Equals("WITH", StringComparison.OrdinalIgnoreCase)) return Unknown(s);
                    if (rest.StartsWith("WITH ", StringComparison.OrdinalIgnoreCase))
                    {
                        var who = rest[5..].Trim();
                        return who.Length == 0 ? Unknown(s) : Act(ShowActionKind.LowerThirdPreview, "", who);
                    }
                    if (rest.EndsWith(" WITH", StringComparison.OrdinalIgnoreCase)) return Unknown(s);
                    var pw = rest.IndexOf(" WITH ", StringComparison.OrdinalIgnoreCase);
                    var pDesign = pw >= 0 ? rest[..pw].Trim() : rest;
                    var pPerson = pw >= 0 ? rest[(pw + 6)..].Trim() : "";
                    if (pDesign.Length == 0 || (pw >= 0 && pPerson.Length == 0)) return Unknown(s);
                    return int.TryParse(pDesign, out var pn)
                        ? Act(ShowActionKind.LowerThirdPreview, pn, pPerson)
                        : Act(ShowActionKind.LowerThirdPreview, pDesign, pPerson);
                }
                if (arg.EndsWith(" WITH", StringComparison.OrdinalIgnoreCase)) return Unknown(s);
                var with = arg.IndexOf(" WITH ", StringComparison.OrdinalIgnoreCase);
                if (with >= 0)
                {
                    var design = arg[..with].Trim();
                    var person = arg[(with + 6)..].Trim();
                    return design.Length == 0 || person.Length == 0
                        ? Unknown(s)
                        : Act(ShowActionKind.LowerThirdShow, design, person);
                }
                return int.TryParse(arg, out var lower)
                    ? Act(ShowActionKind.LowerThirdShow, lower)
                    : Act(ShowActionKind.LowerThirdShow, arg);
            }

            // The web page on air — or the one "ON <page>" names (the target): a key chord or a page action, a click,
            // typed text, a reload, another address (the value). "WEB NEXT" is "WEB KEY NEXT"; PAGE is an alias of WEB.
            case "WEB":
            case "PAGE":
            {
                if (arg.Length == 0) return Unknown(s);
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
                        return value.Length == 0 ? Unknown(s) : Act(ShowActionKind.WebKey, page, value);
                    }
                    case "CLICK":
                    {
                        var (value, page) = SplitOn(rest);
                        return value.Length == 0 ? Unknown(s) : Act(ShowActionKind.WebClick, page, value);
                    }
                    case "TYPE":
                        // The text is the text, spaces and all — no "ON <page>" here: typing goes to the page on air.
                        return rest.Length == 0 ? Unknown(s) : Act(ShowActionKind.WebType, "", rest);
                    case "RELOAD":
                    case "REFRESH":
                    {
                        var page = rest.StartsWith("ON ", StringComparison.OrdinalIgnoreCase) ? rest[3..].Trim() : rest;
                        return Act(ShowActionKind.WebReload, page);
                    }
                    case "OPEN":
                    case "GO":
                    case "NAVIGATE":
                    {
                        var (value, page) = SplitOn(rest);
                        return value.Length == 0 ? Unknown(s) : Act(ShowActionKind.WebOpen, page, value);
                    }
                    // The armed web VT: "WEB ARM" (at the mark, else where the player is), "WEB ARM 1:23", "WEB ARM OFF",
                    // "WEB DISARM", "WEB MARK" (where the player is), "WEB MARK 1:23" — each "ON <page>" or the page on air.
                    case "ARM":
                    case "MARK":
                    {
                        var (value, page) = SplitOn(rest);
                        if (!WebVt.IsValidValue(value)) return Unknown(s);
                        return Act(what == "ARM" ? ShowActionKind.WebArm : ShowActionKind.WebMark, page, value);
                    }
                    case "DISARM":
                    {
                        var page = rest.StartsWith("ON ", StringComparison.OrdinalIgnoreCase) ? rest[3..].Trim() : rest;
                        return Act(ShowActionKind.WebArm, page, "off");
                    }
                    default:
                    {
                        // "WEB NEXT", "WEB PLAY ON youtube", "WEB Ctrl+Shift+F5": an action word or a key as the verb.
                        var (value, page) = SplitOn(arg);
                        return value.Length == 0 ? Unknown(s) : Act(ShowActionKind.WebKey, page, value);
                    }
                }
            }

            // The deck on air: "DECK NEXT", "DECK PREV", "DECK FIRST", "DECK LAST", "DECK PAGE 5", "DECK 5". PDF / SLIDES are aliases.
            case "DECK":
            case "PDF":
            case "SLIDES":
            {
                if (arg.Length == 0) return Unknown(s);
                var word = arg.StartsWith("PAGE ", StringComparison.OrdinalIgnoreCase) ? arg[5..].Trim() : arg;
                if (!Decks.TryParsePage(word, out var page, out var which)) return Unknown(s);
                return which switch
                {
                    "next" => Act(ShowActionKind.DeckNext),
                    "prev" => Act(ShowActionKind.DeckPrev),
                    "first" or "last" => Act(ShowActionKind.DeckPage, "", which),
                    _ => Act(ShowActionKind.DeckPage, "", page.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                };
            }

            // The clip on air: "VIDEO END", "VIDEO END 5", "VT END", "VIDEO RESTART", "CLIP START" — the rehearsal's skip and the top.
            case "VIDEO":
            case "VT":
            case "CLIP":
            {
                if (arg.Length == 0) return Unknown(s);
                var words = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                switch (words[0].ToUpperInvariant())
                {
                    case "END":
                    case "LAST":
                    case "OUT":
                    case "TAIL":
                    {
                        var rest = words.Length > 1 ? words[1] : "";
                        if (!TryParseSeconds(rest, out var ms)) return Unknown(s);
                        return Act(ShowActionKind.VideoToEnd, "", Seconds(ms));
                    }
                    case "RESTART":
                    case "START":
                    case "TOP":
                    case "BEGIN":
                    case "REWIND":
                        return Act(ShowActionKind.VideoRestart);
                    default:
                        return Unknown(s);
                }
            }

            // A line to a device of the Interactive area: "DEVICE Arduino RELAY 1", "DEVICE * PING", "SEND pi SHOW 3" — the device is the target, the line the value.
            case "DEVICE":
            case "SEND":
            {
                var split = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (split.Length < 2) return Unknown(s);
                return Act(ShowActionKind.DeviceSend, split[0], split[1]);
            }

            // The Install page: an announcement by name or as words, an advert by name, the schedule's switch.
            case "ANNOUNCE":
            case "ANNOUNCEMENT":
            {
                if (arg.Length == 0) return Unknown(s);
                return arg.ToUpperInvariant() is "OFF" or "STOP" or "END"
                    ? Act(ShowActionKind.AnnounceOff)
                    : Act(ShowActionKind.Announce, "", arg);
            }
            case "ADVERT":
            case "AD":
            {
                if (arg.Length == 0) return Unknown(s);
                if (arg.ToUpperInvariant() is "OFF" or "STOP" or "SKIP" or "END") return Act(ShowActionKind.AdvertOff);
                return int.TryParse(arg, out var advert)
                    ? Act(ShowActionKind.AdvertPlay, advert)
                    : Act(ShowActionKind.AdvertPlay, arg);
            }
            case "SCHEDULE":
                return arg.ToUpperInvariant() switch
                {
                    "ON" or "START" or "RUN" => Act(ShowActionKind.ScheduleOn),
                    "OFF" or "STOP" or "HOLD" => Act(ShowActionKind.ScheduleOff),
                    _ => Unknown(s),
                };

            // Remote administration: the passcode rides the line, the gate decides.
            case "UPDATE":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (sub[0].ToUpperInvariant() != "APPLY") return Unknown(s);
                return Act(ShowActionKind.UpdateApply, sub.Length > 1 ? sub[1] : "");
            }
            case "RESTART":
                return Act(ShowActionKind.Restart, arg);

            // The overlays from a remote — the clock, the message, the countdown, the logo, the PiP: ON / OFF explicit, a bare verb toggles.
            case "CLOCK":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var what = sub.Length > 0 ? sub[0].ToUpperInvariant() : "";
                var rest = sub.Length > 1 ? sub[1] : "";
                switch (what)
                {
                    case "": case "TOGGLE": return Act(ShowActionKind.ClockToggle);
                    case "ON": case "SHOW": return Act(ShowActionKind.ClockOn);
                    case "OFF": case "HIDE": return Act(ShowActionKind.ClockOff);
                    case "12": case "12H": case "24": case "24H": return Act(ShowActionKind.ClockFormat, "", what[..2]);
                    case "SECONDS": case "SECS": return Act(ShowActionKind.ClockSeconds, "", SwitchWord(rest));
                    case "DATE": return Act(ShowActionKind.ClockDate, "", SwitchWord(rest));
                    default: return Unknown(s);
                }
            }
            case "MESSAGE":
            case "MSG":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var what = sub.Length > 0 ? sub[0].ToUpperInvariant() : "";
                var rest = sub.Length > 1 ? sub[1] : "";
                switch (what)
                {
                    case "": case "TOGGLE": return Act(ShowActionKind.MessageToggle);
                    case "ON": case "SHOW": return Act(ShowActionKind.MessageOn, "", rest);
                    case "OFF": case "HIDE": return Act(ShowActionKind.MessageOff);
                    case "SCROLL": return Act(ShowActionKind.MessageScroll, "", SwitchWord(rest));
                    case "TEXT": return rest.Length == 0 ? Unknown(s) : Act(ShowActionKind.MessageOn, "", rest);
                    default: return Act(ShowActionKind.MessageOn, "", arg);   // "MESSAGE Doors open at 7": the words, and on
                }
            }
            case "TICKER":
                return Act(ShowActionKind.MessageScroll, "", SwitchWord(arg));
            case "COUNTDOWN":
            case "TIMER":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var what = sub.Length > 0 ? sub[0].ToUpperInvariant() : "";
                var rest = sub.Length > 1 ? sub[1] : "";
                switch (what)
                {
                    // A bare COUNTDOWN flips it, the way a bare CLOCK, MESSAGE, LOGO or PIP does:
                    // one key on a phone or a Stream Deck turns it on and off again.
                    case "": case "TOGGLE": return Act(ShowActionKind.CountdownToggle);
                    case "STOP": case "OFF": case "HIDE": case "CLEAR": return Act(ShowActionKind.CountdownStop);
                    // The stage timer's own: a pause that keeps what is left, the resume, seconds either way, a flash.
                    case "PAUSE": case "HOLD": return Act(ShowActionKind.TimerPause);
                    case "RESUME": case "CONTINUE": case "UNPAUSE": return Act(ShowActionKind.TimerResume);
                    case "ADD": case "PLUS": return StageTimer.ParseSeconds(rest) is { } plus ? Act(ShowActionKind.TimerAdd, "", plus >= 0 ? $"+{plus:0}" : $"{plus:0}") : Unknown(s);
                    case "MINUS": case "SUB": case "LESS": return StageTimer.ParseSeconds(rest) is { } minus ? Act(ShowActionKind.TimerAdd, "", $"-{Math.Abs(minus):0}") : Unknown(s);
                    case "FLASH": case "BLINK": return Act(ShowActionKind.TimerFlash);
                    case "START": case "ON": case "GO":
                        return rest.Length == 0 ? Act(ShowActionKind.CountdownStart)
                             : TryParseMinutes(rest, out var started) ? Act(ShowActionKind.CountdownStart, "", Minutes(started))
                             : Unknown(s);
                    case "TO": case "AT": case "UNTIL":
                        return rest.Length == 0 ? Unknown(s) : Act(ShowActionKind.CountdownTo, "", rest);
                    case "LABEL": case "TEXT": case "TITLE":
                        return rest.Length == 0 ? Unknown(s) : Act(ShowActionKind.CountdownLabel, "", rest);
                    case "FOLLOW": case "PLAN":
                        return rest.ToUpperInvariant() switch
                        {
                            "" or "ON" or "1" or "PLAN" => Act(ShowActionKind.CountdownFollow, "", "on"),
                            "OFF" or "0" => Act(ShowActionKind.CountdownFollow, "", "off"),
                            _ => Unknown(s),
                        };
                    default:
                        // TIMER +60 / TIMER -30: seconds onto what is left; else a bare number of minutes starts it.
                        if (what.Length > 0 && (what[0] == '+' || what[0] == '-') && StageTimer.ParseSeconds(what) is { } nudge) return Act(ShowActionKind.TimerAdd, "", nudge >= 0 ? $"+{nudge:0}" : $"{nudge:0}");
                        return TryParseMinutes(arg, out var bare) ? Act(ShowActionKind.CountdownStart, "", Minutes(bare)) : Unknown(s);
                }
            }
            case "PLAN":
            {
                // PLAN SHIFT +2:00 / PLAN SHIFT -30 / PLAN RESUME / PLAN CATCHUP: the day's slip, as the Run surface's buttons do it.
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var what = sub.Length > 0 ? sub[0].ToUpperInvariant() : "";
                var rest = sub.Length > 1 ? sub[1] : "";
                switch (what)
                {
                    case "SHIFT": case "SLIP": case "MOVE":
                        return CueTiming.ParseDelta(rest) is { } delta ? Act(ShowActionKind.PlanShift, "", CueTiming.FormatDeltaExact(delta)) : Unknown(s);
                    case "RESUME": case "NOW":
                        return Act(ShowActionKind.PlanResume);
                    case "CATCHUP": case "CATCH":
                        return Act(ShowActionKind.PlanCatchUp);
                    default:
                        return what.Length > 0 && (what[0] == '+' || what[0] == '-') && CueTiming.ParseDelta(arg) is { } bare ? Act(ShowActionKind.PlanShift, "", CueTiming.FormatDeltaExact(bare)) : Unknown(s);
                }
            }
            case "LOGO":
                return arg.ToUpperInvariant() switch
                {
                    "ON" or "SHOW" => Act(ShowActionKind.LogoOn),
                    "OFF" or "HIDE" => Act(ShowActionKind.LogoOff),
                    "" or "TOGGLE" => Act(ShowActionKind.LogoToggle),
                    _ => Unknown(s),
                };
            case "PIP":
                return arg.ToUpperInvariant() switch
                {
                    "ON" or "SHOW" => Act(ShowActionKind.PipOn),
                    "OFF" or "HIDE" => Act(ShowActionKind.PipOff),
                    "" or "TOGGLE" => Act(ShowActionKind.PipToggle),
                    _ => Unknown(s),
                };
            case "OVERLAYS":
                return arg.ToUpperInvariant() is "OFF" or "CLEAR" or "NONE" ? Act(ShowActionKind.OverlaysOff) : Unknown(s);
            // "PATTERN Grid" / "PATTERN LED wall": the kind of picture on air, by its name (spaces ignored).
            // "PRESET Walk-in": a pattern saved on the Pattern page, back into the picture being edited.
            case "PRESET":
                return arg.Length == 0 ? Unknown(s) : Act(ShowActionKind.PatternPreset, "", arg);
            case "PATTERN":
                return arg.Trim().Length == 0 ? Unknown(s) : Act(ShowActionKind.PatternKind, "", arg.Trim());

            // The weather chip: ON / OFF / TOGGLE (a bare verb toggles), or a view — now, day, tomorrow. FORECAST is an alias.
            case "WEATHER":
            case "FORECAST":
            {
                var w = arg.Trim();
                return w.ToUpperInvariant() switch
                {
                    "ON" or "SHOW" => Act(ShowActionKind.WeatherOn),
                    "OFF" or "HIDE" => Act(ShowActionKind.WeatherOff),
                    "" or "TOGGLE" => Act(ShowActionKind.WeatherToggle),
                    _ => WeatherWords.ParseView(w) is not null ? Act(ShowActionKind.WeatherView, "", w.ToLowerInvariant()) : Unknown(w),
                };
            }

            // The review latch: the preview full-frame on every multiview. ON / OFF explicit, anything else toggles.
            case "REVIEW":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => Act(ShowActionKind.ReviewOn),
                    "OFF" => Act(ShowActionKind.ReviewOff),
                    _ => Act(ShowActionKind.ReviewToggle),
                };

            // The twin: the standby's own verbs, and the link's state.
            case "TWIN":
                return arg.ToUpperInvariant().Replace(" ", "") switch
                {
                    "TAKEOVER" => Act(ShowActionKind.TwinTakeOver),
                    "TAKEOVERFORCE" or "TAKEOVERANYWAY" or "FORCETAKEOVER" => Act(ShowActionKind.TwinTakeOver, "", "force"),
                    "STANDBY" => Act(ShowActionKind.TwinStandBy),
                    "TAKEBACK" => Act(ShowActionKind.TwinTakeBack),
                    "STATUS" or "" => Query(RemoteCommandKind.TwinStatus),
                    _ => Unknown(s),
                };

            // The show lock: the machine held for the show, released, or read.
            case "SHOWLOCK":
            case "SHOW-LOCK":
            case "MACHINELOCK":
                return arg.ToUpperInvariant() switch
                {
                    "ON" or "LOCK" => Act(ShowActionKind.ShowLockOn),
                    "OFF" or "UNLOCK" or "RELEASE" => Act(ShowActionKind.ShowLockOff),
                    "STATUS" or "" => Query(RemoteCommandKind.ShowLockStatus),
                    _ => Unknown(s),
                };

            // The stage: a message to the speaker's display (or the crew's), cleared, a flash, or the state.
            case "STAGE":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var what = sub.Length > 0 ? sub[0].ToUpperInvariant() : "";
                var rest = sub.Length > 1 ? sub[1] : "";
                switch (what)
                {
                    case "": case "STATUS": return Query(RemoteCommandKind.StageStatus);
                    case "MESSAGE": case "MSG": case "SAY": case "SPEAKER": return rest.Length == 0 ? Unknown(s) : Act(ShowActionKind.StageMessage, "speaker", rest);
                    case "CREW": return rest.Length == 0 ? Unknown(s) : Act(ShowActionKind.StageMessage, "crew", rest);
                    case "CLEAR": case "OFF": return Act(ShowActionKind.StageClear);
                    case "ACK": case "SEEN": return rest.Length == 0 ? Unknown(s) : Act(ShowActionKind.StageAck, "", rest);
                    case "FLASH": case "BLINK": return Act(ShowActionKind.TimerFlash);
                    default: return Act(ShowActionKind.StageMessage, "speaker", arg);      // "STAGE Wrap up": the words, to the speaker
                }
            }

            // The nodes: every other Patterns on the network, as the Nodes page lists them.
            // The arcade: a game started for its players, stopped, paused, the house playing itself, a
            // pad's key from a Stream Deck or a phone, the picture's size and its NDI, initials for the
            // board, and what it is doing. Run on an arcade node; the desk sends them to the arcade nodes it hears.
            case "ARCADE":
            case "GAME":
            {
                var sp = arg.IndexOf(' ');
                var sub = (sp < 0 ? arg : arg[..sp]).ToUpperInvariant();
                var tail = sp < 0 ? "" : arg[(sp + 1)..].Trim();
                switch (sub)
                {
                    case "": case "STATUS": return Query(RemoteCommandKind.ArcadeStatus);
                    case "GAMES": case "LIST": return Query(RemoteCommandKind.ArcadeStatus, "games");
                    case "SCORES": case "BOARD": case "LEADERBOARD": return Query(RemoteCommandKind.ArcadeStatus, "scores " + tail);
                    case "START": case "PLAY": return tail.Length == 0 ? Unknown(s) : Act(ShowActionKind.ArcadeStart, "", tail);
                    case "STOP": case "OFF": case "END": return Act(ShowActionKind.ArcadeStop);
                    case "PAUSE": case "HOLD": return Act(ShowActionKind.ArcadePause);
                    case "RESUME": case "CONTINUE": case "UNPAUSE": return Act(ShowActionKind.ArcadeResume);
                    case "ATTRACT": case "DEMO": case "HOUSE": return Act(ShowActionKind.ArcadeAttract, "", tail);
                    case "KEY": case "PAD": case "PRESS": return tail.Length == 0 ? Unknown(s) : Act(ShowActionKind.ArcadeKey, "", tail);
                    case "SIZE": return tail.Length == 0 ? Unknown(s) : Act(ShowActionKind.ArcadeSize, "", tail);
                    case "NDI": return Act(ShowActionKind.ArcadeNdi, "", tail.Length == 0 ? "on" : tail);
                    case "NAME": case "INITIALS": return tail.Length == 0 ? Unknown(s) : Act(ShowActionKind.ArcadeName, "", tail);
                    // "ARCADE WINDOW" opens the game's own window; "ARCADE WINDOW FULL 2" fills display 2; "ARCADE FULLSCREEN" is the same; "ARCADE WINDOW OFF" closes it.
                    case "WINDOW": case "POPOUT": case "POP": return Act(ShowActionKind.ArcadeWindow, "", tail.Length == 0 ? "on" : tail);
                    case "FULLSCREEN": case "FULL": return Act(ShowActionKind.ArcadeWindow, "", ("full " + tail).Trim());
                    default:
                        // "ARCADE pong 2": the game's own name starts it.
                        return ArcadeGameWord(sub) ? Act(ShowActionKind.ArcadeStart, "", arg.Trim()) : Unknown(s);
                }
            }

            // The audience room: questions added, opened, closed and revealed; the wall's picture;
            // messages back; the queue; the path and the draughts board; the room itself.
            case "PLAY":
            case "ROOM":
            {
                var sp = arg.IndexOf(' ');
                var sub = (sp < 0 ? arg : arg[..sp]).ToUpperInvariant();
                var tail = sp < 0 ? "" : arg[(sp + 1)..].Trim();
                switch (sub)
                {
                    case "": case "STATUS": return Query(RemoteCommandKind.PlayStatus);
                    case "RESULTS": return Query(RemoteCommandKind.PlayStatus, "results " + tail);
                    case "QUEUE": return Query(RemoteCommandKind.PlayStatus, "queue");
                    case "ADD": case "ASK": case "QUESTION": return tail.Length == 0 ? Unknown(s) : Act(ShowActionKind.PlayAdd, "", tail);
                    case "OPEN": case "START": return Act(ShowActionKind.PlayOpen, "", tail);
                    case "NEXT": return Act(ShowActionKind.PlayOpen, "", "next");
                    case "CLOSE": case "STOP": return Act(ShowActionKind.PlayClose);
                    case "REVEAL": case "ANSWER": return Act(ShowActionKind.PlayReveal);
                    case "SHOW": case "WALL": return Act(ShowActionKind.PlayShow, "", tail.Length == 0 ? "results" : tail);
                    case "HIDE": return Act(ShowActionKind.PlayShow, "", "off");
                    case "MESSAGE": case "SAY": case "TELL": return tail.Length == 0 ? Unknown(s) : Act(ShowActionKind.PlayMessage, "", tail);
                    case "APPROVE": case "OK": case "YES": return Act(ShowActionKind.PlayApprove, "", tail.Length == 0 ? "all" : tail);
                    case "REJECT": case "NO": return tail.Length == 0 ? Unknown(s) : Act(ShowActionKind.PlayReject, "", tail);
                    case "AUTO": return Act(ShowActionKind.PlayAuto, "", tail.Length == 0 ? "on" : tail);
                    case "PATH": case "STORY": return Act(ShowActionKind.PlayPath, "", tail.Length == 0 ? "open" : tail);
                    case "DRAUGHTS": case "CHECKERS": return Act(ShowActionKind.PlayDraughts, "", tail.Length == 0 ? "reset" : tail);
                    case "NEW": case "RESET": case "CLEAR": return Act(ShowActionKind.PlayRoom, "", sub == "NEW" ? "new" : "reset");
                    case "EXPORT": case "SAVE": return Act(ShowActionKind.PlayExport);
                    default: return Unknown(s);
                }
            }

            // The audience listener: on (with a port), off, or its status.
            case "AUDIENCE":
            {
                var sp = arg.IndexOf(' ');
                var sub = (sp < 0 ? arg : arg[..sp]).ToUpperInvariant();
                var tail = sp < 0 ? "" : arg[(sp + 1)..].Trim();
                return sub switch
                {
                    "" or "STATUS" => Query(RemoteCommandKind.PlayStatus, "audience"),
                    "ON" or "OPEN" => Act(ShowActionKind.AudienceOn, "", tail),
                    "OFF" or "CLOSE" => Act(ShowActionKind.AudienceOff),
                    _ => Unknown(s),
                };
            }

            // Rig day, gamified: the games' switch and the show-ready bar; the alignment game's verbs.
            case "RIGDAY":
            case "RIG-DAY":
            case "GAMES":
                return arg.ToUpperInvariant() switch
                {
                    "" or "STATUS" => Query(RemoteCommandKind.RigDayStatus),
                    "ON" => Act(ShowActionKind.RigDayOn),
                    "OFF" => Act(ShowActionKind.RigDayOff),
                    _ => Unknown(s),
                };
            case "ALIGN":
            case "ALIGNMENT":
            {
                var sp = arg.IndexOf(' ');
                var sub = (sp < 0 ? arg : arg[..sp]).ToUpperInvariant();
                var tail = sp < 0 ? "" : arg[(sp + 1)..].Trim();
                return sub switch
                {
                    "" or "STATUS" => Query(RemoteCommandKind.RigDayStatus, "align"),
                    "START" or "PLAY" => Act(ShowActionKind.AlignStart, "", tail),
                    "STOP" or "END" => Act(ShowActionKind.AlignStop),
                    "NEXT" => Act(ShowActionKind.AlignNext),
                    "PREV" or "BACK" => Act(ShowActionKind.AlignPrev),
                    "NUDGE" or "MOVE" => tail.Length == 0 ? Unknown(s) : Act(ShowActionKind.AlignNudge, "", tail),
                    "SNAP" => Act(ShowActionKind.AlignSnap),
                    _ => Unknown(s),
                };
            }

            // The desk's assistant from a node: one ask, the reply as JSON.
            case "ASSISTANT":
            case "AI":
            {
                var sp = arg.IndexOf(' ');
                var sub = (sp < 0 ? arg : arg[..sp]).ToUpperInvariant();
                var tail = sp < 0 ? "" : arg[(sp + 1)..].Trim();
                return sub is "ASK" or "MODERATE" && tail.Length > 0 ? Query(RemoteCommandKind.AssistantAsk, (sub == "MODERATE" ? "moderate " : "") + tail) : Unknown(s);
            }

            case "NODES":
            case "NODE":
                return arg.ToUpperInvariant() switch
                {
                    "" or "STATUS" or "LIST" => Query(RemoteCommandKind.NodesStatus),
                    _ => Unknown(s),
                };

            // The camera calibration: a run through a camera, cancelled, the demo, applied, undone, or read.
            case "CALIBRATE":
            case "CALIBRATION":
            case "CAL":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var what = sub.Length > 0 ? sub[0].ToUpperInvariant() : "";
                var rest = sub.Length > 1 ? sub[1] : "";
                return what switch
                {
                    "RUN" or "START" => Act(ShowActionKind.CalibrateRun, "", rest),
                    "CANCEL" or "STOP" => Act(ShowActionKind.CalibrateCancel),
                    "DEMO" => Act(ShowActionKind.CalibrateDemo),
                    "APPLY" => Act(ShowActionKind.CalibrateApply),
                    "UNDO" => Act(ShowActionKind.CalibrateUndo),
                    "STATUS" or "" => Query(RemoteCommandKind.CalibrationStatus),
                    _ => Unknown(s),
                };
            }

            case "FREEZE":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => Act(ShowActionKind.FreezeOn),
                    "OFF" => Act(ShowActionKind.FreezeOff),
                    _ => Act(ShowActionKind.FreezeToggle),
                };

            // "FADE", "FADE 2", "FADE 2.5", "FADE UP", "FADE UP 3", "FADEUP 3", "FADE DOWN 1": seconds, or the show's own time.
            // Where it lands comes after or before the seconds: "FADE 2 SCREEN 2", "FADE SCREEN 2 1.5", "FADE GROUP A",
            // "FADE FOCUSED", "FADE UP TICKED 2", "FADE GROUPS" — the scope's words are the target ("" = the rig), the seconds the value.
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
                if (!TryParseFadeWords(rest, out var ms, out var scope)) return Unknown(s);
                return Act(up ? ShowActionKind.FadeUp : ShowActionKind.FadeToBlack, scope.Words, Seconds(ms));
            }

            case "LOOKBACK":
                return Act(ShowActionKind.LookBack, "", arg);

            // A person from the library into the lower third on air (else the first design), and on air.
            case "PERSON":
                return arg.Length == 0 ? Unknown(s) : Act(ShowActionKind.LowerThirdShow, "", arg);

            case "STREAM":
                return arg.ToUpperInvariant() switch
                {
                    "ON" => Act(ShowActionKind.StreamStart),
                    "OFF" => Act(ShowActionKind.StreamStop),
                    _ => Unknown(s),
                };

            case "SECTION":
                if (arg.Length == 0) return Unknown(s);
                return int.TryParse(arg, out var part)
                    ? Act(ShowActionKind.PlaylistPart, part)
                    : Act(ShowActionKind.PlaylistPart, arg);

            case "STINGER":
                if (arg.Length == 0) return Unknown(s);
                if (arg.Equals("STOP", StringComparison.OrdinalIgnoreCase))
                {
                    return Act(ShowActionKind.StingerStop);
                }
                return int.TryParse(arg, out var sting)
                    ? Act(ShowActionKind.StingerFire, sting)
                    : Act(ShowActionKind.StingerFire, arg);

            // VOG and STING name the same library by the same number — they only assert which kind
            // they expect (the value), so a button that says VOG can never fire a stinger. STINGER stays
            // kind-agnostic and untouched: every saved preset and phone bookmark keeps working.
            case "VOG":
                if (arg.Length == 0) return Unknown(s);
                if (arg.Equals("STOP", StringComparison.OrdinalIgnoreCase))
                {
                    return Act(ShowActionKind.StingerStop);
                }
                return int.TryParse(arg, out var vogN)
                    ? Act(ShowActionKind.StingerFire, vogN, "vog")
                    : Act(ShowActionKind.StingerFire, arg, "vog");

            case "STING":
                if (arg.Length == 0) return Unknown(s);
                if (arg.Equals("STOP", StringComparison.OrdinalIgnoreCase))
                {
                    return Act(ShowActionKind.StingerStop);
                }
                return int.TryParse(arg, out var stingN)
                    ? Act(ShowActionKind.StingerFire, stingN, "sting")
                    : Act(ShowActionKind.StingerFire, arg, "sting");

            default:
                return Unknown(s);
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
