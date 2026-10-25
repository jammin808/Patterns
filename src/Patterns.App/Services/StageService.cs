using System.Globalization;
using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The stage: a professional stage timer and messages to stage on the countdown's own clock — one
/// clock for the wall, the confidence screen, the stage page and a caller node. The timer's verbs
/// (pause with what is left kept, resume, seconds either way, a flash), the messages to the
/// speaker's display or the crew's with a receipt when the display's ACK is pressed, and the
/// payload the stage and timer pages long-poll.
/// </summary>
public sealed class StageService
{
    public const int MessagesKept = 50;

    private readonly IStageHost _s;
    private long _rev;
    private bool _hooked;

    public StageService(IStageHost host)
    {
        _s = host;
        UtcNow = () => host.Clock.UtcNow;      // the desk's frame: on a follower the link's measured offset is in it
        _s.SnapshotPublished += Bump;           // the countdown or the stage section moved: the pages read again
    }

    /// <summary>The clock the pages and the tests read; the tests move it.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>Bumped on every change the pages should see; the long-poll waits on it.</summary>
    public long Rev => Interlocked.Read(ref _rev);

    public void Bump() => Interlocked.Increment(ref _rev);

    private void Hook()
    {
        if (_hooked || _s.CueStack is null) return;
        _hooked = true;
        _s.CueStack.Changed += Bump;            // the segment follows the stack
    }

    private DateTime LocalNow => UtcNow().ToLocalTime();

    /// <summary>
    /// The timer as it stands: a linked Stagewatch's time while it answers (round 87 — one room, one timer), else this
    /// desk's countdown on its own clock. A Stagewatch gone quiet holds the time back (idle) rather than freezing a
    /// number on the speaker's screen; past the drop it is forgotten and this desk's own timer runs.
    /// </summary>
    public StageTime Time()
    {
        var follow = _s.FollowedStagewatch;
        if (follow.Frame is { } frame && follow.State != ShowBusState.Gone)
            return follow.State == ShowBusState.Live ? StageFollow.Time(frame, follow.FrameAtSeconds, ShowClock.Seconds) : StageTime.Idle;
        return StageTimer.Evaluate(_s.AirState.Countdown, _s.AirState.Stage, LocalNow, UtcNow());
    }

    /// <summary>Round 87: the Stagewatch the stage follows, by name — "" when it runs its own.</summary>
    public string Following => _s.FollowedStagewatch is { Frame: not null, State: not ShowBusState.Gone } f ? f.Name : "";

    /// <summary>Round 87: the refusal for a verb that would move this desk's timer while it follows a Stagewatch's.</summary>
    private ActionResult? FollowingRefusal(string verb) => Following is { Length: > 0 } name
        ? ActionResult.Refused($"The stage timer follows Stagewatch {name} — {verb} it there (Nodes page: the link).")
        : null;

    public ActionResult Pause()
    {
        if (FollowingRefusal("pause") is { } refused) return refused;
        var ok = false;
        _s.EditAir(air => ok = StageTimer.Pause(air.Countdown, air.Stage, LocalNow, UtcNow()));
        Bump();
        return ok ? ActionResult.Done($"Stage timer paused at {Time().Text}.") : ActionResult.Refused("No timer is running to pause.");
    }

    public ActionResult Resume()
    {
        if (FollowingRefusal("resume") is { } refused) return refused;
        var ok = false;
        _s.EditAir(air => ok = StageTimer.Resume(air.Countdown, air.Stage, UtcNow()));
        Bump();
        return ok ? ActionResult.Done($"Stage timer running: {Time().Text}.") : ActionResult.Refused("Nothing is paused.");
    }

    /// <summary>"+60", "-30", "2m": seconds onto what is left.</summary>
    public ActionResult Add(string words)
    {
        if (FollowingRefusal("add time to") is { } refused) return refused;
        var seconds = StageTimer.ParseSeconds(words);
        if (seconds is null) return ActionResult.Refused($"'{words}' is not a number of seconds (+60, -30, 2m).");
        var ok = false;
        _s.EditAir(air => ok = StageTimer.Add(air.Countdown, air.Stage, seconds.Value, LocalNow, UtcNow()));
        Bump();
        return ok ? ActionResult.Done($"{(seconds >= 0 ? "+" : "")}{seconds:0} s — the stage timer reads {Time().Text}.") : ActionResult.Refused("No timer is running to add to.");
    }

