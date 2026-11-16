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
/// <param name="Ack">The reply answers a command this desk sent — the box said yes, or gave the value asked for.</param>
/// <param name="AckKey">Which command it answers, when the profile can tell ("POWR"); "" for the oldest one waiting.</param>
/// <param name="Outcome">A Show Bus node's answer by Show Bus 13.12.3 — applied, accepted (taken, not run yet) or refused; null for every
/// other profile, whose yes and no say all there is.</param>
public sealed record ProfileReply(string Words, bool IsError, IReadOnlyList<byte[]> SendNext, bool Ack = false, string AckKey = "", LinkOutcome? Outcome = null)
{
    public static ProfileReply Of(string words, bool isError = false) => new(words, isError, Array.Empty<byte[]>());

    /// <summary>A yes, or a value, for a command.</summary>
    public static ProfileReply Acked(string words, string key = "") => new(words, false, Array.Empty<byte[]>(), true, key);

    /// <summary>A no for a command.</summary>
    public static ProfileReply Refused(string words, string key = "") => new(words, true, Array.Empty<byte[]>(), false, key);
}

/// <summary>
/// A device's vocabulary: how the words a cue, the wire, the page or the assistant send become the
/// bytes the box expects, and what the bytes it sends back mean. One session per open link — a
/// projector's authentication and a media server's pending look-ups belong to the connection.
/// Pure and unit tested per profile; the wires are the App's.
/// </summary>
public abstract class ProfileSession
{
    /// <summary>
    /// The session for a device, from its profile, its line ending and its password. A Show Bus node's session also takes this desk's
    /// name, which its HELLO and its proof carry, and this process's instance, which its request ids end in (Show Bus 13.12.1).
    /// </summary>
    public static ProfileSession For(DeviceConfig d, string deskName = "", string inst = "") => d.Profile switch
    {
        DeviceProfile.ShowBusNode => new ShowBusNodeSession(d.Secret, deskName, inst, d.Name),
        DeviceProfile.PjLink => new PjLinkSession(d.Secret),
        DeviceProfile.Disguise => new DisguiseSession(),
        DeviceProfile.Pixera => new PixeraSession(),
        DeviceProfile.Osc => new OscSession(),
        DeviceProfile.Companion => new CompanionSession(d.Surface),
        _ => new LinesSession(d.Link == DeviceLink.Http ? LineEnding.None : d.LineEnding),
    };

    public abstract DeviceProfile Profile { get; }

    /// <summary>The frames for a command in words — none, with the reason, when the words are not this profile's.</summary>
    public abstract IReadOnlyList<byte[]> Encode(string words, out string problem);

    /// <summary>
    /// The frames for one of the show's facts (BLACKOUT 1, LOOK Keynote, the arrival's OPEN 1): as a command for every profile but
    /// one that tells its facts apart from its commands. None, with no words, for a fact this profile does not carry.
    /// </summary>
    public virtual IReadOnlyList<byte[]> EncodeFact(string words, out string problem) => Encode(words, out problem);

    /// <summary>Whether the show's facts may go to the box now: always, but for a profile that sends none before it has paired.</summary>
    public virtual bool TakesFacts => true;

    /// <summary>Whether a line from the box may be read as a command to the show (through its trigger rows): never for a node's own answers.</summary>
    public virtual bool LinesAreCommands => true;

    /// <summary>The frames a new connection opens with, written before anything else the session sends on it; none for most boxes.</summary>
    public virtual IReadOnlyList<byte[]> OnOpened() => Array.Empty<byte[]>();

    /// <summary>The receipt's words for a line the box did not answer within its timeout.</summary>
    public virtual string Unanswered(TimeSpan timeout, ConfirmLevel reached, ConfirmLevel wanted)
        => $"no answer in {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s ({DeviceConfirmation.Label(reached)}, not {DeviceConfirmation.Label(wanted)})";

    /// <summary>A frame from the device: its words, whether it is a fault, what to send next.</summary>
    public virtual ProfileReply OnReceived(string text) => ProfileReply.Of(text);

    /// <summary>
    /// A fresh connection: whatever the last one knew (a seed, a handle) starts over. On a link made of connections it is called once
    /// per connection, before anything is encoded for it and before any line of it reaches <see cref="OnReceived"/>; on any other
    /// link, when the poll first sees it open.
    /// </summary>
    public virtual void OnConnected() { }

    /// <summary>
    /// The keys (<see cref="SentKey"/>) of the lines whose next step the last <see cref="OnConnected"/> forgot — a Pixera command
    /// whose look-up was asked on the connection that ended, so the command itself can never go — each taken once; none for most
    /// boxes. Each such line ends not-sent (Show Bus 13.12.3: never written; it did not run).
    /// </summary>
    public virtual IReadOnlyList<string> TakeForgotten() => Array.Empty<string>();

