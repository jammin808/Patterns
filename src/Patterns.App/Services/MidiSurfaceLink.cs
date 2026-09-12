using System.Runtime.Versioning;
using NAudio.Midi;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// A MIDI control surface as a device link.
///
/// Everything above this is code the desk already had: the surface's messages are rendered as the
/// same text lines an Arduino sends, so the operator's trigger rows, the one action layer, the
/// journal and the arm fence all apply unchanged. What lives here is the three things that are
/// genuinely a control surface's own problem and had nothing to copy.
///
/// <list type="number">
/// <item>THE RATE. A fader sweep is hundreds of messages a second and sixteen encoders can move at
/// once. Presses go straight through because a GO button's whole value is that it is immediate;
/// axes go into <see cref="MidiCoalescer"/> and are read out fifty times a second, which turns a
/// sweep into about a dozen actions instead of several hundred.</item>
/// <item>THE ECHO. A lamp this desk lights is not an operator pressing anything, and a surface that
/// reports its own LEDs back — several do — would otherwise fire the action again, and again.
/// Every byte written is remembered for a moment and the same message coming back is dropped.</item>
/// <item>THE PORT. winmm has no hot-plug notification and no multi-client sharing: if Ableton or
/// the vendor's own utility holds the surface, this cannot open it. So the link retries quietly and
/// says in plain words what it is waiting for, rather than failing once at load and going silent.
/// </item>
/// </list>
///
/// Windows only — NAudio.Midi is winmm underneath. Everywhere else this opens nothing and says so,
/// exactly as the NDI runtime and libVLC do, because a desk that pretends is worse than one that
/// cannot.
/// </summary>
public sealed class MidiSurfaceLink : IDeviceLink
{
    /// <summary>How long a byte the desk wrote is remembered, so the surface's echo of it is dropped.</summary>
    private const int EchoMs = 120;

    /// <summary>
    /// How long after a hand has touched a control the desk keeps its own writes off it. A motorised
    /// fader driven while somebody is holding it fights them — the motor pulls one way, the hand the
    /// other — and the same is true of an LED ring redrawn under a knob being turned. The write is
    /// not dropped, it is held: the moment the hand goes quiet the control lands on what the show
    /// actually is, rather than on wherever it was let go.
    /// </summary>
    private const int TouchMs = 500;

    private readonly string _wanted;
    private readonly MidiCoalescer _axes = new();
    private readonly List<string> _drained = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, long> _written = new();
    private readonly Dictionary<int, long> _touched = new();
    private readonly Dictionary<int, int> _held = new();
    private readonly System.Threading.Timer _sampler;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private MidiIn? _in;
    private MidiOut? _out;
    private volatile string _status = "opening…";
    private volatile bool _open;
    private bool _disposed;

    public MidiSurfaceLink(string portName)
    {
        _wanted = (portName ?? "").Trim();
        _sampler = new System.Threading.Timer(_ => Tick(), null, MidiCoalescer.SampleMs, MidiCoalescer.SampleMs);
    }

    public string Status => _status;

    public bool IsOpen => _open;

    public event Action<string>? LineReceived;

    /// <summary>How many axis readings the sampling saved the desk from running — the page shows it.</summary>
    public long Coalesced => _axes.Coalesced;

    /// <summary>
    /// A line out. A lamp instruction goes to the surface as bytes; anything else is the show's
    /// plain-text feedback, which a MIDI surface has no way to hear, so it is dropped rather than
    /// sprayed down the wire as note-ons nobody could diagnose.
    /// </summary>
    public void Write(string framedLine)
    {
        if (!MidiLines.TryLamp(framedLine, out var status, out var d1, out var d2)) return;
        if (!OperatingSystem.IsWindows()) return;

        // A continuous control with a hand on it is not written to. The value is kept and lands the
        // moment they let go, so the fader ends where the show is rather than where it was released.
        var kind = status & 0xF0;
        if (kind is 0xB0 or 0xE0)
        {
            var key = status | (d1 << 8);
            lock (_gate)
            {
                if (_touched.TryGetValue(key, out var last) && _clock.ElapsedMilliseconds - last < TouchMs)
                {
                    _held[key] = d2;
                    return;
                }
            }
        }
        Send(status, d1, d2);
    }

    [SupportedOSPlatform("windows")]
    private void Send(int status, int d1, int d2)
    {
        var message = status | (d1 << 8) | (d2 << 16);
        lock (_gate)
        {
            // Remembered so the surface's own echo of this lamp is not read as somebody pressing it.
            _written[message] = _clock.ElapsedMilliseconds + EchoMs;
        }
        try
        {
            _out?.Send(message);
        }
        catch (Exception ex)
        {
            _status = "write failed: " + ex.Message;
            _open = false;
            Log.Warn($"MIDI write to '{_wanted}' failed.", ex);
        }
    }

    /// <summary>
    /// The sample tick: the axes that moved, and — while nothing is open — one quiet attempt to
    /// open the port. Both here, because a surface somebody plugs in mid-rig should simply start
    /// working, and winmm will not tell anybody it arrived.
    /// </summary>
    private void Tick()
    {
        if (_disposed) return;
        try
        {
            if (!_open) Reopen();
            _drained.Clear();
            _axes.Drain(_drained);
            foreach (var line in _drained) LineReceived?.Invoke(line);
            if (OperatingSystem.IsWindows()) LandHeld();
        }
        catch (Exception ex)
        {
            Log.Warn("MIDI tick failed.", ex);
        }
    }

