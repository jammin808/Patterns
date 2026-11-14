using System.Globalization;
using System.Text;
using System.Threading;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// A Show Bus node driven as a device (PlayMap's line wire, TCP 9941), on the PeerLink core's rules at level 0 (Show Bus 1.3
/// §13.6.8): a connection opens with <c>@dh HELLO Patterns &lt;desk&gt;</c>; the node's <c>NONCE</c> — exactly 32 lowercase hex, and
/// no other — is answered once with §7's <c>AUTH-PROOF</c> over the nonce and that HELLO name, keyed by the device's Secret (the
/// node's pairing token), and a node that sent none is asked for one. Only after <c>OK paired</c> (or <c>OK open</c>) does it say
/// <c>SUBSCRIBE OFF</c> and <c>SUBSCRIBE CONTEXT</c>, take a context or a STATE, or send the show's facts. Anything that came before
/// the proof is discarded and counted (13.6.3) and never taken as a fact: an older PlayMap greets with STATE right after HELLO, a
/// newer one only after <c>OK paired</c>, and both are read. Every command carries its own id, <c>d&lt;n&gt;-&lt;inst&gt;</c> (13.12.1),
/// and is read by 13.12.3's table; the show's facts go untagged, only those whose key the node takes as a fact, so none becomes a
/// refused verb. A wrong token stops every send until the Secret is typed again: nothing retries toward the node's five-wrong close.
/// The Secret never leaves this session but as the proof's key, and no word of the session names it. Pure; one per open link.
/// <para>
/// Two threads touch it. The link's own thread says a connection moved (<see cref="LinkMoved"/>, before the new connection is
/// published and when one ends) and splits its bytes into lines (<see cref="Split"/>); everything else runs on the desk's thread
/// in the order the lines came. A proof belongs to the connection it was made on: <see cref="Up"/> holds only while no connection
/// moved since the one that was proved opened, so no command or fact goes out on a connection that has not said HELLO and proved
/// itself, even in the moment before the desk's thread hears it came up.
/// </para>
/// </summary>
public sealed class ShowBusNodeSession : ProfileSession
{
    /// <summary>PlayMap's line wire (Show Bus §8).</summary>
    public const int WirePort = 9941;

    /// <summary>The prefix of a command's request id (13.12.1): <c>d</c> for a device.</summary>
    public const string IdPrefix = "d";

    /// <summary>The most of one line read whole; a longer one is skipped to its end, counted, and never taken.</summary>
    public const int MaxLineChars = 1 << 20;

    /// <summary>The most of a node's context or STATE kept for the readers (latest wins).</summary>
    public const int MaxKeptBytes = 512 * 1024;

    /// <summary>
    /// The first words a node takes as a fact, untagged and never answered: a copy of PlayMap's <c>FactLines.Keys</c>
    /// (Mapper's <c>src/PlayMap.Net/Commands/CommandRunner.cs</c>, the list its <c>docs/WIRE.md</c> item 6 names). BLACKOUT is also
    /// the node's verb: only its 0 and 1 are a fact.
    /// </summary>
    public static IReadOnlyList<string> FactKeys { get; } = new[]
    {
        "BLACKOUT", "LIVE", "DUCK", "FROZEN", "REVIEW", "LOOK", "PROGRAM", "STINGER", "LOWERTHIRD",
        "VOL", "MUSICVOL", "LOOKEDITED", "ARMED", "HOLD", "CUE", "DECK", "STEP",
    };

    private static readonly HashSet<string> Facts = new(FactKeys, StringComparer.Ordinal);

    // The link's own lines (not commands): each answered by its own id, so no answer to one settles a command.
    private const string HelloId = "dh";
    private const string NonceId = "dn";
    private const string ProofId = "dp";
    private const string OffId = "do";
    private const string ContextId = "dc";

    /// <summary>The key an answer that names no id carries, so it settles no command waiting on this link.</summary>
    public const string NoId = "-";

    /// <summary>What every reader says of a STATE that came after the node's NONCE and before the proof: an older PlayMap greets so.</summary>
    public const string DiscardedWords = "a STATE before the proof, discarded as Show Bus 13.6.3 asks — this PlayMap greets before it pairs (an older build); nothing of it was taken";

