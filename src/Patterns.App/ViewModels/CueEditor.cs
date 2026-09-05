using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>A choice in a target picker: the id the model stores and the label the operator reads.</summary>
public sealed record PickItem(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One cue as the list shows it: the model row plus what validation says about it.</summary>
public sealed class CueRow : Observable
{
    private string _summary = "";
    private string _problem = "";
    private string _warning = "";
    private bool _isSelected;
    private bool _isCurrent;

    public CueRow(RunCueConfig cue)
    {
        Cue = cue;
        cue.PropertyChanged += OnCueChanged;
    }

    public RunCueConfig Cue { get; }

    public string Number => Cue.Number;
    public string Name => Cue.Name;
    public string Track => Cue.Track;
    public bool HasTrack => Cue.Track.Length > 0;

    public bool Enabled
    {
        get => Cue.Enabled;
        set => Cue.Enabled = value;
    }

    public bool Ready
    {
        get => Cue.Ready;
        set => Cue.Ready = value;
    }

    /// <summary>"Apply 'Walk-in' + Play audio".</summary>
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    /// <summary>Why the cue cannot run (empty = it can). GO refuses a broken cue; the rest of the list runs.</summary>
    public string Problem { get => _problem; set { if (Set(ref _problem, value)) Raise(nameof(IsBroken)); } }

    public bool IsBroken => _problem.Length > 0;

    public string Warning { get => _warning; set { if (Set(ref _warning, value)) Raise(nameof(HasWarning)); } }

    public bool HasWarning => _warning.Length > 0;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>The cue this list ran last (the clicker's place).</summary>
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    private void OnCueChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RunCueConfig.Number): Raise(nameof(Number)); break;
            case nameof(RunCueConfig.Name): Raise(nameof(Name)); break;
            case nameof(RunCueConfig.Track): Raise(nameof(Track)); Raise(nameof(HasTrack)); break;
            case nameof(RunCueConfig.Enabled): Raise(nameof(Enabled)); break;
            case nameof(RunCueConfig.Ready): Raise(nameof(Ready)); break;
        }
    }

    public void Detach() => Cue.PropertyChanged -= OnCueChanged;
}

/// <summary>One action of the selected cue, with the pickers the spec table says it needs.</summary>
public sealed class ActionRow : Observable
{
    private readonly CueEditor _editor;

    public ActionRow(CueEditor editor, CueActionConfig action)
    {
        _editor = editor;
        Action = action;
        TargetChoices = new ObservableCollection<PickItem>();
        RefreshChoices();
    }

    public CueActionConfig Action { get; }

    public IReadOnlyList<PickItem> KindChoices => CueEditor.KindChoices;

    public PickItem SelectedKind
    {
        get => KindChoices.FirstOrDefault(k => k.Id == Action.Kind.ToString())
               ?? new PickItem(Action.Kind.ToString(), CueActionSpec.Label(Action.Kind));
        set
        {
            if (value is null || !Enum.TryParse<CueActionKind>(value.Id, out var kind) || kind == Action.Kind) return;
            Action.Kind = kind;
            Action.Target = "";
            Action.Value = "";
            RefreshChoices();
            Raise(nameof(SelectedKind));
            Raise(nameof(HasTarget));
            Raise(nameof(HasValue));
            Raise(nameof(TargetHint));
            Raise(nameof(ValueHint));
            Raise(nameof(SelectedTarget));
            Raise(nameof(Value));
            _editor.OnCueEdited();
        }
    }

    public ObservableCollection<PickItem> TargetChoices { get; }

    public bool HasTarget => CueActionSpec.For(Action.Kind).Target != TargetKind.None;

    public bool HasValue => CueActionSpec.For(Action.Kind).Value != ValueKind.None;

    public string TargetHint => CueActionSpec.For(Action.Kind).Target switch
    {
        TargetKind.Look => "Which look…",
        TargetKind.Stinger => "Which VOG or stinger…",
        TargetKind.Part => "Which playlist part…",
        TargetKind.Screen => "Which screen…",
        TargetKind.Canvas => "Which canvas…",
        TargetKind.Stack => "Which list…",
        TargetKind.Music => "Which break music… (blank = resume)",
        TargetKind.LowerThird => "Which lower third…",
        _ => "",
    };

    public string ValueHint => CueActionSpec.For(Action.Kind).Value switch
    {
        ValueKind.Transition => "blank = show default · cut · fade in ms (e.g. 800)",
        ValueKind.Minutes => "minutes, e.g. 5",
        ValueKind.Text => "the message text",
        ValueKind.Percent => "percent, 0–125 (100 = as recorded)",
        ValueKind.Level => "percent, 0–100 (the Spotify device's own volume)",
        _ => "",
    };

