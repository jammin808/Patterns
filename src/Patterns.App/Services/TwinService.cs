using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The twin link. As the main: a port the standbys join; the whole show on welcome, then each
/// section the publish names dirty (trailing 200 ms, like the remote's pushes), the air record
/// whenever it moves, and a beat every second. As the standby: dials the main — the address the
/// operator gave, or the one whose beacon this machine hears — joins with the key, copies what
/// comes onto its own show in place with the outputs held closed, beats back, and takes the show
/// when the main has been silent past the limit and the operator said it may, or when they
/// press TAKE OVER. Sockets on workers, the show on the UI thread, like every wire the desk has;
/// the timing and the words are <see cref="TwinWatch"/>'s, tested without a socket.
/// </summary>
public sealed class TwinService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _flushTimer;
    private readonly object _gate = new();
    private string _activeKey = "";
    private CancellationTokenSource? _cts;
    private TwinRole _role;

    // ---- the main's side ----
    private TcpListener? _listener;
    private readonly List<Standby> _standbys = new();
    private readonly HashSet<string> _pendingSections = new(StringComparer.Ordinal);
    private bool _pendingWhole;
    private bool _pendingAir;
    private RecoverySnapshot? _air;
    private long _sectionsSent;

    // ---- the standby's side ----
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly object _writeGate = new();
    private TwinPhase _phase;
    private string _mainName = "";
    private TwinWelcome? _welcome;
    private DateTime? _lastHeardUtc;
    private long _sectionsApplied;
    private string _note = "";
    private RecoverySnapshot? _mirroredAir;
    private bool _dialling;
    private DateTime _lastDialUtc;
    private long _beat;

    /// <summary>The clock the watch reads; the tests move it.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>How a process is seen from outside; the tests answer without a process tree.</summary>
    public IProcessProbe Probe { get; set; } = new SystemProcessProbe();

    public TwinService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TwinWatch.BeatEvery };
        _timer.Tick += (_, _) => Tick();
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _flushTimer.Tick += (_, _) =>
        {
            _flushTimer.Stop();
            Flush();
        };
        _services.Bus.SectionsPublished += OnBuilt;
        _services.RecoveryMoved += OnRecoveryMoved;
    }

    /// <summary>A random id per process, so a standby never joins itself through its own beacon.</summary>
    public string Instance { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>How this desk names itself on the link: the beacon's name, else the computer's.</summary>
    public string Name => _services.Beacon.MachineName;

    public TwinRole Role => _role;

    /// <summary>Where the link stands (UI thread).</summary>
    public TwinPhase Phase => _role switch
    {
        TwinRole.Off => TwinPhase.Off,
        TwinRole.Main => _listener is null ? TwinPhase.Off : TwinPhase.Listening,
        _ => _phase,
    };

    /// <summary>The main's name as a standby knows it; "" before the first welcome.</summary>
    public string MainName => _mainName;

    /// <summary>The standbys a main has, by name (UI thread or not).</summary>
    public IReadOnlyList<string> StandbyNames
    {
        get
        {
            lock (_gate)
            {
                return _standbys.Select(s => s.Name).ToList();
            }
        }
    }

    /// <summary>The Machine page's line (UI thread).</summary>
    public string Status
    {
        get
        {
            var now = Clock();
            switch (_role)
            {
                case TwinRole.Main:
                    if (_listener is null) return _note.Length > 0 ? _note : "Twin off.";
                    List<(string, DateTime)> beats;
                    lock (_gate)
                    {
                        beats = _standbys.Select(s => (s.Name, s.LastBeatUtc)).ToList();
                    }
                    return TwinWatch.DescribeMain(_services.State.Twin.Port, beats, _sectionsSent, now);
                case TwinRole.Standby:
                    return TwinWatch.DescribeStandby(_phase, _mainName, _lastHeardUtc, _sectionsApplied, _services.State.Twin.AutoTakeOver, now, _note);
                default:
                    return "Twin off.";
            }
        }
    }

    /// <summary>The words for the health line: the twin's line while it is not simply in step, "" otherwise.</summary>
    public string HealthWords => _role == TwinRole.Off || Phase is TwinPhase.InStep or TwinPhase.Listening ? "" : Status;

    /// <summary>TWIN STATUS's payload.</summary>
    public string StatusJson()
    {
        var phase = Phase.ToString();
        return JsonSerializer.Serialize(new
        {
            role = _role.ToString().ToLowerInvariant(),
            phase = char.ToLowerInvariant(phase[0]) + phase[1..],
            words = Status,
            main = _mainName,
            standbys = StandbyNames,
            sectionsSent = _sectionsSent,
            sectionsMirrored = _sectionsApplied,
        });
    }

    // ---- the settings ---------------------------------------------------------------

    /// <summary>Opens or closes the listener or the link to match the settings (UI thread, on every publish).</summary>
    public void Reconcile()
    {
        var cfg = _services.State.Twin;
        var key = $"{cfg.Role}|{cfg.Port}|{cfg.MainHost}|{cfg.Key}";
        if (key == _activeKey) return;
        _activeKey = key;
        Stop(sayGoodbye: true);
        _role = cfg.Role;
        _note = "";
        switch (cfg.Role)
        {
            case TwinRole.Main:
                _cts = new CancellationTokenSource();
                try
                {
                    _listener = new TcpListener(IPAddress.Any, cfg.Port);
                    _listener.Start();
                    _ = AcceptLoop(_listener, _cts.Token);
                    Log.Info($"Twin: listening for a standby on port {cfg.Port}.");
                }
                catch (Exception ex)
                {
                    _listener = null;
                    _note = $"Twin: the main could not open port {cfg.Port} — {ex.Message}";
                    Log.Warn(_note, ex);
                    _activeKey = ""; // retried on the next change
                }
                _timer.Start();
                break;
            case TwinRole.Standby:
                _cts = new CancellationTokenSource();
                _phase = TwinPhase.Connecting;
                _mainName = cfg.MainHost;
                _services.OutputsHeldBy = "this desk is the standby twin";
                if (_services.Outputs.IsLive)
                {
                    _services.Outputs.CloseAll();
                    _services.Notify("Twin: this desk is the standby now — its outputs are held closed until it takes over.");
                }
                _timer.Start();
                Dial();
                break;
            default:
                _timer.Stop();
                break;
        }
    }

    private void Stop(bool sayGoodbye)
    {
        _timer.Stop();
        _flushTimer.Stop();
        _cts?.Cancel();
        _cts = null;
        List<Standby> standbys;
        lock (_gate)
        {
            standbys = _standbys.ToList();
            _standbys.Clear();
        }
        foreach (var s in standbys)
        {
            if (sayGoodbye) s.TryWrite(TwinMessage.Format(TwinWord.Bye));
            s.Dispose();
        }
        try { _listener?.Stop(); } catch { /* already down */ }
        _listener = null;
        _pendingSections.Clear();
        _pendingWhole = false;
        _pendingAir = false;
        _sectionsSent = 0;
        if (_role == TwinRole.Standby)
        {
            if (sayGoodbye) TryWriteToMain(TwinMessage.Format(TwinWord.Bye));
            CloseLink();
            _services.OutputsHeldBy = "";
        }
        _phase = TwinPhase.Off;
        _welcome = null;
        _mainName = "";
        _lastHeardUtc = null;
        _sectionsApplied = 0;
        _mirroredAir = null;
        _role = TwinRole.Off;
    }

    /// <summary>Once a second (UI thread): the beats out, the silence counted, the redial, the takeover a standby was told it may make. Public so a test can move the clock and tick.</summary>
    public void Tick()
    {
        try
        {
            var now = Clock();
            switch (_role)
            {
                case TwinRole.Main:
                {
                    var line = TwinMessage.Format(TwinWord.Beat, Interlocked.Increment(ref _beat).ToString());
                    List<Standby> standbys;
                    lock (_gate)
                    {
                        standbys = _standbys.ToList();
                    }
                    if (standbys.Count > 0) _ = Task.Run(() => { foreach (var s in standbys) if (!s.TryWrite(line)) Drop(s); });
                    break;
                }
                case TwinRole.Standby:
                {
                    if (_phase == TwinPhase.InStep && TwinWatch.IsSilent(_lastHeardUtc, now))
                    {
                        _phase = TwinPhase.MainSilent;
                        Log.Warn($"Twin: the main {_mainName} has been silent for {TwinWatch.SilentAfter.TotalSeconds:0} s.");
                    }
                    if (TwinWatch.ShouldTakeOver(_services.State.Twin.AutoTakeOver, _phase, _lastHeardUtc, now))
                    {
                        TakeOver(ActionOrigin.Recovery);
                        break;
                    }
                    if (_stream is not null)
                    {
                        var line = TwinMessage.Format(TwinWord.Beat, Interlocked.Increment(ref _beat).ToString());
                        _ = Task.Run(() => TryWriteToMain(line));
                    }
                    else if (_phase is TwinPhase.Connecting or TwinPhase.MainSilent)
                    {
                        Dial();
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Twin tick failed.", ex);
        }
    }

    // ---- the main: what goes out ------------------------------------------------------

    /// <summary>A publish named its sections (UI thread): they go on the next flush; unnamed means the whole show.</summary>
    private void OnBuilt(ShowState state, HashSet<string>? dirty)
    {
        if (_role != TwinRole.Main || !ReferenceEquals(state, _services.State)) return;
        if (dirty is null) _pendingWhole = true;
        else foreach (var s in TwinSync.Mirrored(dirty)) _pendingSections.Add(s);
        if (!_flushTimer.IsEnabled) _flushTimer.Start();
    }

    /// <summary>The recovery record moved (UI thread): what is on air, the caller's place — a standby needs it to take over.</summary>
    private void OnRecoveryMoved(RecoverySnapshot? record)
    {
        _air = record;
        if (_role != TwinRole.Main) return;
        _pendingAir = true;
        if (!_flushTimer.IsEnabled) _flushTimer.Start();
    }

    /// <summary>The pending lines, built on the UI thread (the show is read here) and written on a worker.</summary>
    private void Flush()
    {
        if (_role != TwinRole.Main) return;
        List<Standby> standbys;
        lock (_gate)
        {
            standbys = _standbys.ToList();
        }
        var lines = new List<string>();
        try
        {
            if (_pendingWhole)
            {
                lines.Add(TwinMessage.Format(TwinWord.Show, TwinSync.ShowJson(_services.State)));
                _sectionsSent += TwinSync.MirroredSections.Count;
            }
            else
            {
                foreach (var section in TwinSync.Mirrored(_pendingSections))
                {
                    lines.Add(TwinMessage.Format(TwinWord.Section, TwinSync.SectionJson(_services.State, section), section));
                    _sectionsSent++;
                }
            }
            if (_pendingAir) lines.Add(AirLine());
        }
        catch (Exception ex)
        {
            Log.Warn("Twin: a section could not be written.", ex);
        }
        _pendingWhole = false;
        _pendingSections.Clear();
        _pendingAir = false;
        if (lines.Count == 0 || standbys.Count == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var s in standbys)
            {
                foreach (var line in lines)
                {
                    if (s.TryWrite(line)) continue;
                    Drop(s);
                    break;
                }
            }
        });
    }

    private string AirLine() => TwinMessage.Format(TwinWord.Air, _air is null ? "null" : JsonUtil.SerializeCompact(_air));

    private void Drop(Standby s)
    {
        lock (_gate)
        {
            _standbys.Remove(s);
        }
        s.Dispose();
        Log.Info($"Twin: the standby {s.Name} left.");
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleStandby(client, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            _ = ex;
        }
        catch (Exception ex)
        {
            Log.Warn("Twin accept loop ended.", ex);
        }
    }

    private async Task HandleStandby(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        Standby? standby = null;
        try
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var joinWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            joinWait.CancelAfter(TimeSpan.FromSeconds(5));
            var first = TwinMessage.Parse(await reader.ReadLineAsync(joinWait.Token));
            var join = first.Word == TwinWord.Join ? TwinJoin.Parse(first.Payload) : null;
            var key = _services.State.Twin.Key;
            string? refused = join is null ? "the first line was not a JOIN"
                : join.Proto != TwinMessage.Proto ? $"another version of the link (yours {join.Proto}, mine {TwinMessage.Proto})"
                : key.Length > 0 && !string.Equals(join.Key, key, StringComparison.Ordinal) ? "wrong key"
                : join.Instance == Instance ? "that is this very desk"
                : null;
            if (refused is not null)
            {
                await WriteLineAsync(stream, TwinMessage.Format(TwinWord.Refused, refused), ct);
                Log.Warn($"Twin: refused a standby ({refused}).");
                client.Dispose();
                return;
            }
            standby = new Standby(client, stream, join!.Name.Length > 0 ? join.Name : join.Machine, Clock());
            // The welcome and the whole show, read on the UI thread — the show is its own.
            var (welcome, show, air) = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var w = new TwinWelcome(Name, Environment.MachineName, Instance, Environment.ProcessId, ProcessStartTicks(), Environment.ProcessPath ?? "", _services.State.Name);
                return (w.ToJson(), TwinSync.ShowJson(_services.State), AirLine());
            });
            if (!standby.TryWrite(TwinMessage.Format(TwinWord.Welcome, welcome)) || !standby.TryWrite(TwinMessage.Format(TwinWord.Show, show)) || !standby.TryWrite(air))
            {
                standby.Dispose();
                return;
            }
            lock (_gate)
            {
                _standbys.Add(standby);
            }
            Log.Info($"Twin: the standby {standby.Name} joined and has the show.");
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                var msg = TwinMessage.Parse(line);
                if (msg.Word == TwinWord.Beat) standby.LastBeatUtc = Clock();
                else if (msg.Word == TwinWord.Bye) break;
            }
        }
        catch (Exception)
        {
            // A standby that went away is routine.
        }
        finally
        {
            if (standby is not null)
            {
                bool listed;
                lock (_gate)
                {
                    listed = _standbys.Remove(standby);
                }
                if (listed) Log.Info($"Twin: the standby {standby.Name} left.");
                standby.Dispose();
            }
            else
            {
                client.Dispose();
            }
        }
    }

    private static long ProcessStartTicks()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }

    // ---- the standby: the link ---------------------------------------------------------

    /// <summary>Where the main is: the address the operator gave, else the one whose beacon says it is a twin main.</summary>
    private (string Host, int Port)? MainAddress()
    {
        var cfg = _services.State.Twin;
        if (cfg.MainHost.Length > 0) return (cfg.MainHost, cfg.Port);
        var beacon = _services.Beacon.LastBeacon;
        var from = _services.Beacon.LastFrom;
        if (beacon is { Twin: > 0 } && from is not null && beacon.Instance != _services.Beacon.Instance) return (from.Address.ToString(), beacon.Twin);
        return null;
    }

    private void Dial()
    {
        if (_dialling || _stream is not null || _phase is TwinPhase.TookOver or TwinPhase.Refused or TwinPhase.Off) return;
        var now = Clock();
        if (now - _lastDialUtc < TwinWatch.BeatEvery) return;
        _lastDialUtc = now;
        var address = MainAddress();
        if (address is null) return; // waiting for a beacon: the line says so
        var (host, port) = address.Value;
        if (_mainName.Length == 0) _mainName = host;
        var cts = _cts;
        if (cts is null) return;
        _dialling = true;
        var join = new TwinJoin(Name, Environment.MachineName, Instance, _services.State.Twin.Key).ToJson();
        _ = Task.Run(async () =>
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient { NoDelay = true };
                using var connectWait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                connectWait.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(host, port, connectWait.Token);
                var stream = client.GetStream();
                await WriteLineAsync(stream, TwinMessage.Format(TwinWord.Join, join), cts.Token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _client = client;
                    _stream = stream;
                    _dialling = false;
                });
                await ReadLoop(client, stream, cts.Token);
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested) Log.Info($"Twin: could not reach the main at {host}:{port} — {ex.Message}");
                client?.Dispose();
                Dispatcher.UIThread.Post(() =>
                {
                    _dialling = false;
                    if (ReferenceEquals(_client, client)) CloseLink();
                });
            }
        });
    }

    private async Task ReadLoop(TcpClient client, NetworkStream stream, CancellationToken ct)
    {
        var reader = new StreamReader(stream, Encoding.UTF8, false, 65536, leaveOpen: true);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                var msg = TwinMessage.Parse(line);
                if (msg.Word == TwinWord.Unknown) continue;
                await Dispatcher.UIThread.InvokeAsync(() => OnLine(client, msg));
                if (msg.Word is TwinWord.Refused or TwinWord.Bye) break;
            }
        }
        catch (Exception)
        {
            // The main went away: the silence is counted below.
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_client, client)) return; // an older link; a newer one is up
            CloseLink();
            if (_phase == TwinPhase.InStep)
            {
                _phase = TwinPhase.MainSilent;
                Log.Warn($"Twin: the link to the main {_mainName} dropped.");
            }
        });
    }

    /// <summary>One line from the main, on the UI thread: the show and its sections land here, the beats are counted here.</summary>
    private void OnLine(TcpClient client, TwinMessage msg)
    {
        if (!ReferenceEquals(_client, client)) return;
        var now = Clock();
        switch (msg.Word)
        {
            case TwinWord.Welcome:
                _welcome = TwinWelcome.Parse(msg.Payload);
                if (_welcome is not null && _welcome.Name.Length > 0) _mainName = _welcome.Name;
                _lastHeardUtc = now;
                _note = "";
                break;
            case TwinWord.Refused:
                _phase = TwinPhase.Refused;
                _note = msg.Payload;
                Log.Warn($"Twin: the main {_mainName} refused the link — {msg.Payload}.");
                break;
            case TwinWord.Show:
            {
                var ok = false;
                _services.BulkEdit(() => ok = TwinSync.ApplyShow(_services.State, msg.Payload));
                if (ok)
                {
                    _sectionsApplied += TwinSync.MirroredSections.Count;
                    var first = _phase != TwinPhase.InStep;
                    _phase = TwinPhase.InStep;
                    _lastHeardUtc = now;
                    _services.NotifyShowMirrored(null);
                    if (first)
                    {
                        Log.Info($"Twin: in step with the main {_mainName}; outputs held closed.");
                        _services.Notify($"Twin: in step with {_mainName} — the show is mirrored here and the outputs are held closed.");
                    }
                }
                else
                {
                    Log.Warn("Twin: the main sent a show this build could not read.");
                }
                break;
            }
            case TwinWord.Section:
            {
                var ok = false;
                _services.BulkEdit(() => ok = TwinSync.ApplySection(_services.State, msg.Name, msg.Payload));
                if (ok)
                {
                    _sectionsApplied++;
                    _lastHeardUtc = now;
                    if (_phase == TwinPhase.MainSilent) _phase = TwinPhase.InStep;
                    _services.NotifyShowMirrored(new[] { msg.Name });
                }
                else
                {
                    Log.Warn($"Twin: the section '{msg.Name}' from the main could not land.");
                }
                break;
            }
            case TwinWord.Air:
                _mirroredAir = msg.Payload == "null" ? null : ReadAir(msg.Payload);
                _lastHeardUtc = now;
                break;
            case TwinWord.Beat:
                _lastHeardUtc = now;
                if (_phase == TwinPhase.MainSilent) _phase = TwinPhase.InStep;
                break;
            case TwinWord.Bye:
                // A main leaving on purpose (its role changed, a clean exit) is not a main that died: nothing is taken over.
                CloseLink();
                _phase = TwinPhase.Connecting;
                _lastHeardUtc = null;
                _note = "";
                Log.Info($"Twin: the main {_mainName} said goodbye.");
                break;
        }
    }

    private static RecoverySnapshot? ReadAir(string json)
    {
        try { return JsonUtil.Deserialize<RecoverySnapshot>(json); }
        catch (JsonException ex)
        {
            Log.Warn("Twin: the air record could not be read.", ex);
            return null;
        }
    }

    private void CloseLink()
    {
        var client = _client;
        _client = null;
        _stream = null;
        try { client?.Dispose(); } catch { /* already down */ }
    }

    private bool TryWriteToMain(string line)
    {
        var stream = _stream;
        if (stream is null) return false;
        try
        {
            lock (_writeGate)
            {
                stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- the standby: the show ---------------------------------------------------------------

    /// <summary>
    /// The standby runs the show from here: the link is dropped, a main on this machine that has
    /// hung is ended (the screens are then free, exactly as a start takes back the previous run's
    /// windows), the hold on the outputs lifts, and the air record the main sent last goes back
    /// on the way a watchdog restart puts it back.
    /// </summary>
    public ActionResult TakeOver(ActionOrigin origin)
    {
        if (_role != TwinRole.Standby) return ActionResult.Refused("This desk is not a standby twin — Machine page, TWIN.");
        if (_phase == TwinPhase.TookOver) return ActionResult.Done("This desk already took the show over.");
        if (_welcome is null) return ActionResult.Refused("Nothing to take over: no main has been joined yet.");
        var now = Clock();
        var main = _mainName.Length > 0 ? _mainName : "the main";
        var notes = new List<string>();
        _cts?.Cancel();
        _cts = new CancellationTokenSource(); // the redial never restarts on its own
        CloseLink();
        _phase = TwinPhase.TookOver;
        _note = $"at {now.ToLocalTime():HH:mm:ss}";

        // A main on this very machine that is still up but stopped beating has hung: its windows
        // would play on under nobody's hand and ours would open behind them.
        var w = _welcome;
        if (string.Equals(w.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase) && w.Pid > 0 && w.Pid != Environment.ProcessId)
        {
            var started = Probe.StartTicks(w.Pid);
            if (started is not null && started == w.StartedAtUtcTicks && OutputTakeover.IsPatterns(Probe.ExePath(w.Pid), w.ExePath))
            {
                notes.Add(Probe.Kill(w.Pid) ? $"ended {main}'s process (pid {w.Pid}) — it had stopped answering" : $"could not end {main}'s process (pid {w.Pid})");
            }
        }
        _services.OutputsHeldBy = "";
        var head = $"TOOK OVER from {main} {_note}" + (notes.Count > 0 ? " — " + string.Join(", ", notes) : "") + ".";
        Log.Warn($"Twin: {head} ({origin.Label})");
        _services.RecoverFromTwin(_mirroredAir, head);
        return ActionResult.Done(head);
    }

    /// <summary>After a takeover: the outputs held again, the link dialled again, the show mirrored again.</summary>
    public ActionResult StandByAgain(ActionOrigin origin)
    {
        if (_role != TwinRole.Standby) return ActionResult.Refused("This desk is not a standby twin — Machine page, TWIN.");
        if (_phase != TwinPhase.TookOver) return ActionResult.Done("This desk is standing by already.");
        _services.OutputsHeldBy = "this desk is the standby twin";
        if (_services.Outputs.IsLive) _services.Outputs.CloseAll();
        _phase = TwinPhase.Connecting;
        _note = "";
        _lastHeardUtc = null;
        _mirroredAir = null;
        _lastDialUtc = DateTime.MinValue;
        Log.Info($"Twin: standing by again for {_mainName} ({origin.Label}).");
        Dial();
        return ActionResult.Done($"Standing by again — the outputs are held closed and the link to {(_mainName.Length > 0 ? _mainName : "the main")} is being dialled.");
    }

    private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
    }

    public void Dispose()
    {
        Stop(sayGoodbye: true);
        _services.Bus.SectionsPublished -= OnBuilt;
        _services.RecoveryMoved -= OnRecoveryMoved;
    }

    /// <summary>One joined standby on the main's side: its socket, its name, when it last beat. Writes are serialised per standby.</summary>
    private sealed class Standby : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly object _writeGate = new();
        private long _lastBeatTicks;

        public Standby(TcpClient client, NetworkStream stream, string name, DateTime nowUtc)
        {
            _client = client;
            _stream = stream;
            Name = name;
            LastBeatUtc = nowUtc;
        }

        public string Name { get; }

        public DateTime LastBeatUtc
        {
            get => new(Interlocked.Read(ref _lastBeatTicks), DateTimeKind.Utc);
            set => Interlocked.Exchange(ref _lastBeatTicks, value.Ticks);
        }

        public bool TryWrite(string line)
        {
            try
            {
                lock (_writeGate)
                {
                    _stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            try { _client.Dispose(); } catch { /* already down */ }
        }
    }
}