    /// <summary>Writes the desk held back while a hand was on a control, now that it has gone quiet.</summary>
    [SupportedOSPlatform("windows")]
    private void LandHeld()
    {
        List<(int Key, int Value)>? due = null;
        lock (_gate)
        {
            if (_held.Count == 0) return;
            var now = _clock.ElapsedMilliseconds;
            foreach (var (key, value) in _held)
            {
                if (_touched.TryGetValue(key, out var last) && now - last < TouchMs) continue;
                (due ??= new()).Add((key, value));
            }
            if (due is null) return;
            foreach (var (key, _) in due) _held.Remove(key);
        }
        foreach (var (key, value) in due) Send(key & 0xFF, (key >> 8) & 0x7F, value);
    }

    private long _nextTry;

    private void Reopen()
    {
        if (!OperatingSystem.IsWindows())
        {
            _status = "MIDI surfaces are Windows only on this build.";
            return;
        }
        var now = _clock.ElapsedMilliseconds;
        if (now < _nextTry) return;
        _nextTry = now + 2000;                                 // a quiet retry, not a spin
        OpenWindows();
    }

    [SupportedOSPlatform("windows")]
    private void OpenWindows()
    {
        try
        {
            var index = FindIn(_wanted);
            if (index < 0)
            {
                _status = MidiIn.NumberOfDevices == 0
                    ? "no MIDI inputs on this machine — plug the surface in"
                    : $"'{_wanted}' is not plugged in — waiting";
                return;
            }

            var midiIn = new MidiIn(index);
            midiIn.MessageReceived += OnMessage;
            midiIn.ErrorReceived += (_, _) => { };            // a malformed byte is not a reason to stop
            midiIn.Start();
            _in = midiIn;

            var outIndex = FindOut(_wanted);
            if (outIndex >= 0)
            {
                try
                {
                    _out = new MidiOut(outIndex);
                }
                catch (Exception ex)
                {
                    // Control without lamps is still control, and saying so is better than refusing
                    // to open the surface at all.
                    Log.Warn($"MIDI output '{_wanted}' would not open; the surface still controls.", ex);
                }
            }

            _axes.Forget();                                    // a fader may have been moved while it was away
            _open = true;
            _status = _out is null
                ? $"open ({_wanted}) — control only, no lamps on this port"
                : $"open ({_wanted})";
        }
        catch (Exception ex)
        {
            // winmm ports are single-client: another application holding the surface is the
            // commonest reason, and it is one the operator can actually do something about.
            _status = $"'{_wanted}' would not open — another application may be holding it ({ex.Message}); retrying";
            Close();
        }
    }

    [SupportedOSPlatform("windows")]
    private void OnMessage(object? sender, MidiInMessageEventArgs e)
    {
        // The driver's own thread. Nothing here allocates, blocks or throws: work goes into a slot
        // and the sample tick takes it out.
        try
        {
            var raw = e.RawMessage;
            var status = raw & 0xFF;
            if (status >= 0xF0) return;                        // clock, active sensing and sysex are not controls
            var d1 = (raw >> 8) & 0x7F;
            var d2 = (raw >> 16) & 0x7F;

            if (IsEcho(status | (d1 << 8) | (d2 << 16))) return;

            // A hand is on this control: remember it, so the desk's own writes stay off it.
            if ((status & 0xF0) is 0xB0 or 0xE0)
            {
                lock (_gate)
                {
                    _touched[status | (d1 << 8)] = _clock.ElapsedMilliseconds;
                }
            }

            var line = _axes.Offer(MidiLines.Read(status, d1, d2));
            if (line is not null) LineReceived?.Invoke(line);
        }
        catch
        {
            // A fault inside somebody else's driver callback can take the driver down with it.
        }
    }

    private bool IsEcho(int message)
    {
        lock (_gate)
        {
            if (_written.Count == 0) return false;
            var now = _clock.ElapsedMilliseconds;
            if (!_written.TryGetValue(message, out var until)) return false;
            if (until < now)
            {
                _written.Remove(message);
                return false;
            }
            _written.Remove(message);
            return true;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int FindIn(string name)
    {
        for (var i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            if (Matches(MidiIn.DeviceInfo(i).ProductName, name)) return i;
        }
        return -1;
    }

    [SupportedOSPlatform("windows")]
    private static int FindOut(string name)
    {
        for (var i = 0; i < MidiOut.NumberOfDevices; i++)
        {
            if (Matches(MidiOut.DeviceInfo(i).ProductName, name)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Windows truncates a MIDI port's name to 31 characters and vendors are not shy with theirs,
    /// so a show file carrying the full name would never match the machine's truncated one. Either
    /// starting with the other counts, and an empty name takes whatever is there — which is what an
    /// operator with one surface plugged in expects.
    /// </summary>
    private static bool Matches(string? product, string wanted)
    {
        var have = (product ?? "").Trim();
        if (wanted.Length == 0) return have.Length > 0;
        return have.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
            || wanted.StartsWith(have, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every MIDI input this machine has, for the page's picker. Empty off Windows.</summary>
    public static IReadOnlyList<string> Inputs()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            var list = new List<string>();
            for (var i = 0; i < MidiIn.NumberOfDevices; i++)
            {
                var name = MidiIn.DeviceInfo(i).ProductName?.Trim() ?? "";
                if (name.Length > 0) list.Add(name);
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn("Listing MIDI inputs failed.", ex);
            return Array.Empty<string>();
        }
    }

    private void Close()
    {
        try
        {
            _in?.Stop();
            _in?.Dispose();
        }
        catch
        {
            // A port that will not close is the driver's problem, not the show's.
        }
        try
        {
            _out?.Dispose();
        }
        catch
        {
            // As above.
        }
        _in = null;
        _out = null;
        _open = false;
    }

    public void Dispose()
    {
        _disposed = true;
        _sampler.Dispose();
        Close();
    }
}
