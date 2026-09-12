using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// One device's wire: lines out, lines in (on any thread), a status word. Serial, TCP and UDP
/// links implement it; tests stand in with a fake.
/// </summary>
public interface IDeviceLink : IDisposable
{
    /// <summary>"open", "connecting…", "closed: …" — what the page shows.</summary>
    string Status { get; }

    bool IsOpen { get; }

    /// <summary>Writes one framed line; never throws — a failed write closes the link, which reopens by itself.</summary>
    void Write(string framedLine);

    /// <summary>Raised with every whole line the device sends, on the link's own thread.</summary>
    event Action<string>? LineReceived;
}

/// <summary>
/// The Interactive area at run time: one link per enabled device, opened and reopened by
/// itself, every line in mapped onto the show's protocol and run through the one action layer
/// with the device's name as its origin, the answer written back, and the show's facts written
/// out as they change — throttled like every other feedback. Ports open only while the
/// Interactive area is on.
/// </summary>
public sealed class DeviceService : IDisposable
{
    private sealed class Open
    {
        public required DeviceConfig Config;
        public required IDeviceLink Link;
        public required string Key;
        public Dictionary<string, string>? Heard;
        public bool WasOpen;
        public string LastUnmapped = "";
        public bool AnnounceOpen;
        public string LastIn = "";
        public string LastOut = "";
        public long In;
        public long Out;
    }

    private readonly AppServices _services;
    private readonly CommandRouter _router;
    private readonly Dictionary<string, Open> _open = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _pushTimer;
    private bool _pushPending;
    private bool _disposed;

    public DeviceService(AppServices services)
    {
        _services = services;
        _router = new CommandRouter(services);
        _pushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _pushTimer.Tick += (_, _) =>
        {
            _pushTimer.Stop();
            if (!_pushPending) return;
            _pushPending = false;
            SendFeedback();
        };
        _services.SnapshotPublished += MarkChanged;
        _services.RuntimeChanged += MarkChanged;
    }

    /// <summary>Tests stand in for the wires: a device → a link, or null to leave it unopened.</summary>
    public Func<DeviceConfig, IDeviceLink?>? LinkFactory { get; set; }

    /// <summary>The links open now, by device id.</summary>
    public int OpenCount => _open.Count;

    public IDeviceLink? LinkFor(string deviceId) => _open.TryGetValue(deviceId, out var o) ? o.Link : null;

    /// <summary>"COM3, COM7" — the serial ports this machine has right now, for the page.</summary>
    public static string SerialPortsText()
    {
        try
        {
            var names = SerialPort.GetPortNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            return names.Length == 0 ? "no serial ports found" : string.Join(", ", names);
        }
        catch (Exception ex)
        {
            return "serial ports: " + ex.Message;
        }
    }

    /// <summary>Opens and closes links to match the show (UI thread): called on every state change and from the 1 s poll.</summary>
    public void Reconcile()
    {
        if (_disposed) return;
        var config = _services.State.Interactive;
        var wanted = new Dictionary<string, DeviceConfig>(StringComparer.Ordinal);
        if (config.Enabled)
        {
            // A link that reconnects on its own — a serial port replugged, a TCP host back up, a
        // surface whose port another application let go — has just become a device that knows
        // nothing about the show. Everything is sent again, exactly as it is for one that has only
        // now connected, because a surface whose lamps went dark with the cable and stayed dark
        // when it came back is a surface an operator cannot trust.
        foreach (var open in _open.Values)
        {
            var live = open.Link.IsOpen;
            if (live && !open.WasOpen)
            {
                open.Heard = null;
                open.AnnounceOpen = true;
                MarkChanged();
            }
            open.WasOpen = live;
        }

        foreach (var d in config.Devices)
            {
                if (d.Enabled && d.Id.Length > 0) wanted[d.Id] = d;
            }
        }

        foreach (var (id, open) in _open.ToList())
        {
            if (!wanted.TryGetValue(id, out var d) || KeyOf(d) != open.Key) Close(id);
        }
        foreach (var (id, d) in wanted)
        {
            if (_open.ContainsKey(id)) continue;
            try
            {
                var link = LinkFactory is { } make ? make(d) : Make(d);
                if (link is null)
                {
                    d.Status = DeviceAddress.Describe(d);
                    continue;
                }
                var open = new Open { Config = d, Link = link, Key = KeyOf(d) };
                link.LineReceived += line => OnLine(id, line);
                _open[id] = open;
                d.Status = link.Status;
                Log.Info($"Device '{d.Name}' opening: {DeviceAddress.Describe(d)}");
            }
            catch (Exception ex)
            {
                d.Status = "could not open: " + ex.Message;
                Log.Warn($"Device '{d.Name}' could not open.", ex);
            }
        }
        foreach (var d in config.Devices)
        {
            if (!config.Enabled) d.Status = "Interactive area off";
            else if (!d.Enabled) d.Status = "off";
            else if (_open.TryGetValue(d.Id, out var open)) d.Status = StatusLine(open);
        }
    }

