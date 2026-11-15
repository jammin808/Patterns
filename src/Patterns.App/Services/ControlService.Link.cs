using System.Globalization;
using Patterns.Core.Services;

namespace Patterns.App.Services;

public sealed partial class ControlService
{
    /// <summary>
    /// Show Bus 1.3 §13.5: the app keys this desk proves LINK against beside the pairing token. This build mints and keeps none,
    /// so a desk accepts a key from no connector; the list is the seam a test plants a key through, and is empty in the desk.
    /// </summary>
    public IReadOnlyList<LinkCredential> LinkKeys { get; set; } = Array.Empty<LinkCredential>();

    /// <summary>The wrong-proof table's clock: monotonic milliseconds. Settable for the tests, which give it a minute in a step.</summary>
    public Func<long> LinkNowMs { get; set; } = () => Environment.TickCount64;

    // Show Bus 1.3 §13.5.6: LINK's wrong proofs, counted apart from the legacy proof's five-and-close, the web's and the decks'.
    private readonly WrongProofTable _wrongProofs = new(LinkAcceptor.WrongProofsPerMinute);

    // §13.6.3: the connections from each address whose LINK has no verdict yet, at most UnfinishedPerAddress of them.
    private readonly ConnectionLedger _unfinished = new();

    // §13.8.5 beside it: the connections from each address that have said nothing yet, bounded by the same number in a ledger of
    // their own, so neither a silent flood nor four LINKs waiting for their proofs shut the port to a deck from that address.
    private readonly ConnectionLedger _silent = new();

    // The app links that finished LINK, under their sub-cap inside the wire's ceilings, so the decks keep the rest of the port.
    private readonly ConnectionLedger _appLinks = new();

    /// <summary>How many connections finished LINK and are open right now.</summary>
    public int AppLinks => _appLinks.Open;

    /// <summary>The (address, kid) pairs the wrong-proof table holds right now: a bounded count, never a proof or a kid.</summary>
    public int WrongProofPairs => _wrongProofs.Pairs;

    /// <summary>
    /// One connection's LINK as it goes (13.6.3): the ask it made, the snonce and the bind its offer named, where its one proof
    /// stands, whether it holds a place among its address's unfinished connections, the handshake's deadline and its app-link slot.
    /// The proof and the deadline race on <see cref="Phase"/> alone: whichever moves it on from <see cref="Asked"/> first wins, so a
    /// deadline never closes a connection whose proof is being decided, and a proof is never decided on a connection it has closed.
    /// </summary>
    private sealed class LinkStep : IDisposable
    {
        // Phase: 0 until the ask, then Asked; the proof moves it to Proving, the deadline to Expired.
        public const int Asked = 1, Proving = 2, Expired = 3;

        public LinkAsk? Ask;
        public string SNonce = "";
        /// <summary>T's <c>bind</c> on this desk's side, worked out once from the certificate this connection showed (13.6.1).</summary>
        public string Bind = "plain";
        public int Phase;
        public bool Unfinished;
        public bool AppLink;
        public CancellationTokenSource? Deadline;

        /// <summary>The connection's one proof has been taken (decided, or closed by the deadline).</summary>
        public bool Spent => Volatile.Read(ref Phase) >= Proving;

        public void Disarm()
        {
            Deadline?.Dispose();
            Deadline = null;
        }

        public void Dispose() => Disarm();
    }

    /// <summary>The credentials in force for <paramref name="token"/>: the pairing token as a code, then the app keys (none in this build).</summary>
    private IReadOnlyList<LinkCredential> LinkCredentials(string token)
    {
        var code = LinkAcceptor.Code(token);
        var keys = LinkKeys;
        if (code is null) return keys;
        var all = new List<LinkCredential>(keys.Count + 1) { code };
        all.AddRange(keys);
        return all;
    }

    /// <summary>
    /// Whether a connection may read the show and hear its pushes (ADR-016): paired by the token, this machine's while the desk
    /// trusts it, an open desk — or a key it proved by LINK that is still in force. A code proved by LINK is the token: it is
    /// <see cref="WirePeer.Presented"/>, so a new token cuts it as it cuts the legacy proof.
    /// </summary>
    private bool Hears(string token, WirePeer peer)
        => Paired(token, peer.Presented, peer.Loopback) || KeyInForce(peer.Link);

