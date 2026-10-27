using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>
/// Show Bus 1.3 §13.16, one connector's link to one node as a machine on injected time and an injected j. Each method is one
/// thing that happened, at its time, and time only moves forward; what the link is at a time — its state, light and flags,
/// the dial it makes, whether it follows the peer, each command's outcome and 13.16.3's <see cref="LinkRecord"/> — is read off
/// it then, after every deadline that fell due by then acted at its own due time. It decides; the carrier dials, reads and
/// writes, takes what to write from <see cref="Drain"/>, and closes its socket once <see cref="KeepsConnection"/> says the
/// machine ended it. It never holds a key, code, K, S, proof or nonce — only the kid of what the carrier proves, and the
/// carrier's word that the acceptor's proof verified — so nothing it publishes can carry a secret, and no verdict is taken
/// unchecked. One writer: the link's own lane.
/// <para>
/// What it keeps: 13.6's order (one proof a connection, only to the id meant and over the certificate shown; nothing taken
/// before the acceptor's proof verifies), 13.6.8's level 0 (floors first; the legacy proof only for a 32-hex NONCE; the
/// bearer only to a pin a proof or a person made), 13.8's pins, 13.9.3's floors, 13.10's liveness with its own gap and the
/// show kept apart, matched pongs as clock samples (13.10.2, <see cref="LinkClock"/>), 13.12's commands and outcomes,
/// 13.15's redial — reset only after a link verified and stable — and holds that outlast a restart (<see cref="Kept"/>),
/// and 13.16's states, flags and light.
/// </para>
/// </summary>
public sealed class PeerLinkMachine
{
    private enum Phase
    {
        /// <summary>No connection: never dialled, or ended and waiting to dial again.</summary>
        Idle,

        /// <summary>A dial is out and nothing has answered it.</summary>
        Dialling,

        /// <summary>A connection is up and nothing is followed from it yet: TLS, LINK, a level-0 pairing.</summary>
        Handshake,

        /// <summary>A link is up: verified, or up with <c>legacy</c>, <c>open</c> or <c>keyless</c>.</summary>
        Up,
    }

    /// <summary>What one connection knows; a new connection starts with none of the last one's.</summary>
    private sealed class Connection
    {
        public bool Tls;
        public bool Plain;
        public string? Cert;
        public LinkOffer? Offer;
        public bool Greeted;
        public long GreetAt;
        public bool ProofWritten;
        public bool NonceProvable;
        public bool AuthProofWritten;
        public int Level = -1;
        public string Proof = "none";
        public bool Verified;
        public bool Keyless;
        public bool Legacy;
        public bool Open;
        public bool CodePlain;
        public string? Scope;
        public LinkShow Show;
        public long ReproveAt = long.MinValue;
        public int OversizeHere;
        public int MalformedHere;
        public long VerifiedAt;
        public bool StableDone = true;
        public bool WentDown;
    }

    private sealed class Command(string id, long writtenAt)
    {
        public string Id { get; } = id;
        public long WrittenAt { get; } = writtenAt;
        public LinkOutcome? Outcome { get; set; }
        public string Words { get; set; } = "";

        /// <summary>When it stopped waiting, in settle order: the first settled are the first let go.</summary>
        public long Left { get; set; }
    }

    private readonly LinkSettings _s;
    private readonly Func<double> _j;
    private readonly LinkClock _clock = new();
    private readonly List<long> _pings = new();
    private readonly Dictionary<string, Command> _commands = new(StringComparer.Ordinal);
    private readonly List<LinkWrite> _writes = new();
    private HashSet<string> _can;
    private LinkPin? _pin;
    private LinkFloor? _floor;
    private string? _boundId;
    private string? _boundT;

    private long _now;
    private long _wallOffset;
    private Phase _phase;
    private Connection? _c;
    private long _phaseAt;
    private long _attemptAt;
    private bool _everClosed;
    private bool _addressHeard;
    private string _scheme = "tls";
    private bool _noTls;
    private bool _tlsHint;
    private string? _shown;
    private LinkOffer? _other;

    private long _lastHeard;
    private long _recoveredAt;
    private long _closedAt;
    private int _failed;
    private long _waitMs;
    private long _nextDialAt = long.MinValue;

    private long _holdUntil = long.MinValue;
    private long _holdUntilWall;
    private long _holdAt;
    private string? _holdRefusal;
    private string? _refused;
    private long _refusedAt;
    private string? _lastClass;
    private string? _why;

    private bool _note;
    private (long At, string? Inst, string? Host)? _h1;
    private (long At, string? Inst, string? Host)? _h2;
    private long _markUntil = long.MinValue;
    private long _markAt;

    private string? _peerInst;
    private long? _lastEvaluate;
    private long _selfGapUntil = long.MinValue;
    private long _leftSeq;
    private long _nextId;
    private int _unconfirmed;
    private int _late;
    private int _replayed;
    private int _oversize;
    private int _malformed;
    private int _unmatched;

    /// <summary>
    /// A machine as its settings and what it kept (null for nothing) make it at <paramref name="now"/>: a hold kept with its
    /// end in wall time holds until that end (13.15.3), and a kept pin that names no certificate is no pin.
    /// <paramref name="jitter"/> is 13.15.1's j; null draws it from the CSPRNG.
    /// </summary>
    public PeerLinkMachine(LinkSettings settings, LinkKept? kept, LinkNow now, Func<double>? jitter = null)
    {
        _s = settings;
        _j = jitter ?? PeerLink.Jitter;
        _can = new HashSet<string>(settings.Can, StringComparer.Ordinal);
        _pin = kept?.Pin is { } pin && PeerLink.IsHex(pin.Fingerprint, 64) ? pin : null;
        _floor = kept?.Floor;
        _boundId = kept?.NodeId;
        _boundT = kept?.NodeKind;
        _now = now.Mono;
        _wallOffset = now.Wall - now.Mono;
        if (kept is { HeldUntilWall: { } until } && until > now.Wall)
        {
            _holdUntil = now.Mono + (until - now.Wall);
            _holdUntilWall = until;
            _holdRefusal = kept.HeldRefusal ?? "key";
            _holdAt = now.Mono;
        }
    }

    /// <summary>What the machine keeps across a restart, to be persisted whenever it changes and before any further I/O (13.15.3).</summary>
    public LinkKept Kept => new(_pin, _floor, _holdUntil > _now ? _holdUntilWall : null, _holdUntil > _now ? _holdRefusal : null, _boundId, _boundT);

    /// <summary>The node this link is for: the one a verified link or the operator bound it to, else the settings', else the pin's.</summary>
    public string? NodeId => _boundId ?? _s.NodeId ?? _pin?.NodeId;

    /// <summary>The node kind this link is for: the one a verified link or the operator bound it to, else the settings'.</summary>
    public string? NodeKind => _boundT ?? _s.Kind;

    /// <summary>The clock its matched pongs measure (13.11).</summary>
    public LinkClock Clock => _clock;

    /// <summary>When the evaluator last pinged every link after its own gap (13.10.4); null before it ever did.</summary>
    public long? PingedAt { get; private set; }

    // ---- what happened -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The connector dials now, when its schedule lets it (<see cref="DialDue"/>): the scheme it dials, or null when a hold, a
    /// refusal, a conflict or its wait stops it. A dial with no connection within <c>link.connect.timeout.ms</c> is a failed dial.
    /// </summary>
    public string? Dial(LinkNow now)
    {
        Advance(now);
        if (_phase != Phase.Idle || DialDue(now) is not { } scheme) return null;
        _phase = Phase.Dialling;
        _phaseAt = _attemptAt = now.Mono;
        _scheme = scheme;
        return scheme;
    }

    /// <summary>A connection is up on the dial's scheme, nothing verified on it; on <c>line</c> the connector greets and asks LINK.</summary>
    public void Connected(LinkNow now)
    {
        Advance(now);
        UpFromDial(now, greet: true);
    }

    /// <summary>
    /// The dial found no TLS on offer. Where a pin or a floor says TLS was there the link is refused, nothing sent (13.9.3);
    /// otherwise a <c>line</c> link goes on plain (13.3.3), a local <c>ws</c> link goes on plain for LINK to prove (13.3.4),
    /// and a <c>ws</c> link to another machine reads the public view keyless, its key kept on this machine (13.3.2).
    /// </summary>
    public void NoTls(LinkNow now)
    {
        Advance(now);
        if (_phase is Phase.Handshake or Phase.Up) return;
        _noTls = true;
        if (_floor is { Tls: true } || _pin is { Trust: not PinTrust.First })
        {
            Refuse(now, "floor");
            return;
        }
        if (_s.Carrier == LinkCarrier.Line)
        {
            Up(now, tls: false, greet: true);
            return;
        }
        Up(now, tls: false, greet: false);
        if (_s.Local) return;
        Linked(now);
        _c!.Keyless = true;
    }

