using System.Text.Json;
using Patterns.Core.Services;

namespace Patterns.Core.Play;

public enum PlayQuestionKind
{
    /// <summary>One of the options.</summary>
    Choice,
    /// <summary>Any of the options.</summary>
    Multi,
    /// <summary>A number on a scale.</summary>
    Scale,
    /// <summary>A word or a phrase — the cloud; through the queue unless the host lets words straight through.</summary>
    Words,
    /// <summary>One right option against the clock: the first right answers score most.</summary>
    Quiz,
}

public enum PlayQuestionState
{
    Draft,
    Open,
    Closed,
    /// <summary>Closed and the right answer shown (a quiz), or the results put up.</summary>
    Revealed,
}

public enum ModerationState
{
    Waiting,
    Fine,
    Doubtful,
    Out,
}

/// <summary>One question, as the host wrote it and as the room answers it.</summary>
public sealed class PlayQuestion
{
    public string Id { get; init; } = NewId();
    public PlayQuestionKind Kind { get; init; }
    public string Text { get; init; } = "";
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    public int ScaleMin { get; init; } = 1;
    public int ScaleMax { get; init; } = 5;
    /// <summary>The right option of a quiz, 0-based; −1 for none.</summary>
    public int Correct { get; init; } = -1;
    public int TimeLimitSeconds { get; init; } = 20;
    public PlayQuestionState State { get; internal set; } = PlayQuestionState.Draft;
    public DateTime? OpenedUtc { get; internal set; }
    public DateTime? ClosedUtc { get; internal set; }
    internal readonly Dictionary<string, PlayAnswer> Answers = new();

    public int AnswerCount => Answers.Count;
    public bool IsOpen => State == PlayQuestionState.Open;
    public string KindWord => Kind.ToString().ToLowerInvariant();

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Seconds left on an open quiz at <paramref name="utcNow"/>; 0 when none or over.</summary>
    public double SecondsLeft(DateTime utcNow)
    {
        if (Kind != PlayQuestionKind.Quiz || State != PlayQuestionState.Open || OpenedUtc is null) return 0;
        return Math.Max(0, TimeLimitSeconds - (utcNow - OpenedUtc.Value).TotalSeconds);
    }

    /// <summary>
    /// The host's line: "quiz Which hall is the keynote in? | A | B | C | correct=2 time=15",
    /// "choice Lunch? | Pizza | Salad", "scale How was the morning? | scale=1-10", "words One word for today".
    /// </summary>
    public static PlayQuestion? Parse(string? line)
    {
        var s = (line ?? "").Trim();
        if (s.Length == 0) return null;
        var sp = s.IndexOf(' ');
        var kindWord = (sp < 0 ? s : s[..sp]).ToLowerInvariant();
        var rest = sp < 0 ? "" : s[(sp + 1)..].Trim();
        var kind = kindWord switch
        {
            "choice" or "poll" or "single" => PlayQuestionKind.Choice,
            "multi" or "multiple" or "several" => PlayQuestionKind.Multi,
            "scale" or "rate" or "rating" => PlayQuestionKind.Scale,
            "words" or "word" or "cloud" or "wordcloud" => PlayQuestionKind.Words,
            "quiz" or "question" => PlayQuestionKind.Quiz,
            _ => (PlayQuestionKind?)null,
        };
        if (kind is null) return null;
        var parts = rest.Split('|').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return null;
        var text = parts[0];
        var options = new List<string>();
        var correct = -1;
        var time = 20;
        var min = 1;
        var max = 5;
        foreach (var part in parts.Skip(1))
        {
            // A settings part holds only key=value words ("correct=2 time=15"); anything else is an option.
            var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length > 0 && words.All(w => w.Contains('=')))
            {
                foreach (var w in words)
                {
                    var eq = w.IndexOf('=');
                    var key = w[..eq].ToLowerInvariant();
                    var value = w[(eq + 1)..];
                    if (key == "correct" && int.TryParse(value, out var c)) correct = c - 1;
                    else if (key == "time" && int.TryParse(value, out var t)) time = Math.Clamp(t, 3, 600);
                    else if (key == "scale" && value.Split('-') is { Length: 2 } range && int.TryParse(range[0], out var a) && int.TryParse(range[1], out var b) && b > a) { min = a; max = Math.Min(b, a + 20); }
                }
                continue;
            }
            options.Add(part.Length > 80 ? part[..80] : part);
        }
        if (kind is PlayQuestionKind.Choice or PlayQuestionKind.Multi or PlayQuestionKind.Quiz && options.Count < 2) return null;
        if (kind is PlayQuestionKind.Choice or PlayQuestionKind.Multi && options.Count < 2) return null;
        if (kind == PlayQuestionKind.Quiz && (correct < 0 || correct >= options.Count)) return null;
        return new PlayQuestion
        {
            Kind = kind.Value,
            Text = text.Length > 200 ? text[..200] : text,
            Options = options,
            Correct = correct,
            TimeLimitSeconds = time,
            ScaleMin = min,
            ScaleMax = max,
        };
    }
}

