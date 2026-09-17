using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// Which picture a mounted input is playing on. The programme, one output's own picture, or the
/// sandboxed preview — the three places a clip, a capture or a page can be at the same time.
/// </summary>
public readonly record struct MediaBus(bool Preview, string OutputId)
{
    /// <summary>The programme, and everything drawn as part of it — the inset, the lower third.</summary>
    public static readonly MediaBus Program = new(false, "");

    /// <summary>What the desk is building, before a CUT or a TAKE.</summary>
    public static readonly MediaBus Sandbox = new(true, "");

    /// <summary>One content target's own picture, by id.</summary>
    public static MediaBus Output(string targetId) => new(false, targetId ?? "");

    public bool IsProgram => !Preview && OutputId.Length == 0;
}

/// <summary>Where a mounted input's sound goes — the room's outputs, the operator's own, or nowhere.</summary>
public enum AudioDestination
{
    /// <summary>The programme's outputs: what the room hears.</summary>
    Program,

    /// <summary>The operator's own output, away from the room.</summary>
    Monitor,

    /// <summary>Nowhere. Mounted for its pictures, not for its sound.</summary>
    Silent,
}

/// <summary>
/// Round 77: which pictures are on a live output right now — the fact the sound follows.
///
/// The fault this exists for: a clip on the programme kept sounding on the room's output while
/// no live screen showed the programme — the desk's monitor had its outputs off and the one live
/// screen showed its own YouTube page — because the rule read "the programme's sound is the
/// room's" whether or not the programme was anywhere in the room. The room hears what it sees:
/// a picture's sound plays while some live output shows that picture, and fades when none does.
///
/// <see cref="OwnTargets"/> are the content targets (a screen, a canvas key) whose own picture is
/// on a live output; <see cref="ProgrammeLive"/> says some live output shows the programme (or
/// the programme leaves the machine another way — an NDI send, the stream); <see cref="AnyLive"/>
/// says any output is open at all. With no output open the desk hears the programme as it always
/// did — a rehearsal at the desk is not a silent one.
/// </summary>
public sealed record LiveOutputs(IReadOnlySet<string> OwnTargets, bool ProgrammeLive, bool AnyLive)
{
    private static readonly IReadOnlySet<string> Nobody = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// What the callers that do not know the outputs assume: the programme is shown, no screen's
    /// own picture is — exactly the rule as it stood before this round, so a paper caller is not
    /// changed by it.
    /// </summary>
    public static readonly LiveOutputs AssumeAll = new(Nobody, true, true);

    /// <summary>No output open.</summary>
    public static readonly LiveOutputs None = new(Nobody, false, false);

    /// <summary>Whether a mount on this bus is being shown to the room: never the preview; the programme when it is live; an own picture when its target is.</summary>
    public bool Shows(in MediaBus bus)
    {
        if (bus.Preview) return false;
        return bus.IsProgram ? ProgrammeLive : OwnTargets.Contains(bus.OutputId);
    }

    /// <summary>
    /// From the live targets as the window manager names them (a screen id, or a canvas key for a
    /// joined canvas) read against the on-air show: a target with its own picture is an own
    /// target, any other shows the programme. <paramref name="programmeLeavesOtherwise"/> is the
    /// NDI send or the stream carrying the programme out of the machine.
    /// </summary>
    public static LiveOutputs Of(ShowState air, IEnumerable<string> liveTargets, bool programmeLeavesOtherwise = false)
    {
        var own = new HashSet<string>(StringComparer.Ordinal);
        var programme = programmeLeavesOtherwise;
        var any = false;
        foreach (var target in liveTargets)
        {
            any = true;
            if (target.Length > 0 && ContentTargets.UsesOwnPattern(air, target)) own.Add(target);
            else programme = true;
        }
        return new LiveOutputs(own, programme, any || programmeLeavesOtherwise);
    }

    /// <summary>A stable text of the facts — the audio graph's topology signature reads it.</summary>
    public string Signature => (ProgrammeLive ? "pgm" : "-") + "|" + (AnyLive ? "live" : "-") + "|" + string.Join(",", OwnTargets.OrderBy(t => t, StringComparer.Ordinal));

    /// <summary>"the programme and 2 screens' own pictures are on live outputs" — STATE and the Audio page.</summary>
    public string Words()
    {
        if (!AnyLive) return "no output is open";
        var own = OwnTargets.Count switch { 0 => "", 1 => "1 screen's own picture", _ => $"{OwnTargets.Count} screens' own pictures" };
        return ProgrammeLive
            ? own.Length > 0 ? $"the programme and {own} are on live outputs" : "the programme is on the live outputs"
            : own.Length > 0 ? $"{own} on the live outputs; the programme is on none" : "the live outputs show no picture with sound";
    }
}

