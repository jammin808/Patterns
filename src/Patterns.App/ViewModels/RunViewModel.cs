using System.Collections.ObjectModel;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>One cue as the caller's list shows it: standby, last run, next, or the rest.</summary>
public sealed class RunRow : Observable
{
    private bool _isStandby;
    private bool _isLast;
    private bool _isNext;
    private string _problem = "";

    public RunRow(RunCueConfig cue, string summary)
    {
        Cue = cue;
        Summary = summary;
    }

    public RunCueConfig Cue { get; }
    public string Number => Cue.Number;
    public string Name => Cue.Name;
    public string Summary { get; }
    public string Notes => Cue.Notes;
    public bool HasNotes => Cue.Notes.Length > 0;
    public bool Enabled => Cue.Enabled;
    public bool RequireConfirm => Cue.RequireConfirm;
    public bool NotReady => !Cue.Ready;

    public bool IsStandby { get => _isStandby; set { if (Set(ref _isStandby, value)) Raise(nameof(Tag)); } }
    public bool IsLast { get => _isLast; set { if (Set(ref _isLast, value)) Raise(nameof(Tag)); } }
    public bool IsNext { get => _isNext; set { if (Set(ref _isNext, value)) Raise(nameof(Tag)); } }
    public string Problem { get => _problem; set { if (Set(ref _problem, value)) Raise(nameof(IsBroken)); } }
    public bool IsBroken => _problem.Length > 0;

    public string Tag => IsStandby ? "STANDBY" : IsLast ? "LAST" : IsNext ? "NEXT" : "";
}

/// <summary>
/// The Run surface: what a caller reads from a metre — the LIVE strip, the list with standby /
/// last / next, the transport row — over the cue stack service. Everything a key or a button
/// does here goes through that service and the action layer.
/// </summary>
public sealed class RunViewModel : Observable
{
    private readonly AppServices _s;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _refresh;
    private CueValidationReport? _report;
    private DateTime? _lastEscUtc;
    private string _banner = "";

    public RunViewModel(AppServices services, MainViewModel vm)
    {
        _s = services;
        _vm = vm;
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            Refresh();
        };

        ArmCommand = new RelayCommand(() =>
        {
            _s.CueStack.SetArmed(!_s.CueStack.Armed, ActionOrigin.Desk);
            _vm.StatusMessage = _s.CueStack.Armed
                ? "ARMED — GO fires the standby cue. The schedule, part start times and plain F-keys wait."
                : "Disarmed — GO is off; the schedule and F-keys are live again.";
        });
        GoCommand = new RelayCommand(() => Go(ActionOrigin.Desk));
        HoldCommand = new RelayCommand(() => _s.CueStack.SetHold(!_s.CueStack.Runtime.Hold, ActionOrigin.Desk));
        StopAllCommand = new RelayCommand(() => _s.Actions.Execute(ShowActionKind.StopAll, ActionOrigin.Desk));
        StandbyUpCommand = new RelayCommand(() => _s.CueStack.StandbyMove(-1));
        StandbyDownCommand = new RelayCommand(() => _s.CueStack.StandbyMove(+1));
        SelectRowCommand = new RelayCommand<RunRow>(row => { if (row is not null) _s.CueStack.Standby(row.Cue.Id); });
        DismissBannerCommand = new RelayCommand(() => Banner = "");

