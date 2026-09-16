using System.Text.Json;
using Patterns.Core.Services;

namespace Patterns.Core.Arcade;

/// <summary>One high score: the game, the score, three initials or a pad's name, when, and the show it was set in.</summary>
public sealed record ScoreEntry(string Id, string Game, int Score, string Name, DateTime WhenUtc, string Show);

/// <summary>
/// The arcade's board: the top fifty per game, kept as a file in the node's folder and exportable;
/// initials arcade style, entered after the match by the pad or by <c>ARCADE NAME</c> on the wire.
/// Pure: the node reads and writes the file, this decides what is on it.
/// </summary>
public sealed class Leaderboard
{
    public const int KeepPerGame = 50;

    private readonly List<ScoreEntry> _entries = new();

    public IReadOnlyList<ScoreEntry> Entries => _entries;

    /// <summary>The last score added — the one initials name.</summary>
    public string LastId { get; private set; } = "";

    /// <summary>Adds the score; its rank on that game's board (1 = the best), or 0 when it did not make the fifty.</summary>
    public int Add(string game, int score, string name, DateTime whenUtc, string show)
    {
        var entry = new ScoreEntry(Guid.NewGuid().ToString("N")[..12], game, score, name, whenUtc, show);
        _entries.Add(entry);
        var board = _entries.Where(e => e.Game == game).OrderByDescending(e => e.Score).ThenBy(e => e.WhenUtc).ToList();
        foreach (var gone in board.Skip(KeepPerGame)) _entries.Remove(gone);
        var rank = board.IndexOf(entry) + 1;
        if (rank > KeepPerGame) rank = 0;
        LastId = rank > 0 ? entry.Id : "";
        return rank;
    }

    /// <summary>The best <paramref name="count"/> on a game, best first.</summary>
    public IReadOnlyList<ScoreEntry> Top(string game, int count = 5)
        => _entries.Where(e => e.Game == game).OrderByDescending(e => e.Score).ThenBy(e => e.WhenUtc).Take(count).ToList();

    /// <summary>Names an entry — the last one added when <paramref name="id"/> is empty: three letters, arcade style, longer allowed.</summary>
    public bool Name(string? id, string initials)
    {
        var target = string.IsNullOrEmpty(id) ? LastId : id;
        var i = _entries.FindIndex(e => e.Id == target);
        if (i < 0) return false;
        var clean = new string((initials ?? "").Trim().Where(ch => !char.IsControl(ch)).Take(12).ToArray()).ToUpperInvariant();
        if (clean.Length == 0) return false;
        _entries[i] = _entries[i] with { Name = clean };
        return true;
    }

    public string Json() => JsonSerializer.Serialize(_entries, JsonUtil.Options);

    /// <summary>The board from its file; an unreadable file is an empty board, never a crash on boot.</summary>
    public static Leaderboard Parse(string? json)
    {
        var board = new Leaderboard();
        if (string.IsNullOrWhiteSpace(json)) return board;
        try
        {
            var list = JsonSerializer.Deserialize<List<ScoreEntry>>(json, JsonUtil.Options);
            if (list is not null) board._entries.AddRange(list.Where(e => e is not null && !string.IsNullOrEmpty(e.Game)));
        }
        catch (JsonException)
        {
            // a corrupt board reads as empty; the next score rewrites the file
        }
        return board;
    }
}
