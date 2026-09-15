using Patterns.Devices;
using Avalonia.Threading;
using System.Text.Json;
using System.Text;
using System.Net.Sockets;
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
    private readonly ServiceKernel _s;
    private string _seen = "";

    public NodesService(ServiceKernel kernel) => _s = kernel;

    /// <summary>The cards, in rail order, refreshed in place.</summary>
    public ObservableCollection<NodeCard> Nodes { get; } = new();

    /// <summary>Heard within the last few beats.</summary>
    public int Fresh { get; private set; }

    /// <summary>Caller nodes linked to this desk right now.</summary>
    public int Linked => _s.Link.CallerCount;

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
            var link = _s.Link.LinkPort;
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
        Rev++;
        Nodes.Clear();
        foreach (var c in cards) Nodes.Add(c);
    }

    /// <summary>Moves when the cards change — a deck's node keys are pushed on it.</summary>
    public long Rev { get; private set; }

    /// <summary>The cards as a deck's bank keys read them: by place, with the kind, the machine, whether it is heard now, and its own words.</summary>
    public object[] Rows() => Nodes.Select((c, i) => (object)new
    {
        n = i + 1,
        instance = c.Instance,
        kind = NodeKinds.Wire(c.Kind),
        name = c.Name,
        show = c.Show,
        live = c.Live,
        fresh = c.Fresh,
        words = c.Words,
        address = c.Address?.ToString() ?? "",
        http = c.HttpPort,
        link = c.LinkPort,
        heardSecondsAgo = Math.Round(c.Age.TotalSeconds),
    }).ToArray();

    /// <summary>NODES on the wire: every card as JSON.</summary>
    /// <summary>The desks heard lately with a wire — where a node finds the assistant's one key.</summary>
    public IReadOnlyList<NodeCard> Desks() => Nodes.Where(c => c.Kind == NodeKind.Desk && c.Fresh && c.WirePort > 0 && c.Address is not null).ToList();

    /// <summary>One line to one node's wire, its reply back — a desk's assistant asked, a hub told.</summary>
    public static Task<string> AskNodeAsync(NodeCard card, string line) => AskAsync(card, line);

    /// <summary>The arcade nodes heard lately, with a wire to speak to.</summary>
    public IReadOnlyList<NodeCard> Arcades() => Nodes.Where(c => c.Kind == NodeKind.Arcade && c.Fresh && c.WirePort > 0 && c.Address is not null).ToList();

    /// <summary>
    /// An arcade verb from this desk to every arcade node it hears, on their wires — a cue, a Stream
    /// Deck key, the assistant. Sent, not awaited: the line is the desk's, a refusal comes back as a
    /// status line. No arcade heard is a refusal that says what to start.
    /// </summary>
    public ActionResult SendToArcades(string line)
    {
        var arcades = Arcades();
        if (arcades.Count == 0) return ActionResult.Refused("No arcade node heard — start Patterns.exe --node arcade on the hub PC, with its Remote page's wire on.");
        foreach (var card in arcades)
        {
            var target = card;
            _ = Task.Run(async () =>
            {
                var reply = await AskAsync(target, line);
                if (!reply.StartsWith("OK", StringComparison.Ordinal))
                {
                    UiThread.Post(() => _s.Notify($"Arcade {target.Name}: {reply}"));
                }
            });
        }
        return ActionResult.Done($"{line} → {arcades.Count} arcade node{(arcades.Count == 1 ? "" : "s")}: {string.Join(", ", arcades.Select(a => a.Name))}");
    }

    /// <summary>ARCADE STATUS / GAMES / SCORES through this desk: each arcade node's answer, as one JSON list.</summary>
    public async Task<string> AskArcadesAsync(string line)
    {
        var arcades = await UiThread.InvokeAsync(Arcades);
        var replies = await Task.WhenAll(arcades.Select(a => AskAsync(a, line)));
        var rows = new List<object>();
        for (var i = 0; i < arcades.Count; i++)
        {
            var reply = replies[i];
            object? status = null;
            if (reply.StartsWith("OK ", StringComparison.Ordinal))
            {
                try { status = JsonDocument.Parse(reply[3..]).RootElement.Clone(); }
                catch (JsonException) { }
            }
            rows.Add(new { node = arcades[i].Name, instance = arcades[i].Instance, address = arcades[i].Address?.ToString() ?? "", status, reply = status is null ? reply : "" });
        }
        return JsonUtil.SerializeCompact(rows);
    }

    /// <summary>How long a node gets to take a connection: one on the show network takes it in a few milliseconds, and one that is gone should not hold a cue.</summary>
    public static TimeSpan ConnectBudget { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long a node gets to answer a line it has taken: a status is a few hundred bytes, but the node's desk thread may be mid-frame, and the desk is not waiting on it.</summary>
    public static TimeSpan ReplyBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The pause before a line that never left this desk is sent again.</summary>
    public static TimeSpan RetryAfter { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// One line to one node, its reply back. A moment's stall on a show network — a switch
    /// relearning, a machine paused by its own housekeeping — is not a node gone: a line that
    /// never left this desk is sent again, once, before the node is called unreachable. A line
    /// that did leave is never sent twice (a KEY tap sent twice is two taps): its reply is waited
    /// for, and its absence said as that.
    /// </summary>
    private static async Task<string> AskAsync(NodeCard card, string line)
    {
        var fault = "";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(RetryAfter);
            var sent = false;
            try
            {
                using var client = new TcpClient { NoDelay = true };
                using (var connect = new CancellationTokenSource(ConnectBudget))
                {
                    await client.ConnectAsync(card.Address!, card.WirePort, connect.Token);
                }
                var stream = client.GetStream();
                using var reply = new CancellationTokenSource(ReplyBudget);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), reply.Token);
                sent = true;
                var reader = new BoundedLineReader(stream, 1 << 20);     // a status is a few hundred bytes; a node that sends a megabyte is not answering
                for (var i = 0; i < 6; i++)
                {
                    var answer = await reader.ReadLineAsync(reply.Token);
                    if (answer is null) break;
                    if (answer.StartsWith("OK", StringComparison.Ordinal) || answer.StartsWith("ERR", StringComparison.Ordinal)) return answer;
                }
                return ControlProtocol.Err($"{card.Name} took the line but gave no reply");
            }
            catch (OperationCanceledException) when (sent)
            {
                return ControlProtocol.Err($"{card.Name} took the line but did not answer within {ReplyBudget.TotalSeconds:0.#} s");
            }
            catch (OperationCanceledException)
            {
                fault = $"{card.Name} did not take a connection within {ConnectBudget.TotalSeconds:0.#} s";
            }
            catch (Exception ex)
            {
                if (sent) return ControlProtocol.Err($"{card.Name} took the line but the reply was lost — {ex.Message}");
                fault = $"{card.Name} unreachable — {ex.Message}";
            }
        }
        return ControlProtocol.Err($"{fault} (asked twice)");
    }

    public string StatusJson()
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            me = new { kind = NodeKinds.Wire(_s.Profile), machine = _s.Beacon.MachineName, instance = _s.Beacon.Instance, link = _s.Link.LinkPort },
            linked = Linked,
            nodes = Nodes.Select(c => new { instance = c.Instance, kind = NodeKinds.Wire(c.Kind), name = c.Name, address = c.Address?.ToString() ?? "", show = c.Show, live = c.Live, fresh = c.Fresh, link = c.LinkPort, http = c.HttpPort, heardSecondsAgo = Math.Round(c.Age.TotalSeconds) }).ToArray(),
        });
}
