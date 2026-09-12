using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// What a device's line meant, in words for the status line and the journal, whether it reads
/// as a fault, and the frames to send next — a Pixera handle found is the play that wanted it.
/// </summary>
public sealed record ProfileReply(string Words, bool IsError, IReadOnlyList<byte[]> SendNext)
{
    public static ProfileReply Of(string words, bool isError = false) => new(words, isError, Array.Empty<byte[]>());
}

/// <summary>
/// A device's vocabulary: how the words a cue, the wire, the page or the assistant send become the
/// bytes the box expects, and what the bytes it sends back mean. One session per open link — a
/// projector's authentication and a media server's pending look-ups belong to the connection.
/// Pure and unit tested per profile; the wires are the App's.
/// </summary>
public abstract class ProfileSession
{
    /// <summary>The session for a device, from its profile, its line ending and its password.</summary>
    public static ProfileSession For(DeviceConfig d) => d.Profile switch
    {
        DeviceProfile.PjLink => new PjLinkSession(d.Secret),
        DeviceProfile.Disguise => new DisguiseSession(),
        DeviceProfile.Pixera => new PixeraSession(),
        DeviceProfile.Osc => new OscSession(),
        _ => new LinesSession(d.Link == DeviceLink.Http ? LineEnding.None : d.LineEnding),
    };

    public abstract DeviceProfile Profile { get; }

    /// <summary>The frames for a command in words — none, with the reason, when the words are not this profile's.</summary>
    public abstract IReadOnlyList<byte[]> Encode(string words, out string problem);

    /// <summary>A frame from the device: its words, whether it is a fault, what to send next.</summary>
    public virtual ProfileReply OnReceived(string text) => ProfileReply.Of(text);

    /// <summary>A fresh connection: whatever the last one knew (a seed, a handle) starts over.</summary>
    public virtual void OnConnected() { }

    /// <summary>Whole frames out of a byte stream so far, the unfinished tail left in the buffer; lines unless the box frames otherwise.</summary>
    public virtual IReadOnlyList<string> Split(StringBuilder buffer) => DeviceLines.Split(buffer);

    /// <summary>A datagram as text — OSC decoded to its address and arguments — or null for one that is not this profile's.</summary>
    public virtual string? Decode(byte[] datagram) => Encoding.UTF8.GetString(datagram);

    /// <summary>Words sent on their own every <see cref="PollEvery"/> while the link is open, so the status line reads the box; null for none.</summary>
    public virtual string? PollWords => null;

    public virtual TimeSpan PollEvery => TimeSpan.FromSeconds(10);

    /// <summary>The bytes of one text frame.</summary>
    protected static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    protected static IReadOnlyList<byte[]> One(string s) => new[] { Bytes(s) };

    protected static IReadOnlyList<byte[]> None(string why, out string problem)
    {
        problem = why;
        return Array.Empty<byte[]>();
    }
}

/// <summary>The profiles by name: their labels, their words, and a preset device for each.</summary>
public static class DeviceProfiles
{
    public static string Label(DeviceProfile p) => p switch
    {
        DeviceProfile.PjLink => "Projector (PJLink)",
        DeviceProfile.Disguise => "Disguise d3 (OSC)",
        DeviceProfile.Pixera => "Pixera (JSON-RPC)",
        DeviceProfile.Osc => "OSC device",
        _ => "Plain lines",
    };

    /// <summary>The words the profile understands — the page's hint, the cue editor's and the assistant's.</summary>
    public static string Words(DeviceProfile p) => p switch
    {
        DeviceProfile.PjLink => "POWER ON · POWER OFF · INPUT HDMI 1 (RGB, VIDEO, HDMI/DIGITAL, STORAGE, NETWORK n, or a two-digit code) · SHUTTER ON/OFF · MUTE ON/OFF · POWER ? · LAMP ? · ERRORS ? · NAME ? · RAW %1POWR ?",
        DeviceProfile.Disguise => "PLAY · PLAY SECTION · LOOP · STOP · NEXT · PREV · START · NEXT TRACK · PREV TRACK · TRACK <name> · TRACK #<n> · CUE <tag> · FADE UP · FADE DOWN · HOLD · VOLUME <0–100> · BRIGHTNESS <0–100> · RAW /d3/showcontrol/<address> [args]",
        DeviceProfile.Pixera => "TIMELINE <name> PLAY · TIMELINE <name> PAUSE · TIMELINE <name> STOP · CUE <timeline> <cue> · API <method> [json params] · RAW {json-rpc}",
        DeviceProfile.Osc => "/address and its arguments — a whole number, a decimal, true/false, a word or \"quoted words\" — e.g. /cue/1/start · /layer/1/opacity 0.5",
        _ => "the line the device expects, as it is — RELAY 1 · SHOW 3 · GET /api/play (HTTP)",
    };

