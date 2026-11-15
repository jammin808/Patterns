using Patterns.Core.Menus;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>The bands of the God's Eye, top to bottom: what tells the desk what to do, the pictures, the sound, the room.</summary>
public enum EyePlane
{
    Control,
    Video,
    Audio,
    Room,
    /// <summary>
    /// The band the other apps are drawn on, above CONTROL — a layout band only (<see cref="EyeNode.Band"/>): no thing's
    /// <see cref="EyeNode.Plane"/> is NETWORK. The NETWORK lens (<see cref="EyeLens.Network"/>) reads kinds, not this band: every
    /// app card, every link between them and the desk.
    /// </summary>
    Network,
}

/// <summary>What a node of the Eye is.</summary>
public enum EyeKind
{
    Desk,
    Source,
    Screen,
    Display,
    NdiSend,
    FarEnd,
    Device,
    Deck,
    Companion,
    Peers,
    Osc,
    Stack,
    Assistant,
    Twin,
    Node,
    AudioSource,
    AudioOut,
    Room,
    Stream,
    /// <summary>Round 87: another show app heard on the show network (Stagewatch, AGMessage, a Slides agent).</summary>
    ShowApp,
    /// <summary>Round 87: one of another app's own things, drawn from the context it publishes (a Stagewatch's timer, its departments).</summary>
    ShowAppItem,
}

/// <summary>What a link of the Eye is: the word between the two things it joins.</summary>
public enum EyeEdgeKind
{
    Feeds,
    Shows,
    Drives,
    Carries,
    Controls,
    Mirrors,
    Follows,
    Hears,
    Routes,
    Serves,
    Sends,
}

/// <summary>A lens over the picture: everything, one band with the desk, the problems and what they touch, or the show network.</summary>
public enum EyeLens
{
    All,
    Video,
    Control,
    Audio,
    Room,
    Problems,
    /// <summary>The show network: every app card and every other Patterns node (<see cref="EyeGraph.IsNetworkCard"/>), the desk, and every link between them.</summary>
    Network,
}

// ---- the facts the desk hands in ------------------------------------------------------------

/// <summary>
/// Everything the Eye is built from (round 66), gathered by the desk once a second and handed in
/// whole: the core never reaches for a service, so the graph is built and tested without a desk.
/// Every list may be empty; a thing that is absent is simply not drawn.
/// </summary>
public sealed class EyeFacts
{
    public string MachineName { get; init; } = "";
    public string Build { get; init; } = "";
    public bool OutputsLive { get; init; }
    /// <summary>Round 78: this desk is the second on its show folder — the first one saves the show and keeps its recovery record; this one takes the folder over when it leaves.</summary>
    public bool SecondDesk { get; init; }
    /// <summary>The super-check's overall light — the desk's own.</summary>
    public CheckLight Health { get; init; } = CheckLight.Grey;
    public string HealthWords { get; init; } = "";
    /// <summary>Round 69: what the desk holds in memory and why, and the GPU cache's bound — one line, stable from tick to tick ("4 held: 2 on air, 1 armed, 1 idle · GPU cache limit 128 MB · rung none").</summary>
    public string MemoryWords { get; init; } = "";

    /// <summary>Round 80: the master rate as the show runs it (MasterRate.Words) — "" when the desk does not say.</summary>
    public string MasterRate { get; init; } = "";
    /// <summary>The super-check's rows that are not green, as words (<see cref="AttentionWords"/>).</summary>
    public IReadOnlyList<string> Attention { get; init; } = Array.Empty<string>();

    /// <summary>
    /// A Super Check row that asks for attention, as the Eye's desk says it: its item, its light's mark (the shape beside the colour the
    /// node is drawn in) and its note — never the reading it measures, which the Super Check and the Machine page read live, so a reading
    /// that moves by the tick moves no picture, with a note or without one.
    /// </summary>
    public static string AttentionWords(CheckRow row)
        => $"{row.Item}: {LinkRecord.MarkOf(row.Light)}" + (row.Note.Length > 0 ? " " + row.Note : "");
    /// <summary>Round 67.6: what the next TAKE alone arrives by, in words; "" for the show's own.</summary>
    public string NextTake { get; init; } = "";
    /// <summary>Round 67.8: the wall's take scope as the picker labels it ("every screen", "the focused screen", "the ticked screens"); "" on a node.</summary>
    public string TakeScope { get; init; } = "";
    /// <summary>Round 67.8: what the next TAKE will do — the plan's words ("→ 1 · Left, 2 · Right · 1 outside the scope keeps its picture") or its refusal; "" on a node.</summary>
    public string TakeWords { get; init; } = "";
    /// <summary>Round 72: a TAKE waiting under a video sting — the ticket the press froze, in words ("→ 1 · Left, 2 · Right when 'Whoosh' ends"); "" with none.</summary>
    public string Landing { get; init; } = "";
    /// <summary>Round 79: the last take this desk ran, as a fact row in words ("TAKE 20:31:05 — the pictures changed · outputs off — unseen"); "" before the first.</summary>
    public string LastTake { get; init; } = "";
    /// <summary>Round 73: what the desk is editing, in words ("Right's preview (its own picture) · Fractal — Fractals page · library: Mandelbrot"); "" on a node.</summary>
    public string Editing { get; init; } = "";
    /// <summary>Round 73: the lower third on screen, in words ("'Keynote' — Jane Doe · leaves in 3 s", "… · until hidden"); "" with none.</summary>
    public string LowerThird { get; init; } = "";
    /// <summary>Round 73: MIDI learn and the surfaces' map in words — "learning LOOK Walk-in — press a control", "12 controls bound on APC40"; "" with no surface in the show.</summary>
    public string Midi { get; init; } = "";
    /// <summary>Round 74: where the desk is, in words ("On the Cues page · settings column: SELECTED CUE · 03.020"); "" on a node.</summary>
    public string Nav { get; init; } = "";
    public IReadOnlyList<EyeDisplay> Displays { get; init; } = Array.Empty<EyeDisplay>();
    public IReadOnlyList<EyeScreen> Screens { get; init; } = Array.Empty<EyeScreen>();
    public IReadOnlyList<EyeSource> Sources { get; init; } = Array.Empty<EyeSource>();
    public IReadOnlyList<EyeDevice> Devices { get; init; } = Array.Empty<EyeDevice>();
    public IReadOnlyList<EyeDeck> Decks { get; init; } = Array.Empty<EyeDeck>();
    /// <summary>Show Bus 1.3 §13.16.5: the acceptor's list — the app links this desk's wire accepted that LINK verified, never decks.</summary>
    public IReadOnlyList<AppLinkRow> AppLinks { get; init; } = Array.Empty<AppLinkRow>();
    public IReadOnlyList<EyeCompanion> Companions { get; init; } = Array.Empty<EyeCompanion>();
    /// <summary>Open TCP control connections beyond the decks that said HELLO.</summary>
    public int WireClients { get; init; }
    /// <summary>Open HTTP connections — pages, phones, a browser's poll.</summary>
    public int WebClients { get; init; }
    public EyeOsc? Osc { get; init; }
    public EyeTwin? Twin { get; init; }
    public IReadOnlyList<EyeNodeHeard> Nodes { get; init; } = Array.Empty<EyeNodeHeard>();
    public EyeRoom? Room { get; init; }
    public EyeStream? Stream { get; init; }
    public IReadOnlyList<EyeNdiSend> NdiSends { get; init; } = Array.Empty<EyeNdiSend>();
    public IReadOnlyList<EyeAudioSource> AudioSources { get; init; } = Array.Empty<EyeAudioSource>();
    public IReadOnlyList<EyeAudioOut> AudioOuts { get; init; } = Array.Empty<EyeAudioOut>();
    public IReadOnlyList<EyeAudioRoute> AudioRoutes { get; init; } = Array.Empty<EyeAudioRoute>();
    public EyeStack? Stack { get; init; }
    public EyeAssistant? Assistant { get; init; }
    /// <summary>
    /// Round 87: the other show apps heard on the show network's hello (UDP 9928), with what each one's context says when this desk
    /// reads it — the desk's one composition (<see cref="ShowBusView.Compose"/>). Left out of the hash wherever <see cref="AppsRev"/>
    /// stands for it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<EyeShowApp> ShowApps { get; init; } = Array.Empty<EyeShowApp>();

    /// <summary>
    /// The apps' revision as the desk's composition kept it (<see cref="ShowBusAppsRevision"/>): it moves when what any app's steady
    /// projection says moves, and the hash reads it in the apps' place; 0 for apps handed in with no revision, which
    /// <see cref="ShowAppsUnrevised"/> then stands for.
    /// </summary>
    public long AppsRev { get; init; }

    /// <summary>What the hash reads of apps handed in with no revision (built by hand): the apps themselves; null whenever a revision stands for them.</summary>
    public IReadOnlyList<EyeShowApp>? ShowAppsUnrevised => AppsRev != 0 ? null : ShowApps;

    /// <summary>
    /// This desk's own Show Bus id: another app's end <c>@&lt;it&gt;</c> is this desk, and <c>@&lt;it&gt;/&lt;item&gt;</c> the thing of
    /// this desk's that its context exports by that id (<see cref="EyeNetwork.ShortId"/>); "" when the desk has none to say.
    /// </summary>
    public string ShowBusId { get; init; } = "";

    /// <summary>
    /// The facts' steady projection — what the desk compares, once a second, to know whether the picture moved (the Eye design's
    /// D9): every fact the graph is built from, with each other app's context as its revision (<see cref="EyeShowApp.ContextRev"/>)
    /// and each link as its record's steady face (<see cref="EyeShowApp.LinkFace"/>). No context is serialised here, and a link heard
    /// again, counting down to a redial or taking a clock sample leaves it as it was.
    /// </summary>
    public string Key() => JsonUtil.SerializeCompact(this);
}

/// <summary>
/// Round 87: another show app — its kind's words ("Stagewatch"), its id, its name, where it stands on the network
/// (live, stale, gone), whether this desk is linked to it, and — when its context is read — its light, its headline,
/// its things and its own peers. Its things are drawn as its own nodes; its light is its own context's, never a guess.
/// </summary>
public sealed record EyeShowApp(string Id, string KindWords, string Name, ShowBusState State, bool Linked, string Words)
{
    /// <summary>
    /// Its §8 kind as its hello or the link that proved it says it (<c>main</c>, <c>playmap</c>, <c>messenger</c>…), "" where neither
    /// does — what a key made for it is bound to beside its id, read from the hello and never back from <see cref="KindWords"/>.
    /// </summary>
    public string Kind { get; init; } = "";

    /// <summary>
    /// Its context as this desk shows it (its moment left out). Left out of the Eye's hash: <see cref="ContextRev"/> stands for it
    /// there, so the once-a-second tick never serialises another app's context to learn whether it moved (the Eye design's D9) —
    /// the revision is reckoned once per arrival, on the thread that received the context (the side-call lane for a Stagewatch,
    /// the desk's thread for a twin node's, every 5 s).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ShowContext? Context { get; init; }

    /// <summary>
    /// The context's revision as its receiver kept it (<see cref="KeptContext"/>): it moves when what the context says moves, and
    /// is what the Eye's hash reads in its place. 0 for an app this desk reads no context of — or one handed in without a
    /// revision, which <see cref="ContextUnrevised"/> then stands for.
    /// </summary>
    public long ContextRev { get; init; }

    /// <summary>
    /// What the hash reads of a context handed in with no revision (an app built by hand): its text, so a picture is never left
    /// stale for want of a revision; "" whenever a revision stands for it — every receiver on the desk keeps one.
    /// </summary>
    public string ContextUnrevised => ContextRev != 0 || Context is null ? "" : Context.ToJson();

    /// <summary>
    /// Round 89: linked and live, and the app says its show is stuck — the link stays live and amber, the card the worse
    /// of its own light and amber, and its counting things carry no time (<see cref="ShowBusView.NoTime"/>).
    /// </summary>
    public bool Stuck { get; init; }

    /// <summary>
    /// Round 91: linked, what the app made of this desk's key (<see cref="StagewatchKey"/>) — a refusal, a key that speaks
    /// for no node of this desk's, or P-legacy's fallback, each said by every reader and amber while the link is live.
    /// </summary>
    public StagewatchKey Key { get; init; } = StagewatchKey.Unknown;

