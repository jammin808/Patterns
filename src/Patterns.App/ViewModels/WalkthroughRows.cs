using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>A role chip on the Help page: pick it and its scenarios are listed.</summary>
public sealed class WalkRoleChip : Observable
{
    private bool _isCurrent;

    public WalkRoleChip(MainViewModel vm, DeskRole role)
    {
        Role = role;
        Label = Walkthroughs.RoleLabel(role);
        Blurb = Walkthroughs.RoleBlurb(role);
        PickCommand = new RelayCommand(() => vm.WalkRole = role);
    }

    public DeskRole Role { get; }
    public string Label { get; }
    public string Blurb { get; }
    public RelayCommand PickCommand { get; }

    public bool IsCurrent { get => _isCurrent; private set => Set(ref _isCurrent, value); }

    public void Refresh(bool current) => IsCurrent = current;
}

/// <summary>One scenario of the picked role: press it and its steps open.</summary>
public sealed class WalkChoice : Observable
{
    private bool _isCurrent;

    public WalkChoice(MainViewModel vm, Walkthrough walkthrough)
    {
        Id = walkthrough.Id;
        Title = walkthrough.Title;
        Goal = walkthrough.Goal;
        Steps = walkthrough.Steps.Count;
        StartCommand = new RelayCommand(() => vm.StartWalkthrough(walkthrough.Id));
    }

    public string Id { get; }
    public string Title { get; }
    public string Goal { get; }
    public int Steps { get; }
    public RelayCommand StartCommand { get; }

    public bool IsCurrent { get => _isCurrent; private set => Set(ref _isCurrent, value); }

    public void Refresh(bool current) => IsCurrent = current;
}

/// <summary>One step of the open scenario as the Help page shows it: its words, GO to its page, and its tick.</summary>
public sealed class WalkStepRow : Observable
{
    private bool _isCurrent;
    private bool _isDone;
    private bool _isDoneByApp;

    public WalkStepRow(MainViewModel vm, int index, WalkStep step)
    {
        Index = index;
        Step = step;
        Number = (index + 1).ToString();
        PageWords = $"→ {step.Page} page";
        GoCommand = new RelayCommand(() => vm.WalkGo(index));
        DoneCommand = new RelayCommand(() => vm.WalkMark(index, !_isDone));
    }

    public int Index { get; }
    public WalkStep Step { get; }
    public string Number { get; }
    public string Title => Step.Title;
    public string Detail => Step.Detail;
    public string Page => Step.Page;
    public string PageWords { get; }
    public bool HasCheck => Step.Check.Length > 0;
    public RelayCommand GoCommand { get; }
    public RelayCommand DoneCommand { get; }

    public bool IsCurrent { get => _isCurrent; private set => Set(ref _isCurrent, value); }
    public bool IsDone { get => _isDone; private set => Set(ref _isDone, value); }

    /// <summary>The app saw this step done in the show itself (its check is met).</summary>
    public bool IsDoneByApp { get => _isDoneByApp; private set => Set(ref _isDoneByApp, value); }

    /// <summary>The tick button's face: what the app saw, what the hand ticked, or the ask.</summary>
    public string TickText => _isDoneByApp ? "✓ seen" : _isDone ? "✓ done" : "DONE";

    public string TickTip => _isDoneByApp
        ? "The show already has this — the app ticked it"
        : _isDone ? "Ticked by hand; press to untick" : "Tick this step off by hand";

    public void Refresh(bool current, bool done, bool byApp)
    {
        IsCurrent = current;
        IsDone = done;
        IsDoneByApp = byApp;
        Raise(nameof(TickText));
        Raise(nameof(TickTip));
    }
}
