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
/// room is hearing is the sound the desk should be checking against. Nothing here reaches an
/// output, a send or the stream — a clip muted at the desk is still heard by the audience, because
/// what the audience hears is decided by the picture that is on air, not by what the operator has
/// their headphones on.
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

    /// <summary>True when a mount on these buses is the one the desk is listening to.</summary>
    public static bool Hears(in MonitorPick pick, IReadOnlyList<MediaBus>? buses)
    {
        if (pick.Source == AudioMonitor.Silent) return false;
        if (buses is null || buses.Count == 0) return pick.Source == AudioMonitor.Program;
        foreach (var bus in buses)
        {
            var heard = pick.Source switch
            {
                AudioMonitor.Preview => bus.Preview,
                AudioMonitor.Output => !bus.Preview && bus.OutputId.Length > 0 && bus.OutputId == pick.OutputId,
                _ => bus.IsProgram,
            };
            if (heard) return true;
        }
        return false;
    }

    /// <summary>
    /// The input as the decoder should play it: its own settings when the desk is listening to it,
    /// silent when it is not. The operator's own mute and volume still apply on top — this only
    /// ever takes sound away.
    /// </summary>
    public static MediaLocator.WantedInput Apply(in MonitorPick pick, MediaLocator.WantedInput input)
        => Hears(pick, input.Buses) ? input : input with { Mute = true };

    /// <summary>The whole list, monitored.</summary>
    public static List<MediaLocator.WantedInput> Apply(ShowState state, List<MediaLocator.WantedInput> inputs)
    {
        var pick = Effective(state);
        for (var i = 0; i < inputs.Count; i++) inputs[i] = Apply(pick, inputs[i]);
        return inputs;
    }

    /// <summary>What the desk is listening to, in a line — the Audio page's readout and the status line.</summary>
    public static string Words(ShowState state)
    {
        var monitor = state.Monitor;
        return monitor.Source switch
        {
            AudioMonitor.Silent => "Clips silent at the desk — the room still hears the programme.",
            AudioMonitor.Preview => "Listening to the preview — what the next TAKE will sound like.",
            AudioMonitor.Output => monitor.OutputId.Length == 0
                ? "Listening to an output — pick which one."
                : $"Listening to {LabelFor(state, monitor.OutputId)} — its own picture's sound, not the programme's.",
            _ => "Listening to the programme — the sound the room is hearing.",
        };
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
