using System.Globalization;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering.Media;

namespace Patterns.App.Services;

/// <summary>
/// The God's Eye on the desk (round 66): the facts gathered from every service once a second and
/// hashed, the graph rebuilt only when they moved, and the operator's view — the focus and the
/// lens — moved by the page, the keys and the wire through the action layer. Nothing here probes
/// a machine or a network: it reads what the services already know.
/// </summary>
public sealed class EyeService
{
    private readonly AppServices _s;
    private string _hash = "";

    public EyeService(AppServices services) => _s = services;

    public EyeGraph Graph { get; private set; } = EyeGraph.Empty;

    public EyePlacement Placement { get; private set; } = EyePlacement.Empty;

    /// <summary>Moves when the picture or the view changed: the page and the canvas read it, the rail's words follow it.</summary>
    public long Rev { get; private set; }

    /// <summary>The thing the eye is on, or null for the whole picture.</summary>
    public string? FocusId { get; private set; }

    public EyeLens Lens { get; private set; } = EyeLens.All;

    /// <summary>The picture or the view moved — on the UI thread, where every verb runs.</summary>
    public event Action? Changed;

    /// <summary>The rail's word: the worst light's count, or ALL GREEN.</summary>
    public string RailWord => Graph.Nodes.Count == 0 ? "—" : Graph.Red > 0 ? $"{Graph.Red} RED" : Graph.Amber > 0 ? $"{Graph.Amber} AMBER" : "ALL GREEN";

    /// <summary>The rail's colour: the worst light in the picture.</summary>
    public string RailHue => Graph.Nodes.Count == 0 ? "#4A505E" : Hue(Graph.WorstLight);

    public string RailLine => Graph.Headline;

    public static string Hue(CheckLight light) => light switch
    {
        CheckLight.Red => CompanionPalette.Hex("red"),
        CheckLight.Amber => CompanionPalette.Hex("amber"),
        CheckLight.Green => CompanionPalette.Hex("brightGreen"),
        _ => "#4A505E",
    };

    /// <summary>Reads the desk and rebuilds the picture when the facts moved: true when it did.</summary>
    public bool Refresh()
    {
        EyeFacts facts;
        try
        {
            facts = Gather();
        }
        catch (Exception ex)
        {
            Log.Warn("The Eye could not read the desk.", ex);
            return false;
        }
        var hash = JsonUtil.SerializeCompact(facts);
        if (hash == _hash) return false;
        _hash = hash;
        Graph = EyeGraph.Build(facts);
        Placement = EyeLayout.Place(Graph);
        if (FocusId is not null && Graph.Find(FocusId) is null) FocusId = null;           // the focused thing left the picture
        if (_record is not null && Moment is { } moment) _replayed = EyeReplay.Apply(Graph, moment);   // round 84: the structure of now, the lights of then
        Moved();
        return true;
    }

    /// <summary>The picture read afresh when it was never read — a wire query before the first tick.</summary>
    private void EnsureRead()
    {
        if (Graph.Nodes.Count == 0) Refresh();
    }

    public ActionResult Focus(string words)
    {
        EnsureRead();
        var id = Shown.Resolve(words);
        if (id is null) return ActionResult.Refused($"The Eye has nothing called '{words.Trim()}'.");
        FocusId = id;
        Moved();
        var n = Shown.Find(id)!;
        return ActionResult.Done($"Eye on {n.Label} — {n.Sub}.");
    }

    public ActionResult Next()
    {
        EnsureRead();
        var id = Shown.Next(FocusId);
        if (id is null) return ActionResult.Done("Nothing red or amber in the picture.");
        FocusId = id;
        Moved();
        var n = Shown.Find(id)!;
        return ActionResult.Done($"Problem {Shown.ProblemIndex(id) + 1} of {Shown.Problems.Count}: {n.Label} — {n.Sub}.");
    }

