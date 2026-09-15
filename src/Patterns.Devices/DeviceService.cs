using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.Devices;

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

    /// <summary>Writes one frame as bytes — an OSC message, a digest and a command; a link that only knows text writes the bytes as UTF-8.</summary>
    void WriteBytes(byte[] frame) => Write(Encoding.UTF8.GetString(frame));

    /// <summary>
    /// Writes one frame and says how far it got: a connection that took the bytes is Delivered,
    /// an HTTP answer is Delivered and a 2xx Accepted, a datagram or a note is Sent and no more.
    /// The default is the write, then the link's own word on whether it is still open.
    /// </summary>
    Task<LinkDelivery> DeliverAsync(byte[] frame)
    {
        WriteBytes(frame);
        return Task.FromResult(IsOpen ? LinkDelivery.Delivered() : LinkDelivery.Failed(Status));
    }

    /// <summary>Raised with every whole line the device sends, on the link's own thread.</summary>
    event Action<string>? LineReceived;
}

/// <summary>How far one frame got on its link.</summary>
public readonly record struct LinkDelivery(ConfirmLevel Reached, bool Ok, string Words)
{
    public static readonly LinkDelivery SentOnly = new(ConfirmLevel.Sent, true, "sent");

    public static LinkDelivery Delivered(string words = "delivered") => new(ConfirmLevel.Delivered, true, words);

    public static LinkDelivery Accepted(string words) => new(ConfirmLevel.Accepted, true, words);

    /// <summary>The box answered and said no — an HTTP 4xx or 5xx.</summary>
    public static LinkDelivery Rejected(string words) => new(ConfirmLevel.Delivered, false, words);

    /// <summary>The bytes never got out: a link that is down, a request that failed.</summary>
    public static LinkDelivery Failed(string words) => new(ConfirmLevel.Sent, false, words);
}

