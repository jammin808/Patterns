using Patterns.Devices;
using Patterns.Core.Model;
using Patterns.Core.Play;
using Patterns.Arcade;
using Patterns.Audience;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Threading;
using Patterns.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Patterns.Platform.Windows;

namespace Patterns.App.Services;

/// <summary>
/// The remote-control server: a TCP line protocol on one port (Bitfocus Companion — generic
/// TCP or the Patterns module, which also receives pushed STATE lines for feedback) and a
/// tiny HTTP server on another serving the phone/tablet web remote plus /api endpoints.
/// Raw TcpListener on purpose: no admin rights, no URL ACLs, portable.
/// </summary>
public sealed partial class ControlService : IDisposable
{
    private readonly ServiceKernel _kernel;
    private readonly IWireHost _services;
    private readonly IRouter _router;
    private readonly object _gate = new();
    private readonly List<WirePeer> _peers = new();
    private TcpListener? _tcp;
    private TcpListener? _http;
    private TcpListener? _audience;
    private readonly ConnectionLedger _audienceLedger = new();
    private readonly ConnectionLedger _wireLedger = new();
    private readonly ConnectionLedger _httpLedger = new();
    private readonly RateLimiter _busySaid = new();
    private CancellationTokenSource? _cts;
    private string _activeKey = "";
    private volatile string _status = "Remote control off.";
    private readonly DispatcherTimer _pushTimer;
    private bool _pushPending;

    public ControlService(ServiceKernel kernel, IWireHost host)
    {
        _kernel = kernel;
        _services = host;
        _router = host.NewRouter();

        // State pushes to Companion are throttled to a trailing 200 ms.
        _pushTimer = global::Patterns.App.Services.DeskTimers.Make(TimeSpan.FromMilliseconds(200));
        _pushTimer.Tick += (_, _) =>
        {
            _pushTimer.Stop();
            if (!_pushPending) return;
            _pushPending = false;
            var json = _router.StateJson();
            Broadcast("STATE " + json);       // each peer's one writer takes it, latest-wins: nothing blocks here
        };
        void Moved()
        {
            Interlocked.Increment(ref _rev);
            ArmPush();
        }
        _services.SnapshotPublished += Moved;
        _services.RuntimeChanged += Moved;   // "audio playing", "stream live": in STATE, never in a snapshot
        _router.Rev = () => Interlocked.Read(ref _rev);

        // The caller's VT clock moves every second while a clip is on air, and so does a running
        // countdown: the remotes get a push each second then — only then, and only while someone
        // listens — so a phone, a Stream Deck and an OSC desk count down with the desk. Nothing
        // else is rebuilt for it.
        _clockTimer = global::Patterns.App.Services.DeskTimers.Make(TimeSpan.FromSeconds(1));
        _clockTimer.Tick += (_, _) => ClockTick();
        _clockTimer.Start();
    }

    private long _rev; // bumped on the UI thread, read by the HTTP long-poll threads
    private bool _stackHooked;
    private readonly DispatcherTimer _clockTimer;
    private int _longPollers;

    private string _deckSignature = "";

    private void ClockTick()
    {
        try
        {
            // A node appearing, the twin's phase moving, a message to the stage waiting: nothing publishes for it, and a deck's keys read it.
            var deck = _services.DeckSignature();
            var deckMoved = deck != _deckSignature;
            _deckSignature = deck;
            if (!deckMoved && _services.VideoOnAir() is null && !OverlayControl.CountsEverySecond(_services.AirState.Countdown, DateTime.Now, DateTime.UtcNow)) return;
            bool listening;
            lock (_gate)
            {
                listening = _peers.Count > 0;
            }
            listening |= Volatile.Read(ref _longPollers) > 0 || _services.Osc is { FeedbackEndpoint: not null };
            if (!listening) return;
            Interlocked.Increment(ref _rev);
            ArmPush();
            _services.Osc?.MarkChanged();
        }
        catch (Exception ex)
        {
            Log.Warn("The VT clock push failed.", ex);
        }
    }

    /// <summary>
    /// The stack's runtime is deliberately not in the snapshot, so STANDBY, ARM, HOLD and a
    /// pending confirm push on their own event, throttled like the snapshot pushes. Hooked
    /// lazily: the stack service is built after this one.
    /// </summary>
    private void HookStack()
    {
        if (_stackHooked || _services.CueStack is null) return;
        _stackHooked = true;
        _services.CueStack.Changed += () =>
        {
            Interlocked.Increment(ref _rev);
            ArmPush();
        };
    }

    /// <summary>The listeners' words; "· paired" added while a pairing token is set, read live so the words follow the token without a rebind.</summary>
    public string Status
    {
        get
        {
            var status = _status;
            if (_tcp is not null && PairingToken.Needed(_kernel.State.Control.Token)) status = status.TrimEnd('.') + " · paired.";
            return status;
        }
    }

    /// <summary>How long the remote's addresses are kept before the machine is asked again.</summary>
    public static readonly TimeSpan RemoteUrlsKeptFor = TimeSpan.FromSeconds(30);

    private IReadOnlyList<string>? _urls;
    private int _urlsPort;
    private string _urlsBind = "";
    private DateTime _urlsAtUtc;

    /// <summary>
    /// LAN URLs the web remote answers on (for the settings panel / QR-by-eye). The desk reads
    /// these every second for its status line and the Install page; the machine's own addresses
    /// come from its interfaces (round 65: never the resolver, which could hold the desk's thread
    /// for as long as a venue's DNS wanted), and the list is kept for half a minute and read again
    /// only then, or when the port changes.
    /// </summary>
    public IReadOnlyList<string> RemoteUrls()
    {
        var port = _kernel.State.Control.HttpPort;
        var bind = _kernel.State.Control.Bind;
        var now = DateTime.UtcNow;
        if (_urls is not null && _urlsPort == port && _urlsBind == bind && now - _urlsAtUtc < RemoteUrlsKeptFor) return _urls;
        var urls = new List<string>();
        if (IPAddress.TryParse(bind, out var bound))
        {
            urls.Add($"http://{bound}:{port}/");      // bound to one address: the one door there is
        }
        else
        {
            urls.Add($"http://localhost:{port}/");
            foreach (var address in LocalAddresses.Enumerate()) urls.Add($"http://{address}:{port}/");
        }
        _urls = urls;
        _urlsPort = port;
        _urlsBind = bind;
        _urlsAtUtc = now;
        return urls;
    }

    /// <summary>The URL another machine reaches the desk on: its first interface address when it has one, else the only door there is; "" with none.</summary>
    public string ReachableUrl()
    {
        var urls = RemoteUrls();
        return urls.Count > 1 ? urls[1] : urls.Count > 0 ? urls[0] : "";
    }

    /// <summary>Drops the kept addresses so the next read asks the machine again (the listeners rebound, a test).</summary>
    public void ForgetRemoteUrls() => _urls = null;

    /// <summary>The audience listener's addresses — the same machine, the audience port (the bound address alone when one is set); empty while it is off.</summary>
    public IReadOnlyList<string> AudienceUrls()
    {
        var cfg = _kernel.State.Control;
        if (!cfg.Enabled || !cfg.AudienceEnabled) return Array.Empty<string>();
        if (IPAddress.TryParse(cfg.AudienceBind, out var bound)) return new[] { $"http://{bound}:{cfg.AudiencePort}/" };
        return RemoteUrls().Select(u => u.Replace($":{cfg.HttpPort}/", $":{cfg.AudiencePort}/")).ToList();
    }

