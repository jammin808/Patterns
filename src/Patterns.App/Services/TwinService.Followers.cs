using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The followers — callers and stage timers — on both sides: a caller's own edits sent as the
/// sections they landed in, the plan offered and applied, the queue that waits for DISARM, the
/// live word, a caller's line on the main, and LINK and UNLINK on the node.
/// </summary>
public sealed partial class TwinService
{
    /// <summary>
    /// A caller's own edit — a cue, a note, the pad — goes to the desk as the section it landed in.
    /// A section that is exactly what landed here from the desk is the desk's own edit coming
    /// back through the publish, not the caller's, and stays where it is.
    /// </summary>
    private void SendMyEdits(HashSet<string>? dirty)
    {
        if (_stream is null || _phase != TwinPhase.InStep) return;
        var sections = dirty is null ? TwinSync.CallerSections : TwinSync.CallerSections.Where(dirty.Contains).ToList();
        foreach (var section in sections)
        {
            string json;
            try
            {
                json = TwinSync.SectionJson(_kernel.State, section);
            }
            catch (Exception ex)
            {
                Log.Warn($"Twin: the caller's {section} could not be written.", ex);
                continue;
            }
            if (_lastLanded.TryGetValue(section, out var landed) && landed == json) continue;
            _lastLanded[section] = json;
            var line = TwinMessage.Format(TwinWord.Section, json, section);
            _ = Task.Run(() => TryWriteToMain(line));
        }
    }

    /// <summary>A caller's verb to the desk: GO, STANDBY, HOLD, a message to stage — run there, journaled there as the caller's.</summary>
    public ActionResult Forward(ShowAction action, ActionOrigin origin)
    {
        if (!IsLinkedToDesk) return ActionResult.Refused("Not linked to a desk — LINK on the Nodes page.");
        var line = TwinMessage.Format(TwinWord.Act, JsonUtil.SerializeCompact(action));
        var sent = TryWriteToMain(line);
        return sent
            ? ActionResult.Requested($"{ActionSpec.Label(action.Kind)} — sent to {_mainName}.")
            : ActionResult.Failed($"{ActionSpec.Label(action.Kind)} could not be sent to {_mainName}.");
    }

    /// <summary>OFFER PLAN on a caller: the cues as they stand here, to the desk's Nodes page, for APPLY there.</summary>
    public ActionResult OfferPlan()
    {
        if (_kernel.Profile != NodeKind.Caller) return ActionResult.Refused("Only a caller node offers a plan.");
        if (_stream is null) return ActionResult.Refused("Not linked to a desk.");
        var json = CuePlan.Json(_kernel.State);
        var ok = TryWriteToMain(TwinMessage.Format(TwinWord.Plan, json));
        _planOffered = true;
        return ok ? ActionResult.Done($"Plan offered to {_mainName}: {CuePlan.Count(_kernel.State.Stacks)} — APPLY is the desk's press.") : ActionResult.Failed("The plan could not be sent.");
    }

    /// <summary>The whole show landed here: the caller's sections as the desk sent them, so the publish that follows is not sent back as an edit.</summary>
    private void RememberLanded(string showJson)
    {
        if (_kernel.Profile != NodeKind.Caller) return;
        try
        {
            var incoming = JsonUtil.Deserialize<ShowState>(showJson);
            if (incoming is null) return;
            foreach (var section in TwinSync.CallerSections) _lastLanded[section] = TwinSync.SectionJson(incoming, section);
        }
        catch (Exception)
        {
            // a show this build cannot read lands nowhere either
        }
    }

    private void HookLive()
    {
        if (_liveHooked || _services.CueStack is null) return;
        _liveHooked = true;
        _services.CueStack.Changed += SendLive;
        EnsureStackHook();
    }

    /// <summary>The desk's stack is the operator's while it is armed: a caller's plan or edit waits, and lands on DISARM.</summary>
    private bool StackArmed => _services.CueStack?.Armed ?? false;

