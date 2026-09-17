using System.Globalization;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>One control of a MIDI surface bound to a line of the wire — a trigger row of the Interactive area, read as a binding.</summary>
public sealed record MidiBinding(string Device, string Match, string Command)
{
    /// <summary>The control's words for a menu, a chip or a card: "NOTE 1 53" — the trailing * dropped.</summary>
    public string Control => MidiBindings.ControlWords(Match);

    /// <summary>The control and its surface: "NOTE 1 53 (APC40)".</summary>
    public string Words => $"{Control} ({Device})";
}

/// <summary>
/// MIDI learn's rules, pure (round 73). The Interactive area's trigger table is the map — a row
/// whose left is a surface line and whose right is a wire line is a control bound to that line —
/// and learn from a right-click writes into the same table, so the page, the wire, STATE, the
/// Eye and the show file all read one thing. What is here is the shaping: what row a learned
/// line and a wire line become, which rows a wire line has, and the words for them.
///
/// The rules, each taken from how the field does it (docs/MIDI-LEARN.md): a press learns the
/// pad at any velocity (the row ends in *), a sweep learns the fader anywhere in its travel and
/// its value rides into a level verb as * (Ableton's absolute mapping, Resolume's "value"
/// shortcuts); the last mapping of a control wins and says what it replaced (Ableton replaces,
/// Resolume warns — the desk does both); a release never binds; the mapping is saved with the
/// show, because a surface mapped for a show belongs to that show.
/// </summary>
public static class MidiBindings
{
    /// <summary>A device of the Interactive area that is a MIDI surface.</summary>
    public static bool IsSurface(DeviceConfig d) => d.Link == DeviceLink.Midi;

    /// <summary>Every control bound on every surface, in table order — rows that read the other way (a fact lighting a lamp) are not controls.</summary>
    public static IReadOnlyList<MidiBinding> All(IEnumerable<DeviceConfig> devices)
    {
        var list = new List<MidiBinding>();
        foreach (var d in devices)
        {
            if (!IsSurface(d)) continue;
            foreach (var t in d.Triggers)
            {
                if (IsBinding(t)) list.Add(new MidiBinding(d.Name, Normalise(t.Match), Normalise(t.Command)));
            }
        }
        return list;
    }

    /// <summary>How many controls one surface has bound.</summary>
    public static int Count(DeviceConfig d) => IsSurface(d) ? d.Triggers.Count(IsBinding) : 0;

    /// <summary>A row that is a control doing something: a surface line on the left, a wire line (not a lamp) on the right.</summary>
    public static bool IsBinding(DeviceTriggerConfig t)
        => MidiLines.IsSurfaceLine(t.Match) && t.Command.Trim().Length > 0 && !MidiLines.IsSurfaceLine(t.Command);

    /// <summary>
    /// The controls bound to a wire line: the row's command is the line (case-blind, spaces
    /// tidied), or the row is a level row ("AUDIO LEVEL *") and the line is that verb with a
    /// number ("AUDIO LEVEL 50") — the fader that sets the level is the control bound to it.
    /// </summary>
    public static IReadOnlyList<MidiBinding> For(IEnumerable<MidiBinding> all, string wire)
    {
        var line = Normalise(wire);
        if (line.Length == 0) return Array.Empty<MidiBinding>();
        var list = new List<MidiBinding>();
        foreach (var b in all)
        {
            if (SameLine(b.Command, line)) list.Add(b);
            else if (b.Command.EndsWith('*') && line.StartsWith(b.Command[..^1], StringComparison.OrdinalIgnoreCase) && line.Length > b.Command.Length - 1) list.Add(b);
        }
        return list;
    }