    private bool KeyInForce(LinkStanding? link)
        => link is { Verified: "key" } l && LinkAcceptor.Of(LinkKeys, l.Kid) is { } key && string.Equals(key.Bound, l.Client, StringComparison.Ordinal);

    /// <summary>
    /// 13.5.3: a verb a key's link may not run — the link stands on a key, the connection is paired no other way, the line is not a
    /// read, and the key in force now is not at <c>operate</c> or not at the scope its link was verified at (a key whose scope moved
    /// is held to the lower of the two until it links again). Until each scope is mapped onto this desk's verbs, a key below
    /// <c>operate</c> reads and runs nothing.
    /// </summary>
    private bool BelowScope(string token, WirePeer peer, RemoteCommand cmd)
        => peer.Link is { Verified: "key" } l && !Paired(token, peer.Presented, peer.Loopback) && !ControlProtocol.IsQuery(cmd)
           && (LinkAcceptor.Of(LinkKeys, l.Kid)?.Scope is not { } now || now != l.Scope || now != "operate");

    /// <summary>The scope a key's link stands at now, for its refusal's words: the lower of the key's in force and the link's own.</summary>
    private string ScopeNow(LinkStanding link)
        => LinkAcceptor.Of(LinkKeys, link.Kid)?.Scope is { } now && LinkAcceptor.Rank(now) < LinkAcceptor.Rank(link.Scope) ? now : link.Scope;

    /// <summary>
    /// 13.6.3: a connection's place among its address's unfinished ones, taken at its LINK ask; false, nothing taken, when that
    /// address already holds as many as the wire allows.
    /// </summary>
    private bool TakeUnfinished(string address, WireLimits limits) => _unfinished.TryAdmit(address, int.MaxValue, limits.UnfinishedPerAddress);

    /// <summary>13.8.5: a connection's place among its address's silent ones, taken at accept and given back at its first byte.</summary>
    private bool TakeSilent(string address, WireLimits limits) => _silent.TryAdmit(address, int.MaxValue, limits.UnfinishedPerAddress);

    private void GiveSilent(string address) => _silent.Release(address);

    /// <summary>T's <c>bind</c> for one connection (13.6.1): the certificate its TLS showed, or <c>plain</c> on the socket.</summary>
    private static string BindOf(Stream wire)
        => wire is System.Net.Security.SslStream { LocalCertificate: { } shown } ? "tls:" + TlsFingerprint.Of(shown) : "plain";

    private void GiveUnfinished(LinkStep step, string address)
    {
        if (!step.Unfinished) return;
        step.Unfinished = false;
        _unfinished.Release(address);
    }

