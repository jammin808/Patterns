using Patterns.Devices;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The standby's link: the dial with its nonce, the read loop, every line from the main landed
/// on the UI thread, the beats with their clocks, and the link closed. A caller and a stage
/// timer node link the same way.
/// </summary>
public sealed partial class TwinService
{
    // ---- the standby: the link ---------------------------------------------------------

    /// <summary>Where the main is: the address the operator gave, else the one whose beacon says it is a twin main.</summary>
    private (string Host, int Port)? MainAddress()
    {
        var cfg = _kernel.State.Twin;
        if (cfg.MainHost.Length > 0) return (cfg.MainHost, cfg.Port);
        var beacon = _kernel.Beacon.LastBeacon;
        var from = _kernel.Beacon.LastFrom;
        if (beacon is { Twin: > 0 } && from is not null && beacon.Instance != _kernel.Beacon.Instance) return (from.Address.ToString(), beacon.Twin);
        return null;
    }

    private void Dial()
    {
        if (_dialling || _stream is not null || _phase is TwinPhase.Refused or TwinPhase.Off) return;
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
        _dialNonce = TwinAuth.NewNonce();
        var tookOver = _phase == TwinPhase.TookOver;
        var join = new TwinJoin(Name, Environment.MachineName, Instance, "", TookOver: tookOver, Kind: IsFollowerNode ? NodeKinds.Wire(_kernel.Profile) : "standby", Nonce: _dialNonce, Handover: tookOver ? _handover?.Id ?? "" : "").ToJson();
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
                await UiThread.InvokeAsync(() =>
                {
                    _client = client;
                    _stream = stream;
                    _dialling = false;
                    _dialFailures = 0;
                });
                await ReadLoop(client, stream, cts.Token);
            }
            catch (Exception ex)
            {
                // The first failure is said, then one in thirty: a main that is down for an hour is one line a half-minute, not one a second.
                var failures = Interlocked.Increment(ref _dialFailures);
                if (!cts.IsCancellationRequested && (failures == 1 || failures % 30 == 0)) Log.Info($"Twin: could not reach the main at {host}:{port} — {ex.Message}");
                client?.Dispose();
                UiThread.Post(() =>
                {
                    _dialling = false;
                    if (ReferenceEquals(_client, client)) CloseLink();
                });
            }
        });
    }

    private async Task ReadLoop(TcpClient client, NetworkStream stream, CancellationToken ct)
    {
        var reader = new BoundedLineReader(stream, JoinLineBytes);         // the main's first word is short; the show that follows is not
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                reader.MaxLineBytes = LinkLineBytes;
                var msg = TwinMessage.Parse(line);
                if (msg.Word == TwinWord.Unknown) continue;
                var heard = Clock();                                            // stamped at the read, before the hop to the UI thread: the exchange's fourth time is the arrival, not the dispatch
                await UiThread.InvokeAsync(() => OnLine(client, msg, heard));
                if (msg.Word is TwinWord.Refused or TwinWord.Bye) break;
            }
        }
        catch (Exception)
        {
            // The main went away: the silence is counted below.
        }
        UiThread.Post(() =>
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
    private void OnLine(TcpClient client, TwinMessage msg, DateTime? heardUtc = null)
    {
        if (!ReferenceEquals(_client, client)) return;
        var now = Clock();
        switch (msg.Word)
        {
            case TwinWord.Challenge:
            {
                // The main proves the key over this dial's nonce before this desk proves anything —
                // a main that cannot is a stranger on the port, and gets no proof, no show and no hold.
                var challenge = TwinChallenge.Parse(msg.Payload);
                var key = _kernel.State.Twin.Key;
                if (key.Length == 0)
                {
                    // Nothing to prove with: said here, in this desk's own words, not as a "wrong key" from the main.
                    _phase = TwinPhase.Refused;
                    _note = IsFollowerNode ? "this node has no key — the desk's key goes on this node's Machine tab" : "this desk has no key — the main's key goes on the Machine page, TWIN";
                    Log.Warn("Twin: this desk has no key to prove to the main; the link is closed.");
                    CloseLink();
                    break;
                }
                if (challenge is null || !TwinAuth.Verify(key, _dialNonce, challenge.Nonce, challenge.Proof))
                {
                    _phase = TwinPhase.Refused;
                    _note = IsFollowerNode ? "the desk did not prove the key — is the key on this node's Machine tab the one the desk's Machine page shows?" : "the main did not prove the key — is its key the same as this desk's?";
                    Log.Warn($"Twin: the main at {_mainName} could not prove the key; the link is closed.");
                    CloseLink();
                    break;
                }
                if (!TryWriteToMain(TwinMessage.Format(TwinWord.Proof, TwinAuth.Proof(key, challenge.Nonce, Instance)))) CloseLink();
                break;
            }
            case TwinWord.Welcome:
                _welcome = TwinWelcome.Parse(msg.Payload);
                if (_welcome is not null && _welcome.Name.Length > 0) _mainName = _welcome.Name;
                _lastHeardUtc = now;
                _note = "";
                if (_phase == TwinPhase.TookOver) SendWhatIHave();
                // A caller's own cues, planned before the desk's show lands over them: kept, and
                // offered once the show is here — the desk decides.
                if (_kernel.Profile == NodeKind.Caller && !_planOffered && _kernel.State.Stacks.Any(s => s.Cues.Count > 0)) _myPlanJson = CuePlan.Json(_kernel.State);
                break;
            case TwinWord.Live:
                _lastHeardUtc = now;
                if (IsFollowerNode) AdoptLive(msg.Payload);
                break;
            case TwinWord.Refused:
                _phase = TwinPhase.Refused;
                _note = msg.Payload;
                Log.Warn($"Twin: the main {_mainName} refused the link — {msg.Payload}.");
                break;
            case TwinWord.Show when _phase == TwinPhase.TookOver:
            case TwinWord.Section when _phase == TwinPhase.TookOver:
            case TwinWord.Air when _phase == TwinPhase.TookOver:
                // This desk has the show: nothing the main sends lands until it takes the show back.
                _lastHeardUtc = now;
                break;
            case TwinWord.HandBack:
                HandedBack(now);
                // The answer the main waits for, said after the outputs closed and the marker went, for the
                // hand-back by name — and again for one already answered, so a hand-back told twice is
                // answered twice. A line that cannot be written closes the link: the next join says standby, which is the answer too.
                if (msg.Payload.Length > 0 && !TryWriteToMain(TwinMessage.Format(TwinWord.Released, msg.Payload))) CloseLink();
                break;
            case TwinWord.Show:
            {
                var ok = false;
                RememberLanded(msg.Payload);
                _services.BulkEdit(() => ok = TwinSync.ApplyShow(_kernel.State, msg.Payload));
                if (ok)
                {
                    _sectionsApplied += TwinSync.MirroredSections.Count;
                    var first = _phase != TwinPhase.InStep;
                    _phase = TwinPhase.InStep;
                    _lastHeardUtc = now;
                    _services.NotifyShowMirrored(null);
                    if (first)
                    {
                        if (IsFollowerNode)
                        {
                            Log.Info($"Twin: this {NodeKinds.Wire(_kernel.Profile)} node is in step with {_mainName}.");
                            _services.Notify(_kernel.Profile == NodeKind.Timer
                                ? $"Nodes: in step with {_mainName} — its clock and its messages show here."
                                : $"Nodes: in step with {_mainName} — its show is here, and GO from here runs there.");
                            if (_kernel.Profile == NodeKind.Caller && _myPlanJson is { } plan)
                            {
                                _myPlanJson = null;
                                _planOffered = true;
                                var offer = TwinMessage.Format(TwinWord.Plan, plan);
                                _ = Task.Run(() => TryWriteToMain(offer));
                            }
                        }
                        else
                        {
                            Log.Info($"Twin: in step with the main {_mainName}; outputs held closed.");
                            _services.Notify($"Twin: in step with {_mainName} — the show is mirrored here and the outputs are held closed.");
                        }
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
                if (TwinSync.IsCallerSection(msg.Name)) _lastLanded[msg.Name] = msg.Payload;   // the desk's own: not an edit of this caller's to send back
                _services.BulkEdit(() => ok = TwinSync.ApplySection(_kernel.State, msg.Name, msg.Payload));
                if (ok)
                {
                    _sectionsApplied++;
                    _lastHeardUtc = now;
                    if (_phase == TwinPhase.MainSilent) { _phase = TwinPhase.InStep; _note = ""; }
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
                if (_phase == TwinPhase.MainSilent) { _phase = TwinPhase.InStep; _note = ""; }
                HeardMainBeat(msg.Payload, heardUtc ?? now);
                break;
            case TwinWord.Bye:
                // A main leaving on purpose (its role changed, a clean exit) is not a main that died: nothing is taken over.
                CloseLink();
                if (_phase != TwinPhase.TookOver)
                {
                    _phase = TwinPhase.Connecting;
                    _lastHeardUtc = null;
                    _note = "";
                }
                Log.Info($"Twin: the main {_mainName} said goodbye.");
                break;
        }
    }

    /// <summary>A standby that has the show, welcomed back by the main: its show and its air go to the main, for TAKE BACK.</summary>
    private void SendWhatIHave()
    {
        string show;
        string air;
        try
        {
            show = TwinMessage.Format(TwinWord.Show, TwinSync.WireJson(_kernel.State, _kernel.State.Twin.SendSecrets));
            air = AirLine();
        }
        catch (Exception ex)
        {
            Log.Warn("Twin: this desk's show could not be written for the main.", ex);
            return;
        }
        _ = Task.Run(() =>
        {
            TryWriteToMain(show);
            TryWriteToMain(air);
        });
    }

    /// <summary>The main took the show back: the outputs close and are held again, the marker goes, the show that follows the word puts this desk in step.</summary>
    private void HandedBack(DateTime now)
    {
        if (_phase != TwinPhase.TookOver) return;
        var main = _mainName.Length > 0 ? _mainName : "the main";
        _services.OutputsHeldBy = "this desk is the standby twin";
        if (_services.OutputsLive) _services.CloseOutputs();
        try
        {
            TwinHandover.Clear(_kernel.Store.BaseDirectory);
        }
        catch (Exception)
        {
            // a folder that would not take the marker did not take one
        }
        _phase = TwinPhase.Connecting;
        _note = "";
        _lastHeardUtc = now;
        _mirroredAir = null;
        Log.Warn($"Twin: {main} took the show back; this desk stands by again.");
        _services.Notify($"Twin: {main} took the show back — this desk's outputs are held closed and it follows again.");
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
        // The next link measures the clocks afresh; the room clock keeps the last frame it knew — a jump would be worse than a stale offset.
        _linkClock = new LinkClock();
        Interlocked.Exchange(ref _lastMainSentTicks, 0);
        Interlocked.Exchange(ref _lastMainReceivedTicks, 0);
    }

    /// <summary>
    /// The main's beat with its stamps: its stamp kept for the echo, and — when it echoes this
    /// desk's own last beat — one exchange closed on the link clock. A follower's room clock then
    /// follows the desk's, past the deadband; a standby twin's does not: a standby that takes over
    /// is a desk of its own, and its clock is its own frame — the offset is on its line instead.
    /// </summary>
    private void HeardMainBeat(string payload, DateTime heardUtc)
    {
        var beat = TwinBeat.Parse(payload);
        if (!beat.HasStamps) return;
        if (beat.HasEcho)
        {
            _linkClock.Sample(beat.PeerSentTicks, beat.PeerReceivedTicks, beat.SentTicks, heardUtc.Ticks);
            if (IsFollowerNode && _kernel.Clock.Follow(_linkClock.Offset)) Log.Info($"Twin: {_kernel.Clock.Words} (measured on the link, round trip {_linkClock.Delay.TotalMilliseconds:0} ms).");
        }
        Interlocked.Exchange(ref _lastMainSentTicks, beat.SentTicks);
        Interlocked.Exchange(ref _lastMainReceivedTicks, heardUtc.Ticks);
    }

    /// <summary>The peer's clock against this desk's, from the link's beats — null before an exchange closed.</summary>
    public TimeSpan? ClockOffset => _linkClock.Known ? _linkClock.Offset : null;

    /// <summary>On a main or a desk hosting callers: each peer's clock against this desk's, by its label on the line.</summary>
    public IReadOnlyList<(string Name, TimeSpan Offset)> PeerClocks
    {
        get
        {
            lock (_gate)
            {
                return _standbys.Where(s => s.Clock.Known).Select(s => (PeerLabel(s), s.Clock.Offset)).ToList();
            }
        }
    }

    /// <summary>The clocks apart on either side — "CLOCKS 3.2 s APART — …" — or "" while within two seconds.</summary>
    public string ClockApartWords
    {
        get
        {
            if (_role == TwinRole.Main || _hosting)
            {
                return string.Join(" · ", PeerClocks.Where(c => LinkClock.Apart(c.Offset)).Select(c => LinkClock.ApartWords(c.Name, c.Offset)));
            }
            return _linkClock.Known && LinkClock.Apart(_linkClock.Offset)
                ? LinkClock.ApartWords(_mainName.Length > 0 ? _mainName : IsFollowerNode ? "the desk" : "the main", _linkClock.Offset)
                : "";
        }
    }

    private string LinkNote(string whose) => _linkClock.Known ? LinkClock.Note(_linkClock.Offset, whose) : "";

    /// <summary>The peers' clocks for a desk hosting callers with the twin off: " · timer STAGE-PC's clock 0.8 s behind", and the warning past two seconds.</summary>
    private string PeerClockWords()
    {
        var notes = PeerClocks.Select(c => LinkClock.Note(c.Offset, c.Name + "'s")).Where(n => n.Length > 0).ToList();
        var apart = ClockApartWords;
        var words = string.Join(" · ", notes);
        if (apart.Length > 0) words = words.Length > 0 ? words + " · " + apart : apart;
        return words.Length > 0 ? " · " + words : "";
    }

    private static string PeerLabel(Standby s) => s.IsCaller ? "caller " + s.Name : s.IsFollower ? "timer " + s.Name : s.Name;

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
}