/// <summary>
/// What became of one line sent to a box: the level the show wanted, the level reached, and the
/// box's own words. Journaled when it lands, shown on the device's card, and waited for by the
/// twin's wall switch.
/// </summary>
public sealed record DeviceReceipt(string Device, string Words, ConfirmLevel Wanted, ConfirmLevel Reached, bool Ok, string Answer, DateTime AtUtc, string Execution = "")
{
    /// <summary>"Projector: POWER ON — accepted (POWR: OK)"; "Projector: INPUT HDMI 1 — rejected: INPT: out of parameter"; "Switcher: POST /route — no answer in 2 s (delivered, not accepted)".</summary>
    public string Line => Ok
        ? $"{Device}: {Words} — {DeviceConfirmation.Label(Reached)}{(Answer.Length > 0 ? $" ({Answer})" : "")}"
        : $"{Device}: {Words} — {Answer}";
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
        /// <summary>The device's vocabulary for this connection: what its words become, what its bytes mean.</summary>
        public required ProfileSession Session;
        public DateTime NextPollUtc;
        public Dictionary<string, string>? Heard;
        public bool WasOpen;
        public string LastUnmapped = "";
        public bool AnnounceOpen;
        public string LastIn = "";
        public string LastOut = "";
        public long In;
        public long Out;
    }

    /// <summary>One line waiting for its receipt.</summary>
    private sealed class Pending
    {
        public required Open Open;
        public required string Words;
        public required string Key;
        public required ConfirmLevel Wanted;
        public required long Seq;
        public ConfirmLevel Reached;
        public bool Observing;
        public string Expect = "";
        /// <summary>The cue execution this line belongs to, "" for a line nobody's row waits on (the wire's DEVICE verb, the page's SEND).</summary>
        public string Execution = "";
        public readonly TaskCompletionSource<DeviceReceipt> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly IDeviceHost _services;
    private readonly IRouter _router;
    private readonly Dictionary<string, Open> _open = new(StringComparer.Ordinal);
    private readonly List<Pending> _pendings = new();
    private readonly List<(long Seq, DeviceReceipt Receipt)> _settled = new();   // the last receipts landed, by their line's sequence: a waiter that marked before a line went sees its receipt even when it landed at once
    private const int SettledKept = 64;
    private readonly IDispatchTimer _pushTimer;
    private long _seq;
    private bool _pushPending;
    private bool _disposed;

    public DeviceService(IDeviceHost services)
    {
        _services = services;
        _router = services.Router;
        _pushTimer = Dispatch.Timer(TimeSpan.FromMilliseconds(200));
        _pushTimer.Tick += () =>
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

    /// <summary>Every receipt as it lands, on the UI thread — the journal's and the page's.</summary>
    public event Action<DeviceReceipt>? Receipt;

    /// <summary>The last receipt that failed — a no, a silence, a line that could not go — until that box answers again; null while every box is answering.</summary>
    public DeviceReceipt? LastFailed { get; private set; }

    /// <summary>The health line's and the glance line's clause: "" while every box answers, else the last that did not.</summary>
    public string HealthWords => LastFailed is { } r ? "DEVICE: " + r.Line : "";

    /// <summary>A mark before a cue fires, so what it sent can be waited for.</summary>
    public long Mark() => Interlocked.Read(ref _seq);

    /// <summary>How many lines sent since the mark are still waiting for their receipt.</summary>
    public int PendingSince(long mark)
    {
        lock (_pendings) return _pendings.Count(p => p.Seq > mark);
    }

    /// <summary>How many lines were sent since the mark, their receipts landed or still to come — a line whose receipt landed at once (Delivered: the socket took it) counts.</summary>
    public int SentSince(long mark)
    {
        lock (_pendings) return _pendings.Count(p => p.Seq > mark) + _settled.Count(r => r.Seq > mark);
    }

    /// <summary>The receipts of every line sent since the mark, once each has landed — by an answer, or by its timeout — the ones already landed included, in the order the lines went.</summary>
    public Task<IReadOnlyList<DeviceReceipt>> ConfirmSince(long mark)
    {
        List<Task<DeviceReceipt>> waits;
        List<(long Seq, DeviceReceipt Receipt)> landed;
        lock (_pendings)
        {
            waits = _pendings.Where(p => p.Seq > mark).Select(p => p.Done.Task).ToList();
            landed = _settled.Where(r => r.Seq > mark).ToList();
        }
        return Task.WhenAll(waits).ContinueWith(t => (IReadOnlyList<DeviceReceipt>)landed.Select(r => r.Receipt).Concat(t.Result).ToList(), TaskContinuationOptions.ExecuteSynchronously);
    }

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
                open.Session.OnConnected();
                open.NextPollUtc = DateTime.UtcNow.AddSeconds(1);   // a projector is asked how it is a moment after it answers
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
            else if (!ReferenceEquals(open.Config, d))
            {
                // The same box, a new object for it — a show landed from the twin, a file reloaded:
                // what is read at send time (the level asked, the timeout, the query, the password)
                // must be the page's current words, not the object that was there when the link opened.
                d.Status = open.Config.Status;
                open.Config = d;
            }
        }
        foreach (var (id, d) in wanted)
        {
            if (_open.ContainsKey(id)) continue;
            try
            {
                var session = ProfileSession.For(d);
                var link = LinkFactory is { } make ? make(d) : Make(d, session);
                if (link is null)
                {
                    d.Status = DeviceAddress.Describe(d);
                    continue;
                }
                var open = new Open { Config = d, Link = link, Key = KeyOf(d), Session = session };
                link.LineReceived += line => OnLine(id, line);
                _open[id] = open;
                d.Status = link.Status;
                Log.Info($"Device '{d.Name}' opening: {DeviceAddress.Describe(d)}");
            }
            catch (Exception ex)
            {
                d.Status = "could not open: " + ex.Message;
                d.Runtime = d.Runtime.Failed("could not open: " + ex.Message, DateTime.UtcNow);
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

    /// <summary>Refreshes the status words (the 1 s poll) and reconciles, so a device switched on or a link that dropped shows within a second — and asks a box that answers questions (a projector's POWER ?) how it is.</summary>
    public void Poll()
    {
        Reconcile();
        var now = DateTime.UtcNow;
        foreach (var open in _open.Values)
        {
            if (open.Session.PollWords is not { } poll || !open.Link.IsOpen || now < open.NextPollUtc) continue;
            open.NextPollUtc = now + open.Session.PollEvery;
            WriteTo(open, poll, out _, quiet: true);
        }
        // The cards' history lines, their ages moved on: a string a second per box, raised only when it changed.
        foreach (var d in _services.State.Interactive.Devices)
        {
            var words = d.Runtime.Words(now);
            if (d.HistoryText != words) d.HistoryText = words;
        }
    }

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
        // The line belongs to the cue whose steps are running, if one is: its receipt settles that cue's row.
        var execution = _services.ExecutionInHand;
        if (!WriteTo(open, line, out var problem, track: true, execution: execution)) return ActionResult.Refused(problem);
        // Dispatched, not done: a datagram is all a datagram can be, so it is Done; a line a box
        // answers is Requested until the receipt says what the box made of it — the cue that sent
        // it settles then, and never pretends before.
        var wanted = DeviceConfirmation.Effective(device);
        return wanted == ConfirmLevel.Sent
            ? ActionResult.Done($"Device {device.Name}: {line} — sent ({DeviceConfirmation.Limit(device.Link, device.Profile)})")
            : ActionResult.Requested($"Device {device.Name}: {line} — sent; awaiting {DeviceConfirmation.Label(wanted)}");
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
            profile = d.Profile.ToString().ToLowerInvariant(),
            address = DeviceAddress.Describe(d),
            enabled = d.Enabled,
            open = _open.TryGetValue(d.Id, out var o) && o.Link.IsOpen,
            status = d.Status,
            lastIn = _open.TryGetValue(d.Id, out var oi) ? oi.LastIn : "",
            lastOut = _open.TryGetValue(d.Id, out var oo) ? oo.LastOut : "",
            // What the box did lately, with its times: the last line, its reply and the level reached, the observed state, the last failure — and whether that failure is its last word.
            lastCommand = d.Runtime.LastCommand,
            lastCommandUtc = d.Runtime.LastCommandUtc,
            lastReply = d.Runtime.LastReply,
            lastReplyUtc = d.Runtime.LastReplyUtc,
            confirmed = d.Runtime.LastConfirmed is { } level ? DeviceConfirmation.Label(level) : "",
            confirmedUtc = d.Runtime.LastConfirmedUtc,
            observed = d.Runtime.LastObserved,
            observedUtc = d.Runtime.LastObservedUtc,
            lastFailure = d.Runtime.LastFailure,
            lastFailureUtc = d.Runtime.LastFailureUtc,
            failing = d.Runtime.Failing,
            history = d.Runtime.Words(DateTime.UtcNow),
        }).ToList();
    }

    private static string KeyOf(DeviceConfig d) => $"{d.Link}|{d.Port}|{d.Baud}|{d.NetPort}|{d.LineEnding}|{d.Profile}|{d.Secret}";

    private static string StatusLine(Open open)
    {
        var s = open.Link.Status;
        if (open.In > 0 || open.Out > 0) s += $" · in {open.In}{(open.LastIn.Length > 0 ? " (" + open.LastIn + ")" : "")} · out {open.Out}{(open.LastOut.Length > 0 ? " (" + open.LastOut + ")" : "")}";
        return s;
    }

    private static IDeviceLink? Make(DeviceConfig d, ProfileSession session)
    {
        switch (d.Link)
        {
            case DeviceLink.Http:
                return d.Port.Trim().Length == 0 ? null : new HttpDeviceLink(d.Port);
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
                return DeviceAddress.TryParseHost(d.Port, d.NetPort, out var host, out var tcpPort) ? new TcpDeviceLink(host, tcpPort, session.Split) : null;
            default:
                return DeviceAddress.TryParseHost(d.Port, d.NetPort, out var uhost, out var uport) ? new UdpDeviceLink(uhost, uport, session.Decode) : null;
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

    private void WriteTo(Open open, string line) => WriteTo(open, line, out _);

    /// <summary>
    /// The words to the device through its profile — a board's line as it is, a projector's %1POWR 1,
    /// a media server's OSC or JSON-RPC. False, with the reason, when the words are not the
    /// profile's; a quiet write (the poll) leaves the counters alone.
    /// </summary>
    private bool WriteTo(Open open, string line, out string problem, bool quiet = false, bool track = false, string execution = "")
    {
        problem = "";
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
            return true;
        }

        var frames = open.Session.Encode(line, out problem);
        if (frames.Count == 0)
        {
            if (problem.Length == 0) problem = $"'{line}' could not be sent to {open.Config.Name}.";
            Log.Warn($"Device '{open.Config.Name}': {problem}");
            return false;
        }
        if (track)
        {
            // The DEVICE verb, a cue's step, the page's SEND: followed to its receipt, and the
            // card's last line. The show's facts and the poll are not — a board hearing
            // BLACKOUT 1 owes nobody an answer.
            open.Config.Runtime = open.Config.Runtime.Sent(line, DateTime.UtcNow);
            var pending = new Pending
            {
                Open = open,
                Words = line,
                Key = open.Session.SentKey(line),
                Wanted = DeviceConfirmation.Effective(open.Config),
                Seq = Interlocked.Increment(ref _seq),
                Execution = execution,
            };
            lock (_pendings) _pendings.Add(pending);
            _ = DeliverAsync(pending, frames);
        }
        else
        {
            foreach (var frame in frames) open.Link.WriteBytes(frame);
        }
        if (!quiet)
        {
            open.Out++;
            open.LastOut = line;
        }
        open.Config.Status = StatusLine(open);
        return true;
    }

    // ---- receipts ---------------------------------------------------------------------------------

    /// <summary>The frames onto the link, then as far up the levels as the link and the box take them, within the device's timeout.</summary>
    private async Task DeliverAsync(Pending pending, IReadOnlyList<byte[]> frames)
    {
        var timeout = DeviceConfirmation.Timeout(pending.Open.Config);
        try
        {
            var last = LinkDelivery.SentOnly;
            foreach (var frame in frames)
            {
                last = await pending.Open.Link.DeliverAsync(frame);
                if (!last.Ok)
                {
                    Complete(pending, last.Reached, false, last.Reached == ConfirmLevel.Sent ? $"not delivered: {last.Words}" : $"rejected: {last.Words}");
                    return;
                }
            }
            pending.Reached = last.Reached;
            if (last.Reached == ConfirmLevel.Sent)
            {
                // A datagram, a note: nothing comes back, and the device's level says so already.
                Complete(pending, ConfirmLevel.Sent, pending.Wanted <= ConfirmLevel.Sent, last.Words);
                return;
            }
            if (pending.Reached >= pending.Wanted)
            {
                Complete(pending, pending.Reached, true, last.Words);
                return;
            }
            if (pending.Wanted == ConfirmLevel.Observed && pending.Reached == ConfirmLevel.Accepted)
            {
                Observe(pending);
            }
            // Else the box answers on its own time: the session reads the reply in Handle, and the timeout is the fence.
            await Task.Delay(timeout);
            Complete(pending, pending.Reached, false, $"no answer in {timeout.TotalSeconds:0.#} s ({DeviceConfirmation.Label(pending.Reached)}, not {DeviceConfirmation.Label(pending.Wanted)})");
        }
        catch (Exception ex)
        {
            Complete(pending, pending.Reached, false, "send failed: " + ex.Message);
        }
    }

    /// <summary>Accepted, and Observed wanted: the query goes, and the next answer that carries the expected words closes the receipt.</summary>
    private void Observe(Pending pending)
    {
        var cfg = pending.Open.Config;
        pending.Observing = true;
        pending.Expect = cfg.ObserveExpect.Trim();
        var frames = pending.Open.Session.Encode(cfg.ObserveQuery, out var problem);
        if (frames.Count == 0)
        {
            Complete(pending, ConfirmLevel.Accepted, false, $"accepted, not observed — the query could not be sent ({problem})");
            return;
        }
        foreach (var frame in frames) pending.Open.Link.WriteBytes(frame);
    }

    /// <summary>A reply, on the UI thread: the oldest line waiting on this device takes it as its yes, its no, or its observation.</summary>
    private void Acknowledge(Open open, ProfileReply reply, string line)
    {
        Pending? pending;
        lock (_pendings) pending = _pendings.FirstOrDefault(p => ReferenceEquals(p.Open, open) && (p.Key.Length == 0 || reply.AckKey.Length == 0 || p.Key == reply.AckKey));
        if (pending is null) return;
        if (pending.Observing)
        {
            if (pending.Expect.Length == 0 || line.Contains(pending.Expect, StringComparison.OrdinalIgnoreCase) || reply.Words.Contains(pending.Expect, StringComparison.OrdinalIgnoreCase))
            {
                Complete(pending, ConfirmLevel.Observed, true, reply.Words);
            }
            else if (reply.IsError)
            {
                Complete(pending, ConfirmLevel.Accepted, false, $"accepted, not observed — {reply.Words}");
            }
            return;   // another answer: the observation waits for its own
        }
        if (reply.IsError)
        {
            Complete(pending, pending.Reached, false, $"rejected: {reply.Words}");
            return;
        }
        if (!reply.Ack) return;
        pending.Reached = ConfirmLevel.Accepted;
        if (pending.Wanted == ConfirmLevel.Observed) Observe(pending);
        else Complete(pending, ConfirmLevel.Accepted, true, reply.Words);
    }

    /// <summary>The receipt, once: off the list, to the event on the UI thread, onto the card.</summary>
    private void Complete(Pending pending, ConfirmLevel reached, bool ok, string answer)
    {
        var receipt = new DeviceReceipt(pending.Open.Config.Name, pending.Words, pending.Wanted, reached, ok, answer, DateTime.UtcNow, pending.Execution);
        lock (_pendings)
        {
            if (!_pendings.Remove(pending)) return;
            _settled.Add((pending.Seq, receipt));
            if (_settled.Count > SettledKept) _settled.RemoveAt(0);
        }
        pending.Done.TrySetResult(receipt);
        Dispatch.Post(() =>
        {
            if (_disposed) return;
            if (!ok) Log.Warn($"Device '{receipt.Device}': {receipt.Line}");
            if (!ok) LastFailed = receipt;
            else if (LastFailed is { } failed && failed.Device == receipt.Device) LastFailed = null;   // the box that said no answers again
            if (_open.TryGetValue(pending.Open.Config.Id, out var open)) open.Config.Status = StatusLine(open) + " · " + (ok ? DeviceConfirmation.Label(reached) : receipt.Answer);
            var config = open?.Config ?? pending.Open.Config;
            config.Runtime = ok
                ? config.Runtime.Confirmed(reached, receipt.Answer, receipt.AtUtc)
                : config.Runtime.Failed($"{receipt.Words} — {receipt.Answer}", receipt.AtUtc);
            Receipt?.Invoke(receipt);
        });
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
        Dispatch.Post(Drain);
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
            if (!_inbound.IsEmpty && Interlocked.Exchange(ref _draining, 1) == 0) Dispatch.Post(Drain);
        }
    }

    private void Handle(string id, string line)
    {
        {
            if (_disposed || !_open.TryGetValue(id, out var open)) return;
            open.In++;
            // What the box said, in the profile's words — and what it asks to send next (a media
            // server's handle found is the play that wanted it).
            var reply = open.Session.OnReceived(line);
            open.LastIn = reply.Words;
            open.Config.Runtime = open.Config.Runtime.Replied(reply.Words.Length > 0 ? reply.Words : line, DateTime.UtcNow);
            foreach (var frame in reply.SendNext)
            {
                open.Link.WriteBytes(frame);
                open.Out++;
            }
            if (reply.IsError) Log.Warn($"Device '{open.Config.Name}' answered: {reply.Words}");
            open.Config.Status = StatusLine(open);
            Acknowledge(open, reply, line);

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
                // A box's own answer — a projector's status, a media server's result, a web API's
                // reply — was read above; it is not a command to the show unless a trigger row says so.
                if (open.Config.Profile != DeviceProfile.Lines || open.Config.Link == DeviceLink.Http) return;
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
        List<Pending> waiting;
        lock (_pendings) waiting = _pendings.ToList();
        foreach (var p in waiting) Complete(p, p.Reached, false, "the Interactive area closed before the box answered");
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
    private readonly Func<StringBuilder, IReadOnlyList<string>> _split;
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _buffer = new();
    private NetworkStream? _stream;
    private volatile string _status = "connecting…";
    private volatile bool _open;

    /// <param name="split">How the byte stream is cut into frames — lines unless the box frames otherwise (Pixera's 0xPX).</param>
    public TcpDeviceLink(string host, int port, Func<StringBuilder, IReadOnlyList<string>>? split = null)
    {
        _host = host;
        _port = port;
        _split = split ?? DeviceLines.Split;
        _ = Task.Run(LoopAsync);
    }

    public string Status => _status;

    public bool IsOpen => _open;

    public event Action<string>? LineReceived;

    public void Write(string framedLine) => WriteBytes(Encoding.UTF8.GetBytes(framedLine));

    public void WriteBytes(byte[] frame)
    {
        try
        {
            var stream = _stream;
            if (stream is null) return;
            stream.Write(frame, 0, frame.Length);
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
                    foreach (var line in _split(_buffer)) LineReceived?.Invoke(line);
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
    private readonly Func<byte[], string?> _decode;
    private readonly CancellationTokenSource _cts = new();
    private volatile string _status;

    /// <param name="decode">A datagram as text — OSC decoded to its address and arguments; null for one that is not the box's.</param>
    public UdpDeviceLink(string host, int port, Func<byte[], string?>? decode = null)
    {
        _host = host;
        _port = port;
        _decode = decode ?? (bytes => Encoding.UTF8.GetString(bytes));
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _to = IPAddress.TryParse(host, out var ip) ? new IPEndPoint(ip, port) : null;
        _status = $"open ({host}:{port}, UDP)";
        _ = Task.Run(ReceiveAsync);
    }

    public string Status => _status;

    public bool IsOpen => true;

    /// <summary>The socket's own end, so a box (or a test) can answer to it.</summary>
    public IPEndPoint? LocalEndpoint => _udp.Client.LocalEndPoint as IPEndPoint;

    public event Action<string>? LineReceived;

    public void Write(string framedLine) => WriteBytes(Encoding.UTF8.GetBytes(framedLine));

    public void WriteBytes(byte[] frame)
    {
        try
        {
            if (_to is not null) _udp.Send(frame, frame.Length, _to);
            else _udp.Send(frame, frame.Length, _host, _port);
        }
        catch (Exception ex)
        {
            _status = "send failed: " + ex.Message;
        }
    }

    /// <summary>A datagram is sent and no more: nothing on this link says it arrived.</summary>
    public Task<LinkDelivery> DeliverAsync(byte[] frame)
    {
        var before = _status;
        WriteBytes(frame);
        return Task.FromResult(ReferenceEquals(before, _status) || !_status.StartsWith("send failed", StringComparison.Ordinal) ? LinkDelivery.SentOnly : LinkDelivery.Failed(_status));
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
                var text = _decode(result.Buffer);
                if (text is null) continue;
                buffer.Append(text);
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
