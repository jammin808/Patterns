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
/// The main's side: a publish onto the wire as the sections that moved, the flush, the listener,
/// a standby joining — the key proved over nonces, the show it gets, the hold when it has the
/// show — and each joined peer as the main keeps it.
/// </summary>
public sealed partial class TwinService
{
    // ---- the main: what goes out ------------------------------------------------------

    /// <summary>A publish named its sections (UI thread): they go on the next flush; unnamed means the whole show.</summary>
    private void OnBuilt(ShowState state, HashSet<string>? dirty)
    {
        if (!ReferenceEquals(state, _kernel.State)) return;
        if (IsFollowerNode)
        {
            if (_kernel.Profile == NodeKind.Caller) SendMyEdits(dirty);                  // a timer owns nothing of the show
            return;
        }
        if (_role != TwinRole.Main && !_hosting) return;
        if (dirty is null)
        {
            _pendingWhole = true;
            if (_landing is null) SetAsideQueued(null);                               // the whole show, the desk's own: newer than anything that waits
        }
        else
        {
            foreach (var s in TwinSync.Mirrored(dirty))
            {
                _pendingSections.Add(s);
                // Whose edit this is decides who is not sent it back: the peer whose edit is landing,
                // or nobody — the desk's own edit to a section is the newer, goes to every peer, and
                // sets aside a caller's edit still waiting for it. Decided here, at the publish: a
                // desk edit within the same flush window as a landing must not be mistaken for it.
                if (_landing is { } from) _echoSkip[s] = from;
                else
                {
                    _echoSkip.Remove(s);
                    SetAsideQueued(s);
                }
            }
        }
        ScheduleFlush();
    }


    /// <summary>The recovery record moved (UI thread): what is on air, the caller's place — a standby needs it to take over.</summary>
    private void OnRecoveryMoved(RecoverySnapshot? record)
    {
        _air = record;
        if (_role != TwinRole.Main) return;
        _pendingAir = true;
        ScheduleFlush();
    }

