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
/// Round 92: a name is dialled at each of its addresses in turn — IPv4 first, then IPv6, at most four — and the next is
/// tried only while no socket has reached the far end at this one; an answer there, a refusal or CERTIFICATE CHANGED among
/// them, ends the dial at that address. Every socket of a connection goes to the address that answered, which is still
/// what is asked whether the key may go plain; the words name the addresses that did not answer, and the log the one that did.
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
/// <para>
/// Show Bus 1.3 §13 at level 0 — the link on the PeerLink core. One <see cref="PeerLinkMachine"/> for the Stagewatch a run dials
/// is told every dial, certificate, first message, frame, beat, ping and pong, and decides: the redial's wait, whose count returns
/// to 0 only after a link settled and up <c>peerlink.stable.ms</c> (13.15.2) — at level 0, where no proof verifies, settled by a
/// verdict that takes the key or, on a connection that carries none, by its first frame — so a Stagewatch that accepts and
/// closes is backed off rather than redialled at once, and the count and a key's hold are the run's under whatever name it
/// dials its Stagewatch by (<see cref="CoreFor"/>); the link's liveness, on an evaluator of its own
/// every <c>link.check.max.ms</c> that never waits behind a side call (13.10.3, 13.10.4); and the clock, which takes a pong as a
/// sample only when it answers one of this link's last pings (13.10.2). The key goes inside TLS only to a certificate a person on
/// this desk pinned — Trust, one press beside its short fingerprint — or to a loopback address, where it never leaves this
/// machine; a certificate the desk pinned at first use is <c>first</c>, kept for comparison, and carries no key (13.6.8, 13.8.2).
/// A refused key's hold is recorded with its end in wall time by target and kid (<see cref="LinkHolds"/>) before any further I/O
/// and outlasts a restart; inside it the link reads the public view without the key and proves the key again on a fresh socket
/// when it ends (13.15.3). The side calls run on a lane of their own, one at a time (13.7.2); their answers are read by 13.12.3's
/// table, and a result owed an approval desk is written before its proposal runs and posted again on 13.12.9's schedule. All of
/// it on <see cref="Time"/>, the link's clock seam.
/// </para>
/// </summary>
public sealed class StagewatchLinkService : IDisposable
{
    private readonly Func<StagewatchLinkConfig> _config;
    private readonly Func<DateTime, IReadOnlyList<ShowBusPeer>> _peers;
    private readonly Action _changed;

    /// <summary>
    /// The core's one writer at a time: every call into a link's machine is made under it, its now read inside it, so the
    /// machine's time only moves forward whichever of the link's tasks calls.
    /// </summary>
    private readonly object _core = new();
    private CancellationTokenSource? _cts;
    private string _runningKey = "";
    private string _runningHostKey = "";

