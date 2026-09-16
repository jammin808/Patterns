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

/// <summary>A lens over the picture: everything, one band with the desk, or the problems and what they touch.</summary>
public enum EyeLens
{
    All,
    Video,
    Control,
    Audio,
    Room,
    Problems,
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
    /// <summary>The super-check's overall light — the desk's own.</summary>
    public CheckLight Health { get; init; } = CheckLight.Grey;
    public string HealthWords { get; init; } = "";
    /// <summary>Round 69: what the desk holds in memory and why, and the GPU cache's bound — one line, stable from tick to tick ("4 held: 2 on air, 1 armed, 1 idle · GPU cache limit 128 MB · rung none").</summary>
    public string MemoryWords { get; init; } = "";
    /// <summary>The super-check's rows that are not green, as words.</summary>
    public IReadOnlyList<string> Attention { get; init; } = Array.Empty<string>();
    /// <summary>Round 67.6: what the next TAKE alone arrives by, in words; "" for the show's own.</summary>
    public string NextTake { get; init; } = "";
    /// <summary>Round 67.8: the wall's take scope as the picker labels it ("every screen", "the focused screen", "the ticked screens"); "" on a node.</summary>
    public string TakeScope { get; init; } = "";
    /// <summary>Round 67.8: what the next TAKE will do — the plan's words ("→ 1 · Left, 2 · Right · 1 outside the scope keeps its picture") or its refusal; "" on a node.</summary>
    public string TakeWords { get; init; } = "";
    /// <summary>Round 72: a TAKE waiting under a video sting — the ticket the press froze, in words ("→ 1 · Left, 2 · Right when 'Whoosh' ends"); "" with none.</summary>
    public string Landing { get; init; } = "";
    public IReadOnlyList<EyeDisplay> Displays { get; init; } = Array.Empty<EyeDisplay>();
    public IReadOnlyList<EyeScreen> Screens { get; init; } = Array.Empty<EyeScreen>();
    public IReadOnlyList<EyeSource> Sources { get; init; } = Array.Empty<EyeSource>();
    public IReadOnlyList<EyeDevice> Devices { get; init; } = Array.Empty<EyeDevice>();
    public IReadOnlyList<EyeDeck> Decks { get; init; } = Array.Empty<EyeDeck>();
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
public sealed record EyeDevice(string Id, string Name, string Profile, string Address, bool Enabled, bool Open, string Status, CheckLight Light, string Words, string InputScreen = "", string Received = "");

/// <summary>A controller that said HELLO on the wire; Paired when it presented the show's token (or none is asked).</summary>
public sealed record EyeDeck(string Name, string Module, string Address, bool Paired);

/// <summary>A Companion heard on mDNS.</summary>
public sealed record EyeCompanion(string Host, string Address, bool Fresh, string Version);

public sealed record EyeOsc(int Port, string Words);

/// <summary>The twin link as this process sees it: Role "main" / "standby" / "" (alone), the other machine's name, the phase's words and light.</summary>
public sealed record EyeTwin(string Role, string Phase, string OtherName, string Words, CheckLight Light);

/// <summary>Another Patterns heard on the beacon.</summary>
public sealed record EyeNodeHeard(string Instance, string Kind, string Name, bool Fresh, bool Linked, string Words);

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
}

/// <summary>One link of the Eye, with its own light and its own words.</summary>
public sealed record EyeEdge(string From, string To, EyeEdgeKind Kind, CheckLight Light, string Words = "")
{
    /// <summary>"feeds", "drives", "carries"… — the word between the two things.</summary>
    public string Verb => Kind.ToString().ToLowerInvariant();
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

    private readonly Dictionary<string, EyeNode> _byId;
    private readonly Dictionary<string, List<string>> _adjacent;
    private readonly Dictionary<string, List<EyeEdge>> _edgesOf;
    private readonly List<string> _problems;

