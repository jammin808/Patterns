namespace Patterns.Core.Services;

/// <summary>One content target — a screen or a joined canvas — as the next CUT / TAKE sees it.</summary>
/// <param name="Id">The screen id or the canvas key.</param>
/// <param name="Label">The wall's name for it: "2 · Comfort", "A · Main wall".</param>
/// <param name="IsCanvas">A joined canvas (a group on the wall).</param>
/// <param name="IsMirror">A repeater: it draws its source's picture and has none of its own.</param>
/// <param name="Locked">LOCK on the tile: it keeps its picture through looks, cues, TAKE ALL and stingers.</param>
/// <param name="Armed">ARM on the tile: the next CUT / TAKE may change it.</param>
/// <param name="Ticked">The tick at the top of the tile.</param>
/// <param name="Shape">Round 75: what the target is, in the wall's words — "a screen of its own", "a repeater of 1 · Left", "Canvas A of 2 · Right, 3 · Rear" — kept by a ticket at the press and said at its landing when the target is something else now. "" when the caller keeps no shapes.</param>
/// <param name="KeepsOwn">Round 80: an armed target whose own picture is on the air and unchanged in the preview — a wall take leaves it as it is (round 30's rule), and the plan says so before the press instead of listing it as taken.</param>
/// <param name="ShapeKey">Round 76: what the target is, structurally (<see cref="TakeShapes"/>) — "own", "mirror:a", "canvas:a|b" — the key a landing compares, so a label that changed during the clip never holds a landing and a member that joined or left always does. "" when the caller keeps no keys, and the words decide as in round 75.</param>
/// <param name="Kind">Round 81: the group the target is in, by kind — main, confidence, info, repeater; "" for a canvas whose screens differ (<see cref="ScreenRoles.KindOf"/>). GROUPS takes to every target of the ticked tiles' kinds.</param>
public sealed record TakeTarget(string Id, string Label, bool IsCanvas = false, bool IsMirror = false, bool Locked = false, bool Armed = true, bool Ticked = false, string Shape = "", string ShapeKey = "", bool KeepsOwn = false, string Kind = "");

/// <summary>
/// Round 76: a target's structural identity apart from its words. Round 75 compared the wall's words
/// ("a repeater of 1 · Left") at the landing, and the words carry labels — a screen renamed during the
/// clip read as a topology change: fail-safe, not true. The key carries ids alone: what a target is
/// (its own, a repeater, a canvas), and of whom. Transactions compare keys; operators read words.
/// </summary>
public static class TakeShapes
{
    /// <summary>A screen with a picture of its own.</summary>
    public const string Own = "own";

    /// <summary>A repeater of the target with this id.</summary>
    public static string Mirror(string sourceId) => "mirror:" + sourceId;

    /// <summary>A joined canvas of these members — order-blind, so a presentation reorder is no change.</summary>
    public static string Canvas(IEnumerable<string> memberIds) => "canvas:" + string.Join("|", memberIds.OrderBy(m => m, StringComparer.Ordinal));
}

/// <summary>A target inside the scope that the take leaves alone, and the one word that says why.</summary>
public sealed record TakeHeld(string Id, string Label, string Reason)
{
    public const string LockedReason = "locked";
    public const string NotArmedReason = "not armed";
    public const string RepeaterReason = "a repeater";
    /// <summary>Round 72: LOCKED after the press — a ticket's landing holds it, whenever the lock came.</summary>
    public const string LockedSinceReason = "locked since the press";
    /// <summary>Round 72: no longer a target in the rig when the ticket lands.</summary>
    public const string GoneReason = "gone from the rig";
    /// <summary>Round 75: the target is not what the press saw — a screen made a repeater, a canvas whose members moved, a screen that joined a canvas — and keeps its picture; <see cref="ChangedSince"/> adds what it is now.</summary>
    public const string ChangedSinceReason = "changed since the press";
    /// <summary>"changed since the press — now a repeater of 1 · Left".</summary>
    public static string ChangedSince(string now) => now.Length == 0 ? ChangedSinceReason : $"{ChangedSinceReason} — now {now}";
}

