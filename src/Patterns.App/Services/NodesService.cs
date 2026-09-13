using System.Collections.ObjectModel;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Every other Patterns on the network as the beacon tells it — desks, caller nodes, arcades,
/// stage timers — kept as cards the Nodes page shows and the rail counts. Read once a second from
/// the desk's poll; the cards are replaced only when something moved, so the page never flickers.
/// </summary>
public sealed class NodesService
{
    private readonly AppServices _s;
    private string _seen = "";

    public NodesService(AppServices services) => _s = services;

    /// <summary>The cards, in rail order, refreshed in place.</summary>
    public ObservableCollection<NodeCard> Nodes { get; } = new();

    /// <summary>Heard within the last few beats.</summary>
    public int Fresh { get; private set; }

    /// <summary>Caller nodes linked to this desk right now.</summary>
    public int Linked => _s.Twin.CallerCount;

    public string RailWord => NodeRegistry.RailWord(Fresh, Linked);

    public string RailLine => NodeRegistry.RailLine(Nodes, Linked);

    /// <summary>Teal when something is near or linked, dark when the network is quiet.</summary>
    public string Hue => Linked > 0 ? "#35E0D0" : Fresh > 0 ? "#5FD0FF" : "#4A505E";

    /// <summary>This process's own card words: what it is and how it is found.</summary>
    public string Identity
    {
        get
        {
            var s = _s.State;
            var kind = NodeKinds.Label(_s.Profile);
            var link = _s.Twin.LinkPort;
            return $"{kind} {_s.Beacon.MachineName} — instance {_s.Beacon.Instance}"
                   + (link > 0 ? $" · callers may link on port {link}" : " · not linking callers")
                   + (s.Control.Enabled ? $" · pages on port {s.Control.HttpPort}" : "");
        }
    }

    /// <summary>From the desk's poll, once a second: the beacons heard onto the cards.</summary>
    public void Poll()
    {
        var now = DateTime.UtcNow;
        var cards = NodeRegistry.Order(_s.Beacon.Peers().Select(p => NodeRegistry.Card(p.Beacon, p.From.Address, p.HeardUtc, now)));
        Fresh = cards.Count(c => c.Fresh);
        var key = string.Join("|", cards.Select(c => $"{c.Instance}:{c.Kind}:{c.Name}:{c.Show}:{c.Live}:{c.Fresh}:{c.LinkPort}:{c.HttpPort}:{(int)(c.Age.TotalSeconds / 5)}"));
        if (key == _seen) return;
        _seen = key;
        Nodes.Clear();
        foreach (var c in cards) Nodes.Add(c);
    }

    /// <summary>NODES on the wire: every card as JSON.</summary>
    public string StatusJson()
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            me = new { kind = NodeKinds.Wire(_s.Profile), machine = _s.Beacon.MachineName, instance = _s.Beacon.Instance, link = _s.Twin.LinkPort },
            linked = Linked,
            nodes = Nodes.Select(c => new { instance = c.Instance, kind = NodeKinds.Wire(c.Kind), name = c.Name, address = c.Address?.ToString() ?? "", show = c.Show, live = c.Live, fresh = c.Fresh, link = c.LinkPort, http = c.HttpPort, heardSecondsAgo = Math.Round(c.Age.TotalSeconds) }).ToArray(),
        });
}
