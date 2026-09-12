using System.Globalization;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>What a MIDI message is, once the status byte has been read.</summary>
public enum MidiKind
{
    /// <summary>A key or pad down. Velocity 0 is a release on most surfaces, and is read as one.</summary>
    Note,
    /// <summary>A key or pad up.</summary>
    NoteOff,
    /// <summary>A knob, a fader, or a button that reports as a controller.</summary>
    Control,
    /// <summary>A program-change button — a bank on a foot controller, a patch on a keyboard.</summary>
    Program,
    /// <summary>A wheel, and what a motorised fader sends and is set by (14-bit, so it gets its own kind).</summary>
    Bend,
    /// <summary>Anything this desk has no word for; carried so a learn row can still be written for it.</summary>
    Other,
}

/// <summary>
/// One MIDI message, as a value.
///
/// Deliberately a struct with no allocation: a fader sweep on an eight-fader surface is hundreds of
/// these a second arriving on a driver thread, and the one thing that must not happen there is the
/// garbage collector waking up in the middle of a show.
/// </summary>
public readonly record struct MidiMessage(MidiKind Kind, int Channel, int Data1, int Data2)
{
    /// <summary>True when this is a control an operator sweeps rather than presses — a fader, a knob, a wheel.</summary>
    public bool IsContinuous => Kind is MidiKind.Control or MidiKind.Bend;

    /// <summary>True when a pad or key has been let go — including the note-on at velocity 0 that most surfaces send instead.</summary>
    public bool IsRelease => Kind == MidiKind.NoteOff || (Kind == MidiKind.Note && Data2 == 0);

    /// <summary>The value an operator is setting, 0–127 (a bend is folded down to it).</summary>
    public int Value => Kind == MidiKind.Bend ? Data2 >> 7 : Data2;
}

/// <summary>
/// MIDI as text lines, both ways.
///
/// This is the whole reason a control surface can ride the Interactive area rather than growing a
/// second one beside it. That area already has exactly what a MIDI map needs — a table of rows an
/// operator fills in, matching a line the device sent against a line to run, with a * that carries
/// the rest of the match into the command — and it has had it since Arduinos. A surface whose
/// messages are rendered as "NOTE 0 53 127" and "CC 0 7 64" is a device that speaks lines, so the
/// mapping, the action layer, the journal, the fencing and the status page are all already written.
///
/// The rows an operator ends up with read as plainly as the rest of the desk:
///   NOTE 0 53 *   →   LOOK 3          (a pad fires a look)
///   CC 0 7 *      →   AUDIO LEVEL *   (a fader sets a level; the * carries the value through)
///   NOTE 0 91 *   →   CUE GO          (the transport button the surface calls PLAY)
///
/// What this does NOT do is pretend to know a controller's note numbers. Nobody can, without the
/// hardware in front of them and the right firmware — which is exactly why the desk lets the
/// operator press the pad and fills the row in, instead of shipping a table that is wrong at a venue.
/// </summary>
public static class MidiLines
{
    /// <summary>The largest a MIDI data byte can be, and the divisor for a percentage.</summary>
    public const int Max = 127;

    /// <summary>
    /// A message as the line an operator sees and writes rows against. The channel is 0-based on
    /// the wire and shown 1-based here, because that is how every controller's own documentation
    /// numbers it and a desk that disagrees with the manual costs somebody an evening.
    /// </summary>
    public static string Format(in MidiMessage m) => m.Kind switch
    {
        // A release is its own word. Rendered as another NOTE it would match the same row as the
        // press — so "NOTE 1 53 *" → CUE GO would GO once when the pad went down and again when it
        // came up, which is the fault the word "once" exists for and the kind nobody finds until
        // a show.
        MidiKind.NoteOff => $"NOTEOFF {m.Channel + 1} {m.Data1}",
        MidiKind.Note when m.Data2 == 0 => $"NOTEOFF {m.Channel + 1} {m.Data1}",
        MidiKind.Note => $"NOTE {m.Channel + 1} {m.Data1} {m.Data2}",
        // Nought to a hundred, not nought to 127. The desk's level verbs refuse anything past 125
        // (and past 100 for break music), so a raw fader would simply stop responding in the top of
        // its travel — a control that silently dies halfway is worse than one that never worked.
        MidiKind.Control => $"CC {m.Channel + 1} {m.Data1} {Percent(m.Data2)}",
        MidiKind.Program => $"PROGRAM {m.Channel + 1} {m.Data1}",
        MidiKind.Bend => $"BEND {m.Channel + 1} {Percent(m.Value)}",
        _ => $"MIDI {m.Channel + 1} {m.Data1} {m.Data2}",
    };

