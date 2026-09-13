using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Patterns.Core.Arcade;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// The arcade node's loop: the engine advanced by wall time and rendered on a thread of its own at
/// the picture's rate, triple-buffered so the page draws the newest whole frame while the next is
/// drawn; the pads (keyboard, the wire, the phone pad, XInput) merged at the step; the picture to
/// NDI when asked; the board in the node's folder. On a desk the verbs go to the arcade nodes it
/// hears (<see cref="NodesService.SendToArcades"/>); this runs them when this process is the arcade.
/// </summary>
public sealed class ArcadeService : IDisposable
{
    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 720;
    public const int DefaultFps = 60;
    private const int Buffers = 3;
    private const int TapSteps = ArcadeEngine.StepHz / 10;

    private readonly AppServices _s;
    private readonly ArcadeEngine _engine = new();
    private readonly object _gate = new();
    private readonly object _pick = new();
    private readonly PaintCache _paints = new();
    private readonly PadButtons[] _pressed = new PadButtons[ArcadeEngine.MaxPads];
    private readonly PadButtons[] _pads = new PadButtons[ArcadeEngine.MaxPads];
    private readonly List<(int Player, PadButtons Button, long ReleaseAt)> _taps = new();
    private readonly SKBitmap?[] _bitmaps = new SKBitmap?[Buffers];
    private readonly SKSurface?[] _surfaces = new SKSurface?[Buffers];
    private readonly FpsMeter _fpsMeter = new();
    private readonly Leaderboard _board;
    private readonly string _boardPath;
    private Thread? _thread;
    private volatile bool _run;
    private volatile int _width = DefaultWidth;
    private volatile int _height = DefaultHeight;
    private volatile int _fps = DefaultFps;
    private int _latest = -1;
    private int _drawing = -1;
    private NdiFrameSender? _ndi;
    private volatile bool _ndiOn;
    private long _rev;
    private long _frames;
    private bool _boardDirty;

    public ArcadeService(AppServices s)
    {
        _s = s;
        _boardPath = Path.Combine(s.Store.BaseDirectory, "arcade-scores.json");
        _board = Leaderboard.Parse(TryRead(_boardPath));
        _engine.TopScores = game => _board.Top(game, 5);
        _engine.MatchEnded += OnMatchEnded;
        _engine.Changed += () => Interlocked.Increment(ref _rev);
    }

    public static bool IsArcadeKind(ShowActionKind kind) => kind is ShowActionKind.ArcadeStart or ShowActionKind.ArcadeStop or ShowActionKind.ArcadePause
        or ShowActionKind.ArcadeResume or ShowActionKind.ArcadeAttract or ShowActionKind.ArcadeKey or ShowActionKind.ArcadeSize or ShowActionKind.ArcadeNdi or ShowActionKind.ArcadeName;

    /// <summary>The wire's line for an arcade action — what the desk sends its arcade nodes.</summary>
    public static string Line(ShowAction a) => a.Kind switch
    {
        ShowActionKind.ArcadeStart => $"ARCADE START {a.Value}".TrimEnd(),
        ShowActionKind.ArcadeStop => "ARCADE STOP",
        ShowActionKind.ArcadePause => "ARCADE PAUSE",
        ShowActionKind.ArcadeResume => "ARCADE RESUME",
        ShowActionKind.ArcadeAttract => $"ARCADE ATTRACT {a.Value}".TrimEnd(),
        ShowActionKind.ArcadeKey => $"ARCADE KEY {a.Value}",
        ShowActionKind.ArcadeSize => $"ARCADE SIZE {a.Value}",
        ShowActionKind.ArcadeNdi => $"ARCADE NDI {a.Value}",
        ShowActionKind.ArcadeName => $"ARCADE NAME {a.Value}",
        _ => "",
    };

    public bool IsRunning => _run;
    public int Width => _width;
    public int Height => _height;
    public int Fps => _fps;
    public bool NdiOn => _ndiOn;
    public long Rev => Interlocked.Read(ref _rev);
    public long Frames => Interlocked.Read(ref _frames);
    public string NdiName => $"PATTERNS ARCADE ({Environment.MachineName})";

    /// <summary>Another picture on this lane — the audience wall — drawn instead of the game while it says so (true).</summary>
    public Func<SKCanvas, int, int, PaintCache, bool>? Board { get; set; }
    public double MeasuredFps => _fpsMeter.Fps;

    public string Words
    {
        get { lock (_gate) return _engine.Words; }
    }

    public ArcadePhase Phase
    {
        get { lock (_gate) return _engine.Phase; }
    }

    public ArcadeSnapshot Snapshot()
    {
        lock (_gate) return _engine.Snapshot();
    }