public sealed record PlayAnswer(string Token, string Nick, IReadOnlyList<int> Choices, int Scale, string Words, DateTime AtUtc, int Points, bool Correct);

/// <summary>A phone in the room: a token it keeps, a nickname, a group it named, its score.</summary>
public sealed class PlayPlayer
{
    public string Token { get; init; } = "";
    public string Nick { get; set; } = "";
    public string Group { get; set; } = "";
    public DateTime JoinedUtc { get; init; }
    public DateTime LastSeenUtc { get; set; }
    public int Score { get; set; }
}

/// <summary>A message back: to the room, to a group ("group:Table 4"), to one phone ("phone:&lt;token&gt;").</summary>
public sealed record PlayMessage(long Seq, string Id, string To, string Text, DateTime SentUtc)
{
    public bool IsFor(PlayPlayer p) => To == "room" || To == "group:" + p.Group || To == "phone:" + p.Token;
    public string ToWords => To == "room" ? "the room" : To.StartsWith("group:") ? To[6..] : "one phone";
}

/// <summary>Something the room wrote, on its way to the wall: the list first, the assistant second, the host last.</summary>
public sealed class ModerationItem
{
    public string Id { get; init; } = PlayQuestion.NewId();
    public string Token { get; init; } = "";
    public string Nick { get; init; } = "";
    public string Text { get; init; } = "";
    public string QuestionId { get; init; } = "";
    public DateTime AtUtc { get; init; }
    public ModerationState State { get; set; }
    public string Note { get; set; } = "";
    public bool AskedAssistant { get; set; }
}

public sealed record PlayResults(PlayQuestionKind Kind, int Answers, IReadOnlyList<int> Counts, double ScaleAverage, IReadOnlyList<KeyValuePair<string, int>> Words, int CorrectCount)
{
    public int Percent(int option) => Answers == 0 || option < 0 || option >= Counts.Count ? 0 : (int)Math.Round(100.0 * Counts[option] / Answers);
}

/// <summary>
/// The room: who joined, the questions in order, the answers and the scores, the queue of what
/// the room wrote, the messages back, the revision the phones and the wall poll on. Pure — the
/// node's server calls it under a lock and the tests drive it with a clock of their own.
/// </summary>
public sealed class PlayRoom
{
    public const string CodeLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const int NickMax = 20;
    public const int WordsMax = 60;

    private readonly Dictionary<string, PlayPlayer> _players = new();
    private readonly List<PlayQuestion> _questions = new();
    private readonly List<PlayMessage> _messages = new();
    private readonly List<ModerationItem> _queue = new();
    private long _seq;

    public PlayRoom(string code, string show, DateTime createdUtc)
    {
        Code = code;
        Show = show;
        CreatedUtc = createdUtc;
    }

    public string Code { get; private set; }
    public string Show { get; set; }
    public DateTime CreatedUtc { get; }
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    /// <summary>Words straight to the wall without a press — for a quiz night, not a conference.</summary>
    public bool AutoApprove { get; set; }
    public IReadOnlyList<string> BlockedWords { get; set; } = DefaultBlocked;
    public long Rev { get; private set; }
    public IReadOnlyList<PlayQuestion> Questions => _questions;
    public IReadOnlyList<PlayMessage> Messages => _messages;
    public IReadOnlyList<ModerationItem> Queue => _queue;
    public IReadOnlyCollection<PlayPlayer> Players => _players.Values;
    public int PlayerCount => _players.Count;
    public Draughts Draughts { get; private set; } = Draughts.New();
    public PathStory Path { get; set; } = PathStory.Sample();