    /// <summary>
    /// The connection's TLS showed this certificate (64 hex). A node pinned by a proof or a person showing another is
    /// CERTIFICATE CHANGED: nothing of the connector's is sent (13.8.3). On <c>line</c> the connector then greets and asks LINK.
    /// </summary>
    public void Certificate(LinkNow now, string fingerprint)
    {
        Advance(now);
        if (_phase is Phase.Idle or Phase.Dialling && !Up(now, tls: true, greet: false)) return;
        if (_refused is not null || _c is null) return;
        _c.Cert = fingerprint;
        _shown = fingerprint;
        if (_pin is { Trust: not PinTrust.First } pinned && pinned.Fingerprint != fingerprint)
        {
            Refuse(now, "certificate");
            return;
        }
        if (_pin is { Trust: PinTrust.First } first && first.Fingerprint != fingerprint) _note = true;
        if (_s.Carrier == LinkCarrier.Line && _phase == Phase.Handshake) Greet(now);
    }

    /// <summary>
    /// A line that answers LINK (13.6.4). <c>OK @l LINK …</c> is the acceptor's offer, proved once, only to the id meant and
    /// over the certificate shown. <c>OK @l paired {…}</c> is the verdict, taken only after this connection's proof was written
    /// and <paramref name="acceptorProofVerifies"/> — the carrier's check of the verdict's <c>proof</c> with K over T — says it
    /// verified; otherwise the connection ends with nothing taken, pinned or floored. At level 0 it is 1.1's pairing.
    /// <c>ERR @l LINK open</c> is an open acceptor (13.6.7), <c>ERR @l LINK &lt;class&gt;</c> a refusal, and any other ERR an
    /// acceptor that does not speak LINK (13.6.8). The carrier passes only the lines that answer this connection's LINK asks.
    /// </summary>
    public void LineAnswer(LinkNow now, string line, Func<string, bool>? acceptorProofVerifies = null)
    {
        Advance(now);
        if (_phase is Phase.Idle or Phase.Dialling && !UpFromDial(now, greet: true)) return;
        if (_refused is not null || _c is null) return;
        var ok = line.StartsWith("OK ", StringComparison.Ordinal);
        if (!ok && !line.StartsWith("ERR ", StringComparison.Ordinal))
        {
            Malformed(now);
            return;
        }
        var rest = line[(ok ? 3 : 4)..];
        if (rest.StartsWith('@')) rest = rest.IndexOf(' ', StringComparison.Ordinal) is var sp and > 0 ? rest[(sp + 1)..] : "";
        if (ok && rest.StartsWith("LINK ", StringComparison.Ordinal))
        {
            Offered(now, PeerLink.ReadOfferLine(line));
            return;
        }
        if (ok && (rest == "paired" || rest.StartsWith("paired ", StringComparison.Ordinal)))
        {
            if (_c.Legacy && !_c.ProofWritten)
            {
                // Level 0: 1.1's pairing. Nothing proves the acceptor (13.6.8), and the link says so: legacy, amber.
                Linked(now);
                return;
            }
            TakeVerdict(now, rest.IndexOf('{', StringComparison.Ordinal) is var brace and >= 0 ? PeerLink.ObjectOf(rest[brace..]) : null, acceptorProofVerifies);
            return;
        }
        if (ok)
        {
            Malformed(now);
            return;
        }
        if (rest.StartsWith("LINK ", StringComparison.Ordinal))
        {
            TakeRefusal(now, rest[5..].Split(' ')[0], 0);
            return;
        }
        // An acceptor that answered LINK and then errs as one that does not speak it is read as neither.
        if (_c.Offer is not null || _c.ProofWritten) EndMalformed(now);
        else LevelZero(now);
    }

    /// <summary>
    /// On <c>ws</c>, the acceptor's first message is <c>link</c> (13.6.5), read by <see cref="PeerLink.ReadOfferMessage"/> —
    /// null when it could not be read. With a key the connector proves once, to the id meant over the certificate shown;
    /// with none it reads the public view keyless.
    /// </summary>
    public void LinkOffered(LinkNow now, LinkOffer? offer)
    {
        Advance(now);
        if (_phase is Phase.Idle or Phase.Dialling && !UpFromDial(now, greet: false)) return;
        if (_refused is not null || _c is null) return;
        Offered(now, offer);
    }

    /// <summary>
    /// On <c>ws</c>, the verdict (13.6.5): <c>"ok":true</c> is taken as a line's <c>OK paired</c> is — after this
    /// connection's proof, with the acceptor's proof verified by <paramref name="acceptorProofVerifies"/>; <c>"ok":false</c>
    /// refuses the key by its <c>class</c> and <c>retry</c>.
    /// </summary>
    public void Verdict(LinkNow now, JsonElement verdict, Func<string, bool>? acceptorProofVerifies = null)
    {
        Advance(now);
        if (_refused is not null || _phase is not (Phase.Handshake or Phase.Up)) return;
        if (PeerLink.Text(verdict, "type") != "auth" || !verdict.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            Malformed(now);
            return;
        }
        if (ok.ValueKind == JsonValueKind.True)
        {
            TakeVerdict(now, verdict, acceptorProofVerifies);
            return;
        }
        var retry = verdict.TryGetProperty("retry", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var seconds) ? Math.Max(0, seconds) : 0;
        TakeRefusal(now, PeerLink.Text(verdict, "class") ?? "refused", retry);
    }

    /// <summary>
    /// On <c>ws</c>, the acceptor's first message is a state frame, not <c>link</c>: an acceptor that does not speak LINK
    /// (13.6.8). After a <c>link</c> message a frame before the verdict is discarded and counted.
    /// </summary>
    public void FirstFrame(LinkNow now)
    {
        Advance(now);
        if (_phase is Phase.Idle or Phase.Dialling && !UpFromDial(now, greet: false)) return;
        if (_refused is not null || _c is null) return;
        if (_c.Offer is null && !_c.ProofWritten) LevelZero(now);
        if (_refused is null) Frame(now);
    }

    /// <summary>
    /// A line acceptor's NONCE (13.6.8): kept only as whether it was exactly 32 lowercase hex, and answered with 1.1's proof of
    /// a code only once the acceptor turns out not to speak LINK — one proof a connection, and none for any other NONCE.
    /// </summary>
    public void Nonce(LinkNow now, string nonce)
    {
        Advance(now);
        if (_phase != Phase.Handshake || _c is null) return;
        if (!_c.Legacy)
        {
            _c.NonceProvable = PeerLink.IsProvableNonce(nonce);
            return;
        }
        if (PeerLink.IsProvableNonce(nonce)) ProveLegacy(now);
    }

    /// <summary>
    /// LINK completed: both proofs checked (13.6.3). The record binds to the id and kind that answered (13.4.3); the
    /// certificate it ran over is pinned by that proof and the floor recorded (13.9.3); a new inst empties the clock
    /// (13.11.7); and written commands left unconfirmed are asked for when the acceptor keeps receipts (13.12.6).
    /// </summary>
    public void Verified(LinkNow now, string? inst = null, string? scope = null, IReadOnlyList<string>? can = null)
    {
        Advance(now);
        if (_refused is not null) return;
        if (_phase is Phase.Idle or Phase.Dialling && !UpFromDial(now, greet: false)) return;
        Linked(now);
        var c = _c!;
        c.Verified = true;
        c.Level = 1;
        c.Proof = "mutual";
        c.Keyless = c.Legacy = c.Open = false;
        c.VerifiedAt = now.Mono;
        c.StableDone = false;
        c.WentDown = false;
        c.CodePlain = c.Plain && _s.Key == "code";
        c.ReproveAt = long.MinValue;
        _lastClass = _why = null;
        _holdUntil = long.MinValue;
        if (c.Offer is { } offer)
        {
            _boundId = offer.Id;
            _boundT = offer.T;
        }
        if (c.Tls && c.Cert is { } cert) _pin = new LinkPin(NodeId, cert, PinTrust.Proof);
        _floor = new LinkFloor(c.Tls || _floor is { Tls: true }, 1, "mutual");
        if (inst is not null)
        {
            if (_peerInst is not null && inst != _peerInst) _clock.Clear();
            _peerInst = inst;
        }
        if (scope is not null) c.Scope = scope;
        if (can is not null) _can = new HashSet<string>(can.Take(PeerLink.CanWords), StringComparer.Ordinal);
        if (!_can.Contains("receipts")) return;
        var ask = _commands.Values.Where(x => x.Outcome == LinkOutcome.Unconfirmed).OrderByDescending(x => x.WrittenAt)
            .Take(PeerLink.ReceiptsAskMost).Select(x => x.Id).ToList();
        if (ask.Count > 0) Write(now, _s.Carrier == LinkCarrier.Line ? "RECEIPTS" : "receipts", null, string.Join(' ', ask));
    }