    /// <summary>How many of the callers' edits wait for DISARM — the Nodes page's and the test's.</summary>
    public int QueuedEdits => _queuedSections.Count;

    /// <summary>
    /// The stack's Changed watched for the DISARM that lands what waited — hooked when the stack is
    /// there (the desk builds its cue stack after its twin) and again wherever something is queued,
    /// so nothing waits on a hook that is not there. The check is idempotent: not armed and
    /// something waiting is the whole condition, whatever fired the change.
    /// </summary>
    private void EnsureStackHook()
    {
        if (_stackHooked || _services.CueStack is not { } stack) return;
        _stackHooked = true;
        stack.Changed += OnStackChanged;
    }

    private void OnStackChanged()
    {
        if (_landingQueued || StackArmed) return;
        if (_queuedSections.Count == 0 && !Plans.Any(p => p.Queued)) return;
        LandQueued();
    }

    /// <summary>The desk edited a section a caller's edit waits on: the desk's is the newer, so the waiting one is set aside, and said.</summary>
    private void SetAsideQueued(string? section)
    {
        var stale = _queuedSections.Keys.Where(k => section is null || k.Section == section).ToList();
        if (stale.Count == 0) return;
        foreach (var key in stale)
        {
            var (caller, _) = _queuedSections[key];
            _queuedSections.Remove(key);
            var words = $"The caller {caller}'s waiting edit to the cues was set aside: this desk edited them since.";
            _kernel.Journal.Record($"caller {caller}", "SectionQueued", key.Section, "Refused", words);
            _services.Notify(words);
        }
    }

    /// <summary>DISARM: the callers' edits that waited land first, then the plans that waited — an APPLY is the operator's press, and wins over an edit that arrived on its own.</summary>
    private void LandQueued()
    {
        _landingQueued = true;
        try
        {
            LandQueuedNow();
        }
        finally
        {
            _landingQueued = false;
        }
    }

    private void LandQueuedNow()
    {
        var callers = new List<string>();
        var waited = _queuedSections.ToList();
        _queuedSections.Clear();
        foreach (var ((instance, section), (caller, json)) in waited)
        {
            var ok = false;
            _landing = instance;
            try
            {
                _services.BulkEdit(() => ok = TwinSync.ApplySection(_kernel.State, section, json));
            }
            finally
            {
                _landing = null;
            }
            if (ok) callers.Add(caller);
            else Log.Warn($"Twin: the caller {caller}'s {section}, kept while the stack was armed, could not land.");
        }
        if (callers.Count > 0)
        {
            var words = $"DISARM: the edits that waited from {string.Join(", ", callers.Distinct())} landed.";
            _kernel.Journal.Record("twin", "SectionQueued", "", "Done", words);
            _services.Notify(words);
        }
        foreach (var offer in Plans.Where(p => p.Queued).ToList())
        {
            var landed = ApplyPlan(offer with { Queued = false });
            Log.Info($"Twin: on DISARM — {landed.Message}");
        }
    }

    /// <summary>The caller's stack as it runs here, to every caller on the link — once a second from the tick, at once on a change.</summary>
    private void SendLive()
    {
        if (_services.CueStack is null) return;
        List<Standby> callers;
        lock (_gate)
        {
            callers = _standbys.Where(s => s.IsFollower).ToList();
        }
        if (callers.Count == 0) return;
        string line;
        try
        {
            var stack = _services.CueStack;
            var rt = stack.Runtime;
            var live = new TwinLive(rt.StandbyCueId ?? "", rt.LastCueId ?? "", rt.Armed, rt.Hold, rt.Executing, _services.AirLabel, _services.OutputsLive, _kernel.State.Blackout, stack.Timing().OffsetText, Interlocked.Increment(ref _liveSeq));
            line = TwinMessage.Format(TwinWord.Live, live.ToJson());
        }
        catch (Exception ex)
        {
            Log.Warn("Twin: the live word could not be written.", ex);
            return;
        }
        _ = Task.Run(() => { foreach (var c in callers) if (!c.TryWrite(line)) Drop(c); });
    }