    public ActionResult Prev()
    {
        EnsureRead();
        var id = Shown.Prev(FocusId);
        if (id is null) return ActionResult.Done("Nothing red or amber in the picture.");
        FocusId = id;
        Moved();
        var n = Shown.Find(id)!;
        return ActionResult.Done($"Problem {Shown.ProblemIndex(id) + 1} of {Shown.Problems.Count}: {n.Label} — {n.Sub}.");
    }

    public ActionResult SetLens(string word)
    {
        var lens = EyeGraph.ParseLens(word);
        if (lens is null) return ActionResult.Refused($"No lens called '{word.Trim()}' — all, video, control, audio, room or problems.");
        Lens = lens.Value;
        Moved();
        return ActionResult.Done($"Eye lens: {Lens.ToString().ToLowerInvariant()}.");
    }

    public ActionResult Reset()
    {
        FocusId = null;
        Lens = EyeLens.All;
        Moved();
        return ActionResult.Done("The whole picture.");
    }

    /// <summary>EYE / EYE STATUS: the picture as JSON.</summary>
    public string Json()
    {
        EnsureRead();
        return EyeJson.Write(Graph, Placement, FocusId, Lens);
    }

    /// <summary>The STATE row: the headline, the worst light, the counts, the view.</summary>
    public object Row() => new
    {
        headline = Graph.Headline,
        worst = Graph.Worst?.Id ?? "",
        worstLight = EyeGraph.Light(Graph.WorstLight),
        worstWords = Graph.Worst is { } w ? $"{w.Label}: {w.Sub}" : "",
        red = Graph.Red,
        amber = Graph.Amber,
        green = Graph.Green,
        grey = Graph.Grey,
        things = Graph.Nodes.Count,
        problems = Graph.Problems.Count,
        focus = FocusId ?? "",
        lens = Lens.ToString().ToLowerInvariant(),
        replay = Replaying,                                                                 // round 84: the page shows the record, not now
        replayAt = Replaying ? ReplayTime.Stamp(ReplayAtUtc) : "",
    };

    /// <summary>The picture in words for the assistant's brief — the picture of now, with one line first while the page replays the record, so the assistant never mistakes then for now.</summary>
    public IReadOnlyList<string> BriefLines()
    {
        EnsureRead();
        if (Moment is { } m)
        {
            var lines = new List<string>(Graph.Nodes.Count + Graph.Edges.Count + 2) { $"The Eye page is replaying the record at {ReplayTime.Stamp(ReplayAtUtc)} — {EyeReplay.Words(m)}. The lines below are the picture of now." };
            lines.AddRange(Graph.Lines());
            return lines;
        }
        return Graph.Lines();
    }

    // ---- the replay (round 84; the record read off the desk's thread, round 85) ----------------

    /// <summary>The journal rows a replay reads back — the newest; a four-megabyte journal holds fewer.</summary>
    private const int JournalRows = 20000;

    /// <summary>The metrics lines read per file — a fence over a file the writer rotates at a megabyte, never reached by a healthy one.</summary>
    private const int MetricsLines = 60000;

    private ReplayRecord? _record;
    private EyeGraph? _replayed;
    private TaskCompletionSource? _read;
    private int _generation;
    private DateTime? _openAt;

    /// <summary>True while the page shows the record instead of the picture of now — from the press, through the read, until NOW.</summary>
    public bool Replaying => _record is not null || _read is not null;

    /// <summary>True while the record is being read on a worker; the strip says so and the scrub bar waits.</summary>
    public bool Reading => _read is not null;

    /// <summary>The read in flight — the tests and the wire await it; null when none.</summary>
    public Task? ReplayRead => _read?.Task;

    /// <summary>The record the replay reads; empty while the replay is closed or the record is still being read.</summary>
    public ReplayRecord Record => _record ?? ReplayRecord.Empty;

    /// <summary>The instant the replay stands at (UTC) — the record's last stamp when it opens with ON.</summary>
    public DateTime ReplayAtUtc { get; private set; }