    /// <summary>
    /// A LINK verdict refused the key (13.6.4): a refused, bound, locked-out or out-of-scope key is held for the longer of
    /// <c>peerlink.hold.refused.ms</c> and the acceptor's retry (13.15.3); <c>busy</c> waits its retry; <c>malformed</c> and
    /// <c>version</c> wait 13.15.1's. A refusal ends a <c>line</c> connection; a <c>ws</c> socket stays a public viewer and
    /// the key is proved again on a fresh one when its hold or wait ends.
    /// </summary>
    public void Refused(LinkNow now, string cls, int retrySeconds)
    {
        Advance(now);
        TakeRefusal(now, cls, retrySeconds);
    }

    /// <summary>The connection ended, or a dial failed: written commands become unconfirmed and the next dial waits 13.15.1's wait.</summary>
    public void Closed(LinkNow now)
    {
        Advance(now);
        if (_phase == Phase.Idle) return;
        _why = null;
        Close(now, failed: true);
    }

    /// <summary>A complete message from the acceptor, and nothing more (13.10.2).</summary>
    public void Heard(LinkNow now) => Hear(now);

    /// <summary>A state frame. On <c>ws</c> it, and only it, makes the show live (13.10.5).</summary>
    public void Frame(LinkNow now)
    {
        if (Hear(now) && _s.Carrier == LinkCarrier.Ws) _c!.Show = LinkShow.Live;
    }

    /// <summary>A <c>ws</c> beat: <c>"live":false</c> makes the show stuck, and a plain beat never clears it (13.10.5).</summary>
    public void Beat(LinkNow now, bool live)
    {
        if (Hear(now) && _s.Carrier == LinkCarrier.Ws && !live) _c!.Show = LinkShow.Stuck;
    }

    /// <summary>The connector wrote a ping with this t1 (its wall clock, ms); its last <see cref="PeerLink.PingsMatched"/> are matched.</summary>
    public void Ping(LinkNow now, long t1)
    {
        Advance(now);
        _pings.Add(t1);
        if (_pings.Count > PeerLink.PingsMatched) _pings.RemoveAt(0);
    }

    /// <summary>
    /// A pong: its t1, t2 and t3 when given, else it answers the last ping. Only a pong that answers one of the last pings is a
    /// clock sample (13.10.2), t4 now on the wall clock; an unmatched one is counted. On a <c>line</c> link with feature
    /// <c>stuck</c> its fifth word sets the show; on <c>ws</c> a pong never clears a stuck show.
    /// </summary>
    public void Pong(LinkNow now, long? t1, long? t2, long? t3, bool stuck)
    {
        if (!Hear(now)) return;
        long? answered = t1 ?? (_pings.Count > 0 ? _pings[^1] : null);
        if (answered is { } m && _pings.Remove(m))
        {
            if (t2 is { } a && t3 is { } b)
                _clock.Sample(m * TimeSpan.TicksPerMillisecond, a * TimeSpan.TicksPerMillisecond, b * TimeSpan.TicksPerMillisecond, now.Wall * TimeSpan.TicksPerMillisecond);
        }
        else
        {
            _unmatched++;
        }
        if (_s.Carrier == LinkCarrier.Line && _can.Contains("stuck")) _c!.Show = stuck ? LinkShow.Stuck : LinkShow.Live;
    }

    /// <summary>
    /// One run of the evaluator (13.10.3–4). After more than <see cref="PeerLink.SelfGap"/> since its last run — this node
    /// paused, slept or starved — it pings every link and moves none to down until that long after; otherwise a link silent
    /// past <c>link.drop.ms</c> is closed and redialled.
    /// </summary>
    public LinkEvaluation Evaluate(LinkNow now)
    {
        Advance(now);
        long? gap = _lastEvaluate is { } last ? now.Mono - last : null;
        _lastEvaluate = now.Mono;
        var selfGap = (long)PeerLink.SelfGap.TotalMilliseconds;
        if (gap > selfGap)
        {
            _selfGapUntil = now.Mono + selfGap;
            PingedAt = now.Mono;
            return new LinkEvaluation(true, false, gap);
        }
        if (now.Mono < _selfGapUntil || _phase != Phase.Up || PeerLink.Liveness(Ms(now.Mono - _lastHeard)) != LinkState.Down)
            return new LinkEvaluation(false, false, gap);
        _why = null;
        Close(now, failed: true);
        return new LinkEvaluation(false, true, gap);
    }

    /// <summary>
    /// A hello heard (§2) for this link's node. It may raise the guard — try TLS first — and never lowers it: for a node
    /// pinned by a proof or a person the pin alone decides, and a hello that contradicts it is a note (13.4.2). An inst heard
    /// A, B, A within <c>peer.stale.ms</c> each (or two hosts from a node that sends no inst) marks the id in conflict until
    /// <c>peer.conflict.clear.ms</c> pass without the alternation (§2, 13.15.3).
    /// </summary>
    public void Hello(LinkNow now, string id, string? tls, string? inst = null, string? host = null)
    {
        Advance(now);
        if (NodeId is { } mine && mine != id) return;
        _addressHeard = true;
        if (_pin is { Trust: not PinTrust.First } p) _note = !(tls == p.Fingerprint || tls == "sha256:" + p.Fingerprint);
        else if (tls is not null) _tlsHint = true;
        var stale = (long)ShowBusLiveness.StaleAfter.TotalMilliseconds;
        var alternates = inst is not null
            ? _h1 is { } a && _h2 is { } b && now.Mono - a.At <= stale && a.At - b.At <= stale && inst == b.Inst && inst != a.Inst
            : host is not null && _h1 is { Inst: null, Host: { } h } c && h != host && now.Mono - c.At <= stale;
        if (alternates)
        {
            _markAt = now.Mono;
            _markUntil = now.Mono + (long)ShowBusPeers.ConflictClearsAfter.TotalMilliseconds;
        }
        _h2 = _h1;
        _h1 = (now.Mono, inst, host);
    }

    /// <summary>
    /// A command asked for under <paramref name="id"/>: written when the link is live or stale, its show not stuck and fewer than
    /// <see cref="PeerLink.InFlightMost"/> wait (null); otherwise not-sent, with why (13.12.3, 13.12.8). Nothing is queued.
    /// </summary>
    public LinkOutcome? Send(LinkNow now, string id, string verb = "command")
    {
        Advance(now);
        var state = State(now);
        string? why = null;
        if (_phase != Phase.Up || state is not (LinkState.Live or LinkState.Stale))
            why = _s.Name + " is " + LinkRecord.StateWord(state) + " — not sent; nothing is queued";
        else if (_c!.Keyless && !_c.Open) why = "this link reads " + _s.Name + "'s public view — not sent";
        else if (_c.Show == LinkShow.Stuck) why = _s.Name + " says its show is stuck — not sent";
        else if (InFlight >= PeerLink.InFlightMost)
            why = PeerLink.InFlightMost.ToString(CultureInfo.InvariantCulture) + " commands are waiting on " + _s.Name + "'s answers";
        var command = new Command(id, now.Mono);
        _commands[id] = command;
        if (why is not null)
        {
            Settle(command, LinkOutcome.NotSent, why);
            return LinkOutcome.NotSent;
        }
        Write(now, verb, null, id);
        return null;
    }

    /// <summary>The command with this id was written: it waits for its answer.</summary>
    public void Written(LinkNow now, string id)
    {
        Advance(now);
        _commands[id] = new Command(id, now.Mono);
    }

    /// <summary>
    /// An answer to one of its commands, read by 13.12.3. An outcome only moves forward: a waiting command takes any answer;
    /// an accepted one only a final applied or refused; an unconfirmed one only applied or refused (13.12.2). Taken only from a
    /// link up as one the connector writes commands on (<see cref="TakesAnswers"/>); any other is discarded and counted.
    /// </summary>
    public void Answer(LinkNow now, string id, LinkReading reading)
    {
        Advance(now);
        if (!TakesAnswers)
        {
            Discard();
            return;
        }
        if (!_commands.TryGetValue(id, out var command)) return;
        if (reading.Repeat) _replayed++;
        if (reading.LateMs is not null) _late++;
        Forward(command, reading);
    }

