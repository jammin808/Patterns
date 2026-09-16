using Patterns.Devices;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The show moving between the twins: the marker a standby on this machine leaves, the hold and
/// the release, TAKE BACK as a transaction with its wall switch, the hand-back answered by
/// RELEASED, and, on the standby's side, TAKE OVER with its fences and STAND BY AGAIN.
/// </summary>
public sealed partial class TwinService
{
    // ---- the main: a standby that has the show ------------------------------------------------

    /// <summary>
    /// The marker a standby on this machine writes when it takes the show, read from the folder this
    /// desk launches one into: while that process lives this desk's outputs are held — two desks
    /// driving the same screens is the one failure worse than one being down. Once a second from the
    /// desk's poll (any role: a main restarted by its watchdog reads it before its first window),
    /// at once when forced.
    /// </summary>
    private void CheckMarker(bool force)
    {
        var now = Clock();
        if (!force && now - _markerCheckedUtc < TimeSpan.FromSeconds(2)) return;
        _markerCheckedUtc = now;
        TwinTookOverMarker? marker;
        try
        {
            marker = TwinHandover.Read(TwinHandover.StandbyHome(_kernel.Store.BaseDirectory));
        }
        catch (Exception)
        {
            return;
        }
        var holds = marker is not null && TwinHandover.Holds(marker, Probe.Look);   // a process that cannot be read still holds: a fence, not an absence
        if (holds && !_holderMarked)
        {
            _holderMarked = true;
            Hold(marker!.Standby, marker.AtUtc, linked: _holderLinked);
        }
        else if (!holds && _holderMarked)
        {
            _holderMarked = false;
            if (marker is not null) HolderDied(marker.Standby);       // the marker stands but its process is gone: it died with the show
            else if (!_holderLinked) Release("ended");                // the marker was cleared: it stood by again on purpose
        }
    }

    /// <summary>
    /// The standby on this machine died with the show — its marker's process is gone. The show it
    /// sent while it ran, if any, lands here, and what it had on air goes back on here the way a
    /// restart puts it back: a room left dark until somebody presses OUTPUTS ON is the very thing
    /// a standby was there to prevent. A standby elsewhere that merely leaves the link is not this:
    /// a link that dropped cannot say whether that desk still runs the show.
    /// </summary>
    private void HolderDied(string name)
    {
        var who = _holder.Length > 0 ? _holder : name.Length > 0 ? name : "the standby";
        var head = $"The standby {who} died with the show at {Clock().ToLocalTime():HH:mm:ss} — put back on here.";
        var notes = new List<string>();
        if (_heldShowJson is { } json)
        {
            var ok = false;
            _services.BulkEdit(() => ok = TwinSync.ApplyShow(_kernel.State, json));
            notes.Add(ok ? "its show landed here" : "its show could not be read — this desk's show stands");
            if (!ok) Log.Warn("Twin: the standby's show could not be read after it died.");
        }
        var air = _heldAir ?? _air;
        _holder = "";
        _holderSinceUtc = null;
        _holderLinked = false;
        _heldShowJson = null;
        _heldAir = null;
        try
        {
            TwinHandover.Clear(TwinHandover.StandbyHome(_kernel.Store.BaseDirectory));
        }
        catch (Exception)
        {
            // a marker whose process is gone holds nothing either way
        }
        if (_role != TwinRole.Standby) _services.OutputsHeldBy = "";
        var words = head + (notes.Count > 0 ? " " + string.Join(", ", notes) + "." : "");
        Log.Warn($"Twin: {words}");
        _services.Notify(words);
        _services.RecoverFromTwin(air, head, peer: who);
    }

    /// <summary>From the desk's poll, once a second, whatever the role: the standby's answer to a hand-back watched for first, then the marker.</summary>
    public void Poll()
    {
        CheckRelease();
        if (_releaseWait is null) CheckMarker(force: false);
    }

    /// <summary>A standby has the show: this desk's outputs are held (and closed, were they open) until TAKE BACK.</summary>
    private void Hold(string standby, DateTime? sinceUtc, bool linked)
    {
        var fresh = _holder.Length == 0;
        _holder = standby.Length > 0 ? standby : "the standby";
        _holderSinceUtc ??= sinceUtc;
        if (linked) _holderLinked = true;
        if (_role == TwinRole.Standby) return; // a standby's own hold has its own words
        _services.OutputsHeldBy = TwinHandover.HoldWords(_holder, _holderSinceUtc);
        if (_services.OutputsLive)
        {
            _services.CloseOutputs();
            Log.Warn($"Twin: the standby {_holder} has the show — this desk's outputs are closed.");
        }
        if (fresh) _services.Notify($"Twin: the standby {_holder} has the show — this desk's outputs are held closed. TAKE BACK (Machine page, TWIN) puts the show back here.");
    }