    /// <summary>The moment shown; null while the replay is closed or the record is still being read.</summary>
    public ReplayMoment? Moment { get; private set; }

    /// <summary>
    /// What the page, its side card and the operator's eye verbs read: the replayed moment while the replay is
    /// open, else the picture of now. The rail, STATE's counts, EYE on the wire and the assistant's facts read
    /// <see cref="Graph"/>: the replay is the operator's reading, never the room's truth.
    /// </summary>
    public EyeGraph Shown => _replayed ?? Graph;

    /// <summary>EYE REPLAY [ON | OFF | &lt;time&gt;]: the record opened at its last stamp or at a time, or closed.</summary>
    public ActionResult Replay(string words)
    {
        var w = (words ?? "").Trim();
        if (w.Length == 0 || w.Equals("ON", StringComparison.OrdinalIgnoreCase)) return Open(null);
        if (w.Equals("OFF", StringComparison.OrdinalIgnoreCase) || w.Equals("NOW", StringComparison.OrdinalIgnoreCase) || w.Equals("LIVE", StringComparison.OrdinalIgnoreCase)) return Close();
        if (!ReplayTime.TryParse(w, DateTime.UtcNow, out var at)) return ActionResult.Refused(NotATime(w));
        return Open(at);
    }

    /// <summary>
    /// Opens the replay. A record already read is shown at once; otherwise the files are read on a worker — the
    /// desk's thread never parses a journal — and the picture relights when the read lands, at the time asked for
    /// or at the record's last stamp. The record is the files as they stood at the press: a row or a sample stamped
    /// after it — the press's own journal row among them — is not in it, so ON opens on what happened before the
    /// press, never on the press. A desk with neither file is refused with the reason before any read.
    /// </summary>
    private ActionResult Open(DateTime? atUtc)
    {
        EnsureRead();
        if (_record is { } record)
        {
            Show(atUtc ?? record.LastUtc ?? DateTime.UtcNow);
            return ActionResult.Done("Replay " + EyeReplay.Words(Moment!));
        }
        if (!File.Exists(_s.Journal.Path) && !File.Exists(MetricsPath("")) && !File.Exists(MetricsPath(".old")))
        {
            return ActionResult.Refused("Nothing to replay — the journal and the metrics file hold no rows yet.");
        }
        _openAt = atUtc;
        if (_read is not null) return ActionResult.Requested("Reading the record — the picture relights when it is read.");
        var generation = ++_generation;
        var pressedUtc = DateTime.UtcNow;
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _read = read;
        Moved();
        _ = ReadAsync(read, generation, pressedUtc);
        return ActionResult.Requested("Reading the record on a worker — the picture relights when it is read.");
    }

    private async Task ReadAsync(TaskCompletionSource read, int generation, DateTime untilUtc)
    {
        ReplayRecord? record = null;
        try
        {
            record = await Task.Run(() => Load(generation, untilUtc));
        }
        catch (OperationCanceledException)
        {
            // Closed, or opened again, before the read ended: this read's record is nobody's.
        }
        catch (Exception ex)
        {
            Log.Warn("The replay could not read the record.", ex);
        }
        try
        {
            if (!ReferenceEquals(_read, read)) return;                              // a close or a later open outran this read
            _read = null;
            if (record is null)
            {
                Moved();
                return;
            }
            _record = record;
            Show(_openAt ?? record.LastUtc ?? DateTime.UtcNow);
        }
        finally
        {
            read.TrySetResult();
        }
    }

    private ActionResult Close()
    {
        if (_record is null && _read is null) return ActionResult.Done("The picture of now — the replay was not open.");
        _generation++;                                                              // a read in flight sees it and stops
        _read = null;
        _record = null;
        _replayed = null;
        Moment = null;
        _openAt = null;
        Moved();
        return ActionResult.Done("The picture of now.");
    }

