namespace Patterns.Core.RigDay;

/// <summary>The caller's on-time streak: GOs within the plan's drift, per session — for callers who want it, and nowhere near the stack for those who do not.</summary>
public sealed class OnTimeStreak
{
    public int Streak { get; private set; }
    public int Best { get; private set; }
    public int Counted { get; private set; }
    public int Late { get; private set; }

    /// <summary>A GO with the running order's offset at that moment (null: no plan to be on time to — not counted).</summary>
    public void Record(TimeSpan? offset, TimeSpan tolerance)
    {
        if (offset is not { } o) return;
        Counted++;
        if (o.Duration() <= tolerance)
        {
            Streak++;
            if (Streak > Best) Best = Streak;
        }
        else
        {
            Streak = 0;
            Late++;
        }
    }

    public void Reset()
    {
        Streak = 0;
        Best = 0;
        Counted = 0;
        Late = 0;
    }

    /// <summary>"4 on time in a row · best 6" — empty until a GO with a plan behind it.</summary>
    public string Words => Counted == 0 ? "" : Streak == 0 ? $"streak reset · best {Best} · {Counted - Late} of {Counted} on time" : $"{Streak} on time in a row · best {Best}";
}