    public PickItem? SelectedTarget
    {
        get
        {
            if (Action.Target.Length == 0) return null;
            // A reference by name (an older show, a hand-typed target) resolves like the validator resolves it.
            return TargetChoices.FirstOrDefault(t => t.Id == Action.Target)
                   ?? TargetChoices.FirstOrDefault(t => string.Equals(t.Label, Action.Target, StringComparison.OrdinalIgnoreCase))
                   ?? new PickItem(Action.Target, $"{Action.Target} (not found)");
        }
        set
        {
            if (value is null || value.Id == Action.Target) return;
            Action.Target = value.Id;
            Raise(nameof(SelectedTarget));
            _editor.OnCueEdited();
        }
    }

    public string Value
    {
        get => Action.Value;
        set
        {
            if (Action.Value == value) return;
            Action.Value = value;
            Raise(nameof(Value));
            _editor.OnCueEdited();
        }
    }

    public void RefreshChoices()
    {
        var target = Action.Target;
        TargetChoices.Clear();
        foreach (var item in _editor.ChoicesFor(CueActionSpec.For(Action.Kind).Target)) TargetChoices.Add(item);
        if (target.Length > 0 && TargetChoices.All(t => t.Id != target && !string.Equals(t.Label, target, StringComparison.OrdinalIgnoreCase)))
        {
            TargetChoices.Add(new PickItem(target, $"{target} (not found)"));
        }
        Raise(nameof(SelectedTarget));
    }
}

/// <summary>
/// The Cues page: the caller's stack and the speaker's clicker list, edited in place, validated
/// as you build (debounced), each cue with its readable summary and its Broken reason.
/// </summary>
public sealed class CueEditor : Observable
{
    public static readonly IReadOnlyList<PickItem> KindChoices =
        CueActionSpec.Editable.Select(k => new PickItem(k.ToString(), CueActionSpec.Label(k))).ToList();

    private readonly AppServices _s;
    private readonly Action<string> _status;
    private readonly DispatcherTimer _revalidate;
    private CueStackConfig? _selectedStack;
    private RunCueConfig? _selectedCue;
    private CueValidationReport? _report;
    private string _validationSummary = "";
    private string _stackNotesText = "";
    private ObservableCollection<CueActionConfig>? _watchedActions;