    /// <summary>The displays flash for three seconds — "look up" — with no message.</summary>
    public ActionResult Flash()
    {
        _s.EditAir(air => air.Stage.FlashUntilUtc = UtcNow().AddSeconds(3));
        Bump();
        return ActionResult.Done("The stage displays flash.");
    }

    /// <summary>Words to the speaker's display (or the crew's), with a receipt to come.</summary>
    public ActionResult Message(string channel, string text, string from, bool flash = true)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return ActionResult.Refused("A message to stage needs words.");
        var message = new StageMessage { Text = text, Channel = channel, From = from, SentUtc = UtcNow(), Flash = flash };
        _s.EditAir(air =>
        {
            air.Stage.Messages.Add(message);
            while (air.Stage.Messages.Count > MessagesKept) air.Stage.Messages.RemoveAt(0);
        });
        Bump();
        return ActionResult.Done($"To the {message.Channel}: '{text}' — a receipt comes when the display's ACK is pressed.");
    }

    /// <summary>The unseen messages off the displays: marked seen by the desk, so the receipts read "cleared".</summary>
    public ActionResult Clear()
    {
        var n = 0;
        var now = UtcNow();
        _s.EditAir(air =>
        {
            foreach (var m in air.Stage.Messages.Where(m => m.AckUtc is null)) { m.AckUtc = now; n++; }
            air.Stage.FlashUntilUtc = null;
        });
        Bump();
        return ActionResult.Done(n == 0 ? "Nothing on the displays to clear." : $"{n} message{(n == 1 ? "" : "s")} cleared from the displays.");
    }

    /// <summary>The display's ACK: the message is seen, and the desk knows when — the receipt's words, or null for a message that is not there to see.</summary>
    public string? Ack(string id)
    {
        StageMessage? found = null;
        _s.EditAir(air => found = air.Stage.Messages.FirstOrDefault(m => m.Id == id && m.AckUtc is null));
        if (found is null) return null;
        var now = UtcNow();
        _s.EditAir(_ => found.AckUtc = now);
        Bump();
        return $"Stage: '{found.Text}' seen at {now.ToLocalTime():HH:mm:ss}.";
    }

    /// <summary>The latest unseen message on a channel, or null.</summary>
    public StageMessage? Pending(string channel) => _s.AirState.Stage.Messages.LastOrDefault(m => m.Channel == channel && m.AckUtc is null);

    /// <summary>
    /// Round 89: the Stagewatch the stage follows says its show is stuck — the stage follows it and shows no time, so the
    /// words say why rather than "idle" or "no timer".
    /// </summary>
    private static bool FollowsAStuckShow(StagewatchSight f) => f is { StuckLive: true, Frame: not null };

    /// <summary>What a speaker's display says under its blank time: no timer, or why a followed Stagewatch's is not shown.</summary>
    public string NoTimeLabel => _s.FollowedStagewatch is var f && FollowsAStuckShow(f)
        ? $"No time shown — Stagewatch {f.Name} says its show is stuck"
        : "No timer running";

    /// <summary>The Countdown page's line: the timer, and what waits on a receipt.</summary>
    public string StatusLine
    {
        get
        {
            var follow = _s.FollowedStagewatch;
            var time = Time();
            string timer;
            if (FollowsAStuckShow(follow))
            {
                timer = $"Stage timer shows no time — following Stagewatch {follow.Name}, which says its show is stuck";
            }
            else
            {
                timer = time.Phase switch
                {
                    StageTimerPhase.Idle => "Stage timer idle",
                    StageTimerPhase.Paused => $"Stage timer PAUSED at {time.Text}",
                    StageTimerPhase.Over => $"Stage timer OVER by {StageTimer.Format(-time.RemainingSeconds)}",
                    _ => $"Stage timer {time.Text} ({time.Colour})",
                };
                if (Following is { Length: > 0 } name) timer += $" — following Stagewatch {name}";
                else if (follow.StuckLive) timer += " — " + StageFollow.Words(follow);               // its stall said before its first frame: this desk's own timer
            }
            var unseen = _s.AirState.Stage.Messages.Count(m => m.AckUtc is null);
            return unseen == 0 ? timer + "." : $"{timer} · {unseen} message{(unseen == 1 ? "" : "s")} waiting on a receipt.";
        }
    }

    /// <summary>STAGE STATUS, and the pages' payload: the timer, the segment, the clock, the messages with their receipts.</summary>
    /// <summary>The stage as a deck's key reads it: the timer in its own colour, the segment and the next, the messages waiting for their ACK, the flash — the block STATE carries.</summary>
    public object Block()
    {
        Hook();
        var air = _s.AirState;
        var time = Time();
        var stack = _s.CueStack;
        var running = stack?.LastCue;
        var next = stack?.StandbyCue;
        return new
        {
            timer = new
            {
                phase = time.Phase.ToString().ToLowerInvariant(),
                remaining = Math.Round(time.RemainingSeconds, 1),
                text = time.Text,
                colour = time.Colour,
                progress = Math.Round(time.Progress01, 4),
                label = air.Countdown.Label,
                paused = air.Stage.Paused,
            },
            segment = running is null ? "" : $"{running.Number} {running.Name}".Trim(),
            next = next is null ? "" : $"{next.Number} {next.Name}".Trim(),
            pendingSpeaker = Pending("speaker")?.Text ?? "",
            pendingCrew = Pending("crew")?.Text ?? "",
            flash = air.Stage.FlashUntilUtc is { } until && until > UtcNow(),
            follows = Following,                                                     // round 87: "" or the Stagewatch the timer follows
        };
    }

    /// <summary>What a deck watches for: the timer's phase and colour, the messages waiting — a change is a push.</summary>
    public string DeckSignature()
    {
        var time = Time();
        return $"{time.Phase}|{time.Colour}|{Pending("speaker")?.Id}|{Pending("crew")?.Id}|{Following}";
    }

    private object? FollowJson()
    {
        var f = _s.FollowedStagewatch;
        if (f.Name.Length == 0 && f.Address.Length == 0) return null;
        return new
        {
            name = f.Name,
            state = f.State.ToString().ToLowerInvariant(),
            words = StageFollow.Words(f),
            title = f.Frame?.Title ?? "",
            next = f.Frame?.Next ?? "",
            message = f.Frame?.Message ?? "",
            stuck = f.StuckLive,                                                     // round 89: it says its show is stuck — no time from it
        };
    }

    public string StatusJson()
    {
        Hook();
        var air = _s.AirState;
        var now = UtcNow();
        var time = Time();
        var stack = _s.CueStack;
        TimingReport? timing = null;
        try { timing = stack?.Timing(); } catch (Exception) { /* a plan that will not read is no segment */ }
        var running = stack?.LastCue;
        var next = stack?.StandbyCue;
        return JsonSerializer.Serialize(new
        {
            rev = Rev,
            serverUtc = now,
            show = air.Name,
            clock = now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            timer = new
            {
                phase = time.Phase.ToString().ToLowerInvariant(),
                remaining = Math.Round(time.RemainingSeconds, 1),
                text = time.Text,
                colour = time.Colour,
                progress = Math.Round(time.Progress01, 4),
                label = Following.Length > 0 ? _s.FollowedStagewatch.Frame?.Title ?? "" : air.Countdown.Label,
                paused = Following.Length > 0 ? time.Phase == StageTimerPhase.Paused : air.Stage.Paused,
                amber = air.Stage.AmberSeconds,
                red = air.Stage.RedSeconds,
            },
            flashUntilUtc = air.Stage.FlashUntilUtc,
            segment = new
            {
                name = running?.Name ?? "",
                number = running?.Number ?? "",
                remaining = timing?.RunningRemaining is { } left ? Math.Round(left.TotalSeconds) : (double?)null,
                overran = timing?.RunningOverran ?? false,
                next = next?.Name ?? "",
                nextNumber = next?.Number ?? "",
                nextAt = next?.PlannedStart ?? "",
                offset = timing?.OffsetText ?? "",
                armed = stack?.Armed ?? false,
            },
            sees = new { speakerSegment = air.Stage.SpeakerSeesSegment, crewSegment = air.Stage.CrewSeesSegment, speakerClock = air.Stage.SpeakerSeesClock },
            // Round 87: one room, one timer — when a Stagewatch is linked the timer above is its, and the pages say so.
            follows = FollowJson(),
            presets = air.Stage.Presets.ToArray(),
            messages = air.Stage.Messages.TakeLast(20).Select(m => new { id = m.Id, text = m.Text, channel = m.Channel, sentUtc = m.SentUtc, ackUtc = m.AckUtc, flash = m.Flash, from = m.From, seen = m.AckUtc is not null }).ToArray(),
            pendingSpeaker = Pending("speaker") is { } sp ? new { id = sp.Id, text = sp.Text, flash = sp.Flash } : null,
            pendingCrew = Pending("crew") is { } cr ? new { id = cr.Id, text = cr.Text, flash = cr.Flash } : null,
        });
    }
}