    /// <summary>Refreshes the status words (the 1 s poll) and reconciles, so a device switched on or a link that dropped shows within a second.</summary>
    public void Poll() => Reconcile();

    /// <summary>A line to a device — the DEVICE verb, the cue action, the page's SEND. The name may be blank or * for the first enabled device.</summary>
    public ActionResult Send(string deviceNameOrNumber, string text)
    {
        var line = (text ?? "").Trim();
        if (line.Length == 0) return ActionResult.Refused("Nothing to send — the line the device expects, e.g. RELAY 1.");
        var config = _services.State.Interactive;
        var device = Interactive.Find(config, deviceNameOrNumber);
        if (device is null) return ActionResult.Refused(config.Devices.Count == 0 ? "No device on the Interactive page." : $"No device named '{deviceNameOrNumber}' on the Interactive page.");
        if (!config.Enabled) return ActionResult.Refused("The Interactive area is off — switch it on on the Interactive page.");
        if (!device.Enabled) return ActionResult.Refused($"Device '{device.Name}' is switched off.");
        if (!_open.TryGetValue(device.Id, out var open))
        {
            Reconcile();
            if (!_open.TryGetValue(device.Id, out open)) return ActionResult.Failed($"Device '{device.Name}' is not open: {device.Status}");
        }
        WriteTo(open, line);
        return ActionResult.Done($"Device {device.Name}: {line}");
    }

    /// <summary>What the devices are doing, for STATE and the page.</summary>
    public IReadOnlyList<object> Rows()
    {
        var config = _services.State.Interactive;
        return config.Devices.Select((d, i) => (object)new
        {
            n = i + 1,
            name = d.Name,
            link = d.Link.ToString().ToLowerInvariant(),
            address = DeviceAddress.Describe(d),
            enabled = d.Enabled,
            open = _open.TryGetValue(d.Id, out var o) && o.Link.IsOpen,
            status = d.Status,
            lastIn = _open.TryGetValue(d.Id, out var oi) ? oi.LastIn : "",
            lastOut = _open.TryGetValue(d.Id, out var oo) ? oo.LastOut : "",
        }).ToList();
    }

    private static string KeyOf(DeviceConfig d) => $"{d.Link}|{d.Port}|{d.Baud}|{d.NetPort}|{d.LineEnding}";

    private static string StatusLine(Open open)
    {
        var s = open.Link.Status;
        if (open.In > 0 || open.Out > 0) s += $" · in {open.In}{(open.LastIn.Length > 0 ? " (" + open.LastIn + ")" : "")} · out {open.Out}{(open.LastOut.Length > 0 ? " (" + open.LastOut + ")" : "")}";
        return s;
    }

    private static IDeviceLink? Make(DeviceConfig d)
    {
        switch (d.Link)
        {
            case DeviceLink.Serial:
            {
                var serialPort = DeviceAddress.SerialPort(d.Port);
                return serialPort.Length == 0 ? null : new SerialDeviceLink(serialPort, d.Baud);
            }
            case DeviceLink.Midi:
                // An empty port name takes whatever surface is plugged in, which is what an
                // operator with one of them expects and saves a trip to the Admin page.
                return new MidiSurfaceLink(d.Port);
            case DeviceLink.Tcp:
                return DeviceAddress.TryParseHost(d.Port, d.NetPort, out var host, out var tcpPort) ? new TcpDeviceLink(host, tcpPort) : null;
            default:
                return DeviceAddress.TryParseHost(d.Port, d.NetPort, out var uhost, out var uport) ? new UdpDeviceLink(uhost, uport) : null;
        }
    }

