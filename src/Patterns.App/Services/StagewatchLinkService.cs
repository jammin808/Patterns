using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Round 87 (Show Bus 1.1): the desk's link to a Stagewatch — its WebSocket's state frames (the stage timer follows
/// them), a beat and a clock exchange every two seconds, and its context every five (the Eye draws its things, the
/// assistant's brief reads them). Off until the Nodes page turns it on; with no address it follows the first
/// Stagewatch heard on the show network. The key goes in the Authorization header, never in the URL. Stale after
/// 6 s of silence, dropped and redialled after 12 s, the redial backing off from half a second to five with jitter.
/// Nothing here blocks the UI thread: the link runs on its own task and publishes one immutable sight at a time.
///
/// Round 88 (Show Bus 1.2): over TLS when the Stagewatch's hello names a certificate — pinned per node on the first
/// link that authenticated, a different one refused as CERTIFICATE CHANGED until the operator accepts it (§10); this
/// desk's own context posted to the Stagewatch every five seconds (which also binds the key to this desk's id); and,
/// when the Stagewatch is an approval desk, the assistant's proposals sent there and the approved ones collected,
/// applied here and answered (§11).
///
/// Round 89: show liveness apart from link liveness (PeerLink profile 13.10.5). The Stagewatch's stuck beat
/// (<c>{"type":"beat","live":false}</c>) marks its show stuck: the held frame is no longer live, so the stage shows no
/// time, while the link — still answering every ping — stays live. Only its next state frame clears the mark; a plain
/// beat or a pong never does. The mark belongs to the Stagewatch that said it: a dial that reaches another one (the show
/// network can offer another after a drop) starts that one's show afresh. One log line for each change of the mark.
///
/// Round 89: the key never crosses the network in clear (PeerLink profile 13.3.2, 13.4.2, 13.6.8). The carrier is chosen
/// by the pin and a probe, never by a hello: a Stagewatch whose certificate is pinned is dialled over TLS whatever a hello
/// says (a hello that contradicts the pin is a note on the link, never a refusal), and a plain answer from it gets nothing.
/// Any other Stagewatch reached at an address that is not loopback is dialled over TLS first; one that answers without TLS
/// is read without the key — its timer followed, no context posted, no approval collected — and the link says "Hall A
/// offers no TLS — the key stays on this desk", asking it again every so often and dialling again over TLS once it shows
/// a certificate. The key goes over plain ws or http only to a loopback address: every socket of a dial goes to the one
/// address resolved for it, and that address, never a name, is what is asked. One log line for each change of the carrier.
///
/// Round 91 (Stagewatch 2.18, Show Bus 1.3 §7.1): the link dials as a node — <c>/ws?node=&lt;this desk's show network
/// id&gt;&amp;t=patterns</c> — and reads Stagewatch's verdict on its key, the first message a node link hears and sent again
/// whenever the key, its binding or the PIN changes there. Nothing keyed is sent before a connection's first message is
/// read. A refused key (<c>ok:false</c>, a 401) is held for the larger of 60 s and the Stagewatch's own <c>retry</c> or
/// <c>Retry-After</c>, recorded as its word is read and before any further I/O: no keyed call and no redial inside it, the
/// timer followed on the public frame meanwhile; a rate limit (429) holds the same and leaves the key's standing as it
/// was. A key the Stagewatch takes that speaks for no node of this desk's (verified none, a 403) holds only the node
/// routes — the context this desk posts, its proposals, the collect and the results — for as long: the binding governs
/// those, not reading (Stagewatch §7.1), so the keyed read of its context and the redial go on and its context stays
/// shown. A fresh verdict that takes the key lifts every hold of a refusal. Against a Stagewatch older than its node links,
/// which sends no verdict, the answers to the keyed calls are the evidence, and every reader says so in amber (P-legacy,
/// docs/OPEN.md L66). One log line for each change of the key's standing; Stagewatch's words with the key masked, never
/// the key.
/// </summary>
public sealed class StagewatchLinkService : IDisposable
{
    private readonly Func<StagewatchLinkConfig> _config;
    private readonly Func<DateTime, IReadOnlyList<ShowBusPeer>> _peers;
    private readonly Action _changed;
    private readonly LinkClock _clock = new();
    private CancellationTokenSource? _cts;
    private string _runningKey = "";
    private string _runningHostKey = "";

    /// <summary>
    /// One Stagewatch's show as a run has heard it, by the key it was dialled under: its context and its stuck mark. Each
    /// field has one writer — the context only the beat task's read of /api/context, the stuck mark only the receive loop
    /// — so a context landing beside a stuck beat can never put a live frame back.
    /// </summary>
    private sealed class HeardShow(string key, string name)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public volatile ShowContext? Context;
        public volatile bool Stuck;
    }

    /// <summary>
    /// One run of the link, from one Reconcile to the next. Its show is replaced only by the link task, at a connect that
    /// reaches a Stagewatch other than the one it holds, so nothing said of one Stagewatch is claimed of another; a new
    /// run starts with none, so nothing of a show outlives a change of settings.
    /// </summary>
    private sealed class Run
    {
        public volatile HeardShow? Show;

        /// <summary>Why the connection up now was ended to dial again ("" when it was not): the link's words after it ends.</summary>
        public string Redial = "";

        /// <summary>
        /// Round 91: what the Stagewatch made of this desk's key, and the holds its words started — kept across this run's
        /// connections, so a refusal said before a drop still holds the redial, and carried into the next run while it
        /// follows the same address with the same key (<see cref="Carry"/>: a pin set on a verdict starts one); a new key or
        /// address starts with none. The verdict on the socket (the receive loop) and the answers to keyed calls (the beat
        /// task) both write here, under <see cref="Gate"/>; <see cref="Said"/> orders them, so an answer to a call that left
        /// before a verdict never overrides it.
        /// </summary>
        public readonly object Gate = new();

        /// <summary>Counts verdicts and refusals (under <see cref="Gate"/>): an answer applies only while none came after its call left.</summary>
        public long Said;

        /// <summary>The show clock's ms until which keyed calls and a redial wait for a refused key (under <see cref="Gate"/>).</summary>
        public long HeldUntilMs;

        /// <summary>The show clock's ms until which keyed calls and a redial wait out a rate limit (under <see cref="Gate"/>).</summary>
        public long BusyUntilMs;

        /// <summary>The show clock's ms until which the node routes wait for a key that speaks for no node of this desk's (under <see cref="Gate"/>).</summary>
        public long NodeHeldUntilMs;

        public volatile StagewatchKey Key = StagewatchKey.Unknown;

        /// <summary>
        /// What the run before heard of the same key at the same address — its standing and its holds, whose ends are on the
        /// show clock both runs keep — so a new run presents a key held there no sooner than that run would have.
        /// </summary>
        public void Carry(Run from)
        {
            lock (from.Gate)
            {
                HeldUntilMs = from.HeldUntilMs;
                BusyUntilMs = from.BusyUntilMs;
                NodeHeldUntilMs = from.NodeHeldUntilMs;
                Key = from.Key;
            }
        }
    }

    /// <summary>
    /// Round 91: one connection of a run — what it dialled and whether it carries the key, and whether its first message is
    /// read yet: nothing keyed is sent before it, so a verdict (the first thing a node link hears) is read before any keyed
    /// call. <see cref="Legacy"/>: that first message was no verdict — a Stagewatch older than its node links.
    /// <see cref="Secret"/> is this desk's key, masked in any words the far end sends back.
    /// </summary>
    private sealed class Connection(Run run, Target target, bool tls, bool keyed, IReadOnlyDictionary<string, string> pins, Func<string> presented, string secret)
    {
        public Run Run { get; } = run;
        public Target Target { get; } = target;
        public bool Tls { get; } = tls;
        public bool Keyed { get; } = keyed;
        public IReadOnlyDictionary<string, string> Pins { get; } = pins;
        public Func<string> Presented { get; } = presented;
        public string Secret { get; } = secret;
        public volatile bool Heard;
        public volatile bool Legacy;
    }

    private volatile Run _run = new();
    private volatile StagewatchSight _sight = StagewatchSight.None;
    private volatile string _status = "Stagewatch link off.";
    private volatile string _tlsWords = "";
    private volatile string _approvalWords = "";
    private volatile string _pendingFingerprint = "";
    private volatile string _pendingPinKey = "";
    private volatile bool _approvalsLive;
    private volatile string _note = "";
    private volatile string _carrier = "";
    private string _carrierSaid = "";
    private long _frames;

    /// <param name="config">The link's settings, read on the UI thread at each reconcile.</param>
    /// <param name="peers">The show network's peers heard now (thread-safe).</param>
    /// <param name="changed">Called (posted to the UI thread) when a frame or a context lands: the stage pages read again.</param>
    public StagewatchLinkService(Func<StagewatchLinkConfig> config, Func<DateTime, IReadOnlyList<ShowBusPeer>> peers, Action changed)
    {
        _config = config;
        _peers = peers;
        _changed = changed;
    }

    /// <summary>Round 88: this desk on the show network — its id and name, the context it posts, the queue it sends to.</summary>
    public string SelfId { get; set; } = "";

    public string SelfName { get; set; } = "";

    /// <summary>Round 88: this process's own context (§6), read on the UI thread; null when it has none to give.</summary>
    public Func<string?>? OwnContext { get; set; }

    /// <summary>Round 88: what this desk has sent to the approval desk (§11).</summary>
    public DeskApprovals Approvals { get; } = new();

    /// <summary>
    /// Round 88: an approved line aimed at this desk that is not one of its assistant's proposals (another app's
    /// assistant proposed a wire verb to Patterns): run through this desk's own wire path on the UI thread, fences and
    /// all. Null: such lines are refused.
    /// </summary>
    public Func<string, Task<(bool Ok, string Words)>>? RunApprovedLine { get; set; }

    /// <summary>
    /// What the link holds now — the stage timer, the Eye and the brief read it: the frame and the link's state from the
    /// link task, the context and the stuck mark from this run's show. While the show is stuck the held frame is not live
    /// (the stage shows no time from it) and the sight says so; with no frame held only the mark is set.
    /// </summary>
    public StagewatchSight Sight
    {
        get
        {
            // The mark before the frame: every writer holds the new frame (or none) before it clears or replaces the
            // mark, so a read that finds the mark cleared finds that frame or a newer one — never a frozen frame counting.
            var show = _run.Show;
            var stuck = show is { Stuck: true };
            var context = show?.Context;
            var s = _sight;
            if (s.Name.Length == 0 && s.Address.Length == 0) return s;
            s = s with { Context = context, Key = _run.Key };
            if (stuck) s = s with { Frame = s.Frame is { } f ? f with { Live = false } : null, Stuck = true };
            // The state is the silence since the last word, read now: a link that stopped talking goes stale on its own.
            var silent = TimeSpan.FromSeconds(Math.Max(0, ShowClock.Seconds - s.FrameAtSeconds));
            var state = _lastWordAt <= 0 ? ShowBusState.Gone : ShowBusLiveness.StateOf(TimeSpan.FromSeconds(Math.Max(0, ShowClock.Seconds - _lastWordAt)));
            return s with { State = state, Words = StatusWords(state, s, silent) };
        }
    }

    private long _lastWordMs;   // the show clock's milliseconds at the last word from the Stagewatch; 0 before any

    private double _lastWordAt
    {
        get => Volatile.Read(ref _lastWordMs) / 1000.0;
        set => Volatile.Write(ref _lastWordMs, (long)(value * 1000));
    }

    /// <summary>
    /// The Nodes page's line: off, looking, following, quiet — and the clock offset when it matters; round 89, with the
    /// carrier's note when there is one (the key held back, or a hello that contradicts the pin).
    /// </summary>
    public string Status
    {
        get
        {
            var line = _status;
            // Round 91: and what the Stagewatch made of this desk's key, when there is something to say of it.
            var extra = string.Join(" · ", new[] { _note, _run.Key.Line }.Where(w => w.Length > 0));
            return extra.Length == 0 ? line : line.TrimEnd('.') + " · " + extra + ".";
        }
    }

    /// <summary>
    /// Round 91: what the Stagewatch made of this desk's key on this run — its verdict on the socket, or (P-legacy) the
    /// answers to the keyed calls of a Stagewatch that sends none. <see cref="StagewatchKey.Unknown"/> before anything said.
    /// </summary>
    public StagewatchKey Key => _run.Key;

    /// <summary>
    /// Round 91: how much longer anything keyed waits — a refused key's hold or a rate limit's (every keyed call and the next
    /// dial), or the node routes' for a key that speaks for no node of this desk's; zero when nothing holds.
    /// </summary>
    public TimeSpan KeyHeld => NodeHeldFor(_run);

    /// <summary>
    /// Round 91: the least a refused key is held — Show Bus 1.3's <c>peerlink.hold.refused.ms</c>, 60 s; a Stagewatch's own
    /// <c>retry</c> or <c>Retry-After</c> holds longer (<see cref="StagewatchFrame.Hold(int, long, TimeSpan)"/>). A test
    /// shortens it to see a hold end. Read on the link's task: set it before the link is turned on.
    /// </summary>
    public TimeSpan RefusedHoldAtLeast { get; set; } = StagewatchFrame.RefusedHold;

    /// <summary>
    /// Round 89: what the connection up now carries — "tls"; "plain" (to loopback, or with no key set); "keyless" (a
    /// Stagewatch at an address that is not loopback that offers no TLS, read without the key); "" with no connection up.
    /// </summary>
    public string Carrier => _carrier;

    /// <summary>
    /// Round 89: whether an address a dial reaches is a loopback address — the only place the key goes over plain ws or
    /// http. Asked of the address the dial's sockets go to, never of a name. A Stagewatch on this same machine reached at
    /// one of its network addresses is not loopback, so it is dialled as another machine's: TLS first, read without the
    /// key when it has none. A test plays another machine from loopback by answering false. Read on the link's task: set
    /// it before the link is turned on.
    /// </summary>
    public Func<IPAddress, bool> IsLocal { get; set; } = IPAddress.IsLoopback;

    /// <summary>
    /// Round 89: how long a TLS dial waits, from its socket's connect, for the far end's certificate. A far end that shows
    /// none by then does not speak TLS on that port (a Stagewatch with no certificate reads the TLS greeting as a request's
    /// head and waits for more), so the link's own deadline ends the attempt.
    /// </summary>
    public TimeSpan TlsAnswerWithin { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Round 89: while a Stagewatch is read without the key, how often the link asks it again whether it speaks TLS — one
    /// socket and one TLS greeting, nothing of this desk's. A certificate shown ends the keyless connection and the link
    /// dials again over TLS, so an answer that was late once does not keep the key off the link for the rest of the show.
    /// Read on the link's task: set it before the link is turned on.
    /// </summary>
    public TimeSpan TlsReprobeEvery { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The pin key of a Stagewatch whose certificate came only after <see cref="TlsAnswerWithin"/> once: its TLS dials wait
    /// the whole upgrade budget for the certificate from then on. Kept across runs: how long to wait is no fact of a show.
    /// </summary>
    private volatile string _slowTls = "";

    /// <summary>The budget of one WebSocket upgrade; a TLS dial's is cut to <see cref="TlsAnswerWithin"/> until a certificate comes.</summary>
    private static readonly TimeSpan UpgradeWithin = TimeSpan.FromSeconds(5);

    /// <summary>How long a dial waits for its host's name to resolve.</summary>
    private static readonly TimeSpan ResolveWithin = TimeSpan.FromSeconds(4);

    /// <summary>Round 88: the link's TLS in words ("" when plain): pinned, new, or CERTIFICATE CHANGED.</summary>
    public string TlsWords => _tlsWords;

    /// <summary>Round 88: the approval desk in words ("" when the Stagewatch is not one, or the link is down).</summary>
    public string ApprovalWords => _approvalWords;

    /// <summary>Round 88: the linked Stagewatch is an approval desk and this desk's key may use it: the assistant sends there.</summary>
    public bool ApprovalsLive => _approvalsLive && _sight.State == ShowBusState.Live;

    /// <summary>Round 88: a certificate the link refused as changed, waiting for the operator (the Nodes page's ACCEPT).</summary>
    public string PendingFingerprint => _pendingFingerprint;

    /// <summary>Round 88: whose certificate changed — "node:&lt;id&gt;" or "addr:&lt;host&gt;:&lt;port&gt;".</summary>
    public string PendingPinKey => _pendingPinKey;

    /// <summary>State frames taken in since the link was turned on.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>The Stagewatch's clock against this desk's, once a clock exchange has closed.</summary>
    public TimeSpan? ClockOffset => _clock.Known ? _clock.Offset : null;

    /// <summary>
    /// Round 88: ACCEPT on the Nodes page (UI thread) — the changed certificate becomes the pin, and the link dials again.
    /// Returns the words for the strip.
    /// </summary>
    public string AcceptPendingCertificate()
    {
        var fp = _pendingFingerprint;
        var key = _pendingPinKey;
        if (fp.Length == 0 || key.Length == 0) return "No changed certificate is waiting.";
        _config().SetPin(key, fp);
        _pendingFingerprint = "";
        _pendingPinKey = "";
        _runningKey = "";                                                                   // dial again with the new pin
        Reconcile();
        return $"Accepted the Stagewatch's new certificate {TlsFingerprint.Short(fp)} — the link dials again.";
    }

    /// <summary>Start, restart or stop the link to match its settings (UI thread; cheap when nothing changed).</summary>
    public void Reconcile()
    {
        var c = _config();
        var hostKey = c.Enabled ? $"{c.Host}|{Hash(c.ApiKey)}" : "";
        var key = c.Enabled ? $"{hostKey}|{c.PinsKey}" : "";
        if (key == _runningKey) return;
        var carry = hostKey.Length > 0 && hostKey == _runningHostKey;                       // the same key at the same address: a pin set, say
        _runningKey = key;
        _runningHostKey = hostKey;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        var previous = _run;
        var run = new Run();
        if (carry) run.Carry(previous);                                                     // round 91: its holds and standing go on
        _sight = StagewatchSight.None;
        _run = run;                                                                         // a new run, after its empty sight: no context, no mark
        _lastWordAt = 0;
        _approvalsLive = false;
        _approvalWords = "";
        _tlsWords = "";
        _note = "";
        _carrier = "";
        if (key.Length == 0)
        {
            _status = "Stagewatch link off.";
            _changed();
            return;
        }
        _cts = new CancellationTokenSource();
        var host = c.Host;
        var apiKey = c.ApiKey;
        var pins = c.Pins.ToDictionary(p => p.Key, p => p.Fingerprint, StringComparer.Ordinal);
        var ct = _cts.Token;
        _status = host.Length > 0 ? $"Stagewatch link: dialling {host}…" : "Stagewatch link: listening for a Stagewatch on the show network…";
        _ = Task.Run(() => RunAsync(run, host, apiKey, pins, ct), CancellationToken.None);
    }

    private static string Hash(string s) => s.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12];

    /// <summary>
    /// Where to dial and what the show network says of it. <paramref name="Heard"/>: a hello names this Stagewatch;
    /// <paramref name="Advertised"/>: the certificate that hello names ("" when none). A hello is a hint — try TLS first —
    /// and never decides the carrier: the pin and the probe do.
    /// </summary>
    private sealed record Target(string Host, int Port, string Name, string PinKey, string Advertised, bool Heard, bool Approvals)
    {
        public bool HelloTls => TlsFingerprint.IsValid(Advertised);
    }

    private async Task RunAsync(Run run, string typedHost, string apiKey, IReadOnlyDictionary<string, string> pins, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var target = Resolve(typedHost);
            if (target is null)
            {
                _status = typedHost.Length > 0
                    ? $"Stagewatch link: '{typedHost}' is not an address — host:port, or empty to find one on the show network."
                    : "Stagewatch link: no Stagewatch heard on the show network yet (UDP 9928).";
                await Delay(ShowBusLiveness.Redial(attempt++, RandomNumberGenerator.GetInt32(1000) / 1000.0), ct);
                continue;
            }
            try
            {
                await ConnectAsync(run, target, apiKey, pins, ct);
                attempt = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or SocketException or OperationCanceledException or InvalidOperationException or System.Security.Authentication.AuthenticationException)
            {
                _status = _pendingFingerprint.Length > 0
                    ? $"Stagewatch link: {target.Name} — {_tlsWords}"
                    : ex is DialRefusedException refused
                        ? $"Stagewatch link: {refused.Message}"
                        : $"Stagewatch link: {target.Name} at {target.Host}:{target.Port} — {Brief(ex)}; trying again.";
            }
            if (Interlocked.Exchange(ref run.Redial, "") is { Length: > 0 } redial) _status = "Stagewatch link: " + redial;
            _carrier = "";
            _sight = _sight with { State = ShowBusState.Gone };
            _approvalsLive = false;
            // Round 91: a hold replaces the wait (Show Bus 1.3 draft §13.15.3): a refused key, or a rate limit, is not
            // presented again by a redial inside it. It was recorded as its word was read, so a connection that ended in an
            // exception waits it as a clean end does.
            var backoff = ShowBusLiveness.Redial(attempt++, RandomNumberGenerator.GetInt32(1000) / 1000.0);
            var held = HeldFor(run);
            if (held > backoff && !_status.Contains("dialling again in", StringComparison.Ordinal))
                _status = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Stagewatch link: {target.Name} — this desk's key is held; dialling again in {Math.Ceiling(held.TotalSeconds):0} s.");
            UiThread.Post(_changed);
            await Delay(held > backoff ? held : backoff, ct);
        }
    }

    /// <summary>How much longer a refused key's hold or a rate limit's holds every keyed call and the next dial (zero when neither).</summary>
    private static TimeSpan HeldFor(Run run)
    {
        long until;
        lock (run.Gate) until = Math.Max(run.HeldUntilMs, run.BusyUntilMs);
        return Left(until);
    }

    /// <summary>How much longer the node routes wait: any hold, a key that speaks for no node of this desk's among them.</summary>
    private static TimeSpan NodeHeldFor(Run run)
    {
        long until;
        lock (run.Gate) until = Math.Max(Math.Max(run.HeldUntilMs, run.BusyUntilMs), run.NodeHeldUntilMs);
        return Left(until);
    }

    private static TimeSpan Left(long untilMs)
    {
        var left = untilMs - ClockMs();
        return left > 0 ? TimeSpan.FromMilliseconds(left) : TimeSpan.Zero;
    }

    /// <summary>The show clock in whole milliseconds: what the holds are kept on.</summary>
    private static long ClockMs() => (long)(ShowClock.Seconds * 1000);

    private static async Task Delay(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await Task.Delay(wait, ct);
        }
        catch (OperationCanceledException)
        {
            // stopped: the loop sees the token
        }
    }

    private static string Brief(Exception ex) => ex is WebSocketException { WebSocketErrorCode: WebSocketError.NotAWebSocket } || ex.Message.Contains("401", StringComparison.Ordinal) || ex.Message.Contains("403", StringComparison.Ordinal)
        ? "refused — check the Stagewatch key on the Nodes page"
        : ex.Message.Length > 120 ? ex.Message[..120] : ex.Message;

    /// <summary>
    /// The address to dial: the typed one, or the first live Stagewatch heard on the show network — with what its hello
    /// says of TLS and the approval queue. A typed address that a hello also names borrows that hello's words. Over TLS
    /// or not is the dial's to decide (the pin and the probe), never this.
    /// </summary>
    private Target? Resolve(string typed)
    {
        var now = DateTime.UtcNow;
        var live = _peers(now).Where(p => p.Kind == "main" && p.StateAt(now) == ShowBusState.Live && p.Address.Length > 0 && (p.Ports.Ws > 0 || p.Ports.Http > 0)).ToList();
        if (typed.Length > 0)
        {
            if (StageFollow.ParseAddress(typed) is not { } t) return null;
            var named = live.FirstOrDefault(p => string.Equals(p.Address, t.Host, StringComparison.OrdinalIgnoreCase) && (p.Ports.Http == t.Port || p.Ports.Ws == t.Port || p.Ports.Https == t.Port));
            if (named is not null) return Of(named, t.Host, t.Port);
            var key = "addr:" + t.Host.ToLowerInvariant() + ":" + t.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new Target(t.Host, t.Port, t.Host, key, "", false, false);
        }
        var peer = live.FirstOrDefault();
        return peer is null ? null : Of(peer, peer.Address, peer.Ports.Https > 0 && peer.OffersTls ? peer.Ports.Https : peer.Ports.Ws > 0 ? peer.Ports.Ws : peer.Ports.Http);

        static Target Of(ShowBusPeer p, string host, int port)
            => new(host, p.OffersTls && p.Ports.Https > 0 ? p.Ports.Https : port, p.Name.Length > 0 ? p.Name : p.Host, "node:" + p.Id, p.Tls, true,
                p.Caps.Contains(DeskProposals.Cap, StringComparer.Ordinal));
    }

    /// <summary>
    /// The note for a pinned Stagewatch whose hello says otherwise (13.4.2): the pin alone decides, so the link dials TLS
    /// as before and says why the hello is not followed; "" when the hello agrees, or none names it.
    /// </summary>
    private static string HelloNote(Target target, string pin)
    {
        if (!target.Heard) return "";
        if (!target.HelloTls) return $"the hello heard for {target.Name} names no TLS — a stale or forged hello; dialling TLS as before";
        return target.Advertised == pin
            ? ""
            : $"the hello heard for {target.Name} names another certificate ({TlsFingerprint.Short(target.Advertised)}) — the pin decides: dialling TLS against the one pinned";
    }

    private bool CheckCertificate(Target target, IReadOnlyDictionary<string, string> pins, X509Certificate? certificate, ref string presentedOut)
    {
        if (certificate is null) return false;
        using var c2 = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var presented = TlsFingerprint.Of(c2);
        presentedOut = presented;
        var verdict = CertificatePins.Check(pins.TryGetValue(target.PinKey, out var p) ? p : "", presented, target.Advertised);
        _tlsWords = CertificatePins.Words(verdict, "Stagewatch " + target.Name, presented);
        if (verdict == PinVerdict.Changed)
        {
            _pendingFingerprint = presented;
            _pendingPinKey = target.PinKey;
        }
        return CertificatePins.Accepts(verdict);
    }

    /// <summary>
    /// One connection. The carrier, by the pin and the probe (13.3.2, 13.4.2): a pinned Stagewatch over TLS or nothing; any
    /// other at an address that is not loopback — and one at loopback whose hello names a certificate — over TLS first,
    /// falling back to plain only when it answers without TLS, the key then sent only to loopback; one at loopback whose
    /// hello names none, plain with the key as before. Every socket of the dial goes to the one address resolved for it.
    /// </summary>
    private async Task ConnectAsync(Run run, Target target, string apiKey, IReadOnlyDictionary<string, string> pins, CancellationToken outer)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = linked.Token;
        var pin = pins.TryGetValue(target.PinKey, out var held) && TlsFingerprint.IsValid(held) ? held : "";
        var pinned = pin.Length > 0;
        _note = pinned ? HelloNote(target, pin) : "";                                   // a hello that contradicts the pin is a note, never a refusal
        var address = await DialAddressAsync(target, ct);
        var local = IsLocal(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
        var presented = "";
        DialProbe? probe = null;
        RemoteCertificateValidationCallback validate = (_, cert, _, _) =>
        {
            probe?.Certificate();
            return CheckCertificate(target, pins, cert, ref presented);
        };
        var tls = pinned || target.HelloTls || !local;
        var keyed = apiKey.Length > 0;
        (SocketsHttpHandler Handler, HttpMessageInvoker Invoker, ClientWebSocket Socket)? dial = null;
        if (tls)
        {
            // The short wait for a certificate is only there to fall back soon: a pinned Stagewatch has no fallback, and
            // one that answered late before is given the whole budget.
            var within = pinned || _slowTls == target.PinKey ? UpgradeWithin : TlsAnswerWithin;
            probe = new DialProbe(tls: true, within);
            try
            {
                dial = await DialAsync(address, probe, validate, target, tls: true, keyed ? apiKey : "", SelfId, ct);
            }
            catch (Exception ex) when (AnsweredWithoutTls(ex, probe))
            {
                // A pinned certificate is TLS or nothing: a plain answer is not that Stagewatch, and nothing of this desk's goes to it.
                if (pinned)
                {
                    var how = probe.Silent
                        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"showed no certificate within {within.TotalSeconds:0.#} s")
                        : "answered without TLS";
                    throw new DialRefusedException($"{target.Name} at {target.Host}:{target.Port} {how}, and its certificate is pinned ({TlsFingerprint.Short(pin)}) — nothing sent (another machine at its address?); trying again");
                }
                tls = false;
            }
        }
        if (dial is null)
        {
            // Plain: the key goes only to loopback. Anywhere else, the Stagewatch is read without the key.
            keyed = apiKey.Length > 0 && local;
            probe = new DialProbe(tls: false, TlsAnswerWithin);
            dial = await DialAsync(address, probe, null, target, tls: false, keyed ? apiKey : "", SelfId, ct);
        }
        var (handler, invoker, ws) = dial.Value;
        using var ownedHandler = handler;
        using var ownedInvoker = invoker;
        using var ownedSocket = ws;
        var heldBack = apiKey.Length > 0 && !keyed;
        _carrier = tls ? "tls" : heldBack ? "keyless" : "plain";
        if (heldBack)
        {
            _note = target.HelloTls
                ? $"{target.Name} answered without TLS though its hello names a certificate — the key stays on this desk"
                : $"{target.Name} offers no TLS — the key stays on this desk";
        }
        var said = target.PinKey + "|" + _carrier + "|" + _note;
        if (said != _carrierSaid)
        {
            _carrierSaid = said;
            Log.Info($"Stagewatch link: {target.Name} at {target.Host}:{target.Port} over {(tls ? "TLS" : heldBack ? "plain ws, read without its key" : "plain ws")}" + (_note.Length > 0 ? " — " + _note + "." : "."));
        }
        _lastWordAt = ShowClock.Seconds;
        var name = target.Name;
        // No frame yet. The same Stagewatch keeps this run's show — its context, and a stall said before the drop, which
        // holds until its next frame. Another Stagewatch starts a show of its own, swapped in after the frameless sight so
        // no read pairs a cleared mark with the old Stagewatch's frame.
        _sight = new StagewatchSight(name, $"{target.Host}:{target.Port}", ShowBusState.Live, null, ShowClock.Seconds, null, "");
        var show = run.Show;
        if (show is null || show.Key != target.PinKey)
        {
            if (show is { Stuck: true }) Log.Info($"Stagewatch link: connected to {name} — {show.Name}, whose show was stuck, is no longer followed.");
            show = new HeardShow(target.PinKey, name);
            run.Show = show;
        }
        _status = $"Stagewatch link: connected to {name} at {target.Host}:{target.Port}" + (tls ? " over TLS" : "") + ".";
        if (!tls) _tlsWords = "";
        // No key, nothing else authenticates the link: the WebSocket's answer is the first trusted contact — for the
        // certificate the hello names only, as round 88 left first use.
        if (tls && apiKey.Length == 0 && target.HelloTls && presented == target.Advertised) Pin(target, pins, presented);
        UiThread.Post(_changed);

        // The side calls ride the dial's own handler: the same address, the same pin, the key only where the socket's carried it.
        var sideKey = keyed ? apiKey : "";
        // Every answer is read whole before it is parsed, an error's body too: bounded here, well above a context's 64 KiB.
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = SideAnswerMost };
        var baseUri = Address(target, tls, socket: false);
        var sendGate = new SemaphoreSlim(1, 1);
        var connection = new Connection(run, target, tls, sideKey.Length > 0, pins, () => presented, apiKey);
        var beat = Task.Run(() => BeatAsync(connection, show, ws, sendGate, http, baseUri, sideKey, ct), CancellationToken.None);
        var reprobe = heldBack ? Task.Run(() => ReprobeAsync(run, address, target, ws, ct), CancellationToken.None) : Task.CompletedTask;
        try
        {
            await ReceiveAsync(connection, show, ws, name, ct);
        }
        finally
        {
            await linked.CancelAsync();
            await EndedWithTheLink(reprobe);                                           // it throws nothing but the link's own cancel
            await EndedWithTheLink(beat);
            sendGate.Dispose();
        }
    }

    /// <summary>A task of the connection, waited for once the connection is over: its cancel is the link's, not a fault.</summary>
    private static async Task EndedWithTheLink(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // it ends with the link
        }
    }

    /// <summary>
    /// While a Stagewatch is read without the key: every <see cref="TlsReprobeEvery"/>, one TLS greeting to the dial's
    /// address. A certificate shown — whatever it is; the dial that follows checks it against the pin — remembers that
    /// Stagewatch as slow to answer TLS and ends this connection, so the next dial goes over TLS and waits long enough for it.
    /// </summary>
    private async Task ReprobeAsync(Run run, IPAddress address, Target target, ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Task.Delay(TlsReprobeEvery, ct);
            if (!await ShowsCertificateAsync(address, target, ct) || ct.IsCancellationRequested) continue;
            _slowTls = target.PinKey;
            var words = $"{target.Name} at {target.Host}:{target.Port} answers TLS now — dialling again over TLS.";
            Volatile.Write(ref run.Redial, words);
            Log.Info("Stagewatch link: " + words);
            ws.Abort();                                                                 // the receive loop ends; the run dials again
            return;
        }
    }

    /// <summary>
    /// One TLS greeting to <paramref name="address"/>: true when the far end shows a certificate within the upgrade's
    /// budget. The handshake is ended at the certificate, so nothing of this desk's follows the greeting.
    /// </summary>
    private static async Task<bool> ShowsCertificateAsync(IPAddress address, Target target, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(UpgradeWithin);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var shown = false;
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, target.Port), budget.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await using var ssl = new SslStream(stream, leaveInnerStreamOpen: true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = target.Host,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    shown = certificate is not null;
                    return false;                                                       // seen is all the probe asks; the handshake ends here
                },
            }, budget.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or ObjectDisposedException)
        {
            // no certificate came, or the handshake was ended at the one that did: the answer is in shown
        }
        return shown;
    }

    /// <summary>The certificate the link met becomes the pin — once an authenticated answer came over it (TOFU, §10).</summary>
    private void Pin(Target target, IReadOnlyDictionary<string, string> pins, string presented)
    {
        if (!TlsFingerprint.IsValid(presented) || (pins.TryGetValue(target.PinKey, out var held) && held == presented)) return;
        UiThread.Post(() => _config().SetPin(target.PinKey, presented));
        _tlsWords = $"Stagewatch {target.Name}'s certificate pinned ({TlsFingerprint.Short(presented)})";
    }

    /// <summary>The Stagewatch's door as this dial reaches it: wss/https over TLS, ws/http plain.</summary>
    private static Uri Address(Target target, bool tls, bool socket)
    {
        var host = target.Host.Contains(':') ? $"[{target.Host}]" : target.Host;
        var scheme = (socket, tls) switch
        {
            (true, true) => "wss",
            (true, false) => "ws",
            (false, true) => "https",
            _ => "http",
        };
        return new Uri(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{scheme}://{host}:{target.Port}"));
    }

    /// <summary>
    /// The address this dial reaches: the host as typed or heard when it is an address, else its name resolved once (IPv4
    /// first) within <see cref="ResolveWithin"/>. Every socket of the dial goes to this address and no other, so whether
    /// the key may go plain is decided by what is dialled, never by a name.
    /// </summary>
    private static async Task<IPAddress> DialAddressAsync(Target target, CancellationToken ct)
    {
        if (IPAddress.TryParse(target.Host, out var literal)) return literal;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ResolveWithin);
        IPAddress[] found;
        try
        {
            // The wait, not only the lookup, is bounded: a resolver that does not take the cancel is left to finish on its own.
            found = await Dns.GetHostAddressesAsync(target.Host, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DialRefusedException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{target.Name} at {target.Host}:{target.Port}: the name did not resolve within {ResolveWithin.TotalSeconds:0} s — nothing sent; trying again."));
        }
        return Array.Find(found, a => a.AddressFamily == AddressFamily.InterNetwork) ?? (found.Length > 0 ? found[0] : throw new SocketException((int)SocketError.HostNotFound));
    }

    /// <summary>
    /// The WebSocket and the side calls of one dial: every socket goes to <paramref name="address"/>, straight (no proxy —
    /// a proxy is another machine), over TLS checked against the pin when <paramref name="validate"/> is given.
    /// </summary>
    private static SocketsHttpHandler Handler(IPAddress address, DialProbe probe, RemoteCertificateValidationCallback? validate)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            // Longer than any upgrade's budget (the handler's own timeout takes in the TLS handshake): the budget, which
            // tells a far end that showed no certificate from a fault, always ends the wait first.
            ConnectTimeout = UpgradeWithin + TimeSpan.FromSeconds(1),
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    socket.Dispose();
                    throw;
                }
                probe.Connected();
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        if (validate is not null) handler.SslOptions.RemoteCertificateValidationCallback = validate;
        return handler;
    }

    /// <summary>One dial: its handler (every socket to <paramref name="address"/>), the invoker the upgrade runs on, and the WebSocket — all let go when it fails.</summary>
    private static async Task<(SocketsHttpHandler Handler, HttpMessageInvoker Invoker, ClientWebSocket Socket)> DialAsync(IPAddress address, DialProbe probe, RemoteCertificateValidationCallback? validate, Target target, bool tls, string key, string selfId, CancellationToken ct)
    {
        var handler = Handler(address, probe, validate);
        var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        try
        {
            var ws = await UpgradeAsync(invoker, probe, target, tls, key, selfId, ct);
            return (handler, invoker, ws);
        }
        catch
        {
            invoker.Dispose();
            handler.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The upgrade to /ws through <paramref name="invoker"/>, the key in its header when one is given, on the budget
    /// <paramref name="probe"/> keeps. Round 91: as a node link naming this desk's show network id
    /// (<see cref="StagewatchFrame.SocketQuery"/>), so a Stagewatch 2.18 hears who it is and says first what it made of the key.
    /// </summary>
    private static async Task<ClientWebSocket> UpgradeAsync(HttpMessageInvoker invoker, DialProbe probe, Target target, bool tls, string key, string selfId, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            ws.Options.KeepAliveInterval = TimeSpan.Zero;                              // the Show Bus beat is the keep-alive
            if (key.Length > 0) ws.Options.SetRequestHeader("Authorization", "Bearer " + key);
            budget.CancelAfter(UpgradeWithin);
            probe.Arm(budget);
            await ws.ConnectAsync(new Uri(Address(target, tls, socket: true), StagewatchFrame.SocketQuery(selfId)), invoker, budget.Token);
            return ws;
        }
        catch
        {
            if (budget.IsCancellationRequested && !ct.IsCancellationRequested) probe.Expired();
            ws.Dispose();
            throw;
        }
        finally
        {
            probe.Arm(null);
        }
    }

    /// <summary>
    /// The far end answered a TLS dial without TLS: a socket reached it and no certificate came, and either the TLS answer's
    /// deadline passed (a server that reads the greeting as a request's head and waits) or the handshake failed on what came
    /// instead (a server that answers the greeting as a bad request). A cancel of the link's own is never an answer.
    /// </summary>
    private static bool AnsweredWithoutTls(Exception ex, DialProbe probe)
    {
        if (!probe.Reached || probe.Answered) return false;
        if (probe.Silent) return true;
        var tlsFailed = false;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException) return false;
            tlsFailed |= e is System.Security.Authentication.AuthenticationException or IOException;
        }
        return tlsFailed;
    }

    /// <summary>
    /// What one dial saw, and how long its upgrade may take. The upgrade spends a budget of <see cref="UpgradeWithin"/>; on
    /// a TLS dial its socket reaching the far end cuts that budget to the TLS answer's deadline, and a certificate gives it
    /// back whole. A far end that showed no certificate by the time the budget ran out is <see cref="Silent"/>: it does not
    /// speak TLS on that port. The side calls' sockets, opened after the upgrade, change no budget.
    /// </summary>
    private sealed class DialProbe(bool tls, TimeSpan tlsWithin)
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _budget;
        private volatile bool _reached;
        private volatile bool _answered;
        private volatile bool _silent;

        /// <summary>A socket of this dial reached the far end.</summary>
        public bool Reached => _reached;

        /// <summary>A TLS handshake of this dial got as far as a certificate.</summary>
        public bool Answered => _answered;

        /// <summary>The TLS answer's deadline passed with the socket connected and no certificate shown.</summary>
        public bool Silent => _silent;

        /// <summary>The upgrade under way spends <paramref name="budget"/>; null once it has ended (before the budget is disposed).</summary>
        public void Arm(CancellationTokenSource? budget)
        {
            lock (_gate) _budget = budget;
        }

        public void Connected()
        {
            _reached = true;
            if (!tls) return;
            lock (_gate) _budget?.CancelAfter(tlsWithin);
        }

        public void Certificate()
        {
            _answered = true;
            lock (_gate) _budget?.CancelAfter(UpgradeWithin);
        }

        /// <summary>The upgrade's budget ran out (not the link's own stop).</summary>
        public void Expired()
        {
            if (tls && _reached && !_answered) _silent = true;
        }
    }

    /// <summary>A dial that ended before anything of this desk's was sent: its words are the link's.</summary>
    private sealed class DialRefusedException(string words) : IOException(words);

    /// <summary>
    /// Every two seconds: the clock exchange (which is also the beat), the liveness check, the approval queue; every
    /// five seconds the Stagewatch's context read and this desk's own posted. Round 91: on a connection that carries the
    /// key, no keyed call goes before its first message is read (a verdict, on a Stagewatch that sends one), and none while
    /// a refused key or a rate limit holds them — meanwhile the timer is followed on the public frame and the Stagewatch's
    /// context, which this desk cannot read, is not shown as if it were current; while a key that speaks for no node of
    /// this desk's holds the node routes, the context is read and shown and only those routes wait.
    /// </summary>
    private async Task BeatAsync(Connection c, HeardShow show, ClientWebSocket ws, SemaphoreSlim sendGate, HttpClient http, Uri baseUri, string apiKey, CancellationToken ct)
    {
        var lastContext = double.NegativeInfinity;
        var target = c.Target;
        var name = target.Name;
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var silent = ShowClock.Seconds - _lastWordAt;
            if (silent > ShowBusLiveness.DropAfter.TotalSeconds)
            {
                _status = $"Stagewatch link: {name} silent for {silent:0} s — dropping and dialling again.";
                ws.Abort();
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(StagewatchFrame.Ping(ClockPing.Ms(ShowClock.UtcNow)));
            await sendGate.WaitAsync(ct);
            try
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                sendGate.Release();
            }
            if (ShowClock.Seconds - lastContext >= 5)
            {
                if (!c.Keyed || KeyedCallsOpen(c))
                {
                    lastContext = ShowClock.Seconds;
                    if (await FetchContextAsync(c, show, http, new Uri(baseUri, "/api/context"), apiKey, ct) && c.Tls) Pin(target, c.Pins, c.Presented());
                    if (NodeRoutesOpen(c)) await PostOwnContextAsync(c, http, new Uri(baseUri, "/api/peers/context"), apiKey, ct);
                }
                else if (c.Heard && HeldFor(c.Run) > TimeSpan.Zero && show.Context is not null)
                {
                    show.Context = null;                                                   // its one writer: what a held key cannot read is not shown as current
                    UiThread.Post(_changed);
                }
            }
            if (target.Approvals && apiKey.Length > 0 && SelfId.Length > 0 && NodeRoutesOpen(c)) await ApprovalsAsync(c, http, baseUri, apiKey, name, ct);
            await Task.Delay(ShowBusLiveness.Beat, ct);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri url, string apiKey, string? json = null)
    {
        var r = new HttpRequestMessage(method, url);
        if (apiKey.Length > 0) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return r;
    }

    /// <summary>
    /// The Stagewatch's context, kept on its show (its one writer); true when an authenticated answer came (the TLS pin
    /// may be set on it). It touches nothing of the frame, so a context read during a stall cannot bring a time back.
    /// Round 91: a keyed read refused (401), or rate-limited (429), holds the key as its answer says; any key the Stagewatch
    /// takes reads it, so a key that speaks for no node of this desk's goes on reading it.
    /// </summary>
    private async Task<bool> FetchContextAsync(Connection c, HeardShow show, HttpClient http, Uri url, string apiKey, CancellationToken ct)
    {
        try
        {
            var asked = Asked(c.Run);
            using var request = Request(HttpMethod.Get, url, apiKey);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // An older Stagewatch, or a key that may not read it: the timer still follows; a keyed refusal holds the key.
                if (apiKey.Length > 0) Answered(c, response, await response.Content.ReadAsStringAsync(ct), asked, context: false);
                return false;
            }
            // Its words reach every reader and the context this desk posts: a key echoed in them is masked before the parse.
            var json = StagewatchFrame.MaskSecret(await response.Content.ReadAsStringAsync(ct), c.Secret);
            if (ShowContext.Parse(json) is { } context)
            {
                show.Context = context;
                UiThread.Post(_changed);
            }
            return apiKey.Length > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            // a context that does not come this time comes next time; the frames are what the stage reads
            return false;
        }
    }

    /// <summary>
    /// This desk's own context to the Stagewatch (§6, `node` key): the Stagewatch's show network shows it, and it binds the
    /// key to this desk's id for the approval queue. Round 91: its answer is evidence of the key — the <c>verified</c> a
    /// Stagewatch 2.18 names (none: the key speaks for no node of this desk's, its node routes held), a refusal in the
    /// Stagewatch's own words, held — and for a Stagewatch that sends no verdict on its socket, the only evidence (P-legacy).
    /// </summary>
    private async Task PostOwnContextAsync(Connection c, HttpClient http, Uri url, string apiKey, CancellationToken ct)
    {
        if (apiKey.Length == 0 || OwnContext is null) return;
        string? json;
        try
        {
            json = await UiThread.InvokeAsync(() => OwnContext()).GetTask();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
        {
            return;
        }
        if (json is null || !NodeRoutesOpen(c)) return;                               // a verdict or a refusal may have come while the context was made
        try
        {
            var asked = Asked(c.Run);
            using var request = Request(HttpMethod.Post, url, apiKey, json);
            using var response = await http.SendAsync(request, ct);
            var said = Answered(c, response, await response.Content.ReadAsStringAsync(ct), asked, context: true);
            _approvalsLive = response.IsSuccessStatusCode && _approvalsLive;
            if (!response.IsSuccessStatusCode)
                _approvalWords = $"Stagewatch {c.Target.Name} refused this desk's context — {said}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            // said again in five seconds
        }
    }

    private readonly HashSet<string> _ranDeskIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Round 91: results this desk owes an approval desk — a proposal it ran whose result a hold stopped, or the desk
    /// refused while this key was held there — by the Stagewatch they are owed to (its pin key) and its id for the
    /// proposal, oldest first; sent to that Stagewatch only, before anything else once its node routes open again, and kept
    /// until an answer settles each. Bounded at <see cref="MostOwed"/>.
    /// </summary>
    private readonly List<(string To, string Id, string Body)> _owed = new();

    /// <summary>The most results owed at once; past it the oldest is let go, said in the log.</summary>
    public const int MostOwed = 100;

    /// <summary>
    /// §11: send what waits, read what became of ours, collect what was approved for this desk and answer it. Round 91: a
    /// refusal is said in the approval desk's own words — a refused collect too, which used to pass in silence — and any
    /// hold of the key holds the queue: nothing more is sent, taken or run once one stands, and a result it stopped is owed.
    /// </summary>
    private async Task ApprovalsAsync(Connection c, HttpClient http, Uri baseUri, string apiKey, string deskName, CancellationToken ct)
    {
        try
        {
            foreach (var e in Approvals.Unsent)
            {
                if (!NodeRoutesOpen(c)) return;
                var asked = Asked(c.Run);
                using var post = Request(HttpMethod.Post, new Uri(baseUri, "/api/proposals"), apiKey, DeskProposals.Request(SelfId, SelfName, SelfId, e.Line, e.Reason, e.Risk));
                using var res = await http.SendAsync(post, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                {
                    _approvalWords = $"the approval desk refused this desk's proposal — {Answered(c, res, body, asked, context: false)}";
                    _approvalsLive = false;
                    return;
                }
                if (DeskProposals.ParseOne(body) is { } made)
                {
                    e.DeskId = made.Id;
                    Update(e, made.State.Length > 0 ? made.State : "pending", deskName);
                }
            }
            // Live only while no hold stands, decided under the run's gate: a refusal applied there meanwhile wins.
            lock (c.Run.Gate)
            {
                if (!NodeRoutesOpen(c)) return;
                _approvalsLive = true;
                _approvalWords = $"approval desk: Stagewatch {deskName} — the assistant's proposals go there";
            }

            foreach (var (id, body) in Owed(c.Target.PinKey))
                if (!await PostResultAsync(c, http, baseUri, apiKey, deskName, id, body, ct)) return;

            var askedMine = Asked(c.Run);
            using (var mine = Request(HttpMethod.Get, new Uri(baseUri, "/api/proposals?from=" + Uri.EscapeDataString(SelfId)), apiKey))
            using (var res = await http.SendAsync(mine, ct))
            {
                if (res.IsSuccessStatusCode)
                {
                    foreach (var p in DeskProposals.ParseList(await res.Content.ReadAsStringAsync(ct)))
                        if (Approvals.ByDesk(p.Id) is { } e && e.State != p.State && !(e.Ran && p.State == "approved")) Update(e, p.State, deskName);
                }
                else Answered(c, res, await res.Content.ReadAsStringAsync(ct), askedMine, context: false);
            }

            if (!NodeRoutesOpen(c)) return;
            var askedCollect = Asked(c.Run);
            using var approved = Request(HttpMethod.Get, new Uri(baseUri, "/api/proposals?target=" + Uri.EscapeDataString(SelfId) + "&state=approved"), apiKey);
            using var list = await http.SendAsync(approved, ct);
            if (!list.IsSuccessStatusCode)
            {
                // A refused collect is said: nothing approved for this desk reaches it until the Stagewatch hands it over.
                var refusal = Answered(c, list, await list.Content.ReadAsStringAsync(ct), askedCollect, context: false);
                _approvalWords = $"the approval desk refused to hand over what was approved for this desk — {refusal}";
                _approvalsLive = false;
                return;
            }
            foreach (var p in DeskProposals.ParseList(await list.Content.ReadAsStringAsync(ct)))
            {
                if (p.TargetId.Length > 0 && p.TargetId != SelfId) continue;
                if (!NodeRoutesOpen(c)) return;                                             // held: what was approved waits there, not taken
                if (!_ranDeskIds.Add(p.Id)) continue;
                var (ok, words) = await RunAsync(p, deskName);
                if (!await PostResultAsync(c, http, baseUri, apiKey, deskName, p.Id, DeskProposals.Result(ok, words), ct)) return;
            }
            if (_ranDeskIds.Count > 1000) _ranDeskIds.Clear();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            // the queue is asked again in two seconds; nothing was run twice (the ids ran are kept), a result owed stays owed
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException or UriFormatException)
        {
            // A reply this desk cannot read, or an apply that threw: said, and the beat goes on — the link's clock and
            // frames must never stop because the queue misbehaved.
            Log.Warn("Approval desk: a step of the queue failed — asked again in two seconds.", ex);
        }
    }

    /// <summary>
    /// One result to the approval desk, owed until an answer settles it: false when a hold stopped it — standing before it
    /// left, or started by its answer (401, 403, 429) — so the queue goes no further; it is sent again once the routes open.
    /// Any other refusal settles it, said in the log.
    /// </summary>
    private async Task<bool> PostResultAsync(Connection c, HttpClient http, Uri baseUri, string apiKey, string deskName, string id, string body, CancellationToken ct)
    {
        var to = c.Target.PinKey;
        Owe(to, id, body);
        if (!NodeRoutesOpen(c)) return false;
        var asked = Asked(c.Run);
        using var result = Request(HttpMethod.Post, new Uri(baseUri, $"/api/proposals/{Uri.EscapeDataString(id)}/result"), apiKey, body);
        using var answer = await http.SendAsync(result, ct);
        if (answer.IsSuccessStatusCode)
        {
            Settle(to, id);
            return true;
        }
        var said = Answered(c, answer, await answer.Content.ReadAsStringAsync(ct), asked, context: false);
        if ((int)answer.StatusCode is 401 or 403 or 429)
        {
            Log.Warn($"Approval desk ({deskName}): the result of {id} was refused — {said}; it is sent again once this desk's key may post it.");
            return false;
        }
        Settle(to, id);
        Log.Warn($"Approval desk ({deskName}): the result of {id} was refused — {said}");
        return true;
    }

    private List<(string Id, string Body)> Owed(string to)
    {
        lock (_owed) return _owed.Where(o => o.To == to).Select(o => (o.Id, o.Body)).ToList();
    }

    private void Owe(string to, string id, string body)
    {
        lock (_owed)
        {
            if (_owed.Exists(o => o.To == to && o.Id == id)) return;
            _owed.Add((to, id, body));
            if (_owed.Count <= MostOwed) return;
            Log.Warn($"Approval desk: the result of {_owed[0].Id} is let go unsent — more than {MostOwed} results owed.");
            _owed.RemoveAt(0);
        }
    }

    private void Settle(string to, string id)
    {
        lock (_owed) _owed.RemoveAll(o => o.To == to && o.Id == id);
    }

    /// <summary>An approved proposal: one of the assistant's (applied by its id) or another app's wire line (run through the wire path).</summary>
    private async Task<(bool Ok, string Words)> RunAsync(DeskProposal p, string deskName)
    {
        if (DeskProposals.LocalId(p.Line) is { } local)
        {
            if (Approvals.ByLocal(local) is not { } e) return (false, "this desk no longer holds that proposal (restarted since?) — ask the assistant again");
            var outcome = await UiThread.InvokeAsync(() =>
            {
                var r = e.Apply();
                e.Ran = true;
                e.Said(r.Ok ? "applied — " + r.Words : "refused here — " + r.Words);
                Log.Info($"Approval desk ({deskName}): approved {p.Line} — {(r.Ok ? "applied" : "refused")}: {r.Words}");
                return r;
            }).GetTask();
            return outcome;
        }
        if (RunApprovedLine is null) return (false, "this Patterns runs only its own assistant's proposals from an approval desk");
        var run = await RunApprovedLine(p.Line);
        Log.Info($"Approval desk ({deskName}): approved \"{p.Line}\"{(p.DecidedBy.Length > 0 ? " by " + p.DecidedBy : "")} — {(run.Ok ? "done" : "refused")}: {run.Words}");
        return run;
    }

    private static void Update(DeskApprovals.Entry e, string state, string deskName)
    {
        e.State = state;
        var words = DeskProposals.StateWords(state, "Stagewatch " + deskName);
        UiThread.Post(() => e.Said(words));
    }

    /// <summary>
    /// Keyed reads may go — the context's: the connection carries the key, its first message is read, and neither a refused
    /// key's hold nor a rate limit's stands.
    /// </summary>
    private static bool KeyedCallsOpen(Connection c) => c.Keyed && c.Heard && HeldFor(c.Run) == TimeSpan.Zero;

    /// <summary>
    /// The node routes may go — the context this desk posts, its proposals, the collect and the results: as
    /// <see cref="KeyedCallsOpen"/>, and no hold of a key that speaks for no node of this desk's stands.
    /// </summary>
    private static bool NodeRoutesOpen(Connection c) => c.Keyed && c.Heard && NodeHeldFor(c.Run) == TimeSpan.Zero;

    /// <summary>The most of a side call's answer read: well above a context's 64 KiB, so no far end fills this desk's memory.</summary>
    private const long SideAnswerMost = 1024 * 1024;

    /// <summary>Where the run's words stand as a call leaves: its answer applies only while no verdict or refusal came after.</summary>
    private static long Asked(Run run)
    {
        lock (run.Gate) return run.Said;
    }

    /// <summary>
    /// The answer to one of this desk's keyed calls, read as evidence of the key (<see cref="StagewatchKey.OfAnswer"/>): a
    /// 401 refuses it, a 403 holds its node routes and a 429 holds it, in the Stagewatch's words; a 2xx to this desk's
    /// context (<paramref name="context"/>) says its standing. Returns the Stagewatch's words for the answer, the key masked.
    /// </summary>
    private string Answered(Connection c, HttpResponseMessage response, string body, long asked, bool context)
    {
        var status = (int)response.StatusCode;
        var error = "";
        var verified = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body.Length is > 0 and <= 64 * 1024 ? body : "{}");
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String) error = StagewatchFrame.Words(e.GetString(), c.Secret);
                if (doc.RootElement.TryGetProperty("verified", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String) verified = v.GetString() ?? "";
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // a body that is no JSON object says nothing more than its status
        }
        var retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (long)Math.Ceiling(delta.TotalSeconds) : 0;
        if (c.Keyed && StagewatchKey.OfAnswer(status, error, verified, retryAfter, context, c.Legacy, RefusedHoldAtLeast) is { } step) Apply(c, step, asked);
        return error.Length > 0 ? error : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"HTTP {status}");
    }

    /// <summary>
    /// One word on the key applied to the run, under its gate: the hold first — recorded as the word is read, before any
    /// further I/O — then the standing, and the approval desk held with it. A verdict (<paramref name="asked"/> −1) is the
    /// latest word; an answer applies only while no verdict or refusal came after its call left. A word that takes the key
    /// lifts every hold of a refusal (never a rate limit's); one that says the key reads but speaks for no node of this
    /// desk's lifts a refused key's hold and holds the node routes. One log line when the standing moves; the certificate
    /// pinned on an answer that authenticated the key.
    /// </summary>
    private void Apply(Connection c, StagewatchKeyStep step, long asked)
    {
        var run = c.Run;
        var name = c.Target.Name;
        StagewatchKey before;
        StagewatchKey after;
        lock (run.Gate)
        {
            if (asked >= 0 && asked != run.Said) return;
            if (asked < 0 || step.Hold is StagewatchHoldKind.Refused or StagewatchHoldKind.Node) run.Said++;
            var now = ClockMs();
            if (step.Takes) (run.HeldUntilMs, run.NodeHeldUntilMs) = (0, 0);
            if (step.Hold == StagewatchHoldKind.Node) run.HeldUntilMs = 0;
            var until = now + (long)step.For.TotalMilliseconds;
            if (step.Hold == StagewatchHoldKind.Refused && until > run.HeldUntilMs) run.HeldUntilMs = until;
            if (step.Hold == StagewatchHoldKind.Busy && until > run.BusyUntilMs) run.BusyUntilMs = until;
            if (step.Hold == StagewatchHoldKind.Node && until > run.NodeHeldUntilMs) run.NodeHeldUntilMs = until;
            before = run.Key;
            after = step.Key is { } k ? k with { Legacy = c.Legacy && c.Keyed } : before;
            run.Key = after;
            if (step.Hold != StagewatchHoldKind.None)
            {
                // The approval desk is held with the key, and says so: no "the proposals go there" beside a refusal.
                _approvalsLive = false;
                if (c.Target.Approvals)
                    _approvalWords = step.Hold == StagewatchHoldKind.Busy
                        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"approval desk: Stagewatch {name} — the assistant's proposals wait out its rate limit ({step.For.TotalSeconds:0} s)")
                        : $"approval desk: Stagewatch {name} — the assistant's proposals wait while this desk's key is held there";
            }
        }
        if (step.Hold == StagewatchHoldKind.Busy)
            Log.Warn(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Stagewatch link: {name} rate-limits this desk's key — its keyed calls wait {step.For.TotalSeconds:0} s ({step.Words})."));
        if (after != before)
        {
            if (after.Fault)
                Log.Warn(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Stagewatch link: {name} — {after.Line}; {(step.Hold == StagewatchHoldKind.Node ? "the context this desk posts and its proposals wait" : "this desk's keyed calls wait")} {step.For.TotalSeconds:0} s."));
            else if (after.Word is "key" or "operator")
                Log.Info($"Stagewatch link: {name} takes this desk's key — verified {after.Word}" + (after.Legacy ? $" ({StagewatchKey.LegacyWords})." : "."));
            else if (after.Keyed)
                Log.Info($"Stagewatch link: {name} takes this desk's key again" + (after.Legacy ? $" — {StagewatchKey.LegacyWords}." : "."));
            else if (after.Said.Length > 0)
                Log.Info($"Stagewatch link: {name} — no key on this link: {after.Said}.");
            UiThread.Post(_changed);
        }
        if (step.Pins && c.Tls) Pin(c.Target, c.Pins, c.Presented());
    }

    /// <summary>
    /// Round 91: a verdict on the socket (Stagewatch 2.18 §7.1), read as it arrives: the first message a node link hears,
    /// and again whenever the key, its binding or the PIN changes there, latest wins. The viewing code's refusal, after
    /// which the Stagewatch closes the socket, holds the redial and says why.
    /// </summary>
    private void Verdict(Connection c, string text)
    {
        if (StagewatchFrame.Auth(text, c.Secret) is not { } auth) return;
        if (StagewatchKey.OfVerdict(auth, SelfId, c.Keyed, RefusedHoldAtLeast) is not { } step) return;
        Apply(c, step, -1);
        if (!auth.View) return;
        var why = step.Words.Length > 0 ? step.Words : auth.Error.Length > 0 ? auth.Error : "its viewing code";
        Volatile.Write(ref c.Run.Redial, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{c.Target.Name} closed the link — {why}; dialling again in {Math.Ceiling(step.For.TotalSeconds):0} s."));
    }

    /// <summary>
    /// Round 91: the first message on a connection. Anything but a verdict says the Stagewatch is older than its node links,
    /// and so does, on a link that carries the key, a viewing code's refusal that says nothing of the key
    /// (<see cref="StagewatchKey.OlderFirst"/>): a link that carries the key then reads the key's standing from the answers
    /// to its keyed calls (P-legacy's labelled fallback) — from the standing that evidence left on a connection before, or
    /// from nothing said yet.
    /// </summary>
    private void First(Connection c, string type, string text)
    {
        c.Legacy = StagewatchKey.OlderFirst(type, type == "auth" ? StagewatchFrame.Auth(text, c.Secret) : null, c.Keyed);
        if (!c.Legacy) return;
        var run = c.Run;
        StagewatchKey before;
        StagewatchKey after;
        lock (run.Gate)
        {
            before = run.Key;
            after = c.Keyed ? before.Legacy && before.Keyed ? before : new StagewatchKey { Keyed = true, Legacy = true } : StagewatchKey.Unknown;
            run.Key = after;
        }
        if (after == before) return;
        if (after.Legacy) Log.Info($"Stagewatch link: {c.Target.Name} — {StagewatchKey.LegacyWords}.");
        UiThread.Post(_changed);
    }

    /// <summary>
    /// The link's one reader: frames, pongs and beats. It alone writes its show's stuck mark — set by the stuck beat,
    /// cleared by the next state frame only once that frame is held; <see cref="Sight"/> reads the mark before the frame,
    /// so a read that finds the mark cleared finds the new frame, never the old one counting again. Round 91: and the
    /// Stagewatch's verdict on this desk's key, read before anything keyed is sent.
    /// </summary>
    private async Task ReceiveAsync(Connection c, HeardShow show, ClientWebSocket ws, string name, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new System.Buffers.ArrayBufferWriter<byte>(16 * 1024);
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return;
            System.Buffers.BuffersExtensions.Write(message, buffer.AsSpan(0, r.Count));
            if (message.WrittenCount > 1024 * 1024) throw new InvalidOperationException("a message past 1 MB is not a frame");
            if (!r.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.WrittenSpan);
            message.ResetWrittenCount();
            var arrived = ShowClock.Seconds;
            _lastWordAt = arrived;
            var type = StagewatchFrame.TypeOf(text);
            if (!c.Heard) First(c, type, text);
            switch (type)
            {
                case "auth":
                    Verdict(c, text);                                                       // its hold recorded before the beat may send anything keyed
                    break;
                case "pong" when StagewatchFrame.Pong(text) is { } p:
                    var t4 = ClockPing.Ms(ShowClock.UtcNow);
                    _clock.Sample(p.T1 * TimeSpan.TicksPerMillisecond, p.T2 * TimeSpan.TicksPerMillisecond, p.T3 * TimeSpan.TicksPerMillisecond, t4 * TimeSpan.TicksPerMillisecond);
                    break;
                case "beat":
                    // A plain beat says the link is alive and nothing of the show; the stuck beat says the show is stuck.
                    if (StagewatchFrame.IsStuckBeat(text))
                    {
                        var words = $"Stagewatch link: {name} says its show is stuck — the stage shows no time.";
                        _status = words;                                                        // said again after a redial
                        if (!show.Stuck)
                        {
                            show.Stuck = true;
                            Log.Warn(words);
                            UiThread.Post(_changed);
                        }
                    }
                    break;
                case "state" or "":
                    if (StagewatchFrame.Parse(text) is { } frame)
                    {
                        Interlocked.Increment(ref _frames);
                        _sight = _sight with { Frame = frame, FrameAtSeconds = arrived, State = ShowBusState.Live };
                        _status = $"Stagewatch link: following {name}" + (_clock.Known && LinkClock.Apart(_clock.Offset) ? " · " + LinkClock.ApartWords("Stagewatch", _clock.Offset) : "") + ".";
                        if (show.Stuck)
                        {
                            show.Stuck = false;                                                 // after the new frame is held, never before
                            Log.Info($"Stagewatch link: {name}'s show runs again.");
                        }
                        UiThread.Post(_changed);
                    }
                    break;
            }
            c.Heard = true;                                                                 // after the first word is applied: no keyed call before it
        }
    }

    private static string StatusWords(ShowBusState state, StagewatchSight s, TimeSpan silent) => state switch
    {
        ShowBusState.Live when s.Stuck => s.Frame is null ? $"{s.Name} says its show is stuck — waiting for its first frame" : $"{s.Name} {ShowBusView.StuckWords}",
        ShowBusState.Live => s.Frame is null ? $"connected to {s.Name}, waiting for its first frame" : $"following {s.Name}",
        ShowBusState.Stale => $"{s.Name} quiet for {silent.TotalSeconds:0} s",
        _ => $"{s.Name} not answering",
    };

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
