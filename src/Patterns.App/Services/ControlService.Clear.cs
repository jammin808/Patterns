using System.Net.Sockets;
using Patterns.Core.Services;

namespace Patterns.App.Services;

public sealed partial class ControlService
{
    /// <summary>
    /// Round 89 (PeerLink profile 13.6.9): this process's pairing token arrived in clear from another machine — over the
    /// web remote's plain listener (<paramref name="via"/> "web") or as plain AUTH on the wire ("wire"). Nothing is
    /// refused for it; it is marked. The Super Check's REMOTE row, the Nodes and Remote pages and STATE's control row say
    /// "rotate it: NEW TOKEN" until the token changes. The first sighting from each address writes one journal row and one
    /// log line, and later ones from it write neither. The words name the address and the door, never the token.
    /// </summary>
    private void SeenInClear(TcpClient client, string via)
    {
        var address = PairedKey(client);
        if (!_kernel.TokenSeenInClear.Note(_kernel.State.Control.Token, address, via, DateTime.UtcNow)) return;
        var words = $"the pairing token arrived in clear from {address} ({TokenClearSightings.HowWords(via)}) — rotate it: NEW TOKEN (Remote page, TRUST)";
        Log.Warn("Remote: " + words + ".");
        _kernel.Journal.Record("Remote", "TokenSeenInClear", address, "Alert", words);
        UiThread.Post(ArmPush);                                                                // STATE's control row moved: the decks hear it
    }

    /// <summary>Round 89: STATE's control row — the machines the token set now arrived from in clear, and the words; empty with none.</summary>
    internal object ControlRow()
    {
        var seen = _kernel.TokenSeenInClear.For(_kernel.State.Control.Token);
        return new
        {
            clearSeen = seen.Select(c => new
            {
                address = c.Address,
                via = c.Via,
                first = c.FirstUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                last = c.LastUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                count = c.Count,
            }).ToArray(),
            words = TokenClearSightings.Words(seen),
        };
    }
}
