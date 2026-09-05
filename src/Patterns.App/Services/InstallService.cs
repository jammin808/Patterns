using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// A permanent install's clock at work: every second the pure <see cref="InstallRuntime"/> says
/// what the schedule wants — a programme's look, the idle content, an advert or an announcement
/// starting or ending — and this service does it through the one action layer, with the schedule
/// as the origin, so the journal reads "ApplyLook from schedule" like everything else. Adverts and
/// announcements fired by hand (ANNOUNCE, ADVERT, a cue, the page) go the same way. An announcement
/// is its words on the message overlay, a VOG from the library and, when it has one, a look of its
/// own; an advert is its look — on named screens only when it says so, the others locked for its
/// length and freed after — and the programme comes back underneath by itself.
/// </summary>
public sealed class InstallService
{
    private readonly AppServices _s;
    private readonly InstallRuntime _rt = new();
    private bool _wasEnabled;
    private bool _idleBlackout;
    private string _lookBefore = "";
    private bool _messageWasOn;
    private string _messageTextBefore = "";
    private readonly List<string> _lockedByMe = new();

    public InstallService(AppServices services) => _s = services;

    /// <summary>Tests: the clock the tick reads when it is not given one.</summary>
    public Func<DateTime>? Clock { get; set; }

    public InstallRuntime Runtime => _rt;

    /// <summary>"Schedule on · programme Daytime until 17:00 · next: advert Lunch offer at 12:30" — the page's line and STATE's.</summary>
    public string Status { get; private set; } = "Schedule off.";

    /// <summary>The last thing the clock did, in words, with its time.</summary>
    public string LastEvent { get; private set; } = "";

    private DateTime Now => Clock?.Invoke() ?? DateTime.Now;

    /// <summary>The 1 s poll.</summary>
    public void Tick() => Tick(Now);

    public void Tick(DateTime now)
    {
        var cfg = _s.State.Install;
        if (_wasEnabled && !cfg.Enabled)
        {
            // Switched off: an override in progress ends, the picture otherwise stays where it is.
            Perform(_rt.Reset(), now, ActionOrigin.Schedule);
        }
        _wasEnabled = cfg.Enabled;
        Perform(_rt.Tick(cfg, now, Busy), now, ActionOrigin.Schedule);
        Status = Describe(cfg, now);
    }

    /// <summary>The desk owns the screens: the caller's stack is armed, or a stinger holds them. The clock waits.</summary>
    private bool Busy => _s.CueStack.SuspendsAutomation || _s.Stingers.OwnsScreens;

    /// <summary>ANNOUNCE — a named announcement (the target, or a name in the value), else the value as words for the show's announcement seconds.</summary>
    public ActionResult Announce(string target, string value, ActionOrigin origin)
    {
        var cfg = _s.State.Install;
        ScheduleSlotConfig? slot;
        if (target.Trim().Length > 0)
        {
            slot = Schedule.Find(cfg, target);
            if (slot is null) return ActionResult.Refused($"No announcement '{target.Trim()}' on the Install page — or leave it blank and give the words.");
        }
        else
        {
            slot = Schedule.Find(cfg, value, SlotKind.Announcement) ?? Schedule.Find(cfg, value, SlotKind.Advert);
            if (slot is null)
            {
                if (value.Trim().Length == 0) return ActionResult.Refused("Nothing to announce — name an announcement of the Install page, or give the words.");
                slot = InstallRuntime.AdHoc(value.Trim(), cfg.AnnouncementSeconds);
            }
        }
        if (slot.Kind == SlotKind.Programme) return ActionResult.Refused($"'{slot.Name}' is a programme — ANNOUNCE takes an announcement (or words), ADVERT an advert.");
        if (Busy) return ActionResult.Refused(_s.CueStack.SuspendsAutomation ? "The caller's stack is armed — announcements wait for the desk." : "A stinger holds the screens — put it back first.");
        var now = Now;
        Perform(_rt.Fire(slot, now), now, origin);
        Status = Describe(cfg, now);
        return ActionResult.Done(slot.IsAdHoc
            ? $"Announcing '{slot.Text}' for {slot.DurationSeconds} s."
            : $"{Schedule.KindWord(slot.Kind)} '{slot.Name}' on for {slot.DurationSeconds} s.");
    }

