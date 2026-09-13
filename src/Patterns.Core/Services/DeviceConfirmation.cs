using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// What a send to a box can establish, and what the show asks of it. Patterns accepting a command
/// is not the box receiving it, receiving is not accepting, accepting is not doing: the four
/// levels of <see cref="ConfirmLevel"/> keep those apart, and every device says how far its link
/// and profile can reach. A cue's step is dispatched at once and its receipt follows; the twin's
/// wall switch is a fence only when the box behind it can answer.
/// </summary>
public static class DeviceConfirmation
{
    /// <summary>The strongest level a link and a profile can reach. A datagram or a note has no answer; a reply is a fact only when the profile reads one.</summary>
    public static ConfirmLevel Attainable(DeviceLink link, DeviceProfile profile)
    {
        switch (link)
        {
            case DeviceLink.Udp:
            case DeviceLink.Midi:
                return ConfirmLevel.Sent;
            case DeviceLink.Http:
                return ConfirmLevel.Observed;                       // a response, its status, and a query after
            default:
                return profile switch
                {
                    DeviceProfile.PjLink or DeviceProfile.Pixera or DeviceProfile.Lines => ConfirmLevel.Observed,
                    _ => ConfirmLevel.Delivered,                    // OSC words over a stream: the socket took them, nobody answers
                };
        }
    }

    /// <summary>What the device's setting comes to: capped at what it can reach, and Observed only with a query to ask.</summary>
    public static ConfirmLevel Effective(DeviceConfig d)
    {
        var wanted = d.Confirm;
        if (wanted == ConfirmLevel.Observed && string.IsNullOrWhiteSpace(d.ObserveQuery)) wanted = ConfirmLevel.Accepted;
        var cap = Attainable(d.Link, d.Profile);
        return (ConfirmLevel)Math.Min((int)wanted, (int)cap);
    }

    public static TimeSpan Timeout(DeviceConfig d) => TimeSpan.FromMilliseconds(Math.Clamp(d.ConfirmTimeoutMs, 200, 30000));

    public static string Label(ConfirmLevel level) => level switch
    {
        ConfirmLevel.Sent => "sent",
        ConfirmLevel.Delivered => "delivered",
        ConfirmLevel.Accepted => "accepted",
        ConfirmLevel.Observed => "observed",
        _ => level.ToString().ToLowerInvariant(),
    };

    /// <summary>The page's words for a level.</summary>
    public static string Describe(ConfirmLevel level) => level switch
    {
        ConfirmLevel.Sent => "Sent — the bytes left this desk; nothing comes back to say they arrived (all a UDP datagram or a MIDI note can say)",
        ConfirmLevel.Delivered => "Delivered — the connection took the bytes (TCP, serial), or the box answered at all (HTTP)",
        ConfirmLevel.Accepted => "Accepted — the box said yes: a projector's OK, Pixera's result, an OK line, a 2xx",
        ConfirmLevel.Observed => "Observed — asked afterwards, the box's state is what was asked for (the query and the expected answer below)",
        _ => level.ToString(),
    };

    /// <summary>"sent only (UDP)" — why a device cannot reach more, for the page and the fence's words.</summary>
    public static string Limit(DeviceLink link, DeviceProfile profile)
    {
        var cap = Attainable(link, profile);
        return cap switch
        {
            ConfirmLevel.Sent => link == DeviceLink.Midi ? "sent only (a MIDI note has no answer)" : "sent only (a UDP datagram has no answer)",
            ConfirmLevel.Delivered => "delivered at most (the profile reads no reply)",
            _ => "",
        };
    }

    /// <summary>
    /// Why a cue cannot be the twin's wall-switch fence, or null: every device it sends to must be
    /// able to answer — at least Delivered — because a fence that cannot answer is a hope. A cue
    /// that is not there yet, or a step whose device is not on the page, is left to fire time,
    /// which refuses it with its own words.
    /// </summary>
    public static string? FenceProblem(ShowState state, string cueWord)
    {
        if (string.IsNullOrWhiteSpace(cueWord)) return null;
        var found = CueStacks.FindCueByWord(state, cueWord);
        if (found is null) return null;
        foreach (var step in found.Value.Cue.Actions)
        {
            if (step.Kind != ShowActionKind.DeviceSend) continue;
            var d = Interactive.Find(state.Interactive, step.Target);
            if (d is null) continue;
            if (Attainable(d.Link, d.Profile) < ConfirmLevel.Delivered)
            {
                return $"the wall-switch cue's device '{d.Name}' is {Limit(d.Link, d.Profile)} — nothing comes back to say the room moved";
            }
        }
        return null;
    }
}