    /// <summary>
    /// Show Bus 1.3 §2: two running nodes say this app's id — the words naming both machines and their addresses ("" when
    /// none). Every reader shows it amber while the app is live; this desk dials no new link to it while it stands.
    /// </summary>
    public string Conflict { get; init; } = "";

    /// <summary>
    /// Show Bus 1.3 §13.16.3: linked, the link's record as its connector published it — the light, mark and words every reader
    /// draws for the link (13.16.4); null for an app this desk does not link to. Left out of the Eye's hash: its counts and ages
    /// move every read; <see cref="LinkFace"/> stands for it there.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public LinkRecord? Link { get; init; }

    /// <summary>
    /// What the Eye's hash reads of the link: its record's steady projection (<see cref="LinkRecord.Face"/>) — what the Eye draws of
    /// it and nothing that runs on (<c>heardMs</c>, <c>nextMs</c>, the attempt, the clock's age and offset, the counts); "" with none.
    /// </summary>
    public string LinkFace => Link?.Face ?? "";

    /// <summary>§6's <c>heard</c>: its hello's age — <c>live</c>, <c>stale</c> or <c>gone</c> — or "" when no hello names it.</summary>
    public string Heard { get; init; } = "";

    /// <summary>
    /// Linked, and the stage follows it: where this desk's last speaker message went and what came of it, in one line
    /// (<see cref="StageSpeakerWords.Line"/>); "" with nothing to say.
    /// </summary>
    public string Speaker { get; init; } = "";

    /// <summary>
    /// The show needs this app (profile 13.1 for this desk: the Stagewatch the stage follows, or one of this desk's own
    /// nodes): its card is counted by the rail, the worst, the problems queue's head and the headline.
    /// </summary>
    public bool Needed { get; init; }

    /// <summary>
    /// Round 96 (Show Bus 1.3 §12): linked, the windows it grants this desk in one line — each by name, where the air shows it and
    /// whether it is ready; "" with none. Never an address.
    /// </summary>
    public string Windows { get; init; } = "";

    /// <summary>
    /// Round 97: a Show Bus node this desk drives as a device (PlayMap) — the device's id on the Interactive page, and its name, which
    /// the brief names as the way to drive it; "" for an app no device of this desk's links to.
    /// </summary>
    public string DeviceId { get; init; } = "";

    /// <summary>Round 97: the device's name (<see cref="DeviceId"/>, or <see cref="DeviceAt"/>), "" for none.</summary>
    public string Device { get; init; } = "";

    /// <summary>
    /// Round 97: a heard PlayMap no device folds into, but at whose address and wire port a node device of this desk's is set — that
    /// device's id, so every reader offers it and never a second one; "" for none. Not a link: nothing is drawn from it to the app.
    /// </summary>
    public string DeviceAt { get; init; } = "";

    /// <summary>Round 97: the words a node device's session counted before the proof — discarded, never taken (Show Bus 13.6.3); "" for none.</summary>
    public string Discarded { get; init; } = "";

    /// <summary>
    /// The addresses its hellos came from (Show Bus 1.3 §2), the one dialled first; none for an app no hello names. Where to dial,
    /// never who: a deck's words name the app heard at its address, and nothing folds by an address alone.
    /// </summary>
    public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Show Bus 1.3 §13.16.5: the links this desk's wire accepted that LINK verified to this app's id — open, or ended within
    /// <c>peer.forget.ms</c> — whatever its kind (<see cref="ShowBusView.Accept"/>). An open one is a link this desk holds
    /// (<see cref="ShowBusView.HoldsLink"/>): LINK proved the id, so it says the app is there where no link this desk dials does.
    /// Steady rows: the hash reads them as they are.
    /// </summary>
    public IReadOnlyList<AppLinkRow> Accepted { get; init; } = Array.Empty<AppLinkRow>();
}

/// <summary>A display Windows shows, or one the show plans and has not plugged in, or one that went missing.</summary>
public sealed record EyeDisplay(string Id, string Label, int Width, int Height, int Hz, bool Primary, bool Planned, bool Missing);

/// <summary>A screen of the rig: its display, its picture's sources, and the three witnesses' verdicts.</summary>
public sealed class EyeScreen
{
    public string Id { get; init; } = "";
    /// <summary>The wire's number for it ("2"), "" when it has none.</summary>
    public string Number { get; init; } = "";
    public string Label { get; init; } = "";
    /// <summary>The display it is placed on (an <see cref="EyeDisplay.Id"/>); "" when planned with none.</summary>
    public string DisplayId { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool OnAir { get; init; }
    public bool Own { get; init; }
    /// <summary>Its own picture in the preview that the audience has not seen yet (round 67).</summary>
    public bool Staged { get; init; }
    /// <summary>The contract's words, "" when none.</summary>
    public string Contract { get; init; } = "";
    /// <summary>MATCH, MISMATCH, UNVERIFIED or "" when there is no contract.</summary>
    public string Verdict { get; init; } = "";
    /// <summary>The signal report's brief line.</summary>
    public string SignalWords { get; init; } = "";
    public bool TestRoute { get; init; }
    /// <summary>What the far end says it receives, "" when nobody said.</summary>
    public string Received { get; init; } = "";
    /// <summary>Grey when nobody said, green when the far end agrees with the contract, red when it does not.</summary>
    public CheckLight ReceivedLight { get; init; } = CheckLight.Grey;
    /// <summary>The input keys its picture draws from ("ndi:Cam 1", "cap:…", "web:…", "media:…").</summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
    /// <summary>Round 77: what its picture has done to it — "mirrored, turned 90°" — or "" for a picture as it came.</summary>
    public string Adjustments { get; init; } = "";
    public string Role { get; init; } = "";
    /// <summary>Round 67.8: LOCKED — it keeps its picture through looks, cues, every take and a sting.</summary>
    public bool Locked { get; init; }
    /// <summary>Round 67.8: ARM — the next CUT / TAKE changes it; false is held through it.</summary>
    public bool Armed { get; init; } = true;
    /// <summary>Round 67.8: the tick at the top of its tile — a TICKED take, a fade or SEND TO TICKED reads it.</summary>
    public bool Ticked { get; init; }
    /// <summary>Round 67.8: the joined canvas it renders through ("A · Main wall"); "" for a screen of its own.</summary>
    public string Canvas { get; init; } = "";
}

/// <summary>A live input: mounted or not, delivering or not; Page is the rail page that edits it ("" for the kind's default).</summary>
public sealed record EyeSource(string Key, string Label, string Kind, bool Mounted, string Status, CheckLight Light, string Page = "", string Held = "");

/// <summary>A box the desk drives; one with an InputScreen carries that screen and is the far end of its link.</summary>
public sealed record EyeDevice(string Id, string Name, string Profile, string Address, bool Enabled, bool Open, string Status, CheckLight Light, string Words, string InputScreen = "", string Received = "", int Mapped = 0);

/// <summary>A controller that said HELLO on the wire; Paired when it presented the show's token (or none is asked).</summary>
public sealed record EyeDeck(string Name, string Module, string Address, bool Paired)
{
    /// <summary>Round 74: where the deck's navigator is, in the deck's own words ("PLAN › Cues › 03.020"); "" for a deck that has not said.</summary>
    public string Where { get; init; } = "";
    /// <summary>Round 74: the deck is recording the desk's actions into a button.</summary>
    public bool Recording { get; init; }
}

/// <summary>A Companion heard on mDNS.</summary>
public sealed record EyeCompanion(string Host, string Address, bool Fresh, string Version);

/// <summary>
/// OSC as the Eye sees it: its port and words; <paramref name="Bind"/>, the address the control ports are bound to, ""
/// for every interface (OSC cannot pair, so an unbound port of an unpaired desk is amber); whether the desk is paired
/// (then OSC's verbs come only from this machine or the allow-list); and the allow-list as read (<see cref="OscGate.Read"/>).
/// </summary>
public sealed record EyeOsc(int Port, string Words, string Bind = "", bool Paired = false, OscAllowList? Allow = null)
{
    /// <summary>How many addresses the allow-list names.</summary>
    public int Allowed => Allow?.Addresses.Count ?? 0;

    /// <summary>The allow-list's entries that are not one whole address.</summary>
    public IReadOnlyList<string> LeftOut => Allow?.LeftOut ?? Array.Empty<string>();
}

/// <summary>The twin link as this process sees it: Role "main" / "standby" / "" (alone), the other machine's name, the phase's words and light.</summary>
public sealed record EyeTwin(string Role, string Phase, string OtherName, string Words, CheckLight Light);

/// <summary>Another Patterns heard on the beacon.</summary>
public sealed record EyeNodeHeard(string Instance, string Kind, string Name, bool Fresh, bool Linked, string Words)
{
    /// <summary>
    /// The Show Bus id its hello says beside the same instance (Show Bus 1.3 §2's <c>inst</c>), "" when no hello names it: the hello,
    /// the beacon and the twin context of one Patterns process are one card, <c>node:&lt;instance&gt;</c>, and another app's end
    /// <c>@&lt;that id&gt;</c> is placed on it.
    /// </summary>
    public string ShowBusId { get; init; } = "";

    /// <summary>
    /// The show needs this node (Show Bus 1.3 §13.1 for this desk: a link it follows, an enabled node device, a window grant on the
    /// air). Only then is a node gone quiet red; otherwise a node the beacon stopped hearing is grey — a hello or a beacon alone is
    /// never green and never a reason to call an absent node a fault (13.16).
    /// </summary>
    public bool Needed { get; init; }
}

public sealed record EyeRoom(string Code, int Phones, string Words, CheckLight Light);

public sealed record EyeStream(string Status, CheckLight Light);

public sealed record EyeNdiSend(string Id, string Label, bool Running, int Connections, string Status);

public sealed record EyeAudioSource(string Id, string Label, string Kind);

public sealed record EyeAudioOut(string Key, string Label, string Kind, bool Mute, double PeakDb, string Error);

/// <summary>A crosspoint of the matrix; <see cref="Followed"/> when the picture made it rather than a row (round 69).</summary>
public sealed record EyeAudioRoute(string SourceId, string DestinationKey, double LevelDb, bool Muted, bool Followed = false);

public sealed record EyeStack(bool Armed, string Standby, string StandbyId, string Last, bool Hold, bool Executing, string Words, CheckLight Light);

public sealed record EyeAssistant(bool HasKey, string Words);

// ---- the picture ---------------------------------------------------------------------------

/// <summary>
/// One thing on the God's Eye: a stable id, what it is, where it sits (band and tier), what it is
/// called, its state in a line, its light, its facts as words, the page that shows it, and the
/// desk menu that is its own ("tile" for a screen, "cue" for the stack's standby cue, "eye" for
/// the rest).
/// </summary>
public sealed record EyeNode(string Id, EyeKind Kind, EyePlane Plane, int Tier, string Label, string Sub, CheckLight Light)
{
    public IReadOnlyList<string> Words { get; init; } = Array.Empty<string>();
    public MenuRoute? Route { get; init; }
    public string MenuKind { get; init; } = "eye";
    public string MenuSubject { get; init; } = "";

    public bool IsProblem => Light is CheckLight.Red or CheckLight.Amber;

    private readonly EyePlane? _band;

    /// <summary>
    /// The band the layout draws it in — <see cref="EyePlane.Network"/> for another app's card and things, which sit in
    /// that app's cluster; its <see cref="Plane"/> otherwise. A lens reads <see cref="Plane"/>, never the band.
    /// </summary>
    public EyePlane Band { get => _band ?? Plane; init => _band = value; }

    /// <summary>The app whose cluster holds it — its id on the show network, its card <c>app:&lt;App&gt;</c>; "" for this desk's own things.</summary>
    public string App { get; init; } = "";

    /// <summary>
    /// The rail, the worst, the head of the problems queue and the headline count it — this desk's own things and a
    /// needed app's card. Another app's things, and the card of an app the show does not need, are that app's to say: they
    /// stay in the picture and in the queue, after every counted problem.
    /// </summary>
    public bool Counted { get; init; } = true;

    /// <summary>
    /// A grey that is information, not a gap (a Companion heard and not connected, the apps past the bound): its mark is i. Only
    /// grey reads it — any other light draws its own mark — and a replay's relighting clears it, since the record holds lights.
    /// </summary>
    public bool Info { get; init; }

    /// <summary>The shape drawn beside its colour, derived from <see cref="Light"/> (and <see cref="Info"/>) — never stored, so never stale.</summary>
    public EyeMark Mark => EyeMarks.Of(Light, Info);