        _s.CueStack.Changed += OnRuntimeChanged;
        _s.AirLabelChanged += () => Raise(nameof(LiveLabel));
        _s.SnapshotPublished += ScheduleRefresh; // stack edits, blackout, a show load
        Refresh();
    }

    public ObservableCollection<RunRow> Rows { get; } = new();

    public ObservableCollection<CueExecutionRecord> History => _s.CueStack.History;

    // ---- the LIVE strip ---------------------------------------------------------

    public string ShowName => _s.State.Name.Length > 0 ? _s.State.Name : "Untitled show";
    public string LiveLabel => _s.AirLabel;
    public bool IsArmed => _s.CueStack.Armed;
    public bool IsHold => _s.CueStack.Runtime.Hold;
    public bool IsBlackout => _s.State.Blackout;
    public string NextAutoText => _s.CueStack.NextAutoText(DateTime.Now);

    /// <summary>"Restored after restart — last GO 03.020 at 19:41:58 — press ARM to continue".</summary>
    public string Banner
    {
        get => _banner;
        set
        {
            if (Set(ref _banner, value)) Raise(nameof(HasBanner));
        }
    }

    public bool HasBanner => _banner.Length > 0;

    // ---- the cards ----------------------------------------------------------------

    public string StandbyText => _s.CueStack.StandbyCue is { } cue ? $"{cue.Number}  {cue.Name}" : "No cue on standby";
    public string StandbyNotes => _s.CueStack.StandbyCue?.Notes ?? "";
    public string StandbyProblem => _s.CueStack.StandbyCue is { } cue && _report?.ReasonFor(cue.Id) is { } r ? $"BROKEN — {r}" : "";
    public bool StandbyIsBroken => StandbyProblem.Length > 0;
    public string StandbyReadyText => _s.CueStack.StandbyCue is { Ready: false, Actions.Count: > 0 } ? "NOT MARKED READY" : "";

    public string RunningText
    {
        get
        {
            var cue = _s.CueStack.LastCue;
            if (cue is null) return "";
            var since = _s.CueStack.Runtime.LastGoUtc is { } at ? DateTime.UtcNow - at : TimeSpan.Zero;
            var elapsed = $"{(int)since.TotalMinutes}:{since.Seconds:00}";
            var planned = cue.PlannedSeconds is { } p ? $" / {p / 60}:{p % 60:00}" : "";
            return $"{cue.Number}  {cue.Name}  ·  {elapsed}{planned}";
        }
    }

    public bool RunningOverPlanned
        => _s.CueStack.LastCue is { PlannedSeconds: { } p } && _s.CueStack.Runtime.LastGoUtc is { } at && (DateTime.UtcNow - at).TotalSeconds > p;

    public string GoText => _s.CueStack.ConfirmText ?? (_s.CueStack.StandbyCue is { } cue ? $"GO  {cue.Number}" : "GO");
    public bool IsConfirming => _s.CueStack.ConfirmText is not null;
    public string ArmText => IsArmed ? "DISARM" : "ARM";
    public bool CanExit => !IsArmed;
    public string ValidationSummary
    {
        get
        {
            var total = _s.CueStack.Stack.Cues.Count;
            var broken = _report?.BrokenCount ?? 0;
            return total == 0 ? "No cues — build the stack on the Cues page." : broken == 0 ? $"{total} cues, all can run" : $"{broken} of {total} broken";
        }
    }

    public RelayCommand ArmCommand { get; }
    public RelayCommand GoCommand { get; }
    public RelayCommand HoldCommand { get; }
    public RelayCommand StopAllCommand { get; }
    public RelayCommand StandbyUpCommand { get; }
    public RelayCommand StandbyDownCommand { get; }
    public RelayCommand<RunRow> SelectRowCommand { get; }
    public RelayCommand DismissBannerCommand { get; }

    /// <summary>GO from the desk or the Enter key: the standby the sender sees is the one right now.</summary>
    public ActionResult Go(ActionOrigin origin)
    {
        var seen = _s.CueStack.Runtime.StandbyCueId;
        var result = _s.Actions.Execute(ShowActionKind.CueGo, origin, seen ?? "");
        if (!result.Ok) Refresh(); // the caller reads the reason on the standby card at once
        return result;
    }

    /// <summary>Esc on the Run surface: cancels a pending confirm; a second Esc within a second is STOP ALL.</summary>
    public void EscapePressed()
    {
        var now = DateTime.UtcNow;
        if (_s.CueStack.Runtime.ConfirmPendingCueId is not null)
        {
            _s.CueStack.CancelConfirm();
            _vm.StatusMessage = "Confirm cancelled.";
            _lastEscUtc = null;
            return;
        }
        if (_lastEscUtc is { } last && now - last < TimeSpan.FromSeconds(1))
        {
            _lastEscUtc = null;
            _s.Actions.Execute(ShowActionKind.StopAll, ActionOrigin.Keyboard);
            return;
        }
        _lastEscUtc = now;
        _vm.StatusMessage = "Press Esc again within a second to STOP ALL (audio, stingers, tone — never outputs or blackout).";
    }

    /// <summary>Each second: the running clock, confirm expiry and asynchronous settling.</summary>
    public void Tick()
    {
        _s.CueStack.Poll();
        Raise(nameof(RunningText));
        Raise(nameof(RunningOverPlanned));
        Raise(nameof(NextAutoText));
    }

    private void OnRuntimeChanged()
    {
        RefreshFlags();
        RaiseLive();
    }

    private void RaiseLive()
    {
        Raise(nameof(IsArmed));
        Raise(nameof(IsHold));
        Raise(nameof(IsBlackout));
        Raise(nameof(ArmText));
        Raise(nameof(CanExit));
        Raise(nameof(GoText));
        Raise(nameof(IsConfirming));
        Raise(nameof(StandbyText));
        Raise(nameof(StandbyNotes));
        Raise(nameof(StandbyProblem));
        Raise(nameof(StandbyIsBroken));
        Raise(nameof(StandbyReadyText));
        Raise(nameof(RunningText));
        Raise(nameof(NextAutoText));
        Raise(nameof(LiveLabel));
        Raise(nameof(ShowName));
        Raise(nameof(ValidationSummary));
    }

    private void ScheduleRefresh()
    {
        _refresh.Stop();
        _refresh.Start();
    }

    /// <summary>Rebuilds the rows from the caller's stack and re-validates it.</summary>
    public void Refresh()
    {
        _refresh.Stop();
        var stack = _s.CueStack.Stack;
        _report = CueValidator.Validate(_s.State, stack, _s.ValidationContext);
        Rows.Clear();
        foreach (var cue in stack.Cues)
        {
            Rows.Add(new RunRow(cue, CueSummary.Describe(_s.State, cue)) { Problem = _report.ReasonFor(cue.Id) ?? "" });
        }
        RefreshFlags();
        RaiseLive();
    }

    private void RefreshFlags()
    {
        var rt = _s.CueStack.Runtime;
        var standbyIndex = -1;
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            row.IsStandby = row.Cue.Id == rt.StandbyCueId;
            row.IsLast = row.Cue.Id == rt.LastCueId;
            if (row.IsStandby) standbyIndex = i;
        }
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].IsNext = standbyIndex >= 0 && i > standbyIndex && Rows[i].Enabled && Rows.Skip(standbyIndex + 1).Take(i - standbyIndex - 1).All(r => !r.Enabled);
        }
    }
}
