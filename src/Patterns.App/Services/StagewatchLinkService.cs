using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
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
/// <para>
/// Round 96 — Show Bus 1.3 §13.6 at level 1, Stagewatch 2.22's acceptor: a run that holds a key the binding lets go dials
/// <c>/ws?…&amp;link=1</c> with no Authorization header (<see cref="StagewatchFrame.LinkQuery"/>) — over TLS to whatever certificate it
/// shows, or plain to loopback — and proves the key once, to the id meant and over the certificate shown; the key never leaves this
/// desk. Nothing of the socket is taken, and no side call or ping made, before the acceptor's proof verifies with K over T; then the
/// certificate is pinned by that proof (13.8.2), the floor {tls, level, proof} kept by name and id with the settings (13.9.3), and the
/// side calls carry the link's S, only to that pin or loopback (13.7.1). A later link that offers less is refused red until a person's
/// FLOOR LOWER. A Stagewatch older than LINK answers with no offer: with no floor, it is dialled from then on, for the rest of the run,
/// as level 0 below — the bearer on a fresh upgrade where 13.6.8 lets it go — the labelled amber fallback P-legacy keeps.
/// </para>
/// <para>
/// The speaker's calls (13.7.2, 13.7.4): a message this desk sends to the speaker through the Stagewatch its stage follows, the
/// clear of one it posted and a display's ACK forwarded — a kind of side call of their own, one in flight, a newer message
/// replacing one not yet sent, woken at once and never waiting a beat or behind the context's read. Each carries a 13.12.1 id
/// in <c>X-Request-Id</c> and ends in one of 13.12.3's five outcomes, said once to <see cref="SpeakerAnswered"/>; none is sent
/// again (13.12.7). They go only over a connection that carried the key, after its first word, while no hold stands and the
/// Stagewatch's show is not stuck; what was never written when the link drops is not-sent, and says why. A clear asked behind
/// a message still on its way is written only if that message reached the Stagewatch, and the queue is one connection's: the
/// next connection sends none of it late.
/// </para>
/// </summary>
public sealed class StagewatchLinkService : IDisposable, ISpeakerCarrier
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

        /// <summary>Its context with the revision it was kept with (one writer, the side-call lane; one reference write publishes both).</summary>
        public volatile KeptContext? Context;
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

        /// <summary>
        /// Round 96 (13.6.8, P-legacy): a LINK socket to this name found a Stagewatch that does not speak LINK, and no floor refused it.
        /// From then this core dials it as one — the bearer only to a certificate a person or a proof pinned, or to loopback — so an
        /// older Stagewatch costs one LINK dial a run, not one a connection; one upgraded to LINK during the run is followed at level 0,
        /// said amber as <c>legacy</c>, until the next run, which asks for LINK again. A floor's refusal sets nothing: the dial after a
        /// person's FLOOR LOWER asks for LINK first.
        /// </summary>
        public volatile bool Level0;
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

        /// <summary>
        /// The machine the link's record comes from (under the core gate): <see cref="Core"/>'s while this run dials a Stagewatch, else
        /// <see cref="Idle"/>'s — off, finding one, or held off an id two running nodes say.
        /// </summary>
        public PeerLinkMachine? Current;

        /// <summary>The machine of a run with no Stagewatch to dial (under the core gate): its record says off, finding or a conflict's hold.</summary>
        public PeerLinkMachine? Idle;

        /// <summary>
        /// The run was turned on with a typed address that is not one: it dials nothing and listens for nothing, so its record is off
        /// and says why — never "finding", which would claim a listening the run does not do.
        /// </summary>
        public bool NotAnAddress;

        /// <summary>Every name this run has dialled its Stagewatch by (under the core gate): a refusal's hold kept for one is lifted for each when a word takes the key.</summary>
        public readonly HashSet<string> Targets = new(StringComparer.Ordinal);

        /// <summary>
        /// Show Bus 1.3 §13.9.3: the floors this desk keeps, by <c>node:</c> and <c>addr:</c> key (under the core gate) — the settings'
        /// at the run's start, and each one a link of this run verified, so a core made later in the run starts from it.
        /// </summary>
        public readonly Dictionary<string, KeptFloor> Floors = new(StringComparer.Ordinal);

        /// <summary>The connection up now, for the evaluator; null between connections.</summary>
        public volatile Connection? Live;

        /// <summary>The kid of the key this run holds (13.5.1) — the name its holds are kept by, never the key or a hash of it; null with no key.</summary>
        public string? Kid;

        /// <summary>
        /// G34: where this run's key may go (<see cref="StagewatchLinkConfig.KeyFor"/>) — read at every dial, so a binding the first
        /// 2xx makes holds for this run's next Stagewatch too, which then gets no key that is bound to another.
        /// </summary>
        public volatile string KeyFor = "";

        /// <summary>
        /// The link is configured to be followed: the show needs it (13.1), and only then may its light be red. Written under the service's
        /// core gate, with every machine of the run told (<see cref="PeerLinkMachine.ShowNeedsLink"/>), so a FOLLOW moves the record's light
        /// at once without a new run.
        /// </summary>
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
        /// The Stagewatch's words when it refused this key a message to its speaker (a 403: a key that may read but not send);
        /// "" before one. While it stands a speaker's message stays on this desk; a verdict that takes the key forgets it.
        /// </summary>
        public volatile string SpeakerRefused = "";

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
                SpeakerRefused = from.SpeakerRefused;
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
        ClientWebSocket socket, SemaphoreSlim sendGate, bool link, CertGate certs)
    {
        private volatile bool _keyed = keyed;
        private volatile bool _withheld = withheld;

        public Run Run { get; } = run;
        public LinkCore Core { get; } = core;
        public Target Target { get; } = target;
        public bool Tls { get; } = tls;

        /// <summary>
        /// The key speaks on this connection: it rode the upgrade as a bearer (level 0), or LINK verified both proofs (13.6.3) — set by
        /// the receive loop at the verdict, and unset by a refusal on a live link, which ends its S.
        /// </summary>
        public bool Keyed { get => _keyed; set => _keyed = value; }

        public bool Public { get; } = isPublic;

        /// <summary>Over TLS to a certificate no person pinned, the key held back for Trust (13.6.8): at the dial, or once a LINK socket found level 0.</summary>
        public bool Withheld { get => _withheld; set => _withheld = value; }

        public string Secret { get; } = secret;

        /// <summary>
        /// Round 96: dialled with <c>link=</c> and no Authorization (13.6.5): its first message is LINK's offer, the key is proved and
        /// never sent, and nothing of it is taken, nor any side call made, before the acceptor's proof verifies (<see cref="Heard"/>).
        /// </summary>
        public bool Link { get; } = link;

        /// <summary>The certificate the connection's every socket must show once a proof pinned it (13.7.1: S goes only to that pin).</summary>
        public CertGate Certs { get; } = certs;

        /// <summary>The key was put to the Stagewatch on this connection — a bearer, or a proof — so its refusal is the key's, kept for a restart.</summary>
        public volatile bool Proved;

        /// <summary>This LINK's transcript and K (the receive loop's alone): what the acceptor's proof and the session credential are checked and made from.</summary>
        public string? Transcript;

        public byte[]? K;

        /// <summary>13.7.1's S on a verified link with its lid; null before the verdict, and from a refusal or the link's end on.</summary>
        public volatile LinkSession? Session;
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

    /// <summary>13.9.3: a floor this run keeps, with the node id the link that recorded it verified (empty when it named none).</summary>
    private sealed record KeptFloor(LinkFloor Floor, string Node);

    /// <summary>13.7.1: a verified link's lid and its session credential S — a secret: it rides only a side call's Authorization header.</summary>
    private sealed record LinkSession(string Lid, string S)
    {
        public override string ToString() => "LinkSession { Lid = " + Lid + " }";
    }

    /// <summary>
    /// The certificate every socket of one connection must show once LINK verified over it (13.7.1, 13.8.2): the upgrade's own TLS
    /// checked the dial, and from the proof on a side call's socket shown any other gets nothing — no S goes to it.
    /// </summary>
    private sealed class CertGate
    {
        private volatile string _pinned = "";

        public void Pin(string fingerprint) => _pinned = fingerprint;

        public bool Admits(string presented) => _pinned.Length == 0 || string.Equals(_pinned, presented, StringComparison.Ordinal);
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
            var kept = show?.Context;
            var s = _sight;
            if (s.Name.Length == 0 && s.Address.Length == 0) return s;
            s = s with { Context = kept?.Context, ContextRev = kept?.Rev ?? 0, Key = KeyNow() };
            if (stuck) s = s with { Frame = s.Frame is { } f ? f with { Live = false } : null, Stuck = true };
            // The state is the silence since the last word, read now: a link that stopped talking goes stale on its own.
            var silent = TimeSpan.FromSeconds(Math.Max(0, ShowClock.Seconds - s.FrameAtSeconds));
            var lastWord = Volatile.Read(ref _lastWordMs);
            var state = lastWord == long.MinValue ? ShowBusState.Gone : ShowBusLiveness.StateOf(TimeSpan.FromMilliseconds(Math.Max(0, MonoMs() - lastWord)));
            return s with { State = state, Words = StatusWords(state, s, silent) };
        }
    }

    /// <summary>
    /// <see cref="Sight"/> with the link's record (Show Bus 1.3 §13.16.3) — what the readers that draw the link take: the Eye, the Nodes
    /// card, the context's peers, the assistant's facts, STATE. The stage's own readers take <see cref="Sight"/>, which reads no record.
    /// </summary>
    public StagewatchSight SightWithLink => Sight with { Link = Record };

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
            var extra = string.Join(" · ", new[] { _note, _reached, KeyNow().Line, BindWords }.Where(w => w.Length > 0));
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

    /// <summary>
    /// Show Bus 1.3 §13.16.3: the link's record now — the one object every reader takes its link facts from (13.16.4): the core's for
    /// the Stagewatch this run dials, else the run's own while it is off, finding one or held off an id in conflict.
    /// </summary>
    public LinkRecord Record
    {
        get
        {
            var run = _run;
            lock (_core)
            {
                var now = Now();
                var record = (run.Current ?? run.Idle)?.Record(now) ?? PeerLinkMachine.Unlinked(IdleSettings(false, null, null, false), now);
                return run.Current is null && run.NotAnAddress ? record with { Words = NotAnAddressWords } : record;
            }
        }
    }

    /// <summary>
    /// The record's words for a link turned on with a typed address that is not one — off, since it neither dials nor listens; the typed
    /// text itself is not repeated, so nothing pasted into the address by mistake rides into STATE, the context or the journal.
    /// </summary>
    public const string NotAnAddressWords = LinkRecord.OffWords + " — the address typed is not one: host:port, or leave it empty to find a Stagewatch on the show network";

    /// <summary>The name a link's record gives the Stagewatch it has none to dial yet.</summary>
    public const string IdleName = StageFollow.Unnamed;

    /// <summary>What a run with no Stagewatch to dial yet is set to: on or off, the address typed, the id held off, whether the show needs it.</summary>
    private static LinkSettings IdleSettings(bool on, string? address, string? nodeId, bool showNeeds) => new()
    {
        Carrier = LinkCarrier.Ws,
        Enabled = on,
        Name = IdleName,
        Kind = "main",
        NodeId = nodeId,
        Address = address,
        ShowNeeds = showNeeds,
        Uses = "post",
    };

    /// <summary>Where the link's record moves are journalled (13.16.7); the kernel hands the desk's journal, a test may leave none.</summary>
    public ShowLog? Journal { get; set; }

    private readonly object _saidGate = new();
    private readonly LinkChangeLog _said = new();

    /// <summary>
    /// Show Bus 1.3 §13.16.7 and §13.19.4: the record read now, and one journal row and one log line for each change of its state —
    /// from, to, the reason and the attempt, with the peer's kind and id and the address — or a repeat's summary; never per beat.
    /// Called wherever the state may move: a dial, a first word, a verdict, a close, a run, and each turn of the evaluator, which sees
    /// a link go quiet. Read and said under one gate, so two callers never say one change twice or out of order.
    /// </summary>
    private void SayChange()
    {
        lock (_saidGate)
        {
            var record = Record;
            foreach (var line in _said.Take(record, MonoMs()))
            {
                var peer = record.PeerId is { Length: > 0 } id ? $"{record.PeerT ?? "main"} {id}" : record.PeerName;
                var address = _sight.Address is { Length: > 0 } at ? " (" + at + ")" : "";
                var words = $"Stagewatch link to {peer}{address}: {line.Words}";
                if (line.Warn) Log.Warn(words);
                else Log.Info(words);
                var label = ShowContextPeerLink.Stagewatch(record)?.Label ?? "Stagewatch link";
                Journal?.Record("Show network", "StagewatchLink", record.PeerId is { Length: > 0 } pid ? $"{label} ({pid})" : label,
                    line.Summary ? "repeating" : record.State, line.Words);
            }
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
    /// ACCEPT on the Nodes page (UI thread) — Show Bus 1.3 §13.8.3: at least 8 hex of the changed certificate, typed by a person who
    /// compared it with the Stagewatch's own screen (<see cref="StagewatchVerbs.AcceptRefusal"/>); then it becomes the pin, a person's,
    /// and the link dials again. Hex that is not the waiting certificate's own beginning — a press made for one that has changed again
    /// — accepts nothing. Returns whether it was accepted, and the words for the strip.
    /// </summary>
    public (bool Accepted, string Words) AcceptCertificate(string typedHex)
    {
        var fp = _pendingFingerprint;
        var key = _pendingPinKey;
        if (fp.Length == 0 || key.Length == 0) return (false, "No changed certificate is waiting.");
        if (StagewatchVerbs.AcceptRefusal(typedHex, fp, PendingName) is { } refusal) return (false, refusal);
        _config().SetPin(key, fp, PinTrust.Person);
        _pendingFingerprint = "";
        _pendingPinKey = "";
        _runningKey = "";                                                                   // dial again with the new pin
        Reconcile();
        Log.Info($"Stagewatch link: the changed certificate {TlsFingerprint.Short(fp)} of {key} accepted by a person on this desk, its hex typed.");
        return (true, $"Accepted the Stagewatch's new certificate {TlsFingerprint.Short(fp)} — the link dials again.");
    }

    /// <summary>The name of the Stagewatch whose certificate changed, as its words say it ("the Stagewatch" while none is named).</summary>
    public string PendingName => _sight.Name is { Length: > 0 } n ? n : "the Stagewatch";

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

    /// <summary>
    /// G34: the key held back from the Stagewatch the link dials because it is bound to another — that Stagewatch's name (its pin key)
    /// and the binding it stands under. Null while the key goes, or no key is set.
    /// </summary>
    private sealed record BindOffer(string Target, string Name, string KeyFor);

    private BindOffer? _bindOffer;

    /// <summary>G34: the target (<c>node:&lt;id&gt;</c> or <c>addr:…</c>) the key is held back from by its binding; "" when none.</summary>
    public string KeyBindTarget => Volatile.Read(ref _bindOffer)?.Target ?? "";

    /// <summary>The name of the Stagewatch the key is held back from by its binding; "" when none.</summary>
    public string KeyBindName => Volatile.Read(ref _bindOffer)?.Name ?? "";

    /// <summary>The record's words for a key its binding holds back from the Stagewatch the link dials: where it is bound, and the press that moves it.</summary>
    public static string HeldBackWords(string keyFor)
        => (keyFor == StagewatchKeyFor.None ? "this desk's key is bound to no Stagewatch" : $"this desk's key is bound to {StagewatchKeyFor.Words(keyFor)}")
           + " and is not sent here; BIND KEY on the Nodes page sends it here";

    /// <summary>"the key is bound to Stagewatch a1b2c3d4 — not sent to Hall B; BIND KEY …" while the binding holds it back; "" otherwise.</summary>
    public string BindWords => Volatile.Read(ref _bindOffer) is { } o
        ? $"this desk's key is bound to {StagewatchKeyFor.Words(o.KeyFor)} — not sent to {o.Name}, whose public view is read; type its own key on the Nodes page, or BIND KEY to send this one"
        : "";

    /// <summary>What the link runs under: its address and key, where the key may go, and the pins a person or a proof made.</summary>
    private static string RunningKey(StagewatchLinkConfig c, string hostKey) => c.Enabled ? $"{hostKey}|{c.KeyFor}|{c.PinsKey}" : "";

    /// <summary>
    /// G34: an unbound key binds to the Stagewatch whose first 2xx took it — a key-shaped key (<c>swk_…</c>) only: Stagewatch looks a
    /// key up and refuses an unknown one, while any other value is a PIN, which a Stagewatch with none set lets through and so proves
    /// nothing. From then on the key goes to that Stagewatch and no other; the running link goes on as it is.
    /// </summary>
    private void BindAtFirstAnswer(Connection c, string apiKey)
    {
        if (c.Run.KeyFor.Length > 0 || !apiKey.StartsWith(StagewatchFrame.KeyPrefix, StringComparison.Ordinal)) return;
        UiThread.Post(() =>
        {
            var cfg = _config();
            var run = c.Run;
            if (!ReferenceEquals(run, _run) || run.KeyFor.Length > 0 || cfg.KeyFor.Length > 0) return;
            var bind = c.Target.PinKey;
            var before = RunningKey(cfg, _runningHostKey);
            cfg.KeyFor = bind;
            run.KeyFor = bind;
            if (string.Equals(_runningKey, before, StringComparison.Ordinal)) _runningKey = RunningKey(cfg, _runningHostKey);   // the link runs on: the key goes where it went
            Log.Info($"Stagewatch link: this desk's key is bound to {c.Target.Name} ({bind}) — its first answer took it; it goes to no other Stagewatch from now.");
            _changed();
        });
    }

    /// <summary>
    /// BIND KEY on the Nodes page (UI thread) — a person's: the key goes to the Stagewatch the press was shown beside
    /// (<paramref name="target"/>, its pin key) from the next dial, made at once. Only while that is the one it is held back from, so a
    /// press drawn for one Stagewatch never binds another. Returns whether it bound, and the words for the strip.
    /// </summary>
    public (bool Bound, string Words) BindKey(string target)
    {
        var c = _config();
        if (c.ApiKey.Length == 0) return (false, "No Stagewatch key is set — type one on the Nodes page first.");
        if (Volatile.Read(ref _bindOffer) is not { } offer) return (false, "The key is held back from no Stagewatch now — nothing to bind.");
        if (!string.Equals(offer.Target, (target ?? "").Trim(), StringComparison.Ordinal))
            return (false, $"The key is held back from {offer.Name} now, not the Stagewatch pressed for — look again.");
        c.KeyFor = offer.Target;
        _runningKey = "";                                                                   // dial again with the key
        Reconcile();
        Log.Info($"Stagewatch link: this desk's key bound to {offer.Name} ({offer.Target}) by a person on this desk — it goes there from now.");
        return (true, $"The Stagewatch key is bound to {offer.Name} — it goes there from the next dial, now.");
    }

    /// <summary>
    /// Show Bus 1.3 §13.16.6's <i>Link again now</i> (UI thread): every hold this desk keeps for the key ends — a refusal's, a rate limit's
    /// and the node routes', in this run and on disk under any target — and the redial's wait with them, so the next dial is due now
    /// (13.15.3). The run and its machines go on: a live link stays up, and the redial count stays where it is, which only a link
    /// verified and stable returns to 0 (13.15.2). Refused, with the reason, while the link is off, while a changed certificate waits for a
    /// person, and while nothing is held and no redial waits — a press then would change nothing.
    /// </summary>
    public (bool Done, string Words) LinkAgain()
    {
        var c = _config();
        if (!c.Enabled) return (false, "The Stagewatch link is off — STAGEWATCH LINK ON (or the Nodes page) first.");
        if (_pendingFingerprint.Length > 0)
            return (false, $"The Stagewatch's certificate changed to {TlsFingerprint.Short(_pendingFingerprint)} — nothing is dialled until a person accepts it on the desk (ACCEPT, at least 8 hex typed).");
        var run = _run;
        var mono = MonoMs();
        var wall = WallMs();
        bool runHeld;
        lock (run.Gate) runHeld = run.HeldUntilMs > mono || run.BusyUntilMs > mono || run.NodeHeldUntilMs > mono;
        var kept = run.Kid is { } k && Holds.Holding(k, wall);
        var record = Record;
        var machineHeld = record.State == "held" && record.Refusal != "conflict";
        if (!runHeld && !kept && !machineHeld && record.State != "down") return (false, NothingToDialWords(record));
        lock (run.Gate)
        {
            run.HeldUntilMs = run.BusyUntilMs = run.NodeHeldUntilMs = 0;
            if (run.Key.Refused) run.Key = StagewatchKey.Unknown;
        }
        lock (_core) run.Core?.Machine.LinkAgainNow(Now());
        var lifted = run.Kid is { } kid ? Holds.Lift(kid, wall) : 0;
        SayChange();
        UiThread.Post(_changed);
        var held = runHeld || kept || machineHeld;
        Log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Stagewatch link: link again now, pressed — {(held ? $"every hold of this desk's key ended, {lifted} of them lifted from the store kept for a restart" : "the redial's wait ended")}; the next dial is due now, attempt {record.Attempt + 1}."));
        return (true, held
            ? "Stagewatch link: every hold of this desk's key ended — the next dial is due now and may present the key again; the link's record says when it goes."
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Stagewatch link: the redial's wait ended — the next dial is due now (attempt {record.Attempt + 1}; the count goes on until a link stays up)."));
    }

    /// <summary>Why LINK AGAIN would change nothing now, by the record's state.</summary>
    private static string NothingToDialWords(LinkRecord record) => record.State switch
    {
        "live" or "stale" => "The Stagewatch link is up and nothing of this desk's key is held — nothing to dial again.",
        "connecting" => "The Stagewatch link is dialling now and nothing is held — nothing to dial again.",
        "finding" => "No Stagewatch is heard on the show network yet — nothing to dial again; the link dials one as soon as it is heard.",
        "held" => $"{record.Words} — LINK AGAIN does not lift it: the link dials again once only one machine says that id.",
        "refused" => $"The Stagewatch link waits for a person on the desk: {record.Words}.",
        _ => "Nothing is held and no redial waits — nothing to dial again.",
    };

    /// <summary>A Stagewatch heard live on the show network by its id, at an address it can be dialled at; null when none is.</summary>
    public ShowBusPeer? HeardStagewatch(string id)
    {
        var now = DateTime.UtcNow;
        return _peers(now).FirstOrDefault(p => p.Kind == "main" && p.Id == id && p.StateAt(now) == ShowBusState.Live && p.Address.Length > 0 && (p.Ports.Ws > 0 || p.Ports.Http > 0));
    }

    /// <summary>A floor a verified link recorded for the Stagewatch the link dials (13.9.3); never at level 0, where nothing verifies.</summary>
    public bool HasFloor
    {
        get
        {
            lock (_core) return _run.Core?.Machine.Record(Now()).Floor is not null;
        }
    }

    /// <summary>
    /// Show Bus 1.3 §13.9.3's <i>Lower floor</i> (UI thread), one press: the floor is gone and a floor's refusal with it. Refused, with
    /// the reason, while none is recorded — a floor comes only from a verified link.
    /// </summary>
    public (bool Done, string Words) LowerFloor()
    {
        string[] keys;
        string[] nodes;
        lock (_core)
        {
            var run = _run;
            if (run.Core is not { } core || core.Machine.Record(Now()).Floor is null) return (false, StagewatchVerbs.NoFloorWords + " — nothing to lower.");
            core.Machine.LowerFloor();
            // The Stagewatch's floor goes at every address a link verified it at, by the node id each floor was recorded for.
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (core.Machine.NodeId is { Length: > 0 } id) ids.Add(id);
            if (run.Floors.TryGetValue(core.Target, out var byName) && byName.Node.Length > 0) ids.Add(byName.Node);
            nodes = [.. ids];
            keys = [core.Target, .. nodes.Select(n => "node:" + n)];
            foreach (var key in run.Floors.Where(f => keys.Contains(f.Key) || ids.Contains(f.Value.Node)).Select(f => f.Key).ToList()) run.Floors.Remove(key);
        }
        _config().LowerFloor(keys, nodes);                                                  // and the kept floors with it, for the next run and a restart
        SayChange();
        Log.Info("Stagewatch link: the floor recorded for the Stagewatch lowered by the operator.");
        return (true, "The Stagewatch link's floor is lowered — it takes a lesser link from now, said amber.");
    }

    /// <summary>
    /// The show needs the link while the stage follows it (13.1): a change is told to the run and to each machine its record comes from,
    /// so the light is red only while the show needs the link — the connection, the redial count and every hold go on as they were.
    /// </summary>
    private void NeedsNow(bool needs)
    {
        var run = _run;
        lock (_core)
        {
            if (run.ShowNeeds == needs) return;
            run.ShowNeeds = needs;
            run.Core?.Machine.ShowNeedsLink(needs);
            run.Idle?.ShowNeedsLink(needs);
        }
        SayChange();
    }

    /// <summary>Start, restart or stop the link to match its settings (UI thread; cheap when nothing changed).</summary>
    public void Reconcile()
    {
        var c = _config();
        NeedsNow(c.FollowStage);
        var kid = c.ApiKey.Length > 0 ? PeerLink.Kid(PeerLink.KeyKey(c.ApiKey)) : "";
        var hostKey = c.Enabled ? $"{c.Host}|{kid}" : "";
        var key = RunningKey(c, hostKey);
        if (key == _runningKey) return;
        var carry = hostKey.Length > 0 && hostKey == _runningHostKey;                       // the same key at the same address: a person's pin set, say
        _runningKey = key;
        _runningHostKey = hostKey;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        var previous = _run;
        var run = new Run { Kid = kid.Length > 0 ? kid : null, ShowNeeds = c.FollowStage, KeyFor = c.KeyFor };
        foreach (var f in c.Floors) run.Floors[f.Key] = new KeptFloor(f.Floor, f.Node ?? "");
        if (carry) run.Carry(previous);                                                     // its holds and standing go on
        var typed = StageFollow.ParseAddress(c.Host) is { } t ? $"{t.Host}:{t.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : null;
        run.NotAnAddress = key.Length > 0 && typed is null && c.Host.Length > 0 && StageFollow.ParseId(c.Host) is null;   // as the run resolves it: any typed text it cannot read
        lock (_core) run.Idle = new PeerLinkMachine(IdleSettings(key.Length > 0 && !run.NotAnAddress, typed, null, run.ShowNeeds), null, Now(), Jitter);
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
        _bindOffer = null;
        if (key.Length == 0)
        {
            _status = "Stagewatch link off.";
            SayChange();
            _changed();
            return;
        }
        _cts = new CancellationTokenSource();
        var host = c.Host;
        var apiKey = c.ApiKey;
        var pins = new Dictionary<string, CertificatePin>(StringComparer.Ordinal);
        foreach (var p in c.Pins) pins[p.Key] = p;
        var ct = _cts.Token;
        _status = StageFollow.ParseId(host) is { } wanted ? $"Stagewatch link: listening for Stagewatch {wanted} on the show network…"
            : host.Length > 0 ? $"Stagewatch link: dialling {host}…" : "Stagewatch link: listening for a Stagewatch on the show network…";
        SayChange();
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
        /// <summary>G34: the <c>addr:</c> binding of the address typed to reach this Stagewatch ("" when it was found, or named by id).</summary>
        public string TypedKey { get; init; } = "";

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
                    // The record says the hold (13.16.1's held, refusal conflict) until the mark clears.
                    lock (_core)
                    {
                        var now = Now();
                        if (run.Idle is not { NodeId: { } idleFor } || idleFor != conflict.Id)
                            run.Idle = new PeerLinkMachine(IdleSettings(true, null, conflict.Id, run.ShowNeeds), null, now, Jitter);
                        run.Idle.Conflict(now, Math.Max(1, (long)clears.TotalMilliseconds));
                        run.Current = null;
                    }
                }
                else
                {
                    lock (_core)
                    {
                        if (run.Idle is { NodeId: not null }) run.Idle = new PeerLinkMachine(IdleSettings(true, null, null, run.ShowNeeds), null, Now(), Jitter);
                        run.Current = null;                                                  // finding one again: the record says so
                    }
                    _status = StageFollow.ParseId(typedHost) is { } wanted
                        ? $"Stagewatch link: no Stagewatch with id {wanted} heard on the show network yet (UDP 9928)."
                        : typedHost.Length > 0
                            ? $"Stagewatch link: '{typedHost}' is not an address — host:port, id:<a heard Stagewatch's id>, or empty to find one on the show network."
                            : "Stagewatch link: no Stagewatch heard on the show network yet (UDP 9928).";
                    _nodeId = "";
                    _held = "";
                }
                if (!string.Equals(wasHeld, _held, StringComparison.Ordinal)) UiThread.Post(_changed);   // the hold reaches the desk's readers
                SayChange();
                await Delay(wait, ct);
                continue;
            }
            finding = 0;
            _held = "";
            _nodeId = target.PinKey.StartsWith("node:", StringComparison.Ordinal) ? target.PinKey[5..] : "";
            if (_nodeId.Length > 0) last = _nodeId;
            var core = CoreFor(run, target, pins);
            long? dueIn;
            var refusal = "";
            lock (_core)
            {
                var now = Now();
                dueIn = core.Machine.DialIn(now);
                if (dueIn is null) refusal = core.Machine.Refusal(now);
            }
            if (dueIn is null)
            {
                // CERTIFICATE CHANGED: nothing of this desk's goes to it until a person accepts the new one (13.8.3). Any other
                // refusal (a floor, 13.9.3) waits for its own press, and nothing is looked at meanwhile.
                if (refusal == "certificate") await ProbeChangedAsync(core, target, ct);
                else await Delay(PeerLink.CheckEvery, ct);
                continue;
            }
            if (dueIn > 0)
            {
                // The core's wait (13.15.1), or a hold that lets nothing be dialled: the target is looked at again each second.
                await Delay(TimeSpan.FromMilliseconds(Math.Min(dueIn.Value, (long)PeerLink.CheckEvery.TotalMilliseconds)), ct);
                continue;
            }
            bool isPublic;
            var floored = false;
            lock (_core)
            {
                var now = Now();
                isPublic = core.Machine.Dial(now) is null;
                // 13.9.3: a Stagewatch with a floor is read only over a link that reaches it, so inside a held key's hold nothing is dialled
                // for its public view — a socket that proves nothing would offer less than the floor.
                floored = isPublic && core.Machine.Kept.Floor is not null;
                // Inside a refused key's hold the dial reads the public view, the key proved again on a fresh socket at its end.
                if (isPublic && !floored && core.Machine.DialPublic(now) is null) continue;
                // 13.11.7: the clock measures one Stagewatch process; another inst in its hello empties the window.
                if (!floored) core.Machine.PeerInst(now, target.Inst);
            }
            if (floored)
            {
                await Delay(PeerLink.CheckEvery, ct);
                continue;
            }
            SayChange();
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
            SayChange();
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
            if (run.Core is { } kept && kept.Target == target.PinKey)
            {
                run.Current = kept.Machine;                                                  // the record is this Stagewatch's again
                return kept;
            }
            var now = Now();
            var nodeId = target.PinKey.StartsWith("node:", StringComparison.Ordinal) ? target.PinKey[5..] : null;
            var saved = pins.TryGetValue(target.PinKey, out var p) && TlsFingerprint.IsValid(p.Fingerprint) ? p : null;
            var pin = saved is null ? null : new LinkPin(nodeId, saved.Fingerprint[TlsFingerprint.Prefix.Length..], saved.Trust);
            // G34: a key bound to another Stagewatch never goes to this one and no hold of it is kept here, but it is set and held back:
            // the record names its kid, reads keyless amber and says where the key is bound, so every reader says so from the record.
            var keyed = run.Kid is not null && StagewatchKeyFor.Goes(run.KeyFor, target.PinKey, target.TypedKey);
            var hold = keyed && run.Kid is { } kid ? Holds.For(target.PinKey, kid, now.Wall) : null;
            var settings = new LinkSettings
            {
                Carrier = LinkCarrier.Ws,
                Name = target.Name,
                Kind = "main",
                NodeId = nodeId,
                Address = $"{target.Host}:{target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                Key = run.Kid,
                KeyHeldBack = run.Kid is not null && !keyed ? HeldBackWords(run.KeyFor) : null,
                ShowNeeds = run.ShowNeeds,
                Uses = "post",
            };
            // 13.9.3: the floor a verified link recorded for this name, or for the id it names.
            var floor = run.Floors.TryGetValue(target.PinKey, out var f) ? f.Floor : nodeId is not null && run.Floors.TryGetValue("node:" + nodeId, out var byId) ? byId.Floor : null;
            var machine = new PeerLinkMachine(settings, new LinkKept(pin, floor, hold?.UntilWallMs, hold?.Word, HeldPublic: hold?.Public ?? true), now, Jitter);
            if (hold is { BusyUntilWallMs: var busyWall } && busyWall > now.Wall) machine.KeyRefused(now, "busy", busyWall - now.Wall);
            var before = run.Core;
            if (before is not null) machine.Continue(now, before.Machine);
            // A hold the run stands under that no core of it recorded (one carried from the run before) holds this core too.
            long heldUntil, busyUntil;
            lock (run.Gate) (heldUntil, busyUntil) = (run.HeldUntilMs, run.BusyUntilMs);
            if (heldUntil > now.Mono && (machine.Kept.HeldUntilWall ?? long.MinValue) < now.Wall + (heldUntil - now.Mono)) machine.KeyRefused(now, "key", heldUntil - now.Mono);
            if (busyUntil > now.Mono) machine.KeyRefused(now, "busy", busyUntil - now.Mono);
            run.Core = new LinkCore(target.PinKey, machine) { FirstSaved = saved is { Trust: PinTrust.First } ? saved.Fingerprint : "" };
            run.Current = machine;                                                           // the record is this Stagewatch's from now
            run.Targets.Add(target.PinKey);
            if (keyed && run.Kid is { } heldKid && machine.Kept is { HeldUntilWall: { } until } carried)
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
        // G34: a heard Stagewatch by its show network id — dialled wherever its hello is heard, held while two machines say it.
        if (StageFollow.ParseId(typed) is { } wanted)
        {
            var byId = live.FirstOrDefault(p => p.Id == wanted);
            if (byId?.Conflict is { } marked2)
            {
                conflict = marked2;
                return null;
            }
            return byId is null ? null : Of(byId, byId.Address, byId.Ports.Https > 0 && byId.OffersTls ? byId.Ports.Https : byId.Ports.Ws > 0 ? byId.Ports.Ws : byId.Ports.Http);
        }
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
            var typedKey = StagewatchKeyFor.Address(t.Host, t.Port);
            if (named is not null) return Of(named, t.Host, t.Port) with { TypedKey = typedKey };
            return new Target(t.Host, t.Port, t.Host, typedKey, "", false, false) { TypedKey = typedKey };
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
        LinkPin? corePin;
        lock (_core) corePin = core.Machine.Pin;
        // 13.8.2: a pin a proof made on this run is the core's before the settings keep it: it binds this dial as any pin does.
        if (pin.Length == 0 && corePin is { Trust: not PinTrust.First } made)
        {
            pin = TlsFingerprint.Prefix + made.Fingerprint;
            first = "";
        }
        var pinned = pin.Length > 0;
        // G34: the key goes only to the Stagewatch it is bound to; to any other the link reads the public view, and the desk offers to
        // bind it to this one (BIND KEY), a person's press.
        var bound = StagewatchKeyFor.Goes(run.KeyFor, target.PinKey, target.TypedKey);
        var key = isPublic || !bound ? "" : apiKey;
        // Round 96 (13.6.5): the key is proved with LINK, never sent, to any Stagewatch that may hear it — over TLS to whatever
        // certificate it shows, the proof bound to that one, or plain to loopback — unless this run found it older than LINK.
        var linkQuery = key.Length > 0 && !core.Level0 ? StagewatchFrame.LinkQuery(SelfId) : null;
        var certs = new CertGate();
        Volatile.Write(ref _bindOffer, apiKey.Length > 0 && !bound ? new BindOffer(target.PinKey, target.Name, run.KeyFor) : null);
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
                // The key rides the upgrade only to a certificate a person or a proof pinned — the check refuses any other — or to
                // loopback; a LINK dial carries none, and proves it once the offer names the certificate this socket was shown.
                var tlsKey = linkQuery is null && key.Length > 0 && (pinned || local) ? key : "";
                RemoteCertificateValidationCallback validate = (_, cert, _, _) =>
                {
                    probe.Certificate();
                    // Once LINK verified over a certificate, a side call's socket shown another gets nothing (13.7.1). The gate reads
                    // this handshake's own fingerprint: the shared one is every socket's of the dial, kept for the words and the probe.
                    var seen = "";
                    var accepted = CheckCertificate(core, target, pin, first, cert, ref seen);
                    presented = seen;
                    return accepted && certs.Admits(seen);
                };
                try
                {
                    var (handler, invoker, socket) = await DialAsync(at, probe, validate, target, tls: true, tlsKey, linkQuery ?? StagewatchFrame.SocketQuery(SelfId), ct);
                    return new Dialled(at, handler, invoker, socket, Tls: true, Keyed: tlsKey.Length > 0, Local: local, Link: linkQuery is not null);
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
            // Plain: the key goes, or is proved, only to loopback. Anywhere else, the Stagewatch is read without the key (a Stagewatch
            // takes a plain LINK socket only from its own machine).
            var plainLink = linkQuery is not null && local;
            var keyed = key.Length > 0 && local && !plainLink;
            var plain = new DialProbe(tls: false, TlsAnswerWithin);
            probes.Add(plain);
            var (plainHandler, plainInvoker, plainSocket) = await DialAsync(at, plain, null, target, tls: false, keyed ? key : "",
                plainLink ? linkQuery! : StagewatchFrame.SocketQuery(SelfId), ct);
            return new Dialled(at, plainHandler, plainInvoker, plainSocket, Tls: false, Keyed: keyed, Local: local, Link: plainLink);
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
        var link = dialled.Link;
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
        var heldBack = key.Length > 0 && !tls && !keyed && !link;                           // off this machine, and it offers no TLS
        var withheld = key.Length > 0 && tls && !keyed && !link;                            // over TLS to a certificate no person pinned (13.6.8)
        _carrier = tls ? "tls" : heldBack || (isPublic && !dialled.Local) ? "keyless" : "plain";
        if (heldBack)
        {
            _note = target.HelloTls
                ? $"{target.Name} answered without TLS though its hello names a certificate — the key stays on this desk"
                : $"{target.Name} offers no TLS — the key stays on this desk";
        }
        if (keyed || link)
        {
            lock (_trustGate) _trust = null;                                                // the key goes, or is proved: nothing waits for Trust
        }
        // The address a name led to: the log's line names it, and the link's words do while the connection is up whenever an
        // address before it did not answer — a name with an address that is dead costs every dial its wait.
        _reached = missed.Count > 0 ? $"reached at {address} after {string.Join(", ", missed)}" : "";
        var reached = IPAddress.TryParse(target.Host, out _) ? "" : $" (reached at {address})";
        var how = tls ? withheld ? "TLS, its key held back until its certificate is trusted" : link ? "TLS, this desk's key proved with LINK, never sent" : "TLS"
            : heldBack ? "plain ws, read without its key" : link ? "plain ws on this machine, this desk's key proved with LINK, never sent" : "plain ws";
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
        // The Stagewatch's own speaker message is kept across a redial to the same one: an answer its frames no longer carry stays known.
        var message = run.Show is { } before && before.Key == target.PinKey ? _sight.Message : StageMessageSeen.None;
        _sight = new StagewatchSight(name, $"{target.Host}:{target.Port}", ShowBusState.Live, null, ShowClock.Seconds, null, "") { Message = message };
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

        // The side calls ride the dial's own handler: the same address, the same pin, the key only where the socket's carried it —
        // or, on a LINK socket, its S once the acceptor's proof verified (13.7.1), and nothing before.
        var sideKey = keyed || link ? apiKey : "";
        // Every answer is read whole before it is parsed, an error's body too: bounded here, well above a context's 64 KiB.
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = StagewatchFrame.SideCallWithin, MaxResponseContentBufferSize = SideAnswerMost };
        var baseUri = Address(target, tls, socket: false);
        using var sendGate = new SemaphoreSlim(1, 1);
        var connection = new Connection(run, core, target, tls, keyed, isPublic, withheld, apiKey, ws, sendGate, link, certs) { Proved = keyed };
        run.Live = connection;
        // The speaker's lane is this connection's from the instant it is the run's: a call asked before the lane's task has
        // started waits for it there, never refused for a lane not yet running. What was asked of the connection before and
        // never written is not this one's to send.
        OpenSpeakerLane(connection);
        var beat = Task.Run(() => BeatAsync(connection, ct), CancellationToken.None);
        var lane = Task.Run(() => SideCallsAsync(connection, show, http, baseUri, sideKey, ct), CancellationToken.None);
        var speaker = Task.Run(() => SpeakerCallsAsync(connection, show, http, baseUri, sideKey, ct), CancellationToken.None);
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
            await EndedWithTheLink(speaker);
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
    private sealed record Dialled(IPAddress At, SocketsHttpHandler Handler, HttpMessageInvoker Invoker, ClientWebSocket Socket, bool Tls, bool Keyed, bool Local, bool Link);

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
    private static async Task<(SocketsHttpHandler Handler, HttpMessageInvoker Invoker, ClientWebSocket Socket)> DialAsync(IPAddress address, DialProbe probe, RemoteCertificateValidationCallback? validate, Target target, bool tls, string key, string query, CancellationToken ct)
    {
        var handler = Handler(address, probe, validate);
        var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        try
        {
            var ws = await UpgradeAsync(invoker, probe, target, tls, key, query, ct);
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
    /// (<see cref="StagewatchFrame.SocketQuery"/>), so a Stagewatch 2.18 hears who it is and says first what it made of the key;
    /// round 96: with <c>link=</c> and no header (<see cref="StagewatchFrame.LinkQuery"/>) when the key is proved instead.
    /// </summary>
    private static async Task<ClientWebSocket> UpgradeAsync(HttpMessageInvoker invoker, DialProbe probe, Target target, bool tls, string key, string query, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            ws.Options.KeepAliveInterval = TimeSpan.Zero;                              // the Show Bus beat is the keep-alive
            if (key.Length > 0) ws.Options.SetRequestHeader("Authorization", "Bearer " + key);
            budget.CancelAfter(UpgradeWithin);
            probe.Arm(budget);
            await ws.ConnectAsync(new Uri(Address(target, tls, socket: true), query), invoker, budget.Token);
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
        // 13.6.5: a LINK socket's one message before its verdict is the link message; it is pinged from the verdict on.
        if (c.Link && !c.Heard) return;
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
            if (ReferenceEquals(run, _run)) SayChange();                                     // a link gone quiet moves its state with no word
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
                // 13.6.3: a LINK socket takes nothing — a context included — before its verdict is read.
                var open = !c.Link || c.Heard;
                if (open && (lastContext == long.MinValue || now - lastContext >= (long)StagewatchFrame.ContextEvery.TotalMilliseconds))
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

    // ---- the speaker's calls -------------------------------------------------------------------------------------------

    /// <summary>The speaker's calls not yet written, in order, and the connection whose lane takes them (both under this gate).</summary>
    private readonly object _speakerGate = new();
    private readonly List<SpeakerCall> _speakerQueue = new();
    private Connection? _speakerOpen;

    /// <summary>Completed when a call is asked (under the gate): the lane that found none waits on it, and makes the next once it is spent.</summary>
    private TaskCompletionSource _speakerAsked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The most speaker's calls that wait at once; one more is not taken, and its asker says so.</summary>
    public const int MostSpeakerCalls = 16;

    /// <summary>
    /// The most one of the speaker's calls takes (§14's <c>link.sidecall.timeout.ms</c>); a test shortens it to see a timeout.
    /// Read on the link's task: set it before a call is asked.
    /// </summary>
    public TimeSpan SpeakerCallWithin { get; set; } = StagewatchFrame.SideCallWithin;

    /// <summary>
    /// What became of one of the speaker's calls — each call answered exactly once, on the UI thread: its receipt read by 13.12.3,
    /// or why it was never sent.
    /// </summary>
    public event Action<SpeakerAnswer>? SpeakerAnswered;

    /// <summary>
    /// Whether a speaker's message can go through the Stagewatch now, read at the moment of the verb: the name of the Stagewatch the
    /// link holds ("" with none — nothing to say of one), and when it cannot, why, in words a reply carries.
    /// </summary>
    public SpeakerRoute Route
    {
        get
        {
            var run = _run;
            var sight = Sight;
            if (sight.Name.Length == 0 && sight.Address.Length == 0) return new SpeakerRoute(false, "", "");
            var name = sight.Name;
            var c = run.Live;
            if (c is null || sight.State == ShowBusState.Gone) return new SpeakerRoute(false, name, $"Stagewatch {name} is not answering");
            if (sight.State == ShowBusState.Stale) return new SpeakerRoute(false, name, $"Stagewatch {name} is quiet");
            var why = SpeakerRefusal(c, run.Show);
            return new SpeakerRoute(why.Length == 0, name, why);
        }
    }

    /// <summary>
    /// Why a speaker's call may not be written on this connection now; "" when it may: the connection carried the key, its first
    /// word is read, no hold stands, the key was not refused a message, and the Stagewatch's show is not stuck.
    /// </summary>
    private string SpeakerRefusal(Connection c, HeardShow? show)
    {
        var name = c.Target.Name;
        if (c.Link && !c.Heard) return $"Stagewatch {name} has not answered this link yet";
        if (!c.Keyed)
        {
            if (c.Secret.Length == 0) return "the link holds no node key";
            if (c.Link && !c.Withheld) return c.Run.Key.Refused ? "this desk's key is refused there" : "this link did not prove this desk's key";
            if (c.Withheld) return "the key waits for Trust — compare the fingerprint on the Nodes page and press Trust";
            if (c.Public) return "this desk's key is held there";
            return Volatile.Read(ref _bindOffer) is not null ? "the key is bound to another Stagewatch" : $"Stagewatch {name} offers no TLS, so the key stays on this desk";
        }
        if (!c.Heard) return $"Stagewatch {name} has not answered this link yet";
        if (HeldFor(c.Run) is var held && held > TimeSpan.Zero)
            return c.Run.Key.Refused
                ? "this desk's key is refused there"
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"its rate limit holds this desk's key {Math.Ceiling(held.TotalSeconds):0} s");
        if (NodeHeldFor(c.Run) > TimeSpan.Zero) return "the key may read but speaks for no node of this desk's there";
        if (c.Run.SpeakerRefused.Length > 0) return "the key may read but not send messages";
        if (show is { Stuck: true }) return $"Stagewatch {name} says its show is stuck";
        return "";
    }

    /// <summary>
    /// One of the speaker's calls asked (UI thread): <paramref name="kind"/> — <see cref="SpeakerCall.Message"/>,
    /// <see cref="SpeakerCall.Clear"/> or <see cref="SpeakerCall.Ack"/> — on <paramref name="path"/>, <paramref name="tag"/> what
    /// the asker knows it by. Taken only while the route stands and this run's connection has a lane open; a newer message replaces
    /// one not yet written, which is answered not-sent. <paramref name="after"/>: the message calls a clear follows — it is
    /// written only if one of them reached the Stagewatch (<see cref="SpeakerCall.After"/>). Returns the call with its 13.12.1
    /// id, or null when it was not taken.
    /// </summary>
    public SpeakerCall? Ask(string kind, string path, string tag, IReadOnlyList<string>? after = null)
    {
        SpeakerCall call;
        SpeakerCall? superseded = null;
        string name;
        lock (_speakerGate)
        {
            var c = _run.Live;
            if (c is null || !ReferenceEquals(c, _speakerOpen) || SpeakerRefusal(c, c.Run.Show).Length > 0) return null;
            if (_speakerQueue.Count >= MostSpeakerCalls) return null;
            string id;
            lock (_core) id = c.Core.Machine.NextId();
            call = new SpeakerCall(kind, path, id, tag) { After = after ?? [] };
            name = c.Target.Name;
            if (kind == SpeakerCall.Message && _speakerQueue.FindIndex(q => q.Kind == SpeakerCall.Message) is var at and >= 0)
            {
                superseded = _speakerQueue[at];
                _speakerQueue.RemoveAt(at);
            }
            _speakerQueue.Add(call);
            _speakerAsked.TrySetResult();
        }
        if (superseded is not null) Answer(superseded, new StageReceipt(LinkOutcome.NotSent, 0, StageSpeakerWords.SupersededWhy), name);
        return call;
    }

    private void Answer(SpeakerCall call, StageReceipt receipt, string name)
        => UiThread.Post(() => SpeakerAnswered?.Invoke(new SpeakerAnswer(call, receipt, name)));

    /// <summary>
    /// The speaker's queue is one connection's: as <paramref name="connection"/> becomes the run's, whatever was asked of the
    /// one before and never written is answered not-sent — a lane still winding down takes none of this connection's calls,
    /// and this connection sends none of that one's late.
    /// </summary>
    private void OpenSpeakerLane(Connection connection)
    {
        List<SpeakerCall> unsent;
        string before;
        lock (_speakerGate)
        {
            before = _speakerOpen?.Target.Name ?? connection.Target.Name;
            unsent = _speakerQueue.ToList();
            _speakerQueue.Clear();
            _speakerOpen = connection;
        }
        foreach (var call in unsent) Answer(call, new StageReceipt(LinkOutcome.NotSent, 0, $"the link to Stagewatch {before} dropped before it was sent"), before);
    }

    /// <summary>
    /// The speaker's lane of one connection (opened to askers as the connection becomes the run's): each call in the order asked,
    /// one in flight, taken the moment it is asked. When the connection ends, what was never written is answered not-sent — it
    /// did not run — and nothing waits for the next connection: a message is never sent late.
    /// </summary>
    private async Task SpeakerCallsAsync(Connection c, HeardShow show, HttpClient http, Uri baseUri, string apiKey, CancellationToken ct)
    {
        var name = c.Target.Name;
        // What each message this lane wrote came to, for a clear that follows one (bounded: forgotten wholesale when full).
        var ended = new Dictionary<string, LinkOutcome>(StringComparer.Ordinal);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SpeakerCall? call = null;
                Task asked = Task.CompletedTask;
                lock (_speakerGate)
                {
                    // The queue is this lane's only while the connection is the one askers are given.
                    if (!ReferenceEquals(_speakerOpen, c)) return;
                    if (_speakerQueue.Count > 0)
                    {
                        call = _speakerQueue[0];
                        _speakerQueue.RemoveAt(0);
                    }
                    else
                    {
                        if (_speakerAsked.Task.IsCompleted) _speakerAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        asked = _speakerAsked.Task;
                    }
                }
                if (call is null)
                {
                    await asked.WaitAsync(ct);
                    continue;
                }
                StageReceipt receipt;
                if (call.After.Count > 0 && !call.After.Any(id => ended.TryGetValue(id, out var came) && came is LinkOutcome.Applied or LinkOutcome.Accepted or LinkOutcome.Unconfirmed))
                {
                    // The clear follows a message that never reached the Stagewatch: what is on its stage is not this desk's to clear.
                    receipt = new StageReceipt(LinkOutcome.NotSent, 0, StageSpeakerWords.NothingToClearWhy);
                }
                else
                {
                    try
                    {
                        receipt = await SpeakerCallAsync(c, show, http, baseUri, apiKey, call, ct);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // A fault this desk did not expect, after the request may have left: unconfirmed, counted, and the lane goes on.
                        Interlocked.Increment(ref _sideFaults);
                        Log.Warn($"Stagewatch link: a speaker's call to {name} failed — counted.", ex);
                        receipt = new StageReceipt(LinkOutcome.Unconfirmed, 0, "this desk could not read what came of it");
                    }
                }
                if (call.Kind == SpeakerCall.Message)
                {
                    if (ended.Count >= MostSpeakerCalls * 4) ended.Clear();
                    ended[call.Id] = receipt.Outcome;
                }
                Answer(call, receipt, name);
            }
        }
        catch (OperationCanceledException)
        {
            // the connection ended: what waits is answered below
        }
        finally
        {
            List<SpeakerCall> unsent = [];
            lock (_speakerGate)
            {
                // Only its own queue: a newer connection's calls are that connection's lane's to send.
                if (ReferenceEquals(_speakerOpen, c))
                {
                    _speakerOpen = null;
                    unsent = _speakerQueue.ToList();
                    _speakerQueue.Clear();
                }
            }
            foreach (var call in unsent) Answer(call, new StageReceipt(LinkOutcome.NotSent, 0, $"the link to Stagewatch {name} dropped before it was sent"), name);
        }
    }

    /// <summary>
    /// One of the speaker's calls: checked against the route once more before it is written (a hold begun, a show gone stuck or the
    /// link ended since it was asked is not-sent, with why), then posted with its id and read by 13.12.3. A 401 and a 429 go through
    /// 13.7.3's one door (<see cref="Answered"/>: a relink, the key's hold, the rate limit's wait); a 403 to a message or a clear
    /// says the key may not send them and holds nothing else; a 403 to an ACK is only that ACK's refusal. No answer after the
    /// request was written is unconfirmed, never refused; a connection that could not be made is not-sent — and it can only be
    /// this call's first, since a POST here always carries a body (<see cref="Request"/>), which the handler never sends twice.
    /// </summary>
    private async Task<StageReceipt> SpeakerCallAsync(Connection c, HeardShow show, HttpClient http, Uri baseUri, string apiKey, SpeakerCall call, CancellationToken ct)
    {
        var name = c.Target.Name;
        if (ct.IsCancellationRequested || c.Socket.State != WebSocketState.Open)
            return new StageReceipt(LinkOutcome.NotSent, 0, $"the link to Stagewatch {name} dropped before it was sent");
        if (SpeakerRefusal(c, show) is { Length: > 0 } why) return new StageReceipt(LinkOutcome.NotSent, 0, why);
        var asked = Asked(c.Run);
        int status;
        string body;
        long retryAfter;
        using var deadline = new CancellationTokenSource(SpeakerCallWithin, Time);
        using var within = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            using var request = Request(c, HttpMethod.Post, new Uri(baseUri, call.Path), apiKey);
            request.Headers.TryAddWithoutValidation("X-Request-Id", call.Id);
            using var response = await http.SendAsync(request, within.Token);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(within.Token);
            retryAfter = RetryAfterOf(response);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError)
        {
            Interlocked.Increment(ref _sideFaults);
            return new StageReceipt(LinkOutcome.NotSent, 0, $"no connection to Stagewatch {name} could be made");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            Interlocked.Increment(ref _sideFaults);
            return new StageReceipt(LinkOutcome.Unconfirmed, 0,
                ct.IsCancellationRequested ? $"the link dropped before Stagewatch {name} answered"
                : ex is OperationCanceledException ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"no answer in {SpeakerCallWithin.TotalSeconds:0.#} s")
                : $"the connection closed before Stagewatch {name} answered");
        }
        var receipt = StageMessagePath.Receipt(status, body, (int)Math.Min(retryAfter, int.MaxValue), c.Secret);
        if (status is 401 or 429)
        {
            Answered(c, status, body, retryAfter, asked, context: false);
        }
        else if (status == 403 && call.Kind != SpeakerCall.Ack && c.Run.SpeakerRefused.Length == 0)
        {
            c.Run.SpeakerRefused = receipt.Words;
            Log.Warn($"Stagewatch link: {name} refused this desk's key a message to its speaker ({receipt.Words}) — the speaker's messages stay on this desk's displays.");
            UiThread.Post(_changed);
        }
        return receipt;
    }

    /// <summary>
    /// One side call's request. A POST always carries a body — an empty one when it has nothing to say, the same bytes on the
    /// wire (<c>Content-Length: 0</c>) — because the HTTP handler sends a request with no body again, on a new connection, when
    /// the connection it went on closes before an answer byte: once any byte of a command may have reached the Stagewatch it is
    /// never sent again (Show Bus 1.3 §13.12.7), and with no second connect a refused connect is only ever the first, so a
    /// not-sent outcome means nothing left (13.12.3).
    /// </summary>
    /// <summary>
    /// One side call's request, with the credential its connection may carry: on a LINK socket the S of its verified link and never
    /// the key (13.7.1) — none before the verdict or after a refusal; on a level-0 socket that carried the key, the key as a bearer.
    /// </summary>
    private static HttpRequestMessage Request(Connection c, HttpMethod method, Uri url, string apiKey, string? json = null)
    {
        var r = new HttpRequestMessage(method, url);
        if (c.Link)
        {
            if (c.Session is { } session) r.Headers.TryAddWithoutValidation("Authorization", StagewatchFrame.SessionHeader(session.Lid, session.S));
        }
        else if (apiKey.Length > 0) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        else if (method == HttpMethod.Post) r.Content = new ByteArrayContent([]);
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
            using var request = Request(c, HttpMethod.Get, url, apiKey);
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
                show.Context = KeptContext.Next(show.Context, context);                          // its revision moves only when what it says moved
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
            using var request = Request(c, HttpMethod.Post, url, apiKey, json);
            using var response = await http.SendAsync(request, ct);
            var said = Answered(c, (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct), RetryAfterOf(response), asked, context: true);
            if (response.IsSuccessStatusCode) BindAtFirstAnswer(c, apiKey);
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
                    using var post = Request(c, HttpMethod.Post, new Uri(baseUri, "/api/proposals"), apiKey, DeskProposals.Request(SelfId, SelfName, SelfId, e.Line, e.Reason, e.Risk));
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
            using (var mine = Request(c, HttpMethod.Get, new Uri(baseUri, "/api/proposals?from=" + Uri.EscapeDataString(SelfId)), apiKey))
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
            using var approved = Request(c, HttpMethod.Get, new Uri(baseUri, "/api/proposals?target=" + Uri.EscapeDataString(SelfId) + "&state=approved"), apiKey);
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
            using var result = Request(c, HttpMethod.Post, new Uri(baseUri, $"/api/proposals/{Uri.EscapeDataString(id)}/result"), apiKey, body);
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
        // A relink ends S (13.7.3); a socket LINK ran on that carries none — refused, or a public viewer — has no S for it to end. A
        // link= socket that found level 0 is read as any level-0 socket is.
        if (status == 401 && relink && (c.Transcript is null || c.Session is not null))
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
            if (step.Takes && !c.Public && asked < 0) run.SpeakerRefused = "";
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
                // A LINK verdict's hold is the core's already, in its own class's word (13.6.4), and the store keeps that word, so a
                // restart reads what this run read; an answer's is told here.
                if (asked < 0 && c.Transcript is not null)
                {
                    if (word == "key" && c.Core.Machine.Refusal(now) is { Length: > 0 } held) word = held;
                }
                else c.Core.Machine.KeyRefused(now, word, holdMs, publicView);
            }
            if ((c.Keyed || c.Proved) && run.Kid is { } kid) Holds.Hold(c.Core.Target, kid, wall + holdMs, word, wall, publicView);
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
        if (StagewatchKey.OfVerdict(auth, SelfId, c.Keyed || c.Proved, RefusedHoldAtLeast) is not { } step) return;
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
            // A LINK socket's offer, its verdict and a verdict on a live link are LINK's (13.6.3, 13.6.5); nothing else is taken before
            // the verdict. A link= socket that found level 0 takes its verdicts as any level-0 socket does.
            if (c.Link && (!c.Heard || (type is "auth" or "link" && c.Transcript is not null)))
            {
                var step = await LinkAsync(c, type, text, ct);
                if (step == LinkStep.Ended)
                {
                    c.Socket.Abort();
                    return;
                }
                if (step == LinkStep.Consumed) continue;
            }
            var restartedAt = 0L;
            lock (_core)
            {
                var now = Now();
                var m = c.Core.Machine;
                if (!c.Heard && !c.Link) m.FirstMessage(now, frame: false);              // 13.6.8: its first message is no link — level 0
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
                    if (c.Transcript is not null) LinkVerdict(c, text);                                       // its hold recorded before the lane may send anything keyed
                    else Verdict(c, text);
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
                        _sight = _sight with { Frame = frame, FrameAtSeconds = arrived, State = ShowBusState.Live, Message = StageMessageSeen.Next(_sight.Message, frame, arrived) };
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
            if (!c.Heard || type == "auth") SayChange();                                   // a first word or a verdict moves the record
            c.Heard = true;                                                                 // after the first word is applied: no keyed call before it
        }
    }

    /// <summary>What LINK made of one message on a <c>link=</c> socket: kept by LINK alone, taken as any message, or the end of the connection.</summary>
    private enum LinkStep
    {
        Consumed,
        Taken,
        Ended,
    }

    /// <summary>
    /// Show Bus 1.3 §13.6 on a <c>link=</c> socket, as Stagewatch's acceptor speaks it (2.22): its first message, the offer, is
    /// proved once — only to the id meant and over the certificate this socket was shown, as the core decides — by one link message
    /// that carries a proof and never the key. The verdict is taken only when the acceptor's proof verifies with K over T (13.6.3);
    /// then the certificate is pinned by that proof (13.8.2), the floor recorded (13.9.3) and S made (13.7.1). A verdict that does not
    /// verify ends the connection as a failed dial, nothing of it taken, pinned or floored. A first message that is not the offer is
    /// an acceptor older than LINK (13.6.8): a floor refuses it; otherwise its bearer, where 13.6.8 lets one go, rides a fresh
    /// upgrade at once, and where none may go the socket reads the public view, the key held back for Trust. On a live link a
    /// refusal (a revoked key, a binding moved) ends S at once and leaves a public viewer. Anything else before the verdict is
    /// discarded and counted.
    /// </summary>
    private async Task<LinkStep> LinkAsync(Connection c, string type, string text, CancellationToken ct)
    {
        var name = c.Target.Name;
        var m = c.Core.Machine;
        string? message = null;
        var step = LinkStep.Consumed;
        var ended = "";
        LinkPin? proofPin = null;
        LinkFloor? floor = null;
        string? boundId = null;
        lock (_core)
        {
            var now = Now();
            if (c.Transcript is not null && type == "link")
            {
                // A second offer on a connection that has proved once is malformed (13.6.5): the core ends it, nothing more proved.
                m.LinkOffered(now, ObjectOf(text) is { } again ? PeerLink.ReadOfferMessage(again) : null);
                if (!m.KeepsConnection(now))
                {
                    step = LinkStep.Ended;
                    ended = $"{name} — {m.Record(now).Words}.";
                }
            }
            else if (c.Heard)
            {
                // A verdict on a live link (13.5.5, 13.6.5): a refusal ends S at once; a second ok is no verdict the link takes.
                var verdict = ObjectOf(text);
                if (verdict is { } v) m.Verdict(now, v, p => c.K is { } k && c.Transcript is { } t && PeerLink.Verify(PeerLink.ServerProof(k, t), p));
                else m.Malformed(now);
                if (verdict is not { } said || !said.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                {
                    c.Session = null;
                    c.Keyed = false;
                }
                step = m.KeepsConnection(now) ? LinkStep.Taken : LinkStep.Ended;
                if (step == LinkStep.Ended) ended = $"{name} sent this link a second verdict — no link takes two; dialling again.";
            }
            else if (c.Transcript is null && type == "link")
            {
                var offer = ObjectOf(text) is { } o ? PeerLink.ReadOfferMessage(o) : null;
                m.LinkOffered(now, offer);
                if (offer is not null && m.Drain().Any(w => w is { Verb: "link", Credential: "proof" }))
                {
                    var k = PeerLink.KeyKey(c.Secret);
                    var kid = PeerLink.Kid(k);
                    var cnonce = RandomNumberGenerator.GetHexString(PeerLink.NonceBytes * 2, lowercase: true);
                    var t = PeerLink.Transcript("ws", PeerLink.Version, offer.Speaks, offer.T + ":" + offer.Id, "patterns:" + SelfId, kid, offer.SNonce, cnonce, offer.Bind);
                    if (t is not null)
                    {
                        c.Transcript = t;
                        c.K = k;
                        c.Proved = true;
                        message = StagewatchFrame.LinkMessage("patterns", SelfId, cnonce, kid, PeerLink.ClientProof(k, t));
                    }
                }
                if (message is null || !m.KeepsConnection(now))
                {
                    step = LinkStep.Ended;
                    ended = $"{name} — {m.Record(now).Words}; nothing of this desk's key sent.";
                }
            }
            else if (c.Transcript is null)
            {
                // 13.6.8: no offer — an older Stagewatch. Floors first; then the bearer only where it may go, on an upgrade of its own.
                m.FirstMessage(now, frame: false);
                var bearer = m.Drain().Any(w => w.Credential == "bearer");
                if (!m.KeepsConnection(now))
                {
                    // Refused (a floor): the next dial asks for LINK again, so a person's FLOOR LOWER or the Stagewatch's own upgrade
                    // is met by LINK first.
                    step = LinkStep.Ended;
                    ended = $"{name} — {m.Record(now).Words}.";
                }
                else if (bearer)
                {
                    c.Core.Level0 = true;
                    m.Redial(now);
                    step = LinkStep.Ended;
                    ended = $"{name} does not speak LINK (an older Stagewatch) — dialling again with this desk's key on the {(c.Tls ? "certificate pinned for it" : "loopback address")}.";
                }
                else
                {
                    c.Core.Level0 = true;
                    c.Withheld = c.Tls;
                    step = LinkStep.Taken;
                }
            }
            else if (type == "auth")
            {
                var verdict = ObjectOf(text);
                var ok = verdict is { } v && v.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                if (verdict is { } said) m.Verdict(now, said, p => c.K is { } k && PeerLink.Verify(PeerLink.ServerProof(k, c.Transcript), p));
                else m.Malformed(now);
                if (!m.KeepsConnection(now))
                {
                    step = LinkStep.Ended;
                    ended = ok
                        ? $"{name} — {PeerLink.ProofWords}; nothing of its verdict taken, dialling again."
                        : $"{name} — {m.Record(now).Words}.";
                }
                else if (ok && c.K is { } k)
                {
                    c.Session = new LinkSession(PeerLink.Lid(c.Transcript), PeerLink.Session(k, c.Transcript));
                    c.Keyed = true;
                    proofPin = m.Pin is { Trust: PinTrust.Proof } p ? p : null;
                    floor = m.Kept.Floor;
                    boundId = m.NodeId;
                    if (floor is not null)
                    {
                        c.Run.Floors[c.Target.PinKey] = new KeptFloor(floor, boundId ?? "");
                        if (boundId is not null) c.Run.Floors["node:" + boundId] = new KeptFloor(floor, boundId);
                    }
                    step = LinkStep.Taken;
                }
                else step = LinkStep.Taken;
            }
            else
            {
                m.Heard(now);                                                               // before the verdict: discarded and counted (13.6.3)
            }
        }
        if (message is not null) await SendLinkAsync(c, message, ct);
        if (step == LinkStep.Ended) Volatile.Write(ref c.Run.Redial, ended);
        if (c.Session is not null && step == LinkStep.Taken && !c.Heard) TakeProof(c, proofPin, floor, boundId);
        SayChange();
        return step;
    }

    /// <summary>
    /// A link LINK verified (13.6.3): the certificate pinned by its proof (13.8.2) and only then, every later socket of the connection
    /// held to it (13.7.1); the floor kept with the settings by name and id (13.9.3), for the next run and a restart. One log line.
    /// </summary>
    private void TakeProof(Connection c, LinkPin? pin, LinkFloor? floor, string? nodeId)
    {
        var target = c.Target;
        var fp = pin is not null && c.Tls ? TlsFingerprint.Prefix + pin.Fingerprint : "";
        if (fp.Length > 0)
        {
            c.Certs.Pin(fp);
            c.Core.FirstSaved = "";
            _tlsWords = $"Stagewatch {target.Name}'s certificate {TlsFingerprint.Short(fp)} is pinned by its proof";
        }
        var keys = new List<string> { target.PinKey };
        if (nodeId is not null && "node:" + nodeId != target.PinKey) keys.Add("node:" + nodeId);
        UiThread.Post(() =>
        {
            var cfg = _config();
            if (fp.Length > 0) cfg.PinProof(target.PinKey, fp);
            if (floor is not null) foreach (var key in keys) cfg.SetFloor(key, floor.Tls, floor.Level, floor.Proof, nodeId ?? "");
        });
        Log.Info($"Stagewatch link: {target.Name} proved itself with LINK — PeerLink 1, " + (c.Tls ? $"over TLS, its certificate {TlsFingerprint.Short(fp)} pinned by the proof" : "on this machine")
            + "; this desk's key proved, never sent, and its side calls carry the link's session.");
    }

    /// <summary>
    /// What a LINK verdict says of this desk's key on every reader (13.6.4): one that verified takes it, as a node link's verdict does;
    /// a refused, bound, locked-out or out-of-scope key is held and kept for a restart; a rate limit waits its retry; an open
    /// acceptor, or a message it could not take, says nothing of the key. The core decided the link already.
    /// </summary>
    private void LinkVerdict(Connection c, string text)
    {
        if (StagewatchFrame.Auth(text, c.Secret) is not { } auth) return;
        if (auth.Ok)
        {
            if (c.Session is not null) Verdict(c, text);
            return;
        }
        var cls = ObjectOf(text) is { } v ? PeerLink.Text(v, "class") ?? "refused" : "refused";
        switch (cls)
        {
            case "refused" or "binding" or "locked" or "scope":
                Verdict(c, text);
                break;
            case "busy":
                Apply(c, new StagewatchKeyStep(null, StagewatchHoldKind.Busy, TimeSpan.FromSeconds(Math.Max(1, auth.Retry)), false, false, ""), -1);
                break;
            default:
                // The peer's class word is not logged: the core reduced any word outside 13.6.4's closed set to malformed.
                string said;
                lock (_core) said = c.Core.Machine.Refusal(Now());
                Log.Info($"Stagewatch link: {c.Target.Name} answered this desk's LINK {(said.Length > 0 ? said : "malformed")} — {(auth.Error.Length > 0 ? auth.Error : "no words")}; its public view is read.");
                break;
        }
    }

    /// <summary>The link message, through the connection's one writer, within <c>link.write.deadline.max.ms</c>.</summary>
    private async Task SendLinkAsync(Connection c, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
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
                $"{c.Target.Name} took no link message in {PeerLink.WriteDeadline.TotalSeconds:0} s — the connection is cut and dialled again."));
            c.Socket.Abort();
        }
        finally
        {
            c.SendGate.Release();
        }
    }

    /// <summary>One message as a JSON object, cloned so it outlives its document; null for anything else.</summary>
    private static JsonElement? ObjectOf(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
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