    /// <summary>
    /// One Stagewatch's show as a run has heard it, by the key it was dialled under: its context and its stuck mark. Each
    /// field has one writer — the context only the side calls' lane's read of /api/context, the stuck mark only the receive
    /// loop — so a context landing beside a stuck beat can never put a live frame back.
    /// </summary>
    private sealed class HeardShow(string key, string name)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public volatile ShowContext? Context;
        public volatile bool Stuck;
    }

    /// <summary>
    /// The Stagewatch a run dials as the core keeps it: its target (the pin key) and the machine told everything its
    /// connections see. Replaced when the run dials under another name — the Stagewatch's own, or another one's — and then the
    /// replacement goes on with what the run owes (<see cref="PeerLinkMachine.Continue"/>): the redial's count and wait, and a key's
    /// hold. The clock and the first pin are the name's.
    /// </summary>
    private sealed class LinkCore(string target, PeerLinkMachine machine)
    {
        public string Target { get; } = target;
        public PeerLinkMachine Machine { get; } = machine;

        /// <summary>The certificate (sha256:…) pinned <c>first</c> for this target, as the settings keep it; "" before one.</summary>
        public string FirstSaved = "";

        /// <summary>The link's monotonic ms before which a changed certificate is not looked at again (13.8.3).</summary>
        public long ProbeAt;

        /// <summary>Certificates looked at after CERTIFICATE CHANGED, each one TLS greeting that sends nothing.</summary>
        public int Probes;

        /// <summary>The counter (§4 <c>seq</c>) of the last frame held under this name; 0 before one (the receive loop's alone).</summary>
        public long LastSeq;
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

        /// <summary>The core for the Stagewatch this run dials now (read and replaced under the service's core gate); null before its first.</summary>
        public LinkCore? Core;

        /// <summary>Every name this run has dialled its Stagewatch by (under the core gate): a refusal's hold kept for one is lifted for each when a word takes the key.</summary>
        public readonly HashSet<string> Targets = new(StringComparer.Ordinal);

        /// <summary>The connection up now, for the evaluator; null between connections.</summary>
        public volatile Connection? Live;

        /// <summary>The kid of the key this run holds (13.5.1) — the name its holds are kept by, never the key or a hash of it; null with no key.</summary>
        public string? Kid;

        /// <summary>The link is configured to be followed: the show needs it (13.1), and only then may its light be red.</summary>
        public bool ShowNeeds;

        /// <summary>
        /// What the Stagewatch made of this desk's key, and the holds its words started — kept across this run's connections,
        /// so a refusal said before a drop still holds the keyed calls, and carried into the next run while it follows the same
        /// address with the same key (<see cref="Carry"/>: a pin set by a person starts one); a new key or address starts with
        /// none. The verdict on the socket (the receive loop) and the answers to keyed calls (the side calls' lane) both write
        /// here, under <see cref="Gate"/>; <see cref="Said"/> orders them, so an answer to a call that left before a verdict
        /// never overrides it.
        /// </summary>
        public readonly object Gate = new();

        /// <summary>Counts verdicts and refusals (under <see cref="Gate"/>): an answer applies only while none came after its call left.</summary>
        public long Said;

        /// <summary>The link's monotonic ms until which keyed calls wait for a refused key (under <see cref="Gate"/>).</summary>
        public long HeldUntilMs;

        /// <summary>The link's monotonic ms until which keyed calls wait out a rate limit (under <see cref="Gate"/>).</summary>
        public long BusyUntilMs;

        /// <summary>The link's monotonic ms until which the node routes wait for a key that speaks for no node of this desk's (under <see cref="Gate"/>).</summary>
        public long NodeHeldUntilMs;

        public volatile StagewatchKey Key = StagewatchKey.Unknown;

        /// <summary>
        /// What the run before heard of the same key at the same address — its standing and its holds, whose ends are on the
        /// link's clock both runs keep — so a new run presents a key held there no sooner than that run would have.
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
    /// One connection of a run — what it dialled and whether it carries the key, and whether its first message is read yet:
    /// nothing keyed is sent before it, so a verdict (the first thing a node link hears) is read before any keyed call.
    /// <see cref="Legacy"/>: that first message was no verdict — a Stagewatch older than its node links. <see cref="Public"/>:
    /// dialled inside a refused key's hold, so it reads the public view and nothing it hears moves the key's standing.
    /// <see cref="Withheld"/>: over TLS to a certificate no person pinned, so the key stayed on this desk (13.6.8).
    /// <see cref="Secret"/> is this desk's key, masked in any words the far end sends back.
    /// </summary>
    private sealed class Connection(Run run, LinkCore core, Target target, bool tls, bool keyed, bool isPublic, bool withheld, string secret,
        ClientWebSocket socket, SemaphoreSlim sendGate)
    {
        public Run Run { get; } = run;
        public LinkCore Core { get; } = core;
        public Target Target { get; } = target;
        public bool Tls { get; } = tls;
        public bool Keyed { get; } = keyed;
        public bool Public { get; } = isPublic;
        public bool Withheld { get; } = withheld;
        public string Secret { get; } = secret;
        public ClientWebSocket Socket { get; } = socket;
        public SemaphoreSlim SendGate { get; } = sendGate;
        public volatile bool Heard;
        public volatile bool Legacy;
        public volatile bool OversizeSaid;
        public volatile bool FaultSaid;

        /// <summary>A frame with a counter was held on this connection (the receive loop's alone): only the first is compared with the last connection's.</summary>
        public bool SeqSeen;

        /// <summary>Why the core ended this connection ("" when it did not): a side call's 401 asking for a relink.</summary>
        public volatile string Ending = "";
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
    private volatile string _reached = "";
    private volatile string _carrier = "";

    /// <summary>A certificate waiting for a person's Trust: its fingerprint (sha256:…), whose it is (the pin key) and the Stagewatch's name.</summary>
    private sealed record TrustOffer(string Fingerprint, string PinKey, string Name);

    /// <summary>
    /// The certificate waiting for Trust now, or null — one reference, replaced whole. Written only under <see cref="_trustGate"/>,
    /// read anywhere.
    /// </summary>
    private TrustOffer? _trust;

    /// <summary>
    /// The gate of <see cref="_trust"/> and of the run it belongs to: an offer is made only while its connection's run is the
    /// link's run, a press compares and takes the offer in one step, and a new run lets every offer go as it takes the run's place,
    /// so a frame of a run let go never offers its certificate again after a press.
    /// </summary>
    private readonly object _trustGate = new();
    private string _carrierSaid = "";
    private long _frames;
    private long _sideFaults;
    private long _lastWordMs = long.MinValue;   // the link's monotonic ms at the last word from the Stagewatch; MinValue before any

    /// <param name="config">The link's settings, read on the UI thread at each reconcile.</param>
    /// <param name="peers">The show network's peers heard now (thread-safe).</param>
    /// <param name="changed">Called (posted to the UI thread) when a frame or a context lands: the stage pages read again.</param>
    public StagewatchLinkService(Func<StagewatchLinkConfig> config, Func<DateTime, IReadOnlyList<ShowBusPeer>> peers, Action changed)
    {
        _config = config;
        _peers = peers;
        _changed = changed;
    }

    /// <summary>This desk on the show network — its id and name, the context it posts, the queue it sends to.</summary>
    public string SelfId { get; set; } = "";

    public string SelfName { get; set; } = "";

    /// <summary>This process's own context (§6), read on the UI thread; null when it has none to give.</summary>
    public Func<string?>? OwnContext { get; set; }

    /// <summary>What this desk has sent to the approval desk (§11).</summary>
    public DeskApprovals Approvals { get; } = new();

    /// <summary>
    /// An approved line aimed at this desk that is not one of its assistant's proposals (another app's assistant proposed a
    /// wire verb to Patterns): run through this desk's own wire path on the UI thread, fences and all. Null: such lines are
    /// refused.
    /// </summary>
    public Func<string, Task<(bool Ok, string Words)>>? RunApprovedLine { get; set; }

    /// <summary>
    /// The link's clock: its holds, its beat, its evaluator, its side calls' schedule, its redial and the stamps of its clock
    /// exchange read it — the master clock (<see cref="ShowClockTime"/>) unless a test hands another. A frame's arrival stays on
    /// the master clock the stage pages read. Read on the link's tasks: set it before the link is turned on.
    /// </summary>
    public TimeProvider Time { get; set; } = ShowClockTime.Instance;

    /// <summary>13.15.1's j for the redial and 13.12.9's for a report's retry, from the CSPRNG unless a test injects one. Set before the link is turned on.</summary>
    public Func<double> Jitter { get; set; } = PeerLink.Jitter;

    /// <summary>Where a refused key's hold is kept across a restart — beside the settings on a desk; in memory unless the kernel hands a store.</summary>
    public LinkHolds Holds { get; set; } = new();

    private long MonoMs() => (long)Time.GetElapsedTime(0).TotalMilliseconds;

    private long WallMs() => Time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>The link's now for its core, read under the core gate so the machine's time only moves forward.</summary>
    private LinkNow Now() => new(MonoMs(), WallMs());

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
            s = s with { Context = context, Key = KeyNow() };
            if (stuck) s = s with { Frame = s.Frame is { } f ? f with { Live = false } : null, Stuck = true };
            // The state is the silence since the last word, read now: a link that stopped talking goes stale on its own.
            var silent = TimeSpan.FromSeconds(Math.Max(0, ShowClock.Seconds - s.FrameAtSeconds));
            var lastWord = Volatile.Read(ref _lastWordMs);
            var state = lastWord == long.MinValue ? ShowBusState.Gone : ShowBusLiveness.StateOf(TimeSpan.FromMilliseconds(Math.Max(0, MonoMs() - lastWord)));
            return s with { State = state, Words = StatusWords(state, s, silent) };
        }
    }

    /// <summary>The key's standing as every reader shows it: the Stagewatch's word, and the Trust a held-back key waits for.</summary>
    private StagewatchKey KeyNow()
    {
        var trust = Volatile.Read(ref _trust);
        var key = _run.Key;
        return trust is not null ? key with { TrustFor = TlsFingerprint.Short(trust.Fingerprint) } : key;
    }

    /// <summary>
    /// The Nodes page's line: off, looking, following, quiet — and the clock offset when it matters; with the carrier's note
    /// when there is one (the key held back, or a hello that contradicts the pin).
    /// </summary>
    public string Status
    {
        get
        {
            var line = _status;
            // And what the Stagewatch made of this desk's key, and the Trust it waits for, when there is something to say of it;
            // and where a name led, while an address before the one that answered did not answer.
            var extra = string.Join(" · ", new[] { _note, _reached, KeyNow().Line }.Where(w => w.Length > 0));
            return extra.Length == 0 ? line : line.TrimEnd('.') + " · " + extra + ".";
        }
    }

    /// <summary>
    /// What the Stagewatch made of this desk's key on this run — its verdict on the socket, or (P-legacy) the answers to the
    /// keyed calls of a Stagewatch that sends none — and the Trust a key held back waits for.
    /// <see cref="StagewatchKey.Unknown"/> before anything said.
    /// </summary>
    public StagewatchKey Key => KeyNow();

    /// <summary>
    /// How much longer anything keyed waits — a refused key's hold or a rate limit's (every keyed call), or the node routes'
    /// for a key that speaks for no node of this desk's; zero when nothing holds.
    /// </summary>
    public TimeSpan KeyHeld => NodeHeldFor(_run);

    /// <summary>
    /// The least a refused key is held — Show Bus 1.3's <c>peerlink.hold.refused.ms</c>, 60 s; a Stagewatch's own
    /// <c>retry</c> or <c>Retry-After</c> holds longer (<see cref="StagewatchFrame.Hold(int, long, TimeSpan)"/>). A test
    /// shortens it to see a hold end. Read on the link's task: set it before the link is turned on.
    /// </summary>
    public TimeSpan RefusedHoldAtLeast { get; set; } = StagewatchFrame.RefusedHold;

    /// <summary>
    /// What the connection up now carries — "tls"; "plain" (to loopback, or with no key set); "keyless" (a Stagewatch at an
    /// address that is not loopback that offers no TLS, read without the key); "" with no connection up.
    /// </summary>
    public string Carrier => _carrier;

    /// <summary>
    /// Whether an address a dial reaches is a loopback address — the only place the key goes over plain ws or http, and over
    /// TLS to a certificate no person pinned. Asked of the address the dial's sockets go to, never of a name. A Stagewatch on
    /// this same machine reached at one of its network addresses is not loopback, so it is dialled as another machine's. A test
    /// plays another machine from loopback by answering false. Read on the link's task: set it before the link is turned on.
    /// </summary>
    public Func<IPAddress, bool> IsLocal { get; set; } = IPAddress.IsLoopback;

    /// <summary>
    /// How a dial resolves a name — every address the name has, in the resolver's order; the system's resolver unless a test
    /// answers with addresses of its own. Asked only of a name, never of an address. Read on the link's task: set it before
    /// the link is turned on.
    /// </summary>
    public Func<string, CancellationToken, Task<IPAddress[]>> ResolveName { get; set; } = Dns.GetHostAddressesAsync;

    /// <summary>
    /// How long a TLS dial waits, from its socket's connect, for the far end's certificate. A far end that shows none by then
    /// does not speak TLS on that port (a Stagewatch with no certificate reads the TLS greeting as a request's head and waits
    /// for more), so the link's own deadline ends the attempt.
    /// </summary>
    public TimeSpan TlsAnswerWithin { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// While a Stagewatch is read without the key, how often the link asks it again whether it speaks TLS — one socket and one
    /// TLS greeting, nothing of this desk's. A certificate shown ends the keyless connection and the link dials again over TLS,
    /// so an answer that was late once does not keep the key off the link for the rest of the show. Read on the link's task:
    /// set it before the link is turned on.
    /// </summary>
    public TimeSpan TlsReprobeEvery { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The pin key of a Stagewatch whose certificate came only after <see cref="TlsAnswerWithin"/> once: its TLS dials wait
    /// the whole upgrade budget for the certificate from then on. Kept across runs: how long to wait is no fact of a show.
    /// </summary>
    private volatile string _slowTls = "";

    /// <summary>
    /// The budget of one WebSocket upgrade, its connection taken in (<see cref="StagewatchFrame.DialWithin"/>); a TLS dial's is
    /// cut to <see cref="TlsAnswerWithin"/> until a certificate comes.
    /// </summary>
    private static readonly TimeSpan UpgradeWithin = StagewatchFrame.DialWithin;

    /// <summary>How long a dial waits for its host's name to resolve.</summary>
    private static readonly TimeSpan ResolveWithin = TimeSpan.FromSeconds(4);

    /// <summary>The longest message the link reads whole (<see cref="StagewatchFrame.ReadWholeMost"/>).</summary>
    public const int ReadWholeMost = StagewatchFrame.ReadWholeMost;

    /// <summary>The link's TLS in words ("" when plain): pinned, new, compared, or CERTIFICATE CHANGED.</summary>
    public string TlsWords => _tlsWords;

    /// <summary>The approval desk in words ("" when the Stagewatch is not one, or the link is down).</summary>
    public string ApprovalWords => _approvalWords;

    /// <summary>The linked Stagewatch is an approval desk and this desk's key may use it: the assistant sends there.</summary>
    public bool ApprovalsLive => _approvalsLive && _sight.State == ShowBusState.Live;

    /// <summary>
    /// The show network id of the Stagewatch the link dials now, or is held off for while two running nodes say it — "" for
    /// a typed address no hello names, or with none to dial. The desk's hello table never puts it out to make room for
    /// another app (Show Bus 1.3 §2), so a flood cannot wipe the mark a hold stands on.
    /// </summary>
    public string NodeId => _nodeId;

    private volatile string _nodeId = "";

    /// <summary>
    /// Show Bus 1.3 §2, 13.15.3: while the link is held off an id two running nodes say, the words naming both machines;
    /// "" otherwise. STATE's <c>bus.stagewatch</c> carries it — its light amber and its headline these words while nothing
    /// is linked — so the deck sees the hold.
    /// </summary>
    public string Held => _held;

    private volatile string _held = "";

    /// <summary>A certificate the link refused as changed, waiting for the operator (the Nodes page's ACCEPT).</summary>
    public string PendingFingerprint => _pendingFingerprint;

    /// <summary>Whose certificate changed — "node:&lt;id&gt;" or "addr:&lt;host&gt;:&lt;port&gt;".</summary>
    public string PendingPinKey => _pendingPinKey;

    /// <summary>
    /// Show Bus 1.3 §13.6.8: the certificate (sha256:…) a person may trust now — pinned <c>first</c> for a Stagewatch off this
    /// machine the key is held back from — or "" when none waits. The Nodes card shows its short fingerprint beside Trust.
    /// </summary>
    public string TrustFingerprint => Volatile.Read(ref _trust)?.Fingerprint ?? "";

    /// <summary>Whose certificate waits for Trust — "node:&lt;id&gt;" or "addr:&lt;host&gt;:&lt;port&gt;"; "" when none waits.</summary>
    public string TrustPinKey => Volatile.Read(ref _trust)?.PinKey ?? "";

    /// <summary>The name of the Stagewatch whose certificate waits for Trust; "" when none waits.</summary>
    public string TrustName => Volatile.Read(ref _trust)?.Name ?? "";

    /// <summary>State frames taken in since the link was turned on.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>Side calls that ended in a fault no answer names (refused, timed out): counted on their own, never moving the link (13.7.2).</summary>
    public long SideCallFaults => Interlocked.Read(ref _sideFaults);

    /// <summary>The Stagewatch's clock against this desk's, once a matched pong's sample is believed (13.11).</summary>
    public TimeSpan? ClockOffset
    {
        get
        {
            lock (_core) return _run.Core?.Machine.Clock is { Known: true } clock ? clock.Offset : null;
        }
    }

    /// <summary>Show Bus 1.3 §13.16.3's record of the link to the Stagewatch dialled now, from its core; null before the first target.</summary>
    public LinkRecord? Record
    {
        get
        {
            lock (_core) return _run.Core?.Machine.Record(Now());
        }
    }

    /// <summary>The certificates looked at after CERTIFICATE CHANGED on this run (13.8.3): one TLS greeting each, nothing sent.</summary>
    public int CertificateProbes
    {
        get
        {
            lock (_core) return _run.Core?.Probes ?? 0;
        }
    }

    /// <summary>
    /// What the core asked to write that the link has not taken: 0 — the link writes the bearer the core asks for in its upgrade,
    /// so each ask is taken as the message that made it is told, and nothing waits on the core from one connection to the next.
    /// </summary>
    public int CoreWritesWaiting
    {
        get
        {
            lock (_core) return _run.Core?.Machine.WritesWaiting ?? 0;
        }
    }

    /// <summary>
    /// ACCEPT on the Nodes page (UI thread) — the changed certificate becomes the pin, a person's, and the link dials again.
    /// Returns the words for the strip.
    /// </summary>
    public string AcceptPendingCertificate()
    {
        var fp = _pendingFingerprint;
        var key = _pendingPinKey;
        if (fp.Length == 0 || key.Length == 0) return "No changed certificate is waiting.";
        _config().SetPin(key, fp, PinTrust.Person);
        _pendingFingerprint = "";
        _pendingPinKey = "";
        _runningKey = "";                                                                   // dial again with the new pin
        Reconcile();
        Log.Info($"Stagewatch link: the changed certificate {TlsFingerprint.Short(fp)} of {key} accepted by a person on this desk.");
        return $"Accepted the Stagewatch's new certificate {TlsFingerprint.Short(fp)} — the link dials again.";
    }

    /// <summary>
    /// Trust on the Nodes card (UI thread) — Show Bus 1.3 §13.6.8's one press beside the short fingerprint: the certificate pinned
    /// at first use becomes a person's pin, and the key goes to it from the next dial, which is made at once. Only a person on this
    /// desk makes it: no wire verb, deck or assistant reaches it. <paramref name="fingerprint"/> is the certificate the press was
    /// shown beside; it is trusted only while it is the one waiting — compared and taken in one step, so a certificate that changed
    /// since the words were drawn is never pinned in its place. The run that offered it is let go before this returns, so no frame of
    /// it offers the certificate again. Returns whether it pinned, and the words for the strip.
    /// </summary>
    public (bool Pinned, string Words) TrustCertificate(string fingerprint)
    {
        TrustOffer offer;
        lock (_trustGate)
        {
            if (_trust is not { } waiting) return (false, "No certificate is waiting for Trust.");
            if (!string.Equals(fingerprint.Trim(), waiting.Fingerprint, StringComparison.Ordinal))
                return (false, $"The certificate waiting now is {TlsFingerprint.Short(waiting.Fingerprint)}, not the one pressed for — compare it again on its screen.");
            offer = waiting;
            _trust = null;
        }
        _config().SetPin(offer.PinKey, offer.Fingerprint, PinTrust.Person);
        _runningKey = "";                                                                   // dial again with the person's pin
        Reconcile();
        Log.Info($"Stagewatch link: {offer.Name}'s certificate {TlsFingerprint.Short(offer.Fingerprint)} trusted by a person on this desk — the key goes to it from now.");
        return (true, $"Trusted Stagewatch {offer.Name}'s certificate {TlsFingerprint.Short(offer.Fingerprint)} — the key goes to it from now; the link dials again.");
    }

    /// <summary>Start, restart or stop the link to match its settings (UI thread; cheap when nothing changed).</summary>
    public void Reconcile()
    {
        var c = _config();
        var kid = c.ApiKey.Length > 0 ? PeerLink.Kid(PeerLink.KeyKey(c.ApiKey)) : "";
        var hostKey = c.Enabled ? $"{c.Host}|{kid}" : "";
        var key = c.Enabled ? $"{hostKey}|{c.PinsKey}" : "";
        if (key == _runningKey) return;
        var carry = hostKey.Length > 0 && hostKey == _runningHostKey;                       // the same key at the same address: a person's pin set, say
        _runningKey = key;
        _runningHostKey = hostKey;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        var previous = _run;
        var run = new Run { Kid = kid.Length > 0 ? kid : null, ShowNeeds = c.FollowStage };
        if (carry) run.Carry(previous);                                                     // its holds and standing go on
        _sight = StagewatchSight.None;
        lock (_trustGate)
        {
            _run = run;                                                                     // a new run, after its empty sight: no context, no mark
            _trust = null;                                                                  // and nothing waiting for Trust until its own frame offers it
        }
        Volatile.Write(ref _lastWordMs, long.MinValue);
        _approvalsLive = false;
        _approvalWords = "";
        _tlsWords = "";
        _note = "";
        _reached = "";
        _carrier = "";
        _nodeId = "";
        _held = "";
        if (key.Length == 0)
        {
            _status = "Stagewatch link off.";
            _changed();
            return;
        }
        _cts = new CancellationTokenSource();
        var host = c.Host;
        var apiKey = c.ApiKey;
        var pins = new Dictionary<string, CertificatePin>(StringComparer.Ordinal);
        foreach (var p in c.Pins) pins[p.Key] = p;
        var ct = _cts.Token;
        _status = host.Length > 0 ? $"Stagewatch link: dialling {host}…" : "Stagewatch link: listening for a Stagewatch on the show network…";
        // A lane that faults is said, never lost in silence: the link stops there until it is turned off and on.
        _ = Task.Run(() => RunAsync(run, host, apiKey, pins, ct), CancellationToken.None).ContinueWith(
            t => Log.Error("Stagewatch link: the link stopped on a fault — turn it off and on to dial again.", t.Exception!.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _ = Task.Run(() => EvaluateAsync(run, ct), CancellationToken.None).ContinueWith(
            t => Log.Error("Stagewatch link: the link's evaluator stopped on a fault — turn the link off and on to watch it again.", t.Exception!.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>
    /// Where to dial and what the show network says of it. <paramref name="Heard"/>: a hello names this Stagewatch;
    /// <paramref name="Advertised"/>: the certificate that hello names ("" when none). A hello is a hint — try TLS first —
    /// and never decides the carrier: the pin and the probe do. <paramref name="Inst"/>: the process its hello names ("" when
    /// none), whose clock the link measures (Show Bus 1.3 §13.11.7).
    /// </summary>
    private sealed record Target(string Host, int Port, string Name, string PinKey, string Advertised, bool Heard, bool Approvals, string Inst = "")
    {
        public bool HelloTls => TlsFingerprint.IsValid(Advertised);
    }

    private async Task RunAsync(Run run, string typedHost, string apiKey, IReadOnlyDictionary<string, CertificatePin> pins, CancellationToken ct)
    {
        var finding = 0;
        // The id this run last dialled or was held off: found on the show network, it is dialled again while it is heard.
        var last = "";
        Holds.KeyIs(run.Kid, WallMs());                                                     // 13.15.3: a hold of another key, or one that ended, is over
        while (!ct.IsCancellationRequested)
        {
            var target = Resolve(typedHost, pins, last, out var conflict);
            if (target is null)
            {
                var wait = ShowBusLiveness.Redial(finding++, Jitter());
                var wasHeld = _held;
                if (conflict is not null)
                {
                    // Show Bus 1.3 §2, 13.15.3: no new link to an id two running nodes say; dialled again once the mark clears.
                    // The id stays this link's, so the hello table keeps its entry and its mark whatever else is heard.
                    _status = $"Stagewatch link: {conflict.Words} — {ShowBusView.ConflictHoldWords}.";
                    var clears = conflict.ClearsUtc - DateTime.UtcNow + TimeSpan.FromMilliseconds(100);
                    if (clears > TimeSpan.Zero && clears < wait) wait = clears;
                    _nodeId = last = conflict.Id;
                    _held = conflict.Words;
                }
                else
                {
                    _status = typedHost.Length > 0
                        ? $"Stagewatch link: '{typedHost}' is not an address — host:port, or empty to find one on the show network."
                        : "Stagewatch link: no Stagewatch heard on the show network yet (UDP 9928).";
                    _nodeId = "";
                    _held = "";
                }
                if (!string.Equals(wasHeld, _held, StringComparison.Ordinal)) UiThread.Post(_changed);   // the hold reaches the desk's readers
                await Delay(wait, ct);
                continue;
            }
            finding = 0;
            _held = "";
            _nodeId = target.PinKey.StartsWith("node:", StringComparison.Ordinal) ? target.PinKey[5..] : "";
            if (_nodeId.Length > 0) last = _nodeId;
            var core = CoreFor(run, target, pins);
            long? dueIn;
            lock (_core) dueIn = core.Machine.DialIn(Now());
            if (dueIn is null)
            {
                // CERTIFICATE CHANGED: nothing of this desk's goes to it until a person accepts the new one (13.8.3).
                await ProbeChangedAsync(core, target, ct);
                continue;
            }
            if (dueIn > 0)
            {
                // The core's wait (13.15.1), or a hold that lets nothing be dialled: the target is looked at again each second.
                await Delay(TimeSpan.FromMilliseconds(Math.Min(dueIn.Value, (long)PeerLink.CheckEvery.TotalMilliseconds)), ct);
                continue;
            }
            bool isPublic;
            lock (_core)
            {
                var now = Now();
                isPublic = core.Machine.Dial(now) is null;
                // Inside a refused key's hold the dial reads the public view, the key proved again on a fresh socket at its end.
                if (isPublic && core.Machine.DialPublic(now) is null) continue;
                // 13.11.7: the clock measures one Stagewatch process; another inst in its hello empties the window.
                core.Machine.PeerInst(now, target.Inst);
            }
            try
            {
                await ConnectAsync(run, core, target, apiKey, isPublic, pins, ct);
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
            // The connection ended, or the dial failed: the core's next dial waits 13.15.1's wait, whose count only a link verified
            // and stable resets (13.15.2) — and a hold replaces it (13.15.3).
            lock (_core)
            {
                core.Machine.Closed(Now());
                core.Machine.Drain();
            }
            if (Interlocked.Exchange(ref run.Redial, "") is { Length: > 0 } redial) _status = "Stagewatch link: " + redial;
            _carrier = "";
            _reached = "";
            _sight = _sight with { State = ShowBusState.Gone };
            _approvalsLive = false;
            var held = HeldFor(run);
            if (held > TimeSpan.Zero && apiKey.Length > 0 && !_status.Contains("dialling again in", StringComparison.Ordinal))
                _status = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Stagewatch link: {target.Name} — this desk's key is held {Math.Ceiling(held.TotalSeconds):0} s more; its public view is read without it meanwhile.");
            UiThread.Post(_changed);
        }
    }

    /// <summary>
    /// The core for <paramref name="target"/>: the run's own while it dials under the same name, else a new one made from what this
    /// desk keeps for that name — the pin and who made it (the settings), and a refused key's hold by target and kid
    /// (<see cref="Holds"/>), which the run's keyed calls then wait out too (13.15.3). A run that reaches its Stagewatch under another
    /// name — a typed address its hello names one moment and not the next, or another Stagewatch heard in its place — goes on with
    /// what the run owes: the redial's count and wait (13.15.2: a flapping hello never resets the backoff) and every hold the run
    /// stands under, kept under the new name too before anything is dialled, so the key is presented again only when its hold ends.
    /// </summary>
    private LinkCore CoreFor(Run run, Target target, IReadOnlyDictionary<string, CertificatePin> pins)
    {
        lock (_core)
        {
            if (run.Core is { } kept && kept.Target == target.PinKey) return kept;
            var now = Now();
            var nodeId = target.PinKey.StartsWith("node:", StringComparison.Ordinal) ? target.PinKey[5..] : null;
            var saved = pins.TryGetValue(target.PinKey, out var p) && TlsFingerprint.IsValid(p.Fingerprint) ? p : null;
            var pin = saved is null ? null : new LinkPin(nodeId, saved.Fingerprint[TlsFingerprint.Prefix.Length..], saved.Trust);
            var hold = run.Kid is { } kid ? Holds.For(target.PinKey, kid, now.Wall) : null;
            var settings = new LinkSettings
            {
                Carrier = LinkCarrier.Ws,
                Name = target.Name,
                Kind = "main",
                NodeId = nodeId,
                Address = $"{target.Host}:{target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                Key = run.Kid,
                ShowNeeds = run.ShowNeeds,
                Uses = "post",
            };
            var machine = new PeerLinkMachine(settings, new LinkKept(pin, null, hold?.UntilWallMs, hold?.Word, HeldPublic: hold?.Public ?? true), now, Jitter);
            if (hold is { BusyUntilWallMs: var busyWall } && busyWall > now.Wall) machine.KeyRefused(now, "busy", busyWall - now.Wall);
            var before = run.Core;
            if (before is not null) machine.Continue(now, before.Machine);
            // A hold the run stands under that no core of it recorded (one carried from the run before) holds this core too.
            long heldUntil, busyUntil;
            lock (run.Gate) (heldUntil, busyUntil) = (run.HeldUntilMs, run.BusyUntilMs);
            if (heldUntil > now.Mono && (machine.Kept.HeldUntilWall ?? long.MinValue) < now.Wall + (heldUntil - now.Mono)) machine.KeyRefused(now, "key", heldUntil - now.Mono);
            if (busyUntil > now.Mono) machine.KeyRefused(now, "busy", busyUntil - now.Mono);
            run.Core = new LinkCore(target.PinKey, machine) { FirstSaved = saved is { Trust: PinTrust.First } ? saved.Fingerprint : "" };
            run.Targets.Add(target.PinKey);
            if (run.Kid is { } heldKid && machine.Kept is { HeldUntilWall: { } until } carried)
                Holds.Hold(target.PinKey, heldKid, until, carried.HeldRefusal ?? "key", now.Wall, carried.HeldPublic);
            if (hold is not null && before is null) RestoreHold(run, target, hold, now);
            return run.Core;
        }
    }

    /// <summary>
    /// A hold kept from before this start (13.15.3): the run's keyed calls wait it out on the link's clock, and every reader
    /// says the key is held — said once.
    /// </summary>
    private static void RestoreHold(Run run, Target target, LinkHold hold, LinkNow now)
    {
        var until = now.Mono + (hold.UntilWallMs - now.Wall);
        var busy = hold.BusyUntilWallMs > now.Wall ? now.Mono + (hold.BusyUntilWallMs - now.Wall) : long.MinValue;
        lock (run.Gate)
        {
            if (busy > run.BusyUntilMs) run.BusyUntilMs = busy;
            if (hold.Word != "busy")
            {
                if (until > run.HeldUntilMs) run.HeldUntilMs = until;
                if (!run.Key.Refused) run.Key = new StagewatchKey { Word = "refused", Said = LinkHolds.Words(hold) + ", said before this desk started", Keyed = true };
            }
        }
        Log.Info($"Stagewatch link: {target.Name} — this desk's key was {LinkHolds.Words(hold)}, said before this start; its public view is read without it meanwhile.");
    }

    /// <summary>How much longer a refused key's hold or a rate limit's holds every keyed call (zero when neither).</summary>
    private TimeSpan HeldFor(Run run)
    {
        long until;
        lock (run.Gate) until = Math.Max(run.HeldUntilMs, run.BusyUntilMs);
        return Left(until);
    }

    /// <summary>How much longer the node routes wait: any hold, a key that speaks for no node of this desk's among them.</summary>
    private TimeSpan NodeHeldFor(Run run)
    {
        long until;
        lock (run.Gate) until = Math.Max(Math.Max(run.HeldUntilMs, run.BusyUntilMs), run.NodeHeldUntilMs);
        return Left(until);
    }

    private TimeSpan Left(long untilMs)
    {
        var left = untilMs - MonoMs();
        return left > 0 ? TimeSpan.FromMilliseconds(left) : TimeSpan.Zero;
    }

    private async Task Delay(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await Task.Delay(wait, Time, ct);
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
    /// The address to dial: the typed one, or a live Stagewatch heard on the show network — with what its hello says of TLS
    /// and the approval queue. A typed address borrows a hello's words only when that hello came from it (an address its
    /// id's datagrams came from, never what a hello says of its own host: Show Bus 1.3 §2, 13.4.3): one such Stagewatch, or
    /// of several the one whose id this desk pinned; else none, and the address stays its own record. Found on the show
    /// network, the one this run last dialled is dialled again while it is heard, else one this desk pinned, else the first;
    /// at the address the table keeps for it while that address is still heard, so a node heard on two addresses is not
    /// dialled on each in turn. An id two running nodes say gets no new link while the mark stands: null, with
    /// <paramref name="conflict"/> saying which — and the link holds rather than moving to another Stagewatch, unless it is
    /// one this desk pinned. Over TLS or not is the dial's to decide (the pin and the probe), never this.
    /// </summary>
    private Target? Resolve(string typed, IReadOnlyDictionary<string, CertificatePin> pins, string last, out ShowBusConflict? conflict)
    {
        conflict = null;
        var now = DateTime.UtcNow;
        var live = _peers(now).Where(p => p.Kind == "main" && p.StateAt(now) == ShowBusState.Live && p.Address.Length > 0 && (p.Ports.Ws > 0 || p.Ports.Http > 0)).ToList();
        bool Pinned(ShowBusPeer p) => pins.ContainsKey("node:" + p.Id);
        if (typed.Length > 0)
        {
            if (StageFollow.ParseAddress(typed) is not { } t) return null;
            var heard = live.Where(p => p.HeardAt(t.Host) && (p.Ports.Http == t.Port || p.Ports.Ws == t.Port || p.Ports.Https == t.Port)).ToList();
            var named = heard.Count == 1 ? heard[0] : heard.Where(Pinned).ToList() is { Count: 1 } pinned ? pinned[0] : null;
            if (named?.Conflict is { } held)
            {
                conflict = held;
                return null;
            }
            if (named is not null) return Of(named, t.Host, t.Port);
            var key = "addr:" + t.Host.ToLowerInvariant() + ":" + t.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new Target(t.Host, t.Port, t.Host, key, "", false, false);
        }
        var peer = live.FirstOrDefault(p => last.Length > 0 && p.Id == last) ?? live.FirstOrDefault(Pinned) ?? live.FirstOrDefault();
        if (peer?.Conflict is { } marked)
        {
            peer = live.FirstOrDefault(p => p.Conflict is null && Pinned(p));
            if (peer is null)
            {
                conflict = marked;
                return null;
            }
        }
        if (peer is null) return null;
        return Of(peer, peer.Address, peer.Ports.Https > 0 && peer.OffersTls ? peer.Ports.Https : peer.Ports.Ws > 0 ? peer.Ports.Ws : peer.Ports.Http);

        static Target Of(ShowBusPeer p, string host, int port)
            => new(host, p.OffersTls && p.Ports.Https > 0 ? p.Ports.Https : port, p.Name.Length > 0 ? p.Name : p.Host, "node:" + p.Id, p.Tls, true,
                p.Caps.Contains(DeskProposals.Cap, StringComparer.Ordinal), p.Instance);
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

    /// <summary>
    /// The certificate a TLS dial was shown, told to the core first. Pinned by a person (or a proof): that one or none — another
    /// is CERTIFICATE CHANGED, refused, and waits for ACCEPT (13.8.3). Pinned by no one but the desk at first use, or not at all:
    /// any certificate carries the handshake, and one other than the hello names, or than the one pinned first, is a note,
    /// never a refusal (13.4.2, 13.8.2) — no key goes to it off this machine whatever it is.
    /// </summary>
    private bool CheckCertificate(LinkCore core, Target target, string pin, string first, X509Certificate? certificate, ref string presentedOut)
    {
        if (certificate is null) return false;
        using var c2 = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var presented = TlsFingerprint.Of(c2);
        presentedOut = presented;
        lock (_core) core.Machine.Certificate(Now(), presented[TlsFingerprint.Prefix.Length..]);
        var name = "Stagewatch " + target.Name;
        if (pin.Length > 0)
        {
            var verdict = CertificatePins.Check(pin, presented, target.Advertised);
            _tlsWords = CertificatePins.Words(verdict, name, presented);
            if (verdict == PinVerdict.Changed)
            {
                _pendingFingerprint = presented;
                _pendingPinKey = target.PinKey;
                lock (_core) core.ProbeAt = MonoMs() + (long)PeerLink.CertProbe.TotalMilliseconds;   // this look was the first
            }
            return CertificatePins.Accepts(verdict);
        }
        _tlsWords = first.Length > 0 && first != presented
            ? $"{name} now shows {TlsFingerprint.Short(presented)}, not {TlsFingerprint.Short(first)} it showed at first use — compare it on its screen"
            : TlsFingerprint.IsValid(target.Advertised) && target.Advertised != presented
                ? $"{name} shows {TlsFingerprint.Short(presented)}, not the certificate its hello names ({TlsFingerprint.Short(target.Advertised)}) — compare it on its screen"
                : $"{name}'s certificate {TlsFingerprint.Short(presented)} is pinned at first use — compare it on its screen";
        return true;
    }

    /// <summary>
    /// One connection. A name is resolved once and its addresses dialled in turn (<see cref="DialOrder"/>), each as itself —
    /// the carrier by the pin, the probe and that address (13.3.2, 13.4.2): a Stagewatch a person pinned over TLS or nothing;
    /// at an address that is not loopback — and at loopback when the hello names a certificate or a first pin is kept — TLS
    /// first, falling back to plain only when it answers without TLS; at loopback with neither, plain. The key rides only to a
    /// certificate a person pinned or to loopback (13.6.8): a TLS link to any other is read without it, its certificate pinned
    /// first and offered for Trust; a plain one off this machine likewise, its words saying no TLS is on offer. Inside a refused
    /// key's hold (<paramref name="isPublic"/>) nothing of the key goes at all. The next address is tried only while no socket
    /// of this one has reached the far end; once the far end has answered — a certificate or none, a status, a verdict — the
    /// dial ends there, so nothing said at one address is ever taken to another. Every socket of the connection — the upgrade,
    /// the side calls, the re-probe — goes to the one address that answered.
    /// </summary>
    private async Task ConnectAsync(Run run, LinkCore core, Target target, string apiKey, bool isPublic, IReadOnlyDictionary<string, CertificatePin> pins, CancellationToken outer)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = linked.Token;
        var saved = pins.TryGetValue(target.PinKey, out var held) && TlsFingerprint.IsValid(held.Fingerprint) ? held : null;
        var pin = saved is { Trust: not PinTrust.First } ? saved.Fingerprint : "";            // a person's (or a proof's) pin: TLS or nothing, and the key over it
        var first = saved is { Trust: PinTrust.First } ? saved.Fingerprint : "";              // the desk's pin at first use: compared, never trusted
        var pinned = pin.Length > 0;
        var key = isPublic ? "" : apiKey;
        _note = pinned ? HelloNote(target, pin) : "";                                       // a hello that contradicts the pin is a note, never a refusal
        var addresses = await DialAddressesAsync(target, ct);
        var presented = "";

        // One address, as itself: its own carrier, and the key over plain ws only when that address is loopback (local). Each
        // dial's probe is kept in probes, so the caller knows whether any socket of this address reached the far end.
        async Task<Dialled> DialAtAsync(IPAddress at, bool local, List<DialProbe> probes)
        {
            lock (_core) core.Machine.Reached(Now(), local);
            if (pinned || first.Length > 0 || target.HelloTls || !local)
            {
                // The short wait for a certificate is only there to fall back soon: a pinned Stagewatch has no fallback, and
                // one that answered late before is given the whole budget.
                var within = pinned || _slowTls == target.PinKey ? UpgradeWithin : TlsAnswerWithin;
                var probe = new DialProbe(tls: true, within);
                probes.Add(probe);
                // The key rides the upgrade only to a certificate a person pinned — the check refuses any other — or to loopback.
                var tlsKey = key.Length > 0 && (pinned || local) ? key : "";
                RemoteCertificateValidationCallback validate = (_, cert, _, _) =>
                {
                    probe.Certificate();
                    return CheckCertificate(core, target, pin, first, cert, ref presented);
                };
                try
                {
                    var (handler, invoker, socket) = await DialAsync(at, probe, validate, target, tls: true, tlsKey, SelfId, ct);
                    return new Dialled(at, handler, invoker, socket, Tls: true, Keyed: tlsKey.Length > 0, Local: local);
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
                }
            }
            // Plain: the key goes only to loopback. Anywhere else, the Stagewatch is read without the key.
            var keyed = key.Length > 0 && local;
            var plain = new DialProbe(tls: false, TlsAnswerWithin);
            probes.Add(plain);
            var (plainHandler, plainInvoker, plainSocket) = await DialAsync(at, plain, null, target, tls: false, keyed ? key : "", SelfId, ct);
            return new Dialled(at, plainHandler, plainInvoker, plainSocket, Tls: false, Keyed: keyed, Local: local);
        }

        Dialled? dialled = null;
        var missed = new List<string>(addresses.Count);                                  // each address nothing answered at, and why
        foreach (var at in addresses)
        {
            var local = IsLocal(at.IsIPv4MappedToIPv6 ? at.MapToIPv4() : at);
            // A link turned off or dialled anew since the last address has words of its own: the dial ends here, before it
            // says which address it tries or dials one more.
            ct.ThrowIfCancellationRequested();
            if (missed.Count > 0)
            {
                _status = $"Stagewatch link: dialling {target.Name} at {target.Host}:{target.Port} — {string.Join(", ", missed)}; trying {at}…";
                UiThread.Post(_changed);
            }
            var probes = new List<DialProbe>(2);
            presented = "";
            try
            {
                dialled = await DialAtAsync(at, local, probes);
                break;
            }
            catch (Exception ex) when (!probes.Exists(p => p.Reached)
                                       && ex is WebSocketException or HttpRequestException or IOException or SocketException or OperationCanceledException)
            {
                missed.Add($"{at} {Missed(ex)}");                                         // no socket reached it, so nothing of this desk's did: the next
            }
        }
        ct.ThrowIfCancellationRequested();                                                // a stopped dial's misses are not the link's words: it ends unsaid
        if (dialled is null)
            throw new DialRefusedException($"{target.Name} at {target.Host}:{target.Port}: {string.Join(", ", missed)} — nothing sent; trying again.");
        var address = dialled.At;
        var tls = dialled.Tls;
        var keyed = dialled.Keyed;
        var ws = dialled.Socket;
        var handler = dialled.Handler;
        using var ownedHandler = handler;
        using var ownedInvoker = dialled.Invoker;
        using var ownedSocket = ws;
        // Answered after the link was turned off or dialled anew: the connection is let go unread, before its carrier, the
        // address it reached or its words are set over the ones the link has now.
        ct.ThrowIfCancellationRequested();
        // No TLS on offer: a local link goes on plain, another machine's is read without the key (13.3.2, 13.3.4).
        if (!tls) lock (_core) core.Machine.NoTls(Now());
        var heldBack = key.Length > 0 && !tls && !keyed;                                    // off this machine, and it offers no TLS
        var withheld = key.Length > 0 && tls && !keyed;                                     // over TLS to a certificate no person pinned (13.6.8)
        _carrier = tls ? "tls" : heldBack || (isPublic && !dialled.Local) ? "keyless" : "plain";
        if (heldBack)
        {
            _note = target.HelloTls
                ? $"{target.Name} answered without TLS though its hello names a certificate — the key stays on this desk"
                : $"{target.Name} offers no TLS — the key stays on this desk";
        }
        if (keyed)
        {
            lock (_trustGate) _trust = null;                                                // the key goes: nothing waits for Trust
        }
        // The address a name led to: the log's line names it, and the link's words do while the connection is up whenever an
        // address before it did not answer — a name with an address that is dead costs every dial its wait.
        _reached = missed.Count > 0 ? $"reached at {address} after {string.Join(", ", missed)}" : "";
        var reached = IPAddress.TryParse(target.Host, out _) ? "" : $" (reached at {address})";
        var how = tls ? withheld ? "TLS, its key held back until its certificate is trusted" : "TLS"
            : heldBack ? "plain ws, read without its key" : "plain ws";
        if (isPublic) how += ", read without this desk's key while it is held";
        var said = target.PinKey + "|" + how + "|" + _note + "|" + reached;
        if (said != _carrierSaid)
        {
            _carrierSaid = said;
            Log.Info($"Stagewatch link: {target.Name} at {target.Host}:{target.Port}{reached} over {how}" + (_note.Length > 0 ? " — " + _note + "." : "."));
        }
        Volatile.Write(ref _lastWordMs, MonoMs());
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
        UiThread.Post(_changed);

        // The side calls ride the dial's own handler: the same address, the same pin, the key only where the socket's carried it.
        var sideKey = keyed ? apiKey : "";
        // Every answer is read whole before it is parsed, an error's body too: bounded here, well above a context's 64 KiB.
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = StagewatchFrame.SideCallWithin, MaxResponseContentBufferSize = SideAnswerMost };
        var baseUri = Address(target, tls, socket: false);
        using var sendGate = new SemaphoreSlim(1, 1);
        var connection = new Connection(run, core, target, tls, sideKey.Length > 0, isPublic, withheld, apiKey, ws, sendGate);
        run.Live = connection;
        var beat = Task.Run(() => BeatAsync(connection, ct), CancellationToken.None);
        var lane = Task.Run(() => SideCallsAsync(connection, show, http, baseUri, sideKey, ct), CancellationToken.None);
        var reprobe = heldBack ? Task.Run(() => ReprobeAsync(run, address, target, ws, ct), CancellationToken.None) : Task.CompletedTask;
        try
        {
            await ReceiveAsync(connection, show, ws, name, ct);
        }
        finally
        {
            if (ReferenceEquals(run.Live, connection)) run.Live = null;
            await linked.CancelAsync();
            await EndedWithTheLink(reprobe);                                           // it throws nothing but the link's own cancel
            await EndedWithTheLink(lane);
            await EndedWithTheLink(beat);
        }
    }

    /// <summary>
    /// After CERTIFICATE CHANGED (13.8.3): nothing of this desk's is sent until a person accepts the new certificate, typing it on
    /// the Nodes page. The certificate the Stagewatch shows is looked at again at most every <c>peerlink.cert.probe.ms</c> — one TLS
    /// greeting, ended at the certificate — so the words name what it shows now; each look counted.
    /// </summary>
    private async Task ProbeChangedAsync(LinkCore core, Target target, CancellationToken ct)
    {
        long wait;
        lock (_core) wait = core.ProbeAt - MonoMs();
        if (wait > 0)
        {
            await Delay(TimeSpan.FromMilliseconds(Math.Min(wait, (long)PeerLink.CheckEvery.TotalMilliseconds)), ct);
            return;
        }
        lock (_core)
        {
            core.ProbeAt = MonoMs() + (long)PeerLink.CertProbe.TotalMilliseconds;
            core.Probes++;
        }
        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await DialAddressesAsync(target, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException && !ct.IsCancellationRequested)
        {
            return;                                                                     // looked at again in a minute
        }
        foreach (var at in addresses)
        {
            var shown = await ShownCertificateAsync(at, target, ct);
            if (shown.Length == 0) continue;
            if (shown != _pendingFingerprint && _pendingPinKey == target.PinKey)
            {
                _pendingFingerprint = shown;
                _tlsWords = CertificatePins.Words(PinVerdict.Changed, "Stagewatch " + target.Name, shown);
                Log.Info($"Stagewatch link: {target.Name} now shows {TlsFingerprint.Short(shown)} — {_tlsWords}.");
                UiThread.Post(_changed);
            }
            break;
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
            await Task.Delay(TlsReprobeEvery, Time, ct);
            if ((await ShownCertificateAsync(address, target, ct)).Length == 0 || ct.IsCancellationRequested) continue;
            _slowTls = target.PinKey;
            var words = $"{target.Name} at {target.Host}:{target.Port} answers TLS now — dialling again over TLS.";
            Volatile.Write(ref run.Redial, words);
            Log.Info("Stagewatch link: " + words);
            ws.Abort();                                                                 // the receive loop ends; the run dials again
            return;
        }
    }

    /// <summary>
    /// One TLS greeting to <paramref name="address"/>: the fingerprint of the certificate the far end shows within the
    /// upgrade's budget, or "" when none came. The handshake is ended at the certificate, so nothing of this desk's follows
    /// the greeting.
    /// </summary>
    private static async Task<string> ShownCertificateAsync(IPAddress address, Target target, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(UpgradeWithin);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var shown = "";
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
                    if (certificate is not null)
                    {
                        using var seen = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                        shown = TlsFingerprint.Of(seen);
                    }
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
    /// The addresses this dial tries, in turn: the host as typed or heard when it is an address, else its name resolved once
    /// within <see cref="ResolveWithin"/>, in <see cref="DialOrder"/>. Every socket of a connection goes to the one address
    /// that answered and no other, so whether the key may go plain is decided by what is dialled, never by a name.
    /// </summary>
    private async Task<IReadOnlyList<IPAddress>> DialAddressesAsync(Target target, CancellationToken ct)
    {
        if (IPAddress.TryParse(target.Host, out var literal)) return [literal];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ResolveWithin);
        IPAddress[] found;
        try
        {
            // The wait, not only the lookup, is bounded: a resolver that does not take the cancel is left to finish on its own.
            found = await ResolveName(target.Host, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DialRefusedException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{target.Name} at {target.Host}:{target.Port}: the name did not resolve within {ResolveWithin.TotalSeconds:0} s — nothing sent; trying again."));
        }
        var order = DialOrder(found);
        return order.Count > 0 ? order : throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>
    /// Round 92: the most addresses one dial of a name tries — as many as the hello table keeps for one id
    /// (<see cref="ShowBusPeers.MaxAddresses"/>, Show Bus 1.3 §14 <c>peer.addresses</c>), so a name is dialled at no more
    /// addresses than one node is heard at, and a pass is bounded: the resolve's 4 s; at each address nothing answers at, at
    /// most the upgrade's 5 s; at the address that answers, what any one dial may take — at most 15 s: 5 s to connect, 5 s
    /// for a certificate when the whole budget is waited for one (a pinned Stagewatch, or one slow to answer TLS before),
    /// then 5 s more, to the upgrade's answer or, when no certificate came, to a plain dial's. So 4 + 3 × 5 + 15 = 34 s at
    /// worst before the redial's backoff. Set by the hello's bound, not by a measurement.
    /// </summary>
    public const int DialAddressesAtMost = ShowBusPeers.MaxAddresses;

    /// <summary>
    /// Round 92: the order a name's addresses are dialled in — IPv4 first, as a Stagewatch listens on IPv4 alone, then IPv6,
    /// each family in the resolver's order; an IPv4 address written as IPv6 is its IPv4 one, a repeat is dropped, and at
    /// most <see cref="DialAddressesAtMost"/> are kept.
    /// </summary>
    public static IReadOnlyList<IPAddress> DialOrder(IEnumerable<IPAddress> found)
    {
        var plain = found.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Distinct().ToList();
        return plain.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Concat(plain.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6))
            .Take(DialAddressesAtMost)
            .ToArray();
    }

    /// <summary>Why nothing answered at an address, in the link's words: refused, unreachable, or silent past the upgrade's budget.</summary>
    private static string Missed(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "refused",
                    SocketError.TimedOut => "did not answer",
                    SocketError.AddressFamilyNotSupported => "unreachable from this machine",
                    SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostDown or SocketError.NetworkDown => "unreachable",
                    _ => "not reached (" + socket.Message + ")",
                };
            }
        }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"did not answer within {UpgradeWithin.TotalSeconds:0} s");
    }

    /// <summary>A dial whose far end answered: the address it reached, its handler, the invoker the upgrade ran on, the WebSocket, its carrier, and whether the address is loopback.</summary>
    private sealed record Dialled(IPAddress At, SocketsHttpHandler Handler, HttpMessageInvoker Invoker, ClientWebSocket Socket, bool Tls, bool Keyed, bool Local);

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
    /// The beat: a ping every <see cref="PeerLink.PingEvery"/> — strictly inside <c>ping.min.ms</c>–<c>ping.max.ms</c> — on a timer of
    /// its own, never behind a side call (13.10.1, 13.7.2); each ping's t1 told to the core before it leaves, so only a pong that
    /// answers one of this link's last pings is a clock sample (13.10.2).
    /// </summary>
    private async Task BeatAsync(Connection c, CancellationToken ct)
    {
        var beatMs = (long)PeerLink.PingEvery.TotalMilliseconds;
        while (!ct.IsCancellationRequested && c.Socket.State == WebSocketState.Open)
        {
            // Each ping is due a beat after the last one left, however long that one's write took.
            var due = MonoMs() + beatMs;
            await PingAsync(c, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, due - MonoMs())), Time, ct);
        }
    }

    /// <summary>
    /// One ping. A write that takes longer than <c>link.write.deadline.max.ms</c> cuts the connection (13.14): a far end that
    /// takes no more of this desk's writes is dialled again.
    /// </summary>
    private async Task PingAsync(Connection c, CancellationToken ct)
    {
        long t1;
        lock (_core)
        {
            var now = Now();
            t1 = now.Wall;
            c.Core.Machine.Ping(now, t1);
        }
        var bytes = Encoding.UTF8.GetBytes(StagewatchFrame.Ping(t1));
        await c.SendGate.WaitAsync(ct);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(PeerLink.WriteDeadline);
            await c.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Volatile.Write(ref c.Run.Redial, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{c.Target.Name} took no ping in {PeerLink.WriteDeadline.TotalSeconds:0} s — the connection is cut and dialled again."));
            c.Socket.Abort();
        }
        finally
        {
            c.SendGate.Release();
        }
    }

    /// <summary>
    /// The evaluator (13.10.3, 13.10.4): every <c>link.check.max.ms</c>, on a timer of its own for the whole run — never behind a
    /// side call, a write or a dial — the core decides whether the link up now is still alive. A link silent past
    /// <c>link.drop.ms</c> is dropped and dialled again; one the core ended (a refused key's hold over, so the key is proved again
    /// on a fresh socket; a side call that asked for a relink) is let go. After a gap of its own past <c>link.selfgap.ms</c> — this
    /// desk paused, slept or starved — it pings at once and drops nothing until it has heard again, said in one line.
    /// </summary>
    private async Task EvaluateAsync(Run run, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PeerLink.CheckEvery, Time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            var c = run.Live;
            LinkEvaluation ev;
            bool keeps;
            lock (_core)
            {
                if (run.Core is not { } core) continue;
                var now = Now();
                ev = core.Machine.Evaluate(now);
                keeps = core.Machine.KeepsConnection(now);
            }
            if (ev.PingedAll && ev.GapMs is { } gap)
            {
                Log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Stagewatch link: this desk's evaluator ran {gap / 1000.0:0.#} s after its last — paused, asleep or starved; the link is pinged now and dropped only once it stays silent."));
                if (c is not null) await PingAfterGapAsync(c, ct);
            }
            if (c is null || c.Socket.State != WebSocketState.Open || !ReferenceEquals(c.Core, run.Core)) continue;
            if (ev.Closed)
            {
                var silent = (MonoMs() - Volatile.Read(ref _lastWordMs)) / 1000.0;
                Volatile.Write(ref run.Redial, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{c.Target.Name} silent for {silent:0} s — dropping and dialling again."));
                c.Socket.Abort();
                continue;
            }
            if (keeps) continue;
            Volatile.Write(ref run.Redial, c.Ending.Length > 0 ? c.Ending : $"{c.Target.Name} — this desk's key is presented again on a fresh link.");
            c.Socket.Abort();
        }
    }

    /// <summary>The evaluator's ping after a gap of its own: through the connection's one writer, which may have closed meanwhile.</summary>
    private async Task PingAfterGapAsync(Connection c, CancellationToken ct)
    {
        try
        {
            await PingAsync(c, ct);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or WebSocketException or InvalidOperationException or OperationCanceledException)
        {
            Log.Info("Stagewatch link: the connection ended before the evaluator's ping — the next connection pings on its beat.");
        }
    }

    /// <summary>
    /// The side calls' lane (13.7.2): every beat, one call at a time, each within <c>link.sidecall.timeout.ms</c> — this desk's read
    /// of the Stagewatch's context and the post of its own every five seconds, the approval queue and the results owed it. Never
    /// in the beat's path: a slow answer delays only the calls behind it, and its fault is counted on its own and never moves the
    /// link. On a connection that carries the key, no keyed call goes before its first message is read (a verdict, on a
    /// Stagewatch that sends one), and none while a refused key or a rate limit holds them — meanwhile the timer is followed on
    /// the public frame and the Stagewatch's context, which this desk cannot read, is not shown as if it were current; while a
    /// key that speaks for no node of this desk's holds the node routes, the context is read and shown and only those routes wait.
    /// </summary>
    private async Task SideCallsAsync(Connection c, HeardShow show, HttpClient http, Uri baseUri, string apiKey, CancellationToken ct)
    {
        var lastContext = long.MinValue;
        var target = c.Target;
        var name = target.Name;
        while (!ct.IsCancellationRequested && c.Socket.State == WebSocketState.Open)
        {
            try
            {
                var now = MonoMs();
                if (lastContext == long.MinValue || now - lastContext >= (long)StagewatchFrame.ContextEvery.TotalMilliseconds)
                {
                    if (!c.Keyed || KeyedCallsOpen(c))
                    {
                        lastContext = now;
                        await FetchContextAsync(c, show, http, new Uri(baseUri, "/api/context"), apiKey, ct);
                        if (NodeRoutesOpen(c)) await PostOwnContextAsync(c, http, new Uri(baseUri, "/api/peers/context"), apiKey, ct);
                    }
                    else if (c.Heard && HeldFor(c.Run) > TimeSpan.Zero && show.Context is not null)
                    {
                        show.Context = null;                                                   // its one writer: what a held key cannot read is not shown as current
                        UiThread.Post(_changed);
                    }
                }
                if (target.Approvals && apiKey.Length > 0 && SelfId.Length > 0 && NodeRoutesOpen(c)) await ApprovalsAsync(c, http, baseUri, apiKey, name, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // A step of the lane this desk did not expect: counted and said once a connection, and the lane goes on — the
                // link's beat and frames never stop because a side call misbehaved.
                Interlocked.Increment(ref _sideFaults);
                if (!c.FaultSaid)
                {
                    c.FaultSaid = true;
                    Log.Warn($"Stagewatch link: a side call to {name} failed — counted, and the side calls go on.", ex);
                }
            }
            try
            {
                await Task.Delay(ShowBusLiveness.Beat, Time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri url, string apiKey, string? json = null)
    {
        var r = new HttpRequestMessage(method, url);
        if (apiKey.Length > 0) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return r;
    }

    private static long RetryAfterOf(HttpResponseMessage response) => response.Headers.RetryAfter?.Delta is { } delta ? (long)Math.Ceiling(delta.TotalSeconds) : 0;

    /// <summary>
    /// The Stagewatch's context, kept on its show (its one writer). It touches nothing of the frame, so a context read during
    /// a stall cannot bring a time back. A keyed read refused (401), or rate-limited (429), holds the key as its answer says;
    /// any key the Stagewatch takes reads it, so a key that speaks for no node of this desk's goes on reading it.
    /// </summary>
    private async Task FetchContextAsync(Connection c, HeardShow show, HttpClient http, Uri url, string apiKey, CancellationToken ct)
    {
        try
        {
            var asked = Asked(c.Run);
            using var request = Request(HttpMethod.Get, url, apiKey);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // An older Stagewatch, or a key that may not read it: the timer still follows; a keyed refusal holds the key.
                if (apiKey.Length > 0) Answered(c, (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct), RetryAfterOf(response), asked, context: false);
                return;
            }
            // Its words reach every reader and the context this desk posts: a key echoed in them is masked before the parse.
            var json = StagewatchFrame.MaskSecret(await response.Content.ReadAsStringAsync(ct), c.Secret);
            if (ShowContext.Parse(json) is { } context)
            {
                show.Context = context;
                UiThread.Post(_changed);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            // a context that does not come this time comes next time; the frames are what the stage reads
            Interlocked.Increment(ref _sideFaults);
        }
    }

    /// <summary>
    /// This desk's own context to the Stagewatch (§6, `node` key): the Stagewatch's show network shows it, and it binds the
    /// key to this desk's id for the approval queue. Its answer is evidence of the key — the <c>verified</c> a Stagewatch 2.18
    /// names (none: the key speaks for no node of this desk's, its node routes held), a refusal in the Stagewatch's own words,
    /// held — and for a Stagewatch that sends no verdict on its socket, the only evidence (P-legacy). It is posted again on its
    /// own schedule, five seconds on, whatever came of it: one that went unanswered is said as unconfirmed, never failed.
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
            var said = Answered(c, (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct), RetryAfterOf(response), asked, context: true);
            _approvalsLive = response.IsSuccessStatusCode && _approvalsLive;
            if (!response.IsSuccessStatusCode)
            {
                var reading = PeerLink.ReadHttp((int)response.StatusCode, "", (int)RetryAfterOf(response));
                _approvalWords = reading.Outcome == LinkOutcome.Unconfirmed
                    ? $"Stagewatch {c.Target.Name} did not confirm this desk's context ({reading.Words}) — posted again in five seconds"
                    : $"Stagewatch {c.Target.Name} refused this desk's context — {said}";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            // No answer says nothing of it: unconfirmed, never failed (13.12.3), and posted again on its own schedule.
            Interlocked.Increment(ref _sideFaults);
            _approvalWords = $"Stagewatch {c.Target.Name} did not confirm this desk's context ({PeerLink.ReadHttp(null, null).Words}) — posted again in five seconds";
        }
    }

    private readonly RecentIds _ranDeskIds = new();

    /// <summary>
    /// Results this desk owes an approval desk (13.12.9) — by the Stagewatch they are owed to (its pin key) and its id for the
    /// proposal, written before the proposal runs, posted again from 3 s doubling to 30 s for 30 minutes until an answer settles
    /// each, sent to that Stagewatch only, before anything else of the queue.
    /// </summary>
    private readonly OwedReports _owed = new();

    /// <summary>The most results owed at once; past it the one owed longest is let go, said in the log.</summary>
    public const int MostOwed = OwedReports.Most;

    /// <summary>What a result owed before its proposal ran says until the run's own words replace it.</summary>
    public const string MayHaveRunWords = "unknown — the link or the desk stopped while it ran; it may have run here: look at this desk's state";

    /// <summary>
    /// §11: send what waits, post what is owed, read what became of ours, collect what was approved for this desk and answer it.
    /// A refusal is said in the approval desk's own words — a refused collect too — and any hold of the key holds the queue:
    /// nothing more is sent, taken or run once one stands, and a result it stopped is owed.
    /// </summary>
    private async Task ApprovalsAsync(Connection c, HttpClient http, Uri baseUri, string apiKey, string deskName, CancellationToken ct)
    {
        try
        {
            foreach (var e in Approvals.Unsent)
            {
                if (!NodeRoutesOpen(c)) return;
                // 13.12.7: a post whose answer said nothing of it is never posted again blind. The read of this desk's proposals
                // below settles it; only a read made a side call's whole time after it that does not list it sends it again.
                if (e.UnconfirmedAt != long.MinValue) continue;
                var asked = Asked(c.Run);
                int? status = null;
                var body = "";
                long retryAfter = 0;
                try
                {
                    using var post = Request(HttpMethod.Post, new Uri(baseUri, "/api/proposals"), apiKey, DeskProposals.Request(SelfId, SelfName, SelfId, e.Line, e.Reason, e.Risk));
                    using var res = await http.SendAsync(post, ct);
                    status = (int)res.StatusCode;
                    body = await res.Content.ReadAsStringAsync(ct);
                    retryAfter = RetryAfterOf(res);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _sideFaults);                                     // no answer: unconfirmed
                }
                if (status is >= 200 and < 300)
                {
                    if (DeskProposals.ParseOne(body) is { } made)
                    {
                        e.DeskId = made.Id;
                        Update(e, made.State.Length > 0 ? made.State : "pending", deskName);
                    }
                    continue;
                }
                var reading = PeerLink.ReadHttp(status, body, (int)retryAfter);
                if (reading.Outcome == LinkOutcome.Unconfirmed)
                {
                    e.UnconfirmedAt = MonoMs();
                    _approvalWords = $"approval desk: Stagewatch {deskName} did not confirm this desk's proposal ({reading.Words}) — it is looked for there before it is sent again";
                    Log.Info($"Approval desk ({deskName}): the proposal {e.LocalId} is unconfirmed — {reading.Words}; it is looked for there before it is sent again.");
                    return;
                }
                _approvalWords = $"the approval desk refused this desk's proposal — {Answered(c, status!.Value, body, retryAfter, asked, context: false)}";
                _approvalsLive = false;
                return;
            }
            // Live only while no hold stands, decided under the run's gate: a refusal applied there meanwhile wins.
            lock (c.Run.Gate)
            {
                if (!NodeRoutesOpen(c)) return;
                _approvalsLive = true;
                _approvalWords = $"approval desk: Stagewatch {deskName} — the assistant's proposals go there";
            }

            var to = c.Target.PinKey;
            foreach (var gone in _owed.Expired(MonoMs()))
                Log.Warn($"Approval desk ({deskName}): the result of {gone} is let go — owed {OwedReports.RetryFor.TotalMinutes:0} minutes and never settled there.");
            foreach (var (id, body) in _owed.Due(to, MonoMs()))
                if (!await PostResultAsync(c, http, baseUri, apiKey, deskName, id, body, ct)) return;

            var askedMine = Asked(c.Run);
            var readAt = MonoMs();
            using (var mine = Request(HttpMethod.Get, new Uri(baseUri, "/api/proposals?from=" + Uri.EscapeDataString(SelfId)), apiKey))
            using (var res = await http.SendAsync(mine, ct))
            {
                if (res.IsSuccessStatusCode)
                {
                    var listed = DeskProposals.ParseList(await res.Content.ReadAsStringAsync(ct));
                    foreach (var p in listed)
                    {
                        if (Approvals.ByDesk(p.Id) is { } e && e.State != p.State && !(e.Ran && p.State == "approved")) Update(e, p.State, deskName);
                        // An unconfirmed post that arrived there: its id is the approval desk's, and it is never posted again.
                        else if (DeskProposals.LocalId(p.Line) is { } local && Approvals.ByLocal(local) is { DeskId.Length: 0 } sent && sent.UnconfirmedAt != long.MinValue)
                        {
                            sent.DeskId = p.Id;
                            sent.UnconfirmedAt = long.MinValue;
                            Update(sent, p.State, deskName);
                        }
                    }
                    // One the approval desk does not list, read a side call's whole time after its post: it never arrived, and goes again.
                    foreach (var e in Approvals.Unsent.Where(e => e.UnconfirmedAt != long.MinValue && readAt - e.UnconfirmedAt >= (long)StagewatchFrame.SideCallWithin.TotalMilliseconds))
                        e.UnconfirmedAt = long.MinValue;
                }
                else Answered(c, (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct), RetryAfterOf(res), askedMine, context: false);
            }

            if (!NodeRoutesOpen(c)) return;
            var askedCollect = Asked(c.Run);
            using var approved = Request(HttpMethod.Get, new Uri(baseUri, "/api/proposals?target=" + Uri.EscapeDataString(SelfId) + "&state=approved"), apiKey);
            using var list = await http.SendAsync(approved, ct);
            if (!list.IsSuccessStatusCode)
            {
                // A refused collect is said: nothing approved for this desk reaches it until the Stagewatch hands it over.
                var refusal = Answered(c, (int)list.StatusCode, await list.Content.ReadAsStringAsync(ct), RetryAfterOf(list), askedCollect, context: false);
                _approvalWords = $"the approval desk refused to hand over what was approved for this desk — {refusal}";
                _approvalsLive = false;
                return;
            }
            foreach (var p in DeskProposals.ParseList(await list.Content.ReadAsStringAsync(ct)))
            {
                if (p.TargetId.Length > 0 && p.TargetId != SelfId) continue;
                if (!NodeRoutesOpen(c)) return;                                             // held: what was approved waits there, not taken
                if (!_ranDeskIds.Add(p.Id)) continue;
                // Written before it runs (13.12.9): a run that faults, or a link that ends as it runs, still leaves its result owed.
                Owe(to, p.Id, DeskProposals.Result(false, MayHaveRunWords));
                var (ok, words) = await RunAsync(p, deskName);
                Owe(to, p.Id, DeskProposals.Result(ok, words));
                if (!await PostResultAsync(c, http, baseUri, apiKey, deskName, p.Id, DeskProposals.Result(ok, words), ct)) return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            // the queue is asked again in two seconds; nothing was run twice (the ids ran are kept), a result owed stays owed
            Interlocked.Increment(ref _sideFaults);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException or UriFormatException)
        {
            // A reply this desk cannot read, or an apply that threw: said, and the lane goes on — the link's clock and
            // frames never stop because the queue misbehaved.
            Log.Warn("Approval desk: a step of the queue failed — asked again in two seconds.", ex);
        }
    }

    private void Owe(string to, string id, string body)
    {
        if (_owed.Owe(to, id, body, MonoMs()) is { } dropped)
            Log.Warn($"Approval desk: the result of {dropped} is let go unsent — more than {OwedReports.Most} results owed.");
    }

    /// <summary>
    /// One result to the approval desk, owed until an answer settles it (13.12.9): a 2xx delivers it, and so does a 409, which
    /// says another post of it settled it there. A hold — standing before it left, or started by its answer (401, 403, 429) —
    /// or an answer 13.12.3 reads as unconfirmed (408, a 5xx, none) keeps it owed for its next try, from 3 s doubling to 30 s,
    /// and stops the queue for this turn: false. Any other refusal settles it, said in the log.
    /// </summary>
    private async Task<bool> PostResultAsync(Connection c, HttpClient http, Uri baseUri, string apiKey, string deskName, string id, string body, CancellationToken ct)
    {
        var to = c.Target.PinKey;
        if (!NodeRoutesOpen(c)) return false;
        var asked = Asked(c.Run);
        int? status = null;
        var answerBody = "";
        long retryAfter = 0;
        try
        {
            using var result = Request(HttpMethod.Post, new Uri(baseUri, $"/api/proposals/{Uri.EscapeDataString(id)}/result"), apiKey, body);
            using var answer = await http.SendAsync(result, ct);
            status = (int)answer.StatusCode;
            answerBody = await answer.Content.ReadAsStringAsync(ct);
            retryAfter = RetryAfterOf(answer);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _sideFaults);                                        // no answer: unconfirmed, posted again
        }
        if (status is >= 200 and < 300 or 409)
        {
            _owed.Settle(to, id);
            return true;
        }
        var reading = PeerLink.ReadHttp(status, answerBody, (int)retryAfter);
        var said = status is { } code ? Answered(c, code, answerBody, retryAfter, asked, context: false) : reading.Words;
        if (status is 401 or 403 or 429 || reading.Outcome == LinkOutcome.Unconfirmed)
        {
            var first = _owed.TriesOf(to, id) == 0;
            _owed.Tried(to, id, MonoMs(), Jitter());
            if (first)
            {
                Log.Warn(reading.Outcome == LinkOutcome.Unconfirmed
                    ? $"Approval desk ({deskName}): the result of {id} is unconfirmed — {reading.Words}{(said != reading.Words && said.Length > 0 ? " (" + said + ")" : "")}; it is posted again from {OwedReports.RetryFirst.TotalSeconds:0} s, doubling to {OwedReports.RetryMost.TotalSeconds:0} s, for {OwedReports.RetryFor.TotalMinutes:0} minutes."
                    : $"Approval desk ({deskName}): the result of {id} was refused — {said}; it is posted again once this desk's key may post it.");
            }
            return false;
        }
        _owed.Settle(to, id);
        Log.Warn($"Approval desk ({deskName}): the result of {id} was refused — {said}");
        return true;
    }

    /// <summary>
    /// An approved proposal: one of the assistant's (applied by its id, and only as the proposal this desk made —
    /// <see cref="DeskProposals.ApplyRefusal"/>) or another app's wire line (run through the wire path).
    /// </summary>
    private async Task<(bool Ok, string Words)> RunAsync(DeskProposal p, string deskName)
    {
        if (DeskProposals.LocalId(p.Line) is { } local)
        {
            var (e, refusal) = Approvals.ForApply(p, local, SelfId);
            if (refusal is not null || e is null)
            {
                var words = refusal ?? DeskProposals.NoLongerHeld;
                Log.Warn($"Approval desk ({deskName}): approved {p.Id} names this desk's proposal {local} — refused: {words}");
                return (false, words);
            }
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
    private bool KeyedCallsOpen(Connection c) => c.Keyed && c.Heard && HeldFor(c.Run) == TimeSpan.Zero;

    /// <summary>
    /// The node routes may go — the context this desk posts, its proposals, the collect and the results: as
    /// <see cref="KeyedCallsOpen"/>, and no hold of a key that speaks for no node of this desk's stands.
    /// </summary>
    private bool NodeRoutesOpen(Connection c) => c.Keyed && c.Heard && NodeHeldFor(c.Run) == TimeSpan.Zero;

    /// <summary>The most of a side call's answer read: well above a context's 64 KiB, so no far end fills this desk's memory.</summary>
    private const long SideAnswerMost = 1024 * 1024;

    /// <summary>Where the run's words stand as a call leaves: its answer applies only while no verdict or refusal came after.</summary>
    private static long Asked(Run run)
    {
        lock (run.Gate) return run.Said;
    }

    /// <summary>
    /// The answer to one of this desk's keyed calls (13.7.3's one door for refusals). A 401 that asks for a relink ends the link
    /// with no hold, its next verdict deciding the key. Any other is evidence of the key (<see cref="StagewatchKey.OfAnswer"/>):
    /// a 401 refuses it, a 403 holds its node routes and a 429 holds it, in the Stagewatch's words; a 2xx to this desk's context
    /// (<paramref name="context"/>) says its standing. Returns the Stagewatch's words for the answer, the key masked.
    /// </summary>
    private string Answered(Connection c, int status, string body, long retryAfter, long asked, bool context)
    {
        var error = "";
        var verified = "";
        var relink = false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body.Length is > 0 and <= 64 * 1024 ? body : "{}");
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String) error = StagewatchFrame.Words(e.GetString(), c.Secret);
                if (doc.RootElement.TryGetProperty("verified", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String) verified = v.GetString() ?? "";
                relink = doc.RootElement.TryGetProperty("relink", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // a body that is no JSON object says nothing more than its status
        }
        var words = error.Length > 0 ? error : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"HTTP {status}");
        if (status == 401 && relink)
        {
            c.Ending = $"{c.Target.Name} asked for a new link — {words}; dialling again.";
            lock (_core) c.Core.Machine.SideCall(Now(), new LinkReading(LinkOutcome.Refused, words, Relink: true));
            return words;
        }
        if (c.Keyed && StagewatchKey.OfAnswer(status, error, verified, retryAfter, context, c.Legacy, RefusedHoldAtLeast) is { } step) Apply(c, step, asked);
        return words;
    }

    /// <summary>
    /// One word on the key applied to the run, under its gate: the hold first — recorded as the word is read, before any
    /// further I/O — then the standing, and the approval desk held with it. A verdict (<paramref name="asked"/> −1) is the
    /// latest word; an answer applies only while no verdict or refusal came after its call left. A word that takes the key
    /// lifts every hold of a refusal (never a rate limit's); one that says the key reads but speaks for no node of this
    /// desk's lifts a refused key's hold and holds the node routes. A refused key's hold and a rate limit's are the core's
    /// too, and a refused key's is kept beside the settings by target and kid before anything else is done (13.15.3); a hold
    /// whose refusal let no keyless link read either (<paramref name="publicView"/> false) lets nothing be dialled inside it.
    /// On a connection dialled for the public view inside a hold, nothing heard moves the key's standing. One log line when
    /// the standing moves.
    /// </summary>
    private void Apply(Connection c, StagewatchKeyStep step, long asked, bool publicView = true)
    {
        var run = c.Run;
        var name = c.Target.Name;
        StagewatchKey before;
        StagewatchKey after;
        long holdMs = 0;
        lock (run.Gate)
        {
            if (asked >= 0 && asked != run.Said) return;
            if (asked < 0 || step.Hold is StagewatchHoldKind.Refused or StagewatchHoldKind.Node) run.Said++;
            var now = MonoMs();
            if (step.Takes && !c.Public) (run.HeldUntilMs, run.NodeHeldUntilMs) = (0, 0);
            if (step.Hold == StagewatchHoldKind.Node) run.HeldUntilMs = 0;
            var until = now + (long)step.For.TotalMilliseconds;
            if (step.Hold == StagewatchHoldKind.Refused && until > run.HeldUntilMs) run.HeldUntilMs = until;
            if (step.Hold == StagewatchHoldKind.Busy && until > run.BusyUntilMs) run.BusyUntilMs = until;
            if (step.Hold == StagewatchHoldKind.Node && until > run.NodeHeldUntilMs) run.NodeHeldUntilMs = until;
            if (step.Hold is StagewatchHoldKind.Refused or StagewatchHoldKind.Busy) holdMs = (long)step.For.TotalMilliseconds;
            before = run.Key;
            after = step.Key is { } k && !c.Public ? k with { Legacy = c.Legacy && c.Keyed } : before;
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
        if (holdMs > 0)
        {
            // The core holds the dial too, and the store keeps a key's hold for a restart, before the caller does anything more.
            var word = step.Hold == StagewatchHoldKind.Busy ? "busy" : "key";
            long wall;
            lock (_core)
            {
                var now = Now();
                wall = now.Wall;
                c.Core.Machine.KeyRefused(now, word, holdMs, publicView);
            }
            if (c.Keyed && run.Kid is { } kid) Holds.Hold(c.Core.Target, kid, wall + holdMs, word, wall, publicView);
        }
        else if (step.Takes && c.Keyed)
        {
            string[] targets;
            long wall;
            lock (_core)
            {
                var now = Now();
                wall = now.Wall;
                c.Core.Machine.KeyTaken(now);
                // 13.15.2 at level 0: the key speaks for this desk — the verdict says so, or an older Stagewatch's answer took it.
                if (!c.Public && step.Hold == StagewatchHoldKind.None && (after.Word is "key" or "operator" || after.Legacy)) c.Core.Machine.Settled(now);
                targets = run.Targets.ToArray();
            }
            // The run's refusal is over under every name it kept it by; a rate limit's goes on to its own end.
            if (run.Kid is { } kid) foreach (var t in targets) Holds.Taken(t, kid, wall);
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
    }

    /// <summary>
    /// A verdict on the socket (Stagewatch 2.18 §7.1), read as it arrives: the first message a node link hears, and again
    /// whenever the key, its binding or the PIN changes there, latest wins. The viewing code's refusal, after which the
    /// Stagewatch closes the socket, holds the redial and says why; it lets no keyless socket read either, so nothing is
    /// dialled inside its hold.
    /// </summary>
    private void Verdict(Connection c, string text)
    {
        if (StagewatchFrame.Auth(text, c.Secret) is not { } auth) return;
        if (StagewatchKey.OfVerdict(auth, SelfId, c.Keyed, RefusedHoldAtLeast) is not { } step) return;
        Apply(c, step, -1, publicView: !auth.View);
        if (!auth.View) return;
        var why = step.Words.Length > 0 ? step.Words : auth.Error.Length > 0 ? auth.Error : "its viewing code";
        Volatile.Write(ref c.Run.Redial, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{c.Target.Name} closed the link — {why}; dialling again in {Math.Ceiling(step.For.TotalSeconds):0} s."));
    }

    /// <summary>
    /// The first message on a connection. Anything but a verdict says the Stagewatch is older than its node links, and so does,
    /// on a link that carries the key, a viewing code's refusal that says nothing of the key
    /// (<see cref="StagewatchKey.OlderFirst"/>): a link that carries the key then reads the key's standing from the answers to
    /// its keyed calls (P-legacy's labelled fallback) — from the standing that evidence left on a connection before, or from
    /// nothing said yet. A connection dialled for the public view inside a hold moves no standing.
    /// </summary>
    private void First(Connection c, string type, string text)
    {
        c.Legacy = StagewatchKey.OlderFirst(type, type == "auth" ? StagewatchFrame.Auth(text, c.Secret) : null, c.Keyed);
        if (!c.Legacy || c.Public) return;
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
    /// Show Bus 1.3 §13.8.2, once a link has its first frame: a certificate the core pinned <c>first</c> — on a link that reads
    /// the public view, compared and never trusted — is kept in the settings, and when the key was held back from it the Trust
    /// press is offered beside its short fingerprint. A first pin changes no dial, so keeping it starts nothing again.
    /// </summary>
    private void FirstPin(Connection c)
    {
        if (!c.Tls || !ReferenceEquals(c.Run, _run)) return;                               // a run let go (a Trust, a pin set) says nothing more
        LinkPin? pin;
        lock (_core) pin = c.Core.Machine.Pin;
        if (pin is not { Trust: PinTrust.First }) return;
        var fp = TlsFingerprint.Prefix + pin.Fingerprint;
        var key = c.Target.PinKey;
        if (c.Core.FirstSaved != fp)
        {
            c.Core.FirstSaved = fp;
            UiThread.Post(() => _config().PinFirst(key, fp));                               // a person's Trust made meanwhile stands
            Log.Info($"Stagewatch link: {c.Target.Name}'s certificate {TlsFingerprint.Short(fp)} pinned at first use — compared, never trusted.");
        }
        lock (_trustGate)
        {
            // Offered only while this connection's run is the link's: a run let go by a press or a pin set offers nothing again.
            if (!c.Withheld || !ReferenceEquals(c.Run, _run) || _trust?.Fingerprint == fp) return;
            _trust = new TrustOffer(fp, key, c.Target.Name);
        }
        Log.Info($"Stagewatch link: {c.Target.Name} — {StagewatchKey.TrustWords(TlsFingerprint.Short(fp))}.");
        UiThread.Post(_changed);
    }

    /// <summary>A verdict's or a frame's <c>inst</c> (§2), "" when it names none: another process than the clock measured empties its window (13.11.7).</summary>
    private static string InstOf(string text)
    {
        if (text.Length > 16 * 1024 || !text.Contains("\"inst\"", StringComparison.Ordinal)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && PeerLink.Text(doc.RootElement, "inst") is { Length: > 0 and <= 8 } inst ? inst : "";
        }
        catch (System.Text.Json.JsonException)
        {
            return "";
        }
    }

    /// <summary>
    /// The link's one reader: frames, pongs, beats and verdicts, each told to the core as it lands. It alone writes its show's
    /// stuck mark — set by the stuck beat, cleared by the next state frame only once that frame is held; <see cref="Sight"/>
    /// reads the mark before the frame, so a read that finds the mark cleared finds the new frame, never the old one counting
    /// again. The Stagewatch's verdict on this desk's key is read before anything keyed is sent. A message longer than the link
    /// reads whole is counted and said, skipped to its end and never taken, and the link stays up (13.14).
    /// </summary>
    private async Task ReceiveAsync(Connection c, HeardShow show, ClientWebSocket ws, string name, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new System.Buffers.ArrayBufferWriter<byte>(16 * 1024);
        var skipping = false;
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return;
            if (!skipping)
            {
                System.Buffers.BuffersExtensions.Write(message, buffer.AsSpan(0, r.Count));
                if (message.WrittenCount > ReadWholeMost)
                {
                    skipping = true;
                    message.ResetWrittenCount();
                    lock (_core) c.Core.Machine.Oversize(Now());
                    if (!c.OversizeSaid)
                    {
                        c.OversizeSaid = true;
                        Log.Warn(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Stagewatch link: {name} sent a message past {ReadWholeMost / 1024} KiB — counted and skipped, never taken; the link stays up."));
                    }
                }
            }
            if (!r.EndOfMessage) continue;
            Volatile.Write(ref _lastWordMs, MonoMs());
            if (skipping)
            {
                skipping = false;
                lock (_core) c.Core.Machine.Heard(Now());
                continue;
            }
            var text = Encoding.UTF8.GetString(message.WrittenSpan);
            message.ResetWrittenCount();
            var arrived = ShowClock.Seconds;
            var type = StagewatchFrame.TypeOf(text);
            var frame = type is "state" or "" ? StagewatchFrame.Parse(text) : null;
            // A state message is a state frame whether or not it carries a time: the show turns over (13.10.5); only one that
            // carries a time is held for the stage.
            var stateFrame = frame is not null || type == "state";
            var stuckBeat = type == "beat" && StagewatchFrame.IsStuckBeat(text);
            var pong = type == "pong" ? StagewatchFrame.Pong(text) : null;
            var restartedAt = 0L;
            lock (_core)
            {
                var now = Now();
                var m = c.Core.Machine;
                if (!c.Heard) m.FirstMessage(now, frame: false);                         // 13.6.8: its first message is no link — level 0
                if (stateFrame)
                {
                    m.Frame(now);
                    // 13.15.2 at level 0: a connection that carries no key — none set, held back for Trust or for no TLS, or a public
                    // view inside a refused key's hold — asks nothing the Stagewatch must take, so its first frame settles it.
                    if (!c.Keyed) m.Settled(now);
                }
                else if (type == "beat") m.Beat(now, live: !stuckBeat);
                else if (pong is { } p) m.Pong(now, p.T1, p.T2, p.T3, stuck: false);
                else m.Heard(now);
                if (type is "auth" or "state" or "" && InstOf(text) is { Length: > 0 } inst) m.PeerInst(now, inst);
                // 13.13.2: a connection's first frame counting from below the last one held is another run of the Stagewatch —
                // the clock it measured is another process's (13.11.7), as another inst would say.
                if (frame is { Seq: > 0 } counted)
                {
                    if (!c.SeqSeen && counted.Seq < c.Core.LastSeq)
                    {
                        m.PeerRestarted(now);
                        restartedAt = counted.Seq;
                    }
                    c.SeqSeen = true;
                    c.Core.LastSeq = counted.Seq;
                }
                m.Drain();                                                                  // the bearer the core asks for rides the upgrade: nothing more to write
            }
            if (restartedAt > 0)
                Log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Stagewatch link: {name} counts its frames from {restartedAt} again — another run of it; its clock is measured afresh."));
            if (!c.Heard) First(c, type, text);
            switch (type)
            {
                case "auth":
                    Verdict(c, text);                                                       // its hold recorded before the lane may send anything keyed
                    break;
                case "beat":
                    // A plain beat says the link is alive and nothing of the show; the stuck beat says the show is stuck.
                    if (stuckBeat)
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
                    if (frame is not null)
                    {
                        Interlocked.Increment(ref _frames);
                        _sight = _sight with { Frame = frame, FrameAtSeconds = arrived, State = ShowBusState.Live };
                        bool apart;
                        lock (_core) apart = c.Core.Machine.Clock.IsApart;
                        var offset = ClockOffset;
                        _status = $"Stagewatch link: following {name}" + (apart && offset is { } o ? " · " + LinkClock.ApartWords("Stagewatch", o) : "") + ".";
                        if (show.Stuck)
                        {
                            show.Stuck = false;                                                 // after the new frame is held, never before
                            Log.Info($"Stagewatch link: {name}'s show runs again.");
                        }
                        UiThread.Post(_changed);
                        FirstPin(c);
                    }
                    else if (stateFrame)
                    {
                        // A state frame that carries no time: the show turns over, and no time is held from it — the frame held
                        // before is let go first, so a stall cleared by it never shows an old time counting.
                        _sight = _sight with { Frame = null, FrameAtSeconds = arrived, State = ShowBusState.Live };
                        if (show.Stuck)
                        {
                            show.Stuck = false;
                            Log.Info($"Stagewatch link: {name}'s show runs again.");
                        }
                        UiThread.Post(_changed);
                        FirstPin(c);
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
