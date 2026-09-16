using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering.Media;

namespace Patterns.App.Services;

/// <summary>
/// The residency ledger (round 69): every held thing — a decoded picture, a decoder, a browser page, a
/// receiver, a deck's pages — with the reason it stays, gathered once a second from the engines; and
/// the sweep that lets an idle picture go when its grace is up (by the machine's class, shortened as the
/// pressure ladder climbs), so memory is given back on a clock and not only once the budget is passed.
/// The source on air, the preview's, an armed page, the standby cue's pre-roll and what the show names
/// are never swept: each has a reason, and the words say which. STATE, the Media page, the Eye and the
/// assistant read the same rows.
/// </summary>
public sealed class ResidencyService
{
    private readonly AppServices _s;

    public ResidencyService(AppServices services)
    {
        _s = services;
        Grace = Residency.Grace(MemoryBudget.ClassOf(MemoryBudget.MachineMB), MemoryPressure.None);
    }

    /// <summary>The rows as of the last poll: what the room sees first, what is leaving last.</summary>
    public IReadOnlyList<Hold> Holds { get; private set; } = Array.Empty<Hold>();

    /// <summary>The grace an idle picture gets now — the class's, shortened by the pressure.</summary>
    public TimeSpan Grace { get; private set; }

    /// <summary>The rung the last poll read.</summary>
    public MemoryPressure Pressure { get; private set; }

    /// <summary>Pictures the sweep let go for being idle, this session.</summary>
    public int PicturesLetGo { get; private set; }

    /// <summary>Polls taken.</summary>
    public int Polls { get; private set; }

    /// <summary>The ledger in a line: "7 held (412 MB): 3 on air, 1 pre-rolled, 1 named by the show, 2 idle — the first lets go in 12 s".</summary>
    public string Words { get; private set; } = "nothing held";

    /// <summary>The tick clock the idle ages are read on: the machine's, or a test's.</summary>
    public Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    /// <summary>The pictures the show names now — the programme's pattern and layers, each screen's own, the air's while EDIT SAFE is open, the logo, the standby cue's look — kept decoded whether or not a frame drew them.</summary>
    public HashSet<string> Named()
    {
        LookConfig? standby = null;
        try
        {
            standby = PreRoll.LookOf(_s.State, _s.CueStack.StandbyCue);
        }
        catch
        {
            // a stack that will not say has no standby to keep for
        }
        return Residency.NamedPictures(_s.State, _s.Sandbox.ProgramState, standby);
    }

    /// <summary>Once a second, after the pressure ladder: the grace for this rung, the sweep, then the rows.</summary>
    public void Poll(MemoryPressure pressure)
    {
        Polls++;
        Pressure = pressure;
        Grace = Residency.Grace(MemoryBudget.ClassOf(MemoryBudget.MachineMB), pressure);
        var named = Named();
        var now = Clock();
        PicturesLetGo += ImageCache.SweepIdle((long)Grace.TotalMilliseconds, named.Contains, now);
        Holds = Gather(named, now);
        Words = Residency.Summary(Holds, Grace);
    }

    /// <summary>The rows, from every engine and the picture cache.</summary>
    public IReadOnlyList<Hold> Gather(ISet<string> named, long nowTicks)
    {
        var list = new List<Hold>();
        var state = _s.State;
        foreach (var (path, bytes, idleMs) in ImageCache.Snapshot(nowTicks))
        {
            var idle = idleMs / 1000.0;
            list.Add(new Hold("pic:" + path, "picture", Path.GetFileName(path), Residency.ForPicture(idle, named.Contains(path)), bytes, idle));
        }
        foreach (var (key, buses, preRoll, retiring, bytes, status) in _s.Video.Holds())
        {
            list.Add(new Hold(key, "clip", Label(state, key), retiring ? HoldReason.Retiring : Residency.ForBuses(buses, preRoll), bytes, 0, status));
        }
        foreach (var (key, preRoll, onAir, armed, retiring, bytes, status) in _s.WebIn.Holds())
        {
            var reason = retiring ? HoldReason.Retiring : armed ? HoldReason.Armed : preRoll ? HoldReason.PreRolled : onAir ? HoldReason.OnAir : HoldReason.Preview;
            list.Add(new Hold(key, "page", Label(state, key), reason, bytes, 0, status));
        }
        foreach (var (key, buses, retiring, status) in _s.NdiIn.Holds())
        {
            list.Add(new Hold(key, "NDI feed", Label(state, key), retiring ? HoldReason.Retiring : Residency.ForBuses(buses, false), 0, 0, status));
        }
        foreach (var (key, buses, bytes, status) in _s.DeckIn.Holds())
        {
            list.Add(new Hold(key, "deck", Label(state, key), Residency.ForBuses(buses, false), bytes, 0, status));
        }
        return list.OrderBy(h => Residency.Rank(h.Reason)).ThenBy(h => h.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The reason a mounted key has now, as the desk says it, for the Media page's line and the Eye's words; "" when the ledger has no row for it.</summary>
    public string ReasonWords(string key)
    {
        foreach (var h in Holds)
        {
            if (h.Key == key) return Residency.Word(h.Reason);
        }
        return "";
    }

    private static string Label(ShowState state, string key)
    {
        var colon = key.IndexOf(':');
        var rest = colon > 0 ? key[(colon + 1)..] : key;
        var label = state.InputLabel(key, "");
        if (label.Length > 0) return label;
        if (key.StartsWith("web:", StringComparison.Ordinal)) return WebAddress.ShortName(rest);
        if (key.StartsWith("vid:", StringComparison.Ordinal) || key.StartsWith("deck:", StringComparison.Ordinal)) return Path.GetFileName(rest);
        return rest;
    }
}