    /// <summary>Starts/stops/rebinds the listeners to match the config (UI thread).</summary>
    public void Reconcile()
    {
        HookStack();
        var cfg = _kernel.State.Control;
        var key = cfg.Enabled ? $"{cfg.HttpPort}|{cfg.TcpPort}@{cfg.Bind}|{(cfg.AudienceEnabled ? $"{cfg.AudiencePort}@{cfg.AudienceBind}" : "")}" : "";
        if (key == _activeKey) return;
        _activeKey = key;
        StartFailed = false;
        ForgetRemoteUrls();

        StopListeners();
        if (!cfg.Enabled)
        {
            _status = "Remote control off.";
            return;
        }

        _cts = new CancellationTokenSource();
        try
        {
            // Round 65: the control ports bind where the Remote page says — every interface, or the
            // control network's own address on a desk with two, so the audience network never sees them.
            var controlBind = IPAddress.TryParse(cfg.Bind, out var boundTo) ? boundTo : IPAddress.Any;
            _tcp = new TcpListener(controlBind, cfg.TcpPort);
            _tcp.Start();
            _ = AcceptLoop(_tcp, HandleTcpClient, _cts.Token);

            _http = new TcpListener(controlBind, cfg.HttpPort);
            _http.Start();
            _ = AcceptLoop(_http, HandleHttpClient, _cts.Token);

            _status = $"Web remote on port {cfg.HttpPort} · Companion (TCP) on port {cfg.TcpPort}{(controlBind.Equals(IPAddress.Any) ? "" : $" at {controlBind} only")}.";
            if (cfg.AudienceEnabled)
            {
                // The room's own socket: the play pages and nothing else, on the audience network's address when the hub has one.
                var bind = IPAddress.TryParse(cfg.AudienceBind, out var address) ? address : IPAddress.Any;
                _audience = new TcpListener(bind, cfg.AudiencePort);
                _audience.Start();
                _ = AcceptLoop(_audience, HandleAudienceClient, _cts.Token);
                _status += $" Audience on port {cfg.AudiencePort}{(bind.Equals(IPAddress.Any) ? "" : $" at {bind}")} — the play pages only.";
            }
            if (_startFailures > 0) Log.Info($"{_status} (after {_startFailures} failed {(_startFailures == 1 ? "try" : "tries")})");
            else Log.Info(_status);
            _startFailures = 0;
        }
        catch (Exception ex)
        {
            _status = $"Remote control failed to start: {ex.Message}";
            // Round 77: the ports are usually held by the desk this one replaces, gone within seconds — the poll asks again
            // (AppServices.RetryListeners); the first failure is the error, the rest a line a minute.
            if (_startFailures++ == 0) Log.Error("Control server start failed.", ex);
            else if (_startFailures % 12 == 0) Log.Warn($"Control server still cannot start ({_startFailures} tries): {ex.Message}");
            StopListeners();
            StartFailed = true;
            _activeKey = ""; // retry on the next change, and from the poll
        }
    }

    private int _startFailures;

    /// <summary>Round 77: the last start failed (a port held by another process); the desk's poll asks again.</summary>
    public bool StartFailed { get; private set; }

    private static async Task AcceptLoop(TcpListener listener, Func<TcpClient, CancellationToken, Task> handler, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => handler(client, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped: the token was cancelled
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            _ = ex; // listener stopped underneath us — normal shutdown
        }
        catch (Exception ex)
        {
            Log.Warn("Control accept loop ended.", ex);
        }
    }

    // ---- TCP line protocol (Companion) --------------------------------------

