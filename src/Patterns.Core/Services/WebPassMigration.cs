using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// What a load's scrub did: the values it rewrote (<see cref="Places"/>), the private addresses it kept on this machine
/// (<see cref="Kept"/>, each address once however many places name it) and the private addresses it could not keep
/// (<see cref="Unkept"/>: a full book, a book that answered nothing — held unkept, black until pasted again).
/// </summary>
public readonly record struct WebPassScrub(int Places, int Kept, int Unkept)
{
    /// <summary>True when the scrub took a pass out of the show: the load writes its journal row.</summary>
    public bool Any => Kept + Unkept > 0;
}

/// <summary>
/// Round 89: a scrub of the passes already written into a show. A show saved before the round, a hand edit or an
/// older peer's show can hold a private page address raw; on load, every place a show names a page — each picture's
/// page and its layers', the Remote &amp; web page, the saved pages, the web input nicknames' keys, the cues' page steps,
/// the devices' command lines and every saved look's captured JSON — is rewritten to the address the show holds
/// instead (<see cref="WindowAddresses.Secure"/>), and one journal row says what was kept and what could not be. Only
/// a page's address is touched: another input's key (an NDI source, a capture device), a nickname and a line of words
/// stay as they are, byte for byte, whatever an <c>@</c> in them looks like. Not a compatibility path: the rewritten
/// show is the only show from then on, and no reader ever sees the raw form again. Pure: the book is the caller's,
/// reached through the function it hands in.
/// </summary>
public static class WebPassMigration
{
    /// <summary>The journal row's kind.</summary>
    public const string RowKind = "WebPassesKept";

    private static readonly Regex KeptAddress = new(
        "kept://[^\\s\"'/\\\\]+/([a-z2-7]{12})(?![a-z2-7])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, SafeRegex.Timeout);

    /// <summary>
    /// Rewrites every private address the show holds to what <paramref name="secure"/> answers for it (the kept
    /// address; an answer that is not one is taken as unkept, so no pass survives) and every held address out of its
    /// own form to that form; answers what changed. Addresses that are not private are left exactly as they are.
    /// </summary>
    public static WebPassScrub Rewrite(ShowState state, Func<string, string> secure)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(secure);
        var places = 0;
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var unkept = new HashSet<string>(StringComparer.Ordinal);

        // A value a hand-edited file left null or empty stays as it is.
        string Address(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var trimmed = value.Trim();
            if (WindowAddresses.IsHeld(trimmed))
            {
                var canonical = WindowAddresses.Held(trimmed);
                if (canonical == trimmed) return value;
                places++;
                if (!WindowAddresses.IsKept(canonical)) unkept.Add(trimmed);       // a held prefix over a pass, or no id: black until pasted again
                return canonical;
            }
            if (!WindowAddresses.LooksPrivate(value)) return value;
            var held = WindowAddresses.Secure(value, secure);
            places++;
            if (WindowAddresses.IsKept(held)) kept.Add(held);
            else unkept.Add(trimmed);
            return held;
        }

        // A web input's key carries its address after "web:"; every other input's key (ndi:, cap:, vid:) names no page.
        string Key(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("web:", StringComparison.OrdinalIgnoreCase)) return value;
            var address = value[4..];
            var held = Address(address);
            return ReferenceEquals(held, address) ? value : value[..4] + held;
        }

        // Free text: an address its scheme names, then one typed bare (a host, then a window's path or a pass word).
        string Text(string value) => string.IsNullOrEmpty(value) ? value : WindowAddresses.RewriteBareText(WindowAddresses.RewriteText(value, Address), Address);

        // A page step's target is a key, an address, a nickname, a host or a word: only a key, an address with its
        // scheme, or one word that is a private address beyond doubt is a page's address; anything else is read as text.
        string PageTarget(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var t = value.Trim();
            if (t.StartsWith("web:", StringComparison.OrdinalIgnoreCase))
            {
                var key = Key(t);
                return ReferenceEquals(key, t) ? value : key;
            }
            if (t.Length > 0 && !t.Any(char.IsWhiteSpace)
                && (t.Contains("://", StringComparison.Ordinal) || WindowAddresses.LooksPrivateBare(t, userWithPassword: true))) return Address(value);
            return Text(value);
        }

        foreach (var pattern in PatternsOf(state))
        {
            pattern.Media.WebUrl = Address(pattern.Media.WebUrl);
            pattern.Layer1.WebUrl = Address(pattern.Layer1.WebUrl);
            pattern.Layer2.WebUrl = Address(pattern.Layer2.WebUrl);
        }

        state.Web.Url = Address(state.Web.Url);
        for (var i = state.Web.SavedUrls.Count - 1; i >= 0; i--)
        {
            var saved = Address(state.Web.SavedUrls[i]);
            if (saved == state.Web.SavedUrls[i]) continue;
            // Two pastes of one address keep one entry: the second saved copy goes once both name the same kept address.
            if (state.Web.SavedUrls.Where((u, at) => at != i).Contains(saved, StringComparer.Ordinal)) state.Web.SavedUrls.RemoveAt(i);
            else state.Web.SavedUrls[i] = saved;
        }

