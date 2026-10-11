using System.Text.Json;
using System.Text.Json.Serialization;

namespace Patterns.Core.Services;

/// <summary>Round 87: who a context is from — its kind on the show network, its id, its name.</summary>
public sealed record ShowContextNode
{
    [JsonPropertyName("t")] public string Kind { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
}

/// <summary>One thing of a node, in the Eye's words: what it is, what it is called, its light and its state.</summary>
public sealed record ShowContextItem
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("light")] public string Light { get; init; } = "grey";
    [JsonPropertyName("state")] public string State { get; init; } = "";
}

/// <summary>One link between two of a node's things, with the word between them and its light.</summary>
public sealed record ShowContextLink
{
    [JsonPropertyName("from")] public string From { get; init; } = "";
    [JsonPropertyName("to")] public string To { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("light")] public string Light { get; init; } = "grey";
}

/// <summary>A journal line in plain words, at a Unix millisecond.</summary>
public sealed record ShowContextLine
{
    [JsonPropertyName("at")] public long At { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = "";
}

/// <summary>A verb a peer (or an assistant) may propose, with its risk — "high" is never pre-ticked by any assistant.</summary>
public sealed record ShowContextCommand
{
    [JsonPropertyName("verb")] public string Verb { get; init; } = "";
    [JsonPropertyName("risk")] public string Risk { get; init; } = "review";
    [JsonPropertyName("words")] public string Words { get; init; } = "";
}

/// <summary>A peer one hop away, as the node holding the context sees it: its light and headline, never its peers.</summary>
public sealed record ShowContextPeer
{
    [JsonPropertyName("t")] public string Kind { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("light")] public string Light { get; init; } = "grey";
    [JsonPropertyName("headline")] public string Headline { get; init; } = "";
}

/// <summary>
/// Round 87 (Show Bus 1.1, docs/SHOWBUS.md §6): what any app's assistant — or another app's Eye — needs to understand
/// a node: its light, its headline computed from the facts (no model), its things and their links in the Eye's
/// vocabulary, its last journal lines, the verbs a peer may propose with their risk, and the peers it hears one hop
/// away. The same shape from Stagewatch, Patterns and AGMessage; no secrets and no private content in any of them.
/// </summary>
public sealed record ShowContext
{
    public const int Version = 1;
    public const int MaxRecent = 20;
    public const int MaxItems = 200;

