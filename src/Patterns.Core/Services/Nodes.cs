using System.Net;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>The kinds as the beacon says them, and as the rail shows them.</summary>
public static class NodeKinds
{
    public static string Wire(NodeKind kind) => kind switch
    {
        NodeKind.Caller => "caller",
        NodeKind.Arcade => "arcade",
        NodeKind.Timer => "timer",
        _ => "desk",
    };

    /// <summary>A beacon's word for its kind; "" (a build before nodes) is a desk.</summary>
    public static NodeKind Parse(string? word) => (word ?? "").Trim().ToLowerInvariant() switch
    {
        "caller" => NodeKind.Caller,
        "arcade" => NodeKind.Arcade,
        "timer" => NodeKind.Timer,
        _ => NodeKind.Desk,
    };

    public static NodeKind? ParseLaunch(string? word) => (word ?? "").Trim().ToLowerInvariant() switch
    {
        "" => null,
        "caller" => NodeKind.Caller,
        "arcade" => NodeKind.Arcade,
        "timer" => NodeKind.Timer,
        "desk" => NodeKind.Desk,
        _ => null,
    };

    public static string Label(NodeKind kind) => kind switch
    {
        NodeKind.Caller => "Caller",
        NodeKind.Arcade => "Arcade",
        NodeKind.Timer => "Stage timer",
        _ => "Desk",
    };

    /// <summary>The words the outputs' hold carries on a node: a node never opens a screen.</summary>
    public static string HoldWords(NodeKind kind) => kind switch
    {
        NodeKind.Caller => "this is a caller node — it plans and calls the show, and never opens outputs",
        NodeKind.Arcade => "this is the arcade node — its picture reaches the wall as a source, never as an output",
        NodeKind.Timer => "this is a stage timer node — its display is a page, never an output",
        _ => "",
    };

    /// <summary>The pages a kind's window shows — a node's few, in its window's order; null = every page, the desk's.</summary>
    public static IReadOnlyCollection<string>? Pages(NodeKind kind) => kind switch
    {
        NodeKind.Caller => new[] { "Run", "Cues", "Stage", "Nodes" },
        NodeKind.Timer => new[] { "Stage", "Nodes" },
        NodeKind.Arcade => new[] { "Arcade", "Nodes" },
        _ => null,
    };
}

/// <summary>One process heard on the beacon, as the Nodes page shows it.</summary>
/// <param name="Instance">The beacon's per-process id.</param>
/// <param name="Kind">Desk, caller, arcade, timer.</param>
/// <param name="Name">The machine as it names itself.</param>
/// <param name="Address">Where it was heard from.</param>
/// <param name="Show">The show it has open.</param>
/// <param name="Words">Its own health line.</param>
/// <param name="LinkPort">The port a caller may link on; 0 when none.</param>
/// <param name="HttpPort">Its pages; 0 when none.</param>
/// <param name="Age">Since it was last heard.</param>
/// <param name="Live">Its outputs are open.</param>
public sealed record NodeCard(string Instance, NodeKind Kind, string Name, IPAddress? Address, string Show, string Words, int LinkPort, int HttpPort, TimeSpan Age, bool Live, int WirePort = 0)
{
    /// <summary>Heard within the last few beats.</summary>
    public bool Fresh => Age < NodeRegistry.StaleAfter;

    public string KindLabel => NodeKinds.Label(Kind);

    /// <summary>"http://10.0.0.12:9696/" — its pages, or "" when it serves none.</summary>
    public string PagesUrl => Address is null || HttpPort <= 0 ? "" : $"http://{Address}:{HttpPort}/";

    /// <summary>"Desk FOH-PC — Gala · live · heard just now".</summary>
    public string Line
    {
        get
        {
            var age = Age.TotalSeconds < 2 ? "heard just now" : Age.TotalSeconds < 60 ? $"heard {Age.TotalSeconds:0} s ago" : $"heard {Age.TotalMinutes:0} min ago";
            var show = Show.Length > 0 ? $" — {Show}" : "";
            return $"{KindLabel} {Name}{show}{(Live ? " · live" : "")} · {age}{(Fresh ? "" : " · GONE?")}";
        }
    }
}

/// <summary>
/// Every process heard on the beacon, kept by instance, and what the rail says about them. Pure:
/// the beacons and the clock go in, the cards come out, so the words are the same on every desk
/// and in the tests.
/// </summary>
public static class NodeRegistry
{
    /// <summary>Five missed beacons: the process is gone or the cable is.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(6);

    /// <summary>Forgotten after this, so a machine switched off leaves the page.</summary>
    public static readonly TimeSpan ForgottenAfter = TimeSpan.FromMinutes(2);

    public static NodeCard Card(Beacon beacon, IPAddress? from, DateTime heardUtc, DateTime utcNow)
        => new(beacon.Instance, NodeKinds.Parse(beacon.Kind), beacon.Machine, from, beacon.Show, beacon.Health, beacon.Link, beacon.Http, utcNow - heardUtc, beacon.Live, beacon.Wire);

    /// <summary>The cards in rail order: desks first, then callers, arcades, timers; each kind by name.</summary>
    public static IReadOnlyList<NodeCard> Order(IEnumerable<NodeCard> cards)
        => cards.Where(c => c.Age < ForgottenAfter).OrderBy(c => c.Kind).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The rail's one word: "NONE", "2 NEAR", "1 LINKED".</summary>
    public static string RailWord(int fresh, int linked)
        => linked > 0 ? $"{linked} LINKED" : fresh > 0 ? $"{fresh} NEAR" : "NONE";

    /// <summary>The rail's line: what is near and what is linked.</summary>
    public static string RailLine(IReadOnlyList<NodeCard> cards, int linked)
    {
        var fresh = cards.Where(c => c.Fresh).ToList();
        if (fresh.Count == 0) return "No other Patterns heard on the beacon — a desk, a caller node or an arcade on this network would be listed here.";
        var kinds = fresh.GroupBy(c => c.Kind).Select(g => $"{g.Count()} {NodeKinds.Label(g.Key).ToLowerInvariant()}{(g.Count() == 1 ? "" : "s")}");
        return $"{string.Join(", ", kinds)} on the beacon" + (linked > 0 ? $" · {linked} linked to this desk" : "") + ".";
    }
}
