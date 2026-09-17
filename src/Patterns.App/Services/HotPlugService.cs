using System.Collections.ObjectModel;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Platform.Windows;

namespace Patterns.App.Services;

/// <summary>A display the rig has never met, connected while a screen is missing its own: the operator's choice, in words, with the two buttons.</summary>
public sealed record HotPlugOffer(string StrangerId, string StrangerWords, ScreenPlacement Lost, SubstituteVerdict Verdict)
{
    public string LostName => HotPlugWatch.LostName(Lost);

    public string Words => Verdict.Words;

    public bool CanSubstitute => Verdict.Offered;

    public string SubstituteLabel => Verdict.Fits
        ? $"USE AS SUBSTITUTE FOR '{LostName}'"
        : Verdict.Mode is { } m ? $"FORCE {m.Width}×{m.Height}{(m.Hz > 0 ? $" @ {m.Hz} Hz" : "")} AND SUBSTITUTE FOR '{LostName}'" : "";
}

/// <summary>
/// Output hot-plug, applied: on every topology change the pure watch decides, and this puts it on
/// the rig — a display that re-indexed keeps its screen; a display unplugged leaves its screen
/// waiting, planned and off, with everything programmed for it kept under a waiting id; its own
/// display coming back is adopted and turned on; a display the rig has never met, connected while a
/// screen waits, is offered as a substitute (when its resolution and rate match, or can be forced)
/// or as its own screen. Every turn is said on the status line, in the journal and on the health
/// line, so nothing happens to the room without a sentence.
/// </summary>
public sealed class HotPlugService
{
    private readonly AppServices _s;
    private bool _applying;
    private bool _sawDisplays;
    private string _lastNote = "";
    private (ScreenPlacement Lost, string Label, (int Width, int Height, int Hz) Mode)? _pendingSubstitute;

    public HotPlugService(AppServices services) => _s = services;

    /// <summary>The clock the words read; the tests pin it.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The modes a display offers; Windows answers, the tests answer for it.</summary>
    public Func<ScreenInfo, IReadOnlyList<(int Width, int Height, int Hz)>> ModesOf { get; set; } = DefaultModes;

    /// <summary>Forces a display's mode: "" on success, else the reason.</summary>
    public Func<ScreenInfo, (int Width, int Height, int Hz), string> ApplyMode { get; set; } = DefaultApply;

    /// <summary>The choices waiting on the operator, one per stranger and screen it could stand in for.</summary>
    public ObservableCollection<HotPlugOffer> Offers { get; } = new();

    /// <summary>The screens waiting for their display.</summary>
    public IReadOnlyList<ScreenPlacement> LostScreens => HotPlugWatch.LostScreens(_s.State);

    /// <summary>The Screens page's line: which screens wait for their display — or, with none, what was decided last.</summary>
    public string Status
    {
        get
        {
            var lost = LostScreens;
            return lost.Count == 0 ? _lastNote : string.Join(" ", lost.Select(p => HotPlugWatch.LostWords(p)));
        }
    }

    /// <summary>The health line's clause while a screen is missing; "" otherwise.</summary>
    public string HealthWords => HotPlugWatch.HealthWords(LostScreens);

