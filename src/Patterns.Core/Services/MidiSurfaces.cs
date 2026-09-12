using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// Starter rows for the surfaces most likely to be in the flight case.
///
/// The desk does not ship a driver per controller, and that is a decision rather than an omission.
/// The Interactive area has supported Arduinos, Teensys, Raspberry Pis and show controllers for
/// rounds without one line of device-specific code, because the vendor's knowledge belongs on the
/// operator's side of the wire. A MIDI note map is worse than a serial protocol in this respect,
/// not better: it changes with firmware, it differs between a unit's modes, and nobody can state it
/// with confidence without the hardware in front of them.
///
/// So these are STARTER ROWS, not a driver. Pressing the button puts them in the operator's own
/// table where they can be read, edited, deleted, or re-learned in thirty seconds by pressing the
/// pad — which turns "the numbers might be wrong" from a bug that appears at a venue into a row
/// that does not light until you touch it. Every set says on the page that it has not been run
/// against the hardware.
///
/// Chosen because each one breaks a different assumption: the APC40 for a grid whose lamps are
/// host-driven by velocity, the Launchpad for the same idea with a different palette, the
/// nanoKONTROL2 for a surface with NO usable feedback at all, and the X-Touch Mini for faders and
/// encoder rings.
/// </summary>
public static class MidiSurfaces
{
    public sealed record Surface(string Name, string Note, IReadOnlyList<(string Match, string Command)> Rows);

    /// <summary>
    /// Every starter set, in the order the page offers them.
    ///
    /// The velocities in the lamp rows are colour indexes into each surface's own palette, and the
    /// note numbers are the ones the vendors publish. Both are stated as a starting point and not
    /// as fact: the note on every set says so, because a desk that claims to know a controller it
    /// has never met is the kind of confidence that costs somebody a show.
    /// </summary>
    public static readonly IReadOnlyList<Surface> All = new[]
    {
        new Surface(
            "AKAI APC40 mkII",
            "The clip grid's lamps are host-driven: the pad lights only because the desk sent a note back, which is why these rows come in pairs. Velocity is a colour index into the APC's own palette, and the numbers here are the vendor's published ones — press the pad and re-learn any row that does not light.",
            new (string, string)[]
            {
                ("NOTE 1 0 *", "CUE GO"),
                ("NOTE 1 1 *", "LOOK #1"),
                ("NOTE 1 2 *", "LOOK #2"),
                ("NOTE 1 3 *", "LOOK #3"),
                ("NOTE 1 4 *", "LOOK #4"),
                ("NOTE 1 91 *", "CUE GO"),
                ("NOTE 1 92 *", "STOPALL"),
                ("NOTE 1 81 *", "BLACKOUT TOGGLE"),
                ("CC 1 7 *", "AUDIO LEVEL *"),
                ("CC 1 14 *", "MUSIC LEVEL *"),
                ("LOOK *", "LAMP 1 1 21"),
                ("BLACKOUT 1", "LAMP 1 81 5"),
                ("BLACKOUT 0", "LAMP 1 81 0"),
                ("ARMED 1", "LAMP 1 91 3"),
                ("ARMED 0", "LAMP 1 91 0"),
                ("LIVE 1", "LAMP 1 92 5"),
                ("LIVE 0", "LAMP 1 92 0"),
            }),

        new Surface(
            "Novation Launchpad (Mini MK3 / X / Pro)",
            "The grid addresses as plain notes only once the unit is in programmer mode, which is a SysEx this build does not send — set it on the unit or with Novation's own tool first. Velocity is an index into the Launchpad's palette.",
            new (string, string)[]
            {
                ("NOTE 1 81 *", "CUE GO"),
                ("NOTE 1 82 *", "LOOK #1"),
                ("NOTE 1 83 *", "LOOK #2"),
                ("NOTE 1 84 *", "LOOK #3"),
                ("NOTE 1 71 *", "BLACKOUT TOGGLE"),
                ("NOTE 1 72 *", "STOPALL"),
                ("BLACKOUT 1", "LAMP 1 71 5"),
                ("BLACKOUT 0", "LAMP 1 71 0"),
                ("ARMED 1", "LAMP 1 81 21"),
                ("ARMED 0", "LAMP 1 81 0"),
                ("LIVE 1", "LAMP 1 72 21"),
                ("LIVE 0", "LAMP 1 72 0"),
            }),

        new Surface(
            "Korg nanoKONTROL2",
            "This one has NO feedback worth the name: its button lamps answer the unit itself until they are switched to external control with Korg's Kontrol Editor, which this desk cannot do for you. Until you do, these rows control the show and nothing on the surface lights. That is said here rather than discovered at a venue.",
            new (string, string)[]
            {
                ("NOTE 1 41 *", "CUE GO"),
                ("NOTE 1 42 *", "STOPALL"),
                ("NOTE 1 45 *", "BLACKOUT TOGGLE"),
                ("CC 1 0 *", "AUDIO LEVEL *"),
                ("CC 1 1 *", "MUSIC LEVEL *"),
                ("NOTE 1 32 *", "LOOK #1"),
                ("NOTE 1 33 *", "LOOK #2"),
                ("NOTE 1 34 *", "LOOK #3"),
                ("NOTE 1 35 *", "LOOK #4"),
            }),

        new Surface(
            "Behringer X-Touch Mini",
            "In its standard mode the encoders send absolute positions and their LED rings are driven by a CC back — which is what the % on those rows is for: the show's own 0–100 reading, stretched onto the ring. In Mackie mode it speaks something else entirely and none of these rows apply.",
            new (string, string)[]
            {
                ("NOTE 1 8 *", "CUE GO"),
                ("NOTE 1 9 *", "STOPALL"),
                ("NOTE 1 10 *", "BLACKOUT TOGGLE"),
                ("CC 1 9 *", "AUDIO LEVEL *"),
                ("CC 1 10 *", "MUSIC LEVEL *"),
                ("VOL *", "CC 1 1 %"),
                ("BLACKOUT 1", "LAMP 1 10 127"),
                ("BLACKOUT 0", "LAMP 1 10 0"),
            }),
    };

    /// <summary>Puts a starter set into a device's own table, leaving any row the operator has already written.</summary>
    public static int Seed(DeviceConfig device, Surface surface)
    {
        var added = 0;
        foreach (var (match, command) in surface.Rows)
        {
            var already = false;
            foreach (var existing in device.Triggers)
            {
                if (string.Equals(existing.Match.Trim(), match, StringComparison.OrdinalIgnoreCase)) already = true;
            }
            if (already) continue;
            device.Triggers.Add(new DeviceTriggerConfig { Match = match, Command = command });
            added++;
        }
        return added;
    }

    /// <summary>The set whose name a port looks like, or null — Windows clips a port name to 31 characters, so either containing the other counts.</summary>
    public static Surface? For(string? portName)
    {
        var port = (portName ?? "").Trim();
        if (port.Length == 0) return null;
        foreach (var surface in All)
        {
            foreach (var word in surface.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 4 && port.Contains(word, StringComparison.OrdinalIgnoreCase)) return surface;
            }
        }
        return null;
    }
}
