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
    AudioPlay,
    AudioStop,
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
}

/// <summary>A parsed remote command (TCP line, HTTP /api/cmd, or the Companion module).</summary>
public readonly record struct RemoteCommand(RemoteCommandKind Kind, int IntArg, string TextArg);

/// <summary>
/// The text command protocol shared by the TCP port (Bitfocus Companion generic TCP and the
/// Patterns Companion module) and the web remote. One command per line; responses are
/// "OK", "OK &lt;json&gt;" or "ERR &lt;reason&gt;". Pure parsing — unit tested.
/// </summary>
public static class ControlProtocol
{
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
                return int.TryParse(arg, out var slot)
                    ? new(RemoteCommandKind.Look, slot, "")
                    : new(RemoteCommandKind.Look, 0, arg);

            case "SCREEN":
            {
                var sub = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (sub.Length < 1 || !int.TryParse(sub[0], out var n)) return new(RemoteCommandKind.Unknown, 0, s);
                var action = sub.Length > 1 ? sub[1].ToUpperInvariant() : "TOGGLE";
                return action switch
                {
                    "ON" => new(RemoteCommandKind.ScreenOn, n, ""),
                    "OFF" => new(RemoteCommandKind.ScreenOff, n, ""),
                    _ => new(RemoteCommandKind.ScreenToggle, n, ""),
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

            case "AUDIO":
                return arg.ToUpperInvariant() switch
                {
                    "PLAY" => new(RemoteCommandKind.AudioPlay, 0, ""),
                    "STOP" => new(RemoteCommandKind.AudioStop, 0, ""),
                    _ => new(RemoteCommandKind.Unknown, 0, s),
                };

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
