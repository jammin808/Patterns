using System.Collections.ObjectModel;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The caller's stack at show time: standby, GO through the one gate, HOLD, confirm, history,
/// asynchronous settling, the sidecar's place and the automation it holds while armed. Runs on
/// the UI thread like every other model edit; the executor is synchronous with a re-entrancy
/// guard, so a GO that arrives while a cue executes is dropped and recorded, never queued. Built
/// on the kernel and a host (<see cref="ICueHost"/>): the desk's, which runs a cue's steps for
/// real; a node's, which rehearses them on paper.
/// </summary>
public sealed class CueStackService
{
    public const int HistoryRows = 50;
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(12);

    /// <summary>The clock every poll and GO without a time of its own reads — the desk's, or a test's, so a row stamped by a test's clock is never settled by the desk's own poll reading the wall's.</summary>
    public Func<DateTime> NowUtc { get; set; } = () => DateTime.UtcNow;

    private readonly ServiceKernel _kernel;
    private readonly ICueHost _host;

    public CueStackService(ServiceKernel kernel, ICueHost host)
    {
        _kernel = kernel;
        _host = host;
        NowUtc = () => kernel.Clock.UtcNow;    // the desk's frame: a caller's plan is read on the desk's clock, not its own
        _kernel.Cues.Changed += () => Changed?.Invoke();
    }

    /// <summary>Raised on the UI thread after anything a caller can see changes.</summary>
    public event Action? Changed;

    /// <summary>The GO on the clock: from the press, through the publish the cue's steps made, to the first frame every output drew with it. A desk's; a node's stack runs on paper and stamps nothing.</summary>
    public GoLatency GoClock { get; } = new();

    public CueStackConfig Stack => CueStacks.Caller(_kernel.State);

    public StackRuntime Runtime => _kernel.Cues.For(Stack);

    /// <summary>Newest first, bounded; the journal file is the durable copy.</summary>
    public ObservableCollection<CueExecutionRecord> History { get; } = new();

    public bool Armed => Runtime.Armed;

    /// <summary>The daily schedule, playlist part start times and plain F-keys wait while the caller is armed (per-show opt-out).</summary>
    public bool SuspendsAutomation => Runtime.Armed && Stack.SuspendAutomationWhileArmed;

    public RunCueConfig? StandbyCue => Runtime.StandbyCueId is { } id ? Stack.Cues.FirstOrDefault(c => c.Id == id) : null;

    public RunCueConfig? LastCue => Runtime.LastCueId is { } id ? Stack.Cues.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>"CONFIRM 03.020" while a confirm window is open, else null.</summary>
    public string? ConfirmText
        => Runtime.ConfirmPendingCueId is { } id && Stack.Cues.FirstOrDefault(c => c.Id == id) is { } cue
            ? $"CONFIRM {cue.Number}"
            : null;

    // ---- arming, standby, hold ---------------------------------------------------

    public void SetArmed(bool armed, ActionOrigin origin)
    {
        var rt = Runtime;
        if (rt.Armed == armed) return;
        rt.Armed = armed;
        if (armed && rt.StandbyCueId is null) StandbyFirst();
        if (!armed)
        {
            CancelConfirm();
            CancelFollow();
        }
        Bump();   // journaled by the action that asked (ListArm / ListDisarm through the executor)
    }

    /// <summary>Selects a cue without changing output. Clicking a row does the same.</summary>
    public void Standby(string? cueId)
    {
        var rt = Runtime;
        if (rt.StandbyCueId == cueId) return;
        rt.StandbyCueId = cueId;
        CancelConfirm();
        CancelFollow(); // the caller chose another cue: nothing fires by itself now
        Bump();
    }

    public void StandbyFirst() => Standby(Stack.Cues.FirstOrDefault(c => c.Enabled)?.Id);

