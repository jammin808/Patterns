using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Play;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// Audience play on the hub: the room (<see cref="PlayRoom"/>, pure) under one lock, the phones'
/// API the control service serves, the host's verbs, the wall through the arcade's picture lane,
/// the story file and the export in the node's folder, and the queue's second look through the
/// desk's assistant. On a desk with no hub heard, the desk is the room.
/// </summary>
public sealed class PlayService : IDisposable
{
    private readonly ServiceKernel _k;
    private readonly IPlayHost _s;
    private readonly object _gate = new();
    private readonly Random _rng = new();
    private readonly string _storyPath;
    private long _wallRev;
    private DateTime _lastAskUtc = DateTime.MinValue;
    private bool _asking;
    private readonly RateLimiter _limits = new();
    private TaskCompletionSource<bool> _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _longPolls;
    private int _longPollsPeak;

    public PlayService(ServiceKernel kernel, IPlayHost host)
    {
        _k = kernel;
        _s = host;
        _storyPath = Path.Combine(kernel.Store.BaseDirectory, "play-story.json");
        Room = new PlayRoom(PlayRoom.NewCode(_rng), kernel.State.Name, DateTime.UtcNow);
        LoadStory();
    }

    public PlayRoom Room { get; private set; }
    public PlayBoardMode Wall { get; private set; } = PlayBoardMode.Off;
    public string WallMessage { get; private set; } = "";
    /// <summary>Whether the queue's waiting items are shown to the desk's assistant before the host.</summary>
    public bool AskAssistant { get; set; } = true;
    public string Code { get { lock (_gate) return Room.Code; } }
    public long Rev { get { lock (_gate) return Room.Rev + _wallRev; } }
    /// <summary>The budgets the room and its socket keep — hard numbers; the player cap follows the Remote page's setting.</summary>
    public AudienceBudget Budget { get; set; } = new();
    public int LongPolls => Volatile.Read(ref _longPolls);
    public int LongPollsPeak => Volatile.Read(ref _longPollsPeak);

    /// <summary>The room woke every waiting phone: called under the gate after anything that moved the revision.</summary>
    private void Signal()
    {
        var tcs = _changed;
        _changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult(true);
    }

    private long RevUnlocked => Room.Rev + _wallRev;

