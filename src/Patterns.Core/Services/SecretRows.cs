using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// Round 76: the rows an older show may still carry a credential in. Before round 75 MIDI learn
/// would bind a control to "RESTART &lt;passcode&gt;" and the Interactive area's trigger table takes
/// any line, so a show file could travel with the admin passcode in clear. The rule now: a row whose
/// command is an admin verb carrying the passcode is removed when the show loads, and the operator is
/// told without the command being repeated. A row whose passcode comes from the device's own line
/// ("RESTART *") carries no secret and stays. Pure.
/// </summary>
public static class SecretRows
{
    /// <summary>The words the operator reads, per surface: no command, no passcode.</summary>
    public static string Note(string deviceName)
        => $"An old administrative control binding on '{deviceName}' was removed because it contained a credential (RESTART and UPDATE APPLY carry the admin passcode). Run those from the desk or the Remote page, or bind a line that takes the passcode from the device.";

    /// <summary>Whether a trigger row's command carries a credential in the show file itself.</summary>
    public static bool Carries(string? command)
    {
        var line = (command ?? "").Trim();
        if (line.Length == 0) return false;
        var parsed = ControlProtocol.Parse(line);
        if (!parsed.IsAction || !ActionSpec.CarriesSecret(parsed.Action.Kind)) return false;
        var target = parsed.Action.Target.Trim();
        return target.Length > 0 && target != "*";
    }

    /// <summary>Every such row removed from every device of the Interactive area; one note per surface that lost one. Idempotent.</summary>
    public static IReadOnlyList<string> Scrub(ShowState state)
    {
        var notes = new List<string>();
        foreach (var device in state.Interactive.Devices)
        {
            var removed = 0;
            for (var i = device.Triggers.Count - 1; i >= 0; i--)
            {
                if (!Carries(device.Triggers[i].Command)) continue;
                device.Triggers.RemoveAt(i);
                removed++;
            }
            if (removed > 0) notes.Add(Note(device.Name.Length > 0 ? device.Name : "a device"));
        }
        return notes;
    }
}