    /// <summary>The scrub bar: the replay moved to a position of the record (0 its first stamp, 1 its last). Not a verb — a drag is the operator's hand on the view, like a pan.</summary>
    public void Scrub(double position)
    {
        if (_record is null) return;
        Show(_record.AtPosition(position, ReplayAtUtc));
    }

    /// <summary>The replay stepped by a span, held inside the record.</summary>
    public void Step(TimeSpan by)
    {
        if (_record is null) return;
        Show(_record.Clamp(ReplayAtUtc + by));
    }

    /// <summary>
    /// EYE AT &lt;time&gt;: the picture as the record has it at that instant, as one wire reply — the operator's view
    /// untouched. The replay's record when it is open, the read in flight when there is one, else a read of its own
    /// on a worker; the desk's thread parses nothing either way.
    /// </summary>
    public async Task<string> JsonAtAsync(string words)
    {
        if (!ReplayTime.TryParse(words, DateTime.UtcNow, out var at)) return ControlProtocol.Err(NotATime((words ?? "").Trim()));
        EnsureRead();
        var record = _record;
        if (record is null && _read is { } pending)
        {
            await pending.Task;
            record = _record;
        }
        record ??= await Task.Run(() => Load(-1, DateTime.UtcNow));
        var moment = EyeReplay.At(record, at);
        return ControlProtocol.Ok(EyeJson.Write(EyeReplay.Apply(Graph, moment), Placement, FocusId, Lens, moment));
    }

    private void Show(DateTime atUtc)
    {
        ReplayAtUtc = atUtc;
        Moment = EyeReplay.At(_record!, atUtc);
        _replayed = EyeReplay.Apply(Graph, Moment);
        Moved();
    }