    /// <summary>
    /// A frame said on a connection this session has already started over from: read without touching what the new connection
    /// knows, it is a reply keyed to the request it answers (<see cref="ProfileReply.AckKey"/>, never "") when the profile can tell
    /// whose — a Pixera command's own result settles its own line — and null otherwise, so it settles nothing; it never sends on.
    /// </summary>
    public virtual ProfileReply? OnReceivedOfEnded(string text) => null;

    /// <summary>
    /// Whether every answer to a request this session made carries its line's key, so a keyed answer settles only the line of that
    /// key and a line keyed "" (a request whose answer the session cannot tell) takes only an answer keyed "".
    /// </summary>
    public virtual bool KeysEveryAnswer => false;

    /// <summary>Whole frames out of a byte stream so far, the unfinished tail left in the buffer; lines unless the box frames otherwise.</summary>
    public virtual IReadOnlyList<string> Split(StringBuilder buffer) => DeviceLines.Split(buffer);

    /// <summary>A datagram as text — OSC decoded to its address and arguments — or null for one that is not this profile's.</summary>
    public virtual string? Decode(byte[] datagram) => Encoding.UTF8.GetString(datagram);

    /// <summary>Words sent on their own every <see cref="PollEvery"/> while the link is open, so the status line reads the box; null for none.</summary>
    public virtual string? PollWords => null;

    /// <summary>The key a reply to these words will carry (<see cref="ProfileReply.AckKey"/>) — "" when the profile cannot tell one command's answer from another's.</summary>
    public virtual string SentKey(string words) => "";

    public virtual TimeSpan PollEvery => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Round 65.11: the words that ask the box what its input receives — a PJLink class 2 projector's
    /// "IRES ?" — and the pattern that reads the answer (groups w, h, hz, enc, bits); null when the
    /// profile has no published way to ask, and the device's own InputQuery / InputPattern stand alone.
    /// </summary>
    public virtual string? InputQuery => null;