/// <summary>
/// What the next CUT / TAKE changes and what it leaves alone — one rule for the wall's keys, the
/// PGM tile's menu, the multiview's NEXT TAKE line, the assistant and the wire (round 67).
///
/// The rules, in the order they bind: LOCKED means locked — a locked target is never taken, whatever
/// the scope says. ALL ARMED takes every armed target and no other. FOCUSED takes the tile being
/// edited alone (the PGM tile focused is the programme: every armed screen). TICKED takes the ticked
/// tiles alone; GROUPS every target in the groups of the ticked tiles (round 81: a group is what a
/// screen is for — main, confidence, info — never a joined canvas); GROUP MAIN / CONFIDENCE / INFO a
/// group by kind; CANVASES the ticked joined canvases. SCREEN n, CANVAS A and an id name their target.
/// Inside any scope ARM still counts (an un-armed tile keeps the picture the audience sees) and a
/// repeater is never taken (it draws its source). Round 81: a scoped take lands on its targets alone,
/// as their own pictures — everything outside the scope is untouched.
///
/// A plan that would change nothing is a refusal that names why, never a silent success: attempts are
/// not facts, and "TAKE" with no screen moved is not a take.
/// </summary>
public sealed record TakePlan
{
    public FadeScope Scope { get; init; }

    /// <summary>The targets the take changes, in wall order.</summary>
    public IReadOnlyList<string> Taken { get; init; } = Array.Empty<string>();

    /// <summary>Targets the scope names that the take leaves alone all the same — locked, not armed, a repeater — with the reason.</summary>
    public IReadOnlyList<TakeHeld> Held { get; init; } = Array.Empty<TakeHeld>();

    /// <summary>Targets outside the scope: untouched by the take (round 81 — a scoped take lands on its targets alone), and nothing more is said about them.</summary>
    public IReadOnlyList<string> Outside { get; init; } = Array.Empty<string>();

    /// <summary>Why nothing would change, or null when something would.</summary>
    public string? Refusal { get; init; }

    public bool IsRefused => Refusal is not null;

    /// <summary>The targets a full send must pin as their own picture (the held ones: un-armed, locked, a repeater); round 81: a scoped send pins nothing, it lands on its targets alone.</summary>
    public IReadOnlyList<string> Kept => Held.Select(h => h.Id).Concat(Outside).ToList();

    /// <summary>Round 81: the take lands on its targets alone — every scope but the whole rig (and FOCUSED on the PGM tile, which is the rig).</summary>
    public bool IsScoped { get; init; }

    /// <summary>"on every armed screen", "on 2 · Comfort alone", "on the ticked tiles: 1 · Main, A · Wall".</summary>
    public string Where { get; init; } = "";

    /// <summary>
    /// The plan in one line for a key's face or a status line: "→ 1 · Main, A · Wall · held: 2 · Comfort (locked)"; round 80:
    /// "→ 1 · Main · 2 · Lobby keeps its own picture (OWN)" for an armed target whose own picture the take leaves as it is.
    /// </summary>
    public string Words
    {
        get
        {
            if (Refusal is not null) return Refusal;
            var keeping = new HashSet<string>(KeepsOwnLabels, StringComparer.Ordinal);
            var taken = string.Join(", ", TakenLabels.Where(l => !keeping.Contains(l)));
            var head = taken.Length > 0 ? $"→ {taken}" : "→ no screen changes its picture";
            var own = KeepsOwnLabels.Count == 0 ? "" : $" · {string.Join(", ", KeepsOwnLabels)} keep{(KeepsOwnLabels.Count == 1 ? "s its" : " their")} own picture (OWN)";
            var held = Held.Count == 0 ? "" : " · held: " + string.Join(", ", Held.Select(h => $"{h.Label} ({h.Reason})"));
            var outside = Outside.Count == 0 ? "" : $" · the programme and {Outside.Count} other{(Outside.Count == 1 ? "" : "s")} untouched";
            return $"{head}{own}{held}{outside}";
        }
    }