    private static readonly string[] DefaultBlocked = { "fuck", "shit", "cunt", "nigger", "faggot" };

    /// <summary>Four letters no phone mistypes: no I, no O, no digits.</summary>
    public static string NewCode(Random rng)
    {
        var chars = new char[4];
        for (var i = 0; i < 4; i++) chars[i] = CodeLetters[rng.Next(CodeLetters.Length)];
        return new string(chars);
    }

    private void Bump() => Rev++;

    /// <summary>The open question, else the last one that was.</summary>
    public PlayQuestion? Current => _questions.FirstOrDefault(q => q.State == PlayQuestionState.Open) ?? _questions.LastOrDefault(q => q.State != PlayQuestionState.Draft);

    public int ActiveCount(DateTime utcNow) => _players.Values.Count(p => (utcNow - p.LastSeenUtc).TotalSeconds < 90);

    // ---- joining ----------------------------------------------------------------------------

    /// <summary>A phone joins (or comes back with its token): a cleaned, unique nickname; the token it keeps.</summary>
    public (PlayPlayer Player, bool Fresh) Join(string? nick, string? token, string? group = null)
    {
        var now = UtcNow();
        if (token is { Length: > 0 } && _players.TryGetValue(token, out var known))
        {
            known.LastSeenUtc = now;
            var wanted = CleanNick(nick);
            if (wanted.Length > 0 && wanted != known.Nick) known.Nick = Unique(wanted, known.Token);
            if (group is not null) known.Group = CleanNick(group);
            Bump();
            return (known, false);
        }
        var fresh = new PlayPlayer
        {
            Token = Guid.NewGuid().ToString("N")[..16],
            JoinedUtc = now,
            LastSeenUtc = now,
            Group = CleanNick(group),
        };
        var name = CleanNick(nick);
        fresh.Nick = Unique(name.Length == 0 ? $"Guest {_players.Count + 1}" : name, fresh.Token);
        _players[fresh.Token] = fresh;
        Bump();
        return (fresh, true);
    }

    public PlayPlayer? Find(string? token) => token is { Length: > 0 } && _players.TryGetValue(token, out var p) ? p : null;

    public PlayPlayer? FindByNick(string nick) => _players.Values.FirstOrDefault(p => string.Equals(p.Nick, nick, StringComparison.OrdinalIgnoreCase));

    public bool Touch(string? token)
    {
        var p = Find(token);
        if (p is null) return false;
        p.LastSeenUtc = UtcNow();
        return true;
    }

    private static string CleanNick(string? nick)
    {
        var s = new string((nick ?? "").Trim().Where(ch => !char.IsControl(ch)).ToArray());
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length > NickMax ? s[..NickMax] : s;
    }

