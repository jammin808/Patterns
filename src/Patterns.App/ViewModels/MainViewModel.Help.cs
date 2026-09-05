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
    // ---- help: the catalogue, its sections and the search ----------------------------

    private string _helpQuery = "";
    private HelpGroup? _helpGroup;
    private bool _helpReadAll;
    private string _helpResultText = "";

    /// <summary>The section chips on the Help page: ALL, then one per group of the catalogue.</summary>
    public IReadOnlyList<HelpGroupChip> HelpGroups { get; }

    /// <summary>The cards shown: the section's topics in catalogue order, or a search's hits strongest first.</summary>
    public System.Collections.ObjectModel.ObservableCollection<HelpRow> HelpRows { get; } = new();

    public RelayCommand ClearHelpCommand { get; }

    /// <summary>The words typed into the Help search; blank shows the section again.</summary>
    public string HelpQuery
    {
        get => _helpQuery;
        set
        {
            if (!Set(ref _helpQuery, value ?? "")) return;
            Raise(nameof(IsHelpSearching));
            RefreshHelpRows();
        }
    }

    public bool IsHelpSearching => _helpQuery.Trim().Length > 0;

    /// <summary>The section shown; null = every topic.</summary>
    public HelpGroup? HelpGroupFilter
    {
        get => _helpGroup;
        set
        {
            if (_helpGroup == value) return;
            _helpGroup = value;
            Raise(nameof(HelpGroupFilter));
            RefreshHelpRows();
        }
    }

    /// <summary>Every card open, for reading the guide through.</summary>
    public bool HelpReadAll
    {
        get => _helpReadAll;
        set
        {
            if (!Set(ref _helpReadAll, value)) return;
            foreach (var row in HelpRows) row.IsOpen = value || row.HasSnippet;
        }
    }

    /// <summary>"37 topics in the order a show happens…" / "6 topics match 'stinger'…".</summary>
    public string HelpResultText { get => _helpResultText; private set => Set(ref _helpResultText, value); }

    /// <summary>The catalogue topics that live on a page, for its ? TIPS flyout.</summary>
    public IReadOnlyList<HelpTopic> HelpTopicsFor(string pageHeader) => HelpTopics.ForPage(pageHeader);

    /// <summary>Opens the Help page on one topic — its card open, the search cleared, every section shown.</summary>
    public void OpenHelpTopic(string id)
    {
        if (HelpTopics.Find(id) is not { } topic) return;
        _helpQuery = "";
        Raise(nameof(HelpQuery));
        Raise(nameof(IsHelpSearching));
        _helpGroup = null;
        Raise(nameof(HelpGroupFilter));
        RefreshHelpRows();
        foreach (var row in HelpRows) row.IsOpen = _helpReadAll || row.Id == topic.Id;
        HelpResultText = $"{topic.Title} — in {HelpTopics.GroupLabel(topic.Group)}.";
        SelectPage(Shell.IndexOf("Help"));
    }

    private void RefreshHelpRows()
    {
        var open = HelpRows.Where(r => r.IsOpen && !r.HasSnippet).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        HelpRows.Clear();
        var query = _helpQuery.Trim();
        if (query.Length > 0)
        {
            var hits = HelpSearch.Find(query);
            foreach (var hit in hits) HelpRows.Add(new HelpRow(this, hit.Topic) { Snippet = hit.Snippet, IsOpen = true });
            HelpResultText = hits.Count == 0
                ? $"Nothing matches '{query}' — try another word, a key, or a verb from the wire."
                : $"{hits.Count} topic{(hits.Count == 1 ? "" : "s")} match{(hits.Count == 1 ? "es" : "")} '{query}', the strongest first.";
        }
        else
        {
            var topics = _helpGroup is { } group ? HelpTopics.In(group) : HelpTopics.All;
            foreach (var topic in topics) HelpRows.Add(new HelpRow(this, topic) { IsOpen = _helpReadAll || open.Contains(topic.Id) });
            HelpResultText = _helpGroup is { } shown
                ? $"{topics.Count} topics in {HelpTopics.GroupLabel(shown)} — {HelpTopics.GroupBlurb(shown)}"
                : $"{topics.Count} topics in the order a show happens — pick a section, search, or open a card.";
        }
        foreach (var chip in HelpGroups) chip.Refresh(chip.Group == _helpGroup);
    }

    // ---- walkthroughs: the Help page's step-through scenarios, by role ---------------

    private DeskRole _walkRole = DeskRole.ShowCaller;
    private WalkthroughProgress? _walk;

    /// <summary>The role chips on the Help page.</summary>
    public IReadOnlyList<WalkRoleChip> WalkRoles { get; }

    /// <summary>The picked role's scenarios.</summary>
    public System.Collections.ObjectModel.ObservableCollection<WalkChoice> WalkChoices { get; } = new();

    /// <summary>The open scenario's steps, in order.</summary>
    public System.Collections.ObjectModel.ObservableCollection<WalkStepRow> WalkSteps { get; } = new();

    /// <summary>Who is at the desk: picking a role lists its scenarios and opens the first.</summary>
    public DeskRole WalkRole
    {
        get => _walkRole;
        set
        {
            if (!Set(ref _walkRole, value)) return;
            Raise(nameof(WalkRoleBlurb));
            RebuildWalkList();
            if (Walkthroughs.For(value).FirstOrDefault() is { } first) StartWalkthrough(first.Id);
        }
    }

    public string WalkRoleBlurb => Walkthroughs.RoleBlurb(_walkRole);
    public bool HasWalk => _walk is not null;
    public string? WalkId => _walk?.Walkthrough.Id;
    public string WalkTitle => _walk?.Walkthrough.Title ?? "";
    public string WalkGoal => _walk?.Walkthrough.Goal ?? "";
    public string WalkWords => _walk?.Words ?? "";
    public double WalkFraction => _walk?.Fraction ?? 0;
    public int WalkCurrent => _walk?.Current ?? -1;

    /// <summary>Opens a scenario (its role's chip follows); the steps the show already has are ticked at once.</summary>
    public void StartWalkthrough(string id)
    {
        if (Walkthroughs.Find(id) is not { } w) return;
        _walk = new WalkthroughProgress(w);
        if (w.Role != _walkRole)
        {
            _walkRole = w.Role;
            Raise(nameof(WalkRole));
            Raise(nameof(WalkRoleBlurb));
            RebuildWalkList();
        }
        WalkSteps.Clear();
        for (var i = 0; i < w.Steps.Count; i++) WalkSteps.Add(new WalkStepRow(this, i, w.Steps[i]));
        ObserveWalkChecks(raise: false);
        RaiseWalk();
    }

    private void RebuildWalkList()
    {
        WalkChoices.Clear();
        foreach (var w in Walkthroughs.For(_walkRole)) WalkChoices.Add(new WalkChoice(this, w));
        foreach (var chip in WalkRoles) chip.Refresh(chip.Role == _walkRole);
        foreach (var c in WalkChoices) c.Refresh(_walk?.Walkthrough.Id == c.Id);
    }

    /// <summary>GO on a step: the step becomes the current one and the desk opens its page.</summary>
    internal void WalkGo(int index)
    {
        if (_walk is null) return;
        _walk.Go(index);
        var page = Shell.Pages.FirstOrDefault(p => p.Header == _walk.Walkthrough.Steps[index].Page);
        if (page is not null) SelectPage(page.Index);
        RaiseWalk();
    }

    /// <summary>A hand tick (or its removal) on a step.</summary>
    internal void WalkMark(int index, bool done)
    {
        if (_walk is null) return;
        if (done) _walk.MarkDone(index);
        else _walk.Unmark(index);
        RaiseWalk();
    }

    private void WalkNext()
    {
        if (_walk is null) return;
        _walk.Next();
        RaiseWalk();
    }

    private void WalkBack()
    {
        if (_walk is null) return;
        _walk.Back();
        RaiseWalk();
    }

    private void WalkRestart()
    {
        if (_walk is null) return;
        _walk.Restart();
        RaiseWalk();
    }

    /// <summary>The app's answers for the open scenario's checks, read from the show and the services: a step ticks itself as the desk does the work.</summary>
    private void ObserveWalkChecks(bool raise = true)
    {
        if (_walk is null) return;
        var changed = false;
        for (var i = 0; i < _walk.Count; i++)
        {
            var check = _walk.Walkthrough.Steps[i].Check;
            if (check.Length == 0) continue;
            var met = EvaluateWalkCheck(check) ?? false;
            if (_walk.IsDoneByApp(i) != met)
            {
                _walk.Observe(i, met);
                changed = true;
            }
        }
        if (changed && raise) RaiseWalk();
    }

    private void RaiseWalk()
    {
        Raise(nameof(HasWalk));
        Raise(nameof(WalkId));
        Raise(nameof(WalkTitle));
        Raise(nameof(WalkGoal));
        Raise(nameof(WalkWords));
        Raise(nameof(WalkFraction));
        Raise(nameof(WalkCurrent));
        if (_walk is not null)
        {
            foreach (var row in WalkSteps) row.Refresh(_walk.Current == row.Index, _walk.IsDone(row.Index), _walk.IsDoneByApp(row.Index));
        }
        foreach (var c in WalkChoices) c.Refresh(_walk?.Walkthrough.Id == c.Id);
    }

    /// <summary>
    /// What the show already has, by the name a walkthrough step asks for (<see cref="Walkthroughs.Checks"/>);
    /// null for a name the desk does not know. Public so the tests pin every name to an answer.
    /// </summary>
    public bool? EvaluateWalkCheck(string check) => check switch
    {
        "mode-prep" => State.Mode == ShowMode.Prep,
        "mode-show" => State.Mode == ShowMode.Show,
        "planned-screens" => State.Output.Placements.Any(p => p.IsPlannedDisplay),
        "planned-adopted" => State.Output.Placements.Count > 0 && State.Output.Placements.All(p => !p.IsPlannedDisplay),
        "screens-present" => State.Output.Placements.Any(p => p.Enabled && LiveInfo(p) is not null),
        "canvas-joined" => CanvasGroups().Count > 0,
        "wall-gaps" => State.Output.Placements.Any(p => p.Gaps.Count > 0) || State.Output.CanvasNames.Any(c => c.SeamGapX > 0 || c.SeamGapY > 0),
        "blend-auto" => State.Output.Placements.Any(p => p.BlendAuto),
        "outputs-on" => _services.Outputs.IsLive,
        "looks-saved" => State.LooksAndCues.Looks.Count > 0,
        "cues-present" => State.Stacks.Any(s => s.Cues.Count > 0),
        "cues-timed" => State.Stacks.Any(s => s.Cues.Any(c => c.PlannedStart.Length > 0 || c.PlannedSeconds is not null)),
        "stack-armed" => _services.CueStack.Armed,
        "edit-safe" => _services.Sandbox.Active,
        "remote-on" => State.Control.Enabled,
        "osc-on" => State.Control.OscEnabled,
        "ndi-on" => State.Ndi.Senders.Any(s => s.Enabled),
        "stream-armed" => State.Stream.Active || State.Stream.Destinations.Any(d => d.Enabled),
        "vogs-present" => State.Stingers.Items.Any(i => i.Kind == StingerKind.Vog),
        "stingers-present" => State.Stingers.Items.Any(i => i.Kind != StingerKind.Vog),
        "lower-thirds-designed" => State.LowerThirds.Designs.Count > 0,
        "people-library" => State.LowerThirds.Entries.Count > 0,
        "web-source" => ActivePattern.Kind == PatternKind.Media && ActivePattern.Media.Source == MediaSource.Web,
        "layers-on" => ActivePattern.Layer1.Enabled || ActivePattern.Layer2.Enabled,
        "beacon-on" => State.Watchdog.BeaconEnabled || State.Watchdog.BeaconListen,
        "multiview-present" => State.Pattern.Kind == PatternKind.Multiview || State.Independent.Any(a => a.Pattern.Kind == PatternKind.Multiview),
        _ => null,
    };

    public string GroupSummary
    {
        get
        {
            var arranged = BuildArranged();
            if (arranged.Count == 0) return "No screens enabled — outputs would be empty.";
            var groups = ScreenLayout.Groups(arranged);
            var parts = new List<string>();
            var canvasIndex = 0;
            foreach (var g in groups.Where(g => g.Count > 1))
            {
                var u = ScreenLayout.Union(g);
                parts.Add($"Canvas {(char)('A' + canvasIndex++)}: {g.Count} screens · {u.Width}×{u.Height} px");
            }
            var singles = groups.Count(g => g.Count == 1);
            if (singles > 0) parts.Add($"{singles} single screen{(singles == 1 ? "" : "s")}");
            return string.Join("   ·   ", parts);
        }
    }

    private void ResetLayout()
    {
        var screens = _services.Screens.All.ToList();
        _services.BulkEdit(() =>
        {
            State.Output.Placements.Clear();
        });
        ReconcilePlacements(screens);
        StatusMessage = "Screen layout reset.";
    }

    private void RaiseArrangement()
    {
        Raise(nameof(GroupSummary));
        Raise(nameof(SelectedIsGrouped));
        Raise(nameof(BlendReadback));
    }

    private void RaiseSelection()
    {
        Raise(nameof(HasSelection));
        Raise(nameof(SelectedScreenTitle));
        Raise(nameof(SelectedEnabled));
        Raise(nameof(SelectedUseCustom));
        Raise(nameof(SelectedIsGrouped));
        Raise(nameof(GroupSummary));
        Raise(nameof(SelectedRotation));
        Raise(nameof(SelectedBrightness));
        Raise(nameof(SelectedGamma));
        Raise(nameof(SelectedTrimR));
        Raise(nameof(SelectedTrimG));
        Raise(nameof(SelectedTrimB));
        Raise(nameof(SelectedScreenLabel));
        Raise(nameof(SelectedCanvasName));
        Raise(nameof(SelectedIsInCanvas));
        Raise(nameof(SelectedFpsOverride));
        Raise(nameof(SelectedIsDisplay));
        Raise(nameof(SelectedDirectOutput));
        Raise(nameof(DirectOutputStatus));
        Raise(nameof(SelectedRole));
        Raise(nameof(SelectedFollowsCues));
        Raise(nameof(SelectedMirrorOf));
        RebuildMirrorSources();
        RaiseBlend();
        Raise(nameof(SelectedGaps));
        Raise(nameof(SelectedSeamGapX));
        Raise(nameof(SelectedSeamGapY));
        Raise(nameof(GapSummary));
        RefreshDisplayModes();
    }
}