    /// <summary>On every topology change (UI thread), before the outputs re-apply and the desk reconciles.</summary>
    public void OnScreensChanged()
    {
        if (_applying) return; // the publish at the end of a pass refreshes the screens: the pass under way already knows
        _applying = true;
        try
        {
            // One edit, one publish: every rename and adoption lands quietly inside it, so no
            // publish — and no reconcile the desk runs on one — ever sees the rig half-moved.
            var programTouched = false;
            _s.BulkEdit(() => programTouched = Apply());
            if (programTouched) _s.RepublishNow();
        }
        catch (Exception ex)
        {
            Log.Error("Hot-plug reconciliation failed.", ex);
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Everything programmed against one id moved onto another, in the show and — while EDIT SAFE holds one — the frozen program; true when the program was touched.</summary>
    private bool RenameQuietly(string oldId, string newId)
    {
        ContentTargets.RenameScreen(_s.State, oldId, newId);
        if (_s.Sandbox.ProgramState is not { } air) return false;
        ContentTargets.RenameScreen(air, oldId, newId);
        return true;
    }

    /// <summary>
    /// Round 76: the ids the last pass moved — a display re-identified after a hot-plug, old id → new id, a
    /// swap's step aside followed through — so the output windows carry over to the new ids rather than
    /// close and open again (black on every output the unplugged display had nothing to do with).
    /// </summary>
    public IReadOnlyDictionary<string, string> RecentRenames { get; private set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Round 76: the ids of the screens the last pass marked lost (their ids before the pass) — their windows close first, before any other screen is matched to an id one of them may have carried.</summary>
    public IReadOnlySet<string> RecentLost { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Round 76: one up per pass, so a reader acts on a pass's renames once and never on a stale map.</summary>
    public int Pass { get; private set; }

    private bool Apply()
    {
        var state = _s.State;
        var now = _s.Screens.Real.Select(Fact).ToList();
        // Round 77: before any display has been seen at all — the boot's first refresh, before the window
        // is attached — an empty list says nothing about the rig, and deciding on it marked every
        // screen unplugged and then back again a moment later (journaled, notified, the outputs' windows
        // re-planned) at every start. Once a display has been seen, an empty list is every display gone.
        if (now.Count == 0 && !_sawDisplays) return false;
        if (now.Count > 0) _sawDisplays = true;
        var plan = HotPlugWatch.Decide(state.Output.Placements.ToList(), now);
        var when = Clock();
        var programTouched = false;
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var lostIds = new HashSet<string>(StringComparer.Ordinal);

        // Losses first: a lost screen's old id may be the very id a re-indexed display now carries.
        foreach (var p in plan.Lost)
        {
            lostIds.Add(p.ScreenId);
            programTouched |= Lose(p, when);
        }
        // Swaps: a display that took another screen's old id — that screen moves aside first, so no two screens share an id, even for a moment.
        foreach (var (p, d) in plan.Renamed)
        {
            var holder = state.Output.Placements.FirstOrDefault(q => !ReferenceEquals(q, p) && q.ScreenId == d.Id);
            if (holder is not null)
            {
                var aside = ScreenPlacement.PlannedIdPrefix + "move-" + Guid.NewGuid().ToString("N")[..8];
                renames[holder.ScreenId] = aside;
                programTouched |= RenameQuietly(holder.ScreenId, aside);
            }
        }
        foreach (var (p, d) in plan.Renamed)
        {
            var old = p.ScreenId;
            renames[old] = d.Id;
            programTouched |= RenameQuietly(old, d.Id);
            Log.Info($"Display re-identified after a hot-plug: {old} → {d.Id} ({d.Words}).");
        }
        RecentRenames = HotPlugWatch.Resolved(renames);
        RecentLost = lostIds;
        Pass++;
        foreach (var (p, d) in plan.Returned)
        {
            programTouched |= Adopt(p, d, "is back", substitute: false);
        }

        // A substitute the operator asked for, once the mode change came through: the display is back under a new id and size.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        if (_pendingSubstitute is { } pending)
        {
            var d = now.FirstOrDefault(x => string.Equals(x.Label.Trim(), pending.Label.Trim(), StringComparison.Ordinal) && x.Width == pending.Mode.Width && x.Height == pending.Mode.Height);
            if (d is not null && HotPlugWatch.IsLost(pending.Lost))
            {
                _pendingSubstitute = null;
                taken.Add(d.Id);
                programTouched |= Adopt(pending.Lost, d, "has a substitute", substitute: true);
            }
            else if (d is null && !now.Any(x => string.Equals(x.Label.Trim(), pending.Label.Trim(), StringComparison.Ordinal)))
            {
                _pendingSubstitute = null; // the display went before its mode came through
            }
        }

        var lost = LostScreens;
        foreach (var d in plan.Strangers)
        {
            if (taken.Contains(d.Id) || lost.Count == 0) continue; // no screen waits: the desk adds it as a screen of its own, as ever
            AddStranger(d, lost);
        }

        // Offers whose display went, or whose screen has its display again, go with them.
        for (var i = Offers.Count - 1; i >= 0; i--)
        {
            var o = Offers[i];
            if (now.All(d => d.Id != o.StrangerId) || !HotPlugWatch.IsLost(o.Lost)) Offers.RemoveAt(i);
        }
        Remember(now);
        return programTouched;
    }

    /// <summary>After the desk placed the displays it found: what each live screen's display is, so the next change can tell it again.</summary>
    public void RememberDisplays() => Remember(_s.Screens.Real.Select(Fact).ToList());

    /// <summary>What each live screen's display is — name, size, place, rate — so the next change can tell it again.</summary>
    private void Remember(IReadOnlyList<DisplayFact> now)
    {
        var byId = now.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var stale = _s.State.Output.Placements.Where(p => !p.Planned && byId.TryGetValue(p.ScreenId, out var d) && (p.DisplayKey != d.Key || p.DisplayOrigin != d.Origin || (d.Hz > 0 && p.DisplayHz != d.Hz))).ToList();
        if (stale.Count == 0) return;
        _s.BulkEdit(() =>
        {
            foreach (var p in stale)
            {
                var d = byId[p.ScreenId];
                p.DisplayKey = d.Key;
                p.DisplayOrigin = d.Origin;
                if (d.Hz > 0) p.DisplayHz = d.Hz;
            }
        });
    }

    /// <summary>A screen's every trace under a new id — the show, and the frozen program's own placements while EDIT SAFE holds one.</summary>
    private bool RenameEverywhere(ScreenPlacement p, string newId, bool unplan)
    {
        var oldId = p.ScreenId;
        if (unplan) p.Planned = false;
        var touched = false;
        if (_s.Sandbox.ProgramState is { } air)
        {
            foreach (var q in air.Output.Placements.Where(q => q.ScreenId == oldId))
            {
                if (unplan) q.Planned = false;
                touched = true;
            }
        }
        return RenameQuietly(oldId, newId) || touched;
    }

    /// <summary>The display went: the screen waits, planned and off, under an id no display can carry — everything programmed for it kept.</summary>
    private bool Lose(ScreenPlacement p, DateTime when)
    {
        var name = HotPlugWatch.LostName(p);
        var size = HotPlugWatch.SizeOf(p.DisplayKey);
        p.WasEnabled = p.Enabled;
        p.WasPinned = p.UserPinned;
        p.Enabled = false;
        p.UserPinned = true;
        p.Planned = true;
        if (size is { } s)
        {
            p.PlannedWidth = s.Width;
            p.PlannedHeight = s.Height;
        }
        p.LostAtUtc = when;
        var touched = RenameEverywhere(p, HotPlugWatch.LostId(), unplan: false);
        var words = "SCREEN UNPLUGGED — " + HotPlugWatch.LostWords(p);
        Say(words, "ScreenLost", name, warn: true);
        return touched;
    }

    /// <summary>
    /// A display for a waiting screen — its own back, or a substitute: adopted, and turned on as it
    /// was. Everything programmed against the waiting id follows onto the display, in the show and
    /// the frozen program alike; a placement the desk gave the display meanwhile gives way.
    /// </summary>
    private bool Adopt(ScreenPlacement p, DisplayFact d, string how, bool substitute)
    {
        var name = HotPlugWatch.LostName(p);
        var enabled = p.WasEnabled;
        var pinned = p.WasPinned;
        var state = _s.State;
        if (state.Output.Placements.FirstOrDefault(x => x.ScreenId == d.Id) is { } existing && !ReferenceEquals(existing, p))
        {
            state.Output.Placements.Remove(existing);
            var stale = state.Independent.FirstOrDefault(a => a.ScreenId == d.Id);
            if (stale is not null) state.Independent.Remove(stale);
        }
        var touched = RenameEverywhere(p, d.Id, unplan: true);
        p.Enabled = enabled;
        p.UserPinned = pinned;
        p.LostAtUtc = null;
        p.DisplayKey = d.Key;
        p.DisplayOrigin = d.Origin;
        p.DisplayHz = d.Hz;
        for (var i = Offers.Count - 1; i >= 0; i--)
        {
            if (Offers[i].StrangerId == d.Id || ReferenceEquals(Offers[i].Lost, p)) Offers.RemoveAt(i);
        }
        var words = $"{(substitute ? "SCREEN SUBSTITUTED" : "SCREEN BACK")} — '{name}' {how}: {d.Words} adopted and {(enabled ? "turned on" : "left off, as it was")}.";
        Say(words, substitute ? "ScreenSubstitute" : "ScreenBack", name, warn: false);
        return touched;
    }

    /// <summary>A display the rig has never met while a screen waits: its own placement, off, and the offer.</summary>
    private void AddStranger(DisplayFact d, IReadOnlyList<ScreenPlacement> lost)
    {
        var info = _s.Screens.Real.FirstOrDefault(s => s.Id == d.Id);
        if (info is null) return;
        var placement = new ScreenPlacement
        {
            ScreenId = d.Id,
            Enabled = false,
            UserPinned = true,
            X = _s.RigEditor.NextFreeX(),
            Y = 0,
            DisplayKey = d.Key,
            DisplayOrigin = d.Origin,
            DisplayHz = d.Hz,
        };
        _s.State.Output.Placements.Add(placement);
        IReadOnlyList<(int, int, int)> modes;
        try
        {
            modes = ModesOf(info);
        }
        catch (Exception ex)
        {
            Log.Warn("Hot-plug: the display's modes could not be read.", ex);
            modes = Array.Empty<(int, int, int)>();
        }
        var verdicts = new List<string>();
        foreach (var l in lost)
        {
            var verdict = HotPlugWatch.Assess(l, d, modes);
            Offers.Add(new HotPlugOffer(d.Id, d.Words, l, verdict));
            verdicts.Add(verdict.Words);
        }
        var words = $"NEW DISPLAY — {d.Words} connected while {string.Join(" and ", lost.Select(l => $"'{HotPlugWatch.LostName(l)}'"))} {(lost.Count == 1 ? "is" : "are")} missing. {string.Join(" ", verdicts)} It stays off until you choose (Screens page).";
        Say(words, "ScreenStranger", d.Words, warn: true);
    }

    /// <summary>The operator's choice: the stranger stands in for the screen — now, or once its mode is forced.</summary>
    public string Substitute(HotPlugOffer offer)
    {
        if (!Offers.Contains(offer)) return "That choice is gone — the display, or the screen's wait, went.";
        var info = _s.Screens.Real.FirstOrDefault(s => s.Id == offer.StrangerId);
        if (info is null) return $"{offer.StrangerWords} is not connected any more.";
        if (!HotPlugWatch.IsLost(offer.Lost)) return $"'{offer.LostName}' has its display again — nothing to substitute.";
        var d = Fact(info);
        if (offer.Verdict.Fits)
        {
            _applying = true;
            try
            {
                var programTouched = false;
                _s.BulkEdit(() => programTouched = Adopt(offer.Lost, d, "has a substitute", substitute: true));
                if (programTouched) _s.RepublishNow();
            }
            finally
            {
                _applying = false;
            }
            return _lastNote;
        }
        if (offer.Verdict.Mode is { } mode)
        {
            string error;
            try
            {
                error = ApplyMode(info, mode);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (error.Length > 0) return $"The mode could not be forced: {error}";
            _pendingSubstitute = (offer.Lost, info.Label, mode);
            Offers.Remove(offer);
            var words = $"Switching {d.Words} to {mode.Width}×{mode.Height}{(mode.Hz > 0 ? $" @ {mode.Hz} Hz" : "")} — it stands in for '{offer.LostName}' once the mode is in force.";
            _lastNote = words;
            Log.Info(words);
            return words;
        }
        return offer.Verdict.Words;
    }

    /// <summary>The operator's other choice: the stranger is a screen of its own, on; the waiting screen keeps waiting.</summary>
    public string KeepOwn(HotPlugOffer offer)
    {
        var placement = _s.State.Output.Placements.FirstOrDefault(p => p.ScreenId == offer.StrangerId);
        if (placement is null) return $"{offer.StrangerWords} is not connected any more.";
        _s.BulkEdit(() =>
        {
            placement.Enabled = true;
            placement.UserPinned = true;
        });
        for (var i = Offers.Count - 1; i >= 0; i--)
        {
            if (Offers[i].StrangerId == offer.StrangerId) Offers.RemoveAt(i);
        }
        var words = $"{offer.StrangerWords} is its own screen, on — set it up on the Screens page. '{offer.LostName}' still waits for its display.";
        Say(words, "ScreenOwn", offer.StrangerWords, warn: false);
        return words;
    }

    private void Say(string words, string kind, string target, bool warn)
    {
        _lastNote = words;
        if (warn) Log.Warn(words);
        else Log.Info(words);
        _s.Notify(words);
        try
        {
            _s.Journal.Record("Rig", kind, target, warn ? "Alert" : "Done", words);
        }
        catch (Exception ex)
        {
            Log.Warn("The journal could not take the hot-plug line.", ex);
        }
    }

    public static DisplayFact Fact(ScreenInfo s) => new(s.Id, s.Label, s.Bounds.Width, s.Bounds.Height, s.Bounds.X, s.Bounds.Y, s.Hz);

    private static IReadOnlyList<(int Width, int Height, int Hz)> DefaultModes(ScreenInfo info)
    {
        if (!DisplayModes.Supported) return Array.Empty<(int, int, int)>();
        var device = DisplayModes.DeviceFor(info.Bounds.ToRaster());
        return device is null ? Array.Empty<(int, int, int)>() : DisplayModes.List(device).Select(m => (m.Width, m.Height, m.Hz)).ToList();
    }

    private static string DefaultApply(ScreenInfo info, (int Width, int Height, int Hz) mode)
    {
        var device = DisplayModes.DeviceFor(info.Bounds.ToRaster());
        return device is null ? "This display could not be matched to a Windows display device." : DisplayModes.Apply(device, new DisplayMode(mode.Width, mode.Height, mode.Hz));
    }
}
