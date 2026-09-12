namespace Patterns.Core.Services;

/// <summary>
/// What a control surface is doing, sampled — the one piece of this that had no precedent to copy.
///
/// The Interactive area was built for Arduinos, which send a few lines a second, so it posts one
/// closure to the UI thread per line and every command it runs writes a journal row to disk
/// synchronously on that thread. A nine-fader surface is not that: one fader sweep emits a message
/// every few milliseconds, sixteen encoders can move at once, and a "select all" gesture is a burst
/// of note-ons. Copying the Arduino path literally would stall the desk tick for the length of a
/// gesture — the exact opposite of what a control surface is for.
///
/// So this does what a game engine does with a gamepad, which is the architecture the desk borrows
/// from elsewhere too. It does NOT turn every input event into a command. It splits the surface in
/// two:
///
/// <list type="bullet">
/// <item>EDGES — pads, keys, transport and bank buttons — go through the instant they arrive.
/// Latency is the whole point of a GO button, and a press is one message, not hundreds.</item>
/// <item>AXES — faders, knobs, wheels — are written into a slot and read out on a sample tick, so a
/// control that moved a hundred times between ticks costs one line carrying where it ended up.
/// Nobody can hear or see the ninety-nine positions it passed through on the way.</item>
/// </list>
///
/// A control whose value has not changed produces nothing at all, so a surface that reports its
/// faders continuously (several do) costs a quiet show nothing.
///
/// Allocation-free on the driver thread: the slots are one array made once, because the thread that
/// must never wake the garbage collector is the one inside somebody else's audio driver.
/// </summary>
public sealed class MidiCoalescer
{
    /// <summary>
    /// How often the axes are read out. Fifty times a second is finer than a hand moves a fader and
    /// far finer than an eye or an ear can follow, and it turns a sweep from several hundred actions
    /// into about a dozen — which is what keeps the journal, the disk and the desk tick out of it.
    /// </summary>
    public const int SampleMs = 20;

    private const int Channels = 16;
    private const int Controllers = 128;
    private const int Bend = Channels * Controllers;        // the bend slots sit after the CC slots
    private const int Slots = Bend + Channels;

    private readonly object _gate = new();
    private readonly int[] _value = new int[Slots];
    private readonly bool[] _waiting = new bool[Slots];
    private readonly int[] _sent = new int[Slots];
    private readonly bool[] _known = new bool[Slots];
    private bool _any;

    /// <summary>How many axis readings the sampling has saved the desk from running. For the status line.</summary>
    public long Coalesced { get; private set; }

    /// <summary>
    /// A message from the driver thread. Returns the line to run NOW — a press, a release, a bank
    /// button — or null when it is an axis, which is held for the next <see cref="Drain"/>.
    /// </summary>
    public string? Offer(in MidiMessage m)
    {
        if (!m.IsContinuous) return MidiLines.Format(m);

        var slot = SlotOf(in m);
        if (slot < 0) return null;
        lock (_gate)
        {
            if (_waiting[slot]) Coalesced++;                  // a reading the desk never has to run
            // Kept as the percentage the operator sees and the desk's verbs take, so two raw
            // readings a hand cannot tell apart are one reading rather than two identical lines.
            _value[slot] = MidiLines.Percent(m.Value);
            _waiting[slot] = true;
            _any = true;
        }
        return null;
    }

    /// <summary>
    /// The axes that have moved since the last read, as lines, into the caller's list. Nothing is
    /// allocated when nothing moved, which is every tick of a show where nobody is touching the
    /// surface — and that is most of them.
    /// </summary>
    public void Drain(List<string> into)
    {
        lock (_gate)
        {
            if (!_any) return;
            _any = false;
            for (var slot = 0; slot < Slots; slot++)
            {
                if (!_waiting[slot]) continue;
                _waiting[slot] = false;
                var value = _value[slot];
                // Where it ended up, not where it went: a fader that came back to where it started
                // between two ticks has not moved as far as the show is concerned.
                if (_known[slot] && _sent[slot] == value) continue;
                _known[slot] = true;
                _sent[slot] = value;
                into.Add(LineOf(slot, value));
            }
        }
    }

    /// <summary>
    /// Forget what every control was last seen at. Called when a surface is opened or reopened: the
    /// operator may have moved a fader while it was unplugged, and a desk that remembered the old
    /// reading would ignore the first move back to it.
    /// </summary>
    public void Forget()
    {
        lock (_gate)
        {
            Array.Clear(_known);
            Array.Clear(_waiting);
            _any = false;
        }
    }

    private static int SlotOf(in MidiMessage m) => m.Kind switch
    {
        MidiKind.Control when m.Data1 is >= 0 and < Controllers => m.Channel * Controllers + m.Data1,
        MidiKind.Bend => Bend + m.Channel,
        _ => -1,
    };

    /// <summary>The slot's line, with the percentage it already holds — Format is not asked to convert twice.</summary>
    private static string LineOf(int slot, int percent)
        => slot >= Bend
            ? $"BEND {slot - Bend + 1} {percent}"
            : $"CC {slot / Controllers + 1} {slot % Controllers} {percent}";
}