    /// <summary>Moves standby by one enabled cue; no output change.</summary>
    public bool StandbyMove(int delta)
    {
        var cues = Stack.Cues;
        if (cues.Count == 0) return false;
        var current = Runtime.StandbyCueId is { } id ? cues.ToList().FindIndex(c => c.Id == id) : -1;
        var i = current;
        for (var hops = 0; hops < cues.Count; hops++)
        {
            i += delta;
            if (i < 0 || i >= cues.Count) return false;
            if (!cues[i].Enabled) continue;
            Standby(cues[i].Id);
            return true;
        }
        return false;
    }

    public void SetHold(bool hold, ActionOrigin origin)
    {
        var rt = Runtime;
        if (rt.Hold == hold) return;
        rt.Hold = hold;
        if (hold) CancelFollow(); // HOLD stops an auto-follow too — it is a GO like any other
        Bump();   // journaled by the action that asked (CueHoldOn / CueHoldOff through the executor)
    }

    public void CancelConfirm()
    {
        var rt = Runtime;
        if (rt.ConfirmPendingCueId is null) return;
        rt.ConfirmPendingCueId = null;
        rt.ConfirmDeadlineUtc = null;
        Bump();
    }

    // ---- auto-follow --------------------------------------------------------------------

    /// <summary>The pending auto-follow's cue, or null.</summary>
    public RunCueConfig? FollowCue => Runtime.FollowCueId is { } id && Runtime.FollowDueUtc is not null ? Stack.Cues.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>"AUTO in 0:07" while a follow is pending, else empty.</summary>
    public string FollowText(DateTime? nowUtc = null)
    {
        if (FollowCue is not { } cue || Runtime.FollowDueUtc is not { } due) return "";
        var left = due - (nowUtc ?? NowUtc());
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return $"AUTO {cue.Number} in {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    public void CancelFollow()
    {
        var rt = Runtime;
        if (rt.FollowDueUtc is null && rt.FollowCueId is null) return;
        rt.FollowDueUtc = null;
        rt.FollowCueId = null;
    }

    /// <summary>
    /// After a cue with a follow fired: the cue now on standby GOes by itself when the delay is
    /// up — from the poll for a delay, at once for zero — as long as the caller has not moved
    /// standby, held or disarmed in between. The gate still decides; a refusal is recorded like
    /// any other and the follow is spent.
    /// </summary>
    private void ArmFollow(RunCueConfig fired, DateTime now)
    {
        var rt = Runtime;
        if (fired.FollowSeconds is not { } delay || rt.StandbyCueId is not { } next) return;
        rt.FollowCueId = next;
        rt.FollowDueUtc = now + TimeSpan.FromSeconds(delay);
    }

    private void FireDueFollow(DateTime now)
    {
        var rt = Runtime;
        if (rt.FollowDueUtc is not { } due || now < due) return;
        var expected = rt.FollowCueId;
        CancelFollow();
        if (expected is null || rt.StandbyCueId != expected || !rt.Armed || rt.Hold) return;
        Go(ActionOrigin.Follow, expected, now);
    }

    // ---- GO -------------------------------------------------------------------------

    /// <summary>
    /// GO from any origin. <paramref name="seenStandbyId"/> is the standby the sender last saw
    /// (remotes always send it; the desk captures it at key-down); null skips the fence.
    /// </summary>
    public ActionResult Go(ActionOrigin origin, string? seenStandbyId = null, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? NowUtc();
        var rt = Runtime;
        var standby = StandbyCue;
        // The double-press lockout is for fingers; a follow is the cue's own doing and may land on the same tick.
        var lastGo = origin.Kind == OriginKind.Follow ? null : rt.LastGoUtc;
        var (decision, reason) = GoGate.Check(new GoGate.Inputs(
            rt.Armed, rt.Hold, _kernel.State.Blackout, rt.Executing,
            standby?.Id, seenStandbyId, lastGo, now,
            standby?.RequireConfirm ?? false, rt.ConfirmPendingCueId, rt.ConfirmDeadlineUtc));

        switch (decision)
        {
            case GoDecision.Confirm:
                rt.ConfirmPendingCueId = standby!.Id;
                rt.ConfirmDeadlineUtc = now + GoGate.ConfirmWindow;
                Bump();
                return ActionResult.Requested($"CONFIRM {standby.Number} — press GO again within {GoGate.ConfirmWindow.TotalSeconds:0} s.");
            case GoDecision.Refuse:
            {
                var label = standby is null ? "GO" : $"GO {standby.Number}";
                Record(standby, CueOutcome.Refused, origin, 0, standby?.Actions.Count ?? 0, reason, now);
                return ActionResult.Refused($"{label} refused — {reason}.");
            }
        }

        // Fire. The re-check against the live state and the run itself live in the action layer.
        rt.ConfirmPendingCueId = null;
        rt.ConfirmDeadlineUtc = null;
        rt.LastGoUtc = now;
        rt.Executing = true;
        var versionBefore = _kernel.Bus.Current.Version;
        var pressClock = ShowClock.Seconds;
        var pressStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        ActionResult result;
        try
        {
            result = _host.RunCue(Stack, standby!, origin);
        }
        finally
        {
            rt.Executing = false;
        }
        // The press on the clock: the publish it made (the bus's version after the steps ran) and how long the steps took; the sinks say the rest on the poll.
        if (_kernel.IsDesk && result.Ok) GoClock.Pressed(standby!.Number, versionBefore, _kernel.Bus.Current.Version, pressClock, System.Diagnostics.Stopwatch.GetElapsedTime(pressStamp).TotalMilliseconds);

        var outcome = result.Status switch
        {
            ActionStatus.Done => CueOutcome.Done,
            ActionStatus.Requested => CueOutcome.Requested,
            ActionStatus.Failed => CueOutcome.Failed,
            _ => CueOutcome.Refused,
        };
        var done = outcome is CueOutcome.Done or CueOutcome.Requested ? standby!.Actions.Count : ActionsDoneFrom(result.Message);
        CancelFollow(); // a GO of any origin spends a pending follow
        if (outcome is not CueOutcome.Refused)
        {
            // The place moves first, so the sidecar the record writes already points past this cue.
            rt.LastCueId = standby!.Id;
            rt.CurrentIndex = Stack.Cues.IndexOf(standby);
            if (result.Ok) _host.AirLabel = $"{standby.Number} {standby.Name}";
            AdvanceStandbyAfter(standby);
            if (result.Ok) ArmFollow(standby, now);
        }
        Record(standby, outcome, origin, done, standby!.Actions.Count, result.Message, now, result.Execution);
        // Rig day's streak: this GO against the running order — only a person's GO; the host says whether the games are on.
        if (outcome is not CueOutcome.Refused && origin.Kind != OriginKind.Follow) _host.RecordGo(Timing(now.ToLocalTime()).Offset);
        Bump();
        // A zero-second follow fires the next cue now, through the same gate, as its own GO.
        if (rt.FollowDueUtc is { } due && due <= now) FireDueFollow(now);
        return result;
    }

    private static int ActionsDoneFrom(string message)
    {
        // "failed at action k of n" → k - 1 stood.
        var i = message.IndexOf("failed at action ", StringComparison.Ordinal);
        if (i < 0) return 0;
        var rest = message[(i + "failed at action ".Length)..];
        var k = new string(rest.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(k, out var n) && n > 0 ? n - 1 : 0;
    }

    private void AdvanceStandbyAfter(RunCueConfig fired)
    {
        var cues = Stack.Cues;
        var index = cues.IndexOf(fired);
        for (var i = index + 1; i < cues.Count; i++)
        {
            if (cues[i].Enabled)
            {
                Runtime.StandbyCueId = cues[i].Id;
                return;
            }
        }
        Runtime.StandbyCueId = Stack.LoopAtEnd ? cues.FirstOrDefault(c => c.Enabled)?.Id : null;
    }

    private void Record(RunCueConfig? cue, CueOutcome outcome, ActionOrigin origin, int done, int total, string detail, DateTime now, CueExecution? execution = null)
    {
        var record = new CueExecutionRecord(now, cue?.Id ?? "", cue?.Number ?? "", cue?.Name ?? "", outcome, origin.Label, done, total, detail,
            execution?.Id ?? "", execution?.DevicePending ?? 0, execution?.Settling ?? false);
        History.Insert(0, record);
        while (History.Count > HistoryRows) History.RemoveAt(History.Count - 1);
        _kernel.Journal.Record(origin.Label, "CueGo", record.Label, outcome.ToString(), detail);
        _host.WriteRunPlace();
    }

    /// <summary>The caller's place for the sidecar, written on every GO.</summary>
    public RunPlace Place()
        => new(Runtime.StandbyCueId, Runtime.LastCueId, Runtime.LastGoUtc, History.Take(RunPlace.HistoryRows).ToList());

    /// <summary>A watchdog relaunch: the place comes back disarmed, pointing at the next cue, firing nothing.</summary>
    public string RestorePlace(RunPlace place)
    {
        var rt = Runtime;
        rt.Armed = false;
        rt.Hold = false;
        rt.LastCueId = place.LastCueId;
        rt.LastGoUtc = place.LastGoUtc;
        rt.CurrentIndex = place.LastCueId is { } last ? Stack.Cues.ToList().FindIndex(c => c.Id == last) : -1;
        History.Clear();
        foreach (var row in place.History.Take(HistoryRows)) History.Add(row);
        var standby = place.StandbyCueId is { } id && Stack.Cues.Any(c => c.Id == id && c.Enabled)
            ? id
            : NextEnabledAfter(place.LastCueId);
        rt.StandbyCueId = standby;
        Bump();
        var lastText = LastCue is { } cue && place.LastGoUtc is { } at
            ? $" — last GO {cue.Number} at {at.ToLocalTime():HH:mm:ss}"
            : "";
        return $"Restored after restart{lastText} — press ARM to continue.";
    }

    private string? NextEnabledAfter(string? cueId)
    {
        var cues = Stack.Cues;
        var index = cueId is null ? -1 : cues.ToList().FindIndex(c => c.Id == cueId);
        for (var i = index + 1; i < cues.Count; i++)
        {
            if (cues[i].Enabled) return cues[i].Id;
        }
        return cues.FirstOrDefault(c => c.Enabled)?.Id;
    }

    // ---- settling and the clock -------------------------------------------------------

    /// <summary>
    /// Called each second: a confirm window expires; a Requested record settles — a service
    /// that reports a failure flips it to FailedLate and it is never re-fired; otherwise it is
    /// Done once the window has passed.
    /// </summary>
    public void Poll(DateTime? nowUtc = null)
    {
        if (GoClock.IsOpen) GoClock.Resolve(FrameBudgets.FirstFrames(GoClock.PendingVersion, ShowClock.Seconds, _kernel.Bus), ShowClock.Seconds);
        var now = nowUtc ?? NowUtc();
        var rt = Runtime;
        if (rt.ConfirmPendingCueId is not null && rt.ConfirmDeadlineUtc is { } deadline && now > deadline)
        {
            CancelConfirm();
        }
        FireDueFollow(now);
        for (var i = 0; i < History.Count; i++)
        {
            var row = History[i];
            if (row.Outcome != CueOutcome.Requested) continue;
            if (row.Pending > 0) continue;                       // a box still owes this cue an answer: its receipt — or its timeout — settles the row, never the clock
            var failure = LateFailure();
            if (failure is not null)
            {
                History[i] = row with { Outcome = CueOutcome.FailedLate, Detail = $"{row.Detail} — later: {failure}" };
                _kernel.Journal.Record(row.Origin, "CueSettled", row.Label, CueOutcome.FailedLate.ToString(), failure);
                Bump();
            }
            else if (now - row.AtUtc > SettleWindow)
            {
                History[i] = row with { Outcome = CueOutcome.Done };
                Bump();
            }
        }
    }

    /// <summary>
    /// A box answered — or ran out of time — for a line one cue sent: that cue's row, found by
    /// the execution the line carried, settles on it and no other row does. A no (rejected, no
    /// answer, an observation that disagrees) is FailedLate with the box's own words, journaled
    /// as a settlement and never re-fired; a yes takes one receipt off the row's count, and the
    /// last yes makes the row Done unless something else is still settling. A line that
    /// belonged to no cue, or to a run the history has forgotten, settles nothing.
    /// </summary>
    public void OnDeviceReceipt(DeviceReceipt receipt)
    {
        if (receipt.Execution.Length == 0) return;
        for (var i = 0; i < History.Count; i++)
        {
            var row = History[i];
            if (row.ExecutionId != receipt.Execution) continue;
            if (!receipt.Ok)
            {
                if (row.Outcome is CueOutcome.FailedLate or CueOutcome.Failed or CueOutcome.Refused) return;
                History[i] = row with { Outcome = CueOutcome.FailedLate, Pending = Math.Max(0, row.Pending - 1), Detail = $"{row.Detail} — later: {receipt.Line}" };
                _kernel.Journal.Record(row.Origin, "CueSettled", row.Label, CueOutcome.FailedLate.ToString(), receipt.Line);
            }
            else
            {
                var pending = Math.Max(0, row.Pending - 1);
                var outcome = row.Outcome == CueOutcome.Requested && pending == 0 && !row.Settling ? CueOutcome.Done : row.Outcome;
                History[i] = row with { Outcome = outcome, Pending = pending };
                if (outcome != row.Outcome) _kernel.Journal.Record(row.Origin, "CueSettled", row.Label, outcome.ToString(), receipt.Line);
            }
            _host.WriteRunPlace();
            Bump();
            return;
        }
    }

    /// <summary>
    /// A step of a cue's tail ran, later: a device line it sent is one more receipt its row
    /// waits for (the row goes back to Requested from Done), and a step that failed makes the
    /// row FailedLate with the step named — the cue did not finish as written.
    /// </summary>
    public void TailStep(string executionId, int number, int of, ActionResult result, bool isDevice)
    {
        if (executionId.Length == 0) return;
        for (var i = 0; i < History.Count; i++)
        {
            var row = History[i];
            if (row.ExecutionId != executionId) continue;
            if (!result.Ok)
            {
                if (row.Outcome is CueOutcome.FailedLate or CueOutcome.Failed or CueOutcome.Refused) return;
                var words = $"step {number} of {of} failed: {result.Message}";
                History[i] = row with { Outcome = CueOutcome.FailedLate, Detail = $"{row.Detail} — later: {words}" };
                _kernel.Journal.Record(row.Origin, "CueSettled", row.Label, CueOutcome.FailedLate.ToString(), words);
            }
            else if (result.Status == ActionStatus.Requested && isDevice)
            {
                History[i] = row with { Outcome = row.Outcome == CueOutcome.Done ? CueOutcome.Requested : row.Outcome, Pending = row.Pending + 1 };
            }
            else
            {
                return;
            }
            _host.WriteRunPlace();
            Bump();
            return;
        }
    }

    /// <summary>
    /// A watched service saying it failed. Break music contributes <see cref="SpotifyService.CommandFailure"/>
    /// and never its Status: that line legitimately says "No Spotify device…" for minutes while
    /// nothing is being asked of it, and feeding it in would poison every asynchronous cue in the show.
    /// </summary>
    private string? LateFailure()
    {
        foreach (var status in _host.WatchedStatuses())
        {
            if (StatusWords.ReadsAsFailure(status)) return status;
        }
        return null;
    }

    // ---- the day's clock -------------------------------------------------------------------

    /// <summary>Where the day stands against the running order, from the last GO and the clock. Pure underneath; cheap enough for every tick.</summary>
    public TimingReport Timing(DateTime? nowLocal = null)
    {
        var rt = Runtime;
        return CueTiming.Estimate(Stack.Cues, rt.LastCueId, rt.LastGoUtc?.ToLocalTime(), rt.StandbyCueId, nowLocal ?? DateTime.Now);
    }

    /// <summary>The cue the day's edits start from: standby, else the one after the last GO, else the first.</summary>
    public int EditFromIndex()
    {
        var cues = Stack.Cues;
        if (Runtime.StandbyCueId is { } standby)
        {
            var i = cues.ToList().FindIndex(c => c.Id == standby);
            if (i >= 0) return i;
        }
        if (Runtime.LastCueId is { } last)
        {
            var i = cues.ToList().FindIndex(c => c.Id == last);
            if (i >= 0) return Math.Min(i + 1, cues.Count);
        }
        return 0;
    }

    /// <summary>Pushes or pulls every planned start from the standby cue on by a delta; says what it did.</summary>
    public string ShiftPlan(TimeSpan delta, ActionOrigin origin)
    {
        var moved = 0;
        _host.BulkEdit(() => moved = CueTiming.Shift(Stack.Cues, EditFromIndex(), delta));
        var text = moved == 0
            ? "No planned start times from the standby cue on — set them on the Cues page or import a running order."
            : $"{moved} planned start{(moved == 1 ? "" : "s")} moved {CueTiming.FormatDelta(delta)} from the standby cue on.";
        _kernel.Journal.Record(origin.Label, "PlanShift", Stack.Name, ActionStatus.Done.ToString(), text);
        Bump();
        return text;
    }

    /// <summary>"We resume now": the standby cue's planned start becomes the clock and the rest of the day moves with it.</summary>
    public string ResumeNow(ActionOrigin origin, DateTime? nowLocal = null)
    {
        var from = EditFromIndex();
        if (from >= Stack.Cues.Count) return "No cue to resume from.";
        var cue = Stack.Cues[from];
        var before = cue.PlannedStart;
        var now = (nowLocal ?? DateTime.Now).TimeOfDay;
        var changed = 0;
        _host.BulkEdit(() => changed = CueTiming.Rebase(Stack.Cues, from, now));
        var text = before.Length > 0
            ? $"{cue.Number} now planned for {CueTiming.FormatClock(now)} (was {before}); {Math.Max(0, changed - 1)} later start{(changed - 1 == 1 ? "" : "s")} moved with it."
            : $"{cue.Number} now planned for {CueTiming.FormatClock(now)}.";
        _kernel.Journal.Record(origin.Label, "PlanResume", Stack.Name, ActionStatus.Done.ToString(), text);
        Bump();
        return text;
    }

    /// <summary>Makes up the lateness before the next mark by squeezing the planned lengths in proportion; says how much was found.</summary>
    public string CatchUp(ActionOrigin origin, DateTime? nowLocal = null)
    {
        var timing = Timing(nowLocal);
        if (timing.Offset is not { } offset || offset <= CueTiming.Tolerance) return "Not behind the plan — nothing to catch up.";
        var recovered = 0;
        _host.BulkEdit(() => recovered = CueTiming.CatchUp(Stack.Cues, EditFromIndex(), offset));
        var text = recovered == 0
            ? "Nothing to squeeze before the next mark — the cues there have no planned lengths above 30 s."
            : recovered >= (int)offset.TotalSeconds - 1
                ? $"Caught up: {CueTiming.FormatDuration(recovered)} taken off the planned lengths before the next mark."
                : $"{CueTiming.FormatDuration(recovered)} found before the next mark — still {CueTiming.FormatDuration((int)offset.TotalSeconds - recovered)} behind.";
        _kernel.Journal.Record(origin.Label, "PlanCatchUp", Stack.Name, ActionStatus.Done.ToString(), text);
        Bump();
        return text;
    }

    /// <summary>"no auto" or "HELD: next auto 19:45 'Break'" — whether anything else can move the picture.</summary>
    public string NextAutoText(DateTime localNow)
    {
        var next = LookService.NextCue(_kernel.State.LooksAndCues.Cues, localNow);
        if (next is not { } n) return SuspendsAutomation ? "AUTO HELD" : "no auto";
        var when = $"{n.At:HH:mm} '{n.Cue.LookName}'";
        return SuspendsAutomation ? $"HELD: next auto {when}" : $"NEXT AUTO {when}";
    }

    private void Bump()
    {
        Runtime.Seq++;
        Changed?.Invoke();
    }
}