    /// <summary>
    /// Where it comes from (the Eye design's D4): this desk's own thing, an app whose link this desk holds or accepts, a thing an app's
    /// own context says, a node only another app's context names (a grey mention, never in that app's colour), or a hello alone.
    /// </summary>
    public EyeOrigin Origin { get; init; } = EyeOrigin.Own;
}

/// <summary>One link of the Eye, with its own light and its own words.</summary>
public sealed record EyeEdge(string From, string To, EyeEdgeKind Kind, CheckLight Light, string Words = "")
{
    /// <summary>"feeds", "drives", "carries"… — the word between the two things.</summary>
    public string Verb => Kind.ToString().ToLowerInvariant();

    /// <summary>The shape drawn at its segment's midpoint beside its colour, derived from <see cref="Light"/>.</summary>
    public EyeMark Mark => EyeMarks.Of(Light);

    /// <summary>
    /// A link this desk holds to another app (its card follows the desk) — the rail, the worst and the headline count
    /// its light, the record's own, whether or not the show needs the app's card.
    /// </summary>
    public bool Counted { get; init; }

    /// <summary>
    /// Where it comes from (the Eye design's D4): this desk's own, a link's record, an app's own context (told), another app's words
    /// about a third (relayed), or a hello alone. A told link between two apps, or a relayed one, is a claim (<see cref="Claim"/>).
    /// </summary>
    public EyeOrigin Origin { get; init; } = EyeOrigin.Own;

    /// <summary>
    /// The app whose claim it is — "Stagewatch Hall A" — for a link one app's context draws to another node's things or says of a third;
    /// "" for a fact. Its words begin "&lt;that app&gt; says:", and it never makes the other node's light.
    /// </summary>
    public string Claim { get; init; } = "";
}

/// <summary>
/// The whole show as one graph (round 66): built from <see cref="EyeFacts"/> alone, deterministic
/// — the same facts make the same ids in the same order — with the headline, the counts and the
/// problems queue computed once. Nothing here is a second source of truth: every light is the
/// light the Super Check, the tile, the device card or the deck already shows.
/// </summary>
public sealed class EyeGraph
{
    public const string DeskId = "desk";

    /// <summary>The one grey card that stands for the apps past <see cref="EyeLayout.MaxApps"/>.</summary>
    public const string MoreId = "apps:more";

    private readonly Dictionary<string, EyeNode> _byId;
    private readonly Dictionary<string, List<string>> _adjacent;
    private readonly Dictionary<string, List<EyeEdge>> _edgesOf;
    private readonly List<string> _problems;
    private readonly Dictionary<string, HashSet<EyePlane>> _clusterPlanes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckLight> _counted = new(StringComparer.Ordinal);
    private readonly (int Index, int Count)[] _lanes;

    private EyeGraph(List<EyeNode> nodes, List<EyeEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
        _byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _adjacent = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        _edgesOf = nodes.ToDictionary(n => n.Id, _ => new List<EyeEdge>(), StringComparer.Ordinal);
        // Two things may share several links (a context's feeds and hears between the same pair): each link is kept, each
        // neighbour once — the walk, the lens and the readers count a thing once and list every link to it.
        var seen = new HashSet<(string, string)>();
        foreach (var e in edges)
        {
            if (!_byId.ContainsKey(e.From) || !_byId.ContainsKey(e.To)) continue;
            // A pair met for the first time, either way: each end lists the other once (a thing linked to itself lists itself once).
            if (seen.Add((e.From, e.To)))
            {
                seen.Add((e.To, e.From));
                _adjacent[e.From].Add(e.To);
                if (e.From != e.To) _adjacent[e.To].Add(e.From);
            }
            _edgesOf[e.From].Add(e);
            if (e.From != e.To) _edgesOf[e.To].Add(e);
        }
        // Each link's lane among the links joining its pair, either way, reckoned once per picture: the canvas draws the k-th
        // of n side by side from it on every frame without counting the pair again.
        _lanes = new (int Index, int Count)[edges.Count];
        var perPair = new Dictionary<(string, string), int>();
        for (var i = 0; i < edges.Count; i++)
        {
            var pair = PairOf(edges[i]);
            var index = perPair.TryGetValue(pair, out var before) ? before : 0;
            perPair[pair] = index + 1;
            _lanes[i] = (index, 0);
        }
        for (var i = 0; i < edges.Count; i++) _lanes[i] = (_lanes[i].Index, perPair[PairOf(edges[i])]);
        // What the rail counts — this desk's own things, its links to other apps and the cards of the apps the show needs — as
        // things: a link and its app's card are one counted thing, lit by the worse of the two, so the four counts sum to Things
        // and each counted problem is queued by the light it is counted with.
        var counted = _counted;
        foreach (var n in nodes)
        {
            if (n.Kind == EyeKind.ShowAppItem && n.App.Length > 0)
            {
                var card = EyeNetwork.CardOf(n.App);
                if (!_clusterPlanes.TryGetValue(card, out var planes)) _clusterPlanes[card] = planes = new HashSet<EyePlane>();
                planes.Add(n.Plane);
            }
            if (n.Counted) counted[n.Id] = n.Light;
        }
        foreach (var e in edges)
        {
            if (!e.Counted || !_byId.ContainsKey(e.From) || !_byId.ContainsKey(e.To)) continue;
            var app = e.From == DeskId ? e.To : e.From;
            counted[app] = counted.TryGetValue(app, out var was) ? ShowContext.Worst(new[] { was, e.Light }) : e.Light;
        }
        Things = counted.Count;
        Red = counted.Values.Count(l => l == CheckLight.Red);
        Amber = counted.Values.Count(l => l == CheckLight.Amber);
        Green = counted.Values.Count(l => l == CheckLight.Green);
        Grey = Things - Red - Amber - Green;
        List<string> Queue(IEnumerable<(EyeNode Node, CheckLight Light)> things) => things.Where(t => t.Light == CheckLight.Red).OrderBy(t => ProblemOrder(t.Node)).ThenBy(t => t.Node.Tier).ThenBy(t => t.Node.Label, StringComparer.OrdinalIgnoreCase)
            .Concat(things.Where(t => t.Light == CheckLight.Amber).OrderBy(t => ProblemOrder(t.Node)).ThenBy(t => t.Node.Tier).ThenBy(t => t.Node.Label, StringComparer.OrdinalIgnoreCase))
            .Select(t => t.Node.Id).ToList();
        var own = Queue(nodes.Where(n => counted.ContainsKey(n.Id)).Select(n => (n, counted[n.Id])).ToList());
        var owned = new HashSet<string>(own, StringComparer.Ordinal);
        _problems = own.Concat(Queue(nodes.Where(n => !owned.Contains(n.Id)).Select(n => (n, n.Light)).ToList())).ToList();
        CountedProblems = own.Count;
        Worst = own.Count > 0 ? _byId[own[0]] : null;
        Headline = Words();
    }

    public IReadOnlyList<EyeNode> Nodes { get; }
    public IReadOnlyList<EyeEdge> Edges { get; }
    /// <summary>The counted things by light — this desk's own things, and each app its link or the show's need counts, once — summing to <see cref="Things"/>.</summary>
    public int Red { get; }
    public int Amber { get; }
    public int Green { get; }
    public int Grey { get; }

    /// <summary>
    /// The things the headline, the rail, STATE and EYE count: this desk's own (<see cref="EyeNode.Counted"/>), and each other app
    /// whose card the show needs or whose link this desk holds — that card and its link one thing, lit by the worse of the two.
    /// </summary>
    public int Things { get; }

    /// <summary>The head of <see cref="Problems"/> the rail counts — the counted reds and ambers; another app's own come after them.</summary>
    public int CountedProblems { get; }

    /// <summary>The apps heard past <see cref="EyeLayout.MaxApps"/>, drawn as the one card <see cref="MoreId"/>.</summary>
    public int MoreApps { get; private set; }

    /// <summary>
    /// Rows of other apps' contexts left out because an end names a node the Eye does not show, or one past the network's bounds
    /// (Show Bus 1.3 §6: "left out and counted"), whose kind is not one of §6's words, or whose peers row names no id of §2's shape;
    /// 0 when every row found its place. The NETWORK band's words say it (<see cref="EyePlacement.LeftEnds"/>).
    /// </summary>
    public int Unplaced { get; private set; }

    /// <summary>The things each drawn app's context names (by card id, its items' distinct ids) — what the layout counts as left out.</summary>
    internal Dictionary<string, int> Named { get; } = new(StringComparer.Ordinal);

    /// <summary>How many things an app's card names in its context (0 for a card with none, or a thing that is no card).</summary>
    public int NamedBy(string cardId) => Named.TryGetValue(cardId, out var n) ? n : 0;

    /// <summary>
    /// The counted reds, then the counted ambers, in the picture's order; then another app's own reds and ambers — what NEXT and
    /// PREV walk.
    /// </summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Position of a node in the problems queue (0-based), or −1 when it is not red or amber.</summary>
    public int ProblemIndex(string id) => _problems.IndexOf(id);

    /// <summary>The first counted problem, or null when what the rail counts is all green and grey.</summary>
    public EyeNode? Worst { get; }

    /// <summary>The worst counted light: the rail's colour.</summary>
    public CheckLight WorstLight => Red > 0 ? CheckLight.Red : Amber > 0 ? CheckLight.Amber : Green > 0 ? CheckLight.Green : CheckLight.Grey;

    /// <summary>One line that is true for the whole picture: "14 things · 1 red · 2 amber — Projector 1: carries 1080p60 against a 50 contract".</summary>
    public string Headline { get; }

    public static EyeGraph Empty { get; } = new(new List<EyeNode>(), new List<EyeEdge>());

    /// <summary>
    /// Round 84: the same things and links, in the same order, with other lights and words — a replayed
    /// moment over the structure of now. The screens' wire numbers ride along so a replay resolves SCREEN n.
    /// </summary>
    public EyeGraph Relit(Func<EyeNode, EyeNode> relight, Func<EyeEdge, EyeEdge>? relink = null)
    {
        var graph = new EyeGraph(Nodes.Select(relight).ToList(), (relink is null ? Edges : Edges.Select(relink)).ToList()) { MoreApps = MoreApps, Unplaced = Unplaced };
        foreach (var (id, number) in ScreenNumbers) graph.ScreenNumbers[id] = number;
        foreach (var (id, named) in Named) graph.Named[id] = named;
        return graph;
    }

    public EyeNode? Find(string id) => _byId.TryGetValue(id, out var n) ? n : null;

    /// <summary>The things linked to one, either way, each once however many links join them, in the order of their first link.</summary>
    public IReadOnlyList<string> Neighbours(string id) => _adjacent.TryGetValue(id, out var l) ? l : Array.Empty<string>();

    /// <summary>Every link that touches a thing, in the edges' order — two links to one neighbour are two entries.</summary>
    public IReadOnlyList<EyeEdge> EdgesOf(string id) => _edgesOf.TryGetValue(id, out var l) ? l : Array.Empty<EyeEdge>();

    /// <summary>The first link between two things, either way, or null — <see cref="EdgesBetween"/> for all of them.</summary>
    public EyeEdge? EdgeBetween(string a, string b) => EdgesOf(a).FirstOrDefault(e => Joins(e, a, b));

    /// <summary>Every link between two things, either way, in the edges' order: one row, one menu entry and one segment each.</summary>
    public IReadOnlyList<EyeEdge> EdgesBetween(string a, string b) => EdgesOf(a).Where(e => Joins(e, a, b)).ToList();

    private static bool Joins(EyeEdge e, string a, string b) => (e.From == a && e.To == b) || (e.From == b && e.To == a);

    private static (string, string) PairOf(EyeEdge e) => string.CompareOrdinal(e.From, e.To) <= 0 ? (e.From, e.To) : (e.To, e.From);

    /// <summary>
    /// The lane of <see cref="Edges"/>[<paramref name="edge"/>] among the links joining its two things, either way, in the edges'
    /// order — its index and how many there are (1 for a link alone) — kept with the picture, so a frame reads it without counting.
    /// </summary>
    public (int Index, int Count) Lane(int edge) => _lanes[edge];