    /// <summary>
    /// What every reader says of a STATE before this desk's proof when the node took HELLO and sent no NONCE: a node that asks this
    /// address for no token, or trusts it, counts it paired at connect and greets at once — which any build does, so no build is named.
    /// </summary>
    public const string DiscardedTrustedWords = "a STATE before this desk's proof, discarded as Show Bus 13.6.3 asks — the node asks this address for no token, or trusts it, and greeted at once; nothing of it was taken";

    /// <summary>What every reader says of a STATE that came before the node said anything of the pairing: nothing is known of why.</summary>
    public const string DiscardedEarlyWords = "a STATE before the proof, discarded as Show Bus 13.6.3 asks; nothing of it was taken";

    /// <summary>Why the last STATE before the proof came, as the readers add it to the count: after a NONCE, an older PlayMap's greeting.</summary>
    public const string OlderWhy = "this PlayMap greets with STATE before it pairs (an older build)";

    /// <summary>Why, when the node took HELLO and sent no NONCE: it asks this address for no token, or trusts it.</summary>
    public const string TrustedWhy = "a STATE before this desk's proof (the node asks this address for no token, or trusts it)";

    /// <summary>What every reader says once the node turned the Secret down.</summary>
    public const string WrongTokenWords = "wrong pairing token — the device's Secret is the node's pairing token (PlayMap: its Network drawer); nothing more is sent until it is typed again";

    /// <summary>What every reader says of a node that asks for a token this device has not been given.</summary>
    public const string NoTokenWords = "the node asks for its pairing token — type it in the device's Secret box (PlayMap: its Network drawer)";

    /// <summary>What a reader says of the pairing this round: the token's proof, level 0, until the node offers LINK on its line wire.</summary>
    public const string LevelWords = "paired by its pairing token (AUTH-PROOF, level 0) — PlayMap's line wire offers no LINK yet";

    private readonly string _secret;
    private readonly string _inst;
    private readonly string _name;
    private readonly string _ending;
    private long _n;
    private string _lastId = "";
    private bool _helloSent;
    private bool _proofSent;
    private bool _nonceAsked;
    private bool _nonceHeard;
    private bool _helloTaken;
    // The link's thread alone reads and writes the split's state; LinkMoved, on that thread too, starts each connection clean.
    private bool _skipping;
    // A connection moved (link thread) / the connection the proof was made on opened (desk thread): Up only while they agree.
    private long _linkGen;
    private long _openGen = -1;
    private int _oversize;

    /// <param name="secret">The node's pairing token, as typed in the device's Secret box ("" for none).</param>
    /// <param name="deskName">This desk's name, which the HELLO says after <c>Patterns</c> and the proof is made over.</param>
    /// <param name="inst">This process's instance (§2), which every request id ends in.</param>
    /// <param name="name">The device's name on this desk, which the words use for the node.</param>
    public ShowBusNodeSession(string? secret, string? deskName, string? inst, string? name = null, LineEnding ending = LineEnding.Lf)
    {
        _secret = secret ?? "";
        _inst = ShowBusPeers.IsInstance(inst) ? inst! : "00000000";
        _name = string.IsNullOrWhiteSpace(name) ? "the node" : name.Trim();
        _ending = DeviceLines.Ending(ending == LineEnding.None ? LineEnding.Lf : ending);
        HelloName = HelloNameFor(deskName);
        Words = "connecting";
    }

    public override DeviceProfile Profile => DeviceProfile.ShowBusNode;

    /// <summary>The name this desk says in HELLO and proves over: <c>Patterns</c> and the desk's name, in words the line grammar keeps whole.</summary>
    public string HelloName { get; }

    /// <summary>The node proved this link's pairing by its token (<c>OK paired</c> to the proof).</summary>
    public bool Paired { get; private set; }

    /// <summary>The node asks this desk for no token (<c>OK open</c>, or no NONCE and no Secret to prove): nothing was proved.</summary>
    public bool Open { get; private set; }

    /// <summary>
    /// The link is up for commands, facts and its context: paired or open on the connection open now, and not stopped. False from the
    /// moment a connection drops or a new one comes up, on the link's own thread, until the new one has said HELLO and been proved.
    /// </summary>
    public bool Up => (Paired || Open) && !Stopped && Volatile.Read(ref _openGen) == Interlocked.Read(ref _linkGen);

    /// <summary>The connection the session last heard of (it moves when one comes up and when one ends), which <see cref="OnOpenedAt"/> takes.</summary>
    public long LinkGeneration => Interlocked.Read(ref _linkGen);