    /// <summary>A device of the profile, as the page's button adds it: the link and port the box uses, the words to try, and no chatter back at a box that would not understand it.</summary>
    public static DeviceConfig Preset(DeviceProfile p, int number)
    {
        var suffix = number > 1 ? " " + number.ToString(CultureInfo.InvariantCulture) : "";
        var d = new DeviceConfig { Profile = p, SpeaksProtocol = false, HearsShow = false, EchoReplies = false };
        switch (p)
        {
            case DeviceProfile.PjLink:
                d.Name = "Projector" + suffix;
                d.Link = DeviceLink.Tcp;
                d.NetPort = 4352;
                d.LineEnding = LineEnding.Cr;
                d.TestText = "POWER ?";
                break;
            case DeviceProfile.Disguise:
                d.Name = "Disguise" + suffix;
                d.Link = DeviceLink.Udp;
                d.NetPort = 7401;
                d.TestText = "PLAY";
                break;
            case DeviceProfile.Pixera:
                d.Name = "Pixera" + suffix;
                d.Link = DeviceLink.Tcp;
                d.NetPort = 1400;
                d.TestText = "API Pixera.Utility.getApiRevision";
                break;
            case DeviceProfile.Osc:
                d.Name = "OSC device" + suffix;
                d.Link = DeviceLink.Udp;
                d.NetPort = 53000;
                d.TestText = "/ping";
                break;
            default:
                d.Name = "Endpoint" + suffix;
                d.Link = DeviceLink.Http;
                d.NetPort = 80;
                d.TestText = "GET /";
                d.Port = "http://192.168.1.50";
                break;
        }
        return d;
    }
}

/// <summary>Plain text lines, as the Interactive area has always sent them; over HTTP the words are the request.</summary>
public sealed class LinesSession : ProfileSession
{
    private readonly LineEnding _ending;

    public LinesSession(LineEnding ending) => _ending = ending;

    public override DeviceProfile Profile => DeviceProfile.Lines;

    public override IReadOnlyList<byte[]> Encode(string words, out string problem)
    {
        problem = "";
        var line = (words ?? "").Trim();
        return line.Length == 0 ? None("Nothing to send — the line the device expects, e.g. RELAY 1.", out problem) : One(DeviceLines.Frame(line, _ending));
    }
}

/// <summary>
/// PJLink class 1, the projector protocol every venue projector speaks on TCP 4352: %1POWR 1 and
/// friends, a CR at the end, and — when the projector asks — an MD5 of its seed and the password
/// in front of the first command of a connection. Projectors drop an idle link after 30 s; the
/// link reopens and this starts over.
/// </summary>
public sealed class PjLinkSession : ProfileSession
{
    private readonly string _password;
    private string? _seed;
    private bool _digestSent;
    private bool _authOff;

    public PjLinkSession(string password) => _password = password ?? "";

    public override DeviceProfile Profile => DeviceProfile.PjLink;

    public override string? PollWords => "POWER ?";

    public override void OnConnected()
    {
        _seed = null;
        _digestSent = false;
        _authOff = false;
    }