    /// <summary>
    /// A RECEIPTS answer (13.12.6): each id's receipt, or null for <c>unknown</c>, which leaves it unconfirmed. Taken, as an
    /// answer is, only from a link up as one the connector writes commands on; any other is discarded whole and counted once.
    /// </summary>
    public void Receipts(LinkNow now, IReadOnlyDictionary<string, LinkReading?> receipts)
    {
        Advance(now);
        if (!TakesAnswers)
        {
            Discard();
            return;
        }
        foreach (var (id, reading) in receipts)
            if (reading is { } r && _commands.TryGetValue(id, out var command) && command.Outcome == LinkOutcome.Unconfirmed) Forward(command, r);
    }

    /// <summary>
    /// A side call's answer (13.7.3). A 401 that asks for a relink ends S: the link closes and relinks on 13.15.1's wait, with
    /// no hold. A 401 without it holds the key, and the socket stays a public viewer until the hold ends. Anything else is the
    /// side call's own, and never moves the link.
    /// </summary>
    public void SideCall(LinkNow now, LinkReading reading)
    {
        Advance(now);
        if (reading.Relink)
        {
            if (_phase == Phase.Idle) return;
            _why = null;
            Close(now, failed: true);
            return;
        }
        if (!reading.Hold) return;
        Hold(now, "key", Math.Max(HoldRefusedMs, reading.RetryAfter * 1000L));
        if (_phase != Phase.Up) return;
        _c!.Verified = false;
        _c.Keyless = true;
        _c.ReproveAt = _holdUntil;
    }

    /// <summary>A message longer than the connector reads whole (13.14): counted and flagged; the link stays up.</summary>
    public void Oversize(LinkNow now)
    {
        Advance(now);
        _oversize++;
        if (_c is not null) _c.OversizeHere++;
    }

    /// <summary>A message the connector could not read: counted.</summary>
    public void Malformed(LinkNow now)
    {
        Advance(now);
        _malformed++;
        if (_c is not null) _c.MalformedHere++;
    }

    /// <summary>This node's own wall clock stepped: the clock's window is emptied (13.11.7).</summary>
    public void WallStepped(LinkNow now)
    {
        Advance(now);
        _clock.Clear();
    }

    /// <summary>
    /// The operator's <i>Link again now</i> (13.15.3, 13.16.6): the hold ends and the connector dials at once; a <c>ws</c>
    /// socket kept as a public viewer after its key was refused ends, so the key is proved again on a fresh one.
    /// </summary>
    public void LinkAgainNow(LinkNow now)
    {
        Advance(now);
        _holdUntil = long.MinValue;
        _nextDialAt = now.Mono;
        if (_phase == Phase.Up && _c is { Keyless: true } c && c.ReproveAt != long.MinValue) Relink(now);
    }

    /// <summary>The operator's <i>Trust certificate</i> on a <c>first</c> pin, one press beside its short fingerprint: pinned by a person (13.6.8). False when there is no first pin.</summary>
    public bool TrustCertificate()
    {
        if (_pin is not { Trust: PinTrust.First } p) return false;
        _pin = p with { Trust = PinTrust.Person };
        return true;
    }

    /// <summary>
    /// The operator's <i>Accept certificate</i> after CERTIFICATE CHANGED (13.8.3): at least 8 hex of the certificate shown
    /// now, typed — spaces dropped, either case. False, and nothing changed, unless it begins that certificate.
    /// </summary>
    public bool AcceptCertificate(string typedHex)
    {
        var hex = new string(typedHex.Where(ch => !char.IsWhiteSpace(ch)).Select(char.ToLowerInvariant).ToArray());
        if (_refused != "certificate" || _shown is null || hex.Length < 8 || !PeerLink.IsHex(hex, hex.Length) || !_shown.StartsWith(hex, StringComparison.Ordinal))
            return false;
        _pin = new LinkPin(NodeId, _shown, PinTrust.Person);
        _refused = null;
        return true;
    }

    /// <summary>
    /// The operator's <i>Accept new id</i> (13.4.3): the record binds to the id and kind that answered LINK in its place, with
    /// no pin and no floor of the old node's. False, and nothing changed, unless the link is refused as another id.
    /// </summary>
    public bool AcceptNewId()
    {
        if (_refused != "other-id" || _other is not { } other) return false;
        _boundId = other.Id;
        _boundT = other.T;
        _pin = null;
        _floor = null;
        _note = false;
        _other = null;
        _refused = null;
        return true;
    }

    /// <summary>The operator's <i>Lower floor</i>, one press (13.9.3): the floor is gone, and a floor's refusal with it.</summary>
    public void LowerFloor()
    {
        _floor = null;
        if (_refused == "floor") _refused = null;
    }

    // ---- what it is at a time ------------------------------------------------------------------------------------------

    /// <summary>13.16.1's state at <paramref name="now"/>.</summary>
    public LinkState State(LinkNow now)
    {
        Advance(now);
        if (!_s.Enabled) return LinkState.Off;
        if (_refused is not null) return LinkState.Refused;
        if (_holdUntil > now.Mono) return LinkState.Held;
        if (_markUntil > now.Mono && _phase is Phase.Idle or Phase.Dialling) return LinkState.Held;
        return _phase switch
        {
            Phase.Idle when !AddressKnown => LinkState.Finding,
            Phase.Idle => _everClosed ? LinkState.Down : LinkState.Connecting,
            Phase.Dialling or Phase.Handshake => LinkState.Connecting,
            _ => Liveness(now.Mono),
        };
    }

    /// <summary>The show's liveness the link carries (13.10.5); unknown while no link is up.</summary>
    public LinkShow Show => _phase == Phase.Up ? _c!.Show : LinkShow.Unknown;

    /// <summary>
    /// The dial the connector makes at <paramref name="now"/>: the dial out, or one its schedule lets it make now — no hold,
    /// refusal or conflict standing and its wait over (a wait or hold is over at its end); null when it makes none.
    /// </summary>
    public string? DialDue(LinkNow now)
    {
        Advance(now);
        if (_phase == Phase.Dialling) return _scheme;
        if (_phase != Phase.Idle || !_s.Enabled || _refused is not null || !AddressKnown) return null;
        if (_holdUntil > now.Mono || _markUntil > now.Mono || _nextDialAt > now.Mono) return null;
        return Scheme();
    }

    /// <summary>Whether the connector keeps a dial or a connection at <paramref name="now"/>: false once it ended one, and the carrier closes its socket.</summary>
    public bool KeepsConnection(LinkNow now)
    {
        Advance(now);
        return _phase != Phase.Idle;
    }

    /// <summary>The wait 13.15.1 gave the last redial, in ms.</summary>
    public long WaitMs => _waitMs;

    /// <summary>Failed dials since the link was last stable (13.15.1's n).</summary>
    public int Attempt => _failed;

    /// <summary>The wall time a key's hold ends; null with none.</summary>
    public long? HeldUntilWall(LinkNow now) => _holdUntil > now.Mono ? _holdUntilWall : null;

    /// <summary>Whether the connector takes the link's facts — state, context — now: a link up and not refused.</summary>
    public bool Follows(LinkNow now) => State(now) != LinkState.Refused && _phase == Phase.Up;

    /// <summary>How many commands wait for answers (13.12.8).</summary>
    public int InFlight => _commands.Values.Count(InFlightNow);

    /// <summary>A command's outcome by its id; null while it waits for its first answer or is not known here.</summary>
    public LinkOutcome? OutcomeOf(string id) => _commands.TryGetValue(id, out var c) ? c.Outcome : null;

    /// <summary>A command's words: why it was not sent, or what its answer said.</summary>
    public string WordsOf(string id) => _commands.TryGetValue(id, out var c) ? c.Words : "";

    /// <summary>
    /// 13.11.6: a timed command may use the offset only when it is fit for <paramref name="toleranceMs"/> and was measured over
    /// TLS or a local link; a clock measured over plain off this machine never schedules.
    /// </summary>
    public bool CanSchedule(LinkNow now, long toleranceMs)
        => _phase == Phase.Up && (_c!.Tls || _s.Local) && _clock.IsFitFor(TimeSpan.FromMilliseconds(toleranceMs), now.Wall * TimeSpan.TicksPerMillisecond);