    /// <summary>
    /// How far every thing in the focus's neighbourhood is, in hops; a thing not reached is absent.
    /// The desk is the hub of every band, so a walk never continues through it: the desk itself
    /// is a neighbour of what it shows and what controls it, but a screen's neighbourhood does not
    /// take in every deck because both touch the desk. Focusing the desk walks everything.
    /// </summary>
    public IReadOnlyDictionary<string, int> Hops(string focusId)
    {
        var hops = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!_byId.ContainsKey(focusId)) return hops;
        var queue = new Queue<string>();
        hops[focusId] = 0;
        queue.Enqueue(focusId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (id == DeskId && focusId != DeskId) continue;
            var next = hops[id] + 1;
            foreach (var n in _adjacent[id])
            {
                if (hops.ContainsKey(n)) continue;
                hops[n] = next;
                queue.Enqueue(n);
            }
        }
        return hops;
    }

    /// <summary>
    /// How bright a thing draws under a focus, from its hop distance: the focus and what it
    /// links to at full, two hops away at 0.55, the rest at a floor of 0.25 — present, never
    /// hidden. No focus (null) is full everywhere.
    /// </summary>
    public static double Emphasis(int? hops) => hops switch
    {
        null => 1.0,
        0 or 1 => 1.0,
        2 => 0.55,
        _ => 0.25,
    };

    /// <summary>A thing's emphasis under a focus's hops (null hops: no focus, everything full); a thing the walk never reached is on the floor.</summary>
    public static double EmphasisOf(string id, IReadOnlyDictionary<string, int>? hops)
        => hops is null ? 1.0 : hops.TryGetValue(id, out var h) ? Emphasis(h) : 0.25;

    /// <summary>
    /// Whether a thing is on a lens: everything; a band and the desk; the problems and what they touch. Another app's
    /// card is on a band's lens too when any of its things is — a lens reads each thing's plane, never the band it is drawn in.
    /// </summary>
    public bool Visible(EyeNode n, EyeLens lens) => lens switch
    {
        EyeLens.All => true,
        EyeLens.Problems => n.IsProblem || n.Kind == EyeKind.Desk || Neighbours(n.Id).Any(id => _byId.TryGetValue(id, out var o) && o.IsProblem),
        EyeLens.Video => OnPlane(n, EyePlane.Video),
        EyeLens.Control => OnPlane(n, EyePlane.Control),
        EyeLens.Audio => OnPlane(n, EyePlane.Audio),
        EyeLens.Room => OnPlane(n, EyePlane.Room),
        EyeLens.Network => n.Kind == EyeKind.Desk || IsNetworkCard(n),
        _ => true,
    };

    /// <summary>A card of the show network: another app's (<see cref="EyeKind.ShowApp"/>) or another Patterns node's (<see cref="EyeKind.Node"/>) — what the NETWORK lens shows beside the desk.</summary>
    public static bool IsNetworkCard(EyeNode n) => n.Kind is EyeKind.ShowApp or EyeKind.Node;

    private bool OnPlane(EyeNode n, EyePlane plane)
        => n.Plane == plane || n.Kind == EyeKind.Desk || (n.Kind == EyeKind.ShowApp && _clusterPlanes.TryGetValue(n.Id, out var planes) && planes.Contains(plane));

    /// <summary>A lens by its word ("video", "problems"); null for a word that is none.</summary>
    public static EyeLens? ParseLens(string word) => word.Trim().ToLowerInvariant() switch
    {
        "" or "all" or "everything" => EyeLens.All,
        "video" or "pictures" or "picture" => EyeLens.Video,
        "control" or "controllers" => EyeLens.Control,
        "audio" or "sound" => EyeLens.Audio,
        "room" or "audience" => EyeLens.Room,
        "problems" or "problem" or "wrong" => EyeLens.Problems,
        "network" or "apps" or "links" => EyeLens.Network,
        _ => null,
    };

    /// <summary>
    /// The thing some words name: an id as it is, "screen 2" or "2" by the screen's number,
    /// "desk" or "this desk", a label whole, then a label containing the words — the pictures
    /// first. Null when nothing answers.
    /// </summary>
    public string? Resolve(string words)
    {
        var w = (words ?? "").Trim();
        if (w.Length == 0) return null;
        var exact = Nodes.FirstOrDefault(n => n.Id.Equals(w, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Id;
        if (w.Equals("desk", StringComparison.OrdinalIgnoreCase) || w.Equals("this desk", StringComparison.OrdinalIgnoreCase) || w.Equals("machine", StringComparison.OrdinalIgnoreCase))
            return _byId.ContainsKey(DeskId) ? DeskId : null;
        var number = w.StartsWith("screen ", StringComparison.OrdinalIgnoreCase) ? w[7..].Trim() : w;
        if (number.All(char.IsDigit))
        {
            var byNumber = Nodes.FirstOrDefault(n => n.Kind == EyeKind.Screen && n.MenuSubject.Length > 0 && ScreenNumbers.TryGetValue(n.Id, out var num) && num == number);
            if (byNumber is not null) return byNumber.Id;
        }
        var ordered = Nodes.OrderBy(ProblemOrder).ThenBy(n => n.Tier).ToList();
        var whole = ordered.FirstOrDefault(n => n.Label.Equals(w, StringComparison.OrdinalIgnoreCase));
        if (whole is not null) return whole.Id;
        var part = ordered.FirstOrDefault(n => n.Label.Contains(w, StringComparison.OrdinalIgnoreCase));
        return part?.Id;
    }

    /// <summary>The screens' wire numbers by node id, for <see cref="Resolve"/>.</summary>
    internal Dictionary<string, string> ScreenNumbers { get; } = new(StringComparer.Ordinal);

    /// <summary>The problem after the current one (the first when none, or the current is not a problem); null when there are none.</summary>
    public string? Next(string? currentId)
    {
        if (Problems.Count == 0) return null;
        var i = currentId is null ? -1 : _problems.IndexOf(currentId);
        return Problems[(i + 1) % Problems.Count];
    }

    public string? Prev(string? currentId)
    {
        if (Problems.Count == 0) return null;
        var i = currentId is null ? 0 : _problems.IndexOf(currentId);
        if (i < 0) i = 0;
        return Problems[(i - 1 + Problems.Count) % Problems.Count];
    }

    /// <summary>
    /// The graph in words, one line per thing and per link — what the assistant reads. Each thing says where it is drawn and the
    /// lens it is on (<see cref="EyeLayout.Where"/>); a thing the headline does not count says so — it is another app's to say —
    /// and an app's card the headline counts by this desk's link alone says the light it is counted with.
    /// </summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>(Nodes.Count + Edges.Count + 1) { Headline };
        foreach (var n in Nodes)
        {
            var mark = n.Counted ? "" : _counted.TryGetValue(n.Id, out var link) ? $" (counted by this desk's link: {Light(link)})" : " (another app's, not counted)";
            lines.Add($"{EyeLayout.Where(n)} · {n.Kind}: {n.Label}{mark} — {Light(n.Light)}{(n.Sub.Length > 0 ? " · " + n.Sub : "")}");
        }
        foreach (var e in Edges)
        {
            var from = Find(e.From)?.Label ?? e.From;
            var to = Find(e.To)?.Label ?? e.To;
            lines.Add($"{from} → {to} ({e.Verb}, {Light(e.Light)}{(e.Words.Length > 0 ? ": " + e.Words : "")})");
        }
        return lines;
    }

    public static string Light(CheckLight light) => light switch
    {
        CheckLight.Red => "red",
        CheckLight.Amber => "amber",
        CheckLight.Green => "green",
        _ => "grey",
    };

    private static int ProblemOrder(EyeNode n) => n.Plane switch
    {
        EyePlane.Video => 0,
        EyePlane.Control => 1,
        EyePlane.Audio => 2,
        _ => 3,
    };

    private string Words()
    {
        if (Nodes.Count == 0) return "Nothing to see yet — the desk has not read its rig.";
        var n = Things;
        var things = n == 1 ? "1 thing" : $"{n} things";
        if (Worst is null)
        {
            return Grey > 0 ? $"{things} linked · all green · {Grey} unknown" : $"{things} linked · all green";
        }
        var counts = new List<string>();
        if (Red > 0) counts.Add($"{Red} red");
        if (Amber > 0) counts.Add($"{Amber} amber");
        return $"{things} · {string.Join(" · ", counts)} — {Worst.Label}: {Worst.Sub}";
    }

    // ---- the build --------------------------------------------------------------------------

    public static EyeGraph Build(EyeFacts f)
    {
        var nodes = new List<EyeNode>();
        var edges = new List<EyeEdge>();
        var numbers = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        void Add(EyeNode node)
        {
            if (!ids.Add(node.Id)) return;                                   // one thing once, whatever the facts repeated
            nodes.Add(node);
        }
        void Link(string from, string to, EyeEdgeKind kind, CheckLight light, string words = "", bool counted = false, EyeOrigin origin = EyeOrigin.Own) => edges.Add(new EyeEdge(from, to, kind, light, words) { Counted = counted, Origin = origin });

        // The desk: the hub of every band.
        var deskWords = new List<string>();
        if (f.Build.Length > 0) deskWords.Add($"Build {f.Build}");
        if (f.SecondDesk) deskWords.Add("a second desk on this show folder — the first one saves the show and keeps its recovery record; this one takes the folder over when it leaves");   // round 78
        if (f.HealthWords.Length > 0) deskWords.Add(f.HealthWords);
        if (f.MemoryWords.Length > 0) deskWords.Add(f.MemoryWords);                                   // round 69: the memory line
        if (f.MasterRate.Length > 0) deskWords.Add($"Master rate {f.MasterRate}");                     // round 80: the rate in force, and the display it follows
        if (f.NextTake.Length > 0) deskWords.Add($"Next take: {f.NextTake} (one shot)");
        if (f.TakeWords.Length > 0) deskWords.Add($"Next TAKE ({f.TakeScope}) {f.TakeWords}");
        if (f.Landing.Length > 0) deskWords.Add($"Landing {f.Landing}");                             // round 72: the ticket a sting will land
        if (f.LastTake.Length > 0) deskWords.Add($"Last take: {f.LastTake}");                        // round 79: what it did, and whether anybody saw it
        if (f.Editing.Length > 0) deskWords.Add($"Editing {f.Editing}");                             // round 73: what the desk's editors are on
        if (f.LowerThird.Length > 0) deskWords.Add($"Lower third {f.LowerThird}");                    // round 73: the name on screen and when it leaves
        if (f.Midi.Length > 0) deskWords.Add($"MIDI {f.Midi}");                                        // round 73: learn armed, or the surfaces' map
        if (f.Nav.Length > 0) deskWords.Add(f.Nav);                                                    // round 74: the page, the column, the selection
        deskWords.AddRange(f.Attention);
        Add(new EyeNode(DeskId, EyeKind.Desk, EyePlane.Video, 1, f.MachineName.Length > 0 ? f.MachineName : "This desk", f.OutputsLive ? "outputs live" : "outputs closed", f.Health)
        {
            Words = deskWords,
            Route = new MenuRoute("Machine"),
        });

        // Sources: the inputs the pictures draw from.
        var sourceLight = new Dictionary<string, CheckLight>(StringComparer.Ordinal);
        foreach (var s in f.Sources.OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase))
        {
            var id = "source:" + s.Key;
            sourceLight[s.Key] = s.Light;
            Add(new EyeNode(id, EyeKind.Source, EyePlane.Video, 0, s.Label.Length > 0 ? s.Label : s.Key, SourceSub(s), s.Light)
            {
                Words = new[] { $"Key {s.Key}", s.Kind, s.Mounted ? "mounted" : "not mounted", s.Status, s.Held.Length > 0 ? "held: " + s.Held : "" }.Where(w => w.Length > 0).ToList(),
                Route = new MenuRoute(s.Page.Length > 0 ? s.Page : SourcePage(s.Kind), s.Key),
            });
        }

        // Displays.
        var displays = f.Displays.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var screensByDisplay = f.Screens.Where(s => s.DisplayId.Length > 0).GroupBy(s => s.DisplayId).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        foreach (var d in f.Displays.OrderBy(d => d.Label, StringComparer.OrdinalIgnoreCase))
        {
            var light = d.Missing ? CheckLight.Red : d.Planned ? CheckLight.Amber : CheckLight.Green;
            var size = d.Width > 0 ? $"{d.Width}×{d.Height}{(d.Hz > 0 ? $" @ {d.Hz} Hz" : "")}" : "";
            var sub = d.Missing ? "MISSING — its display was unplugged" : d.Planned ? "planned, no display yet" : size + (d.Primary ? " · primary" : "");
            Add(new EyeNode("display:" + d.Id, EyeKind.Display, EyePlane.Video, 3, d.Label.Length > 0 ? d.Label : d.Id, sub, light)
            {
                Words = new[] { size, d.Primary ? "the primary display" : "", d.Planned ? "planned" : "", d.Missing ? "missing" : "" }.Where(w => w.Length > 0).ToList(),
                Route = new MenuRoute("Screens", screensByDisplay.TryGetValue(d.Id, out var sid) ? sid : ""),
            });
        }

        // Screens, and what feeds them, shows them, drives them.
        var screenIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // by id and by number → node id
        foreach (var s in f.Screens.OrderBy(s => s.Number.Length > 0 ? s.Number.PadLeft(4, '0') : "9999" + s.Label, StringComparer.Ordinal))
        {
            var id = "screen:" + s.Id;
            var hasDisplay = s.DisplayId.Length > 0 && displays.ContainsKey(s.DisplayId);
            var displayMissing = hasDisplay && displays[s.DisplayId].Missing;
            var displayPresent = hasDisplay && !displayMissing && !displays[s.DisplayId].Planned;
            var verdict = s.Verdict.ToUpperInvariant();
            CheckLight light;
            string sub;
            if (!s.Enabled) { light = CheckLight.Grey; sub = "disabled"; }
            else if (displayMissing) { light = CheckLight.Red; sub = "its display is missing"; }
            else if (verdict == "MISMATCH") { light = CheckLight.Red; sub = s.SignalWords.Length > 0 ? s.SignalWords : "MISMATCH"; }
            else if (s.TestRoute) { light = CheckLight.Amber; sub = "on the test route" + (s.SignalWords.Length > 0 ? " · " + s.SignalWords : ""); }
            else if (verdict == "MATCH") { light = CheckLight.Green; sub = s.SignalWords.Length > 0 ? s.SignalWords : "MATCH"; }
            else if (verdict == "PARTIAL") { light = CheckLight.Amber; sub = s.SignalWords.Length > 0 ? s.SignalWords : "PARTIAL — a contracted property was never stated"; }   // round 72: never green
            else if (s.Contract.Length > 0) { light = CheckLight.Grey; sub = "not verified · " + s.Contract; }
            else if (f.OutputsLive && displayPresent) { light = CheckLight.Green; sub = s.OnAir ? "on air · no contract" : "showing · no contract"; }
            else { light = CheckLight.Grey; sub = displayPresent ? "outputs closed" : "planned, no display"; }
            var words = new List<string>();
            if (s.Number.Length > 0) words.Add($"Screen {s.Number}");
            if (s.Role.Length > 0) words.Add(s.Role);
            if (s.Contract.Length > 0) words.Add("Contract " + s.Contract);
            if (s.Verdict.Length > 0) words.Add("Result " + s.Verdict);
            if (s.Received.Length > 0) words.Add("Received " + s.Received);
            if (s.Own) words.Add("its own picture");
            if (s.Adjustments.Length > 0) words.Add("picture " + s.Adjustments);   // round 77: a mirror or a turn is never a surprise on the wall
            if (s.Staged) words.Add("its own picture in the preview — not taken yet");
            if (s.Canvas.Length > 0) words.Add("in canvas " + s.Canvas);
            if (s.Locked) words.Add("LOCKED — keeps its picture through looks, cues, every take and a sting");
            if (!s.Armed) words.Add("held — the next CUT / TAKE leaves it");
            if (s.Ticked) words.Add("ticked — a TICKED take, a fade or SEND TO TICKED reads it");
            Add(new EyeNode(id, EyeKind.Screen, EyePlane.Video, 2, s.Label.Length > 0 ? s.Label : (s.Number.Length > 0 ? "Screen " + s.Number : s.Id), sub, light)
            {
                Words = words,
                Route = new MenuRoute("Screens", s.Id),
                MenuKind = "tile",
                MenuSubject = s.Id,
            });
            numbers[id] = s.Number;
            screenIds[s.Id] = id;
            if (s.Number.Length > 0) screenIds[s.Number] = id;

            // The desk shows it.
            if (!s.Enabled) Link(DeskId, id, EyeEdgeKind.Shows, CheckLight.Grey, "disabled");
            else if (f.OutputsLive && displayPresent) Link(DeskId, id, EyeEdgeKind.Shows, CheckLight.Green, s.OnAir ? "on air" : "showing");
            else Link(DeskId, id, EyeEdgeKind.Shows, CheckLight.Grey, displayPresent ? "outputs closed" : "no display to show on");

            // What feeds it.
            foreach (var key in s.Sources.Distinct(StringComparer.Ordinal))
            {
                var sid = "source:" + key;
                if (!ids.Contains(sid))
                {
                    Add(new EyeNode(sid, EyeKind.Source, EyePlane.Video, 0, key, "used, not mounted", CheckLight.Red) { Words = new[] { $"Key {key}", "a picture asks for it and nothing is mounted" }, Route = new MenuRoute(SourcePage(""), key) });
                    sourceLight[key] = CheckLight.Red;
                }
                Link(sid, id, EyeEdgeKind.Feeds, sourceLight.TryGetValue(key, out var sl) ? sl : CheckLight.Grey, sl == CheckLight.Red ? "not mounted" : "");
            }

            // The display it drives.
            if (hasDisplay)
            {
                var driveLight = displayMissing ? CheckLight.Red : verdict == "MISMATCH" ? CheckLight.Red : s.TestRoute || verdict == "PARTIAL" ? CheckLight.Amber : verdict == "MATCH" ? CheckLight.Green : CheckLight.Grey;
                Link(id, "display:" + s.DisplayId, EyeEdgeKind.Drives, driveLight, displayMissing ? "the display is missing" : s.SignalWords.Length > 0 ? s.SignalWords : s.Contract.Length > 0 ? "not verified" : "");
            }
        }

        // NDI sends: pictures leaving over the network.
        foreach (var n in f.NdiSends.OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase))
        {
            var id = "ndi:" + n.Id;
            var light = n.Running ? CheckLight.Green : CheckLight.Grey;
            var sub = n.Running ? (n.Connections == 1 ? "1 receiver" : $"{n.Connections} receivers") : n.Status.Length > 0 ? n.Status : "not running";
            Add(new EyeNode(id, EyeKind.NdiSend, EyePlane.Video, 3, n.Label.Length > 0 ? n.Label : n.Id, sub, light) { Words = new[] { "NDI send", n.Status }.Where(w => w.Length > 0).ToList(), Route = new MenuRoute("NDI", n.Id) });
            Link(DeskId, id, EyeEdgeKind.Sends, light, n.Running ? "sending" : "stopped");
        }