    public CueEditor(AppServices services, Action<string> status)
    {
        _s = services;
        _status = status;
        _revalidate = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _revalidate.Tick += (_, _) =>
        {
            _revalidate.Stop();
            Refresh();
        };

        AddCueCommand = new RelayCommand(() => AddCue());
        RemoveCueCommand = new RelayCommand<CueRow>(row => { if (row is not null) RemoveCue(row.Cue); });
        RemoveSelectedCommand = new RelayCommand(() => { if (SelectedCue is not null) RemoveCue(SelectedCue); });
        MoveCueUpCommand = new RelayCommand(() => MoveCue(-1));
        MoveCueDownCommand = new RelayCommand(() => MoveCue(+1));
        RenumberCommand = new RelayCommand(() =>
        {
            if (SelectedStack is null) return;
            _s.BulkEdit(() => CueNumber.Renumber(SelectedStack.Cues));
            Refresh();
            _status($"{SelectedStack.Name} renumbered.");
        });
        SelectCueCommand = new RelayCommand<CueRow>(row => { if (row is not null) SelectedCue = row.Cue; });
        FireCueCommand = new RelayCommand<CueRow>(row =>
        {
            if (row is null) return;
            _s.Actions.FireCue(row.Cue, ActionOrigin.Desk);
            Refresh();
        });
        FireSelectedCommand = new RelayCommand(() =>
        {
            if (SelectedCue is null) return;
            _s.Actions.FireCue(SelectedCue, ActionOrigin.Desk);
            Refresh();
        });
        AddActionCommand = new RelayCommand(() =>
        {
            if (SelectedCue is null) return;
            SelectedCue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook });
            OnCueEdited();
        });
        RemoveActionCommand = new RelayCommand<ActionRow>(row =>
        {
            if (row is null || SelectedCue is null) return;
            SelectedCue.Actions.Remove(row.Action);
            OnCueEdited();
        });
        MoveActionUpCommand = new RelayCommand<ActionRow>(row => MoveAction(row, -1));
        MoveActionDownCommand = new RelayCommand<ActionRow>(row => MoveAction(row, +1));

        _s.SnapshotPublished += ScheduleRevalidate; // any edit anywhere can change what resolves
        _s.Cues.Changed += RefreshMarkers;
        OnShowLoaded();
    }

    public ObservableCollection<CueStackConfig> Stacks => _s.State.Stacks;

    public ObservableCollection<CueRow> Rows { get; } = new();

    public ObservableCollection<ActionRow> ActionRows { get; } = new();

    public CueStackConfig? SelectedStack
    {
        get => _selectedStack;
        set
        {
            if (value is null || ReferenceEquals(_selectedStack, value)) return;
            _selectedStack = value;
            Raise(nameof(SelectedStack));
            Raise(nameof(IsClickerSelected));
            Raise(nameof(LoopAtEnd));
            Raise(nameof(SuspendAutomation));
            SelectedCue = value.Cues.FirstOrDefault();
            Refresh();
        }
    }

    public bool IsClickerSelected => _selectedStack?.IsClicker == true;

    public bool LoopAtEnd
    {
        get => _selectedStack?.LoopAtEnd ?? false;
        set
        {
            if (_selectedStack is null) return;
            _selectedStack.LoopAtEnd = value;
            Raise(nameof(LoopAtEnd));
        }
    }

    public bool SuspendAutomation
    {
        get => _selectedStack?.SuspendAutomationWhileArmed ?? true;
        set
        {
            if (_selectedStack is null) return;
            _selectedStack.SuspendAutomationWhileArmed = value;
            Raise(nameof(SuspendAutomation));
        }
    }

    public RunCueConfig? SelectedCue
    {
        get => _selectedCue;
        set
        {
            if (ReferenceEquals(_selectedCue, value)) return;
            _selectedCue = value;
            WatchActions(value?.Actions);
            RebuildActionRows();
            Raise(nameof(SelectedCue));
            Raise(nameof(HasSelection));
            RefreshMarkers();
        }
    }

    public bool HasSelection => _selectedCue is not null;

    /// <summary>"All 12 cues can run." or "2 of 12 cues are broken."</summary>
    public string ValidationSummary { get => _validationSummary; private set => Set(ref _validationSummary, value); }

    public string StackNotesText { get => _stackNotesText; private set { if (Set(ref _stackNotesText, value)) Raise(nameof(HasStackNotes)); } }

    public bool HasStackNotes => _stackNotesText.Length > 0;

    public CueValidationReport? Report => _report;

    public RelayCommand AddCueCommand { get; }
    public RelayCommand<CueRow> RemoveCueCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand MoveCueUpCommand { get; }
    public RelayCommand MoveCueDownCommand { get; }
    public RelayCommand RenumberCommand { get; }
    public RelayCommand<CueRow> SelectCueCommand { get; }
    public RelayCommand<CueRow> FireCueCommand { get; }
    public RelayCommand FireSelectedCommand { get; }
    public RelayCommand AddActionCommand { get; }
    public RelayCommand<ActionRow> RemoveActionCommand { get; }
    public RelayCommand<ActionRow> MoveActionUpCommand { get; }
    public RelayCommand<ActionRow> MoveActionDownCommand { get; }

    /// <summary>A show was loaded (or the app booted): both lists exist; start on the caller's stack.</summary>
    public void OnShowLoaded()
    {
        CueStacks.Caller(_s.State);
        CueStacks.Clicker(_s.State);
        _selectedStack = null;
        Raise(nameof(Stacks));
        SelectedStack = CueStacks.Caller(_s.State);
    }

    /// <summary>Adds a cue after the selected one (numbered to fit) and selects it.</summary>
    public RunCueConfig AddCue()
    {
        var stack = SelectedStack ?? CueStacks.Caller(_s.State);
        var index = SelectedCue is null ? stack.Cues.Count : stack.Cues.IndexOf(SelectedCue) + 1;
        if (index < 0) index = stack.Cues.Count;
        var previous = index > 0 ? stack.Cues[index - 1].Number : null;
        var next = index < stack.Cues.Count ? stack.Cues[index].Number : null;
        var cue = new RunCueConfig { Number = CueNumber.Between(previous, next) };
        stack.Cues.Insert(index, cue);
        SelectedCue = cue;
        Refresh();
        return cue;
    }

    public void RemoveCue(RunCueConfig cue)
    {
        var stack = SelectedStack;
        if (stack is null) return;
        var index = stack.Cues.IndexOf(cue);
        if (index < 0) return;
        stack.Cues.RemoveAt(index);
        if (ReferenceEquals(SelectedCue, cue))
        {
            SelectedCue = stack.Cues.Count == 0 ? null : stack.Cues[Math.Min(index, stack.Cues.Count - 1)];
        }
        Refresh();
    }

    public void MoveCue(int delta)
    {
        var stack = SelectedStack;
        if (stack is null || SelectedCue is null) return;
        var index = stack.Cues.IndexOf(SelectedCue);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= stack.Cues.Count) return;
        stack.Cues.Move(index, target);
        Refresh();
    }

    private void MoveAction(ActionRow? row, int delta)
    {
        if (row is null || SelectedCue is null) return;
        var actions = SelectedCue.Actions;
        var index = actions.IndexOf(row.Action);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= actions.Count) return;
        actions.Move(index, target);
        OnCueEdited();
    }

    /// <summary>The pickers for one target kind, from the live show.</summary>
    public IEnumerable<PickItem> ChoicesFor(TargetKind kind)
    {
        var state = _s.State;
        switch (kind)
        {
            case TargetKind.Look:
                return state.LooksAndCues.Looks.Select(l => new PickItem(l.Id, l.Name));
            case TargetKind.Stinger:
                // Everything, in library order and kind-labelled: a cue written before the split still finds its item.
                return state.Stingers.Items.Select(s => new PickItem(s.Id, $"{s.KindLabel} · {s.DisplayName}"));
            case TargetKind.Part:
            {
                var names = new List<string>();
                foreach (var section in state.Pattern.Media.Playlist.Sections) names.Add(section.Name);
                foreach (var a in state.Independent)
                {
                    foreach (var section in a.Pattern.Media.Playlist.Sections) names.Add(section.Name);
                }
                return names.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => new PickItem(n, n));
            }
            case TargetKind.Screen:
            {
                var known = _s.Screens.All;
                return Rig.OrderedLivePlacements(state, known)
                    .Select((x, i) => new PickItem(x.Placement.ScreenId, $"{i + 1} · {Rig.LabelFor(x.Placement, x.Info)}"));
            }
            case TargetKind.Canvas:
            {
                var groups = Rig.CanvasGroups(state, _s.Screens.All);
                var items = new List<PickItem>();
                for (var i = 0; i < groups.Count; i++)
                {
                    var key = CanvasNameConfig.KeyFor(groups[i].Select(m => m.ScreenId));
                    var letter = ((char)('A' + i)).ToString();
                    var name = state.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == key)?.Name;
                    items.Add(new PickItem(key, $"{letter} · {(string.IsNullOrWhiteSpace(name) ? $"Canvas {letter}" : name)}"));
                }
                return items;
            }
            case TargetKind.Stack:
                return state.Stacks.Select(s => new PickItem(s.Id, s.Name));
            case TargetKind.Music:
                return state.Spotify.Items.Select(m => new PickItem(m.Id, m.DisplayName));
            case TargetKind.LowerThird:
                return state.LowerThirds.Designs.Select(d => new PickItem(d.Id, d.Name));
            default:
                return Array.Empty<PickItem>();
        }
    }

    /// <summary>An action or field changed: re-read the summary and the checks soon (debounced).</summary>
    public void OnCueEdited()
    {
        RebuildActionRows();
        ScheduleRevalidate();
    }

    private void ScheduleRevalidate()
    {
        _revalidate.Stop();
        _revalidate.Start();
    }

    /// <summary>Rebuilds the rows from the list and runs the validator now.</summary>
    public void Refresh()
    {
        _revalidate.Stop();
        var stack = SelectedStack;
        foreach (var row in Rows) row.Detach();
        Rows.Clear();
        if (stack is null)
        {
            _report = null;
            ValidationSummary = "";
            StackNotesText = "";
            return;
        }
        _report = CueValidator.Validate(_s.State, stack, _s.ValidationContext);
        foreach (var cue in stack.Cues)
        {
            var row = new CueRow(cue)
            {
                Summary = CueSummary.Describe(_s.State, cue),
                Problem = _report.ReasonFor(cue.Id) ?? "",
                Warning = _report.Warnings.TryGetValue(cue.Id, out var w) ? w : "",
            };
            Rows.Add(row);
        }
        var total = stack.Cues.Count;
        var broken = _report.BrokenCount;
        ValidationSummary = total == 0
            ? "No cues yet — add one."
            : broken == 0
                ? $"All {total} cue{(total == 1 ? "" : "s")} can run."
                : $"{broken} of {total} cue{(total == 1 ? "" : "s")} broken — the rest still run.";
        StackNotesText = string.Join("  ", _report.StackNotes);
        RefreshMarkers();
        foreach (var row in ActionRows) row.RefreshChoices();
    }

    private void RefreshMarkers()
    {
        var stack = SelectedStack;
        var current = stack is null ? -1 : _s.Cues.For(stack).CurrentIndex;
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].IsSelected = ReferenceEquals(Rows[i].Cue, SelectedCue);
            Rows[i].IsCurrent = i == current;
        }
    }

    private void RebuildActionRows()
    {
        ActionRows.Clear();
        if (SelectedCue is null) return;
        foreach (var a in SelectedCue.Actions) ActionRows.Add(new ActionRow(this, a));
    }

    private void WatchActions(ObservableCollection<CueActionConfig>? actions)
    {
        if (_watchedActions is not null) _watchedActions.CollectionChanged -= OnActionsChanged;
        _watchedActions = actions;
        if (_watchedActions is not null) _watchedActions.CollectionChanged += OnActionsChanged;
    }

    private void OnActionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRevalidate();
}