    /// <summary>The next request id, <c>&lt;prefix&gt;&lt;n&gt;-&lt;inst&gt;</c> (13.12.1).</summary>
    public string NextId() => PeerLink.RequestId(_s.IdPrefix, ++_nextId, _s.Inst);

    /// <summary>What to write since the last drain, in order.</summary>
    public IReadOnlyList<LinkWrite> Drain()
    {
        var writes = _writes.ToList();
        _writes.Clear();
        return writes;
    }

    /// <summary>13.16.2's flags at <paramref name="now"/>, in its order.</summary>
    public IReadOnlyList<string> Flags(LinkNow now)
    {
        Advance(now);
        var c = _c;
        var up = _phase == Phase.Up && c is not null;
        var f = new List<string>();
        if (up && c!.Plain) f.Add("plain");
        if (up && c!.Keyless) f.Add("keyless");
        if (c is { Legacy: true } && _phase is Phase.Handshake or Phase.Up) f.Add("legacy");
        if (up && c!.Open) f.Add("open");
        if (up && c!.CodePlain) f.Add("code-plain");
        if (up && c!.Verified && Rank(c.Scope) < Rank(_s.Uses ?? "")) f.Add("scope");
        if (Tls() == "first") f.Add("first");
        if (_note) f.Add("note");
        if (_clock.IsApart) f.Add("apart");
        if (up && _s.TimedToleranceMs is { } tolerance && !CanSchedule(now, tolerance)) f.Add("unfit");
        if (c is { OversizeHere: > 0 }) f.Add("oversize");
        if (c is { MalformedHere: > 0 }) f.Add("malformed");
        return f;
    }

    /// <summary>
    /// The link's light at <paramref name="now"/> (13.16.1–2): the worse of its state's and its flags' — amber also while its
    /// show is stuck, its id is in conflict, or its dial has gone unanswered past <see cref="PeerLink.Handshake"/> — and red
    /// only when the show needs the link, a refused one included.
    /// </summary>
    public CheckLight Light(LinkNow now)
    {
        var state = State(now);
        var light = state switch
        {
            LinkState.Off or LinkState.Finding => CheckLight.Grey,
            LinkState.Connecting => _phase != Phase.Idle && now.Mono - _attemptAt > HandshakeMs ? CheckLight.Amber : CheckLight.Grey,
            LinkState.Live => CheckLight.Green,
            LinkState.Stale => CheckLight.Amber,
            _ => CheckLight.Red,
        };
        var amber = Flags(now).Any(flag => flag switch
        {
            "plain" => !_s.Local,
            "keyless" or "open" => _s.Key is not null,
            "legacy" or "code-plain" or "scope" or "apart" or "oversize" => true,
            _ => false,
        }) || Show == LinkShow.Stuck || _markUntil > now.Mono;
        if (amber && light is CheckLight.Grey or CheckLight.Green) light = CheckLight.Amber;
        return light == CheckLight.Red && !_s.ShowNeeds ? CheckLight.Amber : light;
    }

    /// <summary>13.16.3's <c>refusal</c>: why it is refused, a held key's word, <c>conflict</c> while §2's mark holds it, or the last verdict's class.</summary>
    public string Refusal(LinkNow now)
    {
        var state = State(now);
        if (state == LinkState.Refused) return _refused!;
        if (state == LinkState.Held) return _holdUntil > now.Mono ? _holdRefusal! : "conflict";
        return _lastClass ?? "";
    }

    /// <summary>
    /// 13.16.3's <c>tls</c>: <c>local</c>, <c>pinned</c>, <c>first</c> or <c>none</c>. On a connection, pinned or first only
    /// while the certificate it shows is the pin's: a pin names its certificate, and no other is pinned by it.
    /// </summary>
    public string Tls()
    {
        if (_s.Local) return "local";
        if (_phase is Phase.Handshake or Phase.Up && _c is { } c)
        {
            if (!c.Tls) return "none";
            return _pin is { } p && c.Cert == p.Fingerprint ? p.Trust == PinTrust.First ? "first" : "pinned" : "none";
        }
        return _pin is null ? "none" : _pin.Trust == PinTrust.First ? "first" : "pinned";
    }

    /// <summary>The pin as it stands: the certificate, the node and who made it; null with none.</summary>
    public LinkPin? Pin => _pin;

    /// <summary>13.16.3's record at <paramref name="now"/>.</summary>
    public LinkRecord Record(LinkNow now)
    {
        var state = State(now);
        var light = Light(now);
        var known = _clock.Known;
        long? next = _phase == Phase.Idle && _refused is null ? Math.Max(0, Math.Max(_nextDialAt, Math.Max(_holdUntil, _markUntil)) - now.Mono) : null;
        var clock = new LinkRecordClock(
            known ? _clock.Offset.TotalMilliseconds : null,
            known ? (long)_clock.Error.TotalMilliseconds : null,
            _clock.Age(now.Wall * TimeSpan.TicksPerMillisecond) is { } age ? (long)age.TotalMilliseconds : null,
            _clock.Samples,
            _clock.IsApart);
        return new LinkRecord(NodeKind, NodeId, _s.Name, _s.Carrier == LinkCarrier.Line ? "line" : "ws", Math.Max(0, _c?.Level ?? 0), Tls(),
            _pin is { } pin ? pin.Fingerprint[..16] : "", _c?.Proof ?? "none", _s.Key ?? "", _c?.Scope ?? "none",
            LinkRecord.StateWord(state), Refusal(now), LinkRecord.ShowWord(Show), Flags(now), LinkRecord.LightWord(light), LinkRecord.MarkOf(light), Words(now, state),
            Since(now, state) + _wallOffset, _phase == Phase.Up ? now.Mono - _lastHeard : null, _failed, next, clock,
            new LinkCounts(_unconfirmed, _late, _replayed, _oversize, _malformed, _unmatched), _floor);
    }

    // ---- inside --------------------------------------------------------------------------------------------------------

    private static readonly long HandshakeMs = (long)PeerLink.Handshake.TotalMilliseconds;
    private static readonly long HoldRefusedMs = (long)StagewatchFrame.RefusedHold.TotalMilliseconds;
    private static readonly long ConnectMs = (long)StagewatchFrame.DialWithin.TotalMilliseconds;
    private static readonly long DeadlineMs = (long)PeerLink.ReceiptDeadline.TotalMilliseconds;
    private static readonly long StableMs = (long)PeerLink.Stable.TotalMilliseconds;
    private static readonly long FinalMs = (long)PeerLink.ReceiptFinal.TotalMilliseconds;
    private static readonly long StaleMs = (long)ShowBusLiveness.StaleAfter.TotalMilliseconds;
    private static readonly long DropMs = (long)ShowBusLiveness.DropAfter.TotalMilliseconds;

    private static TimeSpan Ms(long ms) => TimeSpan.FromMilliseconds(ms);

    private bool AddressKnown => _s.Address is not null || _addressHeard;

    private static int Rank(string? scope) => scope switch
    {
        "follow" => 1,
        "post" => 2,
        "drive" => 3,
        "operate" => 4,
        _ => 0,
    };

    /// <summary>A dial's scheme: TLS on <c>ws</c>, and on <c>line</c> unless the acceptor offered none and nothing — a pin, a floor, a hello — asks for it.</summary>
    private string Scheme()
    {
        if (_s.Carrier == LinkCarrier.Ws) return "tls";
        return _pin is not null || _floor is { Tls: true } || _tlsHint || !_noTls ? "tls" : "plain";
    }

    /// <summary>13.10.3 with 13.10.4: a link past <c>link.drop.ms</c> shows stale while its evaluator's own gap holds.</summary>
    private LinkState Liveness(long now)
    {
        var state = PeerLink.Liveness(Ms(now - _lastHeard));
        return state == LinkState.Down && now < _selfGapUntil ? LinkState.Stale : state;
    }

    /// <summary>A command waits while it has no answer, or an accepted one from an acceptor that sends its final answer (feature <c>final</c>).</summary>
    private bool InFlightNow(Command c) => c.Outcome is null || (c.Outcome == LinkOutcome.Accepted && _can.Contains("final"));

    /// <summary>A time on the monotonic clock as a now, its wall time by the last offset seen: a deadline acts at its own due time.</summary>
    private LinkNow At(long mono) => new(mono, mono + _wallOffset);