    [JsonPropertyName("v")] public int V { get; init; } = Version;
    [JsonPropertyName("node")] public ShowContextNode Node { get; init; } = new();
    [JsonPropertyName("at")] public long At { get; init; }
    [JsonPropertyName("light")] public string Light { get; init; } = "grey";
    [JsonPropertyName("headline")] public string Headline { get; init; } = "";
    [JsonPropertyName("items")] public IReadOnlyList<ShowContextItem> Items { get; init; } = Array.Empty<ShowContextItem>();
    [JsonPropertyName("links")] public IReadOnlyList<ShowContextLink> Links { get; init; } = Array.Empty<ShowContextLink>();
    [JsonPropertyName("recent")] public IReadOnlyList<ShowContextLine> Recent { get; init; } = Array.Empty<ShowContextLine>();
    [JsonPropertyName("commands")] public IReadOnlyList<ShowContextCommand> Commands { get; init; } = Array.Empty<ShowContextCommand>();
    [JsonPropertyName("peers")] public IReadOnlyList<ShowContextPeer> Peers { get; init; } = Array.Empty<ShowContextPeer>();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>A context another app sent, or null when it is not one. Bounded: a peer cannot hand the Eye ten thousand nodes.</summary>
    public static ShowContext? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 512 * 1024) return null;
        try
        {
            var c = JsonSerializer.Deserialize<ShowContext>(json, Options);
            if (c is null || c.Node is null || c.Node.Kind.Length == 0) return null;
            return c with
            {
                Light = NormalLight(c.Light),
                Items = (c.Items ?? Array.Empty<ShowContextItem>()).Where(i => i is not null && i.Id.Length > 0).Take(MaxItems).Select(i => i with { Light = NormalLight(i.Light) }).ToList(),
                Links = (c.Links ?? Array.Empty<ShowContextLink>()).Where(l => l is not null).Take(MaxItems * 2).Select(l => l with { Light = NormalLight(l.Light) }).ToList(),
                Recent = (c.Recent ?? Array.Empty<ShowContextLine>()).Where(r => r is not null).TakeLast(MaxRecent).ToList(),
                Commands = (c.Commands ?? Array.Empty<ShowContextCommand>()).Where(x => x is not null).Take(64).ToList(),
                Peers = (c.Peers ?? Array.Empty<ShowContextPeer>()).Where(x => x is not null).Take(32).Select(x => x with { Light = NormalLight(x.Light) }).ToList(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"red", "amber", "green" or "grey" — anything else is unknown, and unknown is grey.</summary>
    public static string NormalLight(string? light) => (light ?? "").Trim().ToLowerInvariant() switch
    {
        "red" => "red",
        "amber" or "yellow" or "orange" => "amber",
        "green" => "green",
        _ => "grey",
    };

    public static string Word(CheckLight light) => light switch
    {
        CheckLight.Red => "red",
        CheckLight.Amber => "amber",
        CheckLight.Green => "green",
        _ => "grey",
    };

    public static CheckLight LightOf(string? word) => NormalLight(word) switch
    {
        "red" => CheckLight.Red,
        "amber" => CheckLight.Amber,
        "green" => CheckLight.Green,
        _ => CheckLight.Grey,
    };

    /// <summary>The worst of some lights: red over amber over green over grey (unknown says nothing bad or good).</summary>
    public static CheckLight Worst(IEnumerable<CheckLight> lights)
    {
        var worst = CheckLight.Grey;
        foreach (var l in lights)
        {
            if (l == CheckLight.Red) return CheckLight.Red;
            if (l == CheckLight.Amber) worst = CheckLight.Amber;
            else if (l == CheckLight.Green && worst == CheckLight.Grey) worst = CheckLight.Green;
        }
        return worst;
    }
}

/// <summary>
/// Round 87: this desk's context from the Eye — the same picture the operator sees, in Show Bus 1.1's shape. The
/// headline is the Eye's; the light the worst of the desk's own things; the items and links the Eye's nodes and
/// edges (another app's things drawn on this Eye are left out — they are that app's to say, and its peers row says
/// it). Pure: the graph, the journal rows and the peers are handed in.
/// </summary>
public static class ShowContextBuilder
{
    /// <summary>The verbs a peer or an assistant may propose to this desk, with their risk (docs/SHOWBUS.md §6).</summary>
    public static readonly IReadOnlyList<ShowContextCommand> Commands = new[]
    {
        new ShowContextCommand { Verb = "STATUS", Risk = "low", Words = "read the desk's state" },
        new ShowContextCommand { Verb = "EYE", Risk = "low", Words = "read the God's Eye: every thing, its light and its links" },
        new ShowContextCommand { Verb = "EYE FOCUS <thing>", Risk = "low", Words = "move the operator's Eye to a thing (the desk's view only)" },
        new ShowContextCommand { Verb = "STAGE MESSAGE <words>", Risk = "review", Words = "words to the speaker's display, with a receipt" },
        new ShowContextCommand { Verb = "STAGE CREW <words>", Risk = "review", Words = "words to the crew's display, with a receipt" },
        new ShowContextCommand { Verb = "VIDEO END <seconds>", Risk = "high", Words = "jump the clip on air to that long before its end" },
        new ShowContextCommand { Verb = "VIDEO PLAY", Risk = "high", Words = "play the clip on air" },
        new ShowContextCommand { Verb = "VIDEO PAUSE", Risk = "high", Words = "hold the clip on air" },
        new ShowContextCommand { Verb = "VIDEO SEEK <m:ss>", Risk = "high", Words = "move the clip on air to a point" },
        new ShowContextCommand { Verb = "VIDEO RESTART", Risk = "high", Words = "the clip on air from the top" },
        new ShowContextCommand { Verb = "LOOK <name>", Risk = "high", Words = "put a saved look on air" },
        new ShowContextCommand { Verb = "CUE GO", Risk = "high", Words = "fire the standby cue" },
        new ShowContextCommand { Verb = "BLACKOUT ON", Risk = "high", Words = "every screen to black" },
        new ShowContextCommand { Verb = "BLACKOUT OFF", Risk = "high", Words = "the pictures back" },
    };

    /// <summary>The Eye's kinds that are another app's things, drawn here from its own context.</summary>
    public static bool IsBorrowed(EyeKind kind) => kind is EyeKind.ShowApp or EyeKind.ShowAppItem;

    public static ShowContext FromEye(EyeGraph graph, string id, string name, DateTime utcNow, IEnumerable<ShowLogEntry> journal, IEnumerable<ShowContextPeer> peers)
    {
        var own = graph.Nodes.Where(n => !IsBorrowed(n.Kind)).Take(ShowContext.MaxItems).ToList();
        var ids = own.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        return new ShowContext
        {
            Node = new ShowContextNode { Kind = "patterns", Id = id, Name = name },
            At = ClockPing.Ms(utcNow),
            Light = ShowContext.Word(ShowContext.Worst(own.Select(n => n.Light))),
            Headline = graph.Headline,
            Items = own.Select(n => new ShowContextItem { Id = n.Id, Kind = n.Kind.ToString().ToLowerInvariant(), Label = n.Label, Light = ShowContext.Word(n.Light), State = n.Sub }).ToList(),
            Links = graph.Edges.Where(e => ids.Contains(e.From) && ids.Contains(e.To))
                .Select(e => new ShowContextLink { From = e.From, To = e.To, Kind = e.Verb, Light = ShowContext.Word(e.Light) }).ToList(),
            Recent = journal.TakeLast(ShowContext.MaxRecent).Select(Line).ToList(),
            Commands = Commands,
            Peers = peers.Take(32).ToList(),
        };
    }

    /// <summary>A journal row as one line of words — who, what, the outcome; never a secret (the journal holds none since round 75).</summary>
    public static ShowContextLine Line(ShowLogEntry e)
    {
        var what = string.Join(" ", new[] { e.Kind, e.Target }.Where(w => !string.IsNullOrWhiteSpace(w)));
        var text = $"{what} — {e.Outcome}" + (string.IsNullOrWhiteSpace(e.Message) ? "" : ": " + e.Message) + (string.IsNullOrWhiteSpace(e.Origin) ? "" : $" ({e.Origin})");
        return new ShowContextLine { At = ClockPing.Ms(e.AtUtc), Text = text.Length > 240 ? text[..240] : text };
    }
}
