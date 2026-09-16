using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Patterns.Core.Services;

namespace Patterns.Audio;

/// <summary>One audio endpoint Windows has active: its id (stable across renames) and its friendly name (what the pickers, the routing tables and the wire say).</summary>
public sealed record AudioEndpoint(string Id, string Name);

/// <summary>
/// What one read of the machine's endpoints found, as one immutable value the desk hands round: the
/// outputs and the inputs, their names as lists that keep their identity until the next change (a
/// page compares the reference, never the contents), the read's version, moment and cost, and why it
/// was read.
/// </summary>
public sealed record AudioEndpointSnapshot(
    IReadOnlyList<AudioEndpoint> Render,
    IReadOnlyList<AudioEndpoint> Capture,
    IReadOnlyList<string> RenderNames,
    IReadOnlyList<string> CaptureNames,
    long Version,
    DateTime ReadAtUtc,
    double ReadMs,
    string Reason)
{
    public static readonly AudioEndpointSnapshot Empty = new(Array.Empty<AudioEndpoint>(), Array.Empty<AudioEndpoint>(), Array.Empty<string>(), Array.Empty<string>(), 0, DateTime.MinValue, 0, "not read yet");

    /// <summary>The id behind an output's name, or null when this machine has no output of that name.</summary>
    public string? RenderIdOf(string? name) => IdOf(Render, name);

    /// <summary>The id behind an input's name, or null when this machine has no input of that name.</summary>
    public string? CaptureIdOf(string? name) => IdOf(Capture, name);

    private static string? IdOf(IReadOnlyList<AudioEndpoint> list, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Name, name, StringComparison.OrdinalIgnoreCase)) return list[i].Id;
        }
        return null;
    }

    /// <summary>True when the two reads found the same endpoints in the same order: nothing to publish.</summary>
    public bool SameEndpointsAs(IReadOnlyList<AudioEndpoint> render, IReadOnlyList<AudioEndpoint> capture)
        => Render.SequenceEqual(render) && Capture.SequenceEqual(capture);
}

/// <summary>
/// Round 71: the machine's audio endpoints as a catalogue, never as a question asked of Windows on the
/// desk's thread. Enumerating the endpoints (WASAPI, with each one's name read from its property
/// store) costs a few hundred milliseconds on an ordinary laptop, and the desk asked it every second
/// while a sound-reactive pattern was on the desk, on every right-click menu of a screen, and every
/// fifth tick for the health facts — the stutter the super-check called "(audio)". The catalogue reads
/// once, on a worker, when the desk starts; Windows then tells it when a device comes, goes, changes
/// state or becomes the default (<see cref="IMMNotificationClient"/>), and it reads again, debounced,
/// on a worker, publishing a new snapshot only when the endpoints differ. Every reader — the tick's
/// pickers, the menus' choices, the routing tables, the health facts, the decoder's device ids — reads
/// the current snapshot, which costs a field read. A read the desk wants by name (a REFRESH button, a
/// device the show names that the list has not got) is a nudge, never a wait. Off Windows the
/// catalogue is empty and silent; a test hands it a reader of its own.
/// </summary>
public sealed class AudioEndpointCatalogue : IDisposable
{
    /// <summary>What a read of the machine finds: the outputs and the inputs, in the machine's order.</summary>
    public delegate (IReadOnlyList<AudioEndpoint> Render, IReadOnlyList<AudioEndpoint> Capture) Reader();

    /// <summary>How long a burst of notifications (a dock plugged in is several) is left to settle before the one read.</summary>
    public const int DebounceMs = 400;

    private readonly int _debounceMs;
    private readonly Timer _debounce;
    private readonly object _publishGate = new();
    private volatile AudioEndpointSnapshot _current = AudioEndpointSnapshot.Empty;
    private int _reading;
    private int _again;
    private volatile string _pendingReason = "";
    private int _reads;
    private int _nudges;
    private MMDeviceEnumerator? _listener;
    private NotificationClient? _client;
    private bool _disposed;