    /// <summary>
    /// The deadlines that fell due by <paramref name="now"/>, each acting at its own due time: a dial with no connection
    /// (13.15.1); a line acceptor that answered LINK nothing within <c>receipt.deadline.ms</c>, which does not speak it
    /// (13.6.8); a connection that has not finished LINK within <c>peerlink.handshake.ms</c> (13.6.3); 13.15.2's stable reset;
    /// a public viewer's key proved again once its hold or wait ends (13.15.3); and 13.12.4's final answer.
    /// </summary>
    private void Advance(LinkNow now)
    {
        if (now.Mono < _now) throw new ArgumentOutOfRangeException(nameof(now), "a link's time only moves forward");
        _now = now.Mono;
        _wallOffset = now.Wall - now.Mono;
        if (_phase == Phase.Dialling && now.Mono - _phaseAt > ConnectMs)
        {
            _why = "no connection in " + (ConnectMs / 1000).ToString(CultureInfo.InvariantCulture) + " s";
            Close(At(_phaseAt + ConnectMs + 1), failed: true);
        }
        if (_phase == Phase.Handshake && _c is { Greeted: true, Offer: null, Legacy: false } g && _s.Carrier == LinkCarrier.Line && now.Mono - g.GreetAt > DeadlineMs)
            LevelZero(At(g.GreetAt + DeadlineMs + 1));
        if (_phase == Phase.Handshake && now.Mono - _phaseAt > HandshakeMs)
        {
            _why = "no link in " + (HandshakeMs / 1000).ToString(CultureInfo.InvariantCulture) + " s";
            Close(At(_phaseAt + HandshakeMs + 1), failed: true);
        }
        if (_phase == Phase.Up && _c is { Verified: true, StableDone: false } v && now.Mono >= v.VerifiedAt + StableMs)
        {
            if (!v.WentDown && v.VerifiedAt + StableMs - _lastHeard <= DropMs) _failed = 0;
            v.StableDone = true;
        }
        if (_phase == Phase.Up && _c is { Keyless: true } k && k.ReproveAt != long.MinValue && now.Mono >= k.ReproveAt) Relink(At(k.ReproveAt));
        foreach (var c in _commands.Values.Where(x => InFlightNow(x) && now.Mono - x.WrittenAt > FinalMs).ToList())
            Settle(c, LinkOutcome.Unconfirmed, PeerLink.NoFinalWords);
    }

    /// <summary>A connection comes up on the dial's scheme, or on the one its schedule would dial when no dial was seen.</summary>
    private bool UpFromDial(LinkNow now, bool greet)
    {
        if (_phase == Phase.Idle) _scheme = Scheme();
        return Up(now, _scheme != "plain", greet);
    }

    /// <summary>A connection comes up with nothing of the last one's; false when the link is refused and nothing more is done.</summary>
    private bool Up(LinkNow now, bool tls, bool greet)
    {
        if (_refused is not null) return false;
        if (_phase is Phase.Handshake or Phase.Up) return true;
        if (_phase == Phase.Idle) _attemptAt = now.Mono;
        _scheme = tls ? "tls" : "plain";
        _phase = Phase.Handshake;
        _phaseAt = now.Mono;
        _c = new Connection { Tls = tls, Plain = !tls && !_s.Local && _s.Carrier == LinkCarrier.Line };
        _lastHeard = now.Mono;
        if (greet && _s.Carrier == LinkCarrier.Line) Greet(now);
        return true;
    }

    /// <summary>On <c>line</c>, once a connection: HELLO, then the LINK ask, which carries no credential (13.6.4).</summary>
    private void Greet(LinkNow now)
    {
        if (_c is null || _c.Greeted) return;
        _c.Greeted = true;
        _c.GreetAt = now.Mono;
        Write(now, "HELLO", null);
        Write(now, "LINK", null);
    }

    /// <summary>
    /// The acceptor's answer to LINK: one a connection, of a shape the connector reads, or the connection ends as malformed.
    /// A version it did not offer is refused (by a floor, or as <c>version</c>); another id than the one meant is refused,
    /// naming both, before anything is proved (13.4.3); a certificate other than the one this connection showed is another
    /// machine between us, and nothing is proved. Then the connector proves once (13.6.3), or with no key reads the public
    /// view on <c>ws</c>.
    /// </summary>
    private void Offered(LinkNow now, LinkOffer? offer)
    {
        var c = _c!;
        if (offer is null || c.Offer is not null || c.ProofWritten || c.AuthProofWritten)
        {
            EndMalformed(now);
            return;
        }
        if (offer.Speaks != PeerLink.Version)
        {
            if (_floor is { Level: >= 1 })
            {
                Refuse(now, "floor");
                return;
            }
            _lastClass = "version";
            _why = null;
            Close(now, failed: true);
            return;
        }
        if (NodeId is { } meant && (offer.Id != meant || (NodeKind is { } kind && offer.T != kind)))
        {
            _other = offer;
            Refuse(now, "other-id");
            return;
        }
        c.Offer = offer;
        c.Legacy = false;
        c.Level = -1;
        c.Proof = "none";
        if (_s.Key is null)
        {
            if (_s.Carrier == LinkCarrier.Ws)
            {
                Linked(now);
                c.Keyless = true;
                return;
            }
            _why = "it asks for a key this desk does not hold";
            Close(now, failed: true);
            return;
        }
        var shown = c.Tls ? c.Cert is { } cert ? PeerLink.Bind(cert) : null : "plain";
        if (shown is null || offer.Bind != shown)
        {
            _why = PeerLink.ProofWords;
            Close(now, failed: true);
            return;
        }
        c.ProofWritten = true;
        Write(now, _s.Carrier == LinkCarrier.Line ? "LINK" : "link", "proof");
    }