    private void Close(string id)
    {
        if (!_open.Remove(id, out var open)) return;
        try
        {
            open.Link.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Device '{open.Config.Name}' close issue.", ex);
        }
        Log.Info($"Device '{open.Config.Name}' closed.");
    }

    private readonly List<string> _lamps = new();

    private void WriteTo(Open open, string line)
    {
        // A surface that cannot read words can still light. A fact the show sends is turned into
        // whatever lamps this device's own rows say it lights, and the words themselves are not
        // sprayed at it — a MIDI port given "LOOK Walk-in" as bytes is a fault nobody could
        // diagnose from the surface.
        if (open.Config.Link == DeviceLink.Midi)
        {
            _lamps.Clear();
            DeviceMap.Lamps(open.Config, line, _lamps);
            foreach (var lamp in _lamps)
            {
                open.Link.Write(lamp);
                open.Out++;
                open.LastOut = lamp;
            }
            if (_lamps.Count > 0) open.Config.Status = StatusLine(open);
            return;
        }

        open.Link.Write(DeviceLines.Frame(line, open.Config.LineEnding));
        open.Out++;
        open.LastOut = line;
        open.Config.Status = StatusLine(open);
    }

    private readonly Dictionary<string, Action<string>> _learning = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The next line this device sends goes to the caller instead of being run.
    ///
    /// This is how a control surface is mapped without anybody knowing its note numbers: press the
    /// pad, and the row writes itself. The vendor's published sheet is a starting point and the
    /// hardware is the truth, which is why the desk asks rather than assumes.
    /// </summary>
    public void Learn(string deviceName, Action<string> onLine)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return;
        _learning[deviceName.Trim()] = onLine;
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Id, string Line)> _inbound = new();
    private int _draining;

    /// <summary>
    /// A line from a device, on the link's thread: queued, and run on the UI thread like every
    /// remote command.
    ///
    /// It used to post a closure per line, which was right while every device was a board sending a
    /// few lines a second. A control surface is not that even after its faders are sampled — sixteen
    /// encoders moving at once is hundreds of closures a second, each one an allocation on somebody
    /// else's driver thread and a turn of the dispatcher on the thread that draws the desk. So lines
    /// go into a queue and ONE closure is posted for the lot, on the leading edge: the first line
    /// still runs in the very next dispatcher turn, and everything arriving behind it rides the
    /// same drain rather than queueing a turn each.
    /// </summary>
    private void OnLine(string id, string line)
    {
        _inbound.Enqueue((id, line));
        if (Interlocked.Exchange(ref _draining, 1) != 0) return;
        Dispatcher.UIThread.Post(Drain);
    }

    private void Drain()
    {
        try
        {
            while (_inbound.TryDequeue(out var item)) Handle(item.Id, item.Line);
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
            // A line that arrived while the flag was still set would otherwise wait for the next
            // one to wake the drain, so the queue is looked at once more after it is cleared.
            if (!_inbound.IsEmpty && Interlocked.Exchange(ref _draining, 1) == 0) Dispatcher.UIThread.Post(Drain);
        }
    }

    private void Handle(string id, string line)
    {
        {
            if (_disposed || !_open.TryGetValue(id, out var open)) return;
            open.In++;
            open.LastIn = line;
            open.Config.Status = StatusLine(open);

            // A row waiting to be learned takes this line and nothing else happens: the operator is
            // pressing the pad to say which one it is, not asking the show to do anything.
            if (_learning.Remove(open.Config.Name, out var learner))
            {
                Log.Info($"Device '{open.Config.Name}' learned '{line}'.");
                learner(line);
                return;
            }

            var command = DeviceMap.Resolve(open.Config, line);
            if (command is null)
            {
                // Said once per line, not once per message. Sixteen unmapped encoders on a control
                // surface are hundreds of identical lines a second, and a log nobody can read
                // through is a log that hides the one line that mattered.
                if (open.LastUnmapped != line)
                {
                    open.LastUnmapped = line;
                    Log.Info($"Device '{open.Config.Name}' said '{line}' — no trigger for it.");
                }
                if (open.Config.EchoReplies) WriteTo(open, $"ERR no trigger for '{line}'");
                return;
            }
            open.LastUnmapped = "";
            var origin = new ActionOrigin(OriginKind.Device, open.Config.Name);
            _ = RunAsync(open, command, origin);
        }
    }

    private async Task RunAsync(Open open, string command, ActionOrigin origin)
    {
        string reply;
        try
        {
            reply = await _router.ExecuteAsync(ControlProtocol.Parse(command), origin);
        }
        catch (Exception ex)
        {
            reply = "ERR " + ex.Message;
            Log.Error($"Device '{open.Config.Name}' command failed: {command}", ex);
        }
        if (open.Config.EchoReplies && _open.ContainsKey(open.Config.Id))
        {
            // A long OK payload (STATUS) stays on the desk; a device wants a word.
            var word = reply.StartsWith("OK", StringComparison.Ordinal) ? "OK" : reply;
            WriteTo(open, word.Length > 200 ? word[..200] : word);
        }
    }

    private void MarkChanged()
    {
        if (_disposed || _open.Count == 0) return;
        _pushPending = true;
        if (!_pushTimer.IsEnabled) _pushTimer.Start();
    }

    /// <summary>The show's facts to every device that hears them — only what changed since it last heard.</summary>
    private void SendFeedback()
    {
        if (_disposed) return;
        var listeners = _open.Values.Where(o => o.Config.HearsShow).ToList();
        if (listeners.Count == 0) return;
        var facts = DeviceFeedback.Facts(_router.StateJson());
        foreach (var open in listeners)
        {
            // A device that has just arrived is told so, as a fact like any other — so a row can
            // light a lamp, or wake a surface that needs a first word, without the desk growing a
            // second way of saying things. It rides the throttled feedback rather than the
            // reconcile, because writing to a device sets its status, and a status change is a
            // model change that runs the reconcile again: announcing from there was a loop.
            if (open.AnnounceOpen)
            {
                open.AnnounceOpen = false;
                WriteTo(open, "OPEN 1");
            }
            var changes = DeviceFeedback.Changes(facts, open.Heard);
            foreach (var line in changes) WriteTo(open, line);
            open.Heard = new Dictionary<string, string>(facts, StringComparer.Ordinal);
        }
    }

    /// <summary>Sends every fact to one device now — a device that just connected, or the page's RESEND.</summary>
    public void Resend(DeviceConfig d)
    {
        if (!_open.TryGetValue(d.Id, out var open)) return;
        open.Heard = null;
        _pushPending = true;
        SendFeedback();
    }

    public void Dispose()
    {
        _disposed = true;
        _pushTimer.Stop();
        _services.SnapshotPublished -= MarkChanged;
        _services.RuntimeChanged -= MarkChanged;
        foreach (var id in _open.Keys.ToList()) Close(id);
    }
}

