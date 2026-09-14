namespace Patterns.Core.Services;

/// <summary>Which lane of the desk's second an area runs in.</summary>
public enum TickLane
{
    /// <summary>Every tick, first, whatever the budget: the show's truth — authority, the twin and the stage, the cue schedule, the run's timing, the tallies, the clock, the resources released.</summary>
    Critical,
    /// <summary>Every tick, after the critical lane: the desk's own surfaces that read once a second.</summary>
    Steady,
    /// <summary>Under a small budget, in turn: enumeration, discovery, the metrics, the slow pages' lines. What does not fit this tick runs first next tick.</summary>
    Housekeeping,
}

/// <summary>
/// The desk's second, in lanes. The once-a-second poll used to service some twenty areas in one
/// go on the UI thread; as the desk grew, so did the tick, and a picker enumerating devices sat
/// in the same second as the cue schedule. Here the critical areas run every tick, first; the
/// steady ones after them; and the housekeeping runs under a budget of a few milliseconds, in
/// turn, so that when it does not fit — a slow enumeration, a network probe — the rest of it
/// waits for the next tick rather than the operator waiting for it. The areas that were carried
/// run first next time, so none starves. Pure; the desk's poll asks it what to run.
/// </summary>
public sealed class TickScheduler
{
    /// <summary>The housekeeping lane's budget per tick, ms: what the whole lane may cost the desk in a second.</summary>
    public const double HousekeepingBudgetMs = 4;

    public sealed record Area(string Name, TickLane Lane);

    /// <summary>This desk's housekeeping budget per tick, ms: the constant unless a test says otherwise (a test that reads a page's line after one poll wants the lane whole, whatever the machine running the tests is doing).</summary>
    public double BudgetMs { get; set; } = HousekeepingBudgetMs;

    private readonly List<Area> _critical = new();
    private readonly List<Area> _steady = new();
    private readonly List<Area> _housekeeping = new();
    private int _housekeepingStart;
    private readonly Queue<Area> _carried = new();

    public TickScheduler Add(string name, TickLane lane)
    {
        var area = new Area(name, lane);
        switch (lane)
        {
            case TickLane.Critical: _critical.Add(area); break;
            case TickLane.Steady: _steady.Add(area); break;
            default: _housekeeping.Add(area); break;
        }
        return this;
    }

    public IReadOnlyList<Area> Critical => _critical;
    public IReadOnlyList<Area> Steady => _steady;
    public IReadOnlyList<Area> Housekeeping => _housekeeping;

    /// <summary>Areas carried over from the last tick, still to run — first next tick.</summary>
    public int Carried => _carried.Count;

    /// <summary>
    /// The housekeeping areas in the order this tick should try them: what the last tick carried
    /// over first, then the rest from a start that moves round by one each tick, so over the
    /// ticks every area is first as often as any other.
    /// </summary>
    public IReadOnlyList<Area> HousekeepingOrder()
    {
        var order = new List<Area>(_housekeeping.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var carried in _carried)
        {
            if (seen.Add(carried.Name)) order.Add(carried);
        }
        for (var i = 0; i < _housekeeping.Count; i++)
        {
            var area = _housekeeping[(_housekeepingStart + i) % Math.Max(1, _housekeeping.Count)];
            if (seen.Add(area.Name)) order.Add(area);
        }
        return order;
    }

    /// <summary>
    /// The tick is over: the areas that did not fit are carried to the next, and the start moves
    /// round. Returns how many were carried.
    /// </summary>
    public int Settle(IReadOnlyList<Area> notRun)
    {
        _carried.Clear();
        foreach (var area in notRun) _carried.Enqueue(area);
        if (_housekeeping.Count > 0) _housekeepingStart = (_housekeepingStart + 1) % _housekeeping.Count;
        return _carried.Count;
    }

    /// <summary>Whether another housekeeping area may start, given what the lane has spent this tick.</summary>
    public static bool Fits(double spentMs, double budgetMs = HousekeepingBudgetMs) => spentMs < budgetMs;
}

/// <summary>
/// The desk's pages warmed in the order a show reaches for them — the run's pages first, the
/// build pages next, the rest as they come — and only while the desk has the headroom: never
/// while the stack is armed with the outputs live (the show is on), never while a page switch is
/// in flight, and not while the tick is stressed. Pure; the window's warm-up asks it.
/// </summary>
public static class WarmUpPlan
{
    /// <summary>The pages a show reaches for first, in order.</summary>
    public static readonly IReadOnlyList<string> Likely = new[] { "Cues", "Screens", "Media", "Interactive", "Machine", "Looks", "Panel" };

    /// <summary>The pages to build, likely ones first in their order, then the rest in the order given.</summary>
    public static IReadOnlyList<string> Order(IEnumerable<string> pages)
    {
        var list = pages.ToList();
        var first = Likely.Where(list.Contains).ToList();
        first.AddRange(list.Where(p => !Likely.Contains(p)));
        return first;
    }

    /// <summary>Whether the warm-up should wait a moment rather than build now.</summary>
    /// <param name="armedAndLive">The stack is armed and the outputs are live: the show is on, and the desk's thread is the caller's.</param>
    /// <param name="switchOpen">A page switch is in flight: its frame comes first.</param>
    /// <param name="lastTickMs">The last desk tick, ms: a stressed tick means no room for a build.</param>
    public static bool ShouldPause(bool armedAndLive, bool switchOpen, double lastTickMs)
        => armedAndLive || switchOpen || lastTickMs > TickBudget.SlowMs;

    /// <summary>How long to wait when paused before looking again.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    /// <summary>
    /// On a small machine the pages a show may never open are left to demand: fewer than this
    /// many gigabytes of RAM builds the likely pages alone.
    /// </summary>
    public const double SmallMachineGB = 8;

    /// <summary>Whether a page is worth building ahead on this machine: every page on a big one, the likely ones alone on a small one.</summary>
    public static bool BuildAhead(string page, double machineGB) => machineGB >= SmallMachineGB || Likely.Contains(page);
}