/// <summary>
/// What the desk's own speakers play.
///
/// The fault this exists for: a clip on the programme, another on a confidence screen's own
/// picture and a third loaded into the preview are three decoders, and every one of them opens an
/// audio output. Played together they are a mix nobody chose, over the top of whatever the
/// operator is actually trying to hear — and the more carefully a show is built, the worse it
/// gets, because the busier the rig the more clips are mounted at once.
///
/// So the desk monitors one of them at a time, and by default it is the programme: the sound the
/// room is hearing is the sound the desk should be checking against.
///
/// The first cut of this said "nothing here reaches an output — a clip muted at the desk is still
/// heard by the audience". That was wrong, and the comment is why it took a round to notice: every
/// sound-maker in the build opens the same default endpoint, so on a one-interface rig the desk's
/// speakers ARE the PA and muting a programme clip to audition another picture took the room's
/// sound with it. What makes the sentence true is the thing this round adds — a monitor output of
/// the operator's own — and, until there is one, the rule refuses rather than pretends: the
/// programme's mounts are never silenced by a monitor pick, and anything else has nowhere to go.
/// </summary>
public static class AudioMonitorRule
{
    /// <summary>The monitor choice as the rule reads it, with the output already resolved.</summary>
    public readonly record struct MonitorPick(AudioMonitor Source, string OutputId);

    /// <summary>
    /// What the choice actually means right now. An output that has no picture of its own is
    /// showing the programme, so listening to it is listening to the programme — otherwise picking
    /// a screen that simply follows the show would be silence, which reads as a fault.
    /// </summary>
    public static MonitorPick Effective(ShowState state)
    {
        var monitor = state.Monitor;
        if (monitor.Source != AudioMonitor.Output) return new MonitorPick(monitor.Source, "");
        if (monitor.OutputId.Length == 0) return new MonitorPick(AudioMonitor.Program, "");
        return ContentTargets.UsesOwnPattern(state, monitor.OutputId)
            ? new MonitorPick(AudioMonitor.Output, monitor.OutputId)
            : new MonitorPick(AudioMonitor.Program, "");
    }

    /// <summary>True when a mount on these buses is on the programme — the sound the room hears.</summary>
    public static bool OnProgram(IReadOnlyList<MediaBus>? buses)
    {
        if (buses is null || buses.Count == 0) return true;   // nothing said: the programme's
        foreach (var bus in buses)
        {
            if (bus.IsProgram) return true;
        }
        return false;
    }

    /// <summary>True when a mount on these buses is the one the operator asked to listen to.</summary>
    public static bool Monitored(in MonitorPick pick, IReadOnlyList<MediaBus>? buses)
    {
        if (pick.Source is AudioMonitor.Silent or AudioMonitor.Program) return false;
        if (buses is null) return false;
        foreach (var bus in buses)
        {
            var heard = pick.Source switch
            {
                AudioMonitor.Preview => bus.Preview,
                _ => !bus.Preview && bus.OutputId.Length > 0 && bus.OutputId == pick.OutputId,
            };
            if (heard) return true;
        }
        return false;
    }

    /// <summary>
    /// Where this mount's sound goes.
    ///
    /// The programme's sound is the room's, and the room's sound is not the desk's to take away:
    /// a mount on the programme bus goes to the programme's outputs whatever the operator has
    /// their headphones on. That is the correction this round makes — monitoring the preview used
    /// to silence the programme's clip, and because every sound-maker in the build opens the same
    /// default endpoint, silencing it at the desk silenced it in the room.
    ///
    /// Anything else goes to the operator's own output when they asked for it and there is one.
    /// With no monitor output named there is nowhere for it to go that is not the room, so it
    /// stays silent and the readout says why.
    /// </summary>
    public static AudioDestination Where(in MonitorPick pick, IReadOnlyList<MediaBus>? buses, bool hasMonitorDevice)
        => Where(pick, buses, hasMonitorDevice, LiveOutputs.AssumeAll);

    /// <summary>True when some live output shows a picture this mount is on (nothing said: the programme's).</summary>
    public static bool ShownLive(IReadOnlyList<MediaBus>? buses, LiveOutputs live)
    {
        if (buses is null || buses.Count == 0) return live.ProgrammeLive;
        foreach (var bus in buses)
        {
            if (live.Shows(bus)) return true;
        }
        return false;
    }