    /// <summary>"60 fps · 1280×720 · NDI off" — the page's line under the picture.</summary>
    public string Status
        => $"{(_run ? $"{_fpsMeter.Fps:0} fps" : "not running")} · {_width}×{_height} · NDI {(_ndiOn ? (_ndi?.Status ?? "starting") : "off")}";

    /// <summary>The board's best five for the game in hand: "PONG — 1 ABC 7 · 2 P1 5".</summary>
    public string BoardWords
    {
        get
        {
            lock (_gate)
            {
                var game = _engine.Game?.Id ?? ArcadeEngine.Catalogue[0].Id;
                var top = _board.Top(game, 5);
                var title = ArcadeEngine.Find(game)?.Title ?? game.ToUpperInvariant();
                if (top.Count == 0) return $"{title} — no scores yet.";
                return $"{title} — " + string.Join(" · ", top.Select((e, i) => $"{i + 1} {e.Name} {e.Score}"));
            }
        }
    }

    /// <summary>The loop: from boot on an arcade node, from the first verb that needs a picture elsewhere.</summary>
    public void Start()
    {
        if (_run) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "arcade", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        var t = _thread;
        _thread = null;
        if (t is not null && t.IsAlive && !t.Join(2000)) Log.Warn("The arcade loop did not stop in time.");
    }

    /// <summary>A key from the keyboard, the wire or the phone pad — held until released.</summary>
    public void Key(int player, PadButtons button, bool down)
    {
        if (player < 0 || player >= ArcadeEngine.MaxPads || button == PadButtons.None) return;
        lock (_gate)
        {
            if (down) _pressed[player] |= button; else _pressed[player] &= ~button;
        }
    }

    /// <summary>What a pad holds from the keyboard, the wire and the phone (not XInput) — the tests' eye on a press.</summary>
    public PadButtons Pressed(int player)
    {
        if (player < 0 || player >= ArcadeEngine.MaxPads) return PadButtons.None;
        lock (_gate) return _pressed[player];
    }

    /// <summary>A number on the keyboard: the house plays that game; START joins.</summary>
    public void PickGame(int number)
    {
        if (ArcadeEngine.Find(number.ToString()) is not { } info) return;
        Start();
        lock (_gate) _engine.Attract(info.Id);
    }