    /// <summary>The pending lines, built on the UI thread (the show is read here) and written on a worker.</summary>
    private void Flush()
    {
        if (_role != TwinRole.Main && !_hosting) return;
        List<Standby> standbys;
        lock (_gate)
        {
            standbys = _standbys.ToList();
        }
        var lines = new List<string>();
        var origins = new List<string?>();                                            // per section line: the peer whose edit it was, or null
        try
        {
            if (_pendingWhole)
            {
                lines.Add(TwinMessage.Format(TwinWord.Show, TwinSync.WireJson(_kernel.State, _kernel.State.Twin.SendSecrets)));
                origins.Add(null);
                _sectionsSent += TwinSync.MirroredSections.Count;
            }
            else
            {
                foreach (var section in TwinSync.Mirrored(_pendingSections))
                {
                    lines.Add(TwinMessage.Format(TwinWord.Section, TwinSync.WireSectionJson(_kernel.State, section, _kernel.State.Twin.SendSecrets), section));
                    origins.Add(_echoSkip.TryGetValue(section, out var from) ? from : null);
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
        _echoSkip.Clear();
        if (lines.Count == 0 || standbys.Count == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var s in standbys)
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    if (i < origins.Count && origins[i] is { } from && from == s.Instance) continue;   // its own edit: it has it
                    if (s.TryWrite(lines[i])) continue;
                    Drop(s);
                    break;
                }
            }
        }, CancellationToken.None);
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
            // stopped: the token was cancelled
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
            var reader = new BoundedLineReader(stream, JoinLineBytes);     // a JOIN is a few hundred bytes; the ceiling rises once the key is right
            using var joinWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            joinWait.CancelAfter(TimeSpan.FromSeconds(5));
            var first = TwinMessage.Parse(await reader.ReadLineAsync(joinWait.Token));
            var join = first.Word == TwinWord.Join ? TwinJoin.Parse(first.Payload) : null;
            var key = _kernel.State.Twin.Key;
            string? refused = join is null ? "the first line was not a JOIN"
                : join.Proto != TwinMessage.Proto ? $"another version of the link (yours {join.Proto}, mine {TwinMessage.Proto})"
                : key.Length == 0 ? "this main has no key yet"
                : join.Nonce.Length == 0 ? "the JOIN carried no nonce to prove the key over"
                : join.Instance == Instance ? "that is this very desk"
                : !join.IsFollower && _role != TwinRole.Main ? "this desk is not a twin main — it links callers and stage timers only"
                : join.IsFollower && !_kernel.IsDesk ? "a node does not host callers or stage timers"
                : null;
            if (refused is null)
            {
                // The key, proved and never read off the wire: this desk answers the joiner's nonce
                // first — a stranger listening on the port could otherwise hand a standby a show —
                // then the joiner answers this desk's, and a wrong answer is a wrong key.
                var serverNonce = TwinAuth.NewNonce();
                var challenge = new TwinChallenge(serverNonce, TwinAuth.Proof(key, join!.Nonce, serverNonce));
                await WriteLineAsync(stream, TwinMessage.Format(TwinWord.Challenge, challenge.ToJson()), ct);
                using var proofWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                proofWait.CancelAfter(TimeSpan.FromSeconds(5));
                var answer = TwinMessage.Parse(await reader.ReadLineAsync(proofWait.Token));
                refused = answer.Word != TwinWord.Proof ? "the line after the challenge was not a PROOF"
                    : !TwinAuth.Verify(key, serverNonce, join.Instance, answer.Payload) ? "wrong key"
                    : null;
            }
            if (refused is not null)
            {
                await WriteLineAsync(stream, TwinMessage.Format(TwinWord.Refused, refused), ct);
                Log.Warn($"Twin: refused a standby ({refused}).");
                client.Dispose();
                return;
            }
            // A claim made under a takeover this desk already took back — its hand-back never arrived,
            // or its answer never did — is answered with the hand-back again, never with a hold: the
            // room may well be looking at this desk by now.
            var takenBack = join!.TookOver && !join.IsFollower && join.Handover.Length > 0 && _takenBack.TryGetValue(join.Instance, out var tb) && tb.Takeover == join.Handover ? tb : (TakenBack?)null;
            standby = new Standby(client, stream, join.Name.Length > 0 ? join.Name : join.Machine, Clock()) { HoldsShow = join.TookOver && !join.IsFollower && takenBack is null, IsCaller = join.IsCaller, IsFollower = join.IsFollower, Instance = join.Instance, Machine = join.Machine, Handover = join.Handover };
            if (takenBack is { } stale) Log.Warn($"Twin: the standby {standby.Name} claimed the show under a takeover this desk took back at {stale.AtUtc.ToLocalTime():HH:mm:ss} — told the hand-back again.");
            // The welcome and the whole show, read on the UI thread — the show is its own. A standby
            // that ran the show while this desk was away gets the welcome and nothing to mirror: its
            // show is the newer one, and this desk holds its outputs until TAKE BACK.
            var (welcome, show, air) = await UiThread.InvokeAsync(() =>
            {
                var w = new TwinWelcome(Name, Environment.MachineName, Instance, Environment.ProcessId, ProcessStartTicks(), Environment.ProcessPath ?? "", _kernel.State.Name);
                if (standby.HoldsShow && _switchByHand is { } byHand && byHand.Instance == standby.Instance) _holderLinked = true;   // back on the link mid hand-back: this desk's picture is up on purpose, no hold
                else if (standby.HoldsShow) Hold(standby.Name, null, linked: true);
                // A standby that stands by again — no claim — while its answer to the hand-back is awaited: that is the answer.
                if (!join.TookOver && !standby.IsFollower && _releaseWait is { } wait && wait.Instance == standby.Instance) CompleteRelease(wait, "it stands by again");
                return (w.ToJson(), standby.HoldsShow ? "" : TwinSync.WireJson(_kernel.State, _kernel.State.Twin.SendSecrets), AirLine());
            });
            var welcomed = standby.TryWrite(TwinMessage.Format(TwinWord.Welcome, welcome))
                           && (takenBack is null || standby.TryWrite(TwinMessage.Format(TwinWord.HandBack, takenBack.Value.TakeBack)))
                           && (standby.HoldsShow || (standby.TryWrite(TwinMessage.Format(TwinWord.Show, show)) && standby.TryWrite(air)))
                           && standby.TryWrite(standby.BeatLine(Interlocked.Increment(ref _beat), Clock().Ticks));
            if (!welcomed)
            {
                standby.Dispose();
                return;
            }
            reader.MaxLineBytes = LinkLineBytes;                            // proved: a show's worth of JSON may travel on one line
            lock (_gate)
            {
                _standbys.Add(standby);
            }
            Log.Info(standby.IsCaller ? $"Twin: the caller {standby.Name} linked and has the show."
                : standby.IsFollower ? $"Twin: the stage timer {standby.Name} linked and follows the clock."
                : standby.HoldsShow
                ? $"Twin: the standby {standby.Name} joined and HAS THE SHOW — this desk's outputs are held until TAKE BACK."
                : $"Twin: the standby {standby.Name} joined and has the show.");
            if (standby.IsFollower)
            {
                // Off the accept thread: the desk's words and the live word are the UI thread's.
                UiThread.Post(() =>
                {
                    _services.Notify(standby.IsCaller
                        ? $"Nodes: the caller {standby.Name} linked — its GO, STANDBY and HOLD run here as its own."
                        : $"Nodes: the stage timer {standby.Name} linked — it shows this desk's clock and messages, and its ACKs land here.");
                    SendLive();
                });
            }
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                var msg = TwinMessage.Parse(line);
                if (msg.Word == TwinWord.Beat) standby.HeardBeat(msg.Payload, Clock());
                else if (msg.Word == TwinWord.Bye) break;
                else if (msg.Word == TwinWord.Released) await UiThread.InvokeAsync(() => OnReleased(standby, msg.Payload));
                else if (standby.IsFollower) await UiThread.InvokeAsync(() => OnCallerLine(standby, msg));
                else if (standby.HoldsShow && msg.Word is TwinWord.Show or TwinWord.Air)
                {
                    // What the standby has: kept for TAKE BACK, never applied on its own.
                    await UiThread.InvokeAsync(() =>
                    {
                        if (msg.Word == TwinWord.Show) _heldShowJson = msg.Payload;
                        else _heldAir = msg.Payload == "null" ? null : ReadAir(msg.Payload);
                        _heldLines++;
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // A standby that went away is routine; anything else on its line is said, so a peer that is dropped is never dropped silently.
            if (ex is not (IOException or ObjectDisposedException or OperationCanceledException or SocketException))
            {
                Log.Warn($"Twin: the line from {standby?.Name ?? "a peer"} ended on a fault.", ex);
            }
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
                if (standby.HoldsShow)
                {
                    await UiThread.InvokeAsync(() =>
                    {
                        if (!_holderLinked) return;
                        _holderLinked = false;
                        // Its process is still up by the marker: the marker decides, and what it sent
                        // is kept for this desk to put back should that process die with the show.
                        if (_holderMarked) return;
                        // Mid hand-back, the room being switched by hand: it keeps its place until the second press.
                        if (_switchByHand is { } byHand && byHand.Instance == standby.Instance) return;
                        _heldShowJson = null;
                        _heldAir = null;
                        Release("left the link");
                    });
                }
            }
            else
            {
                client.Dispose();
            }
        }
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

        /// <summary>It ran the show while this desk was away: nothing is mirrored to it, and this desk's outputs wait on TAKE BACK.</summary>
        public bool HoldsShow { get; set; }

        /// <summary>A caller node: follows the show and calls it, sends its cues back, never holds an output.</summary>
        public bool IsCaller { get; init; }

        /// <summary>A follower — a caller or a stage timer: mirrored to and sent the live word, never a standby that could take the show.</summary>
        public bool IsFollower { get; init; }

        /// <summary>The peer's own instance id, so its own edits are not echoed back to it.</summary>
        public string Instance { get; init; } = "";

        /// <summary>The computer it runs on — the same as this one means its windows are these displays.</summary>
        public string Machine { get; init; } = "";

        /// <summary>With <see cref="HoldsShow"/>: the id of the takeover it holds the show under, as its join said.</summary>
        public string Handover { get; init; } = "";

        public DateTime LastBeatUtc
        {
            get => new(Interlocked.Read(ref _lastBeatTicks), DateTimeKind.Utc);
            set => Interlocked.Exchange(ref _lastBeatTicks, value.Ticks);
        }

        private long _lastPeerSentTicks;
        private long _lastPeerReceivedTicks;

        /// <summary>This peer's clock against ours, from the beats' stamps.</summary>
        public LinkClock Clock { get; } = new();

        /// <summary>A beat from the peer: heard now, its stamp kept for the echo, and — when it echoes our own last beat — one exchange closed on the clock.</summary>
        public void HeardBeat(string payload, DateTime nowUtc)
        {
            LastBeatUtc = nowUtc;
            var beat = TwinBeat.Parse(payload);
            if (!beat.HasStamps) return;
            if (beat.HasEcho) Clock.Sample(beat.PeerSentTicks, beat.PeerReceivedTicks, beat.SentTicks, nowUtc.Ticks);
            Interlocked.Exchange(ref _lastPeerSentTicks, beat.SentTicks);
            Interlocked.Exchange(ref _lastPeerReceivedTicks, nowUtc.Ticks);
        }

        /// <summary>Our beat to this peer: our stamp, and the echo of its last — so its next beat closes an exchange for it, and its echo closes one for us.</summary>
        public string BeatLine(long seq, long nowTicks)
            => TwinMessage.Format(TwinWord.Beat, new TwinBeat(seq, nowTicks, Interlocked.Read(ref _lastPeerSentTicks), Interlocked.Read(ref _lastPeerReceivedTicks)).Format());

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
