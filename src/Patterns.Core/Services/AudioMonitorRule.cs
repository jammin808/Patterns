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
    {
        if (OnProgram(buses)) return AudioDestination.Program;
        return hasMonitorDevice && Monitored(pick, buses) ? AudioDestination.Monitor : AudioDestination.Silent;
    }

    /// <summary>
    /// The input as the decoder should play it: where its sound goes, and silent when that is
    /// nowhere. The operator's own mute and volume still apply on top — this only ever takes sound
    /// away, and never from the room.
    /// </summary>
    public static MediaLocator.WantedInput Apply(in MonitorPick pick, MediaLocator.WantedInput input, bool hasMonitorDevice)
    {
        var where = Where(pick, input.Buses, hasMonitorDevice);
        return where == AudioDestination.Silent
            ? input with { Mute = true, Destination = where }
            : input with { Destination = where };
    }

    /// <summary>The whole list, routed.</summary>
    public static List<MediaLocator.WantedInput> Apply(ShowState state, List<MediaLocator.WantedInput> inputs)
    {
        var pick = Effective(state);
        var hasMonitor = state.Monitor.Device.Length > 0;
        for (var i = 0; i < inputs.Count; i++) inputs[i] = Apply(pick, inputs[i], hasMonitor);
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