    /// <summary>A line from a caller on the link (UI thread): its edit lands, its verb runs, its plan is kept for APPLY.</summary>
    private void OnCallerLine(Standby caller, TwinMessage msg)
    {
        switch (msg.Word)
        {
            case TwinWord.Section:
            {
                if (!caller.IsCaller || !TwinSync.IsCallerSection(msg.Name))
                {
                    Log.Warn($"Twin: the {(caller.IsCaller ? "caller" : "timer")} {caller.Name} sent '{msg.Name}', which it does not own — ignored.");
                    return;
                }
                if (StackArmed && !CuePlan.SameShape(_kernel.State.Stacks, msg.Payload))
                {
                    // The stack is the operator's while it is armed: a note, the pad, a cue's own words
                    // land as ever — the caller's live notes are the show — but a cue added, removed or
                    // moved waits, the last such edit from this caller, and lands on DISARM. A desk that
                    // edits the stack meanwhile wins: its edit goes out as its own, and the waiting one
                    // is set aside, said.
                    EnsureStackHook();
                    var first = !_queuedSections.ContainsKey((caller.Instance, msg.Name));
                    _queuedSections[(caller.Instance, msg.Name)] = (caller.Name, msg.Payload);
                    if (first)
                    {
                        var words = $"The caller {caller.Name}'s edit to the cues waits: the stack is armed — it lands on DISARM.";
                        _kernel.Journal.Record($"caller {caller.Name}", "SectionQueued", msg.Name, "Requested", words);
                        _services.Notify(words);
                    }
                    return;
                }
                var ok = false;
                _landing = caller.Instance;                                           // its own edit is not sent back to it
                try
                {
                    _services.BulkEdit(() => ok = TwinSync.ApplySection(_kernel.State, msg.Name, msg.Payload));
                }
                finally
                {
                    _landing = null;
                }
                if (!ok) Log.Warn($"Twin: the caller {caller.Name}'s {msg.Name} could not land.");
                break;
            }
            case TwinWord.Act:
            {
                ShowAction action;
                try
                {
                    action = JsonUtil.Deserialize<ShowAction>(msg.Payload);
                }
                catch (Exception)
                {
                    Log.Warn($"Twin: the caller {caller.Name} sent a verb this build could not read.");
                    return;
                }
                if (action.Kind == ShowActionKind.Unknown) return;
                _services.Actions.Execute(action, new ActionOrigin(OriginKind.Caller, caller.Name));
                break;
            }
            case TwinWord.Plan:
            {
                if (!caller.IsCaller) return;                                          // a plan is a caller's to offer
                var plan = CuePlan.Parse(msg.Payload);
                if (plan is null)
                {
                    Log.Warn($"Twin: the caller {caller.Name} sent a plan this build could not read.");
                    return;
                }
                var diff = CuePlan.Diff(_kernel.State.Stacks, plan);
                var existing = Plans.FirstOrDefault(p => p.Instance == caller.Instance);
                if (existing is not null) Plans.Remove(existing);
                Plans.Add(new PlanOffer(caller.Instance, caller.Name, msg.Payload, diff.Words, CuePlan.Count(plan), diff.IsEmpty));
                var words = diff.IsEmpty
                    ? $"Nodes: the caller {caller.Name} brought {CuePlan.Count(plan)} — the same cues this desk has."
                    : $"Nodes: the caller {caller.Name} brought a plan — {diff.Words}. APPLY on the Nodes page lands it.";
                Log.Info("Twin: " + words);
                _services.Notify(words);
                break;
            }
        }
    }