        // Devices: the far end of a screen's link, or a box the desk drives.
        foreach (var d in f.Devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var words = new[] { d.Profile, d.Address, d.Status, d.Words, d.Received.Length > 0 ? "Receives " + d.Received : "", d.Mapped > 0 ? $"{d.Mapped} control{(d.Mapped == 1 ? "" : "s")} mapped" : "" }.Where(w => w.Length > 0).ToList();
            var light = d.Enabled ? d.Light : CheckLight.Grey;
            var sub = !d.Enabled ? "disabled" : d.Status.Length > 0 ? d.Status : d.Open ? "answering" : "not open";
            if (d.InputScreen.Length > 0 && screenIds.TryGetValue(d.InputScreen, out var carried))
            {
                var id = "farend:" + d.Id;
                var screen = f.Screens.First(s => "screen:" + s.Id == carried);
                var carrySub = screen.Received.Length > 0 ? "receives " + screen.Received : sub;
                Add(new EyeNode(id, EyeKind.FarEnd, EyePlane.Video, 4, d.Name, carrySub, light) { Words = words, Route = new MenuRoute("Interactive", d.Id) });
                var from = screen.DisplayId.Length > 0 && displays.ContainsKey(screen.DisplayId) ? "display:" + screen.DisplayId : carried;
                var carryLight = !d.Enabled ? CheckLight.Grey : screen.ReceivedLight == CheckLight.Grey && !d.Open ? CheckLight.Red : screen.ReceivedLight;
                Link(from, id, EyeEdgeKind.Carries, carryLight, screen.Received.Length > 0 ? screen.Received : d.Open ? "not asked" : "no link");
            }
            else
            {
                var id = "device:" + d.Id;
                Add(new EyeNode(id, EyeKind.Device, EyePlane.Control, 2, d.Name, sub, light) { Words = words, Route = new MenuRoute("Interactive", d.Id) });
                Link(DeskId, id, EyeEdgeKind.Drives, light, d.Status);
            }
        }

        // The controllers: decks that said HELLO, Companions merely heard, the wire's and the web's clients, OSC.
        var deckAddresses = new HashSet<string>(f.Decks.Select(d => d.Address), StringComparer.OrdinalIgnoreCase);
        foreach (var d in f.Decks.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var id = $"deck:{d.Name}@{d.Address}";
            var light = d.Paired ? CheckLight.Green : CheckLight.Amber;
            var sub = d.Paired ? "paired" + (d.Module.Length > 0 ? $" · module {d.Module}" : "") : "connected, not paired — its keys do nothing";
            Add(new EyeNode(id, EyeKind.Deck, EyePlane.Control, 0, d.Name.Length > 0 ? d.Name : d.Address, sub, light)
            {
                // Round 74: where its navigator is. The Eye design's D2: a deck that names an app proved nothing about who dialled — it stays a
                // deck, and its words say both observations without joining them.
                Words = new[] { d.Address, d.Module.Length > 0 ? "module " + d.Module : "", d.Where.Length > 0 ? "navigator: " + d.Where : "", d.Recording ? "recording the desk's actions" : "", EyeNetwork.DeckWords(d, f.ShowApps) }.Where(w => w.Length > 0).ToList(),
                Route = new MenuRoute("Remote"),
            });
            Link(id, DeskId, EyeEdgeKind.Controls, light, d.Paired ? "paired" : "not paired");
        }
        foreach (var c in f.Companions.Where(c => !deckAddresses.Contains(c.Address)).OrderBy(c => c.Host, StringComparer.OrdinalIgnoreCase))
        {
            var id = "companion:" + c.Host;
            Add(new EyeNode(id, EyeKind.Companion, EyePlane.Control, 0, "Companion " + c.Host, c.Fresh ? "heard on mDNS · not connected" : "was heard · gone quiet", CheckLight.Grey) { Words = new[] { c.Address, c.Version.Length > 0 ? "v" + c.Version : "" }.Where(w => w.Length > 0).ToList(), Route = new MenuRoute("Remote"), Info = c.Fresh });   // heard now is information (i); gone quiet is a gap (○)
            Link(id, DeskId, EyeEdgeKind.Hears, CheckLight.Grey, "heard, not connected");
        }
        if (f.WireClients > 0)
        {
            Add(new EyeNode("peers:wire", EyeKind.Peers, EyePlane.Control, 0, "Wire clients", f.WireClients == 1 ? "1 connection" : $"{f.WireClients} connections", CheckLight.Green) { Route = new MenuRoute("Remote") });
            Link("peers:wire", DeskId, EyeEdgeKind.Controls, CheckLight.Green);
        }
        if (f.WebClients > 0)
        {
            Add(new EyeNode("peers:web", EyeKind.Peers, EyePlane.Control, 0, "Web clients", f.WebClients == 1 ? "1 page open" : $"{f.WebClients} pages open", CheckLight.Green) { Route = new MenuRoute("Remote") });
            Link("peers:web", DeskId, EyeEdgeKind.Controls, CheckLight.Green);
        }
        if (f.Osc is { } osc)
        {
            // Round 79: OSC has no session to pair, so the show's token never covers it — open on every interface it is
            // amber with the way out, as the super-check's row is; bound to the control network's address it is green.
            var problem = BindAddress.Problem(osc.Bind);                                     // round 85: a bind that is not an address closes the port
            var open = osc.Bind.Length == 0 && !osc.Paired;                                  // a paired desk takes OSC's verbs only from this machine and the allow-list
            var typo = osc.Paired && osc.LeftOut.Count > 0;                                 // a controller meant to be listed and refused: the Super Check's amber
            var light = problem.Length > 0 ? CheckLight.Red : open || typo ? CheckLight.Amber : CheckLight.Green;
            Add(new EyeNode("osc", EyeKind.Osc, EyePlane.Control, 0, "OSC", $"port {osc.Port}" + (osc.Words.Length > 0 ? " · " + osc.Words : ""), light)
            {
                Route = new MenuRoute("Remote"),
                Words = problem.Length > 0
                    ? new[] { $"closed — {problem}; the port opens when the Remote page's Bind to is an address or empty" }
                    : open
                        ? new[] { "open on every interface — OSC cannot pair, so the token does not cover it; bind the control ports to the control network's address, or turn OSC in off when it is not needed" }
                        : new[]
                        {
                            (osc.Bind.Length > 0 ? $"open on {osc.Bind} only — OSC cannot pair; the bind keeps it to the control network" : "OSC cannot pair")
                                + (osc.Paired ? "; " + OscGate.PairedWords(osc.Allowed, osc.LeftOut, osc.Bind.Length > 0) : ""),
                        },
            });
            Link("osc", DeskId, EyeEdgeKind.Controls, light);
        }

