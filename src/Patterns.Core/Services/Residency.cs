using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>Why a thing in memory is still there (round 69): every held thing has one of these, or an idle clock.</summary>
public enum HoldReason
{
    /// <summary>A picture the room sees draws from it now.</summary>
    OnAir,
    /// <summary>The show names it — a pattern's picture, a layer's, the logo, the standby cue's look — so it is kept decoded whether or not a frame drew it this second.</summary>
    Named,
    /// <summary>The preview alone shows it.</summary>
    Preview,
    /// <summary>A web page whose video the operator armed at a mark: it stays, set up, until it is disarmed or plays — the one thing kept past its want.</summary>
    Armed,
    /// <summary>Opened ahead for the standby cue and held on its first frame, so GO lands on a picture.</summary>
    PreRolled,
    /// <summary>Let go — fading out, or waiting on the render fence for the sinks that drew it — on its way out.</summary>
    Retiring,
    /// <summary>Nothing draws it and nothing names it: it stays until its grace is up, then goes.</summary>
    Idle,
}

/// <summary>One held thing: what it is, why it stays, what it costs, how long since anything drew it.</summary>
public sealed record Hold(string Key, string Kind, string Label, HoldReason Reason, long Bytes, double IdleSeconds = 0, string Status = "")
{
    private const double MB = 1024.0 * 1024.0;

    /// <summary>"Sponsor.mp4 — on air · 64 MB" / "logo.png — idle 42 s, lets go in 18 s · 33 MB" / "Walk-in.png — named by the show · 8 MB".</summary>
    public string Words(TimeSpan grace)
    {
        var why = Reason == HoldReason.Idle
            ? $"idle {IdleSeconds:0} s, {(grace.TotalSeconds > IdleSeconds ? $"lets go in {grace.TotalSeconds - IdleSeconds:0} s" : "letting go")}"
            : Residency.Word(Reason);
        var cost = Bytes > 0 ? $" · {MemoryBudget.Mb(Bytes / MB)}" : "";
        var status = Status.Length > 0 && Reason != HoldReason.Idle ? $" ({Status})" : "";
        return $"{Label} — {why}{status}{cost}";
    }
}

/// <summary>
/// The residency policy (round 69): what may stay in memory and for how long. A picture, a decoder,
/// a browser page or a deck's pages stay while something has a reason for them — a frame draws from
/// them, the show names them, the preview shows them, an armed video waits at its mark, the standby
/// cue pre-rolled them — and an idle thing gets a grace by the machine's class, shortened as the
/// memory pressure ladder climbs, then goes on its own clock rather than only when the budget is
/// passed. Pure: the App's residency service gathers the holds and applies the grace.
/// </summary>
public static class Residency
{
    /// <summary>The grace an idle picture gets, by class: a small machine lets go soon, a big one keeps a picture it may draw again.</summary>
    public static TimeSpan BaseGrace(MachineClass cls) => cls switch
    {
        MachineClass.Small => TimeSpan.FromSeconds(20),
        MachineClass.Big => TimeSpan.FromSeconds(180),
        _ => TimeSpan.FromSeconds(60),
    };

    /// <summary>The grace under pressure: whole with none, half at elevated, a quarter at high, none at critical — an idle thing goes at once.</summary>
    public static TimeSpan Grace(MachineClass cls, MemoryPressure pressure)
    {
        var whole = BaseGrace(cls);
        return pressure switch
        {
            MemoryPressure.None => whole,
            MemoryPressure.Elevated => whole / 2,
            MemoryPressure.High => whole / 4,
            _ => TimeSpan.Zero,
        };
    }

    /// <summary>A picture drawn within this long is "on air" (something is drawing it), not idle.</summary>
    public static readonly TimeSpan DrawnWithin = TimeSpan.FromSeconds(1.5);

    /// <summary>The reason as the desk says it.</summary>
    public static string Word(HoldReason reason) => reason switch
    {
        HoldReason.OnAir => "on air",
        HoldReason.Named => "named by the show",
        HoldReason.Preview => "in the preview",
        HoldReason.Armed => "armed at its mark",
        HoldReason.PreRolled => "pre-rolled for the standby cue",
        HoldReason.Retiring => "retiring",
        _ => "idle",
    };