    public AudioEndpointCatalogue(Reader? reader = null, int debounceMs = DebounceMs)
    {
        Read = reader ?? (OperatingSystem.IsWindows() ? ReadFromWindows : ReadNothing);
        _debounceMs = Math.Max(1, debounceMs);
        _debounce = new Timer(_ => ReadNowGuarded(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The reader the catalogue asks: Windows, or what a test says the machine has.</summary>
    public Reader Read { get; set; }

    /// <summary>The endpoints as last read: one value, replaced whole when they change.</summary>
    public AudioEndpointSnapshot Current => _current;

    /// <summary>The outputs' names, in the machine's order: the same list instance until the endpoints change.</summary>
    public IReadOnlyList<string> RenderNames => _current.RenderNames;

    /// <summary>The inputs' names, in the machine's order: the same list instance until the endpoints change.</summary>
    public IReadOnlyList<string> CaptureNames => _current.CaptureNames;

    /// <summary>Counts the changes published: a page that remembers the version it built from knows, in one compare, whether to rebuild.</summary>
    public long Version => _current.Version;

    /// <summary>How many times the machine was asked — the evidence that the desk's tick asks it never.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>How many times a read was asked for: Windows' notifications and the desk's own asks.</summary>
    public int Nudges => Volatile.Read(ref _nudges);

    /// <summary>True while Windows is telling the catalogue about its devices.</summary>
    public bool Listening => _client is not null;

    /// <summary>Raised, on the worker that read, when a read found different endpoints; a page marshals to its own thread.</summary>
    public event Action<AudioEndpointSnapshot>? Changed;

    /// <summary>The first read on a worker, and Windows asked to say when a device comes, goes or changes.</summary>
    public void Start()
    {
        if (OperatingSystem.IsWindows()) Listen();
        Nudge("start");
    }

    /// <summary>Asks for a read: debounced, on a worker, never on the caller's thread. The reason is kept on the snapshot it produces.</summary>
    public void Nudge(string reason)
    {
        if (_disposed) return;
        Interlocked.Increment(ref _nudges);
        _pendingReason = reason;
        try
        {
            _debounce.Change(_debounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // disposed under the nudge: nothing to read for
        }
    }

    /// <summary>
    /// Reads on the calling thread and publishes — the tests' way in, and the worker's. True when the
    /// endpoints changed. Two threads asking at once read once: the second asks again after the first.
    /// </summary>
    public bool ReadNow(string reason)
    {
        _pendingReason = reason;
        return ReadNowGuarded();
    }

    private bool ReadNowGuarded()
    {
        if (_disposed) return false;
        if (Interlocked.Exchange(ref _reading, 1) == 1)
        {
            Interlocked.Exchange(ref _again, 1);   // a read is on: it asks again when it is done
            return false;
        }
        var changed = false;
        try
        {
            changed = ReadAndPublish(_pendingReason);
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the audio endpoints failed.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _reading, 0);
            if (Interlocked.Exchange(ref _again, 0) == 1) Nudge(_pendingReason);
        }
        return changed;
    }

    private bool ReadAndPublish(string reason)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var (render, capture) = Read();
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Interlocked.Increment(ref _reads);
        AudioEndpointSnapshot next;
        lock (_publishGate)
        {
            var before = _current;
            if (before.SameEndpointsAs(render, capture))
            {
                // The same machine: the moment and the cost move, the version and the lists — the pages' identity — do not.
                _current = before with { ReadAtUtc = DateTime.UtcNow, ReadMs = ms, Reason = reason };
                return false;
            }
            next = new AudioEndpointSnapshot(
                render.ToArray(), capture.ToArray(),
                render.Select(e => e.Name).ToArray(), capture.Select(e => e.Name).ToArray(),
                before.Version + 1, DateTime.UtcNow, ms, reason);
            _current = next;
        }
        Changed?.Invoke(next);
        return true;
    }

    /// <summary>"2 outputs · 1 input · read 3 ms, 12 s ago (device added) · listening" — the health facts' and the support ticket's line.</summary>
    public string Words
    {
        get
        {
            var s = _current;
            if (s.Version == 0 && s.ReadAtUtc == DateTime.MinValue) return OperatingSystem.IsWindows() ? "not read yet" : "no Windows audio";
            var age = DateTime.UtcNow - s.ReadAtUtc;
            var ago = age < TimeSpan.FromSeconds(1) ? "just now" : age < TimeSpan.FromMinutes(2) ? $"{age.TotalSeconds:0} s ago" : age < TimeSpan.FromHours(2) ? $"{age.TotalMinutes:0} min ago" : $"{age.TotalHours:0} h ago";
            return $"{s.Render.Count} output{(s.Render.Count == 1 ? "" : "s")} · {s.Capture.Count} input{(s.Capture.Count == 1 ? "" : "s")} · read {s.ReadMs:0} ms, {ago} ({s.Reason}) · {(Listening ? "Windows says when they change" : "not listening")}";
        }
    }

    /// <summary>Off Windows there is no WASAPI: the machine has no endpoints to read, and says so without a warning.</summary>
    private static (IReadOnlyList<AudioEndpoint> Render, IReadOnlyList<AudioEndpoint> Capture) ReadNothing() => (Array.Empty<AudioEndpoint>(), Array.Empty<AudioEndpoint>());

    // ---- Windows ------------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static (IReadOnlyList<AudioEndpoint> Render, IReadOnlyList<AudioEndpoint> Capture) ReadFromWindows()
    {
        using var enumerator = new MMDeviceEnumerator();
        return (Endpoints(enumerator, DataFlow.Render), Endpoints(enumerator, DataFlow.Capture));
    }

    [SupportedOSPlatform("windows")]
    private static List<AudioEndpoint> Endpoints(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var list = new List<AudioEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                string name;
                try
                {
                    name = device.FriendlyName;
                }
                catch (Exception ex)
                {
                    Log.Warn($"An audio endpoint would not say its name ({device.ID}).", ex);
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(name)) list.Add(new AudioEndpoint(device.ID, name));
            }
        }
        return list;
    }