        // The desk's own: the cue stack and the assistant.
        if (f.Stack is { } stack)
        {
            var sub = stack.Words.Length > 0 ? stack.Words : stack.Armed ? (stack.Standby.Length > 0 ? "armed · standby " + stack.Standby : "armed") : "disarmed";
            Add(new EyeNode("stack", EyeKind.Stack, EyePlane.Control, 1, "Cue stack", sub, stack.Light)
            {
                Words = new[] { stack.Armed ? "armed" : "disarmed", stack.Hold ? "on hold" : "", stack.Executing ? "executing" : "", stack.Standby.Length > 0 ? "Standby " + stack.Standby : "", stack.Last.Length > 0 ? "Last " + stack.Last : "" }.Where(w => w.Length > 0).ToList(),
                Route = new MenuRoute("Cues", stack.StandbyId),
                MenuKind = stack.StandbyId.Length > 0 ? "cue" : "eye",
                MenuSubject = stack.StandbyId,
            });
            Link("stack", DeskId, EyeEdgeKind.Controls, stack.Light, stack.Armed ? "armed" : "disarmed");
        }
        if (f.Assistant is { } ai)
        {
            var light = ai.HasKey ? CheckLight.Green : CheckLight.Grey;
            Add(new EyeNode("assistant", EyeKind.Assistant, EyePlane.Control, 1, "Assistant", ai.Words.Length > 0 ? ai.Words : ai.HasKey ? "ready" : "no key — save one on the Assistant page", light) { Route = new MenuRoute("Assistant") });
            Link("assistant", DeskId, EyeEdgeKind.Controls, light, ai.HasKey ? "advises" : "no key");
        }

        // The other machines: the twin and the nodes heard.
        if (f.Twin is { } twin && twin.Role.Length > 0)
        {
            var isMain = twin.Role.Equals("main", StringComparison.OrdinalIgnoreCase);
            var label = twin.OtherName.Length > 0 ? (isMain ? "Standby " : "Main ") + twin.OtherName : isMain ? "Standby" : "Main";
            Add(new EyeNode("twin", EyeKind.Twin, EyePlane.Control, 2, label, twin.Words.Length > 0 ? twin.Words : twin.Phase, twin.Light) { Words = new[] { "Role " + twin.Role, twin.Phase }.Where(w => w.Length > 0).ToList(), Route = new MenuRoute("Machine") });
            if (isMain) Link(DeskId, "twin", EyeEdgeKind.Mirrors, twin.Light, twin.Phase);
            else Link("twin", DeskId, EyeEdgeKind.Mirrors, twin.Light, twin.Phase);
        }
        // Show Bus 1.3 §2 and the Eye design's D2: one card per app id, folded only on evidence of identity (EyeNetwork.Fold, LinkedId,
        // DeviceFolds). A Patterns process's twin context, its beacon and its hello are one card, node:<instance>; the beacon draws the
        // card itself only for a node no twin context names.
        var apps = EyeNetwork.Fold(f.ShowApps);
        var twinCards = apps.Where(a => a.Id.StartsWith("node:", StringComparison.Ordinal)).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var beacons = new Dictionary<string, EyeNodeHeard>(StringComparer.Ordinal);
        var cardOfId = new Dictionary<string, string>(StringComparer.Ordinal);                     // a §2 id → the card on the Eye that is that node
        foreach (var n in f.Nodes.OrderBy(n => n.Kind, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            var id = "node:" + n.Instance;
            if (twinCards.Contains(id))
            {
                beacons.TryAdd(id, n);                                                          // folded into its twin context's card below, its hello's id with it
                continue;
            }
            if (n.ShowBusId.Length > 0) cardOfId.TryAdd(n.ShowBusId, id);                        // no twin context names it: its hello is all that does
            // Gone quiet is red only when the show needs the node (13.1); a node it does not need is grey, a gap and no fault.
            var light = !n.Fresh ? (n.Needed ? CheckLight.Red : CheckLight.Grey) : n.Linked ? CheckLight.Green : CheckLight.Grey;
            var sub = !n.Fresh ? (n.Needed ? "gone quiet" : "not heard any more · not needed by the show") : n.Linked ? "linked" + (n.Words.Length > 0 ? " · " + n.Words : "") : "heard" + (n.Words.Length > 0 ? " · " + n.Words : "");
            var kind = n.Kind.Length > 0 ? char.ToUpperInvariant(n.Kind[0]) + n.Kind[1..].ToLowerInvariant() : "Node";
            Add(new EyeNode(id, EyeKind.Node, EyePlane.Control, 2, $"{kind} {n.Name}".Trim(), sub, light) { Words = new[] { kind + " node", n.Words }.Where(w => w.Length > 0).ToList(), Route = new MenuRoute("Nodes"), Origin = n.Linked ? EyeOrigin.Link : EyeOrigin.Heard });   // linked, it is known by its link, not only its hello
            if (n.Linked) Link(id, DeskId, EyeEdgeKind.Follows, light, n.Fresh ? "linked" : "gone quiet", origin: EyeOrigin.Link);
            else Link(id, DeskId, EyeEdgeKind.Hears, light, n.Fresh ? "heard on the beacon" : "gone quiet", origin: EyeOrigin.Heard);
        }

        // Round 87: the other show apps, and the things a linked one's context names, each card lit by the show network's one
        // projection (ShowBusView.Light: precedence, never the worst — the link this desk holds says whether the app is there, its
        // hello only where no link is held; heard only is grey, amber and red only when the show needs the app; while it is there,
        // its context's own light and headline, the cautions amber over a green or a grey; a lower source an i note).
        // Each on the NETWORK band in a cluster of its own, at most EyeLayout.MaxApps of them — the needed first, then
        // the linked — and one grey card for the rest; the card counted when the show needs the app, its link edge always.
        var heard = apps.OrderBy(a => a.KindWords, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Where(a => !ids.Contains(EyeNetwork.CardOf(a.Id))).ToList();
        var drawn = heard.OrderByDescending(a => a.Needed).ThenByDescending(ShowBusView.HoldsLink).Take(EyeLayout.MaxApps).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var named = new Dictionary<string, int>(StringComparer.Ordinal);
        var told = new List<(EyeShowApp App, string Card, ShowContext Ctx, HashSet<string> Items, HashSet<string> Edges)>();
        // The show network's lights, computed once for every reader (ShowBusView.Network): the card's light, mark, headline and notes.
        var lights = new Dictionary<string, AppLight>(StringComparer.Ordinal);
        foreach (var lit in ShowBusView.Network(heard.Where(a => drawn.Contains(a.Id)).ToList())) lights.TryAdd(lit.App.Id, lit);
        foreach (var app in heard)
        {
            var id = EyeNetwork.CardOf(app.Id);
            if (!drawn.Contains(app.Id)) continue;
            var lit = lights[app.Id];
            var twinNode = app.Id.StartsWith("node:", StringComparison.Ordinal);
            if (!twinNode) cardOfId[app.Id] = id;
            // A twin node is named by its context's id, which comes over the twin link; its hello's id only where the two agree (or the
            // context names none) — a hello naming another id is said on the card, never taken. Any other app is named by its own id alone:
            // the id its context says of itself is a claim, and one node is never placed as another.
            var nodeSaid = twinNode ? app.Context?.Node.Id ?? "" : "";
            if (nodeSaid.Length > 0) cardOfId.TryAdd(nodeSaid, id);
            var helloSays = twinNode && beacons.TryGetValue(id, out var heardAs) ? heardAs.ShowBusId : "";
            var helloDiffers = helloSays.Length > 0 && nodeSaid.Length > 0 && helloSays != nodeSaid;
            if (helloSays.Length > 0 && !helloDiffers) cardOfId.TryAdd(helloSays, id);
            var ctx = lit.Context;
            // Round 89: a stuck show (profile 13.10.5, 13.16.1): the card amber over its own green, never red for the stall
            // alone; the link amber, as its record says; its counting things "?" with no time (the projection's context says so).
            var stuck = app.Stuck && app.State == ShowBusState.Live;
            // Round 91: this desk's key refused, speaking for no node of this desk's, or read by P-legacy's fallback: amber
            // while the link is live, never red for the key alone; a fault said on the card itself while the link is live.
            var keyAmber = ShowBusView.KeyAmber(app);
            // Show Bus 1.3 §2: two running nodes say its id — amber while it is live, both machines named; never red for that alone,
            // and only an i note while a link LINK verified to that id says which of the two this desk is linked to.
            var conflict = ShowBusView.ConflictAmber(app);
            // The projection's light (precedence, never the worst of the sources): a live link's record amber over the card's green.
            var light = lit.Light;
            var state = lit.StateWord();
            var sub = lit.Headline.Length > 0 ? lit.Headline : state + (app.Words.Length > 0 ? " · " + app.Words : "");
            // An app known only by the links this desk's wire accepted (LINK proved its id; no hello names it, no link of this desk's dials it).
            var acceptedOnly = app.Heard.Length == 0 && ShowBusView.AcceptedLeads(app);
            var words = new List<string> { acceptedOnly ? $"{app.KindWords} — linked to this desk's wire by LINK, not heard on the show network" : $"{app.KindWords} on the show network — {state}" };
            if (beacons.TryGetValue(id, out var beacon))
                words.Add($"heard on the beacon as {beacon.Kind} {beacon.Name}" + (beacon.ShowBusId.Length > 0 ? $" · its hello says id {beacon.ShowBusId}" : "") + (beacon.Fresh ? "" : " · gone quiet"));
            if (helloDiffers)
                words.Add($"i a hello from its instance says id {helloSays}, its context over the twin link says {nodeSaid} — ends naming {helloSays} are not placed on it");
            if (stuck) words.Add($"the link is live; it {ShowBusView.StuckWords}");
            if (app.Linked && app.Key.Line.Length > 0) words.Add("this desk's key: " + app.Key.Line);
            if (ShowBusView.HoldsLink(app) && app.Link is { } record) words.Add("the link: " + record.Steady);
            if (app.Linked && app.Speaker.Length > 0) words.Add(app.Speaker);
            if (app.Linked && app.Windows.Length > 0) words.Add("windows it grants this desk: " + app.Windows);
            foreach (var r in app.Accepted) words.Add(ShowBusView.AcceptedWords(r) + (r.Ended ? "" : " — " + r.Words));
            if (app.DeviceId.Length > 0) words.Add($"driven as the device {app.Device} — DEVICE {ShowBusView.DeviceWord(app)} <its verb>");
            else if (app.DeviceAt.Length > 0) words.Add($"this desk's device {app.Device} is set at its address — drawn as one with it once that link is proved and its context names this app");
            if (app.Discarded.Length > 0) words.Add(app.Discarded);
            if (app.Conflict.Length > 0) words.Add(ShowBusView.ConflictWords(app));
            if (app.Words.Length > 0) words.Add(app.Words);
            words.AddRange(lit.Notes);
            if (ctx is not null)
            {
                foreach (var r in ctx.Recent.TakeLast(3)) words.Add(r.Text);
                foreach (var peer in ctx.Peers.Take(6)) words.Add($"hears {ShowBusPeers.KindWords(peer.Kind)} {peer.Name} — {peer.Light}{(peer.Headline.Length > 0 ? ": " + peer.Headline : "")}");
            }
            Add(new EyeNode(id, EyeKind.ShowApp, EyePlane.Control, 2, $"{app.KindWords} {app.Name}".Trim(), sub, light)
            {
                Words = words,
                Route = acceptedOnly ? new MenuRoute("Nodes", ShowBusView.OpenAccepted(app)!.Lid) : new MenuRoute("Nodes"),
                Band = EyePlane.Network,
                App = app.Id,
                Counted = app.Needed,
                Origin = ShowBusView.HoldsLink(app) ? EyeOrigin.Link : ctx is not null ? EyeOrigin.Told : EyeOrigin.Heard,
            });
            // Show Bus 1.3 §13.16.4: the link's edge is its record — its light, its mark beside its words — and nothing this Eye derives.
            if (app.Linked && app.Link is { } link) Link(id, DeskId, EyeEdgeKind.Follows, link.LightValue, $"{link.Mark} {link.SteadyWords}", counted: true, origin: EyeOrigin.Link);
            else if (app.Linked && stuck) Link(id, DeskId, EyeEdgeKind.Follows, CheckLight.Amber, "linked · its show is stuck", counted: true, origin: EyeOrigin.Link);
            else if (app.Linked && keyAmber) Link(id, DeskId, EyeEdgeKind.Follows, CheckLight.Amber, app.Key.Fault ? "linked · this desk's key " + (app.Key.Refused ? "refused" : "not accepted") : "linked · an older Stagewatch", counted: true, origin: EyeOrigin.Link);
            else if (app.Linked && conflict) Link(id, DeskId, EyeEdgeKind.Follows, CheckLight.Amber, "linked · " + ShowBusView.ConflictEdgeWords, counted: true, origin: EyeOrigin.Link);
            // A link with no record of its own is lit by its own state — live green, quiet amber, gone red only when the
            // show needs it (13.16.1) — never by the app's context, which is the app's to say on its card.
            else if (app.Linked) Link(id, DeskId, EyeEdgeKind.Follows, app.State switch { ShowBusState.Live => CheckLight.Green, ShowBusState.Stale => CheckLight.Amber, _ => app.Needed ? CheckLight.Red : CheckLight.Amber },
                app.State == ShowBusState.Live ? "linked" : state, counted: true, origin: EyeOrigin.Link);
            else if (conflict) Link(id, DeskId, EyeEdgeKind.Hears, CheckLight.Amber, "heard · " + ShowBusView.ConflictEdgeWords, origin: EyeOrigin.Heard);
            // A node device no hello names is not heard: its one edge is the device's. An open link its wire accepted of its kind is the
            // one edge to the desk whether or not a hello names it (the acceptor's, drawn below), its hello's age the card's i note.
            else if (!ShowBusView.Accepts(app) && (app.Heard.Length > 0 || (app.DeviceId.Length == 0 && app.Accepted.Count == 0))) Link(id, DeskId, EyeEdgeKind.Hears, light, app.State == ShowBusState.Live ? "heard on the show network" : state, origin: EyeOrigin.Heard);
            // Round 97: the device that drives a Show Bus node controls it — this desk's link to it, counted, lit and worded by the
            // device link's own record (13.16.4), from the device's node (from the desk when the Eye draws no node for the device).
            if (app.DeviceId.Length > 0 && app.Link is { } driven)
                Link(ids.Contains("device:" + app.DeviceId) ? "device:" + app.DeviceId : DeskId, id, EyeEdgeKind.Controls, driven.LightValue,
                    $"{driven.Mark} drives it — {driven.SteadyWords}", counted: true, origin: EyeOrigin.Link);
            if (ctx is null || app.State == ShowBusState.Gone) continue;
            named[id] = ctx.Items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count();
            // A link this desk holds that is down, held or refused: nothing its context says is current, so none of its things is drawn.
            if (ShowBusView.HoldsLink(app) && app.Link is { IsOn: true, State: "down" or "held" or "refused" }) continue;
            // Show Bus 1.3 §6 P2 and the Eye design's D3: a link item with a row to another node is that link — an edge, never a thing.
            var kinds = ctx.Items.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);
            bool IsLinkItem(string itemId) => kinds.TryGetValue(itemId, out var k) && k.Equals("link", StringComparison.OrdinalIgnoreCase);
            var edgeItems = ctx.Links.SelectMany(l => IsLinkItem(l.From) && EyeNetwork.IsEnd(l.To) ? new[] { l.From } : IsLinkItem(l.To) && EyeNetwork.IsEnd(l.From) ? new[] { l.To } : Array.Empty<string>())
                .ToHashSet(StringComparer.Ordinal);
            // Its chain ends — things with a row to another node — follow its reds and ambers into the dozen it draws (§6 P7), at most
            // EyeNetwork.MaxForced of them, so what its cluster shows holds its problems and where its pictures go.
            var ends = ctx.Links.SelectMany(l => EyeNetwork.IsEnd(l.To) ? new[] { l.From } : EyeNetwork.IsEnd(l.From) ? new[] { l.To } : Array.Empty<string>())
                .Where(i => !edgeItems.Contains(i)).ToHashSet(StringComparer.Ordinal);
            var forced = ctx.Items.Where(i => ends.Contains(i.Id) && ShowContext.LightOf(i.Light) is not (CheckLight.Red or CheckLight.Amber)).Select(i => i.Id).Distinct(StringComparer.Ordinal)
                .Take(EyeNetwork.MaxForced).ToHashSet(StringComparer.Ordinal);
            // Its own things — at most a dozen, the reds and ambers first, so a busy app does not drown the picture — in the
            // context's order; each on its kind's plane for the lenses and in its kind's column of the cluster (§5.3's table).
            var items = ctx.Items.Where(i => !edgeItems.Contains(i.Id)).Select((item, at) => (Item: item, At: at))
                .OrderByDescending(x => ShowContext.LightOf(x.Item.Light) == CheckLight.Red).ThenByDescending(x => ShowContext.LightOf(x.Item.Light) == CheckLight.Amber)
                .ThenByDescending(x => forced.Contains(x.Item.Id))
                .Take(EyeLayout.ClusterThings).OrderBy(x => x.At).Select(x => x.Item).ToList();
            // A link this desk holds that is not live: its things are kept, but nothing it last said is current (13.10.6) — the
            // projection's context says so of its counting things, and each other thing says it is as it last said.
            var notCurrent = ShowBusView.LinkNotCurrent(app);
            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var itemId = $"{id}:{item.Id}";
                if (!itemIds.Add(item.Id) || ids.Contains(itemId)) continue;
                var itemLight = ShowContext.LightOf(item.Light);
                Add(new EyeNode(itemId, EyeKind.ShowAppItem, EyeLayout.KindPlane(item.Kind), EyeLayout.KindColumn(item.Kind), item.Label.Length > 0 ? item.Label : item.Id, item.State, itemLight)
                {
                    Words = new[] { $"{app.KindWords} {app.Name}: {item.Kind}", item.State, notCurrent.Length > 0 && !ShowBusView.IsCounting(item.Kind) ? "as it last said — " + notCurrent : "" }.Where(w => w.Length > 0).ToList(),
                    Route = new MenuRoute("Nodes"),
                    Band = EyePlane.Network,
                    App = app.Id,
                    Counted = false,
                    Origin = EyeOrigin.Told,
                });
            }
            told.Add((app, id, ctx, itemIds, edgeItems));
        }
        var more = heard.Count - drawn.Count;
        // The apps counted on the more card: drawn by nothing else, a link of theirs included.
        var undrawn = heard.Where(a => !drawn.Contains(a.Id)).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        if (more > 0)
        {
            Add(new EyeNode(MoreId, EyeKind.ShowApp, EyePlane.Control, 2, $"+{more} more app{(more == 1 ? "" : "s")} heard", $"the Eye draws {EyeLayout.MaxApps} apps — the Nodes page lists every one", CheckLight.Grey)
            {
                Words = new[] { $"{more} more app{(more == 1 ? " is" : "s are")} heard on the show network than the Eye draws", "the needed and the linked are drawn first" },
                Route = new MenuRoute("Nodes"),
                Band = EyePlane.Network,
                Counted = false,
                Info = true,
            });
        }

