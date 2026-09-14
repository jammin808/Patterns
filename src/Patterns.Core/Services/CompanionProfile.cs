using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// Companion as a box the desk drives: its TCP remote-control API (port 16759) as a device
/// profile, so a cue can say PAGE 3 and the Stream Deck turns to the Q&amp;A page, PRESS 2/0/1 fires
/// a Companion button, VAR speaker "Jane Doe" fills a custom variable — through the same device a
/// projector or a media server is: the link that opens and reopens by itself, the DEVICE verb, the
/// journal, the receipt (Companion answers +OK or -ERR, so Accepted is a fact). The desk and its
/// decks in both directions: the deck drives the desk over the module, the desk drives the deck's
/// pages over this.
/// </summary>
public sealed class CompanionSession : ProfileSession
{
    private readonly string _defaultSurface;

    /// <param name="defaultSurface">The surface id a bare PAGE means — Companion's id for the Stream Deck, from its Surfaces page.</param>
    public CompanionSession(string defaultSurface) => _defaultSurface = (defaultSurface ?? "").Trim();

    public override DeviceProfile Profile => DeviceProfile.Companion;

    /// <summary>The words as Companion's own line, or null with the reason when they are not this profile's.</summary>
    public static string? Line(string words, string defaultSurface, out string problem)
    {
        problem = "";
        var w = (words ?? "").Trim();
        if (w.Length == 0)
        {
            problem = "Nothing to send — PAGE 3, PRESS 2/0/1, VAR speaker Jane Doe.";
            return null;
        }
        var sp = w.IndexOf(' ');
        var verb = (sp < 0 ? w : w[..sp]).ToUpperInvariant();
        var arg = sp < 0 ? "" : w[(sp + 1)..].Trim();
        switch (verb)
        {
            case "RAW":
                if (arg.Length == 0) { problem = "RAW wants Companion's line itself — RAW SURFACES RESCAN."; return null; }
                return arg;
            case "PAGE":
            {
                // PAGE 3 [surface] · PAGE UP [surface] · PAGE DOWN [surface]
                var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) { problem = "PAGE wants a page number, UP or DOWN — PAGE 3, PAGE UP."; return null; }
                var what = parts[0].ToUpperInvariant();
                var surface = parts.Length > 1 ? parts[1] : defaultSurface;
                if (surface.Length == 0) { problem = "PAGE wants a surface — type Companion's id for the Stream Deck (its Surfaces page) in the Surface box, or after the page: PAGE 3 streamdeck:abc123."; return null; }
                if (what is "UP" or "NEXT") return $"SURFACE {surface} PAGE-UP";
                if (what is "DOWN" or "PREV" or "PREVIOUS") return $"SURFACE {surface} PAGE-DOWN";
                if (!int.TryParse(what, out var page) || page < 1) { problem = $"'{parts[0]}' is not a page number, UP or DOWN."; return null; }
                return $"SURFACE {surface} PAGE-SET {page}";
            }
            case "PRESS":
            case "DOWN":
            case "UP":
            {
                var at = Location(arg, out var rest, out problem);
                if (at is null) return null;
                if (rest.Length > 0) { problem = $"{verb} wants the location alone — {verb} 2/0/1."; return null; }
                return $"LOCATION {at} {verb}";
            }
            case "ROTATE":
            {
                // ROTATE LEFT 2/0/1 · ROTATE RIGHT 2/0/1
                var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var dir = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
                if (dir is not ("LEFT" or "RIGHT")) { problem = "ROTATE wants LEFT or RIGHT and the location — ROTATE LEFT 2/0/1."; return null; }
                var at = Location(parts.Length > 1 ? parts[1] : "", out _, out problem);
                if (at is null) return null;
                return $"LOCATION {at} ROTATE-{dir}";
            }
            case "STEP":
            {
                var at = Location(arg, out var rest, out problem);
                if (at is null) return null;
                if (!int.TryParse(rest, out var step) || step < 1) { problem = "STEP wants the location and the step — STEP 2/0/1 2."; return null; }
                return $"LOCATION {at} SET-STEP {step}";
            }
            case "TEXT":
            {
                var at = Location(arg, out var rest, out problem);
                if (at is null) return null;
                return $"LOCATION {at} STYLE TEXT {rest}";
            }
            case "COLOR":
            case "COLOUR":
            case "BGCOLOR":
            case "BGCOLOUR":
            {
                var at = Location(arg, out var rest, out problem);
                if (at is null) return null;
                var colour = rest.Trim();
                if (colour.Length == 0) { problem = $"{verb} wants the location and a colour — {verb} 2/0/1 #ff8800."; return null; }
                return $"LOCATION {at} STYLE {(verb.StartsWith("BG", StringComparison.Ordinal) ? "BGCOLOR" : "COLOR")} {colour}";
            }
            case "VAR":
            case "VARIABLE":
            case "SET":
            {
                // VAR speaker Jane Doe — the rest of the line is the value, spaces and all.
                var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2) { problem = "VAR wants a custom variable's name and its value — VAR speaker Jane Doe."; return null; }
                return $"CUSTOM-VARIABLE {parts[0]} SET-VALUE {parts[1]}";
            }
            case "GET":
            case "VAR?":
                if (arg.Length == 0) { problem = "GET wants a custom variable's name — GET speaker."; return null; }
                return $"CUSTOM-VARIABLE {arg} GET-VALUE";
            case "RESCAN":
                return "SURFACES RESCAN";
            default:
                problem = $"'{w}' is not a Companion command — {DeviceProfiles.Words(DeviceProfile.Companion)}";
                return null;
        }
    }

    /// <summary>"2/0/1 the rest" → the location and what follows; a location is page/row/column, each a whole number.</summary>
    private static string? Location(string arg, out string rest, out string problem)
    {
        problem = "";
        rest = "";
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) { problem = "A location is page/row/column — 2/0/1."; return null; }
        var at = parts[0];
        var nums = at.Split('/');
        if (nums.Length != 3 || nums.Any(n => !int.TryParse(n, out var v) || v < 0)) { problem = $"'{at}' is not a location — page/row/column, as 2/0/1."; return null; }
        rest = parts.Length > 1 ? parts[1] : "";
        return at;
    }

    public override IReadOnlyList<byte[]> Encode(string words, out string problem)
    {
        var line = Line(words, _defaultSurface, out problem);
        return line is null ? Array.Empty<byte[]>() : One(line + "\n");
    }

    /// <summary>Companion answers every line: "+OK", "+OK value" for a get, "-ERR why" for a refusal — the oldest line waiting is the one answered.</summary>
    public override ProfileReply OnReceived(string text)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith("+OK", StringComparison.OrdinalIgnoreCase) || t.Equals("OK", StringComparison.OrdinalIgnoreCase) || t.StartsWith("OK ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = t.StartsWith("+", StringComparison.Ordinal) ? t[3..].Trim() : t.Length > 2 ? t[2..].Trim() : "";
            return ProfileReply.Acked(rest.Length > 0 ? $"OK — {rest}" : "OK");
        }
        if (t.StartsWith("-ERR", StringComparison.OrdinalIgnoreCase) || t.StartsWith("ERR", StringComparison.OrdinalIgnoreCase))
        {
            var rest = t.StartsWith("-", StringComparison.Ordinal) ? t[4..].Trim() : t.Length > 3 ? t[3..].Trim() : "";
            return ProfileReply.Refused(rest.Length > 0 ? $"Companion refused: {rest}" : "Companion refused");
        }
        return ProfileReply.Of(t);
    }
}
