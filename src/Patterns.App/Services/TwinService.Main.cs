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
        _lackStale = true;                                                            // round 89: a password typed here, a page pasted: what a standby is without is read again
        if (IsFollowerNode)
        {
            if (_kernel.Profile == NodeKind.Caller) SendMyEdits(dirty);                  // a timer owns nothing of the show
            return;
        }
        if (_role != TwinRole.Main && !_hosting) return;
        _keptStale = true;                                                            // the kept ids the show names may have moved: cut again at the next beat
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
        _keptStale = true;                                                            // the air record names pages too
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
        // Round 89: each line in two forms where the secrets make a difference — blanked, and whole for a standby the
        // secrets ride to (TwinSecrets.Rides: on this machine, SEND SECRETS on) — each peer is sent its own form.
        var rides = standbys.Select(RidesFor).ToList();
        var anyRides = rides.Contains(true);
        var lines = new List<(string Blank, string Whole)>();
        var origins = new List<string?>();                                            // per section line: the peer whose edit it was, or null
        try
        {
            if (_pendingWhole)
            {
                var blank = TwinMessage.Format(TwinWord.Show, TwinSync.WireJson(_kernel.State, sendSecrets: false));
                lines.Add((blank, anyRides ? TwinMessage.Format(TwinWord.Show, TwinSync.WireJson(_kernel.State, sendSecrets: true)) : blank));
                origins.Add(null);
                _sectionsSent += TwinSync.MirroredSections.Count;
            }
            else
            {
                foreach (var section in TwinSync.Mirrored(_pendingSections))
                {
                    lines.Add(SectionLines(section, anyRides));
                    origins.Add(_echoSkip.TryGetValue(section, out var from) ? from : null);
                    _sectionsSent++;
                }
            }
            if (_pendingAir)
            {
                // The air record holds a whole show while EDIT SAFE is open or a look is pinned: the SHOW line's rule.
                var air = AirLine(rides: false);
                lines.Add((air, anyRides ? AirLine(rides: true) : air));
            }
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
            for (var p = 0; p < standbys.Count; p++)
            {
                var s = standbys[p];
                for (var i = 0; i < lines.Count; i++)
                {
                    if (i < origins.Count && origins[i] is { } from && from == s.Instance) continue;   // its own edit: it has it
                    if (s.TryWrite(rides[p] ? lines[i].Whole : lines[i].Blank)) continue;
                    Drop(s);
                    break;
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>Round 89: one section's line blanked, and whole when a peer is sent its secrets — the same line for a section that carries none.</summary>
    private (string Blank, string Whole) SectionLines(string section, bool whole)
    {
        var blank = TwinMessage.Format(TwinWord.Section, TwinSync.WireSectionJson(_kernel.State, section, sendSecrets: false), section);
        return (blank, whole && TwinSync.CarriesSecrets(section) ? TwinMessage.Format(TwinWord.Section, TwinSync.WireSectionJson(_kernel.State, section, sendSecrets: true), section) : blank);
    }

    /// <summary>Round 89: the show's secrets ride this peer's link — a standby, on this machine, SEND SECRETS on (UI thread).</summary>
    private bool RidesFor(Standby s) => TwinSecrets.Rides(_kernel.State.Twin.SendSecrets, s.SameMachine, s.IsFollower);

    /// <summary>Round 89: why the secrets ride a standby's link or not — another machine is refused first, whatever the setting.</summary>
    private string WhyFor(Standby s) => TwinSecrets.Why(_kernel.State.Twin.SendSecrets, s.SameMachine);

    /// <summary>
    /// Round 89 (UI thread, each beat): each standby that follows is told whether the show's secrets ride its link and,
    /// when they do not, what this desk withholds — again whenever that changes: the setting, a password typed or
    /// cleared, the weather key. When the secrets come to ride a standby's link, the sections that carry them go on the
    /// next flush — the one path sections take, so an older copy never lands after a newer — and it has them within the
    /// second rather than at the next edit; its kept passes follow from SendKept. SEND SECRETS on, for a standby on
    /// another machine, is refused with words in the log and on the desk, once per standby while it stays on — a link
    /// that drops and joins again is not said again.
    /// </summary>
    private void SendSecretsWord()
    {
        if (_role != TwinRole.Main) return;
        if (!_kernel.State.Twin.SendSecrets) _refusedSaid.Clear();
        List<Standby> due;
        lock (_gate)
        {
            due = _standbys.Where(s => !s.IsFollower && !s.HoldsShow).ToList();
        }
        if (due.Count == 0) return;
        var cfg = _kernel.State.Twin;
        var offers = new Dictionary<string, string>(StringComparer.Ordinal);
        var resend = false;
        foreach (var s in due)
        {
            if (cfg.SendSecrets && !s.SameMachine) SayRefused(s);
            var why = WhyFor(s);
            if (!offers.TryGetValue(why, out var offer)) offers[why] = offer = TwinSecretsOffer.For(_kernel.State, why).ToJson();
            var rides = why == TwinSecrets.Ride;
            if (rides && s.Rides == false) resend = true;
            s.Rides = rides;
            if (string.Equals(offer, s.SecretsOffer, StringComparison.Ordinal)) continue;
            s.SecretsOffer = offer;
            var line = TwinMessage.Format(TwinWord.Secrets, offer);
            _ = Task.Run(() => { if (!s.TryWrite(line)) Drop(s); }, CancellationToken.None);
        }
        if (!resend) return;
        foreach (var section in TwinSync.MirroredSections.Where(TwinSync.CarriesSecrets)) _pendingSections.Add(section);
        ScheduleFlush();
    }

    /// <summary>
    /// Round 89 (UI thread): SEND SECRETS refused for a standby on another machine — said in the log and on the desk once
    /// per standby (its instance) while the setting stays on, so a link that keeps dropping and joining again says it once.
    /// </summary>
    private void SayRefused(Standby s)
    {
        if (!_refusedSaid.Add(s.Instance.Length > 0 ? s.Instance : $"{s.Machine}/{s.Name}")) return;
        var words = TwinSecrets.Refused(s.Name);
        Log.Warn(words);
        _services.Notify(words);
    }

    /// <summary>Round 89: the standbys that said they are without some of the show's secrets, with why (UI thread) — a holder of the show and a follower never.</summary>
    private List<(string Name, TwinSecretsLack Lack, string Why)> StandbysWithout()
    {
        lock (_gate)
        {
            return _standbys.Where(s => !s.IsFollower && !s.HoldsShow && s.Lack is { Any: true }).Select(s => (s.Name, s.Lack!, WhyFor(s))).ToList();
        }
    }

    /// <summary>
    /// The AIR line: this desk's air record as it travels (<see cref="TwinSync.WireAirJson"/>) — its air, a whole show
    /// while EDIT SAFE is open or a look is pinned, by the SHOW line's rule: the machine's own sections never, the boxes'
    /// passwords and the weather key only where the secrets ride (<paramref name="rides"/>).
    /// </summary>
    private string AirLine(bool rides) => TwinMessage.Format(TwinWord.Air, TwinSync.WireAirJson(_air, rides));

    /// <summary>Round 89: this desk's book of kept page addresses moved (any thread): each standby is sent its part on the UI thread.</summary>
    private void OnKeptChanged() => UiThread.Post(() =>
    {
        _lackStale = true;                                                            // a standby's own book moved: what it is without is read again
        SendKept();
    });

    /// <summary>
    /// Round 89 (UI thread): the part of this main's book a standby needs — the passes of the private pages the show it
    /// is sent and the air record name as <c>kept://…</c>, never the rest of the book — to each standby that has not had
    /// that part as it stands, and only where the show's secrets ride (<see cref="TwinSecrets.Rides"/>: a standby on this
    /// machine, SEND SECRETS on). Never to a caller or a stage timer (they never take the show), nor to a standby that
    /// holds the show: its air names its own book's passes, and it is sent this part once TAKE BACK makes it a standby
    /// again. Called when the book moves and on each beat, so a show that came to name a kept page, a standby that
    /// joined while the book moved or a setting turned on has it within the second.
    /// </summary>
    private void SendKept()
    {
        if (_role != TwinRole.Main || !_kernel.State.Twin.SendSecrets) return;
        List<Standby> due;
        lock (_gate)
        {
            due = _standbys.Where(s => !s.IsFollower && !s.HoldsShow).ToList();
        }
        due.RemoveAll(s => !RidesFor(s));
        if (due.Count == 0) return;
        var cut = KeptCut();
        due.RemoveAll(s => ReferenceEquals(s.KeptSent, cut));
        if (due.Count == 0) return;
        foreach (var s in due) s.KeptSent = cut;
        if (cut.Count == 0) return;                                                   // nothing the show names is kept: nothing to carry
        var line = KeptLine(cut);
        _ = Task.Run(() => { foreach (var s in due) if (!s.TryWrite(line)) Drop(s); }, CancellationToken.None);
    }

    /// <summary>
    /// The part of the book a standby is sent (UI thread): cut again when the book moved, or when the show or the air
    /// record moved since the last cut — so the show is read for its kept ids at most once a beat while it is edited,
    /// and only while a standby could be sent them. The same object while the part is the same: a standby that has it
    /// is known by reference.
    /// </summary>
    private WebPassBook KeptCut()
    {
        var book = _kernel.WebPasses.Book;
        if (ReferenceEquals(book, _keptCutFrom) && !_keptStale) return _keptCut;
        var cut = book.Count == 0 ? WebPassBook.Empty : book.Only(NamedKeptIds());
        _keptCutFrom = book;
        _keptStale = false;
        if (!cut.SameAs(_keptCut)) _keptCut = cut;
        return _keptCut;
    }

    /// <summary>The kept ids the show this desk sends and its air record name — what a peer sent them needs the passes of.</summary>
    private HashSet<string> NamedKeptIds()
    {
        var ids = new HashSet<string>(WebPassMigration.KeptIdsIn(_kernel.State), StringComparer.Ordinal);
        if (_air is { } record) ids.UnionWith(WebPassMigration.KeptIdsInText(JsonUtil.SerializeCompact(record)));
        return ids;
    }

    /// <summary>The kept ids the show and the air a standby that holds the show sent name — the passes this desk keeps for TAKE BACK.</summary>
    private HashSet<string> HeldKeptIds()
    {
        var ids = new HashSet<string>(WebPassMigration.KeptIdsInText(_heldShowJson), StringComparer.Ordinal);
        if (_heldAir is { } record) ids.UnionWith(WebPassMigration.KeptIdsInText(JsonUtil.SerializeCompact(record)));
        return ids;
    }

    /// <summary>The KEPT line: a book on one line (its file's JSON, never indented).</summary>
    private static string KeptLine(WebPassBook book) => TwinMessage.Format(TwinWord.Kept, book.ToJson(indented: false));

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
            standby = new Standby(client, stream, join.Name.Length > 0 ? join.Name : join.Machine, Clock())
            {
                HoldsShow = join.TookOver && !join.IsFollower && takenBack is null, IsCaller = join.IsCaller, IsFollower = join.IsFollower, Instance = join.Instance, Machine = join.Machine, Handover = join.Handover,
                SameMachine = SameMachineOf(client.Client.RemoteEndPoint, client.Client.LocalEndPoint),   // round 89: what the socket says, never what the join claims
            };
            if (takenBack is { } stale) Log.Warn($"Twin: the standby {standby.Name} claimed the show under a takeover this desk took back at {stale.AtUtc.ToLocalTime():HH:mm:ss} — told the hand-back again.");
            // The welcome and the whole show, read on the UI thread — the show is its own. A standby
            // that ran the show while this desk was away gets the welcome and nothing to mirror: its
            // show is the newer one, and this desk holds its outputs until TAKE BACK.
            var (welcome, show, air, kept, secrets) = await UiThread.InvokeAsync(() =>
            {
                var w = new TwinWelcome(Name, Environment.MachineName, Instance, Environment.ProcessId, ProcessStartTicks(), Environment.ProcessPath ?? "", _kernel.State.Name);
                if (standby.HoldsShow && _switchByHand is { } byHand && byHand.Instance == standby.Instance) _holderLinked = true;   // back on the link mid hand-back: this desk's picture is up on purpose, no hold
                else if (standby.HoldsShow) Hold(standby.Name, null, linked: true);
                // A standby that stands by again — no claim — while its answer to the hand-back is awaited: that is the answer.
                if (!join.TookOver && !standby.IsFollower && _releaseWait is { } wait && wait.Instance == standby.Instance) CompleteRelease(wait, "it stands by again");
                // Round 89: the show's secrets ride the welcome only where they ride the link (TwinSecrets.Rides: a standby
                // on this machine, SEND SECRETS on) — the passes of the pages the show names before the show, the boxes'
                // passwords and the weather key in it. A standby that follows is told first, in the SECRETS word, whether
                // they ride and what it is not sent; a follower is told nothing and sent none; a standby that holds the
                // show is sent no show and no passes (SendKept).
                var rides = RidesFor(standby);
                var keptLine = "";
                var secretsLine = "";
                if (!standby.IsFollower && !standby.HoldsShow)
                {
                    if (rides)
                    {
                        var cut = KeptCut();
                        standby.KeptSent = cut;
                        if (cut.Count > 0) keptLine = KeptLine(cut);
                    }
                    if (_kernel.State.Twin.SendSecrets && !standby.SameMachine) SayRefused(standby);
                    else if (!_kernel.State.Twin.SendSecrets) _refusedSaid.Clear();
                    standby.SecretsOffer = TwinSecretsOffer.For(_kernel.State, WhyFor(standby)).ToJson();
                    standby.Rides = rides;
                    secretsLine = TwinMessage.Format(TwinWord.Secrets, standby.SecretsOffer);
                }
                return (w.ToJson(), standby.HoldsShow ? "" : TwinSync.WireJson(_kernel.State, rides), AirLine(rides), keptLine, secretsLine);
            });
            var welcomed = standby.TryWrite(TwinMessage.Format(TwinWord.Welcome, welcome))
                           && (takenBack is null || standby.TryWrite(TwinMessage.Format(TwinWord.HandBack, takenBack.Value.TakeBack)))
                           && (kept.Length == 0 || standby.TryWrite(kept))
                           && (secrets.Length == 0 || standby.TryWrite(secrets))
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
                else if (msg.Word == TwinWord.Lacks)
                {
                    // Round 89: what the standby says it is without — counts only; the main's line, STATE and the Super Check say it too.
                    if (TwinSecretsLack.Parse(msg.Payload) is { } lack) standby.Lack = lack;
                }
                else if (standby.HoldsShow && msg.Word == TwinWord.Kept)
                {
                    // Round 89: the passes of the pages the show the standby has names, sent after that show — a page pasted
                    // there opens here after TAKE BACK. This desk's own entries stand and the standby's fill the room; the
                    // ones its show and air name count as named, so a full book here makes room from passes no show names.
                    if (WebPassBook.TryParse(msg.Payload, out var book)) await UiThread.InvokeAsync(() => _kernel.WebPasses.MergeFromTwin(book, theirsStand: false, standby.Name, HeldKeptIds()));
                }
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

        /// <summary>Round 89: the book of kept page addresses this standby was last sent (UI thread); null until it was sent one.</summary>
        public WebPassBook? KeptSent { get; set; }

        /// <summary>Round 89: both ends of its connection are this machine — the socket's addresses, never the join's word. The show's secrets ride only such a link.</summary>
        public bool SameMachine { get; init; }

        /// <summary>Round 89: the SECRETS word it was last sent (UI thread); null before one.</summary>
        public string? SecretsOffer { get; set; }

        /// <summary>Round 89: whether the secrets rode its link when it was last told (UI thread) — a change to riding sends the sections that carry them.</summary>
        public bool? Rides { get; set; }

        private TwinSecretsLack? _lack;

        /// <summary>Round 89: what it last said it is without (its LACKS word) — set on its read loop, read on the UI thread; null before it said.</summary>
        public TwinSecretsLack? Lack
        {
            get => Volatile.Read(ref _lack);
            set => Volatile.Write(ref _lack, value);
        }

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