        // Show Bus 1.3 §13.16.5 and §6 P2: a link this desk's wire accepted by LINK is an edge, never a thing — the one edge between the
        // connector's card (app:<its id>) and this desk, lit, marked and worded by the acceptor's list, so an app heard on the bus and
        // linked by LINK is drawn and counted once. LINK proved the id (level 1), so a connector no hello has named gets its card here,
        // counted only through the edge. The edge is counted while the link stands, folded into the card as one thing; once ended it is
        // grey and counted by nothing. K is what the connector did on the link: controls once it ran one of this desk's verbs, else follows.
        foreach (var r in f.AppLinks.Where(r => r.Id.Length > 0 && !undrawn.Contains(r.Id)))
        {
            var card = EyeNetwork.CardOf(r.Id);
            if (!ids.Contains(card))
            {
                if (r.Ended) continue;                                                  // an ended link with no card: the Nodes page lists it
                var kindWords = ShowBusPeers.KindWords(r.T);
                Add(new EyeNode(card, EyeKind.ShowApp, EyePlane.Control, 2, r.Name.Length > 0 ? $"{kindWords} {r.Name}" : AppKeys.ConnectorWords(r.Client),
                    $"linked to this desk by LINK · {r.ProofWords} · scope {r.Scope}", r.Light)
                {
                    Words = new[] { $"{kindWords} — linked to this desk's wire by LINK, not heard on the show network", r.Words },
                    Route = new MenuRoute("Nodes", r.Lid),
                    Band = EyePlane.Network,
                    App = r.Id,
                    Counted = false,
                    Origin = EyeOrigin.Link,
                });
            }
            cardOfId.TryAdd(r.Id, card);
            var drove = r.Drove ? EyeEdgeKind.Controls : EyeEdgeKind.Follows;
            var words = r.Ended
                ? $"{r.Mark} its link to this desk ended" + (r.EndWords.Length > 0 ? " — " + r.EndWords : "")
                : $"{r.Mark} linked to this desk by LINK · {r.ProofWords} · scope {r.Scope}" + (r.Flags.Count > 0 ? " · " + string.Join(", ", r.Flags) : "") + (r.Recording ? " · recording the desk's actions" : "");
            Link(card, DeskId, drove, r.Light, words, counted: !r.Ended, origin: EyeOrigin.Link);
        }