    /// <summary>The command body for the words — "POWR 1", "INPT 31", "AVMT 30", "LAMP ?" — or null when the words are not PJLink's.</summary>
    public static string? Body(string words)
    {
        var w = (words ?? "").Trim();
        if (w.Length == 0) return null;
        if (w.StartsWith("%", StringComparison.Ordinal)) return w.Length > 2 ? w[2..] : null;   // %1POWR ? as typed
        var parts = w.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts[0].ToUpperInvariant();
        var arg = parts.Length > 1 ? string.Join(' ', parts.Skip(1)).ToUpperInvariant() : "";
        switch (verb)
        {
            case "RAW":
                return arg.Length == 0 ? null : arg.StartsWith("%", StringComparison.Ordinal) && arg.Length > 2 ? arg[2..] : arg;
            case "POWER":
                return arg switch { "ON" or "1" => "POWR 1", "OFF" or "0" or "STANDBY" => "POWR 0", "?" or "" => "POWR ?", _ => null };
            case "INPUT":
            {
                if (arg is "?" or "") return "INPT ?";
                if (arg.Length == 2 && char.IsDigit(arg[0]) && char.IsDigit(arg[1])) return "INPT " + arg;
                var ap = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var family = ap[0] switch
                {
                    "RGB" or "VGA" or "COMPUTER" => '1',
                    "VIDEO" or "COMPOSITE" or "SVIDEO" => '2',
                    "DIGITAL" or "HDMI" or "DVI" or "SDI" or "DP" or "DISPLAYPORT" => '3',
                    "STORAGE" or "USB" => '4',
                    "NETWORK" or "LAN" => '5',
                    _ => '\0',
                };
                if (family == '\0') return null;
                var n = ap.Length > 1 && ap[1].Length == 1 && char.IsDigit(ap[1][0]) ? ap[1][0] : '1';
                return $"INPT {family}{n}";
            }
            case "SHUTTER":
            case "BLANK":
                return arg switch { "ON" or "CLOSED" or "CLOSE" => "AVMT 31", "OFF" or "OPEN" => "AVMT 30", "?" or "" => "AVMT ?", _ => null };
            case "MUTE":
                return arg switch { "ON" => "AVMT 21", "OFF" => "AVMT 20", "?" or "" => "AVMT ?", _ => null };
            case "VIDEO":
                return arg switch { "MUTE ON" or "OFF" => "AVMT 11", "MUTE OFF" or "ON" => "AVMT 10", _ => null };
            case "LAMP":
                return "LAMP ?";
            case "ERRORS":
            case "ERROR":
            case "STATUS":
                return "ERST ?";
            case "NAME":
                return "NAME ?";
            case "INFO":
                return "INFO ?";
            case "CLASS":
                return "CLSS ?";
            default:
                return null;
        }
    }

    public override IReadOnlyList<byte[]> Encode(string words, out string problem)
    {
        problem = "";
        var body = Body(words);
        if (body is null) return None($"'{words}' is not a PJLink command — {DeviceProfiles.Words(DeviceProfile.PjLink)}.", out problem);
        var prefix = "";
        if (_seed is { Length: > 0 } seed && !_digestSent)
        {
            if (_password.Length == 0) return None("The projector asks for a password (PJLink authentication is on) — type it in the device's Password box.", out problem);
            prefix = Digest(seed, _password);
            _digestSent = true;
        }
        return One(prefix + "%1" + body + "\r");
    }

    /// <summary>The MD5 of the seed and the password, lowercase hex — what the projector expects in front of the first command.</summary>
    public static string Digest(string seed, string password)
    {
        var hash = MD5.HashData(Encoding.ASCII.GetBytes(seed + password));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public override ProfileReply OnReceived(string text)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith("PJLINK ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = t[7..].Trim();
            if (rest.StartsWith("1", StringComparison.Ordinal))
            {
                _seed = rest.Length > 2 ? rest[2..].Trim() : "";
                _digestSent = false;
                return ProfileReply.Of("connected — authentication on");
            }
            if (rest.StartsWith("0", StringComparison.Ordinal))
            {
                _authOff = true;
                _seed = null;
                return ProfileReply.Of("connected — no authentication");
            }
            if (rest.StartsWith("ERRA", StringComparison.OrdinalIgnoreCase)) return ProfileReply.Of("authentication failed — check the Password box", true);
            return ProfileReply.Of(t);
        }
        // %1POWR=OK, %1POWR=1, %1LAMP=1234 1, %1ERST=000000, %1INPT=ERR2
        var eq = t.IndexOf('=');
        if (t.Length >= 7 && t[0] == '%' && eq == 6)
        {
            var cmd = t[2..6].ToUpperInvariant();
            var value = t[7..].Trim();
            if (value.StartsWith("ERR", StringComparison.OrdinalIgnoreCase))
            {
                var why = value.ToUpperInvariant() switch
                {
                    "ERR1" => "undefined command",
                    "ERR2" => "out of parameter — that input or setting is not on this projector",
                    "ERR3" => "unavailable now — the projector is warming up, cooling down or in standby",
                    "ERR4" => "projector failure",
                    "ERRA" => "authentication failed — check the Password box",
                    _ => value,
                };
                return ProfileReply.Of($"{cmd}: {why}", true);
            }
            if (value.Equals("OK", StringComparison.OrdinalIgnoreCase)) return ProfileReply.Of($"{cmd}: OK");
            return ProfileReply.Of(cmd switch
            {
                "POWR" => value switch { "0" => "power standby", "1" => "power on", "2" => "cooling down", "3" => "warming up", _ => "power " + value },
                "AVMT" => value switch { "30" => "shutter open, sound on", "31" => "shutter closed, sound muted", "11" => "shutter closed", "10" => "shutter open", "21" => "sound muted", "20" => "sound on", _ => "mute " + value },
                "LAMP" => "lamp " + LampWords(value),
                "ERST" => value.All(c => c == '0') ? "no errors" : "errors " + value + " (fan, lamp, temperature, cover, filter, other)",
                "INPT" => "input " + value,
                "NAME" => "name " + value,
                "INFO" => "info " + value,
                "CLSS" => "class " + value,
                _ => $"{cmd} {value}",
            });
        }
        return ProfileReply.Of(t);
    }

