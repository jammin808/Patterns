using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Particles;
using Patterns.Core.Media;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Patterns.Core.LowerThirds;

namespace Patterns.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---- screen arrangement -------------------------------------------------

    private void OnScreensChanged()
    {
        Screens.AdoptRenamedDisplay();
        ReconcilePlacements();
        RefreshOutputsStatus();
    }

    public void ReconcilePlacements() => ReconcilePlacements(_services.Screens.All.ToList());

    /// <summary>
    /// Keeps placements in sync with detected screens (the rig editor does the placing), then
    /// keeps a screen selected and brings every list that reads the rig up to date.
    /// </summary>
    public void ReconcilePlacements(IReadOnlyList<ScreenInfo> screens)
    {
        var placements = State.Output.Placements;
        _services.RigEditor.ReconcilePlacements(screens);
        _services.HotPlug.RememberDisplays();   // a screen placed just now is known again after the next hot-plug

        if (Screens.SelectedPlacement is null || placements.All(p => p != Screens.SelectedPlacement))
        {
            Screens.SelectedPlacement = placements.FirstOrDefault(p => LiveInfo(p) is not null);
        }

        EnsureAssignmentsForCustomScreens();
        RebuildEditTargets();
        RebuildNdiSources();
        RebuildStreamSources();
        RebuildMultiviewTargets();
        RefreshWallDestinations();
        RefreshMonitorOutputs();
        RaiseArrangement();
        // Loading a show, or plugging a display in, can change the mode and the planned set.
        RefreshOutputsStatus();
    }

    public ScreenInfo? LiveInfo(ScreenPlacement placement)
        => _services.Screens.All.FirstOrDefault(s => s.Id == placement.ScreenId);

    // ---- hot-plug: a display unplugged, back, or new ---------------------------------------

    /// <summary>The choices waiting on the operator: a new display while a screen is missing its own.</summary>
    public System.Collections.ObjectModel.ObservableCollection<HotPlugOffer> HotPlugOffers => _services.HotPlug.Offers;

    private string _hotPlugStatus = "";

    /// <summary>Which screens wait for their display, or what was decided last — the Screens page's banner.</summary>
    public string HotPlugStatus
    {
        get => _hotPlugStatus;
        private set
        {
            if (Set(ref _hotPlugStatus, value)) Raise(nameof(HasHotPlug));
        }
    }

    public bool HasHotPlug => _services.HotPlug.LostScreens.Count > 0 || HotPlugOffers.Count > 0;

    private RelayCommand<HotPlugOffer>? _substituteScreen;
    private RelayCommand<HotPlugOffer>? _ownScreen;

    /// <summary>The new display stands in for the missing screen — now, or once its mode is forced.</summary>
    public RelayCommand<HotPlugOffer> SubstituteScreenCommand => _substituteScreen ??= new RelayCommand<HotPlugOffer>(o =>
    {
        if (o is null) return;
        StatusMessage = _services.HotPlug.Substitute(o);
        RefreshOutputsStatus();
    });

    /// <summary>The new display is a screen of its own; the missing screen keeps waiting.</summary>
    public RelayCommand<HotPlugOffer> OwnScreenCommand => _ownScreen ??= new RelayCommand<HotPlugOffer>(o =>
    {
        if (o is null) return;
        StatusMessage = _services.HotPlug.KeepOwn(o);
        RefreshOutputsStatus();
    });

    /// <summary>The Machine page's line: how many outputs ask, what is in force, what the next start does.</summary>
    public string DirectOutputSummary => DirectOutputService.Summary(State);

    internal void RaiseDirectOutputSummary() => Raise(nameof(DirectOutputSummary));

    /// <summary>The Screens page — the selected screen and everything set on it, the planned screens and their adoption.</summary>
    public ScreensPage Screens { get; }

    public List<ArrangedScreen> BuildArranged()
    {
        var result = new List<ArrangedScreen>();
        foreach (var p in State.Output.Placements)
        {
            var info = LiveInfo(p);
            if (info is not null && p.Enabled)
            {
                var size = OutputWindowManager.EffectiveSize(p, info);
                result.Add(new ArrangedScreen(p.ScreenId, SKRectI.Create(p.X, p.Y, size.Width, size.Height), p.BlendsOverlaps));
            }
        }
        return result;
    }

    // ---- roles, locks and repeaters ---------------------------------------------

    /// <summary>A lock goes through the action layer (journaled, the sandbox and the air agree); OWN lights up when the lock gave the target its picture.</summary>
    internal void SetLocked(string targetId, bool locked)
    {
        _services.Actions.Execute(new ShowAction(locked ? ShowActionKind.ScreenLock : ShowActionKind.ScreenUnlock, targetId), ActionOrigin.Desk);
        RebuildEditTargets();
        RefreshTakeScope();
    }

    // ---- custom labels ------------------------------------------------------

    /// <summary>
    /// A screen's label or a canvas's name was typed: every tile and edit target that names it
    /// takes the new words in place. This runs on every keystroke, and rebuilding the wall here —
    /// every tile a new object, every PGM and PVW pane a new render pipeline — was a wall that
    /// re-mounted itself under the pointer for each letter typed.
    /// </summary>
    internal void RefreshTargetNames()
    {
        var geo = Rig.Geometry(State, _services.Screens.All);
        foreach (var tile in SwitcherTiles)
        {
            if (tile.TargetId is { } target) tile.Title = geo.LabelFor(State, target);
        }
        var groups = CanvasGroups();
        for (var i = 0; i < EditTargets.Count; i++)
        {
            var t = EditTargets[i];
            if (t.ScreenId is not { } id) continue;
            string label;
            if (ContentTargets.IsCanvasKey(id))
            {
                var at = groups.FindIndex(g => CanvasNameConfig.KeyFor(g.Select(m => m.ScreenId)) == id);
                if (at < 0) continue;
                var letter = ((char)('A' + at)).ToString();
                label = $"Canvas {letter} — {CanvasNameFor(groups[at], letter)}";
            }
            else
            {
                var p = State.Output.Placements.FirstOrDefault(x => x.ScreenId == id);
                var info = p is null ? null : LiveInfo(p);
                if (p is null || info is null) continue;
                label = $"Screen {info.Index + 1} — {LabelFor(p, info)}";
            }
            if (t.Label == label) continue;
            var renamed = new EditTarget(label, id);
            EditTargets[i] = renamed;
            if (_editTarget?.ScreenId == id)
            {
                // The picker's own instance: re-published so the two-way binding finds it in the
                // list (the setter refuses the null a replaced item briefly leaves behind).
                _editTarget = renamed;
                Raise(nameof(EditTarget));
                Raise(nameof(EditTargetBanner));
            }
        }
        Screens.RaiseSelectionTitle();
        var selected = SwitcherTiles.FirstOrDefault(t => t.TargetId == _selectedTargetId);
        SelectedTargetLabel = selected is null || selected.IsProgramTile ? "PGM" : selected.Title;
    }

    // ---- remote screen/group switching --------------------------------------

    private List<(ScreenPlacement Placement, ScreenInfo Info)> OrderedLivePlacements(IReadOnlyList<ScreenInfo>? screens = null)
        => Rig.OrderedLivePlacements(State, screens ?? _services.Screens.All);

    /// <summary>Remote: screen by its overview number → enabled/disabled/toggled.</summary>
    public bool SetScreenEnabled(int number, bool? target, IReadOnlyList<ScreenInfo>? screens = null)
        => _services.Actions.SetScreenEnabled(number, target, screens);

    /// <summary>Joined-canvas letters (A, B, …) → their member placements, arrangement order.</summary>
    internal List<List<ScreenPlacement>> CanvasGroups(IReadOnlyList<ScreenInfo>? screens = null)
        => Rig.CanvasGroups(State, screens ?? _services.Screens.All);

    /// <summary>Remote: every screen of canvas 'A'/'B'… on or off at once.</summary>
    public bool SetGroupEnabled(string letter, bool enabled, IReadOnlyList<ScreenInfo>? screens = null)
        => _services.Actions.SetGroupEnabled(letter, enabled, screens);

    /// <summary>Screen rows for the remote-state JSON. UI thread.</summary>
    public object[] RemoteScreens(IReadOnlyList<ScreenInfo>? screens = null)
        => _services.Actions.RemoteScreens(screens);

    // ---- NDI ----------------------------------------------------------------

    // NDI source entries use "" (not null) for Program, matching NdiSenderConfig.SourceScreenId.
    public ObservableCollection<EditTarget> NdiSources { get; } = new() { new EditTarget("Program", "") };

    public string[] NdiRateKeys => NdiRateTable.Keys;

    private void RebuildNdiSources()
    {
        var wanted = new List<EditTarget> { new("Program", "") };
        // Additive: every entry the picker offered before is still offered, so a stored
        // SourceScreenId can never be blanked by the combo's two-way binding.
        var geo = Rig.Geometry(State, _services.Screens.All);
        foreach (var key in geo.Targets)
        {
            if (ContentTargets.IsCanvasKey(key)) wanted.Add(new EditTarget(geo.LabelFor(State, key), key));
        }
        foreach (var s in _services.Screens.All)
        {
            if (s.IsVirtual) continue; // the feeds' own screens are listed by their feed below
            wanted.Add(new EditTarget($"Screen {s.Index + 1} — {s.Label}", s.Id));
        }
        foreach (var sender in State.Ndi.Senders)
        {
            var name = string.IsNullOrWhiteSpace(sender.Name) ? "Patterns" : sender.Name.Trim();
            wanted.Add(new EditTarget($"Its own screen — NDI · {name} (a look of its own)", sender.OwnScreenId));
        }
        if (State.Stream.UsesOwnScreen) wanted.Add(new EditTarget("The stream's own screen", StreamConfig.OwnScreenId));
        ReplaceIfChanged(NdiSources, wanted);
    }

    private void AddNdiSender()
    {
        _services.RigEditor.AddNdiSender();
        SyncVirtualScreens(); // every send owns a screen of its own from the moment it exists
        StatusMessage = "NDI sender added — it owns a screen on the rig: mirror any target, or give it a look of its own.";
    }

    // ---- prep mode -----------------------------------------------------------

    /// <summary>Pre-programming at the desk: outputs are held closed, planned screens stand in for the rig.</summary>
    public bool IsPrepMode
    {
        get => State.Mode == ShowMode.Prep;
        set
        {
            var mode = value ? ShowMode.Prep : ShowMode.Show;
            if (State.Mode == mode) return;
            if (value && _services.Outputs.IsLive)
            {
                _services.Outputs.CloseAll(); // prep never leaves something on the screens
            }
            State.Mode = mode;
            RaiseModeChanged();
            StatusMessage = value
                ? "PREP — build the rig, screens, inputs and looks; the outputs stay held until you switch to SHOW."
                : "SHOW — outputs can open. Planned screens still need adopting onto real displays.";
            Log.Info(StatusMessage);
        }
    }

    public string ModeBanner => IsPrepMode
        ? "PREP MODE — pre-programming; outputs are held closed"
        : "SHOW MODE";

    /// <summary>Planned screens that have no display behind them yet (blocks a clean GO). A feed's own screen is not one.</summary>
    public int PlannedScreenCount => State.Output.Placements.Count(p => p.IsPlannedDisplay);

    /// <summary>The feeds' own screens on the rig: one per NDI send, one for the stream while it is set to its own.</summary>
    public int VirtualScreenCount => State.Output.Placements.Count(p => p.IsVirtual);

    /// <summary>Keeps the feeds' screens in step with the senders and the stream; on the poll, and after every add or remove.</summary>
    public void SyncVirtualScreens()
    {
        if (!VirtualScreens.Sync(State)) return;
        _services.Screens.Refresh();
        RebuildEditTargets();
        RebuildNdiSources();
        RebuildStreamSources();
        Raise(nameof(VirtualScreenCount));
        Raise(nameof(PlannedScreenCount));
        RaiseModeChanged();
    }

    /// <summary>What the stream can show: a display captured off the desktop, or a rig target rendered by the engine.</summary>
    public ObservableCollection<EditTarget> StreamSources { get; } = new() { new EditTarget("Primary screen (desktop capture)", "") };

    private void RebuildStreamSources()
    {
        var wanted = new List<EditTarget> { new("Primary screen (desktop capture)", "") };
        foreach (var s in _services.Screens.Real)
        {
            wanted.Add(new EditTarget($"Screen {s.Index + 1} — {s.Label} (desktop capture)", s.Id));
        }
        wanted.Add(new EditTarget("Its own screen — rendered, a look of its own", StreamConfig.OwnScreenId));
        var geo = Rig.Geometry(State, _services.Screens.All);
        foreach (var key in geo.Targets)
        {
            if (ContentTargets.IsCanvasKey(key)) wanted.Add(new EditTarget($"{geo.LabelFor(State, key)} (rendered)", key));
        }
        foreach (var p in State.Output.Placements)
        {
            if (p.IsPlannedDisplay) wanted.Add(new EditTarget($"{LabelFor(p)} (planned, rendered)", p.ScreenId));
        }
        ReplaceIfChanged(StreamSources, wanted);
    }

    public string PrepSummary
    {
        get
        {
            var planned = PlannedScreenCount;
            var real = _services.Screens.Real.Count;
            return planned == 0
                ? $"{real} display{(real == 1 ? "" : "s")} detected · no planned screens"
                : $"{real} display{(real == 1 ? "" : "s")} detected · {planned} planned screen{(planned == 1 ? "" : "s")} waiting to be adopted";
        }
    }

    internal void RaiseModeChanged()
    {
        RefreshOutputsStatus();
        RaiseShell();
    }

    private void GoLive() => _services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
}