    /// <summary>
    /// One LINK line on the wire (13.6.4), answered here and never by the router: the ask, answered with this desk's offer — its
    /// kind and id, a fresh snonce and the certificate this connection shows (<c>tls:sha256:…</c> inside TLS, <c>plain</c> on the
    /// socket) — and the one proof, answered with the verdict and this desk's own proof, or refused in 13.6.4's words and the
    /// connection closed. True when the connection ends.
    /// </summary>
    private async Task<bool> LinkLineAsync(WirePeer peer, LinkStep step, string rid, string body, string address, bool overTls, WireLimits limits)
    {
        // A deadline that has closed this connection took its one proof with it.
        if (Volatile.Read(ref step.Phase) == LinkStep.Expired) return true;
        void Reply(string reply) => peer.Say(WireReceipt.Tag(reply, rid));
        async Task<bool> Last(string cls, string words)
        {
            await LastWordAsync(peer, WireReceipt.Tag(ControlProtocol.Err(LinkAcceptor.Refusal(cls, words)), rid));
            return true;
        }

        // The token read once for the whole line: what this proof is checked against is the token its connection is paired under.
        var token = _kernel.State.Control.Token;
        var credentials = LinkCredentials(token);
        if (!PairingToken.Needed(token) && credentials.Count == 0)
        {
            // 13.6.7: an open desk — no token, no key — proves nothing and asks for nothing; the connection was greeted at its start.
            // A token LINK cannot prove (not of 13.5.1's shape) is no open desk: its kid is unknown here, and the legacy proof keys it.
            Reply(ControlProtocol.Err(LinkAcceptor.Refusal("open", LinkAcceptor.OpenWords)));
            return false;
        }
        if (step.Spent || peer.Link is not null)
        {
            Reply(ControlProtocol.Err(LinkAcceptor.Refusal("malformed", LinkAcceptor.OneLinkWords)));
            return false;
        }
        var parse = PeerLink.ReadLinkLine(body);
        if (!LinkAcceptor.IsProofLine(body))
        {
            if (step.Ask is not null)
            {
                Reply(ControlProtocol.Err(LinkAcceptor.Refusal("malformed", LinkAcceptor.OneLinkWords)));
                return false;
            }
            if (parse.Refused is "version")
            {
                Reply(ControlProtocol.Err(LinkAcceptor.Refusal("version", LinkAcceptor.VersionWords)));
                return false;
            }
            if (parse.Ask is not { } ask)
            {
                Reply(ControlProtocol.Err(LinkAcceptor.Refusal("malformed", LinkAcceptor.AskWords)));
                return false;
            }
            if (!TakeUnfinished(address, limits))
            {
                if (_busySaid.Allow("link-unfinished:" + address, 1, TimeSpan.FromMinutes(1), DateTime.UtcNow))
                    Log.Info($"Wire: {address} holds {limits.UnfinishedPerAddress} connections that have not finished LINK — one more refused busy and closed (Show Bus 1.3 §13.6.3). Said once a minute for this address.");
                return await Last("busy", LinkAcceptor.UnfinishedWords);
            }
            step.Unfinished = true;
            step.Ask = ask;
            step.SNonce = LinkAcceptor.NewNonce();
            Volatile.Write(ref step.Phase, LinkStep.Asked);
            // 13.6.3: a LINK that has no verdict within the handshake's seconds is closed, its place given back by the handler; a
            // proof already being decided has won the step, and the deadline leaves it be.
            step.Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(limits.HandshakeSeconds));
            step.Deadline.Token.Register(() =>
            {
                if (Interlocked.CompareExchange(ref step.Phase, LinkStep.Expired, LinkStep.Asked) != LinkStep.Asked) return;
                if (_busySaid.Allow("link-slow:" + address, 1, TimeSpan.FromMinutes(1), DateTime.UtcNow))
                    Log.Info($"Wire: a LINK from {address} had no proof within {limits.HandshakeSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s — closed (Show Bus 1.3 §13.6.3). Said once a minute for this address.");
                peer.Dispose();
            });
            Reply(ControlProtocol.Ok(LinkAcceptor.Offer(_kernel.ShowBusId, step.SNonce, step.Bind)));
            return false;
        }

