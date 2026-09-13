namespace Patterns.Core.RigDay;

/// <summary>What just went right on rig day.</summary>
public enum CelebrationKind
{
    /// <summary>A node of the alignment game walked onto its target.</summary>
    NodeLocked,
    /// <summary>Every node of a projector's lattice within a pixel: the game done.</summary>
    ProjectorAligned,
    /// <summary>A join's audit went green: a level of Blend Quest cleared.</summary>
    JoinCleared,
    /// <summary>The 2x2's middle clear: the boss.</summary>
    BossCleared,
    /// <summary>The show-ready bar filled: every step clear.</summary>
    ShowReady,
}

/// <summary>
/// A moment worth marking, and for how long: a sweep on the projector's lattice while it is
/// up (the room is not watching a lattice), a word on the desk, a chip in the status. Pure —
/// the clock comes in, so the sweep's phase and its end are unit tested.
/// </summary>
public sealed record Celebration(CelebrationKind Kind, string Words, DateTime AtUtc, TimeSpan Length)
{
    /// <summary>A node locked: brief, it happens twenty-five times a lattice.</summary>
    public static readonly TimeSpan Brief = TimeSpan.FromSeconds(1.2);

    /// <summary>A game done, a level cleared, the bar full: long enough to be seen from the back of the room.</summary>
    public static readonly TimeSpan Full = TimeSpan.FromSeconds(4);

    public static Celebration For(CelebrationKind kind, string words, DateTime utcNow)
        => new(kind, words, utcNow, kind == CelebrationKind.NodeLocked ? Brief : Full);

    public DateTime UntilUtc => AtUtc + Length;

    public bool IsOver(DateTime utcNow) => utcNow >= UntilUtc;

    /// <summary>0 as it starts, 1 as it ends.</summary>
    public double Phase(DateTime utcNow)
    {
        var t = (utcNow - AtUtc).TotalSeconds / Length.TotalSeconds;
        return t < 0 ? 0 : t > 1 ? 1 : t;
    }

    /// <summary>The chip's words: short, in capitals, what the wall says over the lattice.</summary>
    public string Chip => Kind switch
    {
        CelebrationKind.NodeLocked => "LOCKED",
        CelebrationKind.ProjectorAligned => "ALIGNED",
        CelebrationKind.JoinCleared => "JOIN CLEAR",
        CelebrationKind.BossCleared => "BOSS CLEARED",
        _ => "SHOW READY",
    };

    /// <summary>
    /// The sweep at a phase: a ring's radius as a fraction of the picture's half-diagonal (out
    /// fast, then easing) and its alpha (full at the start, gone at the end) — the maths the
    /// pipeline draws by, kept here so a test reads it without a canvas.
    /// </summary>
    public static (double Radius, byte Alpha) Ring(double phase)
    {
        var p = phase < 0 ? 0 : phase > 1 ? 1 : phase;
        var eased = 1 - (1 - p) * (1 - p) * (1 - p);
        var alpha = (byte)Math.Round(255 * (1 - p) * (1 - p));
        return (eased, alpha);
    }
}

/// <summary>
/// Reads the transitions rig day's facts go through and names each one once: the bar filling
/// (and again after it emptied), a join clearing, the boss cleared. The alignment game's own
/// moments come from its keys, not from here. Pure: what was seen last is the only state.
/// </summary>
public sealed class CelebrationTrack
{
    private bool _wasFull;
    private readonly HashSet<string> _clearedLevels = new(StringComparer.Ordinal);
    private bool _bossCleared;
    private bool _seenOnce;

    /// <summary>The one thing to celebrate in this reading, or null. The first reading sets the baseline and celebrates nothing — a rig that is ready when the games come on has already been celebrated by whoever built it.</summary>
    public Celebration? Observe(ShowReadyScore? ready, IReadOnlyList<QuestLevel> quest, DateTime utcNow)
    {
        var full = ready is { IsFull: true };
        var bossNow = quest.Any(l => l.Boss && l.Cleared);
        var clearedNow = quest.Where(l => !l.Boss && l.Cleared).Select(l => l.Name).ToList();
        Celebration? found = null;
        if (_seenOnce)
        {
            if (full && !_wasFull) found = Celebration.For(CelebrationKind.ShowReady, ready!.Words + " — every step clear. The rig is ready.", utcNow);
            else if (bossNow && !_bossCleared) found = Celebration.For(CelebrationKind.BossCleared, "Blend Quest: the boss is cleared — the 2x2's middle blends clean.", utcNow);
            else
            {
                var fresh = clearedNow.FirstOrDefault(n => !_clearedLevels.Contains(n));
                if (fresh is not null) found = Celebration.For(CelebrationKind.JoinCleared, $"Blend Quest: {fresh} cleared.", utcNow);
            }
        }
        _seenOnce = true;
        _wasFull = full;
        _bossCleared = bossNow;
        _clearedLevels.Clear();
        foreach (var n in clearedNow) _clearedLevels.Add(n);
        return found;
    }
}