    /// <summary>ADVERT — an advert of the Install page now, for its seconds, on its screens.</summary>
    public ActionResult PlayAdvert(string target, ActionOrigin origin)
    {
        var cfg = _s.State.Install;
        if (target.Trim().Length == 0) return ActionResult.Refused("Which advert? Name one from the Install page.");
        var slot = Schedule.Find(cfg, target, SlotKind.Advert) ?? Schedule.Find(cfg, target);
        if (slot is null) return ActionResult.Refused($"No advert '{target.Trim()}' on the Install page.");
        if (slot.Kind != SlotKind.Advert) return ActionResult.Refused($"'{slot.Name}' is {(slot.Kind == SlotKind.Programme ? "a programme" : "an announcement")}, not an advert.");
        if (slot.Look.Length == 0) return ActionResult.Refused($"Advert '{slot.Name}' has no look.");
        if (Busy) return ActionResult.Refused(_s.CueStack.SuspendsAutomation ? "The caller's stack is armed — adverts wait for the desk." : "A stinger holds the screens — put it back first.");
        var now = Now;
        Perform(_rt.Fire(slot, now), now, origin);
        Status = Describe(cfg, now);
        return ActionResult.Done($"Advert '{slot.Name}' on for {slot.DurationSeconds} s.");
    }

    /// <summary>ANNOUNCE OFF / ADVERT OFF — the override on ends now; the kind must match, so a key that says ANNOUNCE OFF never skips an advert.</summary>
    public ActionResult EndOverride(SlotKind kind, ActionOrigin origin)
    {
        if (_rt.Override is not { } on) return ActionResult.Done(kind == SlotKind.Advert ? "No advert is on." : "No announcement is on.");
        if (on.Kind != kind)
        {
            return ActionResult.Refused(on.Kind == SlotKind.Advert
                ? $"An advert is on ('{on.Name}') — ADVERT OFF ends it."
                : $"An announcement is on ('{on.Name}') — ANNOUNCE OFF ends it.");
        }
        var now = Now;
        Perform(_rt.EndOverride(), now, origin);
        Status = Describe(_s.State.Install, now);
        return ActionResult.Done($"{Schedule.KindWord(on.Kind)} '{on.Name}' ended.");
    }

    /// <summary>SCHEDULE ON / OFF.</summary>
    public ActionResult SetSchedule(bool on, ActionOrigin origin)
    {
        var cfg = _s.State.Install;
        if (cfg.Enabled == on) return ActionResult.Done(on ? "The schedule is already on." : "The schedule is already off.");
        cfg.Enabled = on;
        Tick(Now);
        if (!on) return ActionResult.Done("Schedule off — what is on stays; announcements and adverts by hand still work.");
        var problems = Schedule.Problems(cfg, _s.State);
        return problems.Count == 0
            ? ActionResult.Done($"Schedule on. {Status}")
            : ActionResult.Requested($"Schedule on, with {problems.Count} row{(problems.Count == 1 ? "" : "s")} to fix: {problems[0]}");
    }

    /// <summary>The block STATE carries: the switch, the site, the programme on, the override on and until when, the next change, every row's state.</summary>
    public object StateRow(DateTime now)
    {
        var cfg = _s.State.Install;
        var programme = _rt.ProgrammeId.Length > 0 ? cfg.Slots.FirstOrDefault(s => s.Id == _rt.ProgrammeId)?.Name ?? "" : "";
        var next = cfg.Enabled ? Schedule.NextChange(cfg, now) : null;
        return new
        {
            on = cfg.Enabled,
            site = cfg.SiteName,
            programme,
            idle = _rt.Idle,
            over = _rt.Override?.IsAdHoc == true ? _rt.Override.Text : _rt.Override?.Name ?? "",
            overKind = _rt.Override is { } o ? Schedule.KindWord(o.Kind) : "",
            overUntil = _rt.Override is null ? "" : _rt.OverrideEndsAt.ToString("HH:mm:ss"),
            next = next is { } n ? $"{n.At:HH:mm} {n.What}" : "",
            status = Status,
            slots = cfg.Slots.Select((s, i) => new { n = i + 1, name = s.Name, kind = Schedule.KindWord(s.Kind), enabled = s.Enabled, status = s.Status }).ToArray(),
            problems = Schedule.Problems(cfg, _s.State).Count,
            update = _s.Updates.StatusRow(),
            management = _s.Management.Status,
        };
    }

