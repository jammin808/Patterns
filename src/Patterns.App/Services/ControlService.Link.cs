using System.Globalization;
using Patterns.Core.Services;

namespace Patterns.App.Services;

public sealed partial class ControlService
{
    /// <summary>
    /// Show Bus 1.3 §13.5: credentials planted beside the kept app keys (<see cref="ServiceKernel.AppKeys"/>) — the seam a test proves
    /// a key of its own making through, with a binding and a scope it names. Empty in the desk, whose keys are the kept ones alone.
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

    /// <summary>
    /// The credentials in force for <paramref name="token"/>: the pairing token as a code, the kept app keys in force with it (every
    /// issued key, and the keys enrolled from this code), then the planted ones.
    /// </summary>
    private IReadOnlyList<LinkCredential> LinkCredentials(string token)
    {
        var all = new List<LinkCredential>();
        if (LinkAcceptor.Code(token) is { } code) all.Add(code);
        all.AddRange(_kernel.AppKeys.InForce(token).Select(k => k.Credential));
        all.AddRange(LinkKeys);
        return all;
    }

    /// <summary>
    /// Whether a connection may read the show and hear its pushes (ADR-016): paired by the token, this machine's while the desk
    /// trusts it, an open desk — or a key it proved by LINK that is still in force. A code proved by LINK is the token: it is
    /// <see cref="WirePeer.Presented"/>, so a new token cuts it as it cuts the legacy proof.
    /// </summary>
    private bool Hears(string token, WirePeer peer)
        => Paired(token, peer.Presented, peer.Loopback) || KeyInForce(token, peer.Link);

    /// <summary>A link that stands on an app key (issued or enrolled) whose key is in force now, for the connector its proof covered.</summary>
    private bool KeyInForce(string token, LinkStanding? link) => KeyOf(token, link) is not null;

    /// <summary>The credential in force now that a key's link stands on, or null: none for a code's link, a key revoked or bound elsewhere.</summary>
    private LinkCredential? KeyOf(string token, LinkStanding? link)
        => link is { Verified: "key" or "link" } l && LinkAcceptor.Of(LinkCredentials(token), l.Kid) is { Verified: "key" or "link" } key
           && string.Equals(key.Bound, l.Client, StringComparison.Ordinal) ? key : null;

    /// <summary>
    /// 13.5.3 with §8.2's map (<see cref="AppKeyScopes"/>): a line a key's link may not run — the link stands on a key, the connection
    /// is paired no other way, and the scope it stands at now (<see cref="ScopeNow"/>) does not reach the line: follow and post read
    /// and subscribe, drive adds clip and playlist transport, CUE GO and STANDBY and STAGE MESSAGE, operate runs everything. A key gone
    /// from force runs nothing (its link is ended as it goes, 13.5.5).
    /// </summary>
    private bool BelowScope(string token, WirePeer peer, RemoteCommand cmd)
        => peer.Link is { Verified: "key" or "link" } l && !Paired(token, peer.Presented, peer.Loopback)
           && (KeyOf(token, l) is null || !AppKeyScopes.Allows(ScopeNow(token, l), cmd));