    /// <summary>Round 80: the wall's names for the taken targets that keep their own picture (<see cref="TakeTarget.KeepsOwn"/>) — still taken (the send carries them), said apart.</summary>
    public IReadOnlyList<string> KeepsOwnLabels { get; init; } = Array.Empty<string>();

    /// <summary>The wall's names for <see cref="Taken"/>, in the same order — a ticket keeps them, so its words stay the press's.</summary>
    public IReadOnlyList<string> TakenLabels { get; init; } = Array.Empty<string>();

    /// <summary>Round 75: each taken target's <see cref="TakeTarget.Shape"/> at the press, in the same order — a ticket keeps them and its landing says them.</summary>
    public IReadOnlyList<string> TakenShapes { get; init; } = Array.Empty<string>();

    /// <summary>Round 76: each taken target's <see cref="TakeTarget.ShapeKey"/> at the press, in the same order — a ticket keeps them and its landing compares.</summary>
    public IReadOnlyList<string> TakenKeys { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Resolves the scope against the rig. <paramref name="focused"/> is the tile the desk has focused
    /// (null = the PGM tile, the programme); <paramref name="named"/> is what the caller resolved for
    /// SCREEN n, CANVAS A or an id — the rig's geometry is the caller's, never this class's. GROUPS and
    /// GROUP &lt;kind&gt; resolve here, over each target's <see cref="TakeTarget.Kind"/>.
    /// </summary>
    public static TakePlan Resolve(IReadOnlyList<TakeTarget> rig, FadeScope scope, string? focused, IReadOnlyList<string>? named = null)
    {
        var byId = rig.ToDictionary(t => t.Id, StringComparer.Ordinal);
        IReadOnlyList<TakeTarget> candidates;
        string where;
        var everything = scope.IsEverything || (scope.Kind == FadeScopeKind.Focused && focused is null);
        if (everything)
        {
            candidates = rig;
            where = "on every armed screen";
        }
        else
        {
            switch (scope.Kind)
            {
                case FadeScopeKind.Focused:
                    if (focused is null || !byId.TryGetValue(focused, out var f))
                        return Refused(scope, "No wall tile is focused — click a tile, or choose ALL ARMED.");
                    candidates = new[] { f };
                    where = $"on {f.Label} alone";
                    break;
                case FadeScopeKind.Ticked:
                    candidates = rig.Where(t => t.Ticked).ToList();
                    if (candidates.Count == 0) return Refused(scope, "Tick the wall tiles first.");
                    where = $"on the ticked tiles: {string.Join(", ", candidates.Select(t => t.Label))}";
                    break;
                case FadeScopeKind.Groups:
                {
                    // Round 81: the groups are the kinds of the ticked tiles — tick one confidence monitor and every
                    // confidence screen takes. A ticked repeater names no group (it draws its source), nor a MIXED canvas.
                    var kinds = rig.Where(t => t.Ticked && ScreenRoles.IsTakeKind(t.Kind)).Select(t => t.Kind).Distinct(StringComparer.Ordinal).ToList();
                    if (kinds.Count == 0) return Refused(scope, "Tick a tile in a group first — GROUPS takes to every screen of the ticked tiles' groups (Main, Confidence, Info).");
                    candidates = rig.Where(t => kinds.Contains(t.Kind)).ToList();
                    where = $"on the {KindWords(kinds)} screens: {string.Join(", ", candidates.Select(t => t.Label))}";
                    break;
                }
                case FadeScopeKind.Group:
                {
                    // Round 81: a group by kind — GROUP MAIN / CONFIDENCE / INFO — every target of that kind.
                    candidates = rig.Where(t => t.Kind == scope.Arg).ToList();
                    if (candidates.Count == 0) return Refused(scope, $"No {scope.Arg} screen on the wall.");
                    where = $"on the {scope.Arg} screens: {string.Join(", ", candidates.Select(t => t.Label))}";
                    break;
                }
                case FadeScopeKind.Canvases:
                    candidates = rig.Where(t => t.Ticked && t.IsCanvas).ToList();
                    if (candidates.Count == 0) return Refused(scope, "Tick a joined canvas on the wall first.");
                    where = $"on the ticked canvases: {string.Join(", ", candidates.Select(t => t.Label))}";
                    break;
                default:
                    if (named is null || named.Count == 0) return Refused(scope, $"No {scope.Label} on the wall.");
                    candidates = named.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                    if (candidates.Count == 0) return Refused(scope, $"'{scope.Label}' is not a screen in the rig.");
                    where = $"on {string.Join(", ", candidates.Select(t => t.Label))}{(candidates.Count == 1 ? " alone" : "")}";
                    break;
            }
        }

        var inside = new HashSet<string>(candidates.Select(t => t.Id), StringComparer.Ordinal);
        var taken = new List<string>();
        var takenLabels = new List<string>();
        var takenShapes = new List<string>();
        var takenKeys = new List<string>();
        var keepsOwn = new List<string>();
        var held = new List<TakeHeld>();
        foreach (var t in rig)
        {
            if (!inside.Contains(t.Id)) continue;
            if (t.Locked) held.Add(new TakeHeld(t.Id, t.Label, TakeHeld.LockedReason));
            else if (t.IsMirror) held.Add(new TakeHeld(t.Id, t.Label, TakeHeld.RepeaterReason));
            else if (!t.Armed) held.Add(new TakeHeld(t.Id, t.Label, TakeHeld.NotArmedReason));
            else
            {
                taken.Add(t.Id);
                takenLabels.Add(t.Label);
                takenShapes.Add(t.Shape);
                takenKeys.Add(t.ShapeKey);
                if (t.KeepsOwn) keepsOwn.Add(t.Label);      // round 80: carried by the send, its picture left as it is — said before the press
            }
        }
        var outside = rig.Where(t => !inside.Contains(t.Id)).Select(t => t.Id).ToList();

        string? refusal = null;
        if (taken.Count == 0)
        {
            if (rig.Count == 0) refusal = "No screens in the rig.";
            else if (held.Count == 1) refusal = OneHeld(held[0]);
            else if (everything)
            {
                var locked = held.Count(h => h.Reason == TakeHeld.LockedReason);
                var unarmed = held.Count(h => h.Reason == TakeHeld.NotArmedReason);
                refusal = locked > 0 && unarmed == 0
                    ? "Every screen is locked — unlock one (LOCK on its tile) to take to it."
                    : $"Nothing is armed to take to — every screen is held ({Summary(held)}). ARM the screens to change, or ARM ALL.";
            }
            else refusal = $"Nothing to take to: {string.Join(", ", held.Select(h => $"{h.Label} ({h.Reason})"))}.";
        }

        return new TakePlan
        {
            Scope = scope,
            Taken = taken,
            TakenLabels = takenLabels,
            TakenShapes = takenShapes,
            TakenKeys = takenKeys,
            KeepsOwnLabels = keepsOwn,
            Held = held,
            Outside = outside,
            Where = where,
            Refusal = refusal,
            IsScoped = !everything,
        };
    }

    /// <summary>"main", "main and confidence", "main, confidence and info".</summary>
    private static string KindWords(IReadOnlyList<string> kinds)
        => kinds.Count == 1 ? kinds[0] : string.Join(", ", kinds.Take(kinds.Count - 1)) + " and " + kinds[^1];

    private static TakePlan Refused(FadeScope scope, string why) => new() { Scope = scope, Refusal = why };

    private static string OneHeld(TakeHeld h) => h.Reason switch
    {
        TakeHeld.LockedReason => $"{h.Label} is locked — it keeps its picture. Unlock it (LOCK on its tile) to take to it.",
        TakeHeld.RepeaterReason => $"{h.Label} is a repeater — it draws its source's picture; take to its source.",
        _ => $"{h.Label} is not armed — ARM it, or use the tile's own TAKE.",
    };

    private static string Summary(IReadOnlyList<TakeHeld> held)
    {
        var parts = new List<string>();
        var locked = held.Count(h => h.Reason == TakeHeld.LockedReason);
        var unarmed = held.Count(h => h.Reason == TakeHeld.NotArmedReason);
        var mirrors = held.Count(h => h.Reason == TakeHeld.RepeaterReason);
        if (locked > 0) parts.Add($"{locked} locked");
        if (unarmed > 0) parts.Add($"{unarmed} not armed");
        if (mirrors > 0) parts.Add($"{mirrors} repeater{(mirrors == 1 ? "" : "s")}");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Round 72: a press is a transaction. A TAKE under a video sting lands when the clip ends, and what lands is
/// what the press promised — the targets its plan named then — never more: an ARM, a tick, a focus or a screen
/// that arrived during the clip is the next press's, not this one's. Less only by what the rig took away since:
/// a screen LOCKED after the press keeps its picture (LOCKED means locked, whenever the lock came), and a screen
/// gone from the rig cannot be sent to. The words the press answered stay facts, and STATE and the Eye show the
/// ticket while it waits.
/// </summary>
public sealed record TakeTicket
{
    /// <summary>The scope words the press ran with — "" every armed screen, "TICKED", "ID a", "TILE a" — for STATE, the Eye and the desk's hooks.</summary>
    public required string Scope { get; init; }

    /// <summary>What the press promised, in wall order.</summary>
    public required IReadOnlyList<string> Taken { get; init; }

    /// <summary>The wall's names for <see cref="Taken"/> at the press, in the same order.</summary>
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();

    /// <summary>Round 75: what each of <see cref="Taken"/> was at the press (<see cref="TakeTarget.Shape"/>), in the same order — the words a landing says for a target that is something else now.</summary>
    public IReadOnlyList<string> Shapes { get; init; } = Array.Empty<string>();

    /// <summary>Round 76: what each of <see cref="Taken"/> was at the press, structurally (<see cref="TakeTarget.ShapeKey"/>), in the same order — what the landing compares; the words decide only when a side has no key.</summary>
    public IReadOnlyList<string> Keys { get; init; } = Array.Empty<string>();

    /// <summary>What the press said: "on every armed screen", "on 2 · Right alone".</summary>
    public required string Where { get; init; }

    /// <summary>A tile's own TAKE: the preview lands on the one target as its own picture.</summary>
    public bool Tile { get; init; }

    /// <summary>The sting that covers the take, by name.</summary>
    public string Cover { get; init; } = "";

    public DateTime PressedUtc { get; init; }

    /// <summary>"→ 1 · Left, 2 · Right when 'Whoosh' ends".</summary>
    public string Words => $"→ {string.Join(", ", Labels.Count == Taken.Count ? Labels : Taken)}{(Cover.Length > 0 ? $" when '{Cover}' ends" : "")}";

    /// <summary>The ticket a plan makes at the press.</summary>
    public static TakeTicket From(TakePlan plan, string scope, string cover, DateTime pressedUtc) => new()
    {
        Scope = scope,
        Taken = plan.Taken,
        Labels = plan.TakenLabels,
        Shapes = plan.TakenShapes,
        Keys = plan.TakenKeys,
        Where = plan.Where,
        Cover = cover,
        PressedUtc = pressedUtc,
    };

    /// <summary>
    /// The landing against the rig as it is now: the promised targets that are still in the rig, still the shape
    /// the press saw and not locked since land; every other target in the rig keeps its picture, the promised ones
    /// with the reason — gone from the rig; changed since the press (round 75: a screen made a repeater, a canvas
    /// whose members moved, a screen that joined a canvas — <paramref name="whereNow"/> says where a promised id
    /// went when it is no longer a target of its own, "in Canvas A"); or locked since. A transaction never gains a
    /// destination, and never lands on one that is not what it promised. A landing with nothing left to land is
    /// a refusal that names why — the sting then puts the show back and says so — never a silent success.
    /// </summary>
    public TakeLanding Land(IReadOnlyList<TakeTarget> rigNow, Func<string, string?>? whereNow = null)
    {
        var byId = new Dictionary<string, TakeTarget>(StringComparer.Ordinal);
        foreach (var t in rigNow) byId[t.Id] = t;
        var landed = new List<string>();
        var held = new List<TakeHeld>();
        for (var i = 0; i < Taken.Count; i++)
        {
            var id = Taken[i];
            var label = i < Labels.Count ? Labels[i] : id;
            var promised = i < Shapes.Count ? Shapes[i] : "";
            var promisedKey = i < Keys.Count ? Keys[i] : "";
            if (!byId.TryGetValue(id, out var now))
            {
                var elsewhere = whereNow?.Invoke(id) ?? "";
                held.Add(new TakeHeld(id, label, elsewhere.Length > 0 ? TakeHeld.ChangedSince(elsewhere) : TakeHeld.GoneReason));
            }
            else if (ShapeChanged(promised, promisedKey, now))
            {
                held.Add(new TakeHeld(id, label, TakeHeld.ChangedSince(now.Shape.Length > 0 ? now.Shape : now.ShapeKey)));
            }
            else if (now.Locked) held.Add(new TakeHeld(id, label, TakeHeld.LockedSinceReason));
            else landed.Add(id);
        }
        var landedSet = new HashSet<string>(landed, StringComparer.Ordinal);
        var kept = rigNow.Where(t => !landedSet.Contains(t.Id)).Select(t => t.Id).ToList();
        string? refusal = landed.Count > 0 ? null
            : Taken.Count == 0 ? "The press promised nothing."
            : $"Nothing lands — {string.Join(", ", held.Select(h => $"{h.Label} ({h.Reason})"))}.";
        return new TakeLanding(landed, held, kept, refusal);
    }

    /// <summary>
    /// Round 76: whether a target is something else than the press promised. The keys decide when both
    /// the press and the rig carry one (a label that changed is no change; a member that joined or left,
    /// a screen made a repeater, a repeater made its own is); the words decide as in round 75 when a side
    /// has none. A press or a rig that keeps neither compares nothing.
    /// </summary>
    public static bool ShapeChanged(string promisedShape, string promisedKey, TakeTarget now)
    {
        if (promisedKey.Length > 0 && now.ShapeKey.Length > 0) return !string.Equals(promisedKey, now.ShapeKey, StringComparison.Ordinal);
        return promisedShape.Length > 0 && now.Shape.Length > 0 && !string.Equals(promisedShape, now.Shape, StringComparison.Ordinal);
    }
}

/// <summary>What a ticket lands now: the targets, the promised ones held since with the reason, every rig target that keeps its picture, or the refusal.</summary>
public sealed record TakeLanding(IReadOnlyList<string> Landed, IReadOnlyList<TakeHeld> HeldSince, IReadOnlyList<string> Kept, string? Refusal)
{
    public bool IsRefused => Refusal is not null;

    /// <summary>" · held since the press: 2 · Right (locked since the press)" or "".</summary>
    public string HeldWords => HeldSince.Count == 0 ? "" : " · held since the press: " + string.Join(", ", HeldSince.Select(h => $"{h.Label} ({h.Reason})"));
}