    private async Task HandleTcpClient(TcpClient client, CancellationToken ct)
    {
        var limits = WireLimits;
        var address = Address(client);
        if (!_wireLedger.TryAdmit(address, limits.MaxClients, limits.MaxClientsPerAddress))
        {
            // The ceiling: said in the wire's own words and the door closed, so a Companion reconnecting in a loop reads why.
            SaidBusy("Wire", address, _wireLedger.Open);
            try
            {
                using var stream = client.GetStream();
                await WriteLine(stream, ControlProtocol.Err(limits.BusyWords(_wireLedger.From(address))), ct);
            }
            catch (Exception)
            {
                // Gone already.
            }
            finally
            {
                client.Dispose();
            }
            return;
        }
        client.NoDelay = true;
        // Round 65: one writer per peer. Replies and STATE pushes go through the peer's queue and
        // its one writer task, so nothing this handler says is ever interleaved with a push.
        var wire = client.GetStream();
        var peer = new WirePeer(wire, client);
        lock (_gate)
        {
            _peers.Add(peer);
        }
        try
        {
            var reader = new BoundedLineReader(wire, WireLineBytes) { LineSeconds = limits.LineSeconds };

            // Greet with current state so feedback initialises immediately — the first line the peer hears.
            var hello = await _router.StateJsonAsync();
            peer.Say("STATE " + hello);

            var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "";
            var origin = new ActionOrigin(OriginKind.Tcp, "", endpoint);
            var loopback = client.Client.RemoteEndPoint is IPEndPoint remote && IPAddress.IsLoopback(remote.Address);
            var presented = "";     // the token this connection presented — matched again on every verb, so NEW TOKEN cuts it off
            var wrongTokens = 0;
            while (!ct.IsCancellationRequested && !peer.Closed)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (InvalidDataException ex)
                {
                    // A line that never ends is not a command: said once, and the door closed.
                    peer.Say(ControlProtocol.Err($"{ex.Message} — the wire's lines are commands, and this one was not; closed"));
                    await peer.FlushAsync(LastWordWait);
                    break;
                }
                if (line is null) break;
                if (line.Trim().Length == 0) continue;
                RemoteCommand cmd;
                try
                {
                    cmd = ControlProtocol.Parse(line);
                }
                catch (Exception ex) when (!Faults.IsIoEnd(ex))
                {
                    peer.Say(WireFault(ex, "TCP"));
                    continue;
                }
                var token = _kernel.State.Control.Token;
                if (cmd.Kind == RemoteCommandKind.Auth)
                {
                    // The connection presents the show's pairing token: the port answers, the router never sees it.
                    if (PairingToken.Matches(token, cmd.Text))
                    {
                        presented = cmd.Text;
                        wrongTokens = 0;
                        lock (_gate)
                        {
                            if (_decks.TryGetValue(client, out var deck)) _decks[client] = deck with { Paired = true };   // round 66: the Eye reads it
                        }
                        peer.Say(ControlProtocol.Ok("paired"));
                    }
                    else if (!PairingToken.Needed(token))
                    {
                        peer.Say(ControlProtocol.Ok("open — this desk asks for no token"));
                    }
                    else if (++wrongTokens >= WrongTokensBeforeClose)
                    {
                        peer.Say(ControlProtocol.Err(ControlProtocol.WrongToken + " — closed"));
                        await peer.FlushAsync(LastWordWait);
                        break;
                    }
                    else
                    {
                        peer.Say(ControlProtocol.Err(ControlProtocol.WrongToken));
                    }
                    continue;
                }
                if (cmd.Kind == RemoteCommandKind.Hello)
                {
                    // "HELLO FOH deck module=3.0.0": history reads "GO from tcp FOH deck", not an address,
                    // and the Remote page lists the deck with the module it runs.
                    var (name, module) = CompanionWords.ParseHello(cmd.Text);
                    origin = new ActionOrigin(OriginKind.Tcp, name, endpoint);
                    lock (_gate)
                    {
                        _decks[client] = new WireDeck(name, module, address, DateTime.UtcNow) { Paired = Paired(token, presented, loopback) };
                    }
                }
                if (cmd.Kind is RemoteCommandKind.NavDeck or RemoteCommandKind.Record)
                {
                    if (!Paired(token, presented, loopback))
                    {
                        // Round 75: the action feed and the deck's whereabouts are a connection's standing, not a
                        // question — an unpaired connection hears what the operator does no more than it moves the show.
                        peer.Say(ControlProtocol.Err(ControlProtocol.NotPaired));
                        continue;
                    }
                    // Round 74: the deck's own facts — where its navigator is, whether it is recording a button —
                    // kept per connection by the port (nothing runs); STATE's decks row and the Eye's deck node read them.
                    var recording = cmd.Kind == RemoteCommandKind.Record && cmd.Text == "ON";
                    lock (_gate)
                    {
                        var deck = _decks.TryGetValue(client, out var known)
                            ? known
                            : new WireDeck(origin.Name.Length > 0 ? origin.Name : address, "", address, DateTime.UtcNow) { Paired = Paired(token, presented, loopback) };
                        _decks[client] = cmd.Kind == RemoteCommandKind.NavDeck ? deck with { Where = cmd.Text } : deck with { Recording = recording };
                        if (cmd.Kind == RemoteCommandKind.Record)
                        {
                            if (recording) _recording[peer] = endpoint;
                            else _recording.Remove(peer);
                        }
                    }
                    peer.Say(ControlProtocol.Ok(cmd.Kind == RemoteCommandKind.NavDeck ? "at " + cmd.Text : recording ? "recording — the desk's actions follow as ACTION lines" : "recording off"));
                    ArmPush();
                    continue;
                }
                if (!ControlProtocol.IsQuery(cmd) && !Paired(token, presented, loopback))
                {
                    // A mutating verb from a connection that has not paired: refused in the wire's words, nothing run.
                    peer.Say(ControlProtocol.Err(ControlProtocol.NotPaired));
                    continue;
                }
                string response;
                try
                {
                    response = await _router.ExecuteAsync(cmd, origin);
                }
                catch (Exception ex) when (!Faults.IsIoEnd(ex))
                {
                    response = WireFault(ex, "TCP");
                }
                peer.Say(response);
            }
        }
        catch (Exception ex)
        {
            // Disconnects are routine; anything else is a fault and leaves its trace.
            if (!Faults.IsIoEnd(ex)) WireFault(ex, "TCP");
        }
        finally
        {
            lock (_gate)
            {
                _peers.Remove(peer);
                _decks.Remove(client);
                _recording.Remove(peer);
            }
            peer.Dispose();
            _wireLedger.Release(address);
        }
    }

    /// <summary>How long a last word (an ERR before a close) is given to reach the peer.</summary>
    private static readonly TimeSpan LastWordWait = TimeSpan.FromSeconds(2);

    /// <summary>Wrong AUTH lines a connection may send before it is closed.</summary>
    public const int WrongTokensBeforeClose = 5;

    /// <summary>
    /// This machine's own browsers and processes never need the token — the desk's pages opened
    /// on the desk, a Companion on the same machine. Off in the tests, which connect from loopback
    /// and want the gate.
    /// </summary>
    public static bool TrustLoopback { get; set; } = true;

    /// <summary>Round 83: the faults behind the wire — not a socket's end — counted; the first is logged with its stack and then one a minute.</summary>
    private readonly FaultThrottle _faults = new();

    /// <summary>How many lines or requests made the desk fault behind the wire since start (the Super Check's REMOTE row).</summary>
    public long FaultCount => _faults.Count;

    /// <summary>
    /// A fault behind the wire is that line's, never the connection's: counted, written to the log by the throttle,
    /// and answered on the line so the sender learns the desk faulted rather than seeing the connection drop. The
    /// answer names the exception's type and the count, never the line (a line may carry a token) and never the stack.
    /// </summary>
    private string WireFault(Exception ex, string where)
    {
        var write = _faults.Note(DateTime.UtcNow);
        var n = _faults.Count;
        if (write) Log.Error($"The {where} handler faulted (fault #{n}): {Faults.Brief(ex)}", ex);
        return ControlProtocol.Err($"the desk faulted on this line ({ex.GetType().Name}) — fault #{n}, logged");
    }

    /// <summary>Whether a connection may run a mutating verb: no token is set, or it is this machine's own, or it presented the token that is set now.</summary>
    internal static bool Paired(string token, string presented, bool loopback)
        => !PairingToken.Needed(token) || (loopback && TrustLoopback) || PairingToken.Matches(token, presented);

    /// <summary>The wire's ceilings — connections in all and from one address, the seconds a started line has to end — and the web remote's. Settable for the tests.</summary>
    public WireLimits WireLimits { get; set; } = WireLimits.Default;

    /// <summary>How many Companion connections are open right now.</summary>
    public int WireConnections => _wireLedger.Open;

    /// <summary>The wire's revision: moves on every push — a publish, a runtime change, a deck signature change, a clock tick that counts.</summary>
    public long Rev => Interlocked.Read(ref _rev);

    private readonly Dictionary<TcpClient, WireDeck> _decks = new();

    /// <summary>Round 74: the peers that asked for the desk's actions (RECORD ON), each with its own endpoint so its own presses are not fed back to it.</summary>
    private readonly Dictionary<WirePeer, string> _recording = new();

    private ShowActions? _feed;

    /// <summary>Round 74: the action layer this port listens to for the recorder's feed (the desk's; a node's port has none to give).</summary>
    public void FeedFrom(ShowActions actions)
    {
        _feed = actions;
        actions.Performed += Feed;
    }

    /// <summary>Round 79: the kinds the recorder found no line for this session, each logged once.</summary>
    private readonly HashSet<ShowActionKind> _unwritten = new();

    /// <summary>
    /// Round 74: the recorder's feed. Every action that ran — a key, a menu line, a MIDI pad, another
    /// deck's press — goes to each peer that is recording, as the wire line that reproduces it
    /// (ACTION LOOK Walk-in), so Companion's Action Recorder writes the desk's own words into a
    /// button. Automation (a cue's steps, a follow, the schedule, a playlist, a stinger, a recovery)
    /// is not a press and is not fed; a refused action did nothing and is not fed; the recording
    /// deck's own presses are its own buttons already and are not fed back to it; a kind the wire
    /// has no line for (TAKE, CUT, a note) is not fed.
    /// </summary>
    public void Feed(ShowAction action, ActionOrigin origin, ActionResult result)
    {
        if (!result.Ok || origin.IsAutomation) return;
        List<KeyValuePair<WirePeer, string>> recording;
        lock (_gate)
        {
            if (_recording.Count == 0) return;
            recording = _recording.ToList();
        }
        var line = WireWriter.Line(_feed?.Readable(action) ?? action);   // the look's name, the screen's number — the operator's words, not the desk's ids
        if (line.Length == 0)
        {
            // Round 79: a kind the writer cannot say is expected (WireWriter.Unsayable, the admin verbs); any other is a
            // verb the recorder silently lost — said once per kind, so a deck's empty recording has a line in the log.
            if (!WireWriter.Unsayable.Contains(action.Kind) && !ActionSpec.CarriesSecret(action.Kind) && _unwritten.Add(action.Kind))
            {
                Log.Warn($"The recorder has no wire line for {action.Kind}: a deck recording the desk will not hear it (WireWriter.Line).");
            }
            return;
        }
        foreach (var (peer, endpoint) in recording)
        {
            if (origin.Kind == OriginKind.Tcp && origin.Endpoint == endpoint) continue;
            peer.Say("ACTION " + line);
        }
    }

    /// <summary>The decks that said HELLO and are still connected — name, module version, address — for the Remote page and STATE.</summary>
    public IReadOnlyList<WireDeck> Decks
    {
        get
        {
            lock (_gate)
            {
                return _decks.Values.OrderBy(d => d.SinceUtc).ToList();
            }
        }
    }

    /// <summary>How many web remote connections are open right now.</summary>
    public int HttpConnections => _httpLedger.Open;

    private static string Address(TcpClient client) => client.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : "?";

    /// <summary>A refusal logged once a minute per address and door: a flood is one line, not a thousand.</summary>
    private void SaidBusy(string door, string address, int open)
    {
        if (_busySaid.Allow(door + ":" + address, 1, TimeSpan.FromMinutes(1), DateTime.UtcNow))
        {
            Log.Warn($"{door}: a connection from {address} refused — {open} open, the ceiling reached (said once a minute).");
        }
    }

    /// <summary>503 and the door closed: a port's ceiling, said in one word so a phone or a tablet tries again in a moment.</summary>
    private static async Task WriteBusyAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var stream = client.GetStream();
            var words = Encoding.UTF8.GetBytes("busy");
            var head = $"HTTP/1.1 503 Service Unavailable\r\nContent-Type: text/plain\r\nContent-Length: {words.Length}\r\nRetry-After: 2\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(words, ct);
        }
        catch (Exception)
        {
            // A client that left; nothing to say.
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>The most bytes a line on the wire may run to: a command is a few dozen, a plan or a show file a few thousand.</summary>
    public const int WireLineBytes = 64 * 1024;

    /// <summary>A node's front door on its control port: what it is, its pages, and where the desk finds it — the desk's remote page is the desk's.</summary>
    private string NodePage()
    {
        var kind = NodeKinds.Label(_kernel.Profile);
        var links = new List<string>();
        if (_kernel.Profile == NodeKind.Arcade) links.Add("<a href='/pad'>the phone pad</a>");
        if (_kernel.Profile is NodeKind.Caller or NodeKind.Timer)
        {
            links.Add("<a href='/stage'>the stage display</a>");
            links.Add("<a href='/stage?view=crew'>the crew's view</a>");
            links.Add("<a href='/timer'>the timer controller</a>");
        }
        if (_services.HasRoom) links.Add("<a href='/host'>the audience host page</a>");
        var join = _services.RoomJoinUrl;
        if (join.Length > 0) links.Add($"the audience joins at <a href='{join}'>{join}</a>");
        return "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>"
             + $"<title>Patterns — {kind} node</title>"
             + "<style>body{font:16px system-ui,sans-serif;background:#0b0d12;color:#e6e9ef;padding:24px;max-width:720px}a{color:#5fd0ff}h1{font-weight:600;font-size:22px}p{line-height:1.5}</style></head>"
             + $"<body><h1>Patterns — {kind} node</h1><p>{System.Net.WebUtility.HtmlEncode(_kernel.Beacon.MachineName)} · {System.Net.WebUtility.HtmlEncode(_kernel.State.Name)}</p>"
             + $"<p>{string.Join(" · ", links)}</p>"
             + "<p>The desk finds this node on the beacon; the node's own verbs answer on this port, and the desk's do not.</p></body></html>";
    }

    private static async Task WriteLine(NetworkStream stream, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, ct);
    }

    /// <summary>A STATE line to every peer, latest-wins on each one's writer: never blocks, never writes a socket from this thread; a peer that cannot take it closes itself and its handler lets go.</summary>
    private void Broadcast(string line)
    {
        List<WirePeer> peers;
        lock (_gate)
        {
            peers = _peers.ToList();
        }
        foreach (var peer in peers)
        {
            peer.Push(line);
        }
    }

    // ---- minimal HTTP (web remote) ------------------------------------------

    /// <summary>The web remote's door: under the same two ceilings as the wire, then the handler with the control port's routes.</summary>
    private async Task HandleHttpClient(TcpClient client, CancellationToken ct)
    {
        var limits = WireLimits;
        var address = Address(client);
        if (!_httpLedger.TryAdmit(address, limits.MaxHttpClients, limits.MaxHttpClientsPerAddress))
        {
            SaidBusy("Web remote", address, _httpLedger.Open);
            await WriteBusyAsync(client, ct);
            return;
        }
        try
        {
            await HandleHttp(client, audience: false, ct);
        }
        finally
        {
            _httpLedger.Release(address);
        }
    }

    /// <summary>Whether the audience listener is up — the room's door is open.</summary>
    public bool AudienceListening => _audience is not null;

    /// <summary>How many audience connections are open right now.</summary>
    public int AudienceConnections => _audienceLedger.Open;

    /// <summary>
    /// The audience's socket: counted against the budgets (so many at once, so many from one
    /// address), then the same handler with the audience's own route table — nothing else answers.
    /// </summary>
    private async Task HandleAudienceClient(TcpClient client, CancellationToken ct)
    {
        if (!_services.HasRoom)
        {
            client.Dispose();                                           // no room on this role: the door is closed, nothing is read
            return;
        }
        var budget = RoomBudget();                                // the network profile's reading of the budgets: behind a venue NAT the per-address ceiling is the port's own
        var address = Address(client);
        if (!_audienceLedger.TryAdmit(address, budget.MaxConnections, budget.MaxConnectionsPerAddress))
        {
            SaidBusy("Audience", address, _audienceLedger.Open);
            await WriteBusyAsync(client, ct);
            return;
        }
        try
        {
            await HandleHttp(client, audience: true, ct);
        }
        finally
        {
            _audienceLedger.Release(address);
        }
    }

    /// <summary>The control port's limits on a request: how much head and body it reads and how long it waits. Settable for the tests.</summary>
    public HttpLimits ControlLimits { get; set; } = HttpLimits.Control;

    /// <summary>The audience port's — tighter, because nobody vouches for what is on the other end.</summary>
    public HttpLimits AudienceLimits { get; set; } = HttpLimits.Audience;

    /// <summary>
    /// A request's head, read as bytes up to the blank line and never past the limit, within the
    /// head's seconds, with whatever of the body came along with it. Null when the client sent
    /// nothing, or went quiet; a fault when it sent more head than a request has.
    /// </summary>
    private static async Task<(byte[] Head, byte[] Extra, string Fault)?> ReadHeadAsync(NetworkStream stream, HttpLimits limits, CancellationToken ct)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(TimeSpan.FromSeconds(limits.HeadSeconds));
        var chunk = new byte[2048];
        using var head = new MemoryStream();
        while (true)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(chunk, timed.Token);
            }
            catch (OperationCanceledException)
            {
                return null;                                    // quiet past the head's seconds, or the port closing: nothing to answer
            }
            if (n <= 0) return null;
            await head.WriteAsync(chunk.AsMemory(0, n), ct);
            var bytes = head.ToArray();
            var (at, length) = HttpHead.EndOfHead(bytes);
            if (at >= 0) return (bytes[..at], bytes[(at + length)..], "");
            if (bytes.Length > limits.MaxHeadBytes) return (Array.Empty<byte>(), Array.Empty<byte>(), $"a head past {limits.MaxHeadBytes} bytes");
        }
    }

    /// <summary>The body: the bytes the head's read brought along, then the rest up to the content length, within the body's seconds; as many as came when the client stopped short.</summary>
    private static async Task<string> ReadBodyAsync(NetworkStream stream, byte[] rest, int contentLength, HttpLimits limits, CancellationToken ct)
    {
        if (contentLength <= 0) return "";
        var body = new byte[contentLength];
        var read = Math.Min(rest.Length, contentLength);
        Array.Copy(rest, body, read);
        if (read < contentLength)
        {
            using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timed.CancelAfter(TimeSpan.FromSeconds(limits.BodySeconds));
            try
            {
                while (read < contentLength)
                {
                    var n = await stream.ReadAsync(body.AsMemory(read, contentLength - read), timed.Token);
                    if (n <= 0) break;
                    read += n;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // the client stopped short of its own content length: what came is the body
            }
        }
        return Encoding.UTF8.GetString(body, 0, read);
    }

    /// <summary>A short answer with a status and a line of plain text — the faults, before any route.</summary>
    private static async Task WriteShortAsync(NetworkStream stream, string status, string words, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(words);
        var head = $"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
    }

    private async Task HandleHttp(TcpClient client, bool audience, CancellationToken ct)
    {
        try
        {
            using var stream = client.GetStream();
            var limits = audience ? AudienceLimits : ControlLimits;

            // The head as bytes, bounded and timed; the body as the bytes its content length says,
            // never as characters — a name with an accent is more bytes than characters, and a
            // reader counting characters waited for bytes that never came.
            var read = await ReadHeadAsync(stream, limits, ct);
            if (read is null) return;
            var (headBytes, rest, headFault) = read.Value;
            if (headFault.Length > 0)
            {
                await WriteShortAsync(stream, "431 Request Header Fields Too Large", headFault, ct);
                return;
            }
            var request = HttpHead.Parse(headBytes, limits);
            if (!request.Ok)
            {
                await WriteShortAsync(stream, request.Status, request.Fault, ct);
                return;
            }
            var method = request.Method;
            var path = request.Path;
            var clientHeader = request.ClientHeader;
            // Round 65: the control port's mutating routes want the show's pairing token when one is
            // set — in a header, never the URL; this machine's own browsers are exempt; the audience
            // port has no verbs to gate.
            var loopback = client.Client.RemoteEndPoint is IPEndPoint remote && IPAddress.IsLoopback(remote.Address);
            var paired = audience || Paired(_kernel.State.Control.Token, request.Token, loopback);
            var body = await ReadBodyAsync(stream, rest, request.ContentLength, limits, ct);

            string status = "200 OK", contentType = "text/html; charset=utf-8";
            string payload = "";
            byte[]? binary = null;
            try
            {
            if (audience && !AudienceRoutes.Allows(method, path))
            {
                // The trust boundary: the audience port answers the play pages and their calls, nothing else — not a command, not the state, not a picture.
                status = "404 Not Found";
                contentType = "text/plain";
                payload = "Not here — the audience port answers the play pages only.";
            }
            else if (!audience && AudienceRoutes.AudienceOnly(path))
            {
                // And the other way: the control port never carries the room, so a phone that finds it finds nothing.
                status = "404 Not Found";
                contentType = "text/plain";
                payload = "The audience pages answer on the audience port — Remote page, AUDIENCE.";
            }
            else if (audience && method == "GET" && (path == "/" || path == "/index.html"))
            {
                payload = PlayPage;
            }
            else if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                payload = _kernel.IsDesk ? RemotePage : NodePage();      // a node's front door is its own, never the desk's remote
            }
            else if (method == "GET" && (path == "/multiview" || path == "/mv"))
            {
                payload = MultiviewPage;
            }
            else if (method == "GET" && path.StartsWith("/mv.jpg", StringComparison.Ordinal))
            {
                contentType = "image/jpeg";
                payload = "";
                binary = RenderMultiviewJpeg(
                    int.TryParse(QueryValue(path, "w"), out var mvw) ? Math.Clamp(mvw, 320, 1920) : 1024,
                    int.TryParse(QueryValue(path, "n"), out var mvn) ? mvn : 1);
            }
            else if (method == "GET" && (path == "/api/state" || path.StartsWith("/api/state?", StringComparison.Ordinal)))
            {
                contentType = "application/json";
                // ?since=<rev> long-polls: the handler is already asynchronous, so it can wait
                // up to 25 s for the next change instead of a tablet polling every 1.5 s.
                var since = QueryValue(path, "since");
                if (long.TryParse(since, out var seen))
                {
                    var deadline = DateTime.UtcNow.AddSeconds(25);
                    Interlocked.Increment(ref _longPollers); // a tablet is waiting: the VT clock's second ticks reach it
                    try
                    {
                        while (Interlocked.Read(ref _rev) == seen && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                        {
                            await Task.Delay(150, ct);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _longPollers);
                    }
                }
                payload = await _router.StateJsonAsync();
            }
            else if (method == "GET" && path == "/api/cues")
            {
                contentType = "application/json";
                payload = await _router.CueListJsonAsync();
            }
            else if (method == "GET" && path == "/run")
            {
                payload = RunPage;
            }
            else if (method == "GET" && (path == "/stage" || path.StartsWith("/stage?", StringComparison.Ordinal)))
            {
                payload = StagePage;
            }
            else if (method == "GET" && path == "/timer")
            {
                payload = TimerPage;
            }
            else if (method == "GET" && (path == "/api/stage" || path.StartsWith("/api/stage?", StringComparison.Ordinal)))
            {
                contentType = "application/json";
                if (_services.Stage is not { } stage)
                {
                    status = "404 Not Found";
                    contentType = "text/plain";
                    payload = "The stage timer is the desk's — not on this node.";
                }
                else
                {
                    // ?since=<rev> long-polls the stage's own revision: a message, a receipt, the timer moved.
                    if (long.TryParse(QueryValue(path, "since"), out var seenStage))
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(25);
                        while (stage.Rev == seenStage && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                        {
                            await Task.Delay(150, ct);
                        }
                    }
                    payload = await UiThread.InvokeAsync(() => stage.StatusJson());
                }
            }
            else if (!paired && method == "POST" && (path == "/api/stage/ack" || path == "/api/arcade/key"))
            {
                status = "403 Forbidden";
                contentType = "application/json";
                payload = NotPairedJson;
            }
            else if (method == "POST" && path == "/api/stage/ack")
            {
                contentType = "application/json";
                // The display's ACK is a verb of the vocabulary: the desk's stage marks the message seen;
                // a timer node forwards it to the desk it follows, and marks its own copy when alone.
                var id = body.Trim().Trim('"');
                var ackOrigin = new ActionOrigin(OriginKind.Http, "stage display", client.Client.RemoteEndPoint?.ToString() ?? "");
                var acked = id.Length > 0 && (await UiThread.InvokeAsync(() => _services.Actions.Execute(new ShowAction(ShowActionKind.StageAck, "", id), ackOrigin))).Ok;
                payload = acked ? "{\"ok\":true}" : "{\"ok\":false,\"msg\":\"no such message, or seen already\"}";
            }
            else if (method == "GET" && (path == "/pad" || path.StartsWith("/pad?", StringComparison.Ordinal)))
            {
                payload = PadPage;
            }
            else if (method == "GET" && (path == "/api/arcade" || path.StartsWith("/api/arcade?", StringComparison.Ordinal)))
            {
                contentType = "application/json";
                // On the arcade node its own state; on a desk the arcade nodes' — asked on their wires.
                var forward = _kernel.Profile != NodeKind.Arcade && await UiThread.InvokeAsync(() => _kernel.Nodes.Arcades().Count) > 0;
                payload = forward
                    ? await _kernel.Nodes.AskArcadesAsync("ARCADE STATUS")
                    : await ArcadeAnswer(a => ((ArcadeService)a).StatusJson(QueryValue(path, "what")));
            }
            else if (method == "POST" && path == "/api/arcade/key")
            {
                // "<player> <button> DOWN|UP|TAP" from the phone pad: the same verb the wire runs.
                contentType = "application/json";
                var words = body.Trim();
                var padOrigin = new ActionOrigin(OriginKind.Http, "pad", client.Client.RemoteEndPoint?.ToString() ?? "");
                var result = await UiThread.InvokeAsync(() => _services.Actions.Execute(new ShowAction(ShowActionKind.ArcadeKey, "", words), padOrigin));
                payload = JsonUtil.SerializeCompact(new { ok = result.Ok, msg = result.Message });
            }
            else if (method == "GET" && (path == "/play" || path.StartsWith("/play?", StringComparison.Ordinal)))
            {
                payload = PlayPage;
            }
            else if (method == "GET" && (path == "/host" || path.StartsWith("/host?", StringComparison.Ordinal)))
            {
                payload = HostPage;
            }
            else if (method == "POST" && path == "/api/play/join")
            {
                contentType = "application/json";
                var from = client.Client.RemoteEndPoint is IPEndPoint joinEp ? joinEp.Address.ToString() : "?";
                payload = await RoomAnswer(r => ((PlayService)r).JoinJson(body, from));
            }
            else if (method == "GET" && (path == "/api/play/state" || path.StartsWith("/api/play/state?", StringComparison.Ordinal)))
            {
                contentType = "application/json";
                // ?rev=<rev> long-polls the room: a question opened, an answer counted, a message sent, the wall changed.
                var token = QueryValue(path, "token");
                var sinceSeq = long.TryParse(QueryValue(path, "since"), out var parsedSince) ? parsedSince : 0L;
                // The wait is a signal, not a poll: the room wakes every waiting phone at once when it moves; past the budget a phone is answered now.
                payload = await RoomAnswerAsync(async (r, waitCt) =>
                {
                    var room = (PlayService)r;
                    if (long.TryParse(QueryValue(path, "rev"), out var seenRev)) await room.WaitForChangeAsync(seenRev, TimeSpan.FromSeconds(20), waitCt);
                    waitCt.ThrowIfCancellationRequested();     // the port closed while the phone waited: nothing of the desk is asked for a phone that is gone
                    return await UiThread.InvokeAsync(() => room.StateJson(token, sinceSeq));
                }, ct);
            }
            else if (method == "POST" && path == "/api/play/answer")
            {
                contentType = "application/json";
                payload = await RoomAnswer(r => ((PlayService)r).AnswerJson(body));
            }
            else if (method == "POST" && path == "/api/play/say")
            {
                contentType = "application/json";
                payload = await RoomAnswer(r => ((PlayService)r).SayJson(body));
            }
            else if (method == "POST" && path == "/api/play/vote")
            {
                contentType = "application/json";
                payload = await RoomAnswer(r => ((PlayService)r).VoteJson(body));
            }
            else if (method == "POST" && path == "/api/play/draughts")
            {
                contentType = "application/json";
                payload = await RoomAnswer(r => ((PlayService)r).DraughtsJson(body));
            }
            else if (method == "POST" && path == "/api/play/host")
            {
                // The host's data behind the admin passcode: the room's phones share this server.
                contentType = "application/json";
                var passcode = body.Trim();
                if (!await UiThread.InvokeAsync(() => _kernel.Gate.Check(_kernel.State.Install.AdminPasscode, passcode, DateTime.UtcNow)))
                {
                    status = "403 Forbidden";
                    payload = "{\"ok\":false}";
                }
                else
                {
                    payload = await RoomAnswer(r => ((PlayService)r).HostJson());
                }
            }
            else if (method == "GET" && (path == "/api/play" || path.StartsWith("/api/play?", StringComparison.Ordinal)))
            {
                contentType = "application/json";
                payload = await RoomAnswer(r => ((PlayService)r).StatusJson(QueryValue(path, "what")));
            }
            else if (method == "GET" && path == "/api/play/feed.csv")
            {
                contentType = "text/csv; charset=utf-8";
                payload = _services.HasRoom ? await RoomAnswer(r => ((PlayService)r).FeedCsv()) : "";
            }
            else if (method == "GET" && path == "/admin")
            {
                payload = AdminPage;
            }
            else if (method == "POST" && path == "/api/admin")
            {
                // "<passcode>\n<command line>": the gate first, then the line through the router with the admin as its origin.
                contentType = "application/json";
                var cut = body.IndexOf('\n');
                var passcode = (cut < 0 ? body : body[..cut]).Trim();
                var line = cut < 0 ? "" : body[(cut + 1)..].Trim();
                var adminOrigin = new ActionOrigin(OriginKind.Http, "admin", client.Client.RemoteEndPoint?.ToString() ?? "");
                string response;
                if (!await UiThread.InvokeAsync(() => _kernel.Gate.Check(_kernel.State.Install.AdminPasscode, passcode, DateTime.UtcNow)))
                {
                    status = "403 Forbidden";
                    response = ControlProtocol.Err(_kernel.Gate.Reason);
                }
                else if (line.Length == 0)
                {
                    response = ControlProtocol.Ok();        // a passcode check on its own: the page unlocking
                }
                else
                {
                    response = await _router.ExecuteAsync(ControlProtocol.Parse(line), adminOrigin);
                }
                var ok = response.StartsWith("OK", StringComparison.Ordinal);
                payload = $"{{\"ok\":{(ok ? "true" : "false")},\"msg\":{System.Text.Json.JsonSerializer.Serialize(response)}}}";
            }
            else if (method == "GET" && path.StartsWith("/api/admin/log", StringComparison.Ordinal))
            {
                contentType = "text/plain; charset=utf-8";
                // The passcode rides in its header (round 65) — never in the URL, which a browser's history and a proxy's log keep.
                if (!await UiThread.InvokeAsync(() => _kernel.Gate.Check(_kernel.State.Install.AdminPasscode, request.Pass, DateTime.UtcNow)))
                {
                    status = "403 Forbidden";
                    payload = _kernel.Gate.Reason;
                }
                else
                {
                    payload = LogTail(80);
                }
            }
            else if (method == "GET" && path.StartsWith("/support-bundle.zip", StringComparison.Ordinal))
            {
                if (!await UiThread.InvokeAsync(() => _kernel.Gate.Check(_kernel.State.Install.AdminPasscode, request.Pass, DateTime.UtcNow)))
                {
                    status = "403 Forbidden";
                    contentType = "text/plain";
                    payload = _kernel.Gate.Reason;
                }
                else
                {
                    contentType = "application/zip";
                    payload = "";
                    // The words come off the UI thread with the show they describe; the zip is built off it.
                    var info = await UiThread.InvokeAsync(SupportBundleInfo);
                    binary = await Task.Run(() => BuildSupportBundle(info), ct);
                }
            }
            else if (method == "GET" && path.StartsWith("/api/screens/", StringComparison.Ordinal) && (path.EndsWith("/edid.bin", StringComparison.Ordinal) || path.EndsWith("/edid.hex", StringComparison.Ordinal) || path.EndsWith("/edid.txt", StringComparison.Ordinal)))
            {
                // Round 65.8: the planned screen's EDID as a processor input or a PC loads it — the bytes, the hex, the summary. Reading: no token.
                var word = Uri.UnescapeDataString(path["/api/screens/".Length..].Split('/')[0]);
                var planned = await UiThread.InvokeAsync(() => _services.Actions.PlannedEdid(word));
                if (planned is null)
                {
                    status = "404 Not Found";
                    contentType = "text/plain";
                    payload = $"No screen '{word}'.";
                }
                else if (path.EndsWith(".bin", StringComparison.Ordinal))
                {
                    contentType = "application/octet-stream";
                    payload = "";
                    binary = planned.Bytes;
                }
                else
                {
                    contentType = "text/plain; charset=utf-8";
                    payload = path.EndsWith(".hex", StringComparison.Ordinal) ? planned.Hex : planned.Summary;
                }
            }
            else if (method == "GET" && path.StartsWith("/pgm.jpg", StringComparison.Ordinal))
            {
                contentType = "image/jpeg";
                payload = "";
                binary = RenderProgramJpeg();
            }
            else if (method == "POST" && path == "/api/cmd")
            {
                contentType = "application/json";
                var cmd = ControlProtocol.Parse(body);
                var httpOrigin = new ActionOrigin(OriginKind.Http, "", client.Client.RemoteEndPoint?.ToString() ?? "");
                string response;
                if (!paired && !ControlProtocol.IsQuery(cmd))
                {
                    // A mutating verb without the show's token: 403 with the wire's words, and the pages ask for the token on it.
                    status = "403 Forbidden";
                    response = ControlProtocol.Err(ControlProtocol.NotPaired);
                }
                else if (IsCueVerb(cmd) && !clientHeader)
                {
                    // A cross-origin page cannot fire cues: the embedded pages and any deliberate
                    // client send this header; plain commands (LOOK, BLACKOUT…) keep working without it.
                    response = ControlProtocol.Err("X-Patterns-Client header required for cue commands");
                }
                else
                {
                    response = await _router.ExecuteAsync(cmd, httpOrigin);
                }
                var ok = response.StartsWith("OK", StringComparison.Ordinal);
                payload = $"{{\"ok\":{(ok ? "true" : "false")},\"msg\":{System.Text.Json.JsonSerializer.Serialize(response)}}}";
            }
            else
            {
                status = "404 Not Found";
                contentType = "text/plain";
                payload = "Not found";
            }
            }
            catch (Exception ex) when (!Faults.IsIoEnd(ex))
            {
                // Round 83: a fault in a route is that request's — answered, counted and logged, never a dropped connection.
                status = "500 Internal Server Error";
                contentType = "application/json";
                binary = null;
                payload = $"{{\"ok\":false,\"msg\":{System.Text.Json.JsonSerializer.Serialize(WireFault(ex, "HTTP"))}}}";
            }

            var bytes = binary ?? Encoding.UTF8.GetBytes(payload);
            var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(bytes, ct);
        }
        catch (Exception ex)
        {
            // Broken sockets are routine for one-shot HTTP; anything else is a fault and leaves its trace.
            if (!Faults.IsIoEnd(ex)) WireFault(ex, "HTTP");
        }
        finally
        {
            client.Dispose();
        }
    }

    private static bool IsCueVerb(RemoteCommand cmd) => cmd.IsAction && cmd.Action.Kind is
        ShowActionKind.CueGo or ShowActionKind.CueStandby or ShowActionKind.CueHoldOn or ShowActionKind.CueHoldOff or
        ShowActionKind.ListArm or ShowActionKind.ListDisarm or ShowActionKind.StopAll;

    /// <summary>The last lines of patterns.log, for the ADMIN page — read with a shared lock, the app keeps writing.</summary>
    private string LogTail(int lines)
    {
        try
        {
            var path = Path.Combine(_kernel.Store.BaseDirectory, "patterns.log");
            if (!File.Exists(path)) return "(no log yet)";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n');
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception ex)
        {
            return "(the log could not be read: " + ex.Message + ")";
        }
    }

    /// <summary>The bundle's own page of facts, read on the UI thread where the show and the services live.</summary>
    private string SupportBundleInfo()
    {
        var lines = new List<string>
        {
            $"Patterns support bundle — {DateTime.Now:yyyy-MM-dd HH:mm} (from the ADMIN page)",
            $"Site: {(_kernel.State.Install.SiteName.Length > 0 ? _kernel.State.Install.SiteName : "(unnamed)")} · machine {Environment.MachineName}",
            $"Build: {UpdateService.RunningVersion} · .NET {Environment.Version} · {Environment.OSVersion}",
            $"Health: {HealthMonitor.Summary(DateTime.UtcNow)}",
            $"Install: {_services.Install?.Status ?? "not on this node"}",
            $"Update: {_services.Updates?.Status ?? "not on this node"}",
            $"Management: {_services.Management?.Status ?? "not on this node"}",
        };
        // Round 65.9: the machine as Windows describes it (the kept reading — this is the desk thread), and
        // the rig of the day against the commissioned one; the snapshot itself rides along as patterns.knowngood.json.
        var machine = MachineProbe.Read();
        lines.Add("");
        lines.Add("MACHINE (as Windows describes it)");
        lines.AddRange(machine.IsEmpty ? new[] { "not read yet" } : machine.Lines);
        lines.Add("");
        lines.Add("KNOWN GOOD RIG");
        var known = _kernel.KnownGood;
        if (known.Known is null)
        {
            lines.Add("not saved");
        }
        else
        {
            var now = machine.IsEmpty ? null : _services.Actions.RigSnapshotNow();
            var drift = now is null ? known.LastDrift : known.Compare(now);
            lines.Add(drift?.Headline ?? "saved — not compared yet");
            if (drift is not null) lines.AddRange(drift.Words);
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The support bundle as bytes for the ADMIN page's download: written beside the settings, then read back.</summary>
    private byte[] BuildSupportBundle(string info)
    {
        var dir = _kernel.Store.BaseDirectory;
        var path = Path.Combine(dir, SupportBundle.FileNameFor(DateTime.Now));
        SupportBundle.Build(dir, path, info, Secrets.ValuesOf(_kernel.State));
        Log.Info($"Support bundle written for the ADMIN page: {path}");
        return File.ReadAllBytes(path);
    }

    /// <summary>The 403 body for a mutating route without the token: the wire's words, as the pages' JSON.</summary>
    private static readonly string NotPairedJson = $"{{\"ok\":false,\"msg\":{System.Text.Json.JsonSerializer.Serialize(ControlProtocol.Err(ControlProtocol.NotPaired))}}}";

    private static string? QueryValue(string path, string key)
    {
        var q = path.IndexOf('?');
        if (q < 0) return null;
        foreach (var pair in path[(q + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            var k = eq < 0 ? pair : pair[..eq];
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    /// <summary>The program as a thumbnail for the /run page — the engine over the current snapshot, like /mv.jpg.</summary>
    private byte[] RenderProgramJpeg()
    {
        lock (_mvGate)
        {
            var snap = _kernel.Bus.Current;
            const int w = 640;
            const int h = 360;
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(w, h),
                ReferenceSize = new SKSizeI(w, h),
                Time = ShowClock.Seconds,
                Now = DateTime.Now,
                UtcNow = DateTime.UtcNow,
                Sink = Patterns.Core.Model.SinkKind.Thumbnail,
                SinkIndex = 0,
                Preview = _kernel.Bus.Sandbox,
                SinkLabel = "pgm-remote",
            };
            _mvEngine.Render(surface.Canvas, snap, in ctx, _mvSink);
            surface.Canvas.Flush();
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 72);
            return data.ToArray();
        }
    }

    private void StopListeners()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        try { _tcp?.Dispose(); } catch { /* already down */ }
        try { _http?.Dispose(); } catch { /* already down */ }
        try { _audience?.Dispose(); } catch { /* already down */ }
        _tcp = null;
        _http = null;
        _audience = null;
        lock (_gate)
        {
            foreach (var peer in _peers)
            {
                peer.Dispose();
            }
            _peers.Clear();
        }
    }

    /// <summary>
    /// Arms the trailing push — never after Dispose. A change that lands on a closed desk (its
    /// last publish, a stack that settles as it goes) must not leave the timer running: a
    /// running timer roots the desk, and its tick would read STATE from services that are gone
    /// (round 64's census).
    /// </summary>
    private void ArmPush()
    {
        if (_disposed) return;
        _pushPending = true;
        if (!_pushTimer.IsEnabled) _pushTimer.Start();
    }

    /// <summary>
    /// The room's answer to a phone, or the closed door (round 64). The room's type is named only
    /// inside the caller's lambda and the inner method here, both compiled only when a room is
    /// there — so a role without one never loads the room's assembly on the wire's account.
    /// </summary>
    private Task<string> RoomAnswer(Func<object, string> answer) => _services.HasRoom ? RoomAnswerOf(answer) : Task.FromResult(NoRoomJson);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<string> RoomAnswerOf(Func<object, string> answer)
    {
        var room = _services.Play!;
        return await UiThread.InvokeAsync(() => answer(room));
    }

    private Task<string> RoomAnswerAsync(Func<object, CancellationToken, Task<string>> answer, CancellationToken ct) => _services.HasRoom ? RoomAnswerAsyncOf(answer, ct) : Task.FromResult(NoRoomJson);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private Task<string> RoomAnswerAsyncOf(Func<object, CancellationToken, Task<string>> answer, CancellationToken ct) => answer(_services.Play!, ct);

    private Task<string> ArcadeAnswer(Func<object, string> answer) => _services.HasArcade ? ArcadeAnswerOf(answer) : Task.FromResult(NoArcadeJson);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<string> ArcadeAnswerOf(Func<object, string> answer)
    {
        var arcade = _services.Arcade!;
        return await UiThread.InvokeAsync(() => answer(arcade));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private AudienceBudget RoomBudget() => _services.Play!.Effective;

    /// <summary>What a phone or a wire hears from a role that has no room or no arcade (round 64): a closed door with the reason, never a crash.</summary>
    private const string NoRoomJson = "{\"ok\":false,\"msg\":\"no audience room on this node\"}";
    private const string NoArcadeJson = "{\"ok\":false,\"msg\":\"no arcade on this node\"}";

    private volatile bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _pushTimer.Stop();
        _clockTimer.Stop();
        StopListeners();
        lock (_mvGate)
        {
            _mvSink.Dispose();
        }
    }

    // ---- remote multiview ---------------------------------------------------

    private readonly PatternEngine _mvEngine = new();
    private readonly SinkState _mvSink = new();
    private readonly object _mvGate = new();

    /// <summary>
    /// Renders the configured multiview (Pattern tab) to a JPEG for /mv.jpg — the engine is
    /// thread-safe over immutable snapshots, so this runs on the socket task, ~1 fps/viewer.
    /// </summary>
    private byte[] RenderMultiviewJpeg(int width, int number)
    {
        lock (_mvGate)
        {
            var snap = _kernel.Bus.Current;
            // The page frame stays 16:9 — the tiles inside it are what carry their targets' shapes.
            var w = width;
            var h = w * 9 / 16;
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(w, h),
                ReferenceSize = new SKSizeI(w, h),
                Time = ShowClock.Seconds,
                Now = DateTime.Now,
                UtcNow = DateTime.UtcNow,
                Sink = Patterns.Core.Model.SinkKind.Thumbnail,
                SinkIndex = 0,
                Preview = _kernel.Bus.Sandbox,
                SinkLabel = "mv-remote",
            };
            var frame = new PatternFrame
            {
                Snapshot = snap,
                Config = snap.State.Pattern,
                Ctx = ctx,
                Sink = _mvSink,
                Canvas = new SKSizeI(w, h),
                Palette = Palette.Resolve(snap),
            };
            _mvEngine.RenderMultiview(surface.Canvas, in frame, _mvSink, Multiviews.Primary(snap.State, number));
            surface.Canvas.Flush();
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 72);
            return data.ToArray();
        }
    }

    private const string RunPage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<title>Patterns Run</title>
<style>
  :root { --bg:#0D0F14; --panel:#151A22; --line:#2A313E; --text:#E8ECF2; --mut:#98A1B1;
          --acc:#3EC1F3; --pgm:#E0342E; --pvw:#2EE68A; --hold:#FFC24D; --off:#4A505E; }
  * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
  body { margin:0; background:var(--bg); color:var(--text); font:16px/1.35 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; padding:12px; }
  .live { display:flex; align-items:center; gap:12px; border-bottom:2px solid var(--pgm); padding:8px 4px 10px; }
  .live .tag { font-size:13px; letter-spacing:.14em; color:var(--mut); font-weight:700; }
  .live .label { font-size:26px; font-weight:800; flex:1; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .chip { font-size:13px; font-weight:800; letter-spacing:.08em; border-radius:6px; padding:4px 8px; display:none; }
  .chip.on { display:inline-block; }
  .armed { background:#3A2E10; color:var(--hold); border:1px solid var(--hold); }
  .hold { background:var(--hold); color:#0E0F13; }
  .bo { background:#000; color:var(--pgm); border:1px solid var(--pgm); }
  .music { background:#10303A; color:var(--acc); border:1px solid var(--acc); }
  .card { background:var(--panel); border:1px solid var(--line); border-radius:12px; padding:12px; margin-top:12px; }
  .card.standby { border-color:var(--pvw); border-width:2px; }
  .card .k { font-size:12px; letter-spacing:.14em; color:var(--mut); font-weight:700; }
  .card .n { font-size:30px; font-weight:800; }
  .card .num { color:var(--mut); font-family:ui-monospace,Menlo,Consolas,monospace; margin-right:8px; }
  .card .notes { color:var(--hold); margin-top:4px; }
  .card .broken { color:var(--pgm); margin-top:4px; }
  .row { display:flex; gap:10px; margin-top:12px; }
  button { border:1px solid var(--line); border-radius:12px; background:var(--panel); color:var(--text); font:inherit; font-weight:800; padding:18px 10px; cursor:pointer; flex:1; font-size:20px; }
  button:disabled { opacity:.35; }
  #go { background:#1E9E5A; border-color:#1E9E5A; color:#fff; flex:2; font-size:26px; }
  #go.confirm { background:var(--hold); color:#0E0F13; }
  #hold.on { background:var(--hold); color:#0E0F13; }
  .next div, .hist div { display:flex; gap:10px; padding:5px 0; border-top:1px solid var(--line); font-size:16px; }
  .next div:first-child, .hist div:first-child { border-top:none; }
  .hist .bad { color:var(--pgm); font-weight:700; }
  img { width:100%; border-radius:8px; margin-top:12px; border:1px solid var(--line); }
  #err { color:var(--pgm); font-size:13px; min-height:16px; margin-top:8px; text-align:center; }
</style>
</head>
<body>
<div class="live">
  <span class="tag">LIVE</span>
  <span class="label" id="air">—</span>
  <span class="chip bo" id="cbo">BLACKOUT</span>
  <span class="chip hold" id="chold">HOLD</span>
  <span class="chip armed" id="carmed">ARMED</span>
  <span class="chip music" id="cmusic">♪ MUSIC</span>
  <span class="chip hold" id="chold2">STING HOLD</span>
  <span class="chip hold" id="cduck">DUCK</span>
</div>
<div class="card standby">
  <div class="k">STANDBY</div>
  <div class="n" id="sb">No cue on standby</div>
  <div class="notes" id="sbnotes"></div>
  <div class="broken" id="sbbroken"></div>
</div>
<div class="row">
  <button id="up" onclick="cmd('CUE STANDBY PREV')">▲</button>
  <button id="down" onclick="cmd('CUE STANDBY NEXT')">▼</button>
  <button id="go" onclick="go()">GO</button>
  <button id="hold" onclick="hold()">HOLD</button>
</div>
<div id="err"></div>
<div class="card next"><div class="k">NEXT</div><div id="next"></div></div>
<img id="pgm" src="/pgm.jpg" alt="program">
<div class="card hist"><div class="k">HISTORY</div><div id="hist"></div></div>
<script>
var st = null, rev = 0, standbyId = '';
function esc(s){ var d=document.createElement('div'); d.textContent=s==null?'':s; return d.innerHTML; }
function tok(){ try { return localStorage.getItem('patterns.token') || ''; } catch (e) { return ''; } }
function pair(){ var t = prompt('This desk asks for its pairing token (Remote page, TRUST):'); if (!t) return false; try { localStorage.setItem('patterns.token', t.trim()); } catch (e) {} return true; }
function cmd(c, again) {
  return fetch('/api/cmd', { method:'POST', body:c, headers:{'X-Patterns-Client':'run-page', 'X-Patterns-Token':tok()} })
    .then(function(r){ if (r.status === 403 && !again && pair()) return cmd(c, true); return r.json().then(function(j){ document.getElementById('err').textContent = j.ok ? '' : j.msg; }); })
    .catch(function(){ document.getElementById('err').textContent = 'Connection lost'; });
}
function go(){ if (standbyId) cmd('CUE GO ' + standbyId); }
function hold(){ var h = st && st.cuestack && st.cuestack.hold; cmd('CUE HOLD ' + (h ? 'OFF' : 'ON')); }
function render(s) {
  st = s; rev = s.rev || 0;
  var c = s.cuestack || {};
  document.getElementById('air').textContent = s.airLabel || '—';
  document.getElementById('cbo').classList.toggle('on', !!s.blackout);
  document.getElementById('chold').classList.toggle('on', !!c.hold);
  document.getElementById('carmed').classList.toggle('on', !!c.armed);
  document.getElementById('cmusic').classList.toggle('on', !!(s.music && s.music.playing));
  var h2 = document.getElementById('chold2');
  h2.classList.toggle('on', !!s.stingHold);
  h2.textContent = s.stingHold ? 'STING HOLD: ' + s.stingHold : 'STING HOLD';
  document.getElementById('cduck').classList.toggle('on', !!s.duck);
  var sb = c.standby; standbyId = sb ? sb.id : '';
  document.getElementById('sb').innerHTML = sb ? '<span class="num">' + esc(sb.number) + '</span>' + esc(sb.name) : 'No cue on standby';
  document.getElementById('sbnotes').textContent = sb ? (sb.notes || '') : '';
  var go = document.getElementById('go');
  go.disabled = !(c.armed && sb);
  go.classList.toggle('confirm', !!c.confirm);
  go.textContent = c.confirm ? c.confirm : (sb ? 'GO ' + sb.number : 'GO');
  var hold = document.getElementById('hold');
  hold.disabled = !c.armed; hold.classList.toggle('on', !!c.hold);
  var nx = document.getElementById('next'); nx.innerHTML = '';
  (c.next || []).forEach(function(x){ var d=document.createElement('div'); d.innerHTML='<span class="num">'+esc(x.number)+'</span>'+esc(x.name); nx.appendChild(d); });
  if (!c.next || c.next.length === 0) nx.innerHTML = '<div style="color:var(--mut)">end of the list</div>';
  var h = document.getElementById('hist'); h.innerHTML = '';
  (c.history || []).forEach(function(r){
    var d=document.createElement('div');
    var bad = /Failed|Refused/.test(r.outcome);
    d.innerHTML = '<span class="num">'+esc((r.at||'').slice(11,19))+'</span><span style="flex:1">'+esc(r.number+' '+r.name)+'</span><span class="'+(bad?'bad':'')+'">'+esc(r.outcome)+'</span><span style="color:var(--mut)">'+esc(r.origin)+'</span>';
    h.appendChild(d);
  });
}
function poll() {
  fetch('/api/state?since=' + rev).then(function(r){ return r.json(); })
    .then(function(s){ render(s); document.getElementById('err').textContent=''; poll(); })
    .catch(function(){ document.getElementById('err').textContent = 'Connection lost — retrying…'; setTimeout(poll, 1500); });
}
fetch('/api/state').then(function(r){ return r.json(); }).then(function(s){ render(s); poll(); });
setInterval(function(){ var i=document.getElementById('pgm'); var n=new Image(); n.onload=function(){ i.src=n.src; }; n.src='/pgm.jpg?t='+Date.now(); }, 2000);
</script>
</body>
</html>
""";

    private const string MultiviewPage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Patterns Multiview</title>
<style>
  body { margin:0; background:#000; }
  img { width:100vw; height:auto; display:block; }
  #err { color:#F0524D; font:13px system-ui; text-align:center; padding:8px; }
</style>
</head>
<body>
<img id="mv" src="/mv.jpg" alt="multiview">
<div id="err"></div>
<script>
var img = document.getElementById('mv');
setInterval(function () {
  var next = new Image();
  next.onload = function(){ img.src = next.src; document.getElementById('err').textContent=''; };
  next.onerror = function(){ document.getElementById('err').textContent = 'Connection lost — retrying…'; };
  next.src = '/mv.jpg?w=' + Math.min(1920, Math.max(320, Math.round(window.innerWidth * (window.devicePixelRatio || 1)))) + '&t=' + Date.now();
}, 1000);
</script>
</body>
</html>
""";

}