    /// <summary>
    /// Round 77: where this mount's sound goes, read against what the room can see.
    ///
    /// Shown on a live output — the programme on a screen that follows it, a screen's own picture
    /// on that screen — it plays to the programme's outputs: the room hears what it sees, and a
    /// screen's own picture is heard where it is shown rather than muted for being nobody's
    /// audition. On no live output it is not the room's: with no output open at all the programme
    /// is still heard at the desk (a rehearsal is not silent); otherwise the operator's own output
    /// carries it when they asked to listen to it — or to the programme — and there is one; else
    /// it is silent, and the words say why. The operator's own mute and volume still apply on top.
    /// </summary>
    public static AudioDestination Where(in MonitorPick pick, IReadOnlyList<MediaBus>? buses, bool hasMonitorDevice, LiveOutputs live)
    {
        if (ShownLive(buses, live)) return AudioDestination.Program;
        var programme = OnProgram(buses);
        if (programme && !live.AnyLive) return AudioDestination.Program;
        if (hasMonitorDevice && (Monitored(pick, buses) || (programme && pick.Source == AudioMonitor.Program))) return AudioDestination.Monitor;
        return AudioDestination.Silent;
    }

    /// <summary>The buses of this mount that a live output shows — what the routing matrix carries its tap for. With no output open, every bus (the desk hears the programme); the preview never.</summary>
    public static IReadOnlyList<MediaBus> LiveBuses(IReadOnlyList<MediaBus>? buses, LiveOutputs live)
    {
        if (buses is null || buses.Count == 0) buses = new[] { MediaBus.Program };
        if (!live.AnyLive) return buses;
        var shown = new List<MediaBus>(buses.Count);
        foreach (var bus in buses)
        {
            if (live.Shows(bus)) shown.Add(bus);
        }
        return shown;
    }

    /// <summary>
    /// The input as the decoder should play it: where its sound goes, and silent when that is
    /// nowhere. The operator's own mute and volume still apply on top — this only ever takes sound
    /// away, and never from the room.
    /// </summary>
    public static MediaLocator.WantedInput Apply(in MonitorPick pick, MediaLocator.WantedInput input, bool hasMonitorDevice)
        => Apply(pick, input, hasMonitorDevice, LiveOutputs.AssumeAll);

    /// <summary>Round 77: the input routed against the live outputs; a mute the rule adds is marked as its own (<see cref="MediaLocator.WantedInput.RuleMuted"/>), so the decoder fades rather than cuts and lifts it itself.</summary>
    public static MediaLocator.WantedInput Apply(in MonitorPick pick, MediaLocator.WantedInput input, bool hasMonitorDevice, LiveOutputs live)
    {
        var where = Where(pick, input.Buses, hasMonitorDevice, live);
        return where == AudioDestination.Silent
            ? input with { Mute = true, RuleMuted = !input.Mute, Destination = where }
            : input with { Destination = where, RuleMuted = false };
    }

    /// <summary>The whole list, routed as the callers before round 77 asked: the programme assumed shown.</summary>
    public static List<MediaLocator.WantedInput> Apply(ShowState state, List<MediaLocator.WantedInput> inputs)
        => Apply(state, inputs, LiveOutputs.AssumeAll);

    /// <summary>The whole list, routed against the live outputs.</summary>
    public static List<MediaLocator.WantedInput> Apply(ShowState state, List<MediaLocator.WantedInput> inputs, LiveOutputs live)
    {
        var pick = Effective(state);
        var hasMonitor = state.Monitor.Device.Length > 0;
        for (var i = 0; i < inputs.Count; i++) inputs[i] = Apply(pick, inputs[i], hasMonitor, live);
        return inputs;
    }

    /// <summary>What the desk is listening to and where, in a line — the Audio page's readout.</summary>
    public static string Words(ShowState state)
    {
        var monitor = state.Monitor;
        var room = state.AudioPlayer.Devices.Count == 0
            ? "the machine's own output"
            : state.AudioPlayer.Devices.Count == 1 ? state.AudioPlayer.Devices[0] : $"{state.AudioPlayer.Devices.Count} outputs";
        var programme = $"The room hears the programme on {room}.";
        if (monitor.Source == AudioMonitor.Program) return $"{programme} Nothing else is playing at the desk.";
        if (monitor.Device.Length == 0)
        {
            return $"{programme} Pick a monitor output below to hear anything else — with one output there is nowhere to audition that is not the room.";
        }
        var what = monitor.Source switch
        {
            AudioMonitor.Silent => "Nothing",
            AudioMonitor.Preview => "The preview",
            _ => monitor.OutputId.Length == 0 ? "Nothing — pick which output" : LabelFor(state, monitor.OutputId),
        };
        return $"{programme} {what} on {monitor.Device}.";
    }

    /// <summary>A target id as an operator reads it: the custom label, else the id.</summary>
    public static string LabelFor(ShowState state, string targetId)
    {
        if (targetId.Length == 0) return "the programme";
        foreach (var p in state.Output.Placements)
        {
            if (p.ScreenId == targetId) return p.CustomLabel.Length > 0 ? p.CustomLabel : targetId;
        }
        foreach (var c in state.Output.CanvasNames)
        {
            if (c.MemberKey == targetId) return c.Name.Length > 0 ? c.Name : targetId;
        }
        return targetId;
    }
}