    public virtual string? InputPattern => null;

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
        DeviceProfile.Companion => "Companion (its TCP API)",
        DeviceProfile.ShowBusNode => "Show Bus node (PlayMap)",
        _ => "Plain lines",
    };

    /// <summary>The words the profile understands — the page's hint, the cue editor's and the assistant's.</summary>
    public static string Words(DeviceProfile p) => p switch
    {
        DeviceProfile.PjLink => "POWER ON · POWER OFF · INPUT HDMI 1 (RGB, VIDEO, HDMI/DIGITAL, STORAGE, NETWORK n, or a two-digit code) · SHUTTER ON/OFF · MUTE ON/OFF · POWER ? · LAMP ? · ERRORS ? · NAME ? · RAW %1POWR ?",
        DeviceProfile.Disguise => "PLAY · PLAY SECTION · LOOP · STOP · NEXT · PREV · START · NEXT TRACK · PREV TRACK · TRACK <name> · TRACK #<n> · CUE <tag> · FADE UP · FADE DOWN · HOLD · VOLUME <0–100> · BRIGHTNESS <0–100> · RAW /d3/showcontrol/<address> [args]",
        DeviceProfile.Pixera => "TIMELINE <name> PLAY · TIMELINE <name> PAUSE · TIMELINE <name> STOP · CUE <timeline> <cue> · API <method> [json params] · RAW {json-rpc}",
        DeviceProfile.Osc => "/address and its arguments — a whole number, a decimal, true/false, a word or \"quoted words\" — e.g. /cue/1/start · /layer/1/opacity 0.5",
        DeviceProfile.Companion => "PAGE <n> [surface] · PAGE UP / DOWN [surface] · PRESS <page/row/column> · DOWN / UP <page/row/column> · ROTATE LEFT / RIGHT <page/row/column> · STEP <page/row/column> <n> · TEXT <page/row/column> <words> · COLOR / BGCOLOR <page/row/column> <#hex> · VAR <name> <value> · GET <name> · RESCAN · RAW <Companion's own line>",
        DeviceProfile.ShowBusNode => "the node's own verbs, each answered by its own receipt — PLAYLIST GO <name> · BLACKOUT ON · PING · STATUS; the show's facts (BLACKOUT 1, LOOK …) follow on their own once it is paired, and its context is drawn on the Eye",
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
            case DeviceProfile.Companion:
                d.Name = "Companion" + suffix;
                d.Link = DeviceLink.Tcp;
                d.NetPort = CompanionModule.ApiPort;
                d.TestText = "RESCAN";
                d.Confirm = ConfirmLevel.Accepted;
                break;
            case DeviceProfile.ShowBusNode:
                // PlayMap's line wire: its own verbs with their own receipts, the show's facts as it hears them, nothing it says run
                // here as a command. A receipt is waited for as long as Show Bus 13.12.4 lets an answer come.
                d.Name = "PlayMap" + suffix;
                d.Link = DeviceLink.Tcp;
                d.NetPort = ShowBusNodeSession.WirePort;
                d.LineEnding = LineEnding.Lf;
                d.TestText = "PING";
                d.HearsShow = true;
                d.Confirm = ConfirmLevel.Accepted;
                d.ConfirmTimeoutMs = (int)PeerLink.ReceiptFinal.TotalMilliseconds;
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

    /// <summary>A board that answers OK or ERR answers the oldest line waiting; anything else is its own line, a trigger's to read.</summary>
    public override ProfileReply OnReceived(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Equals("OK", StringComparison.OrdinalIgnoreCase) || t.StartsWith("OK ", StringComparison.OrdinalIgnoreCase)) return ProfileReply.Acked(t);
        if (t.Equals("ERR", StringComparison.OrdinalIgnoreCase) || t.StartsWith("ERR ", StringComparison.OrdinalIgnoreCase) || t.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) return ProfileReply.Refused(t);
        return ProfileReply.Of(t);
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

    /// <summary>PJLink class 2: IRES ? answers the input's resolution — "%2IRES=1920x1080"; a class 1 projector answers ERR1 (undefined command), which reads as no answer.</summary>
    public override string? InputQuery => "%2IRES ?";

    public override string? InputPattern => @"IRES=(?<w>\d{3,5})x(?<h>\d{3,5})";

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
        if (w.StartsWith('%')) return w.Length > 2 ? w[2..] : null;   // %1POWR ? as typed
        var parts = w.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts[0].ToUpperInvariant();
        var arg = parts.Length > 1 ? string.Join(' ', parts.Skip(1)).ToUpperInvariant() : "";
        switch (verb)
        {
            case "RAW":
                return arg.Length == 0 ? null : arg.StartsWith('%') && arg.Length > 2 ? arg[2..] : arg;
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

    /// <summary>The four-letter command a reply names: POWR, INPT, AVMT.</summary>
    public override string SentKey(string words)
    {
        var body = Body(words);
        return body is { Length: >= 4 } ? body[..4].ToUpperInvariant() : "";
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
#pragma warning disable CA5351, S4790 // PJLink (JBMIA) specifies MD5 for its authentication digest — the projector's protocol, not a secret store
        var hash = MD5.HashData(Encoding.ASCII.GetBytes(seed + password));
#pragma warning restore CA5351, S4790
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public override ProfileReply OnReceived(string text)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith("PJLINK ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = t[7..].Trim();
            if (rest.StartsWith('1'))
            {
                _seed = rest.Length > 2 ? rest[2..].Trim() : "";
                _digestSent = false;
                return ProfileReply.Of("connected — authentication on");
            }
            if (rest.StartsWith('0'))
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
                return ProfileReply.Refused($"{cmd}: {why}", cmd);
            }
            if (value.Equals("OK", StringComparison.OrdinalIgnoreCase)) return ProfileReply.Acked($"{cmd}: OK", cmd);
            return ProfileReply.Acked(cmd switch
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
            }, cmd);
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
        if (!w.StartsWith('/')) return null;
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
        if (w.StartsWith('/')) return OscWords.Parse(w);
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
                if (arg.StartsWith('#') && int.TryParse(arg[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return Of("trackid", id);
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

    /// <summary>The receipt's words for a command whose look-up's connection ended before the command could go on it.</summary>
    public const string ForgottenWords = "not-sent — the connection its look-up was asked on ended before the command could go on it, so the command itself never went; it did not run";

    /// <summary>How many request ids are remembered with the line they serve; an older one that was never answered is let go.</summary>
    public const int AskedKept = 256;

    private readonly Dictionary<int, Func<JsonElement, IReadOnlyList<byte[]>>> _pending = new();
    // Every request this session made, by id, to the key of the line it serves (a look-up and the command it leads to share one),
    // so an answer settles that line's receipt and no other; answered ids leave, and the oldest go past AskedKept.
    private readonly Dictionary<int, string> _asked = new();
    private readonly Queue<int> _askedOrder = new();
    // Look-ups a start-over forgot: their answer, if one still reaches the session, plays nothing and acknowledges nothing.
    private readonly HashSet<int> _gone = new();
    private readonly Queue<int> _goneOrder = new();
    private readonly List<string> _forgotten = new();
    private string? _serving;
    private string _lastKey = "";
    private int _id;

    public override DeviceProfile Profile => DeviceProfile.Pixera;

    /// <summary>A new connection: every look-up of the last one is forgotten, and the lines they served end not-sent.</summary>
    public override void OnConnected()
    {
        foreach (var id in _pending.Keys)
        {
            if (_gone.Add(id)) _goneOrder.Enqueue(id);
            if (_asked.TryGetValue(id, out var key) && !_forgotten.Contains(key)) _forgotten.Add(key);
        }
        _pending.Clear();
        // The oldest forgotten look-ups go past AskedKept, and with them their ids' lines, so a late answer to one is nobody's yes.
        while (_goneOrder.Count > AskedKept)
        {
            var old = _goneOrder.Dequeue();
            _gone.Remove(old);
            _asked.Remove(old);
        }
    }

    public override bool KeysEveryAnswer => true;

    public override ProfileReply? OnReceivedOfEnded(string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt32(out var id)) return null;
            // A look-up still waiting belongs to the connection this session holds; it is answered there, never by an ended one's frame.
            if (_pending.ContainsKey(id) || !_asked.TryGetValue(id, out var key)) return null;
            _asked.Remove(id);
            if (_gone.Remove(id)) return null;   // a forgotten look-up's answer: its line already ended not-sent
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var msg) ? msg.ToString() : error.ToString();
                return ProfileReply.Refused($"Pixera error: {message}", key);
            }
            return root.TryGetProperty("result", out var result) ? ProfileReply.Acked($"result {result}", key) : null;
        }
    }

    public override IReadOnlyList<string> TakeForgotten()
    {
        if (_forgotten.Count == 0) return Array.Empty<string>();
        var keys = _forgotten.ToArray();
        _forgotten.Clear();
        return keys;
    }

    /// <summary>The key of the line just encoded: its first request's (a look-up's for TIMELINE and CUE); "" for RAW, whose id is the sender's.</summary>
    public override string SentKey(string words) => _lastKey;

    private static string KeyOf(int id) => "px" + id.ToString(CultureInfo.InvariantCulture);

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
        var key = _serving ?? KeyOf(id);
        if (_serving is null && _lastKey.Length == 0) _lastKey = key;
        _asked[id] = key;
        _askedOrder.Enqueue(id);
        while (_askedOrder.Count > AskedKept) _asked.Remove(_askedOrder.Dequeue());
        var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        return Bytes(json + Terminator);
    }

    public override IReadOnlyList<byte[]> Encode(string words, out string problem)
    {
        problem = "";
        _lastKey = "";
        var w = (words ?? "").Trim();
        if (w.Length == 0) return None("Nothing to send.", out problem);
        if (w.StartsWith('{')) return One(w + Terminator);
        var parts = w.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts[0].ToUpperInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";
        switch (verb)
        {
            case "RAW":
                return arg.StartsWith('{') ? One(arg + Terminator) : None("RAW wants the JSON-RPC request itself, starting with {.", out problem);
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
            // The line this answer belongs to, by the request it answers: it settles that line's receipt and no other ("" for an id
            // this session did not make, a RAW request's).
            var key = _asked.Remove(id, out var k) ? k : "";
            // A look-up a start-over forgot: its line already ended not-sent; its answer, a handle or an error, is a note that plays
            // nothing and settles nothing.
            if (_gone.Remove(id))
            {
                var said = root.TryGetProperty("error", out var lateError) ? $"error {lateError}" : root.TryGetProperty("result", out var lateResult) ? $"result {lateResult}" : "an answer";
                return ProfileReply.Of($"{said} — the answer to a look-up of a connection that ended; nothing sent on");
            }
            if (root.TryGetProperty("error", out var error))
            {
                _pending.Remove(id);
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var msg) ? msg.ToString() : error.ToString();
                return ProfileReply.Refused($"Pixera error: {message}", key);
            }
            if (root.TryGetProperty("result", out var result))
            {
                var lookedUp = _pending.Remove(id, out var then);
                IReadOnlyList<byte[]> next;
                _serving = key.Length > 0 ? key : null;   // the command a handle leads to serves the line its look-up served
                try
                {
                    next = lookedUp ? then!(result) : Array.Empty<byte[]>();
                }
                finally
                {
                    _serving = null;
                }
                var words = result.ValueKind == JsonValueKind.Number && next.Count > 0 ? "found — sending on" : $"result {result}";
                // A look-up's handle is on the way to the command; the command's own result is the yes. An answer that matches no
                // look-up of this connection is a command's own result, never the yes of a line whose look-up it does not answer.
                return new ProfileReply(words, false, next, Ack: !lookedUp, AckKey: key);
            }
            return ProfileReply.Of(text);
        }
    }
}