    [SupportedOSPlatform("windows")]
    private void Listen()
    {
        try
        {
            _listener = new MMDeviceEnumerator();
            _client = new NotificationClient(this);
            _listener.RegisterEndpointNotificationCallback(_client);
        }
        catch (Exception ex)
        {
            Log.Warn("Windows would not say when the audio devices change; the lists read on demand only.", ex);
            _client = null;
            _listener?.Dispose();
            _listener = null;
        }
    }

    /// <summary>Windows' word on its devices, each one a nudge: the burst settles, one read follows, on a worker.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioEndpointCatalogue _owner;

        public NotificationClient(AudioEndpointCatalogue owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.Nudge($"device {StateWord(newState)}");

        public void OnDeviceAdded(string pwstrDeviceId) => _owner.Nudge("device added");

        public void OnDeviceRemoved(string deviceId) => _owner.Nudge("device removed");

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Multimedia) _owner.Nudge(flow == DataFlow.Capture ? "default input changed" : "default output changed");
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => _owner.Nudge("device property changed");

        private static string StateWord(DeviceState state) => state switch
        {
            DeviceState.Active => "active",
            DeviceState.Disabled => "disabled",
            DeviceState.NotPresent => "not present",
            DeviceState.Unplugged => "unplugged",
            _ => "changed",
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Dispose();
        if (OperatingSystem.IsWindows()) StopListening();
    }

    [SupportedOSPlatform("windows")]
    private void StopListening()
    {
        try
        {
            if (_listener is not null && _client is not null) _listener.UnregisterEndpointNotificationCallback(_client);
        }
        catch (Exception ex)
        {
            Log.Warn("The audio device listener would not unregister.", ex);
        }
        _client = null;
        _listener?.Dispose();
        _listener = null;
    }
}