    /// <summary>Two wire lines are the same line: case-blind, any run of spaces one space.</summary>
    public static bool SameLine(string? a, string? b) => string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A line with its spaces tidied — what every comparison and every row is written with.</summary>
    public static string Normalise(string? line) => string.Join(' ', (line ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>"NOTE 1 53" for "NOTE 1 53 *" — the control without the wildcard a row carries.</summary>
    public static string ControlWords(string? match) => Normalise(match).TrimEnd(' ', '*');

    /// <summary>
    /// The row a learned line and a wire line become. The match is the line's trigger form (the
    /// pad at any velocity, the fader anywhere); the command is the wire line — with its trailing
    /// number turned into * when the control is one that sweeps, so "AUDIO LEVEL 50" learned on a
    /// fader becomes "AUDIO LEVEL *" and the fader sets the level, while the same line learned on
    /// a pad stays "AUDIO LEVEL 50" and the pad sets it to fifty.
    /// </summary>
    public static (string Match, string Command) RowFor(string learnedLine, string wire)
    {
        var match = MidiLines.TriggerOf(learnedLine);
        var command = Normalise(wire);
        if (MidiLines.IsContinuousLine(learnedLine))
        {
            var cut = command.LastIndexOf(' ');
            if (cut > 0 && IsNumber(command[(cut + 1)..])) command = command[..cut] + " *";
        }
        return (match, command);
    }

    private static bool IsNumber(string word)
        => word.Length > 0 && double.TryParse(word.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// The row written into the surface's table: a row that already matches this control is
    /// retimed to the new line (the last mapping wins, and the words say what it replaced), else
    /// a row is added. The words are what the status line and the journal read:
    /// "NOTE 1 53 on APC40 → LOOK Walk-in" or "… (was CUE GO)".
    /// </summary>
    public static string Bind(DeviceConfig device, string learnedLine, string wire)
    {
        var (match, command) = RowFor(learnedLine, wire);
        if (match.Length == 0 || command.Length == 0) return "";
        DeviceTriggerConfig? row = null;
        foreach (var t in device.Triggers)
        {
            if (MidiLines.IsSurfaceLine(t.Match) && SameLine(t.Match, match))
            {
                row = t;
                break;
            }
        }
        var control = ControlWords(match);
        if (row is null)
        {
            device.Triggers.Add(new DeviceTriggerConfig { Match = match, Command = command });
            return $"{control} on {device.Name} → {command}";
        }
        var was = Normalise(row.Command);
        if (SameLine(was, command)) return $"{control} on {device.Name} → {was} (as it was)";   // the row keeps its own spelling
        row.Command = command;
        return $"{control} on {device.Name} → {command} (was {was})";
    }

    /// <summary>Every control bound to a wire line forgotten, on every surface; how many rows went.</summary>
    public static int Forget(IEnumerable<DeviceConfig> devices, string wire)
    {
        var line = Normalise(wire);
        if (line.Length == 0) return 0;
        var gone = 0;
        foreach (var d in devices)
        {
            if (!IsSurface(d)) continue;
            for (var i = d.Triggers.Count - 1; i >= 0; i--)
            {
                var t = d.Triggers[i];
                if (!IsBinding(t)) continue;
                if (SameLine(t.Command, line) || (t.Command.Trim().EndsWith('*') && line.StartsWith(Normalise(t.Command)[..^1], StringComparison.OrdinalIgnoreCase) && line.Length > Normalise(t.Command).Length - 1))
                {
                    d.Triggers.RemoveAt(i);
                    gone++;
                }
            }
        }
        return gone;
    }

    /// <summary>One binding's row forgotten — the page's ✕ on a mapped control.</summary>
    public static bool ForgetRow(IEnumerable<DeviceConfig> devices, MidiBinding binding)
    {
        foreach (var d in devices)
        {
            if (!IsSurface(d) || !string.Equals(d.Name, binding.Device, StringComparison.OrdinalIgnoreCase)) continue;
            for (var i = d.Triggers.Count - 1; i >= 0; i--)
            {
                var t = d.Triggers[i];
                if (IsBinding(t) && SameLine(t.Match, binding.Match) && SameLine(t.Command, binding.Command))
                {
                    d.Triggers.RemoveAt(i);
                    return true;
                }
            }
        }
        return false;
    }
}