/// <summary>A device on a serial port — an Arduino, a Teensy, an RS-232 controller. Opened on a thread; reopened after a fault.</summary>
public sealed class SerialDeviceLink : IDeviceLink
{
    private readonly string _port;
    private readonly int _baud;
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _buffer = new();
    private SerialPort? _serial;
    private volatile string _status = "opening…";
    private volatile bool _open;

    public SerialDeviceLink(string port, int baud)
    {
        _port = port;
        _baud = baud;
        _ = Task.Run(LoopAsync);
    }

    public string Status => _status;

    public bool IsOpen => _open;

    public event Action<string>? LineReceived;

    public void Write(string framedLine)
    {
        try
        {
            _serial?.Write(framedLine);
        }
        catch (Exception ex)
        {
            _status = "write failed: " + ex.Message;
            _open = false;
        }
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            SerialPort? serial = null;
            try
            {
                serial = new SerialPort(_port, _baud, Parity.None, 8, StopBits.One)
                {
                    NewLine = "\n",
                    ReadTimeout = 250,
                    WriteTimeout = 1000,
                    DtrEnable = true,   // an Arduino wants DTR: without it many boards stay silent
                    RtsEnable = true,
                    Encoding = Encoding.ASCII,
                };
                serial.Open();
                _serial = serial;
                _open = true;
                _status = $"open ({_port} at {_baud})";
                var chunk = new byte[512];
                while (!ct.IsCancellationRequested)
                {
                    int read;
                    try
                    {
                        read = serial.Read(chunk, 0, chunk.Length);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                    if (read <= 0) continue;
                    _buffer.Append(Encoding.ASCII.GetString(chunk, 0, read));
                    foreach (var line in DeviceLines.Split(_buffer)) LineReceived?.Invoke(line);
                }
            }
            catch (Exception ex)
            {
                // Dispose cancels and then closes the port under the read, which throws: that is
                // the closing, not a fault. Left uncaught it faulted the loop's task, and the
                // runtime's unobserved-exception sweep counted it against the health line.
                if (ct.IsCancellationRequested) break;
                _status = $"closed: {ex.Message} — retrying";
            }
            finally
            {
                _open = false;
                _serial = null;
                try
                {
                    serial?.Dispose();
                }
                catch
                {
                    // the port went away with the device
                }
            }
            if (ct.IsCancellationRequested) break;
            try
            {
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        _status = "closed";
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _serial?.Dispose();
        }
        catch
        {
            // already gone
        }
    }
}

/// <summary>A device over TCP — a Raspberry Pi, an ESP32, a show controller: connected on a thread, reconnected after a drop.</summary>
public sealed class TcpDeviceLink : IDeviceLink
{
    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _buffer = new();
    private NetworkStream? _stream;
    private volatile string _status = "connecting…";
    private volatile bool _open;