    private string Describe(InstallConfig cfg, DateTime now)
    {
        if (!cfg.Enabled)
        {
            var over = _rt.Override;
            return over is null
                ? (cfg.Slots.Count == 0 ? "Schedule off — add a programme, an advert or an announcement below." : $"Schedule off — {cfg.Slots.Count} row{(cfg.Slots.Count == 1 ? "" : "s")} waiting.")
                : $"Schedule off · {Schedule.KindWord(over.Kind)} '{(over.IsAdHoc ? over.Text : over.Name)}' on until {_rt.OverrideEndsAt:HH:mm:ss}.";
        }
        var parts = new List<string> { "Schedule on" };
        if (_rt.Override is { } on) parts.Add($"{Schedule.KindWord(on.Kind)} '{(on.IsAdHoc ? on.Text : on.Name)}' until {_rt.OverrideEndsAt:HH:mm:ss}");
        if (_rt.ProgrammeId.Length > 0 && cfg.Slots.FirstOrDefault(s => s.Id == _rt.ProgrammeId) is { } programme)
        {
            var until = Schedule.WindowAt(programme, now)?.End;
            parts.Add(until is { } u ? $"programme '{programme.Name}' until {u:HH:mm}" : $"programme '{programme.Name}'");
        }
        else if (_rt.Idle)
        {
            parts.Add(cfg.IdleLook.Length > 0 ? $"idle — look '{cfg.IdleLook}'" : "idle — black");
        }
        if (Busy) parts.Add("waiting for the desk");
        if (Schedule.NextChange(cfg, now) is { } next) parts.Add($"next: {next.What} at {next.At:HH:mm}{(next.At.Date != now.Date ? " tomorrow" : "")}");
        return string.Join(" · ", parts) + ".";
    }