        if (step.Ask is not { } asked)
        {
            Reply(ControlProtocol.Err(LinkAcceptor.Refusal("malformed", LinkAcceptor.ProofBeforeAskWords)));
            return false;
        }
        // Each nonce answers one proof, right or wrong; a proof of any other shape is a wrong proof (13.6.4). A deadline that won
        // the step first has closed the connection.
        if (Interlocked.CompareExchange(ref step.Phase, LinkStep.Proving, LinkStep.Asked) != LinkStep.Asked) return true;
        step.Disarm();
        var now = LinkNowMs();
        string? held = null;
        LinkDecision? decision = null;
        try
        {
            decision = LinkAcceptor.Decide(asked, parse.Proof, _kernel.ShowBusId, step.SNonce, step.Bind, credentials, kid =>
            {
                var locked = _wrongProofs.Reserve(address, kid, now);
                if (locked == 0) held = kid;
                return locked;
            });
        }
        finally
        {
            if (held is not null) _wrongProofs.Settle(address, held, now, wrong: decision?.CountKid == held);
        }
        GiveUnfinished(step, address);
        var client = asked.T + ":" + asked.Id;
        if (!decision.Ok || decision.Credential is not { } credential || decision.Proof is not { } proof || decision.Lid is not { } lid)
        {
            LinkRefused(address, client, asked.Kid, decision);
            return await Last(decision.Class ?? "refused", decision.Words);
        }
        if (!_appLinks.TryAdmit(address, limits.MaxAppLinks, limits.MaxAppLinksPerAddress))
        {
            if (_busySaid.Allow("link-busy:" + address, 1, TimeSpan.FromMinutes(1), DateTime.UtcNow))
                Log.Info($"Wire: {client} from {address} proved its LINK with the app links' sub-cap full ({_appLinks.Open} open) — refused busy; the decks keep the rest of the port. Said once a minute for this address.");
            return await Last("busy", limits.AppLinksBusyWords(_appLinks.From(address)));
        }
        step.AppLink = true;
        var standing = new LinkStanding(client, credential.Kid, credential.Verified, credential.Scope, lid, overTls);
        // 13.6.4's order: the verdict is queued before the connection hears anything as a link, so no push can be written ahead of
        // it; this handler is the only reader of the connection's lines, so no verb comes in between.
        Reply(ControlProtocol.Ok(LinkAcceptor.Verdict(credential, proof, lid, Array.Empty<string>(), _kernel.Beacon.Instance, (int)ShowBusLiveness.Beat.TotalMilliseconds)));
        if (credential.Verified == "code") peer.Presented = token;          // the token this proof was checked against: a new one cuts this link
        peer.Link = standing;
        LinkVerified(address, standing);
        await GreetLinkAsync(peer);
        ArmPush();
        return false;
    }

    /// <summary>
    /// 13.6.4: the greeting a link hears after its verdict — the whole STATE, once more even when the connection was greeted at its
    /// start (this machine's own, while the desk trusts it), since the connector discards whatever came before the verdict (13.6.3).
    /// </summary>
    private async Task GreetLinkAsync(WirePeer peer)
    {
        var json = await _router.StateJsonAsync();
        if (!Hears(_kernel.State.Control.Token, peer)) return;
        peer.TryGreet();
        peer.Say("STATE " + json);
    }

    /// <summary>
    /// A LINK verified: one journal row and one log line — the connector's <c>t:id</c>, the kid, the scope, the carrier and whether it
    /// is inside TLS, the lid. Never a nonce, a proof or K (13.19).
    /// </summary>
    private void LinkVerified(string address, LinkStanding link)
    {
        var words = $"{link.Client} from {address} linked by LINK — {link.Verified} (kid {link.Kid}), scope {link.Scope}, line {(link.Tls ? "tls" : "plain")}, lid {link.Lid}";
        Log.Info("Wire: " + words + ".");
        _kernel.Journal.Record("Remote", "Link", link.Client, "Info", words);
    }

    /// <summary>
    /// A LINK refused: said in the log once a minute for each address and class. Its own journal may name the kid (13.6.3) and so
    /// may this line; a pair the wrong-proof table has just locked writes one journal row. Never the proof or a nonce.
    /// </summary>
    private void LinkRefused(string address, string client, string kid, LinkDecision decision)
    {
        var cls = decision.Class ?? "refused";
        if (_busySaid.Allow("link-" + cls + ":" + address, 1, TimeSpan.FromMinutes(1), DateTime.UtcNow))
            Log.Info($"Wire: a LINK from {address} as {client} for kid {kid} refused {cls} — {decision.Words}. Said once a minute for this address.");
        if (decision.CountKid is { } counted && _wrongProofs.LockedFor(address, counted, LinkNowMs()) > 0
            && _busySaid.Allow("link-locked:" + address + ":" + counted, 1, TimeSpan.FromMinutes(1), DateTime.UtcNow))
        {
            var words = $"{LinkAcceptor.WrongProofsPerMinute} wrong LINK proofs for kid {counted} from {address} in a minute — that pair is refused until the minute ends";
            _kernel.Journal.Record("Remote", "LinkLocked", address, "Alert", words);
        }
    }
}