    /// <summary>
    /// A verdict that takes the key: one a connection, after this connection's proof was written, with the acceptor's proof
    /// checked by the carrier. Anything less ends the connection as a proof that did not verify: nothing taken, pinned or floored.
    /// </summary>
    private void TakeVerdict(LinkNow now, JsonElement? verdict, Func<string, bool>? acceptorProofVerifies)
    {
        if (_c!.Verified)
        {
            EndMalformed(now);
            return;
        }
        if (!_c.ProofWritten || verdict is not { } v || PeerLink.Text(v, "proof") is not { } proof || !PeerLink.IsHex(proof, 64)
            || acceptorProofVerifies is null || !acceptorProofVerifies(proof))
        {
            _lastClass = null;
            _why = PeerLink.ProofWords;
            Close(now, failed: true);
            return;
        }
        var can = v.TryGetProperty("can", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new List<string>();
        Verified(now, PeerLink.Text(v, "inst"), PeerLink.Text(v, "scope"), can);
    }

    /// <summary>The refusal a LINK verdict carries (see <see cref="Refused"/>); <c>open</c> refuses nothing (13.6.7).</summary>
    private void TakeRefusal(LinkNow now, string cls, int retrySeconds)
    {
        if (_phase is Phase.Idle or Phase.Dialling && !UpFromDial(now, greet: false)) return;
        if (_refused is not null || _c is null) return;
        if (!PeerLink.VerdictClasses.Contains(cls))
        {
            // 13.6.4's classes are a closed set and 13.16.3's refusal records only them: any other word is a malformed verdict, counted.
            Discard();
            cls = "malformed";
        }
        if (cls == "open")
        {
            Open(now);
            return;
        }
        _why = null;
        var word = cls == "refused" ? "key" : cls;
        var held = true;
        if (cls is "refused" or "binding" or "locked" or "scope") Hold(now, word, Math.Max(HoldRefusedMs, retrySeconds * 1000L));
        else if (cls == "busy" && retrySeconds > 0) Hold(now, word, retrySeconds * 1000L);
        else
        {
            _lastClass = word;
            held = false;
        }
        _c.Verified = false;
        if (_s.Carrier == LinkCarrier.Line)
        {
            Close(now, failed: true);
            return;
        }
        Linked(now);
        _c.Keyless = true;
        _c.Level = 1;
        _c.Proof = "none";
        if (held)
        {
            _c.ReproveAt = _holdUntil;
            return;
        }
        _waitMs = (long)ShowBusLiveness.Redial(_failed, _j()).TotalMilliseconds;
        _failed++;
        _c.ReproveAt = now.Mono + _waitMs;
    }

    /// <summary>The link is up for its facts from now; its silence counts from now.</summary>
    private void Linked(LinkNow now)
    {
        if (_phase != Phase.Up) _recoveredAt = now.Mono;
        _phase = Phase.Up;
        _lastHeard = now.Mono;
    }

    /// <summary>13.6.7: an open acceptor — refused where a floor stands, else up unverified with the flag <c>open</c>.</summary>
    private void Open(LinkNow now)
    {
        if (_floor is not null)
        {
            Refuse(now, "floor");
            return;
        }
        Linked(now);
        var c = _c!;
        c.Open = true;
        c.Verified = false;
        c.Level = 1;
        c.Proof = "open";
        _lastClass = null;
    }

    /// <summary>
    /// 13.6.8, an acceptor that does not speak LINK. Floors first; then on <c>line</c> the legacy proof, for a NONCE heard on
    /// this connection; on <c>ws</c> the bearer only over TLS to the certificate a proof or a person pinned for the node, else
    /// the keyless public view, its certificate pinned <c>first</c> — compared, never trusted.
    /// </summary>
    private void LevelZero(LinkNow now)
    {
        if (_floor is { Level: >= 1 })
        {
            Refuse(now, "floor");
            return;
        }
        var c = _c!;
        c.Legacy = true;
        c.Level = 0;
        c.Proof = "legacy";
        if (_s.Carrier == LinkCarrier.Line)
        {
            if (c.NonceProvable) ProveLegacy(now);
            return;
        }
        Linked(now);
        if (_s.Key is not null && c.Tls && c.Cert is { } shown && _pin is { Trust: not PinTrust.First } p && p.Fingerprint == shown)
        {
            c.Keyless = false;
            Write(now, null, "bearer");
            return;
        }
        c.Keyless = true;
        if (c.Tls && c.Cert is { } seen && (_pin is null || (_pin.Trust == PinTrust.First && _pin.Fingerprint != seen)))
            _pin = new LinkPin(NodeId, seen, PinTrust.First);
    }

    /// <summary>13.6.8's AUTH-PROOF for a code, once a connection: each nonce answers one proof.</summary>
    private void ProveLegacy(LinkNow now)
    {
        var c = _c!;
        if (_s.Key != "code" || c.AuthProofWritten || c.ProofWritten) return;
        c.AuthProofWritten = true;
        Write(now, "AUTH-PROOF", "auth-proof");
    }

    /// <summary>13.16.1's refused state: a person must act. The connection ends; nothing more is sent or dialled.</summary>
    private void Refuse(LinkNow now, string why)
    {
        _refused = why;
        _refusedAt = now.Mono;
        _why = null;
        Close(now, failed: false);
    }

    /// <summary>An answer to LINK the connector cannot take: counted, and the connection ends as a failed dial.</summary>
    private void EndMalformed(LinkNow now)
    {
        _malformed++;
        _lastClass = "malformed";
        _why = null;
        Close(now, failed: true);
    }

    /// <summary>A <c>ws</c> socket kept as a public viewer after its key was refused ends, and the connector dials a fresh one at once (13.15.3).</summary>
    private void Relink(LinkNow now)
    {
        _why = null;
        Close(now, failed: false);
    }

    /// <summary>
    /// The connection ends: written commands become unconfirmed (13.15.5) and nothing of it is kept. A failed dial waits
    /// 13.15.1's wait; an end the connector chose dials again at once; a refusal dials nothing until a person acts.
    /// </summary>
    private void Close(LinkNow now, bool failed)
    {
        if (_phase == Phase.Idle) return;
        foreach (var c in _commands.Values.Where(InFlightNow).ToList())
            Settle(c, LinkOutcome.Unconfirmed, "the link dropped before " + _s.Name + " answered");
        _phase = Phase.Idle;
        _phaseAt = now.Mono;
        _everClosed = true;
        _closedAt = now.Mono;
        _c = null;
        _pings.Clear();
        if (_refused is not null) return;
        if (!failed)
        {
            _nextDialAt = now.Mono;
            return;
        }
        _waitMs = (long)ShowBusLiveness.Redial(_failed, _j()).TotalMilliseconds;
        _failed++;
        _nextDialAt = now.Mono + _waitMs;
    }

    private void Hold(LinkNow now, string word, long ms)
    {
        _holdAt = now.Mono;
        _holdUntil = now.Mono + ms;
        _holdUntilWall = now.Wall + ms;
        _holdRefusal = word;
    }

    /// <summary>
    /// A complete message heard. Before the link is up — the acceptor's proof not verified, a level-0 pairing not settled —
    /// it is discarded and counted (13.6.3), and nothing of it is taken.
    /// </summary>
    private bool Hear(LinkNow now)
    {
        Advance(now);
        if (_phase == Phase.Handshake)
        {
            _malformed++;
            _c!.MalformedHere++;
            return false;
        }
        if (_phase != Phase.Up) return false;
        var silence = now.Mono - _lastHeard;
        if (silence > DropMs) _c!.WentDown = true;
        if (silence > StaleMs) _recoveredAt = now.Mono;
        _lastHeard = now.Mono;
        return true;
    }

    /// <summary>
    /// Whether the connection's answers to commands are taken now: only on a link up as one the connector writes commands on —
    /// verified, up at level 0, or open — never during the handshake, before the acceptor's proof verifies (13.6.3), and never
    /// on a socket kept as a public viewer. So no answer moves a command an earlier connection left unconfirmed until the link
    /// is up again.
    /// </summary>
    private bool TakesAnswers => _phase == Phase.Up && _c is { } c && (!c.Keyless || c.Open);

    /// <summary>A message the connector does not take: discarded, and counted as malformed on the link and on this connection.</summary>
    private void Discard()
    {
        _malformed++;
        if (_c is not null) _c.MalformedHere++;
    }

    private void Forward(Command c, LinkReading r)
    {
        if (c.Outcome is null)
        {
            Settle(c, r.Outcome, r.Words);
            return;
        }
        if (c.Outcome is LinkOutcome.Accepted or LinkOutcome.Unconfirmed && r.Outcome is LinkOutcome.Applied or LinkOutcome.Refused
            && (r.Final || c.Outcome == LinkOutcome.Unconfirmed || !_can.Contains("final")))
            Settle(c, r.Outcome, r.Words);
    }

    private void Settle(Command c, LinkOutcome outcome, string words)
    {
        if (outcome == LinkOutcome.Unconfirmed && c.Outcome != LinkOutcome.Unconfirmed) _unconfirmed++;
        c.Outcome = outcome;
        c.Words = words;
        if (InFlightNow(c)) return;
        c.Left = ++_leftSeq;
        Trim();
    }

    /// <summary>
    /// The commands kept: every one in flight — nothing waiting is evicted (13.12.8) — and the last
    /// <see cref="PeerLink.ReceiptsKept"/> to stop waiting, for their outcomes and a RECEIPTS ask.
    /// </summary>
    private void Trim()
    {
        if (_commands.Count <= PeerLink.ReceiptsKept + PeerLink.InFlightMost) return;
        var done = _commands.Values.Where(x => !InFlightNow(x)).OrderBy(x => x.Left).ToList();
        foreach (var x in done.Take(done.Count - PeerLink.ReceiptsKept)) _commands.Remove(x.Id);
    }

    private void Write(LinkNow now, string? verb, string? credential, string? detail = null) => _writes.Add(new LinkWrite(now.Mono, verb, credential, detail));

    private long Since(LinkNow now, LinkState state) => state switch
    {
        LinkState.Refused => _refusedAt,
        LinkState.Held => _holdUntil > now.Mono ? _holdAt : Math.Max(_markAt, _closedAt),
        LinkState.Live => _recoveredAt,
        LinkState.Stale => Math.Max(_recoveredAt, _lastHeard + StaleMs + 1),
        LinkState.Down when _phase == Phase.Up => _lastHeard + DropMs + 1,
        _ => _phaseAt,
    };

    /// <summary>A fingerprint as the desk shows it beside a Trust press: its first 16 hex in fours.</summary>
    public static string ShortFingerprint(string fingerprint)
        => string.Join(' ', Enumerable.Range(0, Math.Min(4, fingerprint.Length / 4)).Select(i => fingerprint.Substring(i * 4, 4)));

    private static string Brief(string? id) => id is null ? "?" : (id.Length > 4 ? id[..4] : id) + "…";

    private static string Seconds(long ms) => (ms / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s";

    /// <summary>Why a link up with a key reads the public view keyless: 13.3.2's no TLS, 13.6.8's unpinned level 0, or a refused key.</summary>
    private string KeylessWords(Connection c)
    {
        var name = _s.Name;
        if (_s.Key is null) return name + " · public view";
        if (!c.Tls && !_s.Local) return name + " offers no TLS — the key stays on this machine";
        if (c.Legacy)
            return name + " cannot prove itself yet (an older Stagewatch) — "
                + (c.Cert is { } shown ? "compare its fingerprint " + ShortFingerprint(shown) + " and press Trust to send the key" : "reading its public view");
        return name + " did not take this desk's key (" + (_lastClass ?? _holdRefusal ?? "key") + ") — public view until it proves it again";
    }

    private string LiveWords(Connection c)
    {
        var name = _s.Name;
        var words = c.Proof switch
        {
            "mutual" => string.Join(" · ", new[]
            {
                name,
                _pin is { } pin ? "pinned " + ShortFingerprint(pin.Fingerprint)[..9] : "proved on this machine",
                _s.Key is { } kid ? (kid == "code" ? "code" : "key " + kid[..4] + "…") : "",
                c.Scope is { } scope ? "scope " + scope : "",
                "PeerLink " + PeerLink.Version.ToString(CultureInfo.InvariantCulture),
            }.Where(w => w.Length > 0)),
            "legacy" when c.Keyless => KeylessWords(c),
            "legacy" => name + " cannot prove itself yet — an older build; anything at its address could answer",
            "open" => name + " asks for no key now — anything at its address could answer",
            _ => c.Keyless ? KeylessWords(c) : name + " · public view",
        };
        return c.Show == LinkShow.Stuck ? words + " — says its show is stuck" : words;
    }

    private string Words(LinkNow now, LinkState state)
    {
        var name = _s.Name;
        return state switch
        {
            LinkState.Off => "link off",
            LinkState.Finding => "listening for " + name + " on the show network",
            LinkState.Connecting => "dialling " + name + (_s.Address is { } at ? " at " + at : "") + (_scheme == "plain" ? "" : " over TLS")
                + " (attempt " + (_failed + 1).ToString(CultureInfo.InvariantCulture) + ")",
            LinkState.Live => LiveWords(_c!),
            LinkState.Stale => name + " quiet for " + Seconds(now.Mono - _lastHeard),
            LinkState.Down => name + (_why is { } why ? ": " + why + " —" : " not answering —") + " dialling again in "
                + Seconds(Math.Max(0, _nextDialAt - now.Mono)) + "; nothing is queued",
            LinkState.Held when _holdUntil > now.Mono => name + " refused this desk's key (" + _holdRefusal + ") — next try in " + Seconds(_holdUntil - now.Mono),
            LinkState.Held => "two machines say they are " + (NodeId ?? name) + " — no new link until that clears",
            _ => _refused switch
            {
                "certificate" => "CERTIFICATE CHANGED: " + name + " now shows " + (_shown is { Length: >= 8 } s ? ShortFingerprint(s)[..9] : "another")
                    + " … — nothing sent; accept it on the Nodes page",
                "floor" => name + " used to prove itself with PeerLink " + PeerLink.Version.ToString(CultureInfo.InvariantCulture)
                    + (_floor is { Tls: true } ? " over TLS" : "") + "; it now offers less — not linking",
                _ => (_s.Address ?? name) + " now answers as " + (_other is { } o ? o.T + " " + Brief(o.Id) : "another node") + " — not " + name + " "
                    + Brief(NodeId) + "; accept it on the Nodes page",
            },
        };
    }
}

/// <summary>
/// Show Bus 1.3 §13.12.6, an acceptor's record of what it ran: per verified connector (<c>t:id</c>), the receipts of its last
/// <see cref="PeerLink.ReceiptsKept"/> ids for <see cref="PeerLink.ReceiptsKeptFor"/>. A command whose id it holds never runs
/// again: it is answered from the record with <c>"repeat":true</c>, or accepted while it still runs. A receipt is kept as its
/// fields alone (<c>{"ok":…,"stage":…,"effect":…}</c> or <c>{"ok":false,"error":…}</c>), which the acceptor's carrier wraps, and
/// answered byte for byte as it was kept. The record holds a book only for a connector with an id inside
/// <see cref="PeerLink.ReceiptsKeptFor"/>: a book empty after its expiry purge is dropped, so many connectors that prove one
/// code leave nothing behind once their receipts expire. One writer: the acceptor's lane that reads commands.
/// </summary>
public sealed class ReceiptBook
{
    /// <summary>What the record answers for an id it holds that still runs: accepted.</summary>
    public const string Running = "{\"ok\":true,\"stage\":\"accepted\",\"effect\":\"\"}";

    private sealed class Book
    {
        public Dictionary<string, (long At, string? Receipt)> ById { get; } = new(StringComparer.Ordinal);
        public Queue<string> Order { get; } = new();
    }

    private readonly Dictionary<string, Book> _books = new(StringComparer.Ordinal);

    /// <summary>How many connectors the record holds a book for now: each with at least one id inside its keeping time.</summary>
    public int Connectors => _books.Count;

    /// <summary>
    /// Before running <paramref name="id"/> from <paramref name="connector"/>: the answer when the record holds it — its
    /// receipt with <c>"repeat":true</c>, or <see cref="Running"/>'s while it runs — or null, and then the id is held as
    /// running until <see cref="Keep"/>.
    /// </summary>
    public string? Begin(string connector, string id, long nowMs)
    {
        var book = Of(connector, nowMs);
        if (book.ById.TryGetValue(id, out var held)) return Repeat(held.Receipt ?? Running);
        book.ById[id] = (nowMs, null);
        book.Order.Enqueue(id);
        Bound(book);
        return null;
    }

    /// <summary>The final receipt of <paramref name="id"/>, kept for its repeats and for RECEIPTS.</summary>
    public void Keep(string connector, string id, string receipt, long nowMs)
    {
        var book = Of(connector, nowMs);
        if (!book.ById.ContainsKey(id)) book.Order.Enqueue(id);
        book.ById[id] = (nowMs, receipt);
        Bound(book);
    }

    /// <summary>
    /// A RECEIPTS answer for the connector's own ids, at most <see cref="PeerLink.ReceiptsAskMost"/>: each its final receipt as
    /// kept, <see cref="Running"/>'s while it still runs, or <c>"unknown"</c> when the record does not hold it — with this
    /// acceptor's inst, so the connector tells a restart from an id never read. Asking makes no book.
    /// </summary>
    public string Answer(string connector, IEnumerable<string> ids, string inst, long nowMs)
    {
        var book = Purged(connector, nowMs);
        var s = new StringBuilder("{\"inst\":\"").Append(JsonEncodedText.Encode(inst).ToString()).Append("\",\"receipts\":{");
        var first = true;
        foreach (var id in ids.Distinct(StringComparer.Ordinal).Take(PeerLink.ReceiptsAskMost))
        {
            if (!first) s.Append(',');
            first = false;
            s.Append('"').Append(JsonEncodedText.Encode(id).ToString()).Append("\":");
            s.Append(book is not null && book.ById.TryGetValue(id, out var held) ? held.Receipt ?? Running : "\"unknown\"");
        }
        return s.Append("}}").ToString();
    }

    /// <summary>A receipt answered from the record: <c>"repeat":true</c> appended at its end, every byte before it as kept.</summary>
    public static string Repeat(string receipt)
    {
        var end = receipt.LastIndexOf('}');
        if (end < 0) return receipt;
        var open = receipt.AsSpan(0, end).TrimEnd();
        return open.ToString() + (open.EndsWith("{", StringComparison.Ordinal) ? "" : ",") + "\"repeat\":true" + receipt[end..];
    }

    /// <summary>
    /// The connector's book to write in. A connector new to the record first has every book purged, and each empty one
    /// dropped, so the record's books are only those with an id inside its keeping time.
    /// </summary>
    private Book Of(string connector, long nowMs)
    {
        if (Purged(connector, nowMs) is { } book) return book;
        foreach (var other in _books.Keys.ToList()) Purged(other, nowMs);
        _books[connector] = book = new Book();
        return book;
    }

    /// <summary>The connector's book with its expired ids gone; null, and the book dropped, when nothing is left in it.</summary>
    private Book? Purged(string connector, long nowMs)
    {
        if (!_books.TryGetValue(connector, out var book)) return null;
        var keptMs = (long)PeerLink.ReceiptsKeptFor.TotalMilliseconds;
        while (book.Order.TryPeek(out var oldest) && (!book.ById.TryGetValue(oldest, out var held) || nowMs - held.At > keptMs))
        {
            book.Order.Dequeue();
            book.ById.Remove(oldest);
        }
        if (book.Order.Count > 0) return book;
        _books.Remove(connector);
        return null;
    }

    private static void Bound(Book book)
    {
        while (book.Order.Count > PeerLink.ReceiptsKept) book.ById.Remove(book.Order.Dequeue());
    }
}