    private EyeGraph(List<EyeNode> nodes, List<EyeEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
        _byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _adjacent = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        _edgesOf = nodes.ToDictionary(n => n.Id, _ => new List<EyeEdge>(), StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (!_byId.ContainsKey(e.From) || !_byId.ContainsKey(e.To)) continue;
            _adjacent[e.From].Add(e.To);
            _adjacent[e.To].Add(e.From);
            _edgesOf[e.From].Add(e);
            _edgesOf[e.To].Add(e);
        }
        foreach (var n in nodes)
        {
            switch (n.Light)
            {
                case CheckLight.Red: Red++; break;
                case CheckLight.Amber: Amber++; break;
                case CheckLight.Green: Green++; break;
                default: Grey++; break;
            }
        }
        _problems = nodes.Where(n => n.Light == CheckLight.Red).OrderBy(ProblemOrder).ThenBy(n => n.Tier).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
            .Concat(nodes.Where(n => n.Light == CheckLight.Amber).OrderBy(ProblemOrder).ThenBy(n => n.Tier).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase))
            .Select(n => n.Id).ToList();
        Worst = _problems.Count > 0 ? _byId[_problems[0]] : null;
        Headline = Words();
    }

    public IReadOnlyList<EyeNode> Nodes { get; }
    public IReadOnlyList<EyeEdge> Edges { get; }
    public int Red { get; }
    public int Amber { get; }
    public int Green { get; }
    public int Grey { get; }

    /// <summary>The reds, then the ambers, in the picture's order — what NEXT and PREV walk.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Position of a node in the problems queue (0-based), or −1 when it is not red or amber.</summary>
    public int ProblemIndex(string id) => _problems.IndexOf(id);

    /// <summary>The first problem, or null when the picture is all green and grey.</summary>
    public EyeNode? Worst { get; }

    /// <summary>The worst light in the picture: the rail's colour.</summary>
    public CheckLight WorstLight => Red > 0 ? CheckLight.Red : Amber > 0 ? CheckLight.Amber : Green > 0 ? CheckLight.Green : CheckLight.Grey;

    /// <summary>One line that is true for the whole picture: "14 things · 1 red · 2 amber — Projector 1: carries 1080p60 against a 50 contract".</summary>
    public string Headline { get; }

    public static EyeGraph Empty { get; } = new(new List<EyeNode>(), new List<EyeEdge>());

    public EyeNode? Find(string id) => _byId.TryGetValue(id, out var n) ? n : null;

    /// <summary>The things linked to one, either way, in the edges' order.</summary>
    public IReadOnlyList<string> Neighbours(string id) => _adjacent.TryGetValue(id, out var l) ? l : Array.Empty<string>();

    public IReadOnlyList<EyeEdge> EdgesOf(string id) => _edgesOf.TryGetValue(id, out var l) ? l : Array.Empty<EyeEdge>();

    /// <summary>The edge between two things, either way, or null.</summary>
    public EyeEdge? EdgeBetween(string a, string b) => EdgesOf(a).FirstOrDefault(e => (e.From == a && e.To == b) || (e.From == b && e.To == a));

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

    /// <summary>Whether a thing is on a lens: everything; a band and the desk; the problems and what they touch.</summary>
    public bool Visible(EyeNode n, EyeLens lens) => lens switch
    {
        EyeLens.All => true,
        EyeLens.Problems => n.IsProblem || n.Kind == EyeKind.Desk || Neighbours(n.Id).Any(id => _byId.TryGetValue(id, out var o) && o.IsProblem),
        EyeLens.Video => n.Plane == EyePlane.Video || n.Kind == EyeKind.Desk,
        EyeLens.Control => n.Plane == EyePlane.Control || n.Kind == EyeKind.Desk,
        EyeLens.Audio => n.Plane == EyePlane.Audio || n.Kind == EyeKind.Desk,
        EyeLens.Room => n.Plane == EyePlane.Room || n.Kind == EyeKind.Desk,
        _ => true,
    };

    /// <summary>A lens by its word ("video", "problems"); null for a word that is none.</summary>
    public static EyeLens? ParseLens(string word) => word.Trim().ToLowerInvariant() switch
    {
        "" or "all" or "everything" => EyeLens.All,
        "video" or "pictures" or "picture" => EyeLens.Video,
        "control" or "controllers" => EyeLens.Control,
        "audio" or "sound" => EyeLens.Audio,
        "room" or "audience" => EyeLens.Room,
        "problems" or "problem" or "wrong" => EyeLens.Problems,
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

    /// <summary>The graph in words, one line per thing and per link — what the assistant reads.</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>(Nodes.Count + Edges.Count + 1) { Headline };
        foreach (var n in Nodes) lines.Add($"{n.Plane.ToString().ToUpperInvariant()} · {n.Kind}: {n.Label} — {Light(n.Light)}{(n.Sub.Length > 0 ? " · " + n.Sub : "")}");
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
        var n = Nodes.Count;
        if (n == 0) return "Nothing to see yet — the desk has not read its rig.";
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
        void Link(string from, string to, EyeEdgeKind kind, CheckLight light, string words = "") => edges.Add(new EyeEdge(from, to, kind, light, words));

        // The desk: the hub of every band.
        var deskWords = new List<string>();
        if (f.Build.Length > 0) deskWords.Add($"Build {f.Build}");
        if (f.HealthWords.Length > 0) deskWords.Add(f.HealthWords);
        if (f.MemoryWords.Length > 0) deskWords.Add(f.MemoryWords);                                   // round 69: the memory line
        if (f.NextTake.Length > 0) deskWords.Add($"Next take: {f.NextTake} (one shot)");
        if (f.TakeWords.Length > 0) deskWords.Add($"Next TAKE ({f.TakeScope}) {f.TakeWords}");
        if (f.Landing.Length > 0) deskWords.Add($"Landing {f.Landing}");                             // round 72: the ticket a sting will land
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
            var words = new[] { d.Profile, d.Address, d.Status, d.Words, d.Received.Length > 0 ? "Receives " + d.Received : "" }.Where(w => w.Length > 0).ToList();
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
            Add(new EyeNode(id, EyeKind.Deck, EyePlane.Control, 0, d.Name.Length > 0 ? d.Name : d.Address, sub, light) { Words = new[] { d.Address, d.Module.Length > 0 ? "module " + d.Module : "" }.Where(w => w.Length > 0).ToList(), Route = new MenuRoute("Remote") });
            Link(id, DeskId, EyeEdgeKind.Controls, light, d.Paired ? "paired" : "not paired");
        }
        foreach (var c in f.Companions.Where(c => !deckAddresses.Contains(c.Address)).OrderBy(c => c.Host, StringComparer.OrdinalIgnoreCase))
        {
            var id = "companion:" + c.Host;
            Add(new EyeNode(id, EyeKind.Companion, EyePlane.Control, 0, "Companion " + c.Host, c.Fresh ? "heard on mDNS · not connected" : "was heard · gone quiet", CheckLight.Grey) { Words = new[] { c.Address, c.Version.Length > 0 ? "v" + c.Version : "" }.Where(w => w.Length > 0).ToList(), Route = new MenuRoute("Remote") });
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
            Add(new EyeNode("osc", EyeKind.Osc, EyePlane.Control, 0, "OSC", $"port {osc.Port}" + (osc.Words.Length > 0 ? " · " + osc.Words : ""), CheckLight.Green) { Route = new MenuRoute("Remote") });
            Link("osc", DeskId, EyeEdgeKind.Controls, CheckLight.Green);
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
        foreach (var n in f.Nodes.OrderBy(n => n.Kind, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            var id = "node:" + n.Instance;
            var light = !n.Fresh ? CheckLight.Red : n.Linked ? CheckLight.Green : CheckLight.Grey;
            var sub = !n.Fresh ? "gone quiet" : n.Linked ? "linked" + (n.Words.Length > 0 ? " · " + n.Words : "") : "heard" + (n.Words.Length > 0 ? " · " + n.Words : "");
            var kind = n.Kind.Length > 0 ? char.ToUpperInvariant(n.Kind[0]) + n.Kind[1..].ToLowerInvariant() : "Node";
            Add(new EyeNode(id, EyeKind.Node, EyePlane.Control, 2, $"{kind} {n.Name}".Trim(), sub, light) { Words = new[] { kind + " node", n.Words }.Where(w => w.Length > 0).ToList(), Route = new MenuRoute("Nodes") });
            if (n.Linked) Link(id, DeskId, EyeEdgeKind.Follows, light, n.Fresh ? "linked" : "gone quiet");
            else Link(id, DeskId, EyeEdgeKind.Hears, light, n.Fresh ? "heard on the beacon" : "gone quiet");
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

        var graph = new EyeGraph(nodes, edges);
        foreach (var (id, number) in numbers) graph.ScreenNumbers[id] = number;
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