    /// <summary>One arcade verb, run here.</summary>
    public ActionResult Run(ShowAction a)
    {
        var value = (a.Value ?? "").Trim();
        switch (a.Kind)
        {
            case ShowActionKind.ArcadeStart:
            {
                var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var game = words.Length > 0 ? words[0] : "";
                var players = words.Length > 1 && int.TryParse(words[1], out var n) ? n : 1;
                if (ArcadeEngine.Find(game) is not { } info) return ActionResult.Refused($"No game '{game}' — pong, snake or breakout.");
                players = Math.Clamp(players, 1, info.MaxPlayers);
                Start();
                lock (_gate) _engine.Start(info.Id, players);
                return ActionResult.Done($"{info.Title} for {players} — {Words}");
            }
            case ShowActionKind.ArcadeStop:
                lock (_gate) _engine.Stop();
                return ActionResult.Done("Arcade stopped — the title card.");
            case ShowActionKind.ArcadePause:
            {
                bool ok;
                lock (_gate) ok = _engine.Pause();
                return ok ? ActionResult.Done("Arcade paused.") : ActionResult.Refused("No match to pause.");
            }
            case ShowActionKind.ArcadeResume:
            {
                bool ok;
                lock (_gate) ok = _engine.Resume();
                return ok ? ActionResult.Done("Arcade resumed.") : ActionResult.Refused("Nothing paused.");
            }
            case ShowActionKind.ArcadeAttract:
            {
                if (value.Length > 0 && ArcadeEngine.Find(value) is null) return ActionResult.Refused($"No game '{value}' — pong, snake or breakout.");
                Start();
                string title;
                lock (_gate)
                {
                    _engine.Attract(value.Length > 0 ? value : null);
                    title = _engine.Game?.Title ?? "";
                }
                return ActionResult.Done($"The house plays {title} — START joins.");
            }
            case ShowActionKind.ArcadeKey:
            {
                // "1 UP TAP", "2 A DOWN", "1 START": the pad's number, the button, and how (TAP unless said).
                var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (words.Length == 0) return ActionResult.Refused("ARCADE KEY <player> <button> [DOWN|UP|TAP]");
                var i = 0;
                var player = 1;
                if (int.TryParse(words[0], out var p)) { player = p; i++; }
                if (i >= words.Length) return ActionResult.Refused("ARCADE KEY needs a button: UP DOWN LEFT RIGHT A B START.");
                var button = PadState.ParseButton(words[i]);
                if (button == PadButtons.None) return ActionResult.Refused($"No button '{words[i]}' — UP DOWN LEFT RIGHT A B START.");
                var mode = i + 1 < words.Length ? words[i + 1].ToUpperInvariant() : "TAP";
                if (player < 1 || player > ArcadeEngine.MaxPads) return ActionResult.Refused("The pad is 1 to 4.");
                lock (_gate)
                {
                    switch (mode)
                    {
                        case "DOWN": case "HOLD": case "PRESS": _pressed[player - 1] |= button; break;
                        case "UP": case "RELEASE": _pressed[player - 1] &= ~button; break;
                        default:
                            _pressed[player - 1] |= button;
                            _taps.Add((player - 1, button, _engine.Step + TapSteps));
                            mode = "TAP";
                            break;
                    }
                }
                return ActionResult.Done($"P{player} {button} {mode}");
            }
            case ShowActionKind.ArcadeSize:
            {
                var parts = value.ToLowerInvariant().Split('x', '×', '*');
                if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out var w) || !int.TryParse(parts[1].Trim(), out var h)) return ActionResult.Refused("ARCADE SIZE <width>x<height>, 1920x1080 or 3840x1080.");
                _width = Math.Clamp(w, 320, 4096);
                _height = Math.Clamp(h, 180, 2160);
                Interlocked.Increment(ref _rev);
                return ActionResult.Done($"Arcade picture {_width}×{_height}.");
            }
            case ShowActionKind.ArcadeNdi:
            {
                var on = value.Length == 0 || value.Equals("on", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
                _ndiOn = on;
                if (on) Start();
                Interlocked.Increment(ref _rev);
                return ActionResult.Done(on ? $"NDI on — '{NdiName}' on the network once a receiver asks." : "NDI off.");
            }
            case ShowActionKind.ArcadeName:
            {
                bool ok;
                lock (_gate) { ok = _board.Name(null, value); if (ok) _boardDirty = true; }
                return ok ? ActionResult.Done($"The last score is {value.ToUpperInvariant()}'s.") : ActionResult.Refused("No score to name yet — a match with someone on a pad first.");
            }
            default:
                return ActionResult.Refused("Not an arcade verb.");
        }
    }

    /// <summary>ARCADE STATUS / GAMES / SCORES as JSON.</summary>
    public string StatusJson(string? what)
    {
        var w = (what ?? "").Trim();
        lock (_gate)
        {
            if (w.StartsWith("games", StringComparison.OrdinalIgnoreCase))
            {
                return JsonUtil.SerializeCompact(ArcadeEngine.Catalogue.Select(g => new { id = g.Id, title = g.Title, maxPlayers = g.MaxPlayers, blurb = g.Blurb }).ToArray());
            }
            if (w.StartsWith("scores", StringComparison.OrdinalIgnoreCase))
            {
                var game = w.Length > 6 ? w[6..].Trim() : "";
                var id = ArcadeEngine.Find(game)?.Id ?? _engine.Game?.Id ?? ArcadeEngine.Catalogue[0].Id;
                return JsonUtil.SerializeCompact(new { game = id, scores = _board.Top(id, 10).Select(e => new { e.Name, e.Score, whenUtc = e.WhenUtc, e.Show }).ToArray() });
            }
            var snap = _engine.Snapshot();
            var current = snap.GameId.Length > 0 ? snap.GameId : ArcadeEngine.Catalogue[0].Id;
            return JsonUtil.SerializeCompact(new
            {
                phase = snap.Phase.ToString().ToLowerInvariant(),
                game = snap.GameId,
                title = snap.Title,
                seats = snap.Seats,
                scores = snap.Scores,
                humans = snap.Humans,
                winner = snap.Winner,
                step = snap.Step,
                seed = snap.Seed,
                difficulty = Math.Round(snap.Difficulty, 2),
                words = snap.Words,
                running = _run,
                fps = Math.Round(_fpsMeter.Fps),
                size = new { width = _width, height = _height },
                ndi = new { on = _ndiOn, name = NdiName, status = _ndi?.Status ?? "Off", receivers = _ndi?.Connections ?? 0 },
                board = _board.Top(current, 5).Select(e => new { e.Name, e.Score }).ToArray(),
                games = ArcadeEngine.Catalogue.Select(g => g.Id).ToArray(),
                rev = Rev,
            });
        }
    }

    // ---- the loop ---------------------------------------------------------------------------------

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        double last = 0;
        try
        {
            while (_run)
            {
                var fps = Math.Clamp(_fps, 1, 240);
                var now = clock.Elapsed.TotalSeconds;
                var wake = (Math.Floor(now * fps) + 1) / fps;
                var wait = wake - now;
                if (wait > 0.002) Thread.Sleep((int)((wait - 0.001) * 1000));
                while (clock.Elapsed.TotalSeconds < wake && _run) Thread.SpinWait(40);
                now = clock.Elapsed.TotalSeconds;
                var elapsed = last == 0 ? 1.0 / fps : now - last;
                last = now;
                Frame(elapsed, now);
            }
        }
        catch (Exception ex)
        {
            Log.Error("The arcade loop ended on a fault.", ex);
            _run = false;
        }
    }

    private void Frame(double elapsed, double now)
    {
        int w, h, index;
        SKBitmap bitmap;
        lock (_gate)
        {
            PollPads();
            ReleaseTaps();
            for (var i = 0; i < ArcadeEngine.MaxPads; i++) _engine.SetPad(i, _pressed[i] | _pads[i]);
            _engine.Advance(elapsed);
            w = _width;
            h = _height;
            index = NextBuffer();
            bitmap = EnsureBuffer(index, w, h);
            var canvas = _surfaces[index]!.Canvas;
            if (Board is null || !Board(canvas, w, h, _paints)) _engine.Render(canvas, w, h, _paints);
            canvas.Flush();
            lock (_pick) _latest = index;
            Interlocked.Increment(ref _frames);
            _fpsMeter.Tick(now);
            if (_boardDirty) SaveBoard();
        }
        if (_ndiOn)
        {
            _ndi ??= new NdiFrameSender(NdiName);
            _ndi.Send(bitmap.GetPixels(), w, h, bitmap.RowBytes, _fps, 1);
        }
        else if (_ndi is not null && _ndi.IsOpen)
        {
            _ndi.Close();
        }
    }

    private void PollPads()
    {
        if (!XInputPads.Available) return;
        for (var i = 0; i < ArcadeEngine.MaxPads; i++) _pads[i] = XInputPads.TryRead(i, out var b) ? b : PadButtons.None;
    }

    private void ReleaseTaps()
    {
        for (var i = _taps.Count - 1; i >= 0; i--)
        {
            if (_taps[i].ReleaseAt > _engine.Step) continue;
            _pressed[_taps[i].Player] &= ~_taps[i].Button;
            _taps.RemoveAt(i);
        }
    }

    private int NextBuffer()
    {
        lock (_pick)
        {
            for (var i = 0; i < Buffers; i++) if (i != _latest && i != _drawing) return i;
            return 0;
        }
    }

    private SKBitmap EnsureBuffer(int index, int w, int h)
    {
        var b = _bitmaps[index];
        if (b is null || b.Width != w || b.Height != h)
        {
            _surfaces[index]?.Dispose();
            b?.Dispose();
            b = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            _bitmaps[index] = b;
            _surfaces[index] = SKSurface.Create(b.Info, b.GetPixels(), b.RowBytes);
        }
        return b;
    }

    /// <summary>The newest whole frame onto a canvas, fitted to <paramref name="dest"/>; false when there is none yet.</summary>
    public bool DrawLatest(SKCanvas canvas, SKRect dest)
    {
        int index;
        lock (_pick)
        {
            index = _latest;
            if (index < 0) return false;
            _drawing = index;
        }
        try
        {
            var bitmap = _bitmaps[index];
            if (bitmap is null) return false;
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(bitmap, dest, paint);
            return true;
        }
        finally
        {
            lock (_pick) _drawing = -1;
        }
    }

    private void OnMatchEnded(IArcadeGame game)
    {
        // On the loop's thread, under the gate: every seat with a person gets a line on the board.
        for (var seat = 0; seat < game.Seats && seat < game.Scores.Count; seat++)
        {
            if (!game.IsHuman(seat)) continue;
            var score = game.Scores[seat];
            var rank = _board.Add(game.Id, score, $"P{seat + 1}", DateTime.UtcNow, _s.State.Name);
            _boardDirty = true;
            var words = $"Arcade: {game.Title} — P{seat + 1} {score}{(rank > 0 ? $", #{rank} on the board" : "")}.";
            Dispatcher.UIThread.Post(() => _s.Notify(words));
        }
        Interlocked.Increment(ref _rev);
    }

    private void SaveBoard()
    {
        _boardDirty = false;
        try { File.WriteAllText(_boardPath, _board.Json()); }
        catch (Exception ex) { Log.Warn("The arcade's board could not be saved.", ex); }
    }

    private static string? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) { Log.Warn("The arcade's board could not be read.", ex); return null; }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_boardDirty) SaveBoard();
        }
        _ndi?.Dispose();
        _ndi = null;
        lock (_pick) { _latest = -1; _drawing = -1; }
        for (var i = 0; i < Buffers; i++)
        {
            _surfaces[i]?.Dispose();
            _bitmaps[i]?.Dispose();
            _surfaces[i] = null;
            _bitmaps[i] = null;
        }
        _paints.Dispose();
    }
}