    private void Perform(IReadOnlyList<InstallStep> steps, DateTime now, ActionOrigin origin)
    {
        if (steps.Count == 0) return;
        var programmeFollows = steps.Any(s => s.Kind is InstallStepKind.Programme or InstallStepKind.Idle);
        foreach (var step in steps)
        {
            try
            {
                switch (step.Kind)
                {
                    case InstallStepKind.Programme:
                        ApplyProgramme(step.Slot!, now);
                        break;
                    case InstallStepKind.Idle:
                        ApplyIdle(now);
                        break;
                    case InstallStepKind.OverrideStart:
                        StartOverride(step.Slot!, now, origin);
                        break;
                    case InstallStepKind.OverrideEnd:
                        EndOverrideNow(step.Slot!, step.Note, now, origin, restoreLook: !programmeFollows);
                        break;
                    case InstallStepKind.Note:
                        _s.Journal.Record(ActionOrigin.Schedule.Label, "Install", step.Slot?.Name ?? "", ActionStatus.Refused.ToString(), step.Note);
                        if (step.Slot is not null) step.Slot.Status = step.Note;
                        LastEvent = $"{now:HH:mm} {step.Note}";
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Install step {step.Kind} failed.", ex);
            }
        }
    }

    private void ApplyProgramme(ScheduleSlotConfig slot, DateTime now)
    {
        var look = LookService.Find(_s.State, slot.Look);
        if (look is null)
        {
            slot.Status = $"look '{slot.Look}' not found";
            _s.Journal.Record(ActionOrigin.Schedule.Label, ShowActionKind.ApplyLook.ToString(), slot.Look, ActionStatus.Refused.ToString(), $"Programme '{slot.Name}': look '{slot.Look}' not found.");
            LastEvent = $"{now:HH:mm} programme '{slot.Name}': look '{slot.Look}' not found";
            return;
        }
        var result = _s.Actions.Execute(new ShowAction(ShowActionKind.ApplyLook, look.Id), ActionOrigin.Schedule);
        LiftIdleBlack();
        slot.Status = result.Ok ? $"on air since {now:HH:mm}" : result.Message;
        LastEvent = $"{now:HH:mm} programme '{slot.Name}': {result.Message}";
    }

    private void ApplyIdle(DateTime now)
    {
        var cfg = _s.State.Install;
        if (cfg.IdleLook.Length > 0 && LookService.Find(_s.State, cfg.IdleLook) is { } idle)
        {
            var result = _s.Actions.Execute(new ShowAction(ShowActionKind.ApplyLook, idle.Id), ActionOrigin.Schedule);
            LiftIdleBlack();
            LastEvent = $"{now:HH:mm} idle: look '{idle.Name}' — {result.Message}";
            return;
        }
        if (!_s.AirState.Blackout)
        {
            _s.Actions.Execute(ShowActionKind.BlackoutOn, ActionOrigin.Schedule);
            _idleBlackout = true;
        }
        LastEvent = $"{now:HH:mm} idle: black (no programme scheduled)";
    }

    /// <summary>The black the idle state put up comes down after the programme's look has landed — the audience never sees the old picture.</summary>
    private void LiftIdleBlack()
    {
        if (!_idleBlackout) return;
        _idleBlackout = false;
        if (_s.AirState.Blackout) _s.Actions.Execute(ShowActionKind.BlackoutOff, ActionOrigin.Schedule);
    }

    private void StartOverride(ScheduleSlotConfig slot, DateTime now, ActionOrigin origin)
    {
        _lookBefore = _s.AirLookId;
        var message = _s.AirState.Overlays.Message;
        _messageWasOn = message.Enabled;
        _messageTextBefore = message.Text;
        _lockedByMe.Clear();
        var notes = new List<string>();
        if (slot.Look.Length > 0)
        {
            if (slot.Kind == SlotKind.Advert && slot.Screens.Trim().Length > 0) LockOthers(slot.Screens, origin);
            var look = LookService.Find(_s.State, slot.Look);
            if (look is null)
            {
                notes.Add($"look '{slot.Look}' not found");
                _s.Journal.Record(origin.Label, ShowActionKind.ApplyLook.ToString(), slot.Look, ActionStatus.Refused.ToString(), $"{Schedule.KindWord(slot.Kind)} '{slot.Name}': look '{slot.Look}' not found.");
            }
            else
            {
                var r = _s.Actions.Execute(new ShowAction(ShowActionKind.ApplyLook, look.Id), origin);
                LiftIdleBlack();
                if (!r.Ok) notes.Add(r.Message);
            }
        }
        if (slot.Text.Length > 0)
        {
            var r = _s.Actions.Execute(new ShowAction(ShowActionKind.MessageOn, "", slot.Text), origin);
            if (!r.Ok) notes.Add(r.Message);
        }
        if (slot.Sound.Length > 0)
        {
            var r = _s.Actions.Execute(new ShowAction(ShowActionKind.StingerFire, slot.Sound, "vog"), origin);
            if (!r.Ok) notes.Add(r.Message);
        }
        slot.Status = notes.Count == 0 ? $"on until {_rt.OverrideEndsAt:HH:mm:ss}" : $"on until {_rt.OverrideEndsAt:HH:mm:ss} — {string.Join("; ", notes)}";
        LastEvent = $"{now:HH:mm} {Schedule.KindWord(slot.Kind)} '{(slot.IsAdHoc ? slot.Text : slot.Name)}' on";
    }

    private void EndOverrideNow(ScheduleSlotConfig slot, string why, DateTime now, ActionOrigin origin, bool restoreLook)
    {
        if (slot.Text.Length > 0)
        {
            _s.Actions.Execute(_messageWasOn
                ? new ShowAction(ShowActionKind.MessageOn, "", _messageTextBefore)
                : new ShowAction(ShowActionKind.MessageOff), origin);
        }
        foreach (var id in _lockedByMe)
        {
            _s.Actions.Execute(new ShowAction(ShowActionKind.ScreenUnlock, id), origin);
        }
        _lockedByMe.Clear();
        if (restoreLook && slot.Look.Length > 0 && _lookBefore.Length > 0 && LookService.Find(_s.State, _lookBefore) is { } back)
        {
            _s.Actions.Execute(new ShowAction(ShowActionKind.ApplyLook, back.Id), origin);
        }
        slot.Status = $"{why} at {now:HH:mm}";
        LastEvent = $"{now:HH:mm} {Schedule.KindWord(slot.Kind)} '{(slot.IsAdHoc ? slot.Text : slot.Name)}' off — {why}";
    }

    /// <summary>An advert's placement: every live screen the row does not name keeps its picture — locked for the advert, freed after it.</summary>
    private void LockOthers(string screens, ActionOrigin origin)
    {
        var (numbers, words) = Schedule.ParseScreens(screens);
        var ordered = Rig.OrderedLivePlacements(_s.State, _s.Screens.All);
        for (var i = 0; i < ordered.Count; i++)
        {
            var placement = ordered[i].Placement;
            var label = Rig.LabelFor(placement, ordered[i].Info);
            var named = numbers.Contains(i + 1)
                        || words.Any(w => string.Equals(w, label, StringComparison.OrdinalIgnoreCase) || string.Equals(w, placement.CustomLabel, StringComparison.OrdinalIgnoreCase) || label.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (named || !placement.FollowsCues) continue;
            var r = _s.Actions.Execute(new ShowAction(ShowActionKind.ScreenLock, placement.ScreenId), origin);
            if (r.Ok) _lockedByMe.Add(placement.ScreenId);
        }
    }
}
