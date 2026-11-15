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
        var address = AddressKey(client);
        if (!_kernel.TokenSeenInClear.Note(_kernel.State.Control.Token, address, via, DateTime.UtcNow)) return;
        var words = $"the pairing token arrived in clear from {address} ({TokenClearSightings.HowWords(via)}) — rotate it: NEW TOKEN (Remote page, TRUST)";
        Log.Warn("Remote: " + words + ".");
        _kernel.Journal.Record("Remote", "TokenSeenInClear", address, "Alert", words);
        UiThread.Post(ArmPush);                                                                // STATE's control row moved: the decks hear it
    }

    /// <summary>
    /// Show Bus 1.3 §13.8.5: STATE's <c>control.wire</c> — the wire's TLS as every reader says it, without the counts since start
    /// (the Super Check, the Nodes page and the brief carry those): STATE moves only when the certificate or the port does, so a
    /// connection, a probe or a scan never defeats the repeated push's skip.
    /// </summary>
    private object WireRow()
    {
        var fingerprint = _kernel.Tls.Fingerprint;
        var control = _kernel.State.Control;
        return new
        {
            port = control.TcpPort,
            tls = TlsFingerprint.IsValid(fingerprint),
            fingerprint,
            words = control.Enabled ? WireTls.Words(control.TcpPort, fingerprint, _kernel.Tls.Problem) : "",
        };
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
            // Show Bus 1.3 §13.8.5: whether the wire's port serves TLS (told from plain by the first byte, with the certificate the
            // hello's tls names) and its words — appended at the end.
            wire = WireRow(),
            // Show Bus 1.3 §13.5 and §13.16.5: the app keys in force — each one's kid, the connector it speaks for, its scope, how and
            // when it was made, when it last linked and where it was seen in clear; never a key or its K. Appended at the end.
            keys = _kernel.AppKeys.InForce(_kernel.State.Control.Token).Select(k => new
            {
                kid = k.Kid,
                bound = k.Bound,
                scope = k.Scope,
                made = k.Made,
                since = k.MadeUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                lastUse = k.LastUseUtc?.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                clearSeen = k.ClearSeen is { } m
                    ? new
                    {
                        address = m.Address,
                        via = m.Via,
                        first = m.FirstUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                        last = m.LastUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                        count = m.Count,
                    }
                    : null,
                words = AppKeys.Words(k),
            }).ToArray(),
        };
    }
}