    /// <summary>
    /// The record from the desk's own files: the journal's newest rows and the metrics file with the one it rotated
    /// out before it, both bounded, and nothing stamped at or after <paramref name="untilUtc"/> — the instant of the
    /// press or the ask, so the record is the files as they stood then. Runs on a worker; <paramref name="generation"/>
    /// is the open it serves, and a close or a later open moves the generation so the read stops between lines (−1
    /// reads to the end, for a query).
    /// </summary>
    private ReplayRecord Load(int generation, DateTime untilUtc)
    {
        var rows = _s.Journal.Tail(JournalRows).Where(r => r.AtUtc < untilUtc).ToList();
        Stale(generation);
        var samples = new List<MetricSample>();
        foreach (var suffix in new[] { ".old", "" })
        {
            var path = MetricsPath(suffix);
            try
            {
                if (File.Exists(path)) samples.AddRange(MetricsCsv.Parse(Bounded(File.ReadLines(path), generation)).Where(sample => sample.Utc < untilUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"The replay could not read patterns.metrics.csv{suffix}.", ex);
            }
        }
        Stale(generation);
        return ReplayRecord.From(rows, samples);
    }

    private IEnumerable<string> Bounded(IEnumerable<string> lines, int generation)
    {
        var n = 0;
        foreach (var line in lines)
        {
            if (++n > MetricsLines) yield break;
            if ((n & 1023) == 0) Stale(generation);
            yield return line;
        }
    }

    /// <summary>Stops a read whose open has been closed or replaced since; a query's read (−1) is never stopped.</summary>
    private void Stale(int generation)
    {
        if (generation >= 0 && generation != Volatile.Read(ref _generation)) throw new OperationCanceledException("the replay closed before the record was read");
    }

    private string MetricsPath(string suffix) => Path.Combine(_s.Store.BaseDirectory, "patterns.metrics.csv" + suffix);

    private static string NotATime(string words) => $"'{words}' is not a time — ON, OFF, a clock time (20:14 or 20:14:03) or an ISO 8601 stamp (2026-09-24T20:14:03Z).";

    private void Moved()
    {
        Rev++;
        Changed?.Invoke();
    }

    // ---- the facts ------------------------------------------------------------------------

    private EyeFacts Gather()
    {
        var state = _s.State;
        var now = DateTime.UtcNow;
        var all = _s.Screens.All;
        var report = _s.Metrics.LastReport;
        var (_, attention) = _s.DeskHealthWords();

        var displays = all.Where(i => !i.IsVirtual)
            .Select(i => new EyeDisplay(i.Id, i.Label, i.Bounds.Width, i.Bounds.Height, i.Hz, i.IsPrimary, i.IsPlanned, i.IsMissing)).ToList();

        var screens = new List<EyeScreen>();
        var clock = FrameBudgets.ClockHz(FrameBudgets.Readings(ShowClock.Seconds));
        var air = _s.AirState;
        var geo = Rig.Geometry(state, all);
        var liveTargets = new HashSet<string>(_s.Outputs.Windows.Select(w => w.TargetScreenId), StringComparer.Ordinal);
        var ticked = new HashSet<string>(_s.TickedTargets?.Invoke() ?? Array.Empty<string>(), StringComparer.Ordinal);
        var n = 0;
        foreach (var (placement, info) in Rig.OrderedLivePlacements(state, all))
        {
            n++;
            if (info is { IsVirtual: true }) continue;
            var signal = _s.Actions.SignalReportFor(placement, info, clock);
            var received = signal.Lines.Where(l => l.Item.StartsWith("Received", StringComparison.Ordinal)).ToList();
            var receivedLight = received.Count == 0 ? CheckLight.Grey
                : received.Any(l => l.Light == CheckLight.Red) ? CheckLight.Red
                : received.All(l => l.Light == CheckLight.Green) ? CheckLight.Green : CheckLight.Amber;
            var target = geo.TargetOf(placement.ScreenId);
            var own = ContentTargets.UsesOwnPattern(air, target) ? air.Independent.FirstOrDefault(a => a.ScreenId == target)?.Pattern : null;
            var picture = own ?? air.Pattern;
            var hasContract = placement.Signal.IsSet || placement.TestRoute;
            var worst = signal.Lines.FirstOrDefault(l => l.Light == CheckLight.Red) ?? signal.Lines.FirstOrDefault(l => l.Light == CheckLight.Amber);
            screens.Add(new EyeScreen
            {
                Id = placement.ScreenId,
                Number = n.ToString(CultureInfo.InvariantCulture),
                Label = Rig.LabelFor(placement, info),
                DisplayId = info?.Id ?? "",
                Enabled = placement.Enabled,
                OnAir = liveTargets.Contains(placement.ScreenId) || liveTargets.Contains(target),
                Own = own is not null,
                Staged = _s.Sandbox.IsStaged(target),                                              // round 78: a picture the audience has not seen — staged or edited
                Contract = placement.Signal.IsSet ? SignalTruth.DesignWords(placement.Signal) : "",
                Verdict = hasContract ? signal.Result : "",
                SignalWords = hasContract ? (worst is null ? signal.Result + " · " + signal.Design : $"{signal.Result} — {worst.Item}: {worst.Value}") : "",
                TestRoute = placement.TestRoute,
                Received = placement.Received.IsSet ? SignalTruth.DesignWords(placement.Received) : "",
                ReceivedLight = receivedLight,
                Sources = SourcesOf(picture),
                Adjustments = picture is { Kind: PatternKind.Media } ? picture.Media.AdjustmentWords() : "",   // round 77
                Role = placement.Role.ToString(),
                // Round 67.8: the tile's switches and the canvas, read from the same facts the wall and the take plan read.
                Locked = ScreenRoles.IsLocked(state, target),
                Armed = _s.Arming.IsArmed(target),
                Ticked = ticked.Contains(target),
                Canvas = ContentTargets.IsCanvasKey(target) ? geo.LabelFor(state, target) : "",
            });
        }

        var sources = new List<EyeSource>();
        foreach (var key in InputBus.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var src = InputBus.For(key);
            var colon = key.IndexOf(':');
            var kind = colon > 0 ? key[..colon] : "";
            var rest = colon > 0 ? key[(colon + 1)..] : key;
            var label = state.InputLabel(key, "");
            if (label.Length == 0) label = kind is "vid" or "deck" ? Path.GetFileName(rest) : rest;
            var light = src is null ? CheckLight.Grey : src.FrameSize is null ? CheckLight.Amber : CheckLight.Green;
            var status = src?.StatusText ?? "";
            if (src is not null && src.FrameSize is null && status.Length == 0) status = "no frame yet";
            sources.Add(new EyeSource(key, label, KindWord(kind), src is not null, status, light, EyeGraph.SourcePage(kind), _s.Residency.ReasonWords(key)));   // round 69: why it is in memory
        }

        var devices = state.Interactive.Devices.Select(d =>
        {
            var open = _s.Devices.LinkFor(d.Id)?.IsOpen ?? false;
            var light = !d.Enabled ? CheckLight.Grey : d.Runtime.Failing ? CheckLight.Red : open ? CheckLight.Green : CheckLight.Red;
            return new EyeDevice(d.Id, d.Name, d.Profile.ToString(), DeviceAddress.Describe(d), d.Enabled, open, d.Status, light, d.Runtime.Words(now), d.InputScreen, Mapped: MidiBindings.Count(d));
        }).ToList();

        var token = state.Control.Token;
        var decks = _s.Control.Decks.Select(d => new EyeDeck(d.Name, d.Module, d.Address, d.Paired || !PairingToken.Needed(token)) { Where = d.Where, Recording = d.Recording }).ToList();   // round 74: where each deck's navigator is
        var companions = _s.Kernel.Mdns.Companions
            .Select(c => new EyeCompanion(c.Host.Length > 0 ? c.Host : c.Instance, c.Address?.ToString() ?? "", !c.Expired(now), c.Txt.TryGetValue("version", out var v) ? v : ""))
            .ToList();

        EyeTwin? twin = null;
        if (_s.Twin.Role != TwinRole.Off)
        {
            var isMain = _s.Twin.Role == TwinRole.Main;
            var other = isMain ? (_s.Twin.StandbyNames is { Count: > 0 } standbys ? standbys[0] : "") : _s.Twin.MainName;
            var phase = _s.Twin.Phase;
            var light = isMain
                ? (_s.Twin.StandbyNames.Count > 0 ? CheckLight.Green : CheckLight.Grey)
                : phase switch
                {
                    TwinPhase.InStep => CheckLight.Green,
                    TwinPhase.Connecting or TwinPhase.TookOver => CheckLight.Amber,
                    TwinPhase.MainSilent or TwinPhase.Refused => CheckLight.Red,
                    _ => CheckLight.Grey,
                };
            twin = new EyeTwin(isMain ? "main" : "standby", phase.ToString(), other, _s.Twin.GlanceWords, light);
        }

        var nodes = _s.Kernel.Nodes.Nodes
            .Select(c => new EyeNodeHeard(c.Instance, c.Kind.ToString().ToLowerInvariant(), c.Name, c.Fresh, c.Fresh && c.Live, c.Words))
            .ToList();

        EyeRoom? room = null;
        if (_s.Control.AudienceListening)
        {
            var phones = _s.Play.Room.PlayerCount;
            var refused = _s.Play.JoinsRefused(now).Count;
            room = new EyeRoom(_s.Play.Code, phones, refused > 0 ? $"{refused} joins refused" : "", refused > 0 ? CheckLight.Amber : phones > 0 ? CheckLight.Green : CheckLight.Grey);
        }

        EyeStream? stream = null;
        var health = _s.Stream.Health;
        if (health.Light != StreamLight.Off)
        {
            stream = new EyeStream(health.Line.Length > 0 ? health.Line : health.Word, health.Light switch
            {
                StreamLight.Live => CheckLight.Green,
                StreamLight.Failed => CheckLight.Red,
                _ => CheckLight.Amber,
            });
        }

        var ndi = state.Ndi.Senders.Where(s => s.Enabled).Select(s =>
        {
            var sender = _s.Ndi.SenderFor(s.Id);
            return new EyeNdiSend(s.Id, s.Name, sender?.IsRunning ?? false, sender?.Connections ?? 0, _s.Ndi.StatusFor(s.Id));
        }).ToList();

        var audioSources = new List<EyeAudioSource>();
        var audioOuts = new List<EyeAudioOut>();
        var audioRoutes = new List<EyeAudioRoute>();
        if (state.AudioRouting.Enabled)
        {
            var graph = _s.AudioGraph;
            audioSources.AddRange(AudioRouting.Sources(state).Select(s => new EyeAudioSource(s.Id, s.Label, s.Kind.ToString().ToLowerInvariant())));
            // Every destination the plan knows: the rows, and the outputs the picture alone routes to (round 69).
            foreach (var p in AudioRouting.Resolve(state, air, vogPlaying: false))
            {
                var peak = graph?.PeakDb(p.Key) ?? Db.Floor;
                audioOuts.Add(new EyeAudioOut(p.Key, p.Label, p.Kind == AudioDestinationKind.Ndi ? "ndi" : "device", p.Mute, Math.Round(peak / 3) * 3, graph?.LaneError(p.Key) ?? ""));
            }
            audioRoutes.AddRange(AudioRouting.EffectiveRoutes(state, air).Select(r => new EyeAudioRoute(r.Source, r.Destination, r.LevelDb, !r.Enabled, r.Followed)));
        }

        var cues = _s.CueStack;
        var standby = cues.StandbyCue;
        var last = cues.LastCue;
        var runtime = cues.Runtime;
        var stack = new EyeStack(cues.Armed, standby is null ? "" : $"{standby.Number} {standby.Name}".Trim(), standby?.Id ?? "", last is null ? "" : $"{last.Number} {last.Name}".Trim(),
            runtime.Hold, runtime.Executing, "", cues.Armed ? (runtime.Hold ? CheckLight.Amber : CheckLight.Green) : CheckLight.Grey);

        var hasKey = _s.Kernel.Assistant.HasKey;
        // Round 67.8: the wall's take plan — the desk's picker on a desk, every armed screen on a node with no picker.
        var takeScope = _s.TakeScopeWords?.Invoke();
        var plan = takeScope is null ? null : _s.Actions.PlanTake(FadeScope.Parse(takeScope) ?? FadeScope.Everything);

        return new EyeFacts
        {
            MachineName = Environment.MachineName,
            Build = AppVersion.Current,
            OutputsLive = _s.Outputs.IsLive,
            SecondDesk = !_s.IsPrimaryInstance,                                                                // round 78
            Health = report?.Overall ?? CheckLight.Grey,
            HealthWords = report?.Headline ?? "",
            MemoryWords = Residency.CountWords(_s.Residency.Holds) + " · " + GpuGovernor.EyeWords(Patterns.App.Rendering.GpuCacheGovernor.Facts.HasContext, Patterns.App.Rendering.GpuCacheGovernor.Facts.LimitBytes, _s.Metrics.GpuRung),   // round 69: stable words — counts and bounds, not the fill or the countdown
            MasterRate = Rig.MasterRate(state, _s.Screens.All).Words,                                    // round 80: the rate in force, and the display it follows
            Attention = attention,
            NextTake = _s.NextTake.Pending?.Words ?? "",
            TakeScope = plan?.Scope.Label ?? "",
            TakeWords = plan is null ? "" : plan.IsRefused ? plan.Refusal! : plan.Words,
            Landing = _s.Stingers.SessionTicket?.Words ?? "",                                          // round 72: the ticket a sting will land
            LastTake = _s.Actions.LastTake?.Words ?? "",                                                // round 79: the last take as a fact row
            Editing = _s.EditingFacts?.Invoke()?.Words ?? "",                                           // round 73: what the desk's editors are on
            LowerThird = LowerThirdWords(air, now),                                                       // round 73: the name on screen and when it leaves
            Midi = _s.MidiLearn.EyeWords,                                                                // round 73: learn armed, or the surfaces' map
            Nav = _s.Actions.NavWords(),                                                                 // round 74: the page, the column, the selection
            Displays = displays,
            Screens = screens,
            Sources = sources,
            Devices = devices,
            Decks = decks,
            Companions = companions,
            WireClients = Math.Max(0, _s.Control.WireConnections - decks.Count),
            WebClients = _s.Control.HttpConnections,
            Osc = state.Control.OscEnabled ? new EyeOsc(state.Control.OscPort, _s.Osc.StatusLine, state.Control.Bind) : null,   // round 79: the bind decides its light
            Twin = twin,
            Nodes = nodes,
            Room = room,
            Stream = stream,
            NdiSends = ndi,
            AudioSources = audioSources,
            AudioOuts = audioOuts,
            AudioRoutes = audioRoutes,
            Stack = stack,
            Assistant = new EyeAssistant(hasKey, hasKey ? "ready" : ""),
        };
    }

    /// <summary>The input keys a picture draws from: its layers' sources, the arcade's own picture.</summary>
    public static IReadOnlyList<string> SourcesOf(PatternConfig picture)
    {
        var keys = new List<string>();
        void Layer(LayerConfig l)
        {
            if (!l.Enabled) return;
            var key = l.Source switch
            {
                LayerSource.Video => InputKeys.Video(l.VideoPath),
                LayerSource.Capture => InputKeys.Capture(l.CaptureDevice),
                LayerSource.NdiFeed => InputKeys.Ndi(l.NdiSourceName),
                LayerSource.Web => InputKeys.Web(l.WebUrl),
                LayerSource.Arcade => InputKeys.Arcade(),
                _ => "",
            };
            if (key.Length > 0 && !keys.Contains(key)) keys.Add(key);
        }
        Layer(picture.Layer1);
        Layer(picture.Layer2);
        if (picture.Kind == PatternKind.Media && picture.Media.DeckPath.Length > 0)
        {
            var deck = InputKeys.Deck(picture.Media.DeckPath);
            if (deck.Length > 0 && !keys.Contains(deck)) keys.Add(deck);
        }
        return keys;
    }

    private static string KindWord(string kind) => kind switch
    {
        "ndi" => "NDI feed",
        "cap" => "capture",
        "web" => "web page",
        "deck" => "deck",
        "vid" => "clip",
        "arcade" => "arcade",
        _ => "source",
    };

    /// <summary>Round 73: "'Keynote' — Jane Doe · leaves in 3 s", "'Keynote' · until hidden", or "" with nothing on screen.</summary>
    internal static string LowerThirdWords(ShowState air, DateTime nowUtc)
    {
        var cfg = air.LowerThirds;
        if (cfg.Active is not { } design || !Patterns.Core.LowerThirds.LowerThirdClock.IsLive(cfg, nowUtc)) return "";
        var who = design.PersonName.Length > 0 ? $" — {design.PersonName}" : "";
        var leaves = Patterns.Core.LowerThirds.LowerThirdClock.LeavesIn(cfg, nowUtc);
        var when = leaves is { } s ? $"leaves in {Math.Ceiling(s).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} s"
            : cfg.HiddenAtUtc is not null ? "leaving"
            : Patterns.Core.LowerThirds.LowerThirdClock.HoldMsOf(design, cfg.RunHoldMs) > 0 ? "leaving" : "until hidden";
        return $"'{design.Name}'{who} · {when}";
    }
}