    /// <summary>
    /// True when the room moved past <paramref name="seenRev"/> — at once, or when it does within the
    /// timeout; false on the timeout, or at once past the long-poll budget (the phone asks again).
    /// </summary>
    public async Task<bool> WaitForChangeAsync(long seenRev, TimeSpan timeout, CancellationToken ct)
    {
        Task<bool> waiter;
        lock (_gate)
        {
            if (RevUnlocked != seenRev) return true;
            waiter = _changed.Task;
        }
        var open = Interlocked.Increment(ref _longPolls);
        try
        {
            if (open > Budget.MaxLongPolls) return false;
            var peak = Volatile.Read(ref _longPollsPeak);
            while (open > peak && Interlocked.CompareExchange(ref _longPollsPeak, open, peak) != peak) peak = Volatile.Read(ref _longPollsPeak);
            using var timer = new CancellationTokenSource(timeout);
            using var both = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, ct);
            var delay = Task.Delay(Timeout.InfiniteTimeSpan, both.Token);
            var done = await Task.WhenAny(waiter, delay);
            both.Cancel();
            return done == waiter;
        }
        finally
        {
            Interlocked.Decrement(ref _longPolls);
        }
    }

    public static bool IsPlayKind(ShowActionKind kind) => kind is ShowActionKind.PlayAdd or ShowActionKind.PlayOpen or ShowActionKind.PlayClose or ShowActionKind.PlayReveal
        or ShowActionKind.PlayShow or ShowActionKind.PlayMessage or ShowActionKind.PlayApprove or ShowActionKind.PlayReject or ShowActionKind.PlayAuto
        or ShowActionKind.PlayPath or ShowActionKind.PlayDraughts or ShowActionKind.PlayRoom or ShowActionKind.PlayExport;

    /// <summary>The wire's line for a play action — what the desk sends the hub.</summary>
    public static string Line(ShowAction a) => a.Kind switch
    {
        ShowActionKind.PlayAdd => $"PLAY ADD {a.Value}",
        ShowActionKind.PlayOpen => $"PLAY OPEN {a.Value}".TrimEnd(),
        ShowActionKind.PlayClose => "PLAY CLOSE",
        ShowActionKind.PlayReveal => "PLAY REVEAL",
        ShowActionKind.PlayShow => $"PLAY SHOW {a.Value}".TrimEnd(),
        ShowActionKind.PlayMessage => $"PLAY MESSAGE {a.Value}",
        ShowActionKind.PlayApprove => $"PLAY APPROVE {a.Value}".TrimEnd(),
        ShowActionKind.PlayReject => $"PLAY REJECT {a.Value}",
        ShowActionKind.PlayAuto => $"PLAY AUTO {a.Value}".TrimEnd(),
        ShowActionKind.PlayPath => $"PLAY PATH {a.Value}".TrimEnd(),
        ShowActionKind.PlayDraughts => $"PLAY DRAUGHTS {a.Value}".TrimEnd(),
        ShowActionKind.PlayRoom => a.Value.Equals("new", StringComparison.OrdinalIgnoreCase) ? "PLAY NEW" : "PLAY RESET",
        ShowActionKind.PlayExport => "PLAY EXPORT",
        _ => "",
    };

    /// <summary>The audience listener's address — the door's own port; empty while the listener is off.</summary>
    public string AudienceUrl
    {
        get
        {
            var urls = _s.AudienceUrls();
            return urls.FirstOrDefault(u => !u.Contains("localhost", StringComparison.OrdinalIgnoreCase)) ?? urls.FirstOrDefault() ?? "";
        }
    }

    /// <summary>The door: the audience address with the room's code; empty while the audience port is off (the wall says so).</summary>
    public string JoinUrl
    {
        get
        {
            var url = AudienceUrl;
            return url.Length == 0 ? "" : $"{url.TrimEnd('/')}/play?room={Code}";
        }
    }

    /// <summary>AUDIENCE ON [port] / OFF — the listener is a control setting; the control service opens or closes the socket on the publish.</summary>
    public ActionResult RunAudience(ShowAction a)
    {
        var value = (a.Value ?? "").Trim();
        if (a.Kind == ShowActionKind.AudienceOff)
        {
            _s.BulkEdit(() => _k.State.Control.AudienceEnabled = false);
            return ActionResult.Done("The audience port is closed — the phones find nothing.");
        }
        _s.BulkEdit(() =>
        {
            if (int.TryParse(value, out var port)) _k.State.Control.AudiencePort = port;
            _k.State.Control.AudienceEnabled = true;
            if (!_k.State.Control.Enabled) _k.State.Control.Enabled = true;
        });
        return ActionResult.Done($"The audience port is open on {_k.State.Control.AudiencePort} — the play pages and nothing else; put that port, not the control port, on the audience network.");
    }

    private void LoadStory()
    {
        try
        {
            if (File.Exists(_storyPath))
            {
                var story = PathStory.Parse(File.ReadAllText(_storyPath));
                if (story is not null) Room.Path = story;
                else Log.Warn("play-story.json could not be read — the sample story is on.");
            }
            else
            {
                File.WriteAllText(_storyPath, Room.Path.Json());       // the sample, there to be edited
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The path's story file could not be read or written.", ex);
        }
    }

    private void WallTo(PlayBoardMode mode, string message = "")
    {
        Wall = mode;
        WallMessage = message;
        _wallRev++;
        Signal();
        if (mode != PlayBoardMode.Off) _k.Arcade.Start();
    }

    /// <summary>The wall's picture, on the arcade's lane: true and drawn while the wall is on.</summary>
    public bool DrawWall(SKCanvas canvas, int width, int height, PaintCache paints)
    {
        if (Wall == PlayBoardMode.Off) return false;
        lock (_gate)
        {
            PlayBoard.Render(canvas, width, height, paints, Room, Wall, JoinUrl, WallMessage, DateTime.UtcNow);
        }
        return true;
    }

    /// <summary>On the tick: a quiz past its time closes; a waiting item goes to the assistant.</summary>
    public void Tick()
    {
        bool changed;
        ModerationItem? next = null;
        lock (_gate)
        {
            Room.MaxPlayers = _k.State.Control.AudienceMaxPlayers;
            Room.IdleForget = TimeSpan.FromMinutes(Math.Max(1, Budget.IdleForgetMinutes));
            changed = Room.Tick();
            if (changed) Signal();
            if (AskAssistant && !_asking && (DateTime.UtcNow - _lastAskUtc).TotalSeconds >= 1)
            {
                next = Room.Waiting().FirstOrDefault(m => !m.AskedAssistant);
            }
        }
        if (changed) _k.Notify("Audience: the quiz closed — time was up.");
        if (next is not null) _ = AskAssistantAsync(next);
    }

    private async Task AskAssistantAsync(ModerationItem item)
    {
        _asking = true;
        _lastAskUtc = DateTime.UtcNow;
        try
        {
            string? reply = null;
            if (_k.IsDesk)
            {
                var answer = await _k.Assistant.AskAsync(ModerationQuestion(item.Text));
                reply = answer.Sent ? answer.Reply?.Reply : null;
            }
            else
            {
                var desk = await Dispatcher.UIThread.InvokeAsync(() => _k.Nodes.Desks().FirstOrDefault());
                if (desk is not null)
                {
                    var line = await NodesService.AskNodeAsync(desk, "ASSISTANT MODERATE " + item.Text.Replace('\n', ' '));
                    if (line.StartsWith("OK ", StringComparison.Ordinal))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(line[3..]);
                            if (doc.RootElement.TryGetProperty("sent", out var sent) && sent.GetBoolean() && doc.RootElement.TryGetProperty("reply", out var r)) reply = r.GetString();
                        }
                        catch (JsonException) { }
                    }
                }
            }
            var verdict = Verdict(reply);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lock (_gate)
                {
                    if (verdict is null) { var found = Room.Queue.FirstOrDefault(m => m.Id == item.Id); if (found is not null) found.AskedAssistant = true; }
                    else if (Room.Mark(item.Id, verdict.Value, "the assistant")) Signal();
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("The queue's ask of the assistant failed.", ex);
        }
        finally
        {
            _asking = false;
        }
    }

    /// <summary>The one question the assistant is asked about the room's words.</summary>
    public static string ModerationQuestion(string text)
        => "Moderation for a live event's audience wall. Answer with exactly one word — FINE, DOUBTFUL or OUT — for this text an audience member wrote: \"" + text.Replace("\"", "'") + "\"";

    /// <summary>FINE, DOUBTFUL or OUT from the assistant's words; null when it said nothing usable (the host decides).</summary>
    public static ModerationState? Verdict(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var upper = reply.ToUpperInvariant();
        if (upper.Contains("OUT")) return ModerationState.Out;
        if (upper.Contains("DOUBT")) return ModerationState.Doubtful;
        if (upper.Contains("FINE")) return ModerationState.Fine;
        return null;
    }

    // ---- the host's verbs -------------------------------------------------------------------

    public ActionResult Run(ShowAction a)
    {
        lock (_gate)
        {
            var before = RevUnlocked;
            try
            {
                return RunUnlocked(a);
            }
            finally
            {
                if (RevUnlocked != before) Signal();
            }
        }
    }

    private ActionResult RunUnlocked(ShowAction a)
    {
        var value = (a.Value ?? "").Trim();
        {
            switch (a.Kind)
            {
                case ShowActionKind.PlayAdd:
                {
                    var q = PlayQuestion.Parse(value);
                    if (q is null) return ActionResult.Refused("PLAY ADD <choice|multi|scale|words|quiz> <text> | option | option [| correct=N time=S scale=1-10]");
                    Room.Add(q);
                    return ActionResult.Done($"Added {q.KindWord} '{q.Text}' ({q.Id}) — {Room.Questions.Count(x => x.State == PlayQuestionState.Draft)} waiting; PLAY OPEN starts the next.");
                }
                case ShowActionKind.PlayOpen:
                {
                    var q = Room.Open(value.Length == 0 || value.Equals("next", StringComparison.OrdinalIgnoreCase) ? null : value);
                    if (q is null) return ActionResult.Refused(value.Length == 0 || value.Equals("next", StringComparison.OrdinalIgnoreCase) ? "No question waiting — PLAY ADD one." : $"No question '{value}'.");
                    WallTo(PlayBoardMode.Results);
                    return ActionResult.Done($"Open: {q.KindWord} '{q.Text}'{(q.Kind == PlayQuestionKind.Quiz ? $" — {q.TimeLimitSeconds} s" : "")}.");
                }
                case ShowActionKind.PlayClose:
                {
                    var q = Room.Close();
                    return q is null ? ActionResult.Refused("Nothing is open.") : ActionResult.Done($"Closed '{q.Text}' — {q.AnswerCount} answer{(q.AnswerCount == 1 ? "" : "s")}.");
                }
                case ShowActionKind.PlayReveal:
                {
                    var q = Room.Reveal();
                    if (q is null) return ActionResult.Refused("No question to reveal.");
                    WallTo(PlayBoardMode.Results);
                    var r = Room.Results(q);
                    return ActionResult.Done(q.Kind == PlayQuestionKind.Quiz ? $"Revealed: {r.CorrectCount} of {r.Answers} right." : $"Results up — {r.Answers} answer{(r.Answers == 1 ? "" : "s")}.");
                }
                case ShowActionKind.PlayShow:
                {
                    var word = value.Split(' ', 2)[0].ToLowerInvariant();
                    var mode = word switch
                    {
                        "join" or "door" or "code" or "qr" => PlayBoardMode.Join,
                        "results" or "result" or "question" or "poll" => PlayBoardMode.Results,
                        "leaderboard" or "board" or "scores" => PlayBoardMode.Leaderboard,
                        "message" or "text" => PlayBoardMode.Message,
                        "draughts" or "checkers" => PlayBoardMode.Draughts,
                        "path" or "story" => PlayBoardMode.Path,
                        "off" or "hide" or "none" => PlayBoardMode.Off,
                        _ => (PlayBoardMode?)null,
                    };
                    if (mode is null) return ActionResult.Refused("PLAY SHOW join | results | leaderboard | message [words] | draughts | path | off");
                    var message = mode == PlayBoardMode.Message && value.Contains(' ') ? value[(value.IndexOf(' ') + 1)..].Trim() : "";
                    WallTo(mode.Value, message);
                    return ActionResult.Done(mode == PlayBoardMode.Off ? "The wall shows the arcade again." : $"The wall shows {word}.");
                }
                case ShowActionKind.PlayMessage:
                {
                    var (to, text) = SplitMessage(value);
                    var m = Room.Send(to, text);
                    if (m is null) return ActionResult.Refused(to.StartsWith("phone:") ? $"No phone named '{to[6..]}'." : "PLAY MESSAGE [room | group:<name> | phone:<nick>] <words>");
                    return ActionResult.Done($"Sent to {m.ToWords}: {m.Text}");
                }
                case ShowActionKind.PlayApprove:
                {
                    if (value.Length == 0 || value.Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        var n = Room.ApproveAll();
                        return ActionResult.Done($"Approved {n} — {Room.Waiting().Count} waiting.");
                    }
                    return Room.Approve(value) ? ActionResult.Done($"Approved {value}.") : ActionResult.Refused($"Nothing waiting as '{value}'.");
                }
                case ShowActionKind.PlayReject:
                    return Room.Reject(value) ? ActionResult.Done($"Rejected {value}.") : ActionResult.Refused($"Nothing waiting as '{value}'.");
                case ShowActionKind.PlayAuto:
                {
                    var on = value.Length == 0 || value.Equals("on", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    Room.AutoApprove = on;
                    return ActionResult.Done(on ? "Words go straight to the wall." : "Words wait for the host.");
                }
                case ShowActionKind.PlayPath:
                {
                    switch (value.ToLowerInvariant())
                    {
                        case "" or "open" or "vote":
                            if (!Room.Path.Open()) return ActionResult.Refused("The story is at its end — PLAY PATH RESET starts it again.");
                            WallTo(PlayBoardMode.Path);
                            return ActionResult.Done($"The vote is open: {Room.Path.Current?.Options.Count} ways.");
                        case "close" or "go":
                        {
                            var chosen = Room.Path.Close();
                            if (chosen is null) return ActionResult.Refused("No vote is open.");
                            WallTo(PlayBoardMode.Path);
                            return ActionResult.Done($"The room chose: {chosen.Text}{(Room.Path.IsEnd ? " — the end." : "")}");
                        }
                        case "reset" or "restart":
                            Room.Path.Reset();
                            WallTo(PlayBoardMode.Path);
                            return ActionResult.Done("The story starts again.");
                        case "reload":
                            LoadStory();
                            return ActionResult.Done($"The story read again: {Room.Path.Title}, {Room.Path.Scenes.Count} scenes.");
                        default:
                            return ActionResult.Refused("PLAY PATH open | close | reset | reload");
                    }
                }
                case ShowActionKind.PlayDraughts:
                    Room.Draughts.Reset();
                    WallTo(PlayBoardMode.Draughts);
                    return ActionResult.Done("A new board — two phones take the sides on /play.");
                case ShowActionKind.PlayRoom:
                {
                    var fresh = value.Equals("new", StringComparison.OrdinalIgnoreCase);
                    Room.Reset(fresh, _rng);
                    if (fresh) WallTo(PlayBoardMode.Join);
                    return ActionResult.Done(fresh ? $"A new room: {Room.Code} — every phone joins again." : "The room reset — questions kept as drafts, scores and messages cleared.");
                }
                case ShowActionKind.PlayExport:
                {
                    try
                    {
                        var path = Path.Combine(_k.Store.BaseDirectory, $"play-export-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                        File.WriteAllText(path, Room.ExportJson());
                        return ActionResult.Done($"Exported to {path}.");
                    }
                    catch (Exception ex)
                    {
                        return ActionResult.Failed($"The export could not be written — {ex.Message}");
                    }
                }
                default:
                    return ActionResult.Refused("Not a play verb.");
            }
        }
    }

    /// <summary>"room The poll closes", "group:Table 4 you won", "phone:Sam right" — or plain words to the room.</summary>
    public static (string To, string Text) SplitMessage(string value)
    {
        var v = value.Trim();
        var sp = v.IndexOf(' ');
        var head = sp < 0 ? v : v[..sp];
        var lower = head.ToLowerInvariant();
        if (lower == "room") return ("room", sp < 0 ? "" : v[(sp + 1)..].Trim());
        if (lower.StartsWith("group:") || lower.StartsWith("phone:"))
        {
            // "group:Table 4 you won": the name runs to the first word that is not part of it — the name is what a phone typed, so take words while the target has no message yet.
            var rest = sp < 0 ? "" : v[(sp + 1)..].Trim();
            var kind = head[..6];
            var name = head[6..];
            if (kind.StartsWith("group", StringComparison.OrdinalIgnoreCase) && rest.Length > 0)
            {
                // A group name may hold a space ("Table 4"): try the longest name that exists is the host's job; here one extra word joins when it is a number.
                var words = rest.Split(' ', 2);
                if (words.Length == 2 && int.TryParse(words[0], out _)) { name += " " + words[0]; rest = words[1]; }
            }
            return (kind.ToLowerInvariant() + name, rest);
        }
        return ("room", v);
    }

    // ---- the phones' API ----------------------------------------------------------------------

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name, int fallback = 0) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private static JsonElement Body(string body)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    public string JoinJson(string body, string address = "?")
    {
        var e = Body(body);
        if (!_limits.Allow("join:" + address, Budget.JoinsPerAddressPerMinute, TimeSpan.FromMinutes(1), DateTime.UtcNow))
        {
            return JsonUtil.SerializeCompact(new { ok = false, msg = "Too many joins from this address — a minute, then again." });
        }
        lock (_gate)
        {
            var room = Str(e, "room");
            if (room is { Length: > 0 } && !room.Equals(Room.Code, StringComparison.OrdinalIgnoreCase))
            {
                return JsonUtil.SerializeCompact(new { ok = false, msg = $"That is not this room — the room here is {Room.Code}.", room = Room.Code });
            }
            Room.MaxPlayers = _k.State.Control.AudienceMaxPlayers;
            var (p, fresh, reason) = Room.TryJoin(Str(e, "nick"), Str(e, "token"), Str(e, "group"));
            if (p is null) return JsonUtil.SerializeCompact(new { ok = false, msg = $"No seat — {reason}.", room = Room.Code });
            Signal();
            return JsonUtil.SerializeCompact(new { ok = true, fresh, token = p.Token, nick = p.Nick, group = p.Group, room = Room.Code, show = Room.Show });
        }
    }

    private bool Within(string kind, string? token, int perMinute)
        => _limits.Allow($"{kind}:{token}", perMinute, TimeSpan.FromMinutes(1), DateTime.UtcNow);

    private static string SlowDown(string what) => JsonUtil.SerializeCompact(new { ok = false, msg = $"Slow down — too many {what} this minute." });

    public string AnswerJson(string body)
    {
        var e = Body(body);
        var choices = new List<int>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in c.EnumerateArray()) if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var i)) choices.Add(i);
        }
        if (!Within("answer", Str(e, "token"), Budget.AnswersPerTokenPerMinute)) return SlowDown("answers");
        lock (_gate)
        {
            var msg = Room.Answer(Str(e, "token"), Str(e, "question"), choices, Int(e, "scale", int.MinValue), Str(e, "words"));
            if (msg is "ok" or "queued") Signal();
            return JsonUtil.SerializeCompact(new { ok = msg is "ok" or "queued", msg });
        }
    }

    public string SayJson(string body)
    {
        var e = Body(body);
        if (!Within("say", Str(e, "token"), Budget.SaysPerTokenPerMinute)) return SlowDown("messages");
        lock (_gate)
        {
            var item = Room.Say(Str(e, "token"), Str(e, "text"));
            if (item is null) return JsonUtil.SerializeCompact(new { ok = false, msg = "a word or two, from a phone that joined" });
            Signal();
            return JsonUtil.SerializeCompact(new { ok = item.State != ModerationState.Out, msg = item.State == ModerationState.Out ? "not for the wall" : "with the host" });
        }
    }

    public string VoteJson(string body)
    {
        var e = Body(body);
        if (!Within("vote", Str(e, "token"), Budget.AnswersPerTokenPerMinute)) return SlowDown("votes");
        lock (_gate)
        {
            var msg = Room.Path.Vote(Str(e, "token"), Int(e, "option", -1));
            if (Room.Find(Str(e, "token")) is { } p) p.LastSeenUtc = DateTime.UtcNow;
            _wallRev++;
            Signal();
            return JsonUtil.SerializeCompact(new { ok = msg == "ok", msg });
        }
    }

    public string DraughtsJson(string body)
    {
        var e = Body(body);
        if (!Within("move", Str(e, "token"), Budget.MovesPerTokenPerMinute)) return SlowDown("moves");
        lock (_gate)
        {
            var token = Str(e, "token");
            var p = Room.Find(token);
            if (p is null) return JsonUtil.SerializeCompact(new { ok = false, msg = "join first" });
            var action = (Str(e, "action") ?? "").ToLowerInvariant();
            string msg;
            switch (action)
            {
                case "seat":
                    var side = (Str(e, "side") ?? "").ToLowerInvariant() == "white" ? DraughtSide.White : DraughtSide.Black;
                    msg = Room.Draughts.Seat(p.Token, p.Nick, side) ? "ok" : "that side is taken";
                    break;
                case "leave":
                    Room.Draughts.Leave(p.Token);
                    msg = "ok";
                    break;
                case "move":
                    msg = Room.Draughts.Move(p.Token, Int(e, "from", -1), Int(e, "to", -1));
                    break;
                default:
                    msg = "seat, move or leave";
                    break;
            }
            _wallRev++;
            Signal();
            return JsonUtil.SerializeCompact(new { ok = msg.StartsWith("ok"), msg });
        }
    }

    /// <summary>What one phone sees: the question and its own answer, its messages, the board, the path, the draughts board.</summary>
    public string StateJson(string? token, long sinceSeq)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var p = Room.Find(token);
            if (p is not null) p.LastSeenUtc = now;
            var q = Room.Current;
            object? question = null;
            if (q is not null)
            {
                var mine = Room.AnswerOf(token, q);
                var r = q.State is PlayQuestionState.Closed or PlayQuestionState.Revealed ? Room.Results(q) : null;
                question = new
                {
                    id = q.Id,
                    kind = q.KindWord,
                    text = q.Text,
                    options = q.Options,
                    scaleMin = q.ScaleMin,
                    scaleMax = q.ScaleMax,
                    state = q.State.ToString().ToLowerInvariant(),
                    secondsLeft = Math.Round(q.SecondsLeft(now), 1),
                    answers = q.AnswerCount,
                    correct = q.State == PlayQuestionState.Revealed ? q.Correct : -1,
                    mine = mine is null ? null : new { choices = mine.Choices, scale = mine.Scale, words = mine.Words, points = mine.Points, correct = mine.Correct },
                    results = r is null ? null : new { counts = r.Counts, percent = Enumerable.Range(0, q.Options.Count).Select(r.Percent).ToArray(), average = Math.Round(r.ScaleAverage, 1), words = r.Words.Select(kv => new { word = kv.Key, count = kv.Value }).ToArray() },
                };
            }
            var d = Room.Draughts;
            var board = new StringBuilder(64);
            for (var i = 0; i < 64; i++) board.Append(d[i] switch { 1 => 'w', 2 => 'W', -1 => 'b', -2 => 'B', _ => '.' });
            var scene = Room.Path.Current;
            return JsonUtil.SerializeCompact(new
            {
                rev = Room.Rev + _wallRev,
                room = Room.Code,
                show = Room.Show,
                known = p is not null,
                nick = p?.Nick ?? "",
                group = p?.Group ?? "",
                score = p?.Score ?? 0,
                players = Room.PlayerCount,
                question,
                messages = Room.Inbox(token, sinceSeq).Select(m => new { seq = m.Seq, text = m.Text, to = m.ToWords, at = m.SentUtc }).ToArray(),
                leaderboard = Room.Leaderboard(5).Select(x => new { nick = x.Nick, score = x.Score }).ToArray(),
                wall = Wall.ToString().ToLowerInvariant(),
                path = new { title = Room.Path.Title, scene = scene?.Id ?? "", text = scene?.Text ?? "", options = scene?.Options.Select(o => o.Text).ToArray() ?? Array.Empty<string>(), open = Room.Path.VotingOpen, end = Room.Path.IsEnd, chose = Room.Path.LastChoice },
                draughts = new { board = board.ToString(), turn = d.Turn.ToString().ToLowerInvariant(), mine = d.SideOf(token).ToString().ToLowerInvariant(), winner = d.Winner.ToString().ToLowerInvariant(), words = d.Words, black = d.NickOf(DraughtSide.Black), white = d.NickOf(DraughtSide.White), continueFrom = d.ContinueFrom ?? -1 },
            });
        }
    }

    /// <summary>PLAY STATUS / RESULTS / QUEUE.</summary>
    public string StatusJson(string? what)
    {
        var w = (what ?? "").Trim();
        lock (_gate)
        {
            if (w.StartsWith("audience", StringComparison.OrdinalIgnoreCase))
            {
                var cfg = _k.State.Control;
                return JsonUtil.SerializeCompact(new
                {
                    enabled = cfg.Enabled && cfg.AudienceEnabled,
                    listening = _s.AudienceListening,
                    port = cfg.AudiencePort,
                    bind = cfg.AudienceBind,
                    urls = _s.AudienceUrls(),
                    joinUrl = JoinUrl,
                    players = Room.PlayerCount,
                    maxPlayers = cfg.AudienceMaxPlayers,
                    connections = _s.AudienceConnections,
                    longPolls = LongPolls,
                    longPollsPeak = LongPollsPeak,
                    budget = new { Budget.JoinsPerAddressPerMinute, Budget.AnswersPerTokenPerMinute, Budget.SaysPerTokenPerMinute, Budget.MovesPerTokenPerMinute, Budget.MaxLongPolls, Budget.MaxConnectionsPerAddress, Budget.MaxConnections, Budget.IdleForgetMinutes },
                    assistantOnWire = cfg.AssistantOnWire,
                });
            }
            if (w.StartsWith("results", StringComparison.OrdinalIgnoreCase))
            {
                var id = w.Length > 7 ? w[7..].Trim() : "";
                var q = id.Length > 0 ? Room.FindQuestion(id) : Room.Current;
                if (q is null) return JsonUtil.SerializeCompact(new { question = (object?)null, msg = "no such question" });
                var r = Room.Results(q);
                return JsonUtil.SerializeCompact(new
                {
                    question = new { id = q.Id, kind = q.KindWord, text = q.Text, options = q.Options, correct = q.Correct, state = q.State.ToString().ToLowerInvariant() },
                    answers = r.Answers, counts = r.Counts, percent = Enumerable.Range(0, q.Options.Count).Select(r.Percent).ToArray(), average = Math.Round(r.ScaleAverage, 2),
                    scale = Room.ScaleCounts(q), words = r.Words.Select(kv => new { word = kv.Key, count = kv.Value }).ToArray(), correctCount = r.CorrectCount,
                });
            }
            if (w.StartsWith("queue", StringComparison.OrdinalIgnoreCase))
            {
                return JsonUtil.SerializeCompact(Room.Queue.OrderByDescending(m => m.AtUtc).Take(50).Select(m => new { id = m.Id, nick = m.Nick, text = m.Text, state = m.State.ToString().ToLowerInvariant(), note = m.Note, question = m.QuestionId, at = m.AtUtc }).ToArray());
            }
            var current = Room.Current;
            return JsonUtil.SerializeCompact(new
            {
                room = Room.Code,
                show = Room.Show,
                joinUrl = JoinUrl,
                audience = _k.State.Control.AudienceEnabled ? $"port {_k.State.Control.AudiencePort}" : "off",
                players = Room.PlayerCount,
                here = Room.ActiveCount(DateTime.UtcNow),
                wall = Wall.ToString().ToLowerInvariant(),
                auto = Room.AutoApprove,
                question = current is null ? null : new { id = current.Id, kind = current.KindWord, text = current.Text, state = current.State.ToString().ToLowerInvariant(), answers = current.AnswerCount, secondsLeft = Math.Round(current.SecondsLeft(DateTime.UtcNow), 1) },
                questions = Room.Questions.Select(q => new { id = q.Id, kind = q.KindWord, text = q.Text, state = q.State.ToString().ToLowerInvariant(), answers = q.AnswerCount }).ToArray(),
                waiting = Room.Waiting().Count,
                leaderboard = Room.Leaderboard(5).Select(x => new { nick = x.Nick, score = x.Score }).ToArray(),
                path = new { scene = Room.Path.CurrentId, open = Room.Path.VotingOpen, votes = Room.Path.VoteCount, end = Room.Path.IsEnd },
                draughts = Room.Draughts.Words,
                rev = Room.Rev + _wallRev,
            });
        }
    }

    /// <summary>The host's page: everything, once the passcode opened it.</summary>
    public string HostJson()
    {
        lock (_gate)
        {
            return JsonUtil.SerializeCompact(new
            {
                room = Room.Code,
                show = Room.Show,
                joinUrl = JoinUrl,
                players = Room.Players.OrderBy(p => p.JoinedUtc).Select(p => new { nick = p.Nick, group = p.Group, score = p.Score, here = (DateTime.UtcNow - p.LastSeenUtc).TotalSeconds < 90 }).ToArray(),
                wall = Wall.ToString().ToLowerInvariant(),
                auto = Room.AutoApprove,
                questions = Room.Questions.Select(q =>
                {
                    var r = Room.Results(q);
                    return new { id = q.Id, kind = q.KindWord, text = q.Text, options = q.Options, correct = q.Correct, state = q.State.ToString().ToLowerInvariant(), answers = r.Answers, percent = Enumerable.Range(0, q.Options.Count).Select(r.Percent).ToArray(), average = Math.Round(r.ScaleAverage, 1), words = r.Words.Take(8).Select(kv => kv.Key).ToArray(), secondsLeft = Math.Round(q.SecondsLeft(DateTime.UtcNow)) };
                }).ToArray(),
                queue = Room.Queue.OrderByDescending(m => m.AtUtc).Take(40).Select(m => new { id = m.Id, nick = m.Nick, text = m.Text, state = m.State.ToString().ToLowerInvariant(), note = m.Note, forCloud = m.QuestionId.Length > 0 }).ToArray(),
                messages = Room.Messages.TakeLast(10).Select(m => new { to = m.ToWords, text = m.Text }).ToArray(),
                leaderboard = Room.Leaderboard(10).Select(x => new { nick = x.Nick, score = x.Score }).ToArray(),
                path = new { title = Room.Path.Title, scene = Room.Path.CurrentId, open = Room.Path.VotingOpen, votes = Room.Path.VoteCounts(), end = Room.Path.IsEnd, text = Room.Path.Current?.Text ?? "" },
                draughts = Room.Draughts.Words,
                rev = Room.Rev + _wallRev,
            });
        }
    }

    /// <summary>The room as lines for the message overlay's feed: the question and its leaders, the board, the last word to the room.</summary>
    public string FeedCsv()
    {
        lock (_gate)
        {
            var lines = new List<string>();
            var q = Room.Current;
            if (q is not null)
            {
                var r = Room.Results(q);
                lines.Add($"{q.Text} — {r.Answers} answer{(r.Answers == 1 ? "" : "s")}");
                for (var i = 0; i < q.Options.Count; i++) lines.Add($"{q.Options[i]}: {r.Percent(i)}%");
                if (q.Kind == PlayQuestionKind.Words) lines.AddRange(r.Words.Take(6).Select(kv => $"{kv.Key} ×{kv.Value}"));
                if (q.Kind == PlayQuestionKind.Scale && r.Answers > 0) lines.Add($"average {r.ScaleAverage:0.0}");
            }
            var top = Room.Leaderboard(3);
            for (var i = 0; i < top.Count; i++) lines.Add($"{i + 1}. {top[i].Nick} {top[i].Score}");
            var last = Room.Messages.LastOrDefault(m => m.To == "room");
            if (last is not null) lines.Add(last.Text);
            if (lines.Count == 0) lines.Add($"Join at {JoinUrl}");
            return string.Join("\n", lines.Select(l => l.Replace('\n', ' '))) + "\n";
        }
    }

    public void Dispose()
    {
    }
}
