namespace Patterns.Core.Services;

/// <summary>One content target — a screen or a joined canvas — as the next CUT / TAKE sees it.</summary>
/// <param name="Id">The screen id or the canvas key.</param>
/// <param name="Label">The wall's name for it: "2 · Comfort", "A · Main wall".</param>
/// <param name="IsCanvas">A joined canvas (a group on the wall).</param>
/// <param name="IsMirror">A repeater: it draws its source's picture and has none of its own.</param>
/// <param name="Locked">LOCK on the tile: it keeps its picture through looks, cues, TAKE ALL and stingers.</param>
/// <param name="Armed">ARM on the tile: the next CUT / TAKE may change it.</param>
/// <param name="Ticked">The tick at the top of the tile.</param>
public sealed record TakeTarget(string Id, string Label, bool IsCanvas = false, bool IsMirror = false, bool Locked = false, bool Armed = true, bool Ticked = false);

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
}

/// <summary>
/// What the next CUT / TAKE changes and what it leaves alone — one rule for the wall's keys, the
/// PGM tile's menu, the multiview's NEXT TAKE line, the assistant and the wire (round 67).
///
/// The rules, in the order they bind: LOCKED means locked — a locked target is never taken, whatever
/// the scope says. ALL ARMED takes every armed target and no other. FOCUSED takes the tile being
/// edited alone (the PGM tile focused is the programme: every armed screen). TICKED takes the ticked
/// tiles alone; TICKED GROUPS the ticked joined canvases alone. SCREEN n, GROUP A and an id name their
/// target. Inside any scope ARM still counts (an un-armed tile keeps the picture the audience sees) and a
/// repeater is never taken (it draws its source). Everything outside the scope keeps its picture as an
/// un-armed tile does, and the next full send lifts that.
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

    /// <summary>Targets outside the scope: they keep their picture, and nothing more is said about them.</summary>
    public IReadOnlyList<string> Outside { get; init; } = Array.Empty<string>();

    /// <summary>Why nothing would change, or null when something would.</summary>
    public string? Refusal { get; init; }

    public bool IsRefused => Refusal is not null;

    /// <summary>Every target the send must pin as its own picture: the held and the outside together.</summary>
    public IReadOnlyList<string> Kept => Held.Select(h => h.Id).Concat(Outside).ToList();

    /// <summary>"on every armed screen", "on 2 · Comfort alone", "on the ticked tiles: 1 · Main, A · Wall".</summary>
    public string Where { get; init; } = "";

    /// <summary>The plan in one line for a key's face or a status line: "→ 1 · Main, A · Wall · held: 2 · Comfort (locked)".</summary>
    public string Words
    {
        get
        {
            if (Refusal is not null) return Refusal;
            var taken = string.Join(", ", TakenLabels);
            var held = Held.Count == 0 ? "" : " · held: " + string.Join(", ", Held.Select(h => $"{h.Label} ({h.Reason})"));
            var outside = Outside.Count == 0 ? "" : $" · {Outside.Count} outside the scope keep{(Outside.Count == 1 ? "s" : "")} {(Outside.Count == 1 ? "its" : "their")} picture";
            return $"→ {taken}{held}{outside}";
        }
    }

    /// <summary>The wall's names for <see cref="Taken"/>, in the same order — a ticket keeps them, so its words stay the press's.</summary>
    public IReadOnlyList<string> TakenLabels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Resolves the scope against the rig. <paramref name="focused"/> is the tile the desk has focused
    /// (null = the PGM tile, the programme); <paramref name="named"/> is what the caller resolved for
    /// SCREEN n, GROUP A or an id — the rig's geometry is the caller's, never this class's.
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
                    candidates = rig.Where(t => t.Ticked && t.IsCanvas).ToList();
                    if (candidates.Count == 0) return Refused(scope, "Tick a group (a joined canvas) on the wall first.");
                    where = $"on the ticked groups: {string.Join(", ", candidates.Select(t => t.Label))}";
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
            Held = held,
            Outside = outside,
            Where = where,
            Refusal = refusal,
        };
    }

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
        Where = plan.Where,
        Cover = cover,
        PressedUtc = pressedUtc,
    };

    /// <summary>
    /// The landing against the rig as it is now: the promised targets that are still in the rig and not locked
    /// since land; every other target in the rig keeps its picture, the ones the press promised with the reason.
    /// A landing with nothing left to land is a refusal that names why — the sting then puts the show back and
    /// says so — never a silent success.
    /// </summary>
    public TakeLanding Land(IReadOnlyList<string> rig, Func<string, bool> lockedNow)
    {
        var inRig = new HashSet<string>(rig, StringComparer.Ordinal);
        var landed = new List<string>();
        var held = new List<TakeHeld>();
        for (var i = 0; i < Taken.Count; i++)
        {
            var id = Taken[i];
            var label = i < Labels.Count ? Labels[i] : id;
            if (!inRig.Contains(id)) held.Add(new TakeHeld(id, label, TakeHeld.GoneReason));
            else if (lockedNow(id)) held.Add(new TakeHeld(id, label, TakeHeld.LockedSinceReason));
            else landed.Add(id);
        }
        var landedSet = new HashSet<string>(landed, StringComparer.Ordinal);
        var kept = rig.Where(t => !landedSet.Contains(t)).ToList();
        string? refusal = landed.Count > 0 ? null
            : Taken.Count == 0 ? "The press promised nothing."
            : $"Nothing lands — {string.Join(", ", held.Select(h => $"{h.Label} ({h.Reason})"))}.";
        return new TakeLanding(landed, held, kept, refusal);
    }
}

/// <summary>What a ticket lands now: the targets, the promised ones held since with the reason, every rig target that keeps its picture, or the refusal.</summary>
public sealed record TakeLanding(IReadOnlyList<string> Landed, IReadOnlyList<TakeHeld> HeldSince, IReadOnlyList<string> Kept, string? Refusal)
{
    public bool IsRefused => Refusal is not null;

    /// <summary>" · held since the press: 2 · Right (locked since the press)" or "".</summary>
    public string HeldWords => HeldSince.Count == 0 ? "" : " · held since the press: " + string.Join(", ", HeldSince.Select(h => $"{h.Label} ({h.Reason})"));
}
