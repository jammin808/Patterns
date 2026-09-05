using System.Globalization;
using System.Text;
using System.Text.Json;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The Interactive area, pure: how a line to a device is framed and lines from it are split,
/// how a device's words become show commands, what a device hears back about the show, and
/// how an address is read. Arduinos over serial, Raspberry Pis, ESP32s and show controllers
/// over IP all speak the same text lines — one per command, a newline at the end.
/// </summary>
public static class DeviceLines
{
    /// <summary>The ending a device expects on every line Patterns writes.</summary>
    public static string Ending(LineEnding ending) => ending switch
    {
        LineEnding.CrLf => "\r\n",
        LineEnding.Cr => "\r",
        LineEnding.None => "",
        _ => "\n",
    };

    /// <summary>One line as the device receives it: the text, trimmed of its own line breaks, plus the ending.</summary>
    public static string Frame(string text, LineEnding ending) => (text ?? "").TrimEnd('\r', '\n') + Ending(ending);

    /// <summary>
    /// Splits what arrived so far into whole lines — on \n, \r or both — and leaves the unfinished
    /// tail in the buffer. Blank lines are dropped; a line is trimmed. A buffer that grows past 4 KB
    /// without a line break is a device talking binary, and is cleared rather than kept.
    /// </summary>
    public static IReadOnlyList<string> Split(StringBuilder buffer)
    {
        var lines = new List<string>();
        var text = buffer.ToString();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\n' or '\r')) continue;
            var line = text[start..i].Trim();
            if (line.Length > 0) lines.Add(line);
            start = i + 1;
        }
        buffer.Clear();
        if (start < text.Length)
        {
            var tail = text[start..];
            if (tail.Length <= 4096) buffer.Append(tail);
        }
        return lines;
    }
}

/// <summary>
/// A device's words onto the show's protocol: a trigger row maps a line the device sends (a
/// button's name, a sensor's word) to a command; with no row matching, a device that is allowed
/// to speak the protocol itself has its line taken as it is. Pure.
/// </summary>
public static class DeviceMap
{
    /// <summary>
    /// The protocol line a device's line means, or null when it means nothing here. A trigger's
    /// Match is compared whole (case-blind) or, ending in *, as a prefix; the rest of the line
    /// after a prefix match rides as the command's tail when the command ends in *.
    /// </summary>
    public static string? Resolve(DeviceConfig device, string line)
    {
        var text = (line ?? "").Trim();
        if (text.Length == 0) return null;
        foreach (var trigger in device.Triggers)
        {
            var match = trigger.Match.Trim();
            if (match.Length == 0 || trigger.Command.Trim().Length == 0) continue;
            if (match.EndsWith('*'))
            {
                var prefix = match[..^1];
                var word = prefix.TrimEnd();
                string tail;
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) tail = text[prefix.Length..].Trim();
                else if (string.Equals(text, word, StringComparison.OrdinalIgnoreCase)) tail = "";   // "sensor *" hears a bare SENSOR too
                else continue;
                var command = trigger.Command.Trim();
                return command.EndsWith('*') ? (command[..^1] + tail).Trim() : command;
            }
            if (string.Equals(match, text, StringComparison.OrdinalIgnoreCase)) return trigger.Command.Trim();
        }
        if (!device.SpeaksProtocol) return null;
        return ControlProtocol.Parse(text).Kind == RemoteCommandKind.Unknown ? null : text;
    }
}