    /// <summary>Nothing more is sent: a wrong token, or a token this device does not have, or one 13.6.8 does not prove.</summary>
    public bool Stopped { get; private set; }

    /// <summary>What the session says of the link now, in one line; never the Secret, the nonce or the proof.</summary>
    public string Words { get; private set; }

    /// <summary>STATE lines that came before the proof: discarded and counted (13.6.3), never taken.</summary>
    public int StateDiscarded { get; private set; }

    /// <summary>
    /// Why the last STATE before the proof came, in the readers' words — <see cref="OlderWhy"/> after the node's NONCE,
    /// <see cref="TrustedWhy"/> after it took HELLO with no NONCE, "" when nothing was known yet (or none was discarded).
    /// </summary>
    public string DiscardedWhy { get; private set; } = "";

    /// <summary>STATE lines taken after the proof (a newer PlayMap's greeting comes right after <c>OK paired</c>).</summary>
    public int StateTaken { get; private set; }

    /// <summary>Any other line that came before the proof (a context): discarded and counted, never taken.</summary>
    public int Discarded { get; private set; }

    /// <summary>Lines past <see cref="MaxLineChars"/>, or a context or STATE past <see cref="MaxKeptBytes"/>: skipped whole and counted (either thread).</summary>
    public int Oversize => Volatile.Read(ref _oversize);

    /// <summary>The node's last STATE taken after the proof, its text kept (latest wins, at most <see cref="MaxKeptBytes"/>); null for none.</summary>
    public string? LastState { get; private set; }

    /// <summary>The node's context as this desk keeps it, taken only from a proved link (latest wins); null before one came.</summary>
    public KeptContext? Context { get; private set; }

    /// <summary>The last command's outcome in 13.12.2's word and its words ("" before any): what the device card and STATE show.</summary>
    public string LastOutcome { get; private set; } = "";

    /// <summary>A connection the session has opened on (HELLO written) since it was made.</summary>
    public bool Connected => _helloSent;

    /// <summary>
    /// <c>Patterns</c> and the desk's name as HELLO says it and the proof is made over (13.6.8 keys the proof by the name the node
    /// heard): letters, digits, '-', '_', '.' and single spaces kept, anything else a '-' so no quote, backslash, '=' or line break reaches
    /// the line, at most 40 characters after the word.
    /// </summary>
    public static string HelloNameFor(string? deskName)
    {
        var sb = new StringBuilder();
        foreach (var c in (deskName ?? "").Trim())
        {
            if (sb.Length >= 40) break;
            var ch = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : c == ' ' ? ' ' : '-';
            if (ch == ' ' && sb.Length > 0 && sb[^1] == ' ') continue;   // one space between words, as the line grammar reads them
            sb.Append(ch);
        }
        var desk = sb.ToString().Trim(' ', '-');
        return desk.Length == 0 ? "Patterns" : "Patterns " + desk;
    }

    public override bool TakesFacts => Up;

    public override bool LinesAreCommands => false;

    /// <summary>A fresh connection: nothing proved on it yet. A stop for a wrong token outlives it — the Secret must be typed again.</summary>
    public override void OnConnected()
    {
        // The connection's own start is OnOpened, in order with its lines; the reconcile's late notice of it changes nothing here.
    }

    /// <summary>
    /// On the link's own thread, before a new connection is published and when one ends: the proof of the last connection no longer
    /// holds, so nothing more is sent until the next one is proved, and the split starts the next connection's bytes clean.
    /// </summary>
    public long LinkMoved()
    {
        _skipping = false;
        return Interlocked.Increment(ref _linkGen);
    }

    /// <summary>The link dropped (the desk's thread saw it closed): nothing is proved until a new connection is.</summary>
    public void Dropped()
    {
        Paired = false;
        Open = false;
    }

    /// <summary>The connection the link said came up as <paramref name="generation"/> opens: its HELLO, and nothing proved on it yet.</summary>
    public IReadOnlyList<byte[]> OnOpenedAt(long generation)
    {
        Volatile.Write(ref _openGen, generation);
        return Opening();
    }

    /// <summary>The connection open now opens (a link that does not say when one moves): its HELLO, and nothing proved on it yet.</summary>
    public override IReadOnlyList<byte[]> OnOpened()
    {
        Volatile.Write(ref _openGen, Interlocked.Read(ref _linkGen));
        return Opening();
    }

