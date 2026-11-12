namespace Patterns.Core.Services;

/// <summary>A rectangle of the Eye's world, in its own units (doubles: the camera scales them).</summary>
public readonly record struct EyeRect(double X, double Y, double W, double H)
{
    public static readonly EyeRect Empty = new(0, 0, 0, 0);

    public double Right => X + W;
    public double Bottom => Y + H;
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
    public bool IsEmpty => W <= 0 || H <= 0;

    public bool Contains(double px, double py) => px >= X && px < Right && py >= Y && py < Bottom;

    public bool Intersects(EyeRect o) => !IsEmpty && !o.IsEmpty && X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public EyeRect Union(EyeRect o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        var x = Math.Min(X, o.X);
        var y = Math.Min(Y, o.Y);
        return new EyeRect(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
    }

    public EyeRect Inflate(double d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);
}

/// <summary>One band of the picture: its plane, its word and where it is drawn.</summary>
public sealed record EyeBand(EyePlane Plane, string Label, EyeRect Rect)
{
    /// <summary>A grey line beside the word — on the NETWORK band, what it leaves out and whether the fit view reads its labels.</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// The NETWORK band's view — state the desk keeps and nothing else moves (a focus, a light or a new thing never does):
/// the clusters the operator opened and folded (card ids), each app's sticky slot, each open cluster's sticky cell in the grid,
/// and the canvas the fit view is read on (the Eye page's canvas, the side card beside it; the reference desk's until the page
/// says its own).
/// </summary>
public sealed record EyeNetworkView
{
    public IReadOnlyCollection<string> Opened { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> Folded { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, int> Slots { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Each open cluster's cell in the grid, as the last placement left it (<see cref="EyePlacement.Cells"/>).</summary>
    public IReadOnlyDictionary<string, int> Cells { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
    public double CanvasW { get; init; } = EyeLayout.ReferenceCanvasW;
    public double CanvasH { get; init; } = EyeLayout.ReferenceCanvasH;

    public static EyeNetworkView Default { get; } = new();
}

/// <summary>Where everything is: a rectangle per thing, the bands, and the whole picture's bounds.</summary>
public sealed class EyePlacement
{
    private readonly Dictionary<string, EyeRect> _rects;
    private readonly List<KeyValuePair<string, EyeRect>> _ordered;

    public EyePlacement(Dictionary<string, EyeRect> rects, IReadOnlyList<EyeBand> bands, EyeRect bounds)
    {
        _rects = rects;
        _ordered = rects.ToList();
        Bands = bands;
        Bounds = bounds;
    }

    public static EyePlacement Empty { get; } = new(new Dictionary<string, EyeRect>(StringComparer.Ordinal), Array.Empty<EyeBand>(), EyeRect.Empty);

    public IReadOnlyDictionary<string, EyeRect> Rects => _rects;
    public IReadOnlyList<EyeBand> Bands { get; }
    public EyeRect Bounds { get; }

    /// <summary>The open clusters' cards, in slot order — the expansion set as it is drawn.</summary>
    public IReadOnlyList<string> Expanded { get; init; } = Array.Empty<string>();

    /// <summary>Each open cluster's cell in the grid — kept by the desk and handed back in the next view, so a cluster keeps its cell.</summary>
    public IReadOnlyDictionary<string, int> Cells { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Per app card, the things its context names that are not drawn — every one while it is folded.</summary>
    public IReadOnlyDictionary<string, int> Hidden { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>The apps heard past the Eye's bound (one grey card stands for them).</summary>
    public int LeftApps { get; init; }

    /// <summary>The things the drawn apps name that are not drawn — folded clusters and past the dozen.</summary>
    public int LeftItems { get; init; }

    /// <summary>The fit view's scale on the canvas it was placed for.</summary>
    public double FitScale { get; init; }

    /// <summary>The fit view is under the label scale even with every cluster the operator left open folded — it shows marks only.</summary>
    public bool MarksOnly { get; init; }

    /// <summary>What the NETWORK band says beside its word — what is left out and whether the fit view reads it; "" with no band.</summary>
    public string Note { get; init; } = "";

    public bool IsExpanded(string cardId) => Expanded.Contains(cardId, StringComparer.Ordinal);

    public EyeRect Of(string id) => _rects.TryGetValue(id, out var r) ? r : EyeRect.Empty;

    /// <summary>The thing under a world point, or null — rectangle maths, no tree needed at the Eye's sizes.</summary>
    public string? At(double wx, double wy)
    {
        foreach (var (id, rect) in _ordered)
        {
            if (rect.Contains(wx, wy)) return id;
        }
        return null;
    }

    /// <summary>
    /// The rectangle around a thing and what it links to — what a focus fits. A thing of a folded cluster has no place: the focus
    /// fits its app's card, where it is folded, and never the world's corner.
    /// </summary>
    public EyeRect Around(string id, EyeGraph graph)
    {
        var r = Of(id);
        if (r.IsEmpty && graph.Find(id) is { App.Length: > 0 } folded) return Of("app:" + folded.App);
        foreach (var n in graph.Neighbours(id)) r = r.Union(Of(n));
        return r;
    }
}

/// <summary>
/// The Eye's layout (round 66): a deterministic grid, never a force simulation. Bands top to
/// bottom in <see cref="BandOrder"/>; within a band the tiers are columns in the direction the
/// signal travels; within a cell the things stack in a stable order and the stack is centred on
/// the band. The same show always draws the same picture, a thing keeps its place while its
/// neighbours change, and nothing overlaps by construction — O(n), rebuilt with the graph.
/// The other apps sit on a NETWORK band above CONTROL, each a cluster of its own — its card, then its things in a
/// three-column grid in its kind's column order — ⌈√open⌉ clusters to a row in slot order, the folded cards in a strip below
/// them; as many clusters open as keep the fit view's scale at <see cref="LabelScale"/> or more on the canvas it is read on.
/// A cluster keeps its cell and a card its slot, so one cluster that opens, folds or leaves moves no other; the clusters move
/// together only when the grid's columns change (the highest cell in use crossing a square) and the strip only when the grid
/// gains or loses a row. The measured cause: before the band every other app's thing stacked in one CONTROL column, 48 things
/// 3,304 units tall, and no label showed at the fit view on any canvas under 1,237 px (PLAN §113.1).
/// </summary>
public static class EyeLayout
{
    public const double NodeW = 200;
    public const double NodeH = 52;
    public const double ColW = 280;
    public const double RowGap = 16;
    public const double BandGap = 72;
    public const double BandPad = 28;
    public const int Tiers = 5;

    /// <summary>The apps the Eye draws; one grey card says how many more are heard (the hello reader keeps 128 peers).</summary>
    public const int MaxApps = 128;

    /// <summary>The clusters open at once — what a person reads; the rest are cards in the strip.</summary>
    public const int MaxExpanded = 16;

    /// <summary>The things an app's cluster draws: the first dozen, its reds and ambers first.</summary>
    public const int ClusterThings = 12;

    /// <summary>A cluster's things in rows of three under its card.</summary>
    public const int ClusterColumns = 3;

    /// <summary>The gap between two things side by side in a cluster or the strip.</summary>
    public const double CellGap = 16;

    /// <summary>The gap between two clusters, and between the clusters and the strip.</summary>
    public const double ClusterGap = 48;

    /// <summary>The scale from which the canvas draws a thing's label (below it a thing is a mark with no word).</summary>
    public const double LabelScale = 0.35;

    /// <summary>The Eye page's side card beside its canvas (EyeSection.axaml's second column).</summary>
    public const double SideCardW = 300;

    /// <summary>The reference canvas until the page says its own — a 1,920 × 1,080 desk's width less the side card, and its height.</summary>
    public const double ReferenceCanvasW = 1920 - SideCardW;

    public const double ReferenceCanvasH = 1080;

    /// <summary>A cluster's width: three things side by side.</summary>
    public const double ClusterW = ClusterColumns * NodeW + (ClusterColumns - 1) * CellGap;

    /// <summary>A cell of the grid's height: a card and a full dozen under it, whatever the cluster draws, so no cluster's count moves another.</summary>
    public const double ClusterH = (1 + ClusterThings / ClusterColumns) * NodeH + ClusterThings / ClusterColumns * RowGap;

    /// <summary>The folded cards to a row of the strip — as wide as the widest grid (⌈√<see cref="MaxExpanded"/>⌉ clusters of three).</summary>
    public const int StripAcross = 4 * ClusterColumns;

    public static readonly IReadOnlyList<EyePlane> BandOrder = new[] { EyePlane.Network, EyePlane.Control, EyePlane.Video, EyePlane.Audio, EyePlane.Room };

    public static string BandLabel(EyePlane plane) => plane switch
    {
        EyePlane.Network => "NETWORK",
        EyePlane.Control => "CONTROL",
        EyePlane.Video => "VIDEO",
        EyePlane.Audio => "AUDIO",
        _ => "ROOM",
    };

    /// <summary>Where a thing is drawn and the lens it is on, in words — "VIDEO", or "NETWORK · VIDEO" for another app's.</summary>
    public static string Where(EyeNode n) => n.Band == n.Plane ? BandLabel(n.Plane) : $"{BandLabel(n.Band)} · {BandLabel(n.Plane)}";

    /// <summary>(Show Bus §6's kind table): the plane another app's thing of a kind is on, for the lenses.</summary>
    public static EyePlane KindPlane(string? kind) => Kind(kind).Plane;

    /// <summary>(Show Bus §6's kind table): the column order of a kind in its cluster — links and health, the show, screens, far ends.</summary>
    public static int KindColumn(string? kind) => Kind(kind).Column;

    private static (EyePlane Plane, int Column) Kind(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "link" or "deck" or "client" or "desk" or "node" or "twin" or "assistant" or "ai" or "approvals" or "health" or "machine" or "preflight"
            or "fault" or "clock" or "sync" or "mirror" or "fleet" or "decode" or "optimise" or "scan" or "calibration" or "align" or "site" or "survey" => (EyePlane.Control, 0),
        "timer" or "schedule" or "department" or "cue" or "message" or "stack" or "playlist" or "playlists" or "chase" or "timecode" or "show" => (EyePlane.Control, 1),
        "air" or "media" or "content" or "input" or "videoio" => (EyePlane.Video, 1),
        "screen" or "output" or "outputs" or "send" or "stage" => (EyePlane.Video, 2),
        "projector" or "surface" or "led" => (EyePlane.Video, 3),
        "room" or "meetings" or "prompt" or "help" or "phone" or "audience" => (EyePlane.Room, 1),
        "audio" => (EyePlane.Audio, 1),
        _ => (EyePlane.Control, 1),
    };

    public static EyePlacement Place(EyeGraph graph) => Place(graph, EyeNetworkView.Default);

    /// <summary>
    /// Each app's sticky slot — the one it holds while its card is drawn; a new card takes the lowest free slot, in the
    /// picture's order; a card that leaves the picture frees its own. The same history of pictures gives the same slots.
    /// </summary>
    public static Dictionary<string, int> Slots(EyeGraph graph, IReadOnlyDictionary<string, int> kept)
    {
        var cards = graph.Nodes.Where(n => n.Kind == EyeKind.ShowApp && n.Band == EyePlane.Network && n.Id != EyeGraph.MoreId).Select(n => n.Id).ToList();
        var drawn = cards.ToHashSet(StringComparer.Ordinal);
        var slots = kept.Where(kv => drawn.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var taken = slots.Values.ToHashSet();
        var next = 0;
        foreach (var card in cards)
        {
            if (slots.ContainsKey(card)) continue;
            while (taken.Contains(next)) next++;
            slots[card] = next;
            taken.Add(next);
        }
        return slots;
    }

    /// <summary>
    /// The picture placed for a view: this desk's bands as round 66 placed them, under a NETWORK band when another app is drawn.
    /// The open clusters are the operator's opened ones (slot order, at most <see cref="MaxExpanded"/>) and then as many more —
    /// the needed apps first, then the linked, then by slot — as keep the fit view at <see cref="LabelScale"/> or more on the
    /// view's canvas; a folded cluster stays folded. When even the operator's alone cannot, the fit view shows marks only and the
    /// band says so. Nothing here reads a light, so a light that changes never opens or moves a cluster.
    /// </summary>
    public static EyePlacement Place(EyeGraph graph, EyeNetworkView view)
    {
        var own = PlaceOwn(graph);
        var net = Network.Of(graph, view);
        if (net.Cards.Count == 0 && net.More is null)
        {
            // No other app: the fit view of this desk's own bands, said on the top band when it shows marks only.
            var fit = EyeCamera.FitScale(own.Bounds, view.CanvasW, view.CanvasH);
            var marks = fit > 0 && fit < LabelScale;
            var note = marks ? MarksOnlyWords : "";
            var bands = own.Bands.Select((b, i) => i == 0 && marks ? b with { Note = note } : b).ToList();
            return new EyePlacement(own.Rects, bands, own.Bounds) { FitScale = fit, MarksOnly = marks, Note = note };
        }
        var folded = new HashSet<string>(view.Folded, StringComparer.Ordinal);
        var asked = new HashSet<string>(view.Opened, StringComparer.Ordinal);
        var opened = net.Openable.Where(c => asked.Contains(c) && !folded.Contains(c)).Take(MaxExpanded).ToList();
        var openedSet = new HashSet<string>(opened, StringComparer.Ordinal);
        var fill = net.Openable.Where(c => !openedSet.Contains(c) && !folded.Contains(c))
            .OrderBy(c => net.Needed.Contains(c) ? 0 : 1).ThenBy(c => net.Linked.Contains(c) ? 0 : 1).ThenBy(c => net.Slot[c]).ToList();
        var room = Math.Min(MaxExpanded - opened.Count, fill.Count);
        (HashSet<string> Open, double Scale) Try(int k)
        {
            var trial = opened.Concat(fill.Take(k)).ToHashSet(StringComparer.Ordinal);
            var (w, h) = net.Measure(trial, view.Cells, own.Bounds.IsEmpty ? 0 : own.Bounds.W);
            return (trial, EyeCamera.FitScale(Stack(own.Bounds, w, h), view.CanvasW, view.CanvasH));
        }
        var pick = Try(room);
        for (var k = room - 1; k >= 0 && pick.Scale < LabelScale; k--) pick = Try(k);
        return net.Place(graph, own, pick.Open, view.Cells, pick.Scale, pick.Scale < LabelScale);
    }

    /// <summary>What a fit view under the label scale says: it shows marks only.</summary>
    public const string MarksOnlyWords = "too many to read at this size: marks only — zoom or focus to read";

    /// <summary>
    /// Each open cluster's cell: the one it held while it stayed open, else the lowest cell free, in slot order. A cluster that
    /// folds frees its cell, which stays a gap until another opens into it. Cells run below <see cref="MaxExpanded"/>.
    /// </summary>
    public static Dictionary<string, int> Cells(IReadOnlyList<string> openInSlotOrder, IReadOnlyDictionary<string, int> kept)
    {
        var open = new HashSet<string>(openInSlotOrder, StringComparer.Ordinal);
        var cells = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<int>();
        foreach (var card in openInSlotOrder)
        {
            if (kept.TryGetValue(card, out var cell) && cell >= 0 && cell < MaxExpanded && open.Contains(card) && taken.Add(cell)) cells[card] = cell;
        }
        var next = 0;
        foreach (var card in openInSlotOrder)
        {
            if (cells.ContainsKey(card)) continue;
            while (taken.Contains(next)) next++;
            cells[card] = next;
            taken.Add(next);
        }
        return cells;
    }

    /// <summary>The whole picture's bounds with the NETWORK band of a size on top of this desk's bands.</summary>
    private static EyeRect Stack(EyeRect own, double netW, double netH)
        => new(0, 0, Math.Max(own.IsEmpty ? 0 : own.W, netW), netH + (own.IsEmpty ? 0 : BandGap + own.H));

    private sealed record Own(Dictionary<string, EyeRect> Rects, List<EyeBand> Bands, EyeRect Bounds);

    /// <summary>This desk's own bands (every band but NETWORK), from y = 0 down — round 66's grid, each thing by its band.</summary>
    private static Own PlaceOwn(EyeGraph graph)
    {
        var rects = new Dictionary<string, EyeRect>(StringComparer.Ordinal);
        var bands = new List<EyeBand>();
        var bounds = EyeRect.Empty;
        var width = Tiers * ColW + BandPad;
        double y = 0;
        foreach (var plane in BandOrder)
        {
            if (plane == EyePlane.Network) continue;
            var cells = new List<EyeNode>[Tiers];
            var any = false;
            foreach (var n in graph.Nodes)
            {
                if (n.Band != plane) continue;
                var tier = Math.Clamp(n.Tier, 0, Tiers - 1);
                (cells[tier] ??= new List<EyeNode>()).Add(n);
                any = true;
            }
            if (!any) continue;
            var deepest = cells.Max(c => c?.Count ?? 0);
            var inner = deepest * NodeH + (deepest - 1) * RowGap;
            var bandRect = new EyeRect(0, y, width, inner + 2 * BandPad);
            bands.Add(new EyeBand(plane, BandLabel(plane), bandRect));
            for (var tier = 0; tier < Tiers; tier++)
            {
                var cell = cells[tier];
                if (cell is null) continue;
                cell.Sort(static (a, b) =>
                {
                    var byLabel = string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
                    return byLabel != 0 ? byLabel : string.CompareOrdinal(a.Id, b.Id);
                });
                var stack = cell.Count * NodeH + (cell.Count - 1) * RowGap;
                var top = y + BandPad + (inner - stack) / 2;
                var x = BandPad + tier * ColW;
                for (var i = 0; i < cell.Count; i++)
                {
                    var rect = new EyeRect(x, top + i * (NodeH + RowGap), NodeW, NodeH);
                    rects[cell[i].Id] = rect;
                    bounds = bounds.Union(rect);
                }
            }
            bounds = bounds.Union(bandRect);
            y += bandRect.H + BandGap;
        }
        return new Own(rects, bands, bounds);
    }

    /// <summary>The NETWORK band's things: the cards in slot order, each card's things in its kind's column order, and the grey card for the rest.</summary>
    private sealed class Network
    {
        public List<EyeNode> Cards { get; } = new();
        public Dictionary<string, List<EyeNode>> Things { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Slot { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Needed { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Linked { get; } = new(StringComparer.Ordinal);
        public List<string> Openable { get; } = new();
        public EyeNode? More { get; private set; }

        public static Network Of(EyeGraph graph, EyeNetworkView view)
        {
            var net = new Network();
            var slots = Slots(graph, view.Slots);
            foreach (var n in graph.Nodes)
            {
                if (n.Band != EyePlane.Network) continue;
                if (n.Id == EyeGraph.MoreId) net.More = n;
                else if (n.Kind == EyeKind.ShowApp)
                {
                    net.Cards.Add(n);
                    net.Slot[n.Id] = slots[n.Id];
                    if (n.Counted) net.Needed.Add(n.Id);
                }
                else if (n.App.Length > 0)
                {
                    var card = "app:" + n.App;
                    if (!net.Things.TryGetValue(card, out var list)) net.Things[card] = list = new List<EyeNode>();
                    list.Add(n);
                }
            }
            foreach (var e in graph.Edges)
            {
                if (e.Counted) net.Linked.Add(e.From == EyeGraph.DeskId ? e.To : e.From);
            }
            net.Cards.Sort((a, b) => net.Slot[a.Id].CompareTo(net.Slot[b.Id]));
            foreach (var card in net.Cards.Select(c => c.Id).ToList())
            {
                if (!net.Things.TryGetValue(card, out var things)) continue;
                net.Things[card] = things.OrderBy(t => Math.Clamp(t.Tier, 0, 3)).ToList();   // a stable sort: the context's order within a column
                net.Openable.Add(card);
            }
            return net;
        }

        private int Count(string card) => Things.TryGetValue(card, out var t) ? Math.Min(t.Count, ClusterThings) : 0;

        private static int PerRow(int cells) => cells == 0 ? 0 : (int)Math.Ceiling(Math.Sqrt(cells));

        /// <summary>The band's width and height with a set of clusters open, beside this desk's bands of a width.</summary>
        public (double W, double H) Measure(IReadOnlySet<string> open, IReadOnlyDictionary<string, int> kept, double ownW)
            => Lay(CellsOf(open, kept), ownW, 0, null);

        /// <summary>The open clusters' cells, the cells they kept first (<see cref="EyeLayout.Cells"/>).</summary>
        private Dictionary<string, int> CellsOf(IReadOnlySet<string> open, IReadOnlyDictionary<string, int> kept)
            => EyeLayout.Cells(Cards.Where(c => open.Contains(c.Id)).Select(c => c.Id).ToList(), kept);

        /// <summary>
        /// Lays the band out from <paramref name="top"/> — each open cluster at its cell, ⌈√(cells in use)⌉ to a row, every cell
        /// a full cluster's size; then the strip, each folded card at its slot's place, <see cref="StripAcross"/> to a row, and the
        /// grey card after the last slot — into <paramref name="rects"/> when given. A gap stays where a cluster folded or an app
        /// left, so nothing else moves.
        /// </summary>
        private (double W, double H) Lay(IReadOnlyDictionary<string, int> cells, double ownW, double top, Dictionary<string, EyeRect>? rects)
        {
            var right = Math.Max(ownW, Tiers * ColW + BandPad);
            var y = top + BandPad;
            if (cells.Count > 0)
            {
                var high = cells.Values.Max();
                var perRow = PerRow(high + 1);
                var rows = high / perRow + 1;
                foreach (var card in Cards)
                {
                    if (!cells.TryGetValue(card.Id, out var cell)) continue;
                    var x = BandPad + cell % perRow * (ClusterW + ClusterGap);
                    var cy = y + cell / perRow * (ClusterH + ClusterGap);
                    right = Math.Max(right, x + ClusterW + BandPad);
                    if (rects is null) continue;
                    rects[card.Id] = new EyeRect(x, cy, NodeW, NodeH);
                    var things = Things.TryGetValue(card.Id, out var t) ? t.Take(ClusterThings).ToList() : new List<EyeNode>();
                    for (var j = 0; j < things.Count; j++)
                    {
                        rects[things[j].Id] = new EyeRect(x + j % ClusterColumns * (NodeW + CellGap), cy + (1 + j / ClusterColumns) * (NodeH + RowGap), NodeW, NodeH);
                    }
                }
                y += rows * ClusterH + (rows - 1) * ClusterGap + ClusterGap;
            }
            var strip = Cards.Where(c => !cells.ContainsKey(c.Id)).Select(c => (Node: c, At: Slot[c.Id])).ToList();
            if (More is not null) strip.Add((More, Cards.Count == 0 ? 0 : Cards.Max(c => Slot[c.Id]) + 1));
            if (strip.Count > 0)
            {
                foreach (var (node, at) in strip)
                {
                    var x = BandPad + at % StripAcross * (NodeW + CellGap);
                    right = Math.Max(right, x + NodeW + BandPad);
                    if (rects is not null) rects[node.Id] = new EyeRect(x, y + at / StripAcross * (NodeH + RowGap), NodeW, NodeH);
                }
                var rows = strip.Max(s => s.At) / StripAcross + 1;
                y += rows * NodeH + (rows - 1) * RowGap + ClusterGap;
            }
            var height = y - ClusterGap - top + BandPad;
            return (right, Math.Max(height, NodeH + 2 * BandPad));
        }

        public EyePlacement Place(EyeGraph graph, Own own, IReadOnlySet<string> open, IReadOnlyDictionary<string, int> kept, double scale, bool marksOnly)
        {
            var rects = new Dictionary<string, EyeRect>(StringComparer.Ordinal);
            var cells = CellsOf(open, kept);
            var (w, h) = Lay(cells, own.Bounds.IsEmpty ? 0 : own.Bounds.W, 0, rects);
            var bandRect = new EyeRect(0, 0, w, h);
            var shift = h + BandGap;
            var bands = new List<EyeBand>();
            var bounds = bandRect;
            foreach (var r in rects.Values) bounds = bounds.Union(r);
            foreach (var (id, r) in own.Rects)
            {
                var moved = new EyeRect(r.X, r.Y + shift, r.W, r.H);
                rects[id] = moved;
                bounds = bounds.Union(moved);
            }
            var hidden = new Dictionary<string, int>(StringComparer.Ordinal);
            var expanded = new List<string>();
            foreach (var card in Cards)
            {
                var isOpen = cells.ContainsKey(card.Id);
                if (isOpen) expanded.Add(card.Id);
                var left = Math.Max(0, graph.NamedBy(card.Id) - (isOpen ? Count(card.Id) : 0));
                if (left > 0) hidden[card.Id] = left;
            }
            var leftItems = hidden.Values.Sum();
            var note = Words(expanded.Count, Cards.Count, graph.MoreApps, leftItems, marksOnly);
            bands.Add(new EyeBand(EyePlane.Network, BandLabel(EyePlane.Network), bandRect) { Note = note });
            foreach (var b in own.Bands)
            {
                var moved = b with { Rect = new EyeRect(b.Rect.X, b.Rect.Y + shift, b.Rect.W, b.Rect.H) };
                bands.Add(moved);
                bounds = bounds.Union(moved.Rect);
            }
            return new EyePlacement(rects, bands, bounds)
            {
                Expanded = expanded,
                Cells = cells,
                Hidden = hidden,
                LeftApps = graph.MoreApps,
                LeftItems = leftItems,
                FitScale = scale,
                MarksOnly = marksOnly,
                Note = note,
            };
        }

        /// <summary>What the band says beside its word: the clusters open, what is left out, and a fit view that shows marks only.</summary>
        private static string Words(int open, int cards, int moreApps, int leftItems, bool marksOnly)
        {
            var words = new List<string> { $"{open} of {cards} app{(cards == 1 ? "" : "s")} open" };
            if (moreApps > 0) words.Add($"{moreApps} more heard, not drawn");
            if (leftItems > 0) words.Add($"{leftItems} thing{(leftItems == 1 ? "" : "s")} not drawn");
            if (marksOnly) words.Add(MarksOnlyWords);
            return string.Join(" · ", words);
        }
    }
}