        foreach (var label in state.InputLabels) label.Key = Key(label.Key);

        foreach (var stack in state.Stacks)
        {
            foreach (var cue in stack.Cues)
            {
                foreach (var step in cue.Actions)
                {
                    step.Target = IsPageStep(step.Kind) ? PageTarget(step.Target) : Text(step.Target);
                    step.Value = step.Kind == ShowActionKind.WebOpen ? Address(step.Value) : Text(step.Value);
                }
            }
        }

        foreach (var device in state.Interactive.Devices)
        {
            foreach (var trigger in device.Triggers) trigger.Command = Text(trigger.Command);
        }

        foreach (var look in state.LooksAndCues.Looks)
        {
            look.Json = RewriteJson(look.Json, Address, Text);
        }
        return new WebPassScrub(places, kept.Count, unkept.Count);
    }

    /// <summary>The kept ids a show names anywhere — its pictures, its looks, its cues — so a full book drops none of them.</summary>
    public static IReadOnlySet<string> KeptIdsIn(ShowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in KeptAddress.Matches(JsonUtil.SerializeCompact(state))) ids.Add(m.Groups[1].Value.ToLowerInvariant());
        return ids;
    }

    /// <summary>
    /// The journal row's words for a load's scrub: how many private addresses it kept on this machine, how many it could
    /// not keep (they show black until pasted again), and where older copies of the passes stay.
    /// </summary>
    public static string Words(WebPassScrub scrub)
    {
        var words = $"kept {scrub.Kept} private page address{(scrub.Kept == 1 ? "" : "es")} on this machine (Stagewatch windows and keyed links)";
        if (scrub.Unkept > 0) words += $"; {scrub.Unkept} could not be kept and show{(scrub.Unkept == 1 ? "s" : "")} black — paste {(scrub.Unkept == 1 ? "it" : "them")} again";
        return words + "; older copies in backups/ and the log keep them on this machine — the support bundle hides them";
    }

    /// <summary>The one journal row a load that took a pass out of the show writes: Done, or DoneWithWarnings when an address could not be kept.</summary>
    public static ShowLogEntry Row(WebPassScrub scrub, DateTime atUtc)
        => new(atUtc, "Desk", RowKind, "", scrub.Unkept > 0 ? "DoneWithWarnings" : "Done", Words(scrub));

    /// <summary>The steps that name a page in their target (WebOpen's value is the address its browser is sent to).</summary>
    private static bool IsPageStep(ShowActionKind kind)
        => kind is ShowActionKind.WebKey or ShowActionKind.WebClick or ShowActionKind.WebType or ShowActionKind.WebReload
            or ShowActionKind.WebOpen or ShowActionKind.WebArm or ShowActionKind.WebMark;

    private static IEnumerable<PatternConfig> PatternsOf(ShowState state)
    {
        yield return state.Pattern;
        foreach (var a in state.Independent) yield return a.Pattern;
    }

    /// <summary>
    /// A look's captured JSON with its private addresses rewritten: a value named like an address (…Url) as an address,
    /// every other string as text; written back in the shape a look is captured in, and only when something changed.
    /// </summary>
    private static string RewriteJson(string json, Func<string, string> address, Func<string, string> text)
    {
        if (string.IsNullOrWhiteSpace(json) || !LooksLikeItHolds(json)) return json;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // Not JSON: the text itself is rewritten, so a broken look still carries no pass.
            return text(json);
        }
        if (root is null) return json;
        var changed = false;
        Walk(root, "");
        return changed ? root.ToJsonString(JsonUtil.Options) : json;

        void Walk(JsonNode node, string name)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToList())
                    {
                        if (o[key] is { } child) Walk(child, key);
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a.ToList())
                    {
                        if (item is not null) Walk(item, name);
                    }
                    break;
                case JsonValue v when v.TryGetValue<string>(out var s):
                    var rewritten = name.EndsWith("Url", StringComparison.Ordinal) ? address(s) : text(s);
                    if (rewritten != s)
                    {
                        ReplaceValue(v, rewritten);
                        changed = true;
                    }
                    break;
            }
        }
    }

    private static void ReplaceValue(JsonValue value, string replacement)
    {
        switch (value.Parent)
        {
            case JsonObject o:
                o[value.GetPropertyName()] = replacement;
                break;
            case JsonArray a:
                a[value.GetElementIndex()] = replacement;
                break;
        }
    }

    /// <summary>
    /// A cheap look before the parse: a pass needs a scheme's "://", a window's "/w/", a user's "@" or a query's "=" —
    /// a look with none of them holds no private address, and is not parsed.
    /// </summary>
    private static bool LooksLikeItHolds(string json)
        => json.Contains("://", StringComparison.Ordinal) || json.Contains("/w/", StringComparison.OrdinalIgnoreCase)
           || json.Contains('@', StringComparison.Ordinal) || json.Contains('=', StringComparison.Ordinal) || json.Contains("\\u003", StringComparison.OrdinalIgnoreCase);
}