    private string Unique(string nick, string token)
    {
        var name = nick;
        var n = 2;
        while (_players.Values.Any(p => p.Token != token && string.Equals(p.Nick, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{nick} {n++}";
        }
        return name;
    }

    // ---- the questions ----------------------------------------------------------------------

    public PlayQuestion Add(PlayQuestion q)
    {
        _questions.Add(q);
        Bump();
        return q;
    }

    public bool Remove(string id)
    {
        var i = _questions.FindIndex(q => q.Id == id);
        if (i < 0 || _questions[i].IsOpen) return false;
        _questions.RemoveAt(i);
        Bump();
        return true;
    }

    public PlayQuestion? FindQuestion(string? id) => id is { Length: > 0 } ? _questions.FirstOrDefault(q => q.Id == id) : null;

    /// <summary>Opens a question (the next draft when none is named); the one that was open closes first.</summary>
    public PlayQuestion? Open(string? id = null)
    {
        var q = id is { Length: > 0 } ? FindQuestion(id) : NextDraft();
        if (q is null) return null;
        Close();
        q.State = PlayQuestionState.Open;
        q.OpenedUtc = UtcNow();
        q.ClosedUtc = null;
        q.Answers.Clear();
        foreach (var item in _queue.Where(m => m.QuestionId == q.Id).ToList()) _queue.Remove(item);
        Bump();
        return q;
    }

    private PlayQuestion? NextDraft()
    {
        var current = Current;
        var from = current is null ? 0 : _questions.IndexOf(current) + 1;
        for (var i = from; i < _questions.Count; i++) if (_questions[i].State == PlayQuestionState.Draft) return _questions[i];
        return _questions.FirstOrDefault(q => q.State == PlayQuestionState.Draft);
    }

    public PlayQuestion? Close()
    {
        var q = _questions.FirstOrDefault(x => x.IsOpen);
        if (q is null) return null;
        q.State = PlayQuestionState.Closed;
        q.ClosedUtc = UtcNow();
        Bump();
        return q;
    }

    /// <summary>The results (and a quiz's right answer) shown: the current question, closed first if it was open.</summary>
    public PlayQuestion? Reveal()
    {
        var q = Current;
        if (q is null) return null;
        if (q.IsOpen) Close();
        q.State = PlayQuestionState.Revealed;
        Bump();
        return q;
    }

    /// <summary>The clock: an open quiz past its time closes by itself.</summary>
    public bool Tick()
    {
        var q = _questions.FirstOrDefault(x => x.IsOpen);
        if (q is null || q.Kind != PlayQuestionKind.Quiz || q.SecondsLeft(UtcNow()) > 0) return false;
        Close();
        return true;
    }

    // ---- answering --------------------------------------------------------------------------

    /// <summary>A phone's answer to the open question; "ok", "queued" (words for the host), or the reason it was not taken.</summary>
    public string Answer(string? token, string? questionId, IReadOnlyList<int>? choices, int scale, string? words)
    {
        var p = Find(token);
        if (p is null) return "who? — join first";
        p.LastSeenUtc = UtcNow();
        var q = _questions.FirstOrDefault(x => x.IsOpen);
        if (q is null) return "nothing is open";
        if (questionId is { Length: > 0 } && questionId != q.Id) return "that question is over";
        var now = UtcNow();
        switch (q.Kind)
        {
            case PlayQuestionKind.Choice:
            case PlayQuestionKind.Quiz:
            {
                if (choices is not { Count: 1 } || choices[0] < 0 || choices[0] >= q.Options.Count) return "pick one option";
                if (q.Kind == PlayQuestionKind.Quiz)
                {
                    if (q.Answers.ContainsKey(p.Token)) return "already answered";
                    if (q.SecondsLeft(now) <= 0) return "time is up";
                    var correct = choices[0] == q.Correct;
                    var elapsed = (now - q.OpenedUtc!.Value).TotalSeconds;
                    var points = correct ? 500 + (int)Math.Round(500 * Math.Max(0, 1 - elapsed / q.TimeLimitSeconds)) : 0;
                    p.Score += points;
                    q.Answers[p.Token] = new PlayAnswer(p.Token, p.Nick, new[] { choices[0] }, 0, "", now, points, correct);
                }
                else
                {
                    q.Answers[p.Token] = new PlayAnswer(p.Token, p.Nick, new[] { choices[0] }, 0, "", now, 0, false);
                }
                Bump();
                return "ok";
            }
            case PlayQuestionKind.Multi:
            {
                var picked = (choices ?? Array.Empty<int>()).Where(c => c >= 0 && c < q.Options.Count).Distinct().OrderBy(c => c).ToList();
                if (picked.Count == 0) return "pick at least one";
                q.Answers[p.Token] = new PlayAnswer(p.Token, p.Nick, picked, 0, "", now, 0, false);
                Bump();
                return "ok";
            }
            case PlayQuestionKind.Scale:
            {
                if (scale < q.ScaleMin || scale > q.ScaleMax) return $"a number from {q.ScaleMin} to {q.ScaleMax}";
                q.Answers[p.Token] = new PlayAnswer(p.Token, p.Nick, Array.Empty<int>(), scale, "", now, 0, false);
                Bump();
                return "ok";
            }
            case PlayQuestionKind.Words:
            {
                var text = CleanWords(words);
                if (text.Length == 0) return "a word or two";
                if (IsBlocked(text))
                {
                    _queue.Add(new ModerationItem { Token = p.Token, Nick = p.Nick, Text = text, QuestionId = q.Id, AtUtc = now, State = ModerationState.Out, Note = "the word list" });
                    Bump();
                    return "not for the wall";
                }
                if (AutoApprove)
                {
                    q.Answers[p.Token] = new PlayAnswer(p.Token, p.Nick, Array.Empty<int>(), 0, text, now, 0, false);
                    Bump();
                    return "ok";
                }
                foreach (var old in _queue.Where(m => m.QuestionId == q.Id && m.Token == p.Token && m.State == ModerationState.Waiting).ToList()) _queue.Remove(old);
                _queue.Add(new ModerationItem { Token = p.Token, Nick = p.Nick, Text = text, QuestionId = q.Id, AtUtc = now });
                Bump();
                return "queued";
            }
            default:
                return "not a question";
        }
    }

    public PlayAnswer? AnswerOf(string? token, PlayQuestion? q) => q is not null && token is { Length: > 0 } && q.Answers.TryGetValue(token, out var a) ? a : null;

    private static string CleanWords(string? words)
    {
        var s = new string((words ?? "").Trim().Where(ch => !char.IsControl(ch)).ToArray());
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length > WordsMax ? s[..WordsMax] : s;
    }

    public bool IsBlocked(string text)
    {
        var lower = text.ToLowerInvariant();
        return BlockedWords.Any(w => w.Length > 0 && lower.Contains(w, StringComparison.Ordinal));
    }

    public PlayResults Results(PlayQuestion q)
    {
        var answers = q.Answers.Values.ToList();
        var counts = new int[q.Options.Count];
        foreach (var a in answers) foreach (var c in a.Choices) if (c >= 0 && c < counts.Length) counts[c]++;
        var scaleAverage = q.Kind == PlayQuestionKind.Scale && answers.Count > 0 ? answers.Average(a => a.Scale) : 0;
        var words = q.Kind == PlayQuestionKind.Words
            ? answers.GroupBy(a => a.Words.ToLowerInvariant()).Select(g => new KeyValuePair<string, int>(g.First().Words, g.Count())).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(30).ToList()
            : new List<KeyValuePair<string, int>>();
        var correct = answers.Count(a => a.Correct);
        return new PlayResults(q.Kind, answers.Count, counts, scaleAverage, words, correct);
    }

    /// <summary>The scale's spread — one count per step from min to max.</summary>
    public int[] ScaleCounts(PlayQuestion q)
    {
        var counts = new int[Math.Max(1, q.ScaleMax - q.ScaleMin + 1)];
        foreach (var a in q.Answers.Values) if (a.Scale >= q.ScaleMin && a.Scale <= q.ScaleMax) counts[a.Scale - q.ScaleMin]++;
        return counts;
    }

    public IReadOnlyList<PlayPlayer> Leaderboard(int count = 10)
        => _players.Values.Where(p => p.Score > 0).OrderByDescending(p => p.Score).ThenBy(p => p.JoinedUtc).Take(count).ToList();

    // ---- what the room writes --------------------------------------------------------------

    /// <summary>Free words from a phone, for the wall's message — into the queue (or out, by the list).</summary>
    public ModerationItem? Say(string? token, string? text)
    {
        var p = Find(token);
        var clean = CleanWords(text);
        if (p is null || clean.Length == 0) return null;
        p.LastSeenUtc = UtcNow();
        var item = new ModerationItem { Token = p.Token, Nick = p.Nick, Text = clean, AtUtc = UtcNow() };
        if (IsBlocked(clean)) { item.State = ModerationState.Out; item.Note = "the word list"; }
        _queue.Add(item);
        if (_queue.Count > 500) _queue.RemoveAt(0);
        Bump();
        return item;
    }

    /// <summary>The assistant's word on an item: fine, doubtful (the host decides) or out.</summary>
    public bool Mark(string id, ModerationState state, string note)
    {
        var item = _queue.FirstOrDefault(m => m.Id == id);
        if (item is null || item.State != ModerationState.Waiting) return false;
        item.AskedAssistant = true;
        if (state == ModerationState.Doubtful) { item.Note = note; Bump(); return true; }
        item.State = state;
        item.Note = note;
        if (state == ModerationState.Fine) Land(item);
        Bump();
        return true;
    }

    /// <summary>The host's press: the item is fine (a word lands in its cloud) or out.</summary>
    public bool Approve(string id) => Decide(id, ModerationState.Fine, "the host");

    public bool Reject(string id) => Decide(id, ModerationState.Out, "the host");

    public int ApproveAll()
    {
        var n = 0;
        foreach (var item in _queue.Where(m => m.State == ModerationState.Waiting).ToList()) if (Approve(item.Id)) n++;
        return n;
    }

    private bool Decide(string id, ModerationState state, string note)
    {
        var item = _queue.FirstOrDefault(m => m.Id == id);
        if (item is null || item.State is ModerationState.Fine or ModerationState.Out) return false;
        item.State = state;
        item.Note = note;
        if (state == ModerationState.Fine) Land(item);
        Bump();
        return true;
    }

    private void Land(ModerationItem item)
    {
        if (item.QuestionId.Length == 0) return;
        var q = FindQuestion(item.QuestionId);
        if (q is null) return;
        q.Answers[item.Token] = new PlayAnswer(item.Token, item.Nick, Array.Empty<int>(), 0, item.Text, item.AtUtc, 0, false);
    }

    /// <summary>What the host may put on the wall: the approved shouts, newest first.</summary>
    public IReadOnlyList<ModerationItem> Approved(int count = 20)
        => _queue.Where(m => m.State == ModerationState.Fine && m.QuestionId.Length == 0).OrderByDescending(m => m.AtUtc).Take(count).ToList();

    public IReadOnlyList<ModerationItem> Waiting() => _queue.Where(m => m.State == ModerationState.Waiting).ToList();

    // ---- messages back ----------------------------------------------------------------------

    /// <summary>A message to the room, a group or one phone (by its nickname): "room", "group:Table 4", "phone:Sam".</summary>
    public PlayMessage? Send(string to, string text)
    {
        var clean = CleanWords(text);
        if (clean.Length == 0) return null;
        var target = (to ?? "room").Trim();
        if (target.StartsWith("phone:", StringComparison.OrdinalIgnoreCase))
        {
            var who = target[6..].Trim();
            var p = Find(who) ?? FindByNick(who);
            if (p is null) return null;
            target = "phone:" + p.Token;
        }
        else if (target.StartsWith("group:", StringComparison.OrdinalIgnoreCase)) target = "group:" + CleanNick(target[6..]);
        else target = "room";
        var m = new PlayMessage(++_seq, PlayQuestion.NewId(), target, clean, UtcNow());
        _messages.Add(m);
        if (_messages.Count > 200) _messages.RemoveAt(0);
        Bump();
        return m;
    }

    /// <summary>A phone's messages after a sequence number: the room's, its group's, its own.</summary>
    public IReadOnlyList<PlayMessage> Inbox(string? token, long afterSeq)
    {
        var p = Find(token);
        if (p is null) return Array.Empty<PlayMessage>();
        return _messages.Where(m => m.Seq > afterSeq && m.IsFor(p)).ToList();
    }

    // ---- the room as a whole ----------------------------------------------------------------

    /// <summary>A new room: the questions kept as drafts, the answers, scores, queue and messages gone; a new code sends every phone back to the door.</summary>
    public void Reset(bool newCode, Random rng)
    {
        foreach (var q in _questions)
        {
            q.State = PlayQuestionState.Draft;
            q.OpenedUtc = q.ClosedUtc = null;
            q.Answers.Clear();
        }
        _queue.Clear();
        _messages.Clear();
        foreach (var p in _players.Values) p.Score = 0;
        Draughts = Draughts.New();
        Path.Reset();
        if (newCode)
        {
            Code = NewCode(rng);
            _players.Clear();
        }
        Bump();
    }

    /// <summary>Everything, for the host's export after the show — nicknames, never tokens.</summary>
    public string ExportJson()
    {
        var export = new
        {
            room = Code,
            show = Show,
            createdUtc = CreatedUtc,
            players = _players.Values.Select(p => new { p.Nick, p.Group, p.Score, p.JoinedUtc }).ToArray(),
            questions = _questions.Select(q => new
            {
                q.Id, kind = q.KindWord, q.Text, q.Options, q.Correct, q.TimeLimitSeconds, state = q.State.ToString().ToLowerInvariant(), q.OpenedUtc, q.ClosedUtc,
                answers = q.Answers.Values.Select(a => new { a.Nick, a.Choices, a.Scale, a.Words, a.AtUtc, a.Points, a.Correct }).ToArray(),
            }).ToArray(),
            queue = _queue.Select(m => new { m.Nick, m.Text, state = m.State.ToString().ToLowerInvariant(), m.Note, m.AtUtc }).ToArray(),
            messages = _messages.Select(m => new { to = m.ToWords, m.Text, m.SentUtc }).ToArray(),
            path = Path.History,
        };
        return JsonSerializer.Serialize(export, JsonUtil.Options);
    }
}
