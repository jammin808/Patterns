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
            Flag(list, root, "editSafe", "editsafe");
            Text(list, root, "previousLook", "look/previous");
            Text(list, root, "airLook", "look/air");
            Text(list, root, "previewLook", "look/preview");
            Text(list, root, "pattern", "pattern");
            if (root.TryGetProperty("rev", out var rev) && rev.ValueKind == JsonValueKind.Number) list.Add(OscMessage.Of(Prefix + "rev", (int)(rev.GetInt64() & 0x7FFFFFFF)));

            // The show's lists by number — what a bank of keys reads to label itself: /looks/3 "Walk-in" …
            Names(list, root, "looks", "looks", 16, withAir: true);
            Names(list, root, "lowerThirds", "lowerthirds", 8);
            Names(list, root, "people", "people", 8);
            Names(list, root, "stingers", "stingers", 8);
            Names(list, root, "sections", "sections", 6);
            if (root.TryGetProperty("music", out var musicList) && musicList.ValueKind == JsonValueKind.Object) Names(list, musicList, "items", "music/items", 6);

            if (root.TryGetProperty("deck", out var deck))
            {
                if (deck.ValueKind == JsonValueKind.Object)
                {
                    list.Add(OscMessage.Of(Prefix + "deck/page", Int(deck, "page")));
                    list.Add(OscMessage.Of(Prefix + "deck/count", Int(deck, "count")));
                    list.Add(OscMessage.Of(Prefix + "deck/ended", Bit(deck, "ended")));
                    list.Add(OscMessage.Of(Prefix + "deck/file", Str(deck, "file")));
                }
                else
                {
                    list.Add(OscMessage.Of(Prefix + "deck/page", 0));
                    list.Add(OscMessage.Of(Prefix + "deck/count", 0));
                    list.Add(OscMessage.Of(Prefix + "deck/ended", 0));
                    list.Add(OscMessage.Of(Prefix + "deck/file", ""));
                }
            }
            // The install: the schedule's switch, the programme on, the override on and until when, the next change.
            if (root.TryGetProperty("install", out var install) && install.ValueKind == JsonValueKind.Object)
            {
                list.Add(OscMessage.Of(Prefix + "install/on", Bit(install, "on")));
                list.Add(OscMessage.Of(Prefix + "install/programme", Str(install, "programme")));
                list.Add(OscMessage.Of(Prefix + "install/over", Str(install, "over")));
                list.Add(OscMessage.Of(Prefix + "install/until", Str(install, "overUntil")));
                list.Add(OscMessage.Of(Prefix + "install/next", Str(install, "next")));
            }
            if (root.TryGetProperty("web", out var web))
            {
                var isPage = web.ValueKind == JsonValueKind.Object;
                list.Add(OscMessage.Of(Prefix + "web/page", isPage ? Str(web, "page") : ""));
                list.Add(OscMessage.Of(Prefix + "web/service", isPage ? Str(web, "service") : ""));
            }

            if (root.TryGetProperty("screens", out var screens) && screens.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in screens.EnumerateArray())
                {
                    if (!s.TryGetProperty("n", out var n) || n.ValueKind != JsonValueKind.Number) continue;
                    var number = n.GetInt32();
                    list.Add(OscMessage.Of($"{Prefix}screen/{number}", Bit(s, "enabled")));
                    list.Add(OscMessage.Of($"{Prefix}lock/{number}", Bit(s, "locked")));
                    if (s.TryGetProperty("armed", out _)) list.Add(OscMessage.Of($"{Prefix}armed/{number}", Bit(s, "armed")));
                    if (s.TryGetProperty("label", out _)) list.Add(OscMessage.Of($"{Prefix}screen/{number}/name", Str(s, "label")));
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
                    // The cues after the standby, by place — a bank of keys: /cue/next/1 "01.030" "Coffee" …
                    var k = 0;
                    foreach (var row in next.EnumerateArray())
                    {
                        if (++k > 6) break;
                        list.Add(OscMessage.Of($"{Prefix}cue/next/{k}", Str(row, "number"), Str(row, "name")));
                    }
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

    /// <summary>A list's names by number: /address/1 "name" … up to <paramref name="max"/>, and /address/n/air 1|0 when asked; a shorter list sends "" for the rest, so a key that lost its item goes blank.</summary>
    private static void Names(List<OscMessage> list, JsonElement e, string property, string address, int max, bool withAir = false)
    {
        if (!e.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array) return;
        var n = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (++n > max) break;
            list.Add(OscMessage.Of($"{Prefix}{address}/{n}", Str(item, "name")));
            if (withAir) list.Add(OscMessage.Of($"{Prefix}{address}/{n}/air", Bit(item, "air")));
        }
        for (var blank = n + 1; blank <= max; blank++)
        {
            list.Add(OscMessage.Of($"{Prefix}{address}/{blank}", ""));
            if (withAir) list.Add(OscMessage.Of($"{Prefix}{address}/{blank}/air", 0));
        }
    }

    private static int Int(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static int Bit(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True ? 1 : 0;

    private static string Str(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