    /// <summary>"1234 h, on" from "1234 1"; several lamps in turn.</summary>
    private static string LampWords(string value)
    {
        var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        for (var i = 0; i + 1 < p.Length; i += 2) parts.Add($"{p[i]} h ({(p[i + 1] == "1" ? "on" : "off")})");
        return parts.Count == 0 ? value : string.Join(", ", parts);
    }

    /// <summary>Whether the projector said it needs no digest; for the tests and the status line.</summary>
    public bool AuthenticationOff => _authOff;
}

/// <summary>An OSC message written as words — "/layer/1/opacity 0.5 \"Main stage\"" — and read back to them.</summary>
public static class OscWords
{
    /// <summary>The message for the words: the address, then each argument typed — a whole number, a decimal, true/false, a word or a "quoted string"; null when the words are not an address.</summary>
    public static OscMessage? Parse(string words)
    {
        var w = (words ?? "").Trim();
        if (!w.StartsWith("/", StringComparison.Ordinal)) return null;
        var parts = Tokens(w);
        if (parts.Count == 0) return null;
        var args = new List<object?>();
        foreach (var (token, quoted) in parts.Skip(1))
        {
            if (quoted) { args.Add(token); continue; }
            if (int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i)) args.Add(i);
            else if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) args.Add(f);
            else if (token.Equals("true", StringComparison.OrdinalIgnoreCase)) args.Add(true);
            else if (token.Equals("false", StringComparison.OrdinalIgnoreCase)) args.Add(false);
            else args.Add(token);
        }
        return new OscMessage(parts[0].Token, args);
    }

    /// <summary>The words for a message: the address and its arguments, a string with spaces in quotes.</summary>
    public static string Format(OscMessage m)
    {
        var sb = new StringBuilder(m.Address);
        foreach (var a in m.Args)
        {
            sb.Append(' ');
            sb.Append(a switch
            {
                null => "nil",
                int i => i.ToString(CultureInfo.InvariantCulture),
                long l => l.ToString(CultureInfo.InvariantCulture),
                float f => f.ToString("0.###", CultureInfo.InvariantCulture),
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                string s => s.Contains(' ') || s.Length == 0 ? "\"" + s.Replace("\"", "\\\"") + "\"" : s,
                byte[] bytes => $"<{bytes.Length} bytes>",
                _ => a.ToString() ?? "",
            });
        }
        return sb.ToString();
    }

    private static List<(string Token, bool Quoted)> Tokens(string s)
    {
        var list = new List<(string, bool)>();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && s[i] == ' ') i++;
            if (i >= s.Length) break;
            if (s[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) i++;
                    sb.Append(s[i]);
                    i++;
                }
                i++;
                list.Add((sb.ToString(), true));
            }
            else
            {
                var start = i;
                while (i < s.Length && s[i] != ' ') i++;
                list.Add((s[start..i], false));
            }
        }
        return list;
    }
}

/// <summary>Any box that speaks OSC over UDP — QLab, Resolume, TouchDesigner, a lighting desk: the words are the address and its arguments.</summary>
public sealed class OscSession : ProfileSession
{
    public override DeviceProfile Profile => DeviceProfile.Osc;

    public override IReadOnlyList<byte[]> Encode(string words, out string problem)
    {
        problem = "";
        var m = OscWords.Parse(words);
        return m is null ? None($"'{words}' is not an OSC message — an address like /cue/1/start, then its arguments.", out problem) : new[] { OscCodec.Encode(m) };
    }

    public override string? Decode(byte[] datagram)
    {
        var messages = OscCodec.Decode(datagram);
        return messages.Count == 0 ? null : string.Join("\n", messages.Select(OscWords.Format));
    }
}

