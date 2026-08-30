namespace Patterns.Core.Services;

public enum RemoteCommandKind
{
    Unknown,
    Go,
    Stop,
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
            case "GO": return new(RemoteCommandKind.Go, 0, "");
            case "STOP": return new(RemoteCommandKind.Stop, 0, "");
            case "IDENTIFY": return new(RemoteCommandKind.Identify, 0, "");
            case "NEXT": return new(RemoteCommandKind.Next, 0, "");
            case "PREV": case "BACK": return new(RemoteCommandKind.Prev, 0, "");
            case "STATUS": return new(RemoteCommandKind.Status, 0, "");
            case "PING": return new(RemoteCommandKind.Ping, 0, "");

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