    /// <summary>The scope a key's link stands at now, for its gate and its refusal's words: the lower of the key's in force and the link's own.</summary>
    private string ScopeNow(string token, LinkStanding link)
        => KeyOf(token, link)?.Scope is { } now && LinkAcceptor.Rank(now) < LinkAcceptor.Rank(link.Scope) ? now : link.Scope;

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
    private async Task<bool> LinkLineAsync(WirePeer peer, LinkStep step, string rid, string body, string address, bool overTls, WireLimits limits, string helloName)
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
            // 13.5.7: a desk offers enrol, and a code proved inside TLS enrols (the decision says only then); over plain it never does.
            // A node enrols nothing: it has no Nodes page to list a key on or revoke it from (13.5.7's list), so it never keeps one.
            decision = LinkAcceptor.Decide(asked, parse.Proof, _kernel.ShowBusId, step.SNonce, step.Bind, credentials, kid =>
            {
                var locked = _wrongProofs.Reserve(address, kid, now);
                if (locked == 0) held = kid;
                return locked;
            }, enrol: _kernel.IsDesk);
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
        // 13.5.7: a code proved over TLS enrols a key for this connector (the one enrolled before for it gives way); none past the bound.
        var enrolment = decision.EnrolK is { } enrolK ? _kernel.AppKeys.Enrol(enrolK, client, helloName, token, DateTime.UtcNow) : null;
        var enrolled = enrolment?.Key;
        if (decision.EnrolK is not null && enrolled is null) Log.Warn($"Wire: {client} proved the pairing token over TLS and enrolled no key — this desk keeps {AppKeys.MaxKeys} app keys at most; revoke one on the Nodes page.");
        // 13.6.4's order: the verdict is queued before the connection hears anything as a link, so no push can be written ahead of
        // it; this handler is the only reader of the connection's lines, so no verb comes in between.
        Reply(ControlProtocol.Ok(LinkAcceptor.Verdict(credential, proof, lid, LinkCan(overTls && _kernel.IsDesk), _kernel.Beacon.Instance, (int)ShowBusLiveness.Beat.TotalMilliseconds, enrolled?.Kid)));
        if (credential.Verified == "code") peer.Presented = token;          // the token this proof was checked against: a new one cuts this link
        peer.Link = standing;
        // 13.5.5: a key revoked or bound elsewhere, or a token replaced, after this line's credentials were read and before the link
        // stood was missed by the cut (it reads the links that stand): the link is refused now in the same words and closed.
        if (StaleLink(token, standing) is { } stale)
        {
            if (credential.Verified == "code") RevokeEnrolled(_kernel.State.Control.Token);
            await LastWordAsync(peer, ControlProtocol.Err(LinkAcceptor.Refusal("refused", stale)));
            return true;
        }
        LinkVerified(address, standing);
        if (enrolment is { } made) KeyEnrolled(address, made.Key, made.Replaced);
        if (credential.Verified is "key" or "link") _kernel.AppKeys.Used(credential.Kid, DateTime.UtcNow);
        await GreetLinkAsync(peer);
        ArmPush();
        return false;
    }

    /// <summary>
    /// Why a link that has just stood is out of force already, or null: its token was replaced, or its key revoked or bound to another
    /// connector, since its line read them — 13.5.5's words.
    /// </summary>
    private string? StaleLink(string token, LinkStanding link)
    {
        var now = _kernel.State.Control.Token;
        if (link.Verified == "code") return string.Equals(now, token, StringComparison.Ordinal) ? null : AppKeyStore.ReplacedWords;
        if (link.Verified is not ("key" or "link") || KeyOf(now, link) is not null) return null;
        if (link.Verified == "link" && !string.Equals(now, token, StringComparison.Ordinal)) return AppKeyStore.ReplacedWords;
        return _kernel.AppKeys.Keys.Any(k => k.Kid == link.Kid) ? AppKeyStore.MovedWords : AppKeyStore.RevokedWords;
    }

    /// <summary>The features this desk uses on a link (13.6.6): <c>enrol</c> inside TLS on a desk (13.5.7), none on a plain carrier or a node.</summary>
    private static IReadOnlyList<string> LinkCan(bool overTls) => overTls ? new[] { "enrol" } : Array.Empty<string>();

    /// <summary>
    /// 13.5.5: the keys whose live links end now (revoked, bound elsewhere, lowered, or enrolled from a code that was replaced): each
    /// link that stands on one hears <c>ERR LINK refused — &lt;words&gt;</c> with no id as its last line and is closed, and leaves the
    /// recorder's feed. One journal row and one log line for each key's links.
    /// </summary>
    private void KeysEnded(IReadOnlyList<string> kids, string words)
    {
        List<WirePeer> cut;
        lock (_gate)
        {
            cut = _peers.Where(p => p.Link is { Verified: "key" or "link" } l && kids.Contains(l.Kid)).ToList();
            foreach (var peer in cut) _recording.Remove(peer);
        }
        foreach (var peer in cut)
        {
            peer.SayLast(ControlProtocol.Err(LinkAcceptor.Refusal("refused", words)));
            _ = Task.Run(async () =>
            {
                await peer.FlushAsync(LastWordWait);
                peer.Dispose();
            }, CancellationToken.None);   // a told link is never left open, whatever the listeners do
        }
        foreach (var kid in kids)
        {
            // A key with no live link ends nothing: the change that ended it wrote its own row (the press, the new token, the enrolment).
            var ended = cut.Count(p => p.Link?.Kid == kid);
            if (ended == 0) continue;
            var line = $"key {kid}: {words} — {ended} live link{(ended == 1 ? "" : "s")} refused and closed (Show Bus 1.3 §13.5.5)";
            Log.Info("Wire: " + line + ".");
            _kernel.Journal.Record("Remote", "KeyLinksEnded", kid, "Info", line);
        }
    }

    /// <summary>The kept keys moved: STATE's control row says them, so the decks and the desk hear it.</summary>
    private void KeysChanged() => UiThread.Post(ArmPush);

    /// <summary>
    /// 13.5.5: a new code revokes the keys enrolled from the old one — one journal row naming the kids, never a key. At start
    /// (<paramref name="atStart"/>) it revokes the ones kept from a token that changed while the desk was closed, so no key out of force
    /// stays in the list where nobody sees it.
    /// </summary>
    private void RevokeEnrolled(string token, bool atStart = false)
    {
        var gone = _kernel.AppKeys.RevokeEnrolled(token);
        if (gone.Count == 0) return;
        var why = atStart ? "the pairing token is not the one these keys were enrolled from (it changed while the desk was closed)" : "the pairing token changed";
        var words = $"{why} — {gone.Count} enrolled key{(gone.Count == 1 ? "" : "s")} revoked with it: {string.Join(", ", gone)} (Show Bus 1.3 §13.5.5)";
        Log.Info("Wire: " + words + ".");
        _kernel.Journal.Record("Remote", "KeysRevokedByNewToken", "", "Info", words);
    }

    /// <summary>
    /// 13.5.7: a key enrolled from the pairing token over TLS — one journal row and one log line, the kid and its connector; never K. A
    /// connector that proves the code again while it holds a key enrolled before (one that does not adopt the key its verdict named)
    /// re-enrols on each dial: that renewal keeps the old key's scope and is said in the log once a minute for its connector, with no
    /// journal row, so a flapping link fills nothing.
    /// </summary>
    private void KeyEnrolled(string address, AppKey key, string? replaced)
    {
        if (replaced is not null)
        {
            if (_busySaid.Allow("key-renewed:" + key.Bound, 1, TimeSpan.FromMinutes(1), DateTime.UtcNow))
                Log.Info($"Wire: {key.Bound} from {address} proved the pairing token over TLS again and its enrolled key was renewed ({replaced} → {key.Kid}, scope {key.Scope} kept) — it proves the code instead of the key it enrolled. Said once a minute for this connector.");
            return;
        }
        var words = $"{key.Bound} from {address} enrolled key {key.Kid} from the pairing token over TLS — scope {key.Scope}; raise it on the Nodes page (KEY BIND)";
        Log.Info("Wire: " + words + ".");
        _kernel.Journal.Record("Remote", "KeyEnrolled", key.Bound, "Info", words);
    }

    /// <summary>
    /// 13.6.9: an app key arrived in clear from another machine — refused as before (it opens nothing in clear), and marked on its row
    /// and in STATE, amber, with when and from where: replace it. The first mark writes one journal row and one log line; never the key.
    /// </summary>
    private void KeySeenInClear(System.Net.Sockets.TcpClient client, AppKey key, string via)
    {
        var address = AddressKey(client);
        if (!_kernel.AppKeys.MarkClear(key.Kid, address, via, DateTime.UtcNow)) return;
        var words = $"app key {key.Kid} ({AppKeys.ConnectorWords(key.Bound)}) arrived in clear from {address} ({TokenClearSightings.HowWords(via)}) — replace it: KEY REVOKE, then KEY MAKE (Nodes page)";
        Log.Warn("Remote: " + words + ".");
        _kernel.Journal.Record("Remote", "KeySeenInClear", key.Kid, "Alert", words);
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