    /// <summary>The standby no longer has the show: this desk's outputs are its own again — OUTPUTS ON is the operator's press.</summary>
    private void Release(string reason)
    {
        var who = _holder.Length > 0 ? _holder : "the standby";
        _holder = "";
        _holderSinceUtc = null;
        _holderLinked = false;
        _holderMarked = false;
        _heldShowJson = null;
        _heldAir = null;
        if (_role != TwinRole.Standby) _services.OutputsHeldBy = "";
        Log.Info($"Twin: the standby {who} {reason} — this desk's outputs are its own again.");
        _services.Notify($"Twin: the standby {who} {reason} — this desk's outputs are its own again; OUTPUTS ON puts the show on here.");
    }

    /// <summary>
    /// The main takes the show back from a standby that ran it: the standby's show — the edits made
    /// while it ran — lands here first, what it had on air goes on here the way a restart puts it
    /// back, the standby is told to close its outputs and follow again — and says that it has, for
    /// the hand-back by name, before this desk counts it released — and the whole show goes back
    /// over the link so it is in step from here. A take-back that waits on a fact stops in words and
    /// the next press supplies or re-asks it: the room switched by hand, the standby told again.
    /// </summary>
    public ActionResult TakeBack(ActionOrigin origin)
    {
        if (_role != TwinRole.Main) return ActionResult.Refused("This desk is not the main twin — Machine page, TWIN.");
        if (_handoverBusy) return ActionResult.Refused(_releaseWait is not null
            ? "A hand-back is in progress — the standby was told to let go and its answer is awaited."
            : "A hand-back is in progress — the wall switch was asked and its answer is awaited.");
        if (_releaseWait is { } overdue) return TellAgain(overdue, origin);
        var now = Clock();
        Standby? holder;
        if (_switchByHand is { } byHand)
        {
            // The second press of a take-back whose route is the operator's own: the room was switched
            // by hand — the operator's word is the route's confirmation — so the standby is released now.
            lock (_gate)
            {
                holder = _standbys.FirstOrDefault(s => s.HoldsShow && s.Instance == byHand.Instance);
            }
            _switchByHand = null;
            _handoverNote = "";
            byHand.Tx.Resume("switched by hand — the operator's word");
            if (byHand.Tx.Next == HandoverStage.RouteConfirmed) byHand.Tx.Reached(HandoverStage.RouteConfirmed);   // a cue that fired and nothing a box confirmed: confirmed by the operator's word
            return FinishTakeBack(holder, byHand.Instance, byHand.Name, byHand.Handover, byHand.Tx, byHand.Words + " Switched by hand.", origin);
        }
        if (_holder.Length == 0) return ActionResult.Refused("No standby has the show — nothing to take back.");
        lock (_gate)
        {
            holder = _standbys.FirstOrDefault(s => s.HoldsShow);
        }
        if (holder is null) return ActionResult.Refused($"The standby {_holder} has the show but is not on the link — TAKE BACK once it is, or OUTPUTS ON if it is gone.");
        var cue = _kernel.State.Twin.TakeBackCue;
        var sameMachine = holder.Machine.Length > 0 && string.Equals(holder.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        var marked = _holderMarked;                                                    // its marker was seen: its word on disk counts as its answer
        var tx = new TwinTransaction(HandoverKind.TakeBack, sameMachine ? HandoverShape.SameMachine : HandoverShape.AcrossMachines, cue.Length > 0, now);
        _handover = tx;
        var head = $"TOOK BACK from {holder.Name} at {now.ToLocalTime():HH:mm:ss}.";
        var notes = new List<string>();
        // The standby's show — the edits made while it ran — lands here first, once: a second
        // press after a stopped route finds it landed already.
        if (_heldShowJson is { } json)
        {
            var ok = false;
            _services.BulkEdit(() => ok = TwinSync.ApplyShow(_kernel.State, json));
            notes.Add(ok ? "its show landed here" : "its show could not be read — this desk's show stands");
            if (!ok) Log.Warn("Twin: the standby's show could not be read on TAKE BACK.");
            _heldShowJson = null;
        }
        var air = _heldAir;
        var words = head + (notes.Count > 0 ? " " + string.Join(", ", notes) + "." : "");

        if (tx.Shape == HandoverShape.AcrossMachines)
        {
            // The room looks at one of two machines through its switcher. This desk's picture goes
            // up first, on displays the room is not yet looking at; the room is pointed at it and
            // the switch answers; only then is the standby, whose picture the room was watching,
            // told to let go. A switch that fails leaves the standby up and the room on it — and a
            // room nobody's box can vouch for is switched by the operator, who says so with the next press.
            _services.OutputsHeldBy = "";
            _services.RecoverFromTwin(air, head, peer: holder.Name);
            tx.Reached(HandoverStage.TargetReady);
            if (!tx.HasRoute) return SwitchTheRoomByHand(tx, holder, words, "No take-back cue is set", origin);
            var wall = FireWallSwitch(cue);
            tx.Reached(HandoverStage.RouteRequested);
            if (!wall.Ok) return StopTakeBack(tx, holder, cue, $"could not fire ({wall.Reason})", origin);
            words += " " + wall.Words;
            if (wall.Receipts is { } receipts)
            {
                // The boxes answer on their own time: the standby is released when they have,
                // and not at all when one of them says no or says nothing.
                _handoverBusy = true;
                var pendingWords = words;
                var instance = holder.Instance;
                var name = holder.Name;
                var handover = holder.Handover;
                _ = receipts.ContinueWith(t => UiThread.Post(() =>
                {
                    _handoverBusy = false;
                    if (tx.Stopped || tx.IsComplete) return;
                    var (ok, said) = ReadReceipts(t);
                    if (!ok)
                    {
                        StopTakeBack(tx, holder, cue, $"was not confirmed — {said}", origin);
                        return;
                    }
                    tx.Reached(HandoverStage.RouteConfirmed);
                    Standby? still;
                    lock (_gate)
                    {
                        still = _standbys.FirstOrDefault(s => s.HoldsShow && s.Instance == instance);
                    }
                    var asked = FinishTakeBack(still, instance, name, handover, tx, pendingWords + " " + said, origin);
                    if (!asked.Ok) _services.Notify(asked.Message);
                }), TaskScheduler.Default);
                return ActionResult.Requested($"TAKE BACK: the show is on here and the room was asked to look at this desk — the standby {holder.Name} is released once the switch answers.");
            }
            // Fired, and nothing a box confirms: sent is not switched. The operator's word finishes it.
            return SwitchTheRoomByHand(tx, holder, words, $"The take-back cue '{cue}' fired, but it sends nothing a box confirms", origin);
        }
        // One machine: the standby's windows are these displays. It lets go first — HANDBACK closes
        // them, and it says so — and this desk's picture goes up after: a dark instant, never two sets.
        CommitTakeBack(holder, tx);
        return AskRelease(tx, holder, holder.Instance, holder.Name, holder.Handover, sameMachine: true, marked, air, head, words, cue, origin);
    }

    /// <summary>Across machines, the route confirmed or the operator's word given: this desk is the main again, and the standby is told to let go — released once it says it has.</summary>
    private ActionResult FinishTakeBack(Standby? holder, string instance, string name, string handover, TwinTransaction tx, string words, ActionOrigin origin)
    {
        CommitTakeBack(holder, tx);
        return AskRelease(tx, holder, instance, name, handover, sameMachine: false, marked: false, air: null, head: "", words, cue: "", origin);
    }

    /// <summary>
    /// A take-back across machines whose route nobody's box vouches for: the show is up here, on
    /// displays the room is not yet looking at, and the standby stays up on its own input until the
    /// operator has switched the room and pressed TAKE BACK again. Nothing infers a switch thrown.
    /// </summary>
    private ActionResult SwitchTheRoomByHand(TwinTransaction tx, Standby holder, string words, string why, ActionOrigin origin)
    {
        tx.Stop("the route is the operator's own");
        _switchByHand = new SwitchByHand(tx, holder.Instance, holder.Name, holder.Handover, words);
        _handoverNote = TwinTransaction.SwitchByHandWords(holder.Name, why);
        Log.Warn($"Twin: {_handoverNote} ({origin.Label}) — {tx.Trail}");
        _services.Notify(_handoverNote);
        return ActionResult.Requested(_handoverNote);
    }

    /// <summary>The route did not move the room: the standby stays up and the holder, the words say what finishes it.</summary>
    private ActionResult StopTakeBack(TwinTransaction tx, Standby holder, string cue, string reason, ActionOrigin origin)
    {
        tx.Stop(reason);
        _handoverNote = TwinTransaction.RouteStoppedWords(holder.Name, cue, reason);
        Log.Warn($"Twin: {_handoverNote} ({origin.Label}) — {tx.Trail}");
        _services.Notify(_handoverNote);
        return ActionResult.Failed(_handoverNote);
    }

    /// <summary>This desk is the main again: the holder's state cleared. The standby clears its own marker when it lets go — that is one of the ways its answer is known. The word to the standby follows, on the caller's thread, so it reads them in that order.</summary>
    private void CommitTakeBack(Standby? holder, TwinTransaction tx)
    {
        if (holder is not null) holder.HoldsShow = false;
        _holderLinked = false;
        _holderMarked = false;
        _holder = "";
        _holderSinceUtc = null;
        tx.Reached(HandoverStage.AuthorityCommitted);
    }

    // ---- the hand-back, answered -------------------------------------------------------------

    private const int ReleaseTimeoutSeconds = 5;

    /// <summary>A takeover this desk took back: the takeover's id (the standby's join carries it) and the hand-back's, so a claim made under that takeover is answered with the hand-back again.</summary>
    private readonly record struct TakenBack(string Takeover, string TakeBack, DateTime AtUtc);

    /// <summary>A take-back across machines stopped for the operator's switch: the second press releases the standby named.</summary>
    private sealed record SwitchByHand(TwinTransaction Tx, string Instance, string Name, string Handover, string Words);

    /// <summary>A hand-back sent and its answer awaited — or overdue, and kept for the next press, the standby's re-dial or, on one machine, its marker's word.</summary>
    private sealed class ReleaseWait
    {
        public required TwinTransaction Tx { get; init; }
        public required string Instance { get; init; }
        public required string Name { get; init; }
        public required bool SameMachine { get; init; }
        public required bool Marked { get; init; }
        public required ActionOrigin Origin { get; init; }
        public RecoverySnapshot? Air { get; init; }
        public string Head { get; init; } = "";
        public string Words { get; init; } = "";
        public string Cue { get; init; } = "";
        public DateTime AskedUtc { get; set; }
        public bool Overdue { get; set; }
    }

    /// <summary>
    /// HANDBACK with the handover's id to the standby, and the wait for its RELEASED: on one machine
    /// this desk's outputs stay held until it answers — its windows are these displays — and the
    /// picture goes up the moment it has; across machines the room already shows this desk. A line
    /// that could not be written drops the link, and the standby is told again the moment it dials
    /// back; a standby that is not on the link at all is told when it is.
    /// </summary>
    private ActionResult AskRelease(TwinTransaction tx, Standby? holder, string instance, string name, string handover, bool sameMachine, bool marked, RecoverySnapshot? air, string head, string words, string cue, ActionOrigin origin)
    {
        var wait = new ReleaseWait { Tx = tx, Instance = instance, Name = name, SameMachine = sameMachine, Marked = marked, Origin = origin, Air = air, Head = head, Words = words, Cue = cue, AskedUtc = Clock() };
        _releaseWait = wait;
        _takenBack[instance] = new TakenBack(handover, tx.Id, wait.AskedUtc);
        if (holder is null)
        {
            // Not on the link to be told: the wait stands, overdue from the start, and its re-dial is answered with the hand-back.
            wait.Overdue = true;
            tx.Stop("the standby is not on the link to be told");
            _handoverNote = TwinTransaction.ReleaseStoppedWords(name, sameMachine);
            if (sameMachine) _services.OutputsHeldBy = $"the standby {name} was told to let go and has not said it has — TAKE BACK again tells it again";
            Log.Warn($"Twin: {_handoverNote} ({origin.Label}) — {tx.Trail}");
            _services.Notify(_handoverNote);
            return ActionResult.Failed(_handoverNote);
        }
        _handoverBusy = true;
        _handoverNote = "";
        if (sameMachine) _services.OutputsHeldBy = TwinTransaction.ReleaseAwaitedWords(name, sameMachine: true);
        if (!holder.TryWrite(TwinMessage.Format(TwinWord.HandBack, tx.Id)))
        {
            Log.Warn($"Twin: the hand-back to {name} could not be written — it is told again the moment it links.");
            Drop(holder);
        }
        return ActionResult.Requested(TwinTransaction.ReleaseAwaitedWords(name, sameMachine));
    }

    /// <summary>TAKE BACK again while the standby's answer is overdue: told again on the link it has now, or said where it stands.</summary>
    private ActionResult TellAgain(ReleaseWait wait, ActionOrigin origin)
    {
        Standby? peer;
        lock (_gate)
        {
            peer = _standbys.FirstOrDefault(s => !s.IsFollower && s.Instance == wait.Instance);
        }
        if (peer is null)
        {
            return ActionResult.Refused($"The standby {wait.Name} is not on the link — it is told the hand-back the moment it links again"
                + (wait.SameMachine ? "; a process of it that has gone is seen by its marker, and the show goes on here by itself." : "."));
        }
        wait.Tx.Resume("told again");
        wait.Overdue = false;
        wait.AskedUtc = Clock();
        _handoverBusy = true;
        _handoverNote = "";
        if (wait.SameMachine) _services.OutputsHeldBy = TwinTransaction.ReleaseAwaitedWords(wait.Name, sameMachine: true);
        Log.Info($"Twin: the standby {wait.Name} is told the hand-back again ({origin.Label}).");
        if (!peer.TryWrite(TwinMessage.Format(TwinWord.HandBack, wait.Tx.Id))) Drop(peer);
        return ActionResult.Requested(TwinTransaction.ReleaseAwaitedWords(wait.Name, wait.SameMachine));
    }

    /// <summary>RELEASED from a standby (UI thread): the hand-back it names is the one awaited, or it is not.</summary>
    private void OnReleased(Standby from, string id)
    {
        if (_releaseWait is { } wait && wait.Tx.Id == id) CompleteRelease(wait, $"{from.Name} said it let go");
        else Log.Info($"Twin: {from.Name} said it let go for the hand-back '{id}', which this desk is not waiting on.");
    }

    /// <summary>
    /// The old owner is released — it said so, its marker went, its process went, or it stands by
    /// again on a fresh link. On one machine the picture goes up here now; either way the whole
    /// show goes back over the link and the handover is complete.
    /// </summary>
    private void CompleteRelease(ReleaseWait wait, string how)
    {
        if (!ReferenceEquals(_releaseWait, wait)) return;
        _releaseWait = null;
        _handoverBusy = false;
        _handoverNote = "";
        var tx = wait.Tx;
        if (tx.Stopped) tx.Resume(how);
        else tx.Note(how);
        tx.Reached(HandoverStage.OldOwnerReleased);
        var words = wait.Words;
        if (wait.SameMachine)
        {
            _services.OutputsHeldBy = "";
            _services.RecoverFromTwin(wait.Air, wait.Head, peer: wait.Name);
            tx.Reached(HandoverStage.TargetReady);
            if (wait.Cue.Length > 0) words += " " + FireWallSwitch(wait.Cue).Words;   // a switch on one machine is the operator's own to have set; fired, never a fence
        }
        _heldAir = null;
        _pendingWhole = true;
        _pendingAir = true;
        ScheduleFlush();
        tx.Reached(HandoverStage.Complete);
        Log.Warn($"Twin: {words} ({wait.Origin.Label}) — {tx.Trail}");
        _services.Notify(words);
    }

    /// <summary>
    /// Once a second while a hand-back's answer is awaited. On one machine the standby's word is
    /// also on disk: the marker it was seen by, gone, is its outputs closed (it clears the marker
    /// after closing them); the marker's process gone is a standby that died after the ask — either
    /// way these displays are free. Past the limit the wait is overdue: said, the outputs still held
    /// here on one machine, and the next press, the re-dial or the marker finishes it.
    /// </summary>
    private void CheckRelease()
    {
        if (_releaseWait is not { } wait) return;
        var now = Clock();
        if (wait.SameMachine && wait.Marked)
        {
            var home = TwinHandover.StandbyHome(_kernel.Store.BaseDirectory);
            var gone = false;
            TwinTookOverMarker? marker = null;
            try
            {
                gone = !File.Exists(TwinHandover.PathFor(home));
                if (!gone) marker = TwinHandover.Read(home);
            }
            catch (Exception)
            {
                // a folder that cannot be looked at says nothing: the answer, or the limit, decides
            }
            if (gone)
            {
                CompleteRelease(wait, "its marker is gone");
                return;
            }
            if (marker is not null && !TwinHandover.Holds(marker, Probe.Look))
            {
                TwinHandover.Clear(home);
                CompleteRelease(wait, "its process is gone");
                return;
            }
        }
        if (!wait.Overdue && now - wait.AskedUtc > TimeSpan.FromSeconds(ReleaseTimeoutSeconds))
        {
            wait.Overdue = true;
            _handoverBusy = false;
            wait.Tx.Stop("the standby did not say it let go");
            _handoverNote = TwinTransaction.ReleaseStoppedWords(wait.Name, wait.SameMachine);
            if (wait.SameMachine) _services.OutputsHeldBy = $"the standby {wait.Name} was told to let go and has not said it has — TAKE BACK again tells it again";
            Log.Warn($"Twin: {_handoverNote} ({wait.Origin.Label}) — {wait.Tx.Trail}");
            _services.Notify(_handoverNote);
        }
    }

    /// <summary>
    /// What firing the wall-switch cue came to: no cue set, fired, or not — with the reason and the
    /// words for the line — and, when the cue sent lines to boxes that answer, the receipts still
    /// to come: the route is confirmed by them, never by the cue having fired.
    /// </summary>
    private readonly record struct WallSwitch(bool Ok, string Reason, string Words, Task<IReadOnlyList<DeviceReceipt>>? Receipts = null);

    /// <summary>
    /// The wall-switch cue — the room's own fence. A cue that puts this machine's input on the wall
    /// (a switcher's HTTP or OSC verb, a PJLink input, a matrix route), so whichever desk the room
    /// shows is the one running the show. Ok with no words when there is none.
    /// </summary>
    private WallSwitch FireWallSwitch(string cue)
    {
        if (cue.Length == 0) return new WallSwitch(true, "", "");
        var mark = _services.DeviceMark();
        var result = _services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, cue), ActionOrigin.Recovery);
        var words = result.Ok ? $"Wall switch: cue '{cue}' fired." : $"Wall switch: cue '{cue}' could not fire — {result.Message}";
        if (result.Ok) Log.Info($"Twin: {words}");
        else Log.Warn($"Twin: {words}");
        if (!result.Ok) return new WallSwitch(false, result.Message, words);
        // Fired is dispatched. A cue that sent lines to boxes is confirmed by their receipts, up
        // to each device's own timeout; a cue with no box to answer is the operator's own switch.
        var receipts = _services.DeviceSentSince(mark) > 0 ? _services.DeviceConfirmSince(mark) : null;
        return new WallSwitch(true, "", words, receipts);
    }

    /// <summary>
    /// "Wall switch confirmed: Switcher: POST /route — accepted (200 OK)" or the first receipt that
    /// failed, for the words. A route is confirmed by a box that said yes — a receipt at Accepted or
    /// Observed — never by the bytes having arrived: a cue whose receipts all stop at Delivered was
    /// heard, not obeyed, and says so.
    /// </summary>
    private static (bool Ok, string Words) ReadReceipts(Task<IReadOnlyList<DeviceReceipt>> task)
    {
        if (!task.IsCompletedSuccessfully) return (false, "the receipts could not be read");
        #pragma warning disable VSTHRD002 // IsCompletedSuccessfully was checked the line above
        var receipts = task.Result;
        #pragma warning restore VSTHRD002
        var bad = receipts.FirstOrDefault(r => !r.Ok);
        if (bad is not null) return (false, bad.Line);
        if (!receipts.Any(r => r.Reached >= ConfirmLevel.Accepted))
        {
            return (false, receipts.Count == 0
                ? "no box answered"
                : "no box said yes — " + string.Join("; ", receipts.Select(r => r.Line)) + " — delivered is not switched");
        }
        return (true, "Wall switch confirmed: " + string.Join("; ", receipts.Select(r => r.Line)) + ".");
    }

    private bool MainIsOnThisMachine() => _welcome is { } w && string.Equals(w.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    // ---- the standby: the show ---------------------------------------------------------------

    /// <summary>
    /// The standby runs the show from here. The fence first, before anything is let go of: the
    /// takeover is marked on disk for a main on this machine that comes back, and a main on this
    /// very machine that is still up but stopped beating — hung; its windows would play on under
    /// nobody's hand and ours would open behind them — is ended, and only once it is provably gone
    /// does the hold on the outputs lift and the air record the main sent last go back on, the way
    /// a watchdog restart puts it back. Either fence failing, nothing is taken by itself: two desks
    /// on one set of screens is the one failure worse than one being down. A press can override
    /// (TAKE OVER ANYWAY, TWIN TAKEOVER FORCE), and says so in its words.
    /// </summary>
    public ActionResult TakeOver(ActionOrigin origin, bool force = false)
    {
        if (IsFollowerNode) return ActionResult.Refused($"A {NodeKinds.Label(_kernel.Profile).ToLowerInvariant()} node never takes the show over — it has no outputs to take it onto.");
        if (_role != TwinRole.Standby) return ActionResult.Refused("This desk is not a standby twin — Machine page, TWIN.");
        if (_phase == TwinPhase.TookOver) return ActionResult.Done("This desk already took the show over.");
        if (_handoverBusy) return ActionResult.Refused("A takeover is in progress — the wall switch was asked and its answer is awaited.");
        if (_welcome is null) return ActionResult.Refused("Nothing to take over: no main has been joined yet.");
        var now = Clock();
        var main = _mainName.Length > 0 ? _mainName : "the main";
        var notes = new List<string>();
        var w = _welcome;
        var localMain = MainIsOnThisMachine();
        var tx = new TwinTransaction(HandoverKind.TakeOver, localMain ? HandoverShape.SameMachine : HandoverShape.AcrossMachines, _kernel.State.Twin.TakeOverCue.Length > 0, now);
        _handover = tx;

        // The marker: said on disk, in this desk's own folder, so a main on this machine that comes
        // back reads it before its first window opens and holds its outputs while this process
        // lives. Only such a main ever reads it; one elsewhere loses nothing by its absence.
        string? fence = null;
        var marked = false;
        try
        {
            TwinHandover.Write(_kernel.Store.BaseDirectory, new TwinTookOverMarker(Name, Environment.MachineName, Environment.ProcessId, ProcessStartTicks(), Environment.ProcessPath ?? "", now, main, tx.Id));
            marked = true;
        }
        catch (Exception ex)
        {
            if (localMain) fence = $"the takeover could not be marked on disk ({ex.Message})";
            else Log.Warn("Twin: the takeover could not be marked on disk — the main is on another machine and nothing reads it there.", ex);
        }

        // The hung main on this very machine: ended, and only a process that is provably the one
        // the welcome named and provably Patterns — a pid is never enough to end something by. A
        // process that is gone, or whose id was handed out again, holds nothing; one that is up
        // but cannot be read from here is a fence, not an absence: it may well still have the
        // screens, and "could not see it" is never "it is gone".
        if (fence is null && localMain && w.Pid > 0 && w.Pid != Environment.ProcessId)
        {
            var sight = Probe.Look(w.Pid);
            if (sight.IsGoneOrReused(w.StartedAtUtcTicks))
            {
                // gone with its windows, or another program wearing its id: nothing to end
            }
            else if (sight.IsUnreadable)
            {
                fence = $"{main}'s process (pid {w.Pid}) is still up but cannot be read from here (another user's, or elevated?), so it cannot be ended";
            }
            else if (!OutputTakeover.IsPatterns(sight.ExePath, w.ExePath))
            {
                fence = $"pid {w.Pid} is up with {main}'s start time but is not Patterns ({sight.ExePath}), so it is not ended";
            }
            else if (Probe.Kill(w.Pid))
            {
                notes.Add($"ended {main}'s process (pid {w.Pid}) — it had stopped answering");
            }
            else
            {
                fence = $"{main}'s process (pid {w.Pid}) is still up and could not be ended";
            }
        }

        if (fence is not null && !force)
        {
            tx.Stop(fence);
            return RefuseTakeOver(fence, marked, origin);
        }
        if (fence is not null) notes.Add(fence + " — taken over anyway");

        // The wall switch — the room's own fence, between two machines — fires before a single
        // output opens here, so the room is looking at this machine by the time its picture is
        // up. Taken by itself from another machine, a cue that could not fire refuses the
        // takeover: the room could not be told which desk to show, so no desk is changed; the
        // next try comes after the usual pause. A press goes ahead and carries the failure in its
        // words — the operator can switch the wall by hand.
        var wall = FireWallSwitch(_kernel.State.Twin.TakeOverCue);
        if (tx.HasRoute && !localMain) tx.Reached(HandoverStage.RouteRequested);
        if (!wall.Ok && !localMain && origin.Kind == OriginKind.Recovery && !force)
        {
            var why = $"the wall switch cue '{_kernel.State.Twin.TakeOverCue}' could not fire ({wall.Reason}), so the room could not be told to show this desk";
            tx.Stop(why);
            return RefuseTakeOver(why, marked, origin);
        }
        if (wall.Ok && wall.Receipts is null && tx.HasRoute && !localMain && origin.Kind == OriginKind.Recovery && !force)
        {
            // The fence said the cue had a box that answers; by fire time it had none: sent is not switched.
            var why = $"the wall switch cue '{_kernel.State.Twin.TakeOverCue}' fired but sent nothing a box confirms, so the room may not be showing this desk";
            tx.Stop(why);
            return RefuseTakeOver(why, marked, origin);
        }
        if (!tx.HasRoute && !localMain) notes.Add("no wall-switch cue — switch the room to this desk by hand");
        if (wall.Ok && wall.Receipts is { } receipts && !localMain)
        {
            // The switcher answers on its own time: the outputs open here once it has said yes. By
            // itself, a switch that said no or nothing refuses the takeover and the hold stays; a
            // press goes on and carries the answer in its words.
            _handoverBusy = true;
            var wallWords = wall.Words;
            _ = receipts.ContinueWith(t => UiThread.Post(() =>
            {
                _handoverBusy = false;
                if (_phase == TwinPhase.TookOver || _role != TwinRole.Standby) return;
                var (ok, said) = ReadReceipts(t);
                if (!ok && origin.Kind == OriginKind.Recovery && !force)
                {
                    var why = $"the wall switch cue '{_kernel.State.Twin.TakeOverCue}' was not confirmed ({said}), so the room may not be showing this desk";
                    tx.Stop(why);
                    _nextAutoTakeOverUtc = Clock() + TwinWatch.RetryAfterRefusal;
                    RefuseTakeOver(why, marked, origin);
                    return;
                }
                if (!ok) notes.Add($"the wall switch was not confirmed ({said}) — taken over anyway; switch the wall by hand");
                tx.Reached(HandoverStage.RouteConfirmed);
                _services.Notify(CompleteTakeOver(tx, main, notes, ok ? wallWords + " " + said : wallWords, origin, now));
            }), TaskScheduler.Default);
            return ActionResult.Requested($"Taking over from {main}: the wall switch was asked — the outputs open here once it answers.");
        }
        if (tx.HasRoute && !localMain)
        {
            // A press carries a failed switch, or one no box vouched for, in its words and goes on: the operator switches by hand.
            tx.Reached(HandoverStage.RouteConfirmed);
            if (wall.Ok && wall.Receipts is null)
            {
                tx.Note("no box answered — the operator's press");
                notes.Add($"the wall switch cue '{_kernel.State.Twin.TakeOverCue}' sent nothing a box confirms — switch the room to this desk by hand if it did not");
            }
            else if (!wall.Ok)
            {
                tx.Note("the cue could not fire — the operator's press");
            }
        }
        return ActionResult.Done(CompleteTakeOver(tx, main, notes, wall.Words, origin, now));
    }

    /// <summary>The show is this desk's: the link closed, the phase moved, the hold lifted, the air record put back — and said.</summary>
    private string CompleteTakeOver(TwinTransaction tx, string main, List<string> notes, string wallWords, ActionOrigin origin, DateTime now)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource(); // the redial never restarts on its own
        CloseLink();
        _phase = TwinPhase.TookOver;
        _note = $"at {now.ToLocalTime():HH:mm:ss}";
        _services.OutputsHeldBy = "";
        tx.Reached(HandoverStage.AuthorityCommitted);
        // The beat goes on: this desk keeps dialling, so the main, once it is back, can take the show back over the link.
        StartBeating(_cts);
        _lastDialUtc = DateTime.MinValue;
        var head = $"TOOK OVER from {main} {_note}" + (notes.Count > 0 ? " — " + string.Join(", ", notes) : "") + ".";
        _services.RecoverFromTwin(_mirroredAir, head);
        tx.Reached(HandoverStage.TargetReady);
        tx.Reached(HandoverStage.Complete);
        Log.Warn($"Twin: {head} ({origin.Label}) — {tx.Trail}");
        return wallWords.Length > 0 ? head + " " + wallWords : head;
    }

    /// <summary>A takeover a fence stopped: the marker taken back (one that says this desk has the show would be a lie), the reason on the line, said once, and refused.</summary>
    private ActionResult RefuseTakeOver(string fence, bool marked, ActionOrigin origin)
    {
        if (marked) TwinHandover.Clear(_kernel.Store.BaseDirectory);
        var refusal = $"Not taken over: {fence}. The outputs stay held closed — TAKE OVER ANYWAY (Machine page, TWIN) or TWIN TAKEOVER FORCE overrides, by hand only.";
        var first = _note != "not taken over: " + fence;
        _note = "not taken over: " + fence;
        Log.Warn($"Twin: {refusal} ({origin.Label})");
        if (first) _services.Notify(refusal);
        return ActionResult.Refused(refusal);
    }

    /// <summary>After a takeover: the outputs held again, the link dialled again, the show mirrored again.</summary>
    public ActionResult StandByAgain(ActionOrigin origin)
    {
        if (_role != TwinRole.Standby) return ActionResult.Refused("This desk is not a standby twin — Machine page, TWIN.");
        if (_phase != TwinPhase.TookOver) return ActionResult.Done("This desk is standing by already.");
        _services.OutputsHeldBy = "this desk is the standby twin";
        if (_services.OutputsLive) _services.CloseOutputs();
        try
        {
            TwinHandover.Clear(_kernel.Store.BaseDirectory);
        }
        catch (Exception)
        {
            // nothing marked
        }
        CloseLink(); // a link dialled after the takeover said TookOver: the next join says standby
        _phase = TwinPhase.Connecting;
        _note = "";
        _lastHeardUtc = null;
        _mirroredAir = null;
        _lastDialUtc = DateTime.MinValue;
        Log.Info($"Twin: standing by again for {_mainName} ({origin.Label}).");
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        StartBeating(_cts);
        Dial();
        return ActionResult.Done($"Standing by again — the outputs are held closed and the link to {(_mainName.Length > 0 ? _mainName : "the main")} is being dialled.");
    }
}