    /// <summary>
    /// The line without its value — what a learn row matches on. A pad pressed twice at different
    /// velocities is the same pad, and a fader at 0 is the same fader as at 127, so the row the desk
    /// writes when you move a control ends there and takes a * for the rest.
    /// </summary>
    public static string Trigger(in MidiMessage m) => m.Kind switch
    {
        MidiKind.NoteOff => $"NOTEOFF {m.Channel + 1} {m.Data1}",
        MidiKind.Note when m.Data2 == 0 => $"NOTEOFF {m.Channel + 1} {m.Data1}",
        MidiKind.Note => $"NOTE {m.Channel + 1} {m.Data1} *",
        MidiKind.Control => $"CC {m.Channel + 1} {m.Data1} *",
        MidiKind.Program => $"PROGRAM {m.Channel + 1} {m.Data1}",
        MidiKind.Bend => $"BEND {m.Channel + 1} *",
        _ => $"MIDI {m.Channel + 1} {m.Data1} *",
    };

    /// <summary>
    /// True when this line is a surface's own, rather than one of the show's plain-text facts. It is
    /// what lets one trigger table carry both directions: a row whose left-hand side is a surface
    /// line is a control doing something, and a row whose left-hand side is a fact is the show
    /// lighting a lamp.
    /// </summary>
    public static bool IsSurfaceLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var text = line.TrimStart();
        foreach (var word in Words)
        {
            if (text.StartsWith(word, StringComparison.OrdinalIgnoreCase)
                && (text.Length == word.Length || text[word.Length] == ' '))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly string[] Words = { "NOTEOFF", "NOTE", "CC", "BEND", "PROGRAM", "MIDI", "LAMP" };

    /// <summary>The three raw bytes as a message; anything this desk has no word for still arrives as Other.</summary>
    public static MidiMessage Read(int status, int data1, int data2)
    {
        var channel = status & 0x0F;
        return (status & 0xF0) switch
        {
            0x80 => new MidiMessage(MidiKind.NoteOff, channel, data1, data2),
            0x90 => new MidiMessage(MidiKind.Note, channel, data1, data2),
            0xB0 => new MidiMessage(MidiKind.Control, channel, data1, data2),
            0xC0 => new MidiMessage(MidiKind.Program, channel, data1, 0),
            0xE0 => new MidiMessage(MidiKind.Bend, channel, data1, (data2 << 7) | data1),
            _ => new MidiMessage(MidiKind.Other, channel, data1, data2),
        };
    }

    /// <summary>
    /// A line the desk wants written back to the surface, as raw bytes — the lamp path.
    ///
    /// LAMP 1 53 21 lights pad 53 on channel 1 at colour 21; LAMP 1 53 0 puts it out. The colour is
    /// whatever that surface's own palette calls 21, which is why the profile that knows the number
    /// is data an operator can edit rather than a table compiled into the desk: a firmware update
    /// changes the palette, and a table in C# would then need a release.
    ///
    /// Returns false for a line that means nothing here, because a device's feedback text and a
    /// lamp instruction go down the same wire and only one of them is ours to send.
    /// </summary>
    public static bool TryLamp(string line, out int status, out int data1, out int data2)
    {
        status = data1 = data2 = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;

        var verb = parts[0].ToUpperInvariant();
        if (verb is not ("LAMP" or "NOTE" or "CC" or "BEND")) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel)) return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first)) return false;
        var second = 0;
        if (parts.Length > 3 && !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out second)) return false;

        channel = Math.Clamp(channel - 1, 0, 15);
        second = Math.Clamp(second, 0, Max);

        switch (verb)
        {
            case "CC":
                status = 0xB0 | channel;
                data1 = Math.Clamp(first, 0, Max);
                data2 = second;
                return true;
            case "BEND":
                // Fourteen bits, not seven: this is what SETS a motorised fader, and a fader driven
                // at 7-bit resolution steps visibly on its way to a position.
                status = 0xE0 | channel;
                first = Math.Clamp(first, 0, 16383);
                data1 = first & 0x7F;
                data2 = (first >> 7) & 0x7F;
                return true;
            default:                                           // LAMP and NOTE are the same wire
                status = 0x90 | channel;
                data1 = Math.Clamp(first, 0, Max);
                data2 = second;
                return true;
        }
    }

    /// <summary>
    /// A 0–127 reading as the percentage the desk's level verbs take. A fader's top must be exactly
    /// 100 and its bottom exactly 0 — an operator who pushes a fader all the way up and gets 99 %
    /// has found a bug, whatever the arithmetic says.
    /// </summary>
    public static int Percent(int value) => (int)Math.Round(Math.Clamp(value, 0, Max) * 100.0 / Max);
}