/// <summary>
/// What a device hears about the show: short KEY VALUE lines an Arduino parses in a few lines
/// of code — BLACKOUT 1, LIVE 0, LOOK Walk-in, CUE 01.020, ARMED 1, HOLD 0, DUCK 0, FROZEN 0,
/// DECK 3 12, PROGRAM Walk-in — built from the same STATE JSON every remote reads, and sent
/// only when a value changes, so a quiet show is a quiet wire.
/// </summary>
public static class DeviceFeedback
{
    /// <summary>The facts as KEY → VALUE, from the state JSON; a fact the JSON lacks is not in the map.</summary>
    public static IReadOnlyDictionary<string, string> Facts(string stateJson)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stateJson);
        }
        catch (JsonException)
        {
            return facts;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return facts;
            Bit(facts, root, "blackout", "BLACKOUT");
            Bit(facts, root, "live", "LIVE");
            Bit(facts, root, "duck", "DUCK");
            Bit(facts, root, "frozen", "FROZEN");
            Bit(facts, root, "review", "REVIEW");
            Text(facts, root, "airLook", "LOOK");
            Text(facts, root, "airLabel", "PROGRAM");
            Text(facts, root, "stingerPlaying", "STINGER");
            Text(facts, root, "lowerThird", "LOWERTHIRD");
            if (root.TryGetProperty("cuestack", out var cue) && cue.ValueKind == JsonValueKind.Object)
            {
                Bit(facts, cue, "armed", "ARMED");
                Bit(facts, cue, "hold", "HOLD");
                facts["CUE"] = cue.TryGetProperty("standby", out var standby) && standby.ValueKind == JsonValueKind.Object && standby.TryGetProperty("number", out var number) && number.ValueKind == JsonValueKind.String
                    ? number.GetString() ?? ""
                    : "";
            }
            if (root.TryGetProperty("deck", out var deck))
            {
                facts["DECK"] = deck.ValueKind == JsonValueKind.Object
                    ? $"{Int(deck, "page")} {Int(deck, "count")}"
                    : "0 0";
            }
            if (root.TryGetProperty("presenter", out var presenter) && presenter.ValueKind == JsonValueKind.Object)
            {
                facts["STEP"] = $"{Int(presenter, "index") + 1} {Int(presenter, "count")}";
            }
        }
        return facts;
    }

    /// <summary>
    /// The lines to send now: every fact that differs from what the device heard last (all of
    /// them for a device that heard nothing yet), as KEY VALUE. Empty when nothing changed.
    /// </summary>
    public static IReadOnlyList<string> Changes(IReadOnlyDictionary<string, string> facts, IReadOnlyDictionary<string, string>? heard)
    {
        var lines = new List<string>();
        foreach (var (key, value) in facts.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (heard is not null && heard.TryGetValue(key, out var old) && old == value) continue;
            lines.Add(value.Length > 0 ? $"{key} {value}" : key);
        }
        return lines;
    }

    private static void Bit(Dictionary<string, string> facts, JsonElement e, string property, string key)
    {
        if (e.TryGetProperty(property, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) facts[key] = v.GetBoolean() ? "1" : "0";
    }

    private static void Text(Dictionary<string, string> facts, JsonElement e, string property, string key)
    {
        if (!e.TryGetProperty(property, out var v)) return;
        facts[key] = v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Replace('\n', ' ').Replace('\r', ' ') : "";
    }

    private static int Int(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}

/// <summary>Where a device is, read from what the operator typed. Pure.</summary>
public static class DeviceAddress
{
    /// <summary>"COM3", "/dev/ttyUSB0", "/dev/tty.usbmodem14101": a serial port name, or "" when the text is not one.</summary>
    public static string SerialPort(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return "";
        if (t.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(t[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0) return "COM" + n.ToString(CultureInfo.InvariantCulture);
        if (t.StartsWith("/dev/", StringComparison.Ordinal) && t.Length > 5 && !t.Contains(' ')) return t;
        return "";
    }

    /// <summary>"192.168.1.50" with the port typed apart, or "host:7000" with it inside: the host and the port, or false.</summary>
    public static bool TryParseHost(string text, int fallbackPort, out string host, out int port)
    {
        host = "";
        port = fallbackPort;
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Contains(' ')) return false;
        var colon = t.LastIndexOf(':');
        if (colon > 0 && !t.Contains('[') && int.TryParse(t[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p))
        {
            host = t[..colon];
            port = p;
        }
        else
        {
            host = t;
        }
        return host.Length > 0 && port is > 0 and < 65536;
    }

    /// <summary>"COM3 at 115200", "192.168.1.50:7000 (TCP)", "10.0.0.9:8000 (UDP)" — or what is missing.</summary>
    public static string Describe(DeviceConfig d)
    {
        switch (d.Link)
        {
            case DeviceLink.Serial:
            {
                var port = SerialPort(d.Port);
                return port.Length == 0 ? "no serial port named — COM3, or /dev/ttyUSB0" : $"{port} at {d.Baud.ToString(CultureInfo.InvariantCulture)}";
            }
            default:
            {
                if (!TryParseHost(d.Port, d.NetPort, out var host, out var port)) return "no address — 192.168.1.50, or host:7000";
                return $"{host}:{port.ToString(CultureInfo.InvariantCulture)} ({(d.Link == DeviceLink.Udp ? "UDP" : "TCP")})";
            }
        }
    }
}

/// <summary>The Interactive area's words on the desk and for the cues. Pure.</summary>
public static class Interactive
{
    /// <summary>The device a cue or a verb names — by name (case-blind) or by its place, 1-based; "" or * is the first enabled device.</summary>
    public static DeviceConfig? Find(InteractiveConfig config, string nameOrNumber)
    {
        var t = (nameOrNumber ?? "").Trim();
        if (t.Length == 0 || t == "*") return config.Devices.FirstOrDefault(d => d.Enabled) ?? config.Devices.FirstOrDefault();
        var byName = config.Devices.FirstOrDefault(d => string.Equals(d.Name.Trim(), t, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;
        if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= config.Devices.Count) return config.Devices[n - 1];
        return config.Devices.FirstOrDefault(d => d.Id == t);
    }
}
