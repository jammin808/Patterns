using System.Globalization;
using Patterns.Core.Model;
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

    // Show Bus 1.3 §13.16.5: the links that ended (grey for peer.forget.ms) and the refusals of the last minute, beside the open links.
    private readonly AcceptorList _acceptor = new();

    // Why a link that is ending ends, set by what ends it (a key revoked, the operator's CLOSE) before its connection closes.
    private readonly Dictionary<WirePeer, string> _linkEnds = new();

    /// <summary>The acceptor's list's clock: wall time, settable for the tests, which step it past peer.forget.ms.</summary>
    public Func<DateTime> AcceptorNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Show Bus 1.3 §13.16.5: the acceptor's list — every link this wire accepted that LINK verified and that stands now, each at the
    /// scope it stands at now and marked when its key (or the token, for a code's link) was seen in clear, then each that ended within
    /// peer.forget.ms, grey. Never a deck: a connection proved by the legacy proof is on <see cref="Decks"/>, and one here is not.
    /// </summary>
    public IReadOnlyList<AppLinkRow> AppLinkList
    {
        get
        {
            var token = _kernel.State.Control.Token;
            List<(WirePeer Peer, LinkStanding Link, string Where, bool Recording)> open;
            lock (_gate)
            {
                // A link records and says where its navigator is as a deck does (RECORD is follow's, NAV DECK operate's): its row says so.
                open = _peers.Where(p => p.Link is not null && !p.Closed)
                    .Select(p => (p, p.Link!, _decks.TryGetValue(p, out var d) ? d.Where : "", _recording.ContainsKey(p))).ToList();
            }
            var now = AcceptorNow();
            return open.Select(o => RowOf(token, o.Peer, o.Link) with { Where = o.Where, Recording = o.Recording }).OrderBy(r => r.SinceUtc)
                .Concat(_acceptor.EndedRows(now))
                .ToList();
        }
    }

    /// <summary>Show Bus 1.3 §13.16.5: the LINK refusals of the last minute, each with its words and its count.</summary>
    public IReadOnlyList<AppLinkRefusal> LinkRefusals => _acceptor.Refusals(AcceptorNow());

    /// <summary>The LINK refusals the list's bound left out of its rows this run, counted: the Nodes page says how many beside the rows.</summary>
    public long LinkRefusalsUncounted => _acceptor.RefusalsUncounted;

    /// <summary>
    /// STATE's <c>decks</c> rows, on the desk and on a node alike (Show Bus 1.3 §13.16.5): every deck that said HELLO — level 0, no
    /// <c>t</c> or <c>id</c>, <c>verified</c> empty (the legacy proof is not LINK), scope <c>operate</c> while it is paired and
    /// <c>none</c> while it is not — then the acceptor's list, one row per app link LINK verified (level 1, its connector's
    /// <c>t</c> and <c>id</c>, <c>key</c>, <c>link</c> or <c>code</c>, its scope now) with its TLS, kid, light, mark, words and
    /// whether it ended appended. The fields after <c>recording</c> are appended at the end of the row, so a reader that matches the
    /// older row's fields still does; a reader that counts decks takes the rows at level 0 only.
    /// </summary>
    internal object[] DeckRows()
    {
        var decks = Decks.Select(d => (object)new
        {
            name = d.Name, module = d.Module, address = d.Address, where = d.Where, recording = d.Recording,
            level = 0, t = "", id = "", verified = "", scope = d.Paired ? "operate" : "none",
        });
        var links = AppLinkList.Select(r => (object)new
        {
            name = r.Label, module = "", address = r.Address, where = r.Where, recording = r.Recording,
            level = r.Level, t = r.T, id = r.Id, verified = r.Verified, scope = r.Scope,
            tls = r.Tls, kid = r.Kid, light = LinkRecord.LightWord(r.Light), mark = r.Mark, words = r.Words, ended = r.Ended,
        });
        return decks.Concat(links).ToArray();
    }

    /// <summary>One link's row as the list draws it, from what it stands on now.</summary>
    private AppLinkRow RowOf(string token, WirePeer peer, LinkStanding link)
    {
        var clear = link.Verified == "code"
            ? _kernel.TokenClearWords.Length > 0
            : _kernel.AppKeys.Keys.FirstOrDefault(k => k.Kid == link.Kid)?.ClearSeen is not null;
        var scope = link.Verified == "code" ? link.Scope : KeyOf(token, link) is null ? "none" : ScopeNow(token, link);
        return new AppLinkRow(link.Lid, link.Client, link.Name, link.Address, peer.Loopback, link.Tls, link.Verified, link.Kid, scope, link.SinceUtc) { ClearSeen = clear, Drove = peer.Drove };
    }

    /// <summary>
    /// A link that stood has ended: kept on the list grey, with why — what ended it said so before the close, else the connection
    /// itself ended. One log line; the journal's rows are the act's that ended it (a revoke, a CLOSE, a new token).
    /// </summary>
    private void LinkEnded(WirePeer peer, LinkStanding link, string? why)
    {
        var words = why ?? (peer.ClosedBecause.Length > 0 ? peer.ClosedBecause : "the connection ended");
        try
        {
            _acceptor.Ended(RowOf(_kernel.State.Control.Token, peer, link) with { Scope = link.Scope }, AcceptorNow(), words);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
        {
            // The key store or the kernel went first (a desk shutting down): the link has ended all the same; only its grey row is lost.
            Log.Warn($"Wire: the link of {link.Client} (lid {link.Lid}) ended and its row on the acceptor's list could not be read — {ex.Message}.");
        }
        Log.Info($"Wire: the link of {link.Client} from {link.Address} (lid {link.Lid}) ended — {words}.");
        UiThread.Post(ArmPush);
    }

    /// <summary>
    /// How long a closed link's connection is held open after its last word is written, before the desk closes it: zero on a desk. A
    /// test widens it to send lines into a closing link and see that none runs.
    /// </summary>
    public TimeSpan CloseLinger { get; set; } = TimeSpan.Zero;

    /// <summary>What <see cref="CloseLink"/> found: the link's label once it is told, or why nothing was closed.</summary>
    public enum CloseOutcome
    {
        /// <summary>No open link has that lid: it ended already, or never stood.</summary>
        NoLink,

        /// <summary>The link was told its last word already (a CLOSE, a revoke, a new token) and is closing.</summary>
        Closing,

        /// <summary>The link was told this desk's operator closed it; its connection ends once that line is written.</summary>
        Told,
    }

    /// <summary>
    /// Show Bus 1.3 §13.16.6: the operator closes one app link (the Nodes page's CLOSE, its right-click entry): its last line says this
    /// desk's operator closed it, in the refused class so its connector holds before it dials again (docs/OPEN.md L115: the line has no
    /// disconnect word), and the connection ends. From the press nothing the link sends runs (<see cref="WirePeer.LastSaid"/>). The key
    /// it stood on stays in force — revoking it is the wall. The outcome, and the link's label when it was told.
    /// </summary>
    public (CloseOutcome Outcome, string Label) CloseLink(string lid)
    {
        WirePeer? peer;
        lock (_gate)
        {
            peer = _peers.FirstOrDefault(p => p.Link is { } l && l.Lid == lid && !p.Closed);
            if (peer is null) return (CloseOutcome.NoLink, "");
            var closingLink = peer.Link!;
            var closingLabel = closingLink.Name.Length > 0 ? closingLink.Name : AppKeys.ConnectorWords(closingLink.Client);
            if (_linkEnds.ContainsKey(peer) || peer.LastSaid) return (CloseOutcome.Closing, closingLabel);
            _linkEnds[peer] = "closed by this desk's operator";
            _recording.Remove(peer);
        }
        var link = peer.Link!;
        var label = link.Name.Length > 0 ? link.Name : AppKeys.ConnectorWords(link.Client);
        peer.SayLast(ControlProtocol.Err(LinkAcceptor.Refusal("refused", AcceptorWords.ClosedWords(_kernel.Beacon.MachineName))));
        var linger = CloseLinger;
        _ = Task.Run(async () =>
        {
            await peer.FlushAsync(LastWordWait);
            if (linger > TimeSpan.Zero) await Task.Delay(linger, CancellationToken.None);
            peer.Dispose();
        }, CancellationToken.None);   // a told link is never left open, whatever the listeners do
        return (CloseOutcome.Told, label);
    }

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
            _acceptor.Refused(address, client, credential.Kid, "busy", limits.AppLinksBusyWords(_appLinks.From(address)), AcceptorNow());
            return await Last("busy", limits.AppLinksBusyWords(_appLinks.From(address)));
        }
        step.AppLink = true;
        var standing = new LinkStanding(client, credential.Kid, credential.Verified, credential.Scope, lid, overTls)
        {
            Name = helloName, Address = address, SinceUtc = AcceptorNow(),
        };
        // 13.5.7: a code proved over TLS enrols a key for this connector (the one enrolled before for it gives way); none past the bound.
        var enrolment = decision.EnrolK is { } enrolK ? _kernel.AppKeys.Enrol(enrolK, client, helloName, token, DateTime.UtcNow) : null;
        var enrolled = enrolment?.Key;
        if (decision.EnrolK is not null && enrolled is null) Log.Warn($"Wire: {client} proved the pairing token over TLS and enrolled no key — this desk keeps {AppKeys.MaxKeys} app keys at most; revoke one on the Nodes page.");
        // 13.6.4's order: the verdict is queued before the connection hears anything as a link, so no push can be written ahead of
        // it; this handler is the only reader of the connection's lines, so no verb comes in between.
        Reply(ControlProtocol.Ok(LinkAcceptor.Verdict(credential, proof, lid, LinkCan(_kernel.IsDesk, overTls), _kernel.Beacon.Instance, (int)ShowBusLiveness.Beat.TotalMilliseconds, enrolled?.Kid)));
        if (credential.Verified == "code") peer.Presented = token;          // the token this proof was checked against: a new one cuts this link
        peer.Link = standing;
        // 13.5.5: a key revoked or bound elsewhere, or a token replaced, after this line's credentials were read and before the link
        // stood was missed by the cut (it reads the links that stand): the link is refused now in the same words and closed.
        if (StaleLink(token, standing) is { } stale)
        {
            if (credential.Verified == "code") RevokeEnrolled(_kernel.State.Control.Token);
            // Refused, not ended: the link never stood for anything, so it is one refusal on the list and no grey row beside it.
            peer.Link = null;
            lock (_gate) _linkEnds.Remove(peer);
            _acceptor.Refused(address, client, credential.Kid, "refused", stale, AcceptorNow());
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

    /// <summary>
    /// The features this wire uses on a link (13.6.6), in §14's order: <c>final</c> on a desk, whose cue history says what a
    /// <c>CUE GO</c> came to (13.12.4); <c>receipts</c> everywhere (13.12.6); <c>enrol</c> inside TLS on a desk (13.5.7). Never
    /// <c>stuck</c> or <c>expire</c>: neither is offered before this desk's show writer has a measured stall (the Super Check's
    /// "Desk thread" row records the distribution a later round reads, LP-1).
    /// </summary>
    private static IReadOnlyList<string> LinkCan(bool desk, bool overTls)
        => desk ? (overTls ? new[] { "final", "receipts", "enrol" } : new[] { "final", "receipts" }) : new[] { "receipts" };

    // Show Bus 1.3 §13.12.6: the receipts of each verified connector's last ids, and the clock their ages run on.
    private readonly AcceptorReceipts _receipts = new();

    // The desk thread's stall distribution as the wire sees it: from each command's read to the thread taking it (LP-1).
    private readonly DeskStall _deskStall = new();

    /// <summary>
    /// The receipts record's clock, the final's window and a command's read-to-ran wait (13.12.5): monotonic milliseconds, so a wall-clock
    /// step never makes or hides a late run. Settable for the tests, which step it.
    /// </summary>
    public Func<long> ReceiptsNowMs { get; set; } = () => Environment.TickCount64;

    /// <summary>Called on the wire's thread with each line's request id (empty without one) the moment the line is read: the tests' seam between read and ran.</summary>
    public Action<string>? LineRead { get; set; }

    // A command the desk's thread has not taken: no clock reads this.
    private const long NotTaken = long.MinValue;

    /// <summary>The desk thread's stall distribution so far (the Super Check's "Desk thread" row).</summary>
    public DeskStallFacts DeskStall => _deskStall.Snapshot();

    /// <summary>How many connectors' receipts the record holds now.</summary>
    public int ReceiptConnectors => _receipts.ConnectorsHeld;

    /// <summary>
    /// The connector a verb's receipt is kept for (13.12.6): a link LINK verified, a verb (never a read, which changes nothing and is
    /// answered afresh), and an id of 13.12.1's shape. Null otherwise: the line runs as it always did.
    /// </summary>
    private static string? KeptFor(WirePeer peer, string rid, RemoteCommand cmd)
        => peer.Link is { } link && cmd.Kind == RemoteCommandKind.Action && AcceptorReceipts.Keeps(rid) ? link.Client : null;

    /// <summary>
    /// 13.12.6: <c>@rq1 RECEIPTS &lt;id&gt; …</c>, answered by the port from the record for the link's own ids —
    /// <c>OK @rq1 {"inst":…,"receipts":{…}}</c> — or refused: no id to answer by, a connection LINK did not verify, an ask of no id or
    /// of more than receipts.ask.max.
    /// </summary>
    private string ReceiptsAnswer(WirePeer peer, string rid, string body)
    {
        if (rid.Length == 0) return ControlProtocol.Err(AcceptorReceipts.NeedIdWords);
        if (peer.Link is not { } link) return WireReceipt.Tag(ControlProtocol.Err(AcceptorReceipts.NotLinkedWords), rid);
        if (!Hears(_kernel.State.Control.Token, peer)) return WireReceipt.Tag(ControlProtocol.Err(ControlProtocol.NotPaired), rid);
        var ids = body.TrimStart()[8..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return AcceptorReceipts.AskOf(ids) is { } ask
            ? WireReceipt.Tag(ControlProtocol.Ok(_receipts.Answer(link.Client, ask, _kernel.Beacon.Instance, ReceiptsNowMs())), rid)
            : WireReceipt.Tag(ControlProtocol.Err(AcceptorReceipts.AskWords), rid);
    }

    /// <summary>
    /// A link's verb answered: its receipt kept (13.12.6). A <c>CUE GO</c> on a desk answered <c>Requested</c> stays running in the
    /// record and is owed its final line (13.12.4) once its row in the cue history says what came of it; one answered <c>Requested</c>
    /// that no row will settle (no run named, or a node's) is kept as it read, accepted, never applied; one answered <c>Confirm</c>
    /// ran nothing and never will — its final follows at once, refused, since only a second GO, a person's, runs the cue.
    /// </summary>
    private void Settled(WirePeer peer, string connector, string rid, RemoteCommand cmd, string response, long? late, long readMono)
    {
        if (cmd.Action.Kind == ShowActionKind.CueGo && CueAnswer(response) is { } go)
        {
            if (go.Outcome == "Requested" && (!_kernel.IsDesk || go.Execution.Length == 0))
            {
                _receipts.Keep(connector, rid, AcceptorReceipts.Accepted(AcceptorReceipts.WordsOf(response[3..]), late), ReceiptsNowMs());
                return;
            }
            if (go.Outcome == "Requested")
            {
                var owed = new OwedFinal(peer, connector, rid, go.Execution, readMono, late);
                UiThread.Post(() =>
                {
                    _finals.Add(owed);
                    SettleFinals();
                });
                return;
            }
            if (go.Outcome == "Confirm" && _kernel.IsDesk)
            {
                var (line, receipt) = CueFinal.Line(rid, false, ConfirmWords, late);
                _receipts.Keep(connector, rid, receipt, ReceiptsNowMs());
                peer.Say(line);
                return;
            }
        }
        _receipts.Keep(connector, rid, AcceptorReceipts.FromAnswer(response, late), ReceiptsNowMs());
    }

    /// <summary>The final words of a <c>CUE GO</c> that asked for a second GO: it ran nothing.</summary>
    public const string ConfirmWords = "not run — this GO asked for a second GO, a person's, within the confirm window; the cue runs on that GO, never on this one";

    /// <summary>A <c>CUE GO</c>'s answer as this wire gives it: its outcome and the run it started; null for any other answer.</summary>
    private static (string Outcome, string Execution)? CueAnswer(string response)
    {
        if (!response.StartsWith("OK {", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(response[3..]);
            var o = doc.RootElement;
            string Str(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
            return (Str("outcome"), Str("execution"));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>A link's <c>CUE GO</c> waiting for its row to say what came of it: the connection, its connector, its id, its run, when it was read.</summary>
    private sealed record OwedFinal(WirePeer Peer, string Connector, string Rid, string Execution, long ReadMono, long? Late);

    // The finals owed, on the desk's thread alone. Each run is one row of the cue history: an owed final leaves with its row, so they
    // are never more than the history's rows.
    private readonly List<OwedFinal> _finals = new();

    /// <summary>How many CUE GO finals are owed now.</summary>
    public int FinalsOwed => _finals.Count;

    /// <summary>
    /// 13.12.4 on the desk's thread: each owed final whose row has settled is said — <c>OK @id final {…}</c> applied for a cue that ran
    /// (failed late or part-way included, its words naming what failed), <c>ERR @id final …</c> refused only for one where nothing ran
    /// (<see cref="CueFinal"/>) — and kept in the record. Said on its connection only within receipt.final.ms of its read (an answer at
    /// it still counts) and while that connection is open; later, or once it closed, the record alone takes it, where a RECEIPTS ask after
    /// a relink reads it. One whose row has left the history, or that waited receipts.kept.ms, is given up: the record keeps it as its
    /// first answer read — accepted, unconfirmed in words, it may have run — never running and never more than was observed. A fault here
    /// is logged and settles nothing; it never leaves the desk's timer or the cue stack's change.
    /// </summary>
    private void SettleFinals()
    {
        if (_finals.Count == 0) return;
        try
        {
            var history = _services.CueStack?.History;
            var now = ReceiptsNowMs();
            for (var i = _finals.Count - 1; i >= 0; i--)
            {
                var owed = _finals[i];
                var row = history?.FirstOrDefault(r => r.ExecutionId == owed.Execution);
                if (row is null || now - owed.ReadMono > (long)PeerLink.ReceiptsKeptFor.TotalMilliseconds)
                {
                    _finals.RemoveAt(i);
                    _receipts.Keep(owed.Connector, owed.Rid, AcceptorReceipts.Accepted(AcceptorReceipts.GivenUpWords, owed.Late), now);
                    continue;
                }
                if (CueFinal.Of(row) is not { } final) continue;
                _finals.RemoveAt(i);
                var (line, receipt) = CueFinal.Line(owed.Rid, final.Ok, final.Words, owed.Late);
                _receipts.Keep(owed.Connector, owed.Rid, receipt, now);
                if (now - owed.ReadMono <= (long)PeerLink.ReceiptFinal.TotalMilliseconds && !owed.Peer.Closed && !owed.Peer.LastSaid) owed.Peer.Say(line);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Wire: a CUE GO's final could not be settled; the record keeps what it had.", ex);
        }
    }

    /// <summary>
    /// 13.12.5: a command with an id that ran more than receipt.late.ms after it was read — its receipt says <c>lateMs</c>, and one
    /// journal row and one log line say which verb, from where, and how late. Never the line's text, which may carry a secret's word.
    /// </summary>
    private void LateCommand(RemoteCommand cmd, string rid, ActionOrigin origin, long lateMs)
    {
        var verb = cmd.Kind == RemoteCommandKind.Action ? cmd.Action.Kind.ToString() : cmd.Kind.ToString();
        var words = $"{verb} (@{rid}) from {origin.Label} {AcceptorReceipts.LateWords(lateMs)} — the desk's thread was busy; its receipt says so (Show Bus 1.3 §13.12.5)";
        Log.Info("Wire: " + words + ".");
        _kernel.Journal.Record(origin.Label, "LateCommand", verb, "Warn", words);
    }

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
            foreach (var peer in cut)
            {
                _recording.Remove(peer);
                _linkEnds[peer] = words;
            }
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
        _acceptor.Refused(address, client, kid, cls, decision.Words, AcceptorNow());   // counted on the Nodes page's list this minute; no push
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
