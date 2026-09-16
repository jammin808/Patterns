
namespace Patterns.Core.Services;

/// <summary>How hard the media memory presses on its budget: the ladder's rungs.</summary>
public enum MemoryPressure
{
    None,
    Elevated,
    High,
    Critical,
}

/// <summary>
/// The media memory the app owns, in one view against one budget: the pictures resident and
/// retiring, the frame pools live and retiring, the frames retiring behind the fence, and what
/// the app registers besides (the decks' pages). A per-source budget alone could not say what
/// opening another source costs, or when the whole is near the ceiling; this can, before the
/// process gets there. The budget is a share of the app's ceiling for the machine's class, and
/// the pressure is read from the ratio at fixed rungs — deterministic, so a soak and a desk read
/// the same word for the same bytes. The ladder's steps at each rung are the app's to take
/// (<see cref="Steps"/> says which); what must never be touched is the source on air, the
/// program frame, the cue state, the projection geometry and the route and authority truth.
/// </summary>
public static class MediaMemory
{
    private const long MB = 1024L * 1024;

    /// <summary>The media budget as a share of the app's ceiling: a 16 GB machine's 3 GB ceiling gives 1.8 GB for media.</summary>
    public const double ShareOfAppCeiling = 0.6;

    /// <summary>The rungs, as a share of the budget.</summary>
    public const double ElevatedAt = 0.70;
    public const double HighAt = 0.85;
    public const double CriticalAt = 1.0;

    /// <summary>What the app registers besides the pictures, the pools and the frames: the decks' pages, thumbnails — bytes.</summary>
    public static Func<long>? Extra { get; set; }

    /// <summary>
    /// What the render side holds, in bytes — the picture cache, the frame pools and the frames
    /// retiring behind the render fence. The render module registers the reader when it is
    /// built; a process that never renders (a caller, a timer) reads zero, which is the truth.
    /// </summary>
    public static Func<MediaBytes>? Source { get; set; }

    /// <summary>The render side's bytes at one moment.</summary>
    public readonly record struct MediaBytes(long Pictures, long PicturesRetiring, long Pools, long PoolsRetiring, long FramesRetiring);

    /// <summary>The media budget for a machine of this size, in bytes.</summary>
    public static long BudgetBytes(double totalMB) => (long)(MemoryBudget.For(totalMB, MemoryBudget.PictureCapacity, 4).AppCeilingMB * ShareOfAppCeiling * MB);

    /// <summary>The shares a rung is left at, below its entry (round 64): elevated leaves under 65 %, high under 80 %, critical under 92 % — so a reading that sits on a line does not flap the steps on and off.</summary>
    public const double ElevatedLeavesAt = 0.65;
    public const double HighLeavesAt = 0.80;
    public const double CriticalLeavesAt = 0.92;

    /// <summary>How long a reading has to sit below a rung's leaving share before the ladder steps down; stepping up is never delayed.</summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The next rung from the current one and a reading (round 64): up at once when the share
    /// passes a rung's entry; down only once the share has sat below the current rung's leaving
    /// share for <see cref="Dwell"/> — <paramref name="belowSince"/> is when it first did (-1
    /// while it has not), kept by the caller between readings.
    /// </summary>
    public static MemoryPressure Step(MemoryPressure current, long bytes, long budget, double nowSeconds, ref double belowSince)
    {
        var target = LevelOf(bytes, budget);
        if (target > current)
        {
            belowSince = -1;
            return target;
        }
        if (budget <= 0 || current == MemoryPressure.None)
        {
            belowSince = -1;
            return current;
        }
        var share = (double)bytes / budget;
        var leaves = current switch
        {
            MemoryPressure.Critical => CriticalLeavesAt,
            MemoryPressure.High => HighLeavesAt,
            _ => ElevatedLeavesAt,
        };
        if (share >= leaves)
        {
            belowSince = -1;
            return current;
        }
        if (belowSince < 0) belowSince = nowSeconds;
        if (nowSeconds - belowSince < Dwell.TotalSeconds) return current;
        belowSince = -1;
        return current - 1;                                                  // one rung at a time: the next reading judges the next
    }

    /// <summary>The rung for these bytes against this budget: none under 70 %, elevated from there, high from 85 %, critical at the budget and past it.</summary>
    public static MemoryPressure LevelOf(long bytes, long budget)
    {
        if (budget <= 0) return MemoryPressure.None;
        var share = bytes / (double)budget;
        return share >= CriticalAt ? MemoryPressure.Critical
            : share >= HighAt ? MemoryPressure.High
            : share >= ElevatedAt ? MemoryPressure.Elevated
            : MemoryPressure.None;
    }