    private IReadOnlyList<byte[]> Opening()
    {
        Paired = false;
        Open = false;
        _proofSent = false;
        _nonceAsked = false;
        _nonceHeard = false;
        _helloTaken = false;
        _helloSent = true;
        if (!Stopped) Words = "connected — waiting for the node's NONCE";
        return One(Line($"@{HelloId} HELLO {HelloName}"));
    }

    /// <summary>A command, tagged with its own id (13.12.1); not-sent, with why, before the proof or after a stop (13.12.3).</summary>
    public override IReadOnlyList<byte[]> Encode(string words, out string problem)
    {
        problem = "";
        var line = (words ?? "").Trim();
        if (line.Length == 0) return None("Nothing to send — the node's own verb, e.g. PLAYLIST GO show.", out problem);
        if (line.Contains('\n') || line.Contains('\r')) return None("not-sent — one line is one command; this holds a line break", out problem);
        if (line.StartsWith('@')) return None("not-sent — the device gives each command its own id; send the line without one", out problem);
        if (Stopped) return None("not-sent — " + Words, out problem);
        if (!Up && (Paired || Open)) return None("not-sent — the link is not live — its connection moved; waiting for its proof on the next one, and nothing is queued", out problem);
        if (!Up) return None($"not-sent — the link is not live — waiting for its proof ({_name}: {Words}); nothing is queued", out problem);
        _lastId = PeerLink.RequestId(IdPrefix, ++_n, _inst);
        return One(Line($"@{_lastId} {line}"));
    }

    /// <summary>The id the line just encoded carries, which its receipt names.</summary>
    public override string SentKey(string words) => _lastId;

    /// <summary>
    /// One of the show's facts, untagged, once the link is up — and only one the node takes as a fact (its first word one of
    /// <see cref="FactKeys"/>, BLACKOUT only as 0 or 1, no quote mark its grammar would read as an unclosed phrase). Any other goes
    /// nowhere and says nothing: the arrival's OPEN 1 is the desk's word for a board, not a node's.
    /// </summary>
    public override IReadOnlyList<byte[]> EncodeFact(string words, out string problem)
    {
        problem = "";
        var line = (words ?? "").Trim();
        if (!Up || !IsFact(line)) return Array.Empty<byte[]>();
        return One(Line(line));
    }

    /// <summary>Whether the node takes this line as a fact: its first word a fact key, BLACKOUT only with 0 or 1, one line, no quote mark.</summary>
    public static bool IsFact(string line)
    {
        if (line.Length == 0 || line.Contains('"') || line.Contains('\n') || line.Contains('\r') || line.StartsWith('@')) return false;
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var key = space < 0 ? line : line[..space];
        if (!Facts.Contains(key)) return false;
        return key != "BLACKOUT" || line is "BLACKOUT 0" or "BLACKOUT 1";
    }

    public override string Unanswered(TimeSpan timeout, ConfirmLevel reached, ConfirmLevel wanted)
        => "unconfirmed — no answer in " + timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s — it may have run; look at its state";

    /// <summary>The words of a command whose link dropped before the node answered it (13.12.3).</summary>
    public string DroppedWords => "unconfirmed — the link dropped before " + _name + " answered";

    /// <summary>A connection moved since the one open now on the desk's thread opened: the lines read now are a gone connection's.</summary>
    private bool Stale => Volatile.Read(ref _openGen) != Interlocked.Read(ref _linkGen);

    public override ProfileReply OnReceived(string text)
    {
        var t = (text ?? "").Trim();
        // A line of a connection that has ended: an answer to a command still settles it (the node said what it did), but nothing
        // of the link's own is read — no proof, no context, no STATE — and nothing is sent onto the next connection before its HELLO.
        if (Stale && !(t.StartsWith("OK", StringComparison.Ordinal) || t.StartsWith("ERR", StringComparison.Ordinal))) return Quiet("a line of a connection that has ended — not read");
        if (t.StartsWith("NONCE ", StringComparison.Ordinal)) return OnNonce(t[6..].Trim());
        if (t.StartsWith("STATE ", StringComparison.Ordinal) || t == "STATE") return OnState(t.Length > 6 ? t[6..] : "");
        if (t.StartsWith("CONTEXT ", StringComparison.Ordinal) || t == "CONTEXT") return OnContext(t.Length > 8 ? t[8..] : "");
        if (t.StartsWith("OK", StringComparison.Ordinal) || t.StartsWith("ERR", StringComparison.Ordinal)) return OnAnswer(t);
        return new ProfileReply(Short(t), false, Array.Empty<byte[]>(), false, NoId);
    }