        // Show Bus 1.3 §6's cross-node ends and the Eye design's D3, D4 and D6, once every card and thing is drawn: each context's rows
        // placed on what they name — @<this desk> on the desk, @<this desk>/<item> on the thing this desk exports by that id, another
        // node's item where it is drawn, else that node's card — and a row whose end names a node the Eye does not show left out and
        // counted. A link item's row to a node is that link's one edge, from the app's card; every other row that touches the item is
        // re-anchored to the far node. A row to another node is the publishing app's claim: told, worded "<app> says", never a fact
        // about the other. Within one app, a row joins two drawn things, as before.
        var unplaced = 0;
        var crossApp = 0;
        var relayed = 0;
        var mentions = 0;
        var ownByShort = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in nodes.Where(n => n.App.Length == 0 && n.Kind is not (EyeKind.ShowApp or EyeKind.ShowAppItem)))
        {
            ownByShort.TryAdd(n.Id, n.Id);
            ownByShort.TryAdd(EyeNetwork.ShortId(n.Id), n.Id);
        }
        var pairs = new HashSet<(string, string)>();
        foreach (var e in edges)
        {
            pairs.Add((e.From, e.To));
            pairs.Add((e.To, e.From));
        }
        // The links this desk holds or accepts, by their records: per card, this desk's end of its record's edge — the desk, or the
        // device of this desk's that drives that node. Whichever node of this desk's the record starts from, it is this desk's one link
        // to that card, and an app's own claim of the same link is never drawn beside it.
        var cards = nodes.Where(n => n.Kind is EyeKind.ShowApp or EyeKind.Node).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var ownIds = nodes.Where(n => n.App.Length == 0 && n.Kind is not (EyeKind.ShowApp or EyeKind.ShowAppItem or EyeKind.Node)).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var deskEndOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in edges.Where(e => e.Origin == EyeOrigin.Link))
        {
            var (card, mine) = cards.Contains(e.From) && ownIds.Contains(e.To) ? (e.From, e.To) : cards.Contains(e.To) && ownIds.Contains(e.From) ? (e.To, e.From) : ("", "");
            if (card.Length == 0) continue;
            if (!deskEndOf.TryGetValue(card, out var held) || held == DeskId) deskEndOf[card] = mine;   // a device that drives the node is the nearer end
        }
        string LabelOf(string id) => nodes.FirstOrDefault(n => n.Id == id)?.Label ?? id;
        // An end placed on what it names: the item where it is drawn, else that node's card with the item kept in the claim's words
        // (`lost`), so a reader still reads which item was meant; null where the Eye shows no such node.
        string? Resolve(string text, out string lost)
        {
            lost = "";
            if (!EyeNetwork.TryEnd(text, out var end)) return null;
            if (f.ShowBusId.Length > 0 && end.Node == f.ShowBusId)
            {
                if (end.Item.Length == 0) return DeskId;
                if (ownByShort.TryGetValue(end.Item, out var own)) return own;
                lost = $"this desk's {end.Item}";
                return DeskId;
            }
            if (!cardOfId.TryGetValue(end.Node, out var card)) return null;
            if (end.Item.Length == 0) return card;
            if (ids.Contains($"{card}:{end.Item}")) return $"{card}:{end.Item}";
            lost = $"{LabelOf(card)}'s {end.Item}";
            return card;
        }
        void Claim(string from, string to, EyeEdgeKind kind, CheckLight light, string words, string claim, EyeOrigin origin)
        {
            edges.Add(new EyeEdge(from, to, kind, light, words) { Origin = origin, Claim = claim });
            pairs.Add((from, to));
            pairs.Add((to, from));
        }
        var saidOn = new HashSet<int>();                                                                // the record edges a far end's word follows already
        foreach (var (app, card, ctx, itemIds, edgeItems) in told)
        {
            var says = $"{app.KindWords} {app.Name}".Trim();
            var states = ctx.Items.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            // Each link item's far node, from its one row to a node: the edge of that link, and where its other rows are re-anchored.
            var far = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var l in ctx.Links)
            {
                var outward = edgeItems.Contains(l.From) && EyeNetwork.IsEnd(l.To);
                if (!outward && !(edgeItems.Contains(l.To) && EyeNetwork.IsEnd(l.From))) continue;
                var item = outward ? l.From : l.To;
                if (far.ContainsKey(item)) continue;                                                     // one row per link (§6 P2)
                var target = Resolve(outward ? l.To : l.From, out var lostItem);
                far[item] = target;
                if (target is null || target == card || !EyeNetwork.TryKind(l.Kind, out var kind))
                {
                    unplaced++;
                    continue;
                }
                var said = states.TryGetValue(item, out var it) ? it : new ShowContextItem { Id = item };
                // Drawn once: a link this desk holds or accepts already has its edge, from the record, which leads (D3) — whether the record
                // runs from the desk or from the device that drives the node; the item's other rows re-anchor to that end of this desk's.
                if (target == DeskId && deskEndOf.TryGetValue(card, out var deskEnd))
                {
                    far[item] = deskEnd;
                    // Precedence: the record sets the edge's light and mark; the other end's own word for the same link follows it, an
                    // i note where it says otherwise (Show Bus 1.3 §13.16.4) — the two ends of one link are the only two words it shows.
                    var at = edges.FindIndex(e => e.Origin == EyeOrigin.Link && ((e.From == card && e.To == deskEnd) || (e.From == deskEnd && e.To == card)));
                    if (at >= 0 && saidOn.Add(at))                                                          // one far-end word per edge
                    {
                        var other = $"{says} says: {(said.State.Length > 0 ? said.State : said.Label)}";
                        var differs = ShowContext.LightOf(said.Light) != edges[at].Light;
                        edges[at] = edges[at] with { Words = edges[at].Words + " · " + (differs ? "i " : "") + other };
                    }
                    continue;
                }
                if (crossApp >= EyeNetwork.MaxCrossApp)
                {
                    unplaced++;
                    continue;
                }
                crossApp++;
                var light = ShowContext.LightOf(said.Light);
                var text = $"{says} says: {(said.State.Length > 0 ? said.State : said.Label)}" + (lostItem.Length > 0 ? " — " + lostItem : "");
                if (outward) Claim(card, target, kind, light, text, says, EyeOrigin.Told);
                else Claim(target, card, kind, light, text, says, EyeOrigin.Told);
            }
            var linked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in ctx.Links)
            {
                if ((edgeItems.Contains(l.From) && EyeNetwork.IsEnd(l.To)) || (edgeItems.Contains(l.To) && EyeNetwork.IsEnd(l.From))) continue;   // the link's own row, drawn above
                var cross = EyeNetwork.IsEnd(l.From) || EyeNetwork.IsEnd(l.To) || edgeItems.Contains(l.From) || edgeItems.Contains(l.To);
                if (!EyeNetwork.TryKind(l.Kind, out var kind))
                {
                    if (cross) unplaced++;                                                                   // a word §6 does not have: refused, never defaulted
                    continue;
                }
                var lostWords = new List<string>(2);
                string? End(string end)
                {
                    if (EyeNetwork.IsEnd(end))
                    {
                        var placed = Resolve(end, out var lost);
                        if (lost.Length > 0) lostWords.Add(lost);
                        return placed;
                    }
                    if (edgeItems.Contains(end)) return far.TryGetValue(end, out var anchored) ? anchored : null;   // re-anchored to the link's far node
                    return itemIds.Contains(end) ? $"{card}:{end}" : null;
                }
                var from = End(l.From);
                var to = End(l.To);
                if (from is null || to is null)
                {
                    if (cross) unplaced++;
                    continue;
                }
                if (from == to) continue;
                if (!cross)
                {
                    Link(from, to, kind, ShowContext.LightOf(l.Light), origin: EyeOrigin.Told);
                    linked.Add(l.From);
                    linked.Add(l.To);
                    continue;
                }
                if (crossApp >= EyeNetwork.MaxCrossApp)
                {
                    unplaced++;
                    continue;
                }
                crossApp++;
                if (itemIds.Contains(l.From)) linked.Add(l.From);
                if (itemIds.Contains(l.To)) linked.Add(l.To);
                Claim(from, to, kind, ShowContext.LightOf(l.Light), $"{says} says: {kind.ToString().ToLowerInvariant()}" + string.Concat(lostWords.Select(w => " — " + w)), says, EyeOrigin.Told);
            }
            foreach (var item in itemIds.Where(i => !linked.Contains(i))) Link(card, $"{card}:{item}", EyeEdgeKind.Serves, CheckLight.Grey, origin: EyeOrigin.Told);

            // One hop (§6 peers, D5): a node another app hears is drawn where the Eye has it, else as a grey mention of its own — never in
            // the relaying app's colour — and the hearing is that app's claim: dashed, worded "<app> says", in the row's light only where
            // the row says a link proves it. This desk's own id is the desk's own record's to say.
            foreach (var p in ctx.Peers.Take(EyeNetwork.MaxMentionsPerContext))
            {
                if (p.Id.Length == 0 || p.Id == f.ShowBusId || p.Id == app.Id || p.Id == ctx.Node.Id) continue;
                if (!EyeNetwork.IsId(p.Id))
                {
                    unplaced++;                                                                             // not §2's shape: never a card's id
                    continue;
                }
                if (!cardOfId.TryGetValue(p.Id, out var target))
                {
                    if (apps.Any(a => a.Id == p.Id)) continue;                                            // heard past the bound: "+N more" counts it
                    if (mentions >= EyeNetwork.MaxMentions)
                    {
                        unplaced++;
                        continue;
                    }
                    target = "app:" + p.Id;
                    mentions++;
                    var kindWords = ShowBusPeers.KindWords(p.Kind);
                    Add(new EyeNode(target, EyeKind.ShowApp, EyePlane.Control, 2, $"{kindWords} {p.Name}".Trim() + " — heard by " + says, "heard, not linked", CheckLight.Grey)
                    {
                        Words = new[] { $"{says} says it hears {kindWords} {p.Name}".Trim() + (p.Headline.Length > 0 ? ": " + p.Headline : ""), "not heard by this desk, not linked" },
                        Route = new MenuRoute("Nodes"),
                        Band = EyePlane.Network,
                        App = p.Id,
                        Counted = false,
                        Origin = EyeOrigin.Relayed,
                    });
                    cardOfId[p.Id] = target;
                }
                if (pairs.Contains((target, card)) || relayed >= EyeNetwork.MaxRelayed) continue;
                relayed++;
                var proved = p.Link.Length > 0 && p.Verified is "key" or "operator";
                Claim(target, card, proved ? EyeEdgeKind.Follows : EyeEdgeKind.Hears, proved ? ShowContext.LightOf(p.Light) : CheckLight.Grey,
                    $"{says} says: " + (proved ? $"linked over {p.Link}, verified {p.Verified}" : "heard, not linked"), says, EyeOrigin.Relayed);
            }
        }

        // Sound: sources, outputs, the routes between them.
        var routed = new HashSet<string>(f.AudioRoutes.Where(r => !r.Muted).Select(r => r.DestinationKey), StringComparer.Ordinal);
        var routedSources = new HashSet<string>(f.AudioRoutes.Where(r => !r.Muted).Select(r => r.SourceId), StringComparer.Ordinal);
        foreach (var s in f.AudioSources.OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase))
        {
            Add(new EyeNode("asrc:" + s.Id, EyeKind.AudioSource, EyePlane.Audio, 0, s.Label.Length > 0 ? s.Label : s.Id, routedSources.Contains(s.Id) ? s.Kind : s.Kind + " · not routed", routedSources.Contains(s.Id) ? CheckLight.Green : CheckLight.Grey) { Route = new MenuRoute("Audio") });
        }
        var outs = new Dictionary<string, EyeAudioOut>(StringComparer.Ordinal);
        foreach (var o in f.AudioOuts.OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase))
        {
            outs[o.Key] = o;
            var light = o.Error.Length > 0 ? CheckLight.Red : o.Mute ? CheckLight.Grey : routed.Contains(o.Key) ? CheckLight.Green : CheckLight.Grey;
            var sub = o.Error.Length > 0 ? o.Error : o.Mute ? "muted" : routed.Contains(o.Key) ? $"peak {o.PeakDb:0} dB" : "no route";
            Add(new EyeNode("aout:" + o.Key, EyeKind.AudioOut, EyePlane.Audio, 3, o.Label.Length > 0 ? o.Label : o.Key, sub, light) { Words = new[] { o.Kind, o.Error }.Where(w => w.Length > 0).ToList(), Route = new MenuRoute("Audio") });
        }
        foreach (var r in f.AudioRoutes)
        {
            var from = "asrc:" + r.SourceId;
            var to = "aout:" + r.DestinationKey;
            if (!ids.Contains(from) || !ids.Contains(to)) continue;
            var light = r.Muted ? CheckLight.Grey : outs.TryGetValue(r.DestinationKey, out var o) && o.Error.Length > 0 ? CheckLight.Red : CheckLight.Green;
            Link(from, to, EyeEdgeKind.Routes, light, r.Muted ? "muted" : r.Followed ? $"follows the picture · {r.LevelDb:+0.0;-0.0} dB" : $"{r.LevelDb:+0.0;-0.0} dB");
        }

        // The room and the stream.
        if (f.Room is { } room)
        {
            Add(new EyeNode("room", EyeKind.Room, EyePlane.Room, 2, "Audience room", $"code {room.Code} · {(room.Phones == 1 ? "1 phone" : room.Phones + " phones")}" + (room.Words.Length > 0 ? " · " + room.Words : ""), room.Light) { Route = new MenuRoute("Arcade") });
            Link(DeskId, "room", EyeEdgeKind.Serves, room.Light, room.Phones > 0 ? "phones joined" : "waiting");
        }
        if (f.Stream is { } stream)
        {
            Add(new EyeNode("stream", EyeKind.Stream, EyePlane.Room, 3, "Stream", stream.Status.Length > 0 ? stream.Status : "streaming", stream.Light) { Route = new MenuRoute("Stream") });
            Link(DeskId, "stream", EyeEdgeKind.Sends, stream.Light, stream.Status);
        }

        var graph = new EyeGraph(nodes, edges) { MoreApps = Math.Max(0, more), Unplaced = unplaced };
        foreach (var (id, number) in numbers) graph.ScreenNumbers[id] = number;
        foreach (var (id, count) in named) graph.Named[id] = count;
        return graph;
    }

    private static string SourceSub(EyeSource s)
    {
        if (!s.Mounted) return s.Kind.Length > 0 ? s.Kind + " · not mounted" : "not mounted";
        return s.Status.Length > 0 ? s.Status : s.Kind.Length > 0 ? s.Kind + " · mounted" : "mounted";
    }

    /// <summary>The page of the rail that edits a kind of source.</summary>
    public static string SourcePage(string kind) => kind.ToLowerInvariant() switch
    {
        "ndi" => "NDI",
        "web" or "page" => "Media",
        "arcade" => "Arcade",
        _ => "Media",
    };
}