    /// <summary>
    /// APPLY on the desk's Nodes page: a version of the show kept first, then the caller's stacks onto
    /// this desk's — mirrored on to everyone on the link. While the desk's stack is armed the plan
    /// waits instead: a stack replaced under the operator's hands can take the standby cue with it,
    /// and GO would then refuse mid-show. Kept on the page, landed on DISARM, or set aside.
    /// </summary>
    public ActionResult ApplyPlan(PlanOffer offer)
    {
        var plan = CuePlan.Parse(offer.Json);
        if (plan is null) return ActionResult.Refused("That plan could not be read.");
        var stored = Plans.FirstOrDefault(p => p.Instance == offer.Instance);
        if (StackArmed)
        {
            EnsureStackHook();
            var queued = offer with { Queued = true };
            if (stored is not null) Plans[Plans.IndexOf(stored)] = queued;
            else Plans.Add(queued);
            var waits = $"The caller {offer.Caller}'s plan waits: the stack is armed — it lands on DISARM, or ✕ sets it aside.";
            _kernel.Journal.Record($"caller {offer.Caller}", "PlanQueued", "", "Requested", waits);
            _services.Notify(waits);
            return ActionResult.Requested(waits);
        }
        _services.SaveNow();                                                         // the show as it was, kept as a version
        var stacks = 0;
        _services.BulkEdit(() => stacks = CuePlan.Merge(_kernel.State, plan));
        if (stored is not null) Plans.Remove(stored);
        var words = $"The caller {offer.Caller}'s plan landed: {offer.Count}, {stacks} stack{(stacks == 1 ? "" : "s")} — the show as it was is under EARLIER VERSIONS.";
        _kernel.Journal.Record($"caller {offer.Caller}", "PlanApply", "", "Done", words);
        _services.Notify(words);
        return ActionResult.Done(words);
    }

    public void DismissPlan(PlanOffer offer)
    {
        var stored = Plans.FirstOrDefault(p => p.Instance == offer.Instance);
        if (stored is not null) Plans.Remove(stored);
    }

    /// <summary>A caller: the desk's stack as it runs there onto this desk's runtime, so the Run surface here reads the desk's standby, ARM and HOLD.</summary>
    private void AdoptLive(string json)
    {
        var live = TwinLive.Parse(json);
        if (live is null) return;
        _live = live;
        var stack = CueStacks.Caller(_kernel.State);
        var rt = _kernel.Cues.For(stack);
        _services.DeskEdit(() =>
        {
            rt.StandbyCueId = live.Standby.Length > 0 ? live.Standby : null;
            rt.LastCueId = live.Last.Length > 0 ? live.Last : null;
            rt.Armed = live.Armed;
            rt.Hold = live.Hold;
            rt.Executing = live.Executing;
        });
    }
    /// <summary>LINK on a caller node's Nodes page: follow that desk — its address and link port onto the twin settings; the link dials on the publish.</summary>
    public string LinkTo(NodeCard card)
    {
        if (!IsFollowerNode) return "Only a caller or a stage timer node links to a desk this way — the desk's own twin is set on the Machine page.";
        if (card.Kind != NodeKind.Desk) return $"{card.KindLabel} {card.Name} is not a desk to follow.";
        if (card.Address is null || card.LinkPort <= 0) return $"{card.Name} is not linking callers (its Nodes page: Accept caller nodes).";
        var cfg = _kernel.State.Twin;
        _services.BulkEdit(() =>
        {
            cfg.MainHost = card.Address.ToString();
            cfg.Port = card.LinkPort;
        });
        return $"Linking to {card.Name} at {card.Address}:{card.LinkPort} — if the desk refuses, its key goes on this node's Machine tab (the desk shows it on its Machine page, TWIN).";
    }

    /// <summary>UNLINK on a caller node: leave the desk and plan on with the show as it stands here.</summary>
    public string Unlink()
    {
        if (!IsFollowerNode) return "Not a caller or a stage timer node.";
        _services.BulkEdit(() => _kernel.State.Twin.MainHost = "");
        _kernel.Clock.Reset();
        return _kernel.Profile == NodeKind.Timer ? "Unlinked — the clock is this node's own again." : "Unlinked — planning on with the show as it stands here.";
    }

    private static long ProcessStartTicks()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }
}