    /// <summary>The reason a mounted source has from the pictures it plays on: pre-rolled ahead, on air when any bus is not the preview, else the preview's alone.</summary>
    public static HoldReason ForBuses(IReadOnlyList<MediaBus>? buses, bool preRoll)
    {
        if (preRoll) return HoldReason.PreRolled;
        if (buses is null || buses.Count == 0) return HoldReason.OnAir;   // a source with no bus list is a source the engine holds for the air
        foreach (var bus in buses)
        {
            if (!bus.Preview) return HoldReason.OnAir;
        }
        return HoldReason.Preview;
    }

    /// <summary>The reason a decoded picture has: drawn within the window is on air, named by the show is kept, else idle.</summary>
    public static HoldReason ForPicture(double idleSeconds, bool named)
        => idleSeconds <= DrawnWithin.TotalSeconds ? HoldReason.OnAir : named ? HoldReason.Named : HoldReason.Idle;

    /// <summary>The order the rows are shown in: what the room sees first, what is leaving last.</summary>
    public static int Rank(HoldReason reason) => reason switch
    {
        HoldReason.OnAir => 0,
        HoldReason.PreRolled => 1,
        HoldReason.Armed => 2,
        HoldReason.Named => 3,
        HoldReason.Preview => 4,
        HoldReason.Idle => 5,
        _ => 6,
    };

    /// <summary>"7 held (412 MB): 3 on air, 1 pre-rolled, 1 named by the show, 2 idle — the first lets go in 12 s" / "nothing held".</summary>
    public static string Summary(IReadOnlyList<Hold> holds, TimeSpan grace)
    {
        if (holds.Count == 0) return "nothing held";
        var parts = new List<string>();
        foreach (var group in holds.GroupBy(h => h.Reason).OrderBy(g => Rank(g.Key)))
        {
            parts.Add($"{group.Count()} {Word(group.Key)}");
        }
        var bytes = holds.Sum(h => h.Bytes);
        var idle = holds.Where(h => h.Reason == HoldReason.Idle).ToList();
        var soonest = idle.Count == 0 ? "" : grace <= TimeSpan.Zero ? " — idle things go at once under this pressure"
            : $" — the first lets go in {Math.Max(0, grace.TotalSeconds - idle.Max(h => h.IdleSeconds)):0} s";
        return $"{holds.Count} held{(bytes > 0 ? $" ({MemoryBudget.Mb(bytes / (1024.0 * 1024.0))})" : "")}: {string.Join(", ", parts)}{soonest}";
    }

    /// <summary>
    /// Every picture path the show names right now — the programme's pattern and layers, each screen's own
    /// pattern, the logo, and the standby cue's look — the pictures kept decoded whether or not a frame drew them.
    /// </summary>
    public static HashSet<string> NamedPictures(ShowState state, ShowState? sandbox = null, LookConfig? standby = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddPattern(PatternConfig? p)
        {
            if (p is null) return;
            Add(set, p.Media.ImagePath);
            Add(set, p.Layer1.ImagePath);
            Add(set, p.Layer2.ImagePath);
        }
        AddPattern(state.Pattern);
        foreach (var a in state.Independent) AddPattern(a.Pattern);
        Add(set, state.Brand.LogoPath);
        if (sandbox is not null)
        {
            AddPattern(sandbox.Pattern);
            foreach (var a in sandbox.Independent) AddPattern(a.Pattern);
        }
        if (standby is { Json.Length: > 0 })
        {
            try
            {
                var data = JsonUtil.Deserialize<LookData>(standby.Json);
                if (data is not null)
                {
                    AddPattern(data.Pattern);
                    foreach (var a in data.Independent) AddPattern(a.Pattern);
                }
            }
            catch
            {
                // a look that will not parse names nothing
            }
        }
        return set;
    }

    private static void Add(HashSet<string> set, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        set.Add(path);
        var resolved = ShowFiles.Resolve(path);
        if (!string.IsNullOrWhiteSpace(resolved)) set.Add(resolved);
    }
}