    private ProfileReply OnNonce(string nonce)
    {
        _nonceHeard = true;
        if (Up || Stopped || _proofSent || !_helloSent) return Quiet("a NONCE the link does not need now — not answered");
        return Prove(nonce);
    }

    /// <summary>13.6.8: one proof for a nonce of exactly 32 lowercase hex, keyed as 1.1 desks key the token; any other stops the link with why.</summary>
    private ProfileReply Prove(string nonce)
    {
        if (!PeerLink.IsProvableNonce(nonce)) return Stop("a NONCE that is not 32 lowercase hex is never answered with a proof (Show Bus 13.6.8)");
        if (_secret.Trim().Length == 0) return Stop(NoTokenWords);
        if (PeerLink.LegacyKey(_secret) is null) return Stop(PeerLink.InvisibleWords);
        var proof = PeerLink.LegacyProof(_secret, nonce, HelloName)!;
        _proofSent = true;
        Words = "proving — its pairing token answers the node's NONCE";
        return new ProfileReply("proving", false, One(Line($"@{ProofId} AUTH-PROOF {proof}")), false, NoId);
    }

    private ProfileReply OnState(string json)
    {
        if (!Up)
        {
            StateDiscarded++;
            DiscardedWhy = _nonceHeard ? OlderWhy : _helloTaken ? TrustedWhy : "";
            return Quiet(_nonceHeard ? DiscardedWords : _helloTaken ? DiscardedTrustedWords : DiscardedEarlyWords);
        }
        if (json.Length > MaxKeptBytes)
        {
            Interlocked.Increment(ref _oversize);
            return Quiet("a STATE past " + (MaxKeptBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB — skipped whole, counted");
        }
        StateTaken++;
        LastState = json;
        return Quiet("its STATE, taken after the proof");
    }

    private ProfileReply OnContext(string json)
    {
        if (!Up)
        {
            Discarded++;
            return Quiet("a context before the proof — discarded as Show Bus 13.6.3 asks");
        }
        if (json.Length > MaxKeptBytes)
        {
            Interlocked.Increment(ref _oversize);
            return Quiet("a context past " + (MaxKeptBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB — skipped whole, counted");
        }
        if (ShowContext.Parse(json) is not { } context)
        {
            Interlocked.Increment(ref _oversize);
            return Quiet("a context this desk cannot read — skipped, counted");
        }
        Context = KeptContext.Next(Context, context);
        return Quiet($"its context — {context.Items.Count.ToString(CultureInfo.InvariantCulture)} things, {context.Light}");
    }

    private ProfileReply OnAnswer(string line)
    {
        if (PeerLink.ReadLine(line) is not { } read) return Quiet(Short(line));
        if (read.Id is not { } id) return new ProfileReply(Short(line), false, Array.Empty<byte[]>(), false, NoId);
        var ok = line.StartsWith("OK", StringComparison.Ordinal);
        if (Stale && id is HelloId or NonceId or ProofId or OffId or ContextId) return Quiet("an answer on a connection that has ended — not read");
        switch (id)
        {
            case HelloId:
                if (!ok) return Quiet("the node did not take HELLO: " + Short(read.Reading.Words));
                _helloTaken = true;
                if (Up || Stopped || _proofSent || _nonceAsked) return Quiet("HELLO taken");
                if (_secret.Trim().Length == 0)
                {
                    // No NONCE came and none can be proved: the node asks this desk for no token (an open node, or this address
                    // trusted). Nothing was proved; every reader says so.
                    return GoUp(open: true, "the node asks this desk for no token — nothing was proved; anything at its address could answer");
                }
                _nonceAsked = true;
                return new ProfileReply("asking the node for a NONCE", false, One(Line($"@{NonceId} NONCE")), false, NoId);
            case NonceId:
                if (ok) _nonceHeard = true;
                if (!ok) return Stop("the node gave no NONCE to prove: " + Short(read.Reading.Words));
                return Up || Stopped || _proofSent ? Quiet("a NONCE the link does not need now") : Prove(read.Reading.Words.Trim());
            case ProofId:
                if (ok && read.Reading.Words.StartsWith("paired", StringComparison.Ordinal)) return GoUp(open: false, LevelWords);
                if (ok && read.Reading.Words.StartsWith("open", StringComparison.Ordinal)) return GoUp(open: true, "the node asks for no token — nothing was proved; anything at its address could answer");
                return Stop(read.Reading.Words.Contains("too many", StringComparison.OrdinalIgnoreCase)
                    ? "the node closed its door to this address for its wrong tokens — " + WrongTokenWords
                    : WrongTokenWords);
            case OffId:
                return Quiet(ok ? "its STATE pushes off" : "the node did not turn its pushes off: " + Short(read.Reading.Words));
            case ContextId:
                return Quiet(ok ? "its context subscribed" : "the node gives no context to this desk: " + Short(read.Reading.Words));
        }
        var r = read.Reading;
        LastOutcome = r.Outcome.Word() + (r.Words.Length > 0 ? " — " + Short(r.Words) : "");
        return r.Outcome switch
        {
            LinkOutcome.Refused => ProfileReply.Refused("refused — " + Short(r.Words), id) with { Outcome = LinkOutcome.Refused },
            LinkOutcome.Accepted => ProfileReply.Acked("accepted, not run yet" + (r.Words.Length > 0 ? " — " + Short(r.Words) : ""), id) with { Outcome = LinkOutcome.Accepted },
            LinkOutcome.Applied => ProfileReply.Acked("applied" + (r.Words.Length > 0 ? " — " + Short(r.Words) : ""), id) with { Outcome = LinkOutcome.Applied },
            _ => new ProfileReply(LastOutcome, false, Array.Empty<byte[]>(), false, id),
        };
    }

    /// <summary>The link is up: the node's STATE pushes off (a newer node greets once more with STATE after the proof), its context on.</summary>
    private ProfileReply GoUp(bool open, string words)
    {
        Paired = !open;
        Open = open;
        Words = words;
        return new ProfileReply(open ? "open" : "paired", false, new[] { Bytes(Line($"@{OffId} SUBSCRIBE OFF")), Bytes(Line($"@{ContextId} SUBSCRIBE CONTEXT")) }, false, NoId);
    }

    private ProfileReply Stop(string words)
    {
        Stopped = true;
        Paired = false;
        Open = false;
        Words = words;
        return new ProfileReply(words, true, Array.Empty<byte[]>(), false, NoId);
    }

    private static ProfileReply Quiet(string words) => new(words, false, Array.Empty<byte[]>(), false, NoId);

    private string Line(string text) => text + _ending;

    private static string Short(string s) => s.Length <= 200 ? s : s[..200] + "…";

    /// <summary>
    /// Whole lines out of the stream so far. A node's context and STATE are long lines, so a line is read whole up to
    /// <see cref="MaxLineChars"/>; one longer is skipped to its end and counted, and nothing of it is read as a line. On the link's
    /// own thread only, as <see cref="LinkMoved"/> is, so a line cut by a dropped connection never swallows the next one's first.
    /// </summary>
    public override IReadOnlyList<string> Split(StringBuilder buffer)
    {
        var lines = new List<string>();
        var start = 0;
        var at = 0;
        foreach (var chunk in buffer.GetChunks())
        {
            var span = chunk.Span;
            for (var i = 0; i < span.Length; i++, at++)
            {
                if (span[i] is not ('\n' or '\r')) continue;
                if (_skipping) _skipping = false;
                else if (at > start)
                {
                    var line = buffer.ToString(start, at - start).Trim();
                    if (line.Length > 0) lines.Add(line);
                }
                start = at + 1;
            }
        }
        var tail = buffer.Length - start;
        if (_skipping || tail > MaxLineChars)
        {
            if (!_skipping) Interlocked.Increment(ref _oversize);
            _skipping = true;
            buffer.Clear();
        }
        else buffer.Remove(0, start);
        return lines;
    }

    /// <summary>
    /// The link's record (Show Bus 1.3 §13.16.3) as every reader draws it: carrier <c>line</c>, level 0, plain; live once paired,
    /// <c>legacy</c> amber (P-legacy), or <c>open</c> amber when nothing was proved; refused after a stop; connecting before.
    /// <paramref name="open"/>: the connection is up now. Nothing in it is a secret.
    /// </summary>
    public LinkRecord Record(bool enabled, bool open, string peerId = "")
    {
        var state = !enabled ? LinkState.Off : Stopped ? LinkState.Refused : open && Up ? LinkState.Live : open ? LinkState.Connecting : _helloSent ? LinkState.Down : LinkState.Connecting;
        var flags = state == LinkState.Live ? (Paired ? new[] { "legacy" } : new[] { "open" }) : Array.Empty<string>();
        var light = state switch
        {
            LinkState.Off => CheckLight.Grey,
            LinkState.Live => CheckLight.Amber,
            LinkState.Refused or LinkState.Down => CheckLight.Red,
            _ => CheckLight.Amber,
        };
        var words = state switch
        {
            LinkState.Off => LinkRecord.OffWords,
            LinkState.Live when Paired => _name + " cannot prove itself yet — an older build; anything at its address could answer",
            LinkState.Live => _name + " asks this desk for no token — anything at its address could answer",
            LinkState.Refused => _name + ": " + Words,
            LinkState.Down => _name + " is not answering — its link is down",
            _ => _name + ": " + Words,
        };
        return new LinkRecord("playmap", peerId.Length > 0 ? peerId : null, _name, "line", 0, "plain", "", Paired && state == LinkState.Live ? "legacy" : Open && state == LinkState.Live ? "open" : "none",
            "", "none", LinkRecord.StateWord(state), Stopped ? "key" : "", "unknown", flags, LinkRecord.LightWord(light), LinkRecord.MarkOf(light), words,
            0, null, 0, null, default, new LinkCounts(0, 0, 0, Oversize, 0, 0), null);
    }
}

/// <summary>
/// One Show Bus node device as every reader takes it (the Nodes page, the Eye, STATE, the brief): where it is, whether its link is
/// up and proved, its context, its record and what the session counted. Built on the devices' thread, read anywhere. No secret.
/// </summary>
public sealed record ShowBusNodeSight(string DeviceId, string DeviceName, string Host, int Port, bool Enabled, bool Connected, bool Paired, bool Open,
    bool Stopped, KeptContext? Context, LinkRecord Record, string Words, int StateDiscarded, int StateTaken, int Discarded, string LastOutcome,
    bool Proved = true, string DiscardedWhy = "")
{
    /// <summary>The link is up for facts and the context: paired or open on the connection open now, connected, and not stopped.</summary>
    public bool Up => Connected && Proved && (Paired || Open) && !Stopped;

    /// <summary>The node's id its context names ("" before one came).</summary>
    public string ContextId => Context?.Context.Node.Id ?? "";

    /// <summary>The words every reader adds for what the session discarded before the proof ("" when nothing was).</summary>
    public string DiscardedLine => StateDiscarded + Discarded == 0 ? ""
        : string.Create(CultureInfo.InvariantCulture, $"{StateDiscarded + Discarded} line{(StateDiscarded + Discarded == 1 ? "" : "s")} before the proof discarded (Show Bus 13.6.3)")
          + (StateDiscarded > 0 && DiscardedWhy.Length > 0 ? " — " + DiscardedWhy : "");

    /// <summary>STATE's row for the device, at the end of its device row: no secret, no proof, no nonce.</summary>
    public object Row() => new
    {
        level = 0,
        pairing = Paired ? "auth-proof" : Open ? "open" : "none",
        up = Up,
        stopped = Stopped,
        words = Words,
        contextId = ContextId,
        contextRev = Context?.Rev ?? 0,
        stateDiscarded = StateDiscarded,
        stateTaken = StateTaken,
        discarded = Discarded,
        discardedWhy = DiscardedWhy,
        lastOutcome = LastOutcome,
        link = Record.ToElement(),
    };

    /// <summary>A sight to compare readers' keys with: what is drawn, not when.</summary>
    public string Face => string.Join("|", DeviceId, DeviceName, Host, Port.ToString(CultureInfo.InvariantCulture), Up, Stopped, Words, ContextId, (Context?.Rev ?? 0).ToString(CultureInfo.InvariantCulture), Record.Face,
        StateDiscarded.ToString(CultureInfo.InvariantCulture), Discarded.ToString(CultureInfo.InvariantCulture), LastOutcome);
}