    public TcpDeviceLink(string host, int port)
    {
        _host = host;
        _port = port;
        _ = Task.Run(LoopAsync);
    }

    public string Status => _status;

    public bool IsOpen => _open;

    public event Action<string>? LineReceived;

    public void Write(string framedLine)
    {
        try
        {
            var stream = _stream;
            if (stream is null) return;
            var bytes = Encoding.UTF8.GetBytes(framedLine);
            stream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            _status = "write failed: " + ex.Message;
            _open = false;
        }
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient { NoDelay = true };
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectTimeout.CancelAfter(5000);
                await client.ConnectAsync(_host, _port, connectTimeout.Token);
                _stream = client.GetStream();
                _open = true;
                _status = $"open ({_host}:{_port})";
                var chunk = new byte[1024];
                while (!ct.IsCancellationRequested)
                {
                    var read = await _stream.ReadAsync(chunk, ct);
                    if (read <= 0) break;   // the device hung up
                    _buffer.Append(Encoding.UTF8.GetString(chunk, 0, read));
                    foreach (var line in DeviceLines.Split(_buffer)) LineReceived?.Invoke(line);
                }
                _status = "closed by the device — reconnecting";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _status = $"closed: {ex.Message} — reconnecting";
            }
            finally
            {
                _open = false;
                _stream = null;
                client?.Dispose();
            }
            if (ct.IsCancellationRequested) break;
            try
            {
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        _status = "closed";
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // already gone
        }
    }
}

/// <summary>A device over UDP — lines out as datagrams, lines back from the same socket. Connectionless: open the moment it is made.</summary>
public sealed class UdpDeviceLink : IDeviceLink
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint? _to;
    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private volatile string _status;

    public UdpDeviceLink(string host, int port)
    {
        _host = host;
        _port = port;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _to = IPAddress.TryParse(host, out var ip) ? new IPEndPoint(ip, port) : null;
        _status = $"open ({host}:{port}, UDP)";
        _ = Task.Run(ReceiveAsync);
    }

    public string Status => _status;

    public bool IsOpen => true;

    public event Action<string>? LineReceived;

    public void Write(string framedLine)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(framedLine);
            if (_to is not null) _udp.Send(bytes, bytes.Length, _to);
            else _udp.Send(bytes, bytes.Length, _host, _port);
        }
        catch (Exception ex)
        {
            _status = "send failed: " + ex.Message;
        }
    }

    private async Task ReceiveAsync()
    {
        var ct = _cts.Token;
        var buffer = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct);
                buffer.Append(Encoding.UTF8.GetString(result.Buffer));
                if (buffer.Length > 0 && buffer[^1] is not ('\n' or '\r')) buffer.Append('\n');   // a datagram is a line
                foreach (var line in DeviceLines.Split(buffer)) LineReceived?.Invoke(line);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _status = $"receive issue: {ex.Message}";
                try
                {
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _udp.Close();
        }
        catch
        {
            // already closed
        }
    }
}