/// <summary>
/// Disguise d3 over its OSC device: the show-control addresses under /d3/showcontrol/ as words —
/// PLAY, STOP, NEXT, CUE 1.2, TRACK Main, VOLUME 80 — and RAW for any address of d3's own list.
/// d3's OSC device listens on the port set on it (7401 as shipped); the same device sends its
/// transport state back, which arrives here as lines a trigger row can read.
/// </summary>
public sealed class DisguiseSession : ProfileSession
{
    public const string Prefix = "/d3/showcontrol/";

    public override DeviceProfile Profile => DeviceProfile.Disguise;

    /// <summary>The message for the words, or null when they are not d3's.</summary>
    public static OscMessage? Message(string words)
    {
        var w = (words ?? "").Trim();
        if (w.Length == 0) return null;
        if (w.StartsWith("/", StringComparison.Ordinal)) return OscWords.Parse(w);
        var parts = w.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts[0].ToUpperInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";
        var argUpper = arg.ToUpperInvariant();
        switch (verb)
        {
            case "RAW": return OscWords.Parse(arg);
            case "PLAY": return argUpper is "SECTION" or "TO END OF SECTION" ? Of("playsection") : Of("play");
            case "LOOP": return Of("loopsection");
            case "STOP": case "PAUSE": return Of("stop");
            case "NEXT": return argUpper == "TRACK" ? Of("nexttrack") : Of("nextsection");
            case "PREV": case "PREVIOUS": case "BACK": return argUpper == "TRACK" ? Of("previoustrack") : Of("previoussection");
            case "START": case "RETURN": case "RESTART": return Of("returntostart");
            case "HOLD": return Of("hold");
            case "FADE": return argUpper switch { "UP" or "IN" => Of("fadeup"), "DOWN" or "OUT" => Of("fadedown"), _ => null };
            case "TRACK":
                if (arg.Length == 0) return null;
                if (arg.StartsWith("#", StringComparison.Ordinal) && int.TryParse(arg[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return Of("trackid", id);
                return Of("trackname", arg);
            case "CUE":
            case "GOTO":
                if (arg.Length == 0) return null;
                return float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var cue) ? Of("cue", cue) : Of("cue", arg);
            case "VOLUME": case "VOL": return Level(arg, "volume");
            case "BRIGHTNESS": case "BRIGHT": return Level(arg, "brightness");
            default: return null;
        }
    }

    private static OscMessage Of(string leaf, params object?[] args) => new(Prefix + leaf, args);

    private static OscMessage? Level(string arg, string leaf)
    {
        if (!float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return null;
        return Of(leaf, Math.Clamp(pct > 1 ? pct / 100f : pct, 0f, 1f));
    }

    public override IReadOnlyList<byte[]> Encode(string words, out string problem)
    {
        problem = "";
        var m = Message(words);
        return m is null ? None($"'{words}' is not a Disguise command — {DeviceProfiles.Words(DeviceProfile.Disguise)}.", out problem) : new[] { OscCodec.Encode(m) };
    }

    public override string? Decode(byte[] datagram)
    {
        var messages = OscCodec.Decode(datagram);
        return messages.Count == 0 ? null : string.Join("\n", messages.Select(OscWords.Format));
    }
}

/// <summary>
/// Pixera over its JSON-RPC API on TCP 1400, every request and reply ending in 0xPX. Pixera's API
/// works on handles: a timeline is found by name first and told to play by its handle, so one line
/// of words becomes a look-up whose reply sends the command — the pending steps live here. API
/// sends any method with its parameters as typed; RAW sends the JSON-RPC as it is. The method
/// names follow Pixera's API reference for its timelines; a box on another version answers with
/// its own error, which the status line shows word for word.
/// </summary>
public sealed class PixeraSession : ProfileSession
{
    public const string Terminator = "0xPX";

    private readonly Dictionary<int, Func<JsonElement, IReadOnlyList<byte[]>>> _pending = new();
    private int _id;

    public override DeviceProfile Profile => DeviceProfile.Pixera;

    public override void OnConnected() => _pending.Clear();

    public override IReadOnlyList<string> Split(StringBuilder buffer)
    {
        var frames = new List<string>();
        while (true)
        {
            var text = buffer.ToString();
            var at = text.IndexOf(Terminator, StringComparison.Ordinal);
            if (at < 0)
            {
                if (buffer.Length > 65536) buffer.Clear();   // not talking JSON-RPC at all
                return frames;
            }
            var frame = text[..at].Trim();
            buffer.Remove(0, at + Terminator.Length);
            if (frame.Length > 0) frames.Add(frame);
        }
    }

    /// <summary>One request frame: the JSON-RPC envelope with the next id, the terminator after it.</summary>
    public byte[] Request(string method, object? parameters, out int id)
    {
        id = ++_id;
        var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        return Bytes(json + Terminator);
    }

    public override IReadOnlyList<byte[]> Encode(string words, out string problem)
    {
        problem = "";
        var w = (words ?? "").Trim();
        if (w.Length == 0) return None("Nothing to send.", out problem);
        if (w.StartsWith("{", StringComparison.Ordinal)) return One(w + Terminator);
        var parts = w.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts[0].ToUpperInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";
        switch (verb)
        {
            case "RAW":
                return arg.StartsWith("{", StringComparison.Ordinal) ? One(arg + Terminator) : None("RAW wants the JSON-RPC request itself, starting with {.", out problem);
            case "API":
            {
                var ap = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (ap.Length == 0) return None("API wants a method name — API Pixera.Utility.getApiRevision.", out problem);
                object? parameters = null;
                if (ap.Length > 1)
                {
                    try { parameters = JsonSerializer.Deserialize<JsonElement>(ap[1]); }
                    catch (JsonException) { return None($"The parameters after {ap[0]} are not JSON.", out problem); }
                }
                return new[] { Request(ap[0], parameters, out _) };
            }
            case "TIMELINE":
            {
                // TIMELINE <name> PLAY|PAUSE|STOP — the name may carry spaces; the verb is the last word.
                var last = arg.LastIndexOf(' ');
                if (last <= 0) return None("TIMELINE wants a name and PLAY, PAUSE or STOP.", out problem);
                var name = arg[..last].Trim();
                var action = arg[(last + 1)..].ToUpperInvariant() switch { "PLAY" or "GO" => "play", "PAUSE" => "pause", "STOP" => "stop", _ => "" };
                if (action.Length == 0) return None("TIMELINE wants PLAY, PAUSE or STOP after the name.", out problem);
                return new[] { Lookup("Pixera.Timelines.getTimelineFromName", new { name }, handle => new[] { Request($"Pixera.Timelines.Timeline.{action}", new { handle }, out _) }) };
            }
            case "CUE":
            {
                // CUE <timeline> <cue> — the timeline by name, then its cue by name, then the cue applied.
                var cp = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (cp.Length < 2) return None("CUE wants the timeline's name and the cue's name.", out problem);
                var timeline = cp[0];
                var cue = cp[1];
                return new[]
                {
                    Lookup("Pixera.Timelines.getTimelineFromName", new { name = timeline }, handle => new[]
                    {
                        Lookup("Pixera.Timelines.Timeline.getCueFromName", new { handle, name = cue }, cueHandle => new[]
                        {
                            Request("Pixera.Timelines.Cue.apply", new { handle = cueHandle }, out _),
                        }),
                    }),
                };
            }
            default:
                return None($"'{words}' is not a Pixera command — {DeviceProfiles.Words(DeviceProfile.Pixera)}.", out problem);
        }
    }

    /// <summary>A request whose reply — a handle — is put into the next step.</summary>
    private byte[] Lookup(string method, object parameters, Func<long, IReadOnlyList<byte[]>> then)
    {
        var frame = Request(method, parameters, out var id);
        _pending[id] = result => result.ValueKind == JsonValueKind.Number && result.TryGetInt64(out var handle) ? then(handle) : Array.Empty<byte[]>();
        return frame;
    }

    /// <summary>How many look-ups wait for their reply; for the tests.</summary>
    public int PendingCount => _pending.Count;

    public override ProfileReply OnReceived(string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return ProfileReply.Of(text);
        }
        using (doc)
        {
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var i) ? i : -1;
            if (root.TryGetProperty("error", out var error))
            {
                _pending.Remove(id);
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var msg) ? msg.ToString() : error.ToString();
                return ProfileReply.Of($"Pixera error: {message}", true);
            }
            if (root.TryGetProperty("result", out var result))
            {
                var next = _pending.Remove(id, out var then) ? then(result) : Array.Empty<byte[]>();
                var words = result.ValueKind == JsonValueKind.Number && next.Count > 0 ? "found — sending on" : $"result {result}";
                return new ProfileReply(words, false, next);
            }
            return ProfileReply.Of(text);
        }
    }
}