    /// <summary>One reading of the media memory: every part, the budget, the rung and the words.</summary>
    public sealed record Reading(long Pictures, long PicturesRetiring, long Pools, long PoolsRetiring, long FramesRetiring, long Extra, long Budget)
    {
        public long Total => Pictures + PicturesRetiring + Pools + PoolsRetiring + FramesRetiring + Extra;

        public MemoryPressure Level => LevelOf(Total, Budget);

        /// <summary>The share of the budget in use, 0–1 and past.</summary>
        public double Share => Budget > 0 ? Total / (double)Budget : 0;

        /// <summary>"media 612 MB of 1.8 GB (34 %) — no pressure" / "… (91 %) — HIGH pressure: retired swept, pictures trimmed, pre-roll suppressed, decks narrowed".</summary>
        public string Words => $"media {MemoryBudget.Mb(Total / (double)MB)} of {MemoryBudget.Mb(Budget / (double)MB)} ({Share * 100:0} %) — {LevelWords(Level)}";

        /// <summary>"pictures 84 MB (+12 MB retiring) · frame pools 116 MB (+64 MB retiring) · frames 8 MB retiring · decks 40 MB".</summary>
        public string Parts
        {
            get
            {
                var parts = new List<string>();
                if (Pictures > 0 || PicturesRetiring > 0) parts.Add($"pictures {MemoryBudget.Mb(Pictures / (double)MB)}{(PicturesRetiring > 0 ? $" (+{MemoryBudget.Mb(PicturesRetiring / (double)MB)} retiring)" : "")}");
                if (Pools > 0 || PoolsRetiring > 0) parts.Add($"frame pools {MemoryBudget.Mb(Pools / (double)MB)}{(PoolsRetiring > 0 ? $" (+{MemoryBudget.Mb(PoolsRetiring / (double)MB)} retiring)" : "")}");
                if (FramesRetiring > 0) parts.Add($"frames {MemoryBudget.Mb(FramesRetiring / (double)MB)} retiring");
                if (Extra > 0) parts.Add($"decks and the rest {MemoryBudget.Mb(Extra / (double)MB)}");
                return parts.Count == 0 ? "nothing held" : string.Join(" · ", parts);
            }
        }
    }

    /// <summary>What the media holds now against the budget for a machine of this size.</summary>
    public static Reading Read(double totalMB)
    {
        long extra = 0;
        try
        {
            extra = Math.Max(0, Extra?.Invoke() ?? 0);
        }
        catch
        {
            extra = 0;
        }
        var held = Source?.Invoke() ?? default;
        return new Reading(held.Pictures, held.PicturesRetiring, held.Pools, held.PoolsRetiring, held.FramesRetiring, extra, BudgetBytes(totalMB));
    }

    /// <summary>"no pressure" / "elevated pressure: …" — the rung and the steps it takes.</summary>
    public static string LevelWords(MemoryPressure level) => level switch
    {
        MemoryPressure.None => "no pressure",
        MemoryPressure.Elevated => "elevated pressure: " + Steps(level),
        MemoryPressure.High => "HIGH pressure: " + Steps(level),
        _ => "CRITICAL pressure: " + Steps(level),
    };

    /// <summary>
    /// The ladder's steps at a rung, cumulative: elevated sweeps what is retiring and trims the
    /// pictures to half their budget; high also holds back the standby cue's pre-roll and narrows
    /// the decks' page window; critical also refuses to open a source the preview alone wants.
    /// The source on air, the program frame, the cue state and the geometry are never touched.
    /// </summary>
    public static string Steps(MemoryPressure level) => level switch
    {
        MemoryPressure.None => "",
        MemoryPressure.Elevated => "retired swept, pictures trimmed",
        MemoryPressure.High => "retired swept, pictures trimmed, pre-roll held back, decks narrowed",
        _ => "retired swept, pictures trimmed, pre-roll held back, decks narrowed, no new preview-only source opened, armed pages let go",
    };

    /// <summary>The rung as one lower-case word for the wire and Companion.</summary>
    public static string Word(MemoryPressure level) => level.ToString().ToLowerInvariant();

    /// <summary>The check's light for a rung: green with none, amber elevated and high, red critical.</summary>
    public static CheckLight Light(MemoryPressure level) => level switch
    {
        MemoryPressure.None => CheckLight.Green,
        MemoryPressure.Critical => CheckLight.Red,
        _ => CheckLight.Amber,
    };
}
