using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>
/// The state remotes receive, as OSC: one message per fact under /patterns/state/…, built
/// from the same JSON the TCP STATE push carries, so every controller reads the same show.
/// Pure — a JSON string in, messages out; a field the JSON does not have is simply not sent.
/// </summary>
public static class OscFeedback
{
    public const string Prefix = "/patterns/state/";

    public static IReadOnlyList<OscMessage> FromState(string stateJson)
    {
        var list = new List<OscMessage>(32);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stateJson);
        }
        catch (JsonException)
        {
            return list;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return list;

            Flag(list, root, "live", "live");
            Flag(list, root, "blackout", "blackout");
            Text(list, root, "airLabel", "program");
            Flag(list, root, "duck", "duck");
            Flag(list, root, "tone", "tone");
            if (root.TryGetProperty("audio", out var audio)) Flag(list, audio, "playing", "audio");
            if (root.TryGetProperty("music", out var music))
            {
                Flag(list, music, "playing", "music");
                Text(list, music, "now", "music/now");
                if (music.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.Number) list.Add(OscMessage.Of(Prefix + "music/level", level.GetInt32()));
            }
            Text(list, root, "stingerPlaying", "stinger");
            Text(list, root, "stingHold", "stinger/hold");
            Text(list, root, "lowerThird", "lowerthird");
            Text(list, root, "lowerThirdPerson", "lowerthird/person");
            Text(list, root, "lowerThirdPreview", "lowerthird/preview");
            Text(list, root, "lowerThirdPreviewPerson", "lowerthird/preview/person");
            Text(list, root, "lowerThirdDefault", "lowerthird/default");
            Flag(list, root, "lowerThirdEdited", "lowerthird/edited");
            if (root.TryGetProperty("stream", out var stream)) Flag(list, stream, "active", "stream");
            Text(list, root, "playlist", "playlist");
            Text(list, root, "health", "health");
            Flag(list, root, "review", "review");
            Flag(list, root, "frozen", "freeze");
            Text(list, root, "previousLook", "look/previous");
            if (root.TryGetProperty("rev", out var rev) && rev.ValueKind == JsonValueKind.Number) list.Add(OscMessage.Of(Prefix + "rev", (int)(rev.GetInt64() & 0x7FFFFFFF)));

            if (root.TryGetProperty("screens", out var screens) && screens.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in screens.EnumerateArray())
                {
                    if (!s.TryGetProperty("n", out var n) || n.ValueKind != JsonValueKind.Number) continue;
                    var number = n.GetInt32();
                    list.Add(OscMessage.Of($"{Prefix}screen/{number}", Bit(s, "enabled")));
                    list.Add(OscMessage.Of($"{Prefix}lock/{number}", Bit(s, "locked")));
                }
            }

            if (root.TryGetProperty("cuestack", out var cue) && cue.ValueKind == JsonValueKind.Object)
            {
                Flag(list, cue, "armed", "cue/armed");
                Flag(list, cue, "hold", "cue/hold");
                Text(list, cue, "confirm", "cue/confirm");
                Row(list, cue, "standby", "cue/standby");
                Row(list, cue, "previous", "cue/previous");
                if (cue.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.Array && next.GetArrayLength() > 0)
                {
                    var first = next[0];
                    list.Add(OscMessage.Of(Prefix + "cue/next", Str(first, "number"), Str(first, "name")));
                }
                else
                {
                    list.Add(OscMessage.Of(Prefix + "cue/next", "", ""));
                }
                if (cue.TryGetProperty("last", out var last) && last.ValueKind == JsonValueKind.Object)
                {
                    list.Add(OscMessage.Of(Prefix + "cue/last", Str(last, "number"), Str(last, "outcome")));
                }
                if (cue.TryGetProperty("timing", out var timing) && timing.ValueKind == JsonValueKind.Object)
                {
                    Text(list, timing, "offset", "cue/offset");
                    Text(list, timing, "follow", "cue/follow");
                }
            }
        }
        return list;
    }

    private static void Flag(List<OscMessage> list, JsonElement e, string property, string address)
    {
        if (!e.TryGetProperty(property, out var v)) return;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) list.Add(OscMessage.Of(Prefix + address, v.GetBoolean() ? 1 : 0));
    }

    private static void Text(List<OscMessage> list, JsonElement e, string property, string address)
    {
        if (!e.TryGetProperty(property, out var v)) return;
        if (v.ValueKind == JsonValueKind.String) list.Add(OscMessage.Of(Prefix + address, v.GetString() ?? ""));
        else if (v.ValueKind == JsonValueKind.Null) list.Add(OscMessage.Of(Prefix + address, ""));
    }

    /// <summary>A cue row as two strings — the number and the name — or two empty strings when there is none.</summary>
    private static void Row(List<OscMessage> list, JsonElement e, string property, string address)
    {
        if (!e.TryGetProperty(property, out var v)) return;
        list.Add(v.ValueKind == JsonValueKind.Object
            ? OscMessage.Of(Prefix + address, Str(v, "number"), Str(v, "name"))
            : OscMessage.Of(Prefix + address, "", ""));
    }

    private static int Bit(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True ? 1 : 0;

    private static string Str(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
