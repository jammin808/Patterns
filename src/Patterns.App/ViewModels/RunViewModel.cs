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
    private string _plan = "";

    public RunRow(RunCueConfig cue, string summary)
    {
        Cue = cue;
        Summary = summary;
    }

    /// <summary>"10:35 → 10:42 · 12:00": planned start, expected start, planned length — whichever the cue has.</summary>
    public string Plan { get => _plan; set { if (Set(ref _plan, value)) Raise(nameof(HasPlan)); } }

    public bool HasPlan => _plan.Length > 0;

    public bool HasFollow => Cue.FollowSeconds is not null;

    public string FollowTag => Cue.FollowSeconds is { } f ? (f == 0 ? "AUTO" : $"AUTO {CueTiming.FormatDuration(f)}") : "";

    public string MarkTag => Cue.Mark switch
    {
        CueMark.Break => "BREAK",
        CueMark.Lunch => "LUNCH",
        CueMark.End => "END",
        _ => "",
    };

    public bool HasMark => Cue.Mark != CueMark.None;

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
        ShiftLaterCommand = new RelayCommand(() => _vm.StatusMessage = _s.CueStack.ShiftPlan(TimeSpan.FromMinutes(1), ActionOrigin.Desk));
        ShiftEarlierCommand = new RelayCommand(() => _vm.StatusMessage = _s.CueStack.ShiftPlan(TimeSpan.FromMinutes(-1), ActionOrigin.Desk));
        ResumeNowCommand = new RelayCommand(() => _vm.StatusMessage = _s.CueStack.ResumeNow(ActionOrigin.Desk));
        CatchUpCommand = new RelayCommand(() => _vm.StatusMessage = _s.CueStack.CatchUp(ActionOrigin.Desk));
        CancelFollowCommand = new RelayCommand(() =>
        {
            _s.CueStack.CancelFollow();
            _vm.StatusMessage = "Auto-follow cancelled — the next cue waits for GO.";
            RefreshTiming();
        });

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

    /// <summary>A stinger landed and is holding the screens for the caller's take.</summary>
    public bool IsStingHolding => _s.Stingers.Holding;

    public string StingHoldText => _s.Stingers.Holding ? $"STING HOLD: {_s.Stingers.HoldName}" : "";

    /// <summary>The chip: break music is on and asked to play. Sound, so never the label — the label is the picture.</summary>
    public bool IsMusicPlaying => _s.State.Spotify.Enabled && _s.State.Spotify.Playing;

    /// <summary>The chip: the live duck is on — the room is speaking and everything but a VOG has made way.</summary>
    public bool IsDucked => _s.State.Stingers.DuckActive;

    public string DuckTip => $"Live duck: music, stinger sounds and clip audio held at {_s.State.Stingers.DuckToPct:0}% for an announcement — press D or DUCK again to lift it; STOP ALL leaves it";

    public string MusicTip => _s.Spotify.NowPlaying.Length > 0
        ? $"Break music: {_s.Spotify.NowPlaying}{(_s.Spotify.DeviceLabel.Length > 0 ? " — " + _s.Spotify.DeviceLabel : "")} — STOP ALL pauses it"
        : "Break music playing — STOP ALL pauses it";
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

    /// <summary>The cue after standby — the one GO lands on next: "04  Sponsor loop" or "" at the end of the list.</summary>
    public string NextText => Rows.FirstOrDefault(r => r.IsNext) is { } next ? $"{next.Number}  {next.Name}" : "";
    public bool HasNext => NextText.Length > 0;
    public bool HasStandbyPlan => StandbyPlanText.Length > 0;
    public bool HasValidationSummary => ValidationSummary.Length > 0;
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

    // ---- the day's clock -------------------------------------------------------------

    private TimingReport _timing = TimingReport.Empty;

    /// <summary>Where the day stands, refreshed every second and on every GO.</summary>
    public TimingReport Timing => _timing;

    /// <summary>"ON TIME", "3 MIN LATE", "2 MIN EARLY" — empty until something is planned.</summary>
    public string OffsetText => _timing.OffsetText;

    public bool IsLate => _timing.IsLate;

    public bool IsOnPlan => _timing.OffsetText.Length > 0 && !_timing.IsLate;

    /// <summary>"Next break ≈ 10:42 (planned 10:35, +7 min) · Lunch … · End …".</summary>
    public string ScheduleSummary => _timing.Summary;

    /// <summary>The list carries a running order: planned starts or lengths, or a break, lunch or end.</summary>
    public bool HasPlan => _s.CueStack.Stack.Cues.Any(c => c.PlannedStart.Length > 0 || c.PlannedSeconds is not null || c.Mark != CueMark.None);

    /// <summary>The standby cue's plan: "planned 10:35 · expected 10:42 (+7 min) · 12:00".</summary>
    public string StandbyPlanText
    {
        get
        {
            if (_s.CueStack.StandbyCue is not { } cue) return "";
            var e = _timing.For(cue.Id);
            var parts = new List<string>();
            if (e?.PlannedAt is { } p) parts.Add($"planned {CueTiming.FormatClock(p)}");
            if (e is { Past: false })
            {
                var expected = $"expected {(e.Uncertain ? "≥ " : "")}{CueTiming.FormatClock(e.EstimatedAt)}";
                if (e.Delta is { } d && Math.Abs(d.TotalSeconds) >= 30) expected += $" ({CueTiming.FormatDelta(d)})";
                parts.Add(expected);
            }
            if (cue.PlannedSeconds is { } len) parts.Add(CueTiming.FormatDuration(len));
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>"AUTO 01.030 in 0:07" while the next cue is going to fire by itself.</summary>
    public string FollowText => _s.CueStack.FollowText();

    public bool HasFollow => FollowText.Length > 0;

    private void RefreshTiming()
    {
        _timing = _s.CueStack.Timing();
        foreach (var row in Rows)
        {
            var e = _timing.For(row.Cue.Id);
            var parts = new List<string>();
            if (e?.PlannedAt is { } p) parts.Add(CueTiming.FormatClock(p));
            if (e is { Past: false } && (e.PlannedAt is null || Math.Abs(e.Delta!.Value.TotalSeconds) >= 30))
            {
                parts.Add($"{(e.PlannedAt is null ? "≈ " : "→ ")}{(e.Uncertain ? "≥ " : "")}{CueTiming.FormatClock(e.EstimatedAt)}");
            }
            if (row.Cue.PlannedSeconds is { } len) parts.Add(CueTiming.FormatDuration(len));
            row.Plan = string.Join("  ", parts);
        }
        Raise(nameof(Timing));
        Raise(nameof(OffsetText));
        Raise(nameof(IsLate));
        Raise(nameof(IsOnPlan));
        Raise(nameof(ScheduleSummary));
        Raise(nameof(HasPlan));
        Raise(nameof(StandbyPlanText));
        Raise(nameof(FollowText));
        Raise(nameof(HasFollow));
    }

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
    public RelayCommand ShiftLaterCommand { get; }
    public RelayCommand ShiftEarlierCommand { get; }
    public RelayCommand ResumeNowCommand { get; }
    public RelayCommand CatchUpCommand { get; }
    public RelayCommand CancelFollowCommand { get; }

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
        _vm.StatusMessage = "Press Esc again within a second to STOP ALL (audio, break music, VOGs, stingers, tone — never outputs or blackout).";
    }

    /// <summary>Each second: the running clock, confirm expiry and asynchronous settling.</summary>
    public void Tick()
    {
        _s.CueStack.Poll();
        RefreshTiming();
        Raise(nameof(RunningText));
        Raise(nameof(RunningOverPlanned));
        Raise(nameof(NextAutoText));
        Raise(nameof(IsMusicPlaying));
        Raise(nameof(MusicTip));     // follows the track
        Raise(nameof(IsStingHolding));
        Raise(nameof(StingHoldText));
        Raise(nameof(IsDucked));
        Raise(nameof(DuckTip));
    }

    private void OnRuntimeChanged()
    {
        RefreshFlags();
        RefreshTiming();
        RaiseLive();
    }

    private void RaiseLive()
    {
        Raise(nameof(IsArmed));
        Raise(nameof(IsHold));
        Raise(nameof(IsBlackout));
        Raise(nameof(IsMusicPlaying));
        Raise(nameof(MusicTip));
        Raise(nameof(IsStingHolding));
        Raise(nameof(StingHoldText));
        Raise(nameof(IsDucked));
        Raise(nameof(DuckTip));
        Raise(nameof(ArmText));
        Raise(nameof(CanExit));
        Raise(nameof(GoText));
        Raise(nameof(IsConfirming));
        Raise(nameof(StandbyText));
        Raise(nameof(NextText));
        Raise(nameof(HasNext));
        Raise(nameof(HasStandbyPlan));
        Raise(nameof(HasValidationSummary));
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
        RefreshTiming();
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
