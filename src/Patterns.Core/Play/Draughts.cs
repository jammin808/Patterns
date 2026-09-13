namespace Patterns.Core.Play;

public enum DraughtSide
{
    None,
    Black,
    White,
}

/// <summary>
/// English draughts on the wall, two phones the pieces: men move forward on the dark squares, a
/// jump takes, a jump is taken when one is there (and goes on while it can), the far row crowns,
/// and a side with nothing to move has lost. Black (the dark pieces, the top rows) moves first.
/// </summary>
public sealed class Draughts
{
    private readonly sbyte[] _squares = new sbyte[64];
    private readonly Dictionary<DraughtSide, (string Token, string Nick)> _seats = new();

    public static Draughts New()
    {
        var d = new Draughts();
        d.Reset();
        return d;
    }

    public DraughtSide Turn { get; private set; } = DraughtSide.Black;
    public DraughtSide Winner { get; private set; }
    /// <summary>A piece mid-capture: the only one that may move, and only to jump again.</summary>
    public int? ContinueFrom { get; private set; }
    public int Moves { get; private set; }

    /// <summary>+1 a white man, +2 a white king, −1 a black man, −2 a black king, 0 empty; index row × 8 + column.</summary>
    public sbyte this[int index] => _squares[index];

    public static bool IsDark(int index) => (index / 8 + index % 8) % 2 == 1;

    public static DraughtSide SideOfPiece(sbyte piece) => piece > 0 ? DraughtSide.White : piece < 0 ? DraughtSide.Black : DraughtSide.None;

    public void Reset()
    {
        Array.Clear(_squares);
        for (var i = 0; i < 64; i++)
        {
            if (!IsDark(i)) continue;
            var row = i / 8;
            if (row < 3) _squares[i] = -1;
            else if (row > 4) _squares[i] = 1;
        }
        Turn = DraughtSide.Black;
        Winner = DraughtSide.None;
        ContinueFrom = null;
        Moves = 0;
    }

    public int Count(DraughtSide side) => _squares.Count(s => SideOfPiece(s) == side);

    public string NickOf(DraughtSide side) => _seats.TryGetValue(side, out var s) ? s.Nick : "";

    public DraughtSide SideOf(string? token)
    {
        if (string.IsNullOrEmpty(token)) return DraughtSide.None;
        foreach (var (side, seat) in _seats) if (seat.Token == token) return side;
        return DraughtSide.None;
    }

    /// <summary>A phone takes a side — a free one, or the one it has; false when another phone holds it.</summary>
    public bool Seat(string token, string nick, DraughtSide side)
    {
        if (side == DraughtSide.None || string.IsNullOrEmpty(token)) return false;
        if (_seats.TryGetValue(side, out var held) && held.Token != token) return false;
        var mine = SideOf(token);
        if (mine != DraughtSide.None && mine != side) _seats.Remove(mine);
        _seats[side] = (token, nick);
        return true;
    }

    public void Leave(string token)
    {
        var side = SideOf(token);
        if (side != DraughtSide.None) _seats.Remove(side);
    }

    /// <summary>The legal moves for a side: every jump when one is there, else every step; a piece mid-capture jumps alone.</summary>
    public IReadOnlyList<(int From, int To, int Over)> Legal(DraughtSide side)
    {
        var jumps = new List<(int, int, int)>();
        var steps = new List<(int, int, int)>();
        for (var i = 0; i < 64; i++)
        {
            var piece = _squares[i];
            if (SideOfPiece(piece) != side) continue;
            if (ContinueFrom is { } only && only != i) continue;
            var row = i / 8;
            var col = i % 8;
            foreach (var (dr, dc) in Directions(piece))
            {
                var r1 = row + dr;
                var c1 = col + dc;
                if (r1 < 0 || r1 > 7 || c1 < 0 || c1 > 7) continue;
                var j = r1 * 8 + c1;
                if (_squares[j] == 0)
                {
                    if (ContinueFrom is null) steps.Add((i, j, -1));
                    continue;
                }
                if (SideOfPiece(_squares[j]) == side) continue;
                var r2 = row + 2 * dr;
                var c2 = col + 2 * dc;
                if (r2 < 0 || r2 > 7 || c2 < 0 || c2 > 7) continue;
                var k = r2 * 8 + c2;
                if (_squares[k] == 0) jumps.Add((i, k, j));
            }
        }
        return jumps.Count > 0 ? jumps : steps;
    }

    private static IEnumerable<(int, int)> Directions(sbyte piece)
    {
        if (piece == 2 || piece == -2)
        {
            yield return (-1, -1); yield return (-1, 1); yield return (1, -1); yield return (1, 1);
        }
        else if (piece == 1)
        {
            yield return (-1, -1); yield return (-1, 1);          // white climbs
        }
        else if (piece == -1)
        {
            yield return (1, -1); yield return (1, 1);            // black descends
        }
    }

    /// <summary>A move by the phone that holds the side to move: "ok", or why not.</summary>
    public string Move(string? token, int from, int to)
    {
        if (Winner != DraughtSide.None) return "the game is over";
        var side = SideOf(token);
        if (side == DraughtSide.None) return "take a side first";
        if (side != Turn) return "not your turn";
        if (from < 0 || from > 63 || to < 0 || to > 63) return "off the board";
        var legal = Legal(side);
        var move = legal.FirstOrDefault(m => m.From == from && m.To == to);
        if (move == default && !(legal.Count > 0 && legal[0].From == from && legal[0].To == to)) return legal.Any(m => m.Over >= 0) ? "a jump is there — take it" : "not a move";
        var piece = _squares[from];
        _squares[to] = piece;
        _squares[from] = 0;
        Moves++;
        var crowned = false;
        if (piece == 1 && to / 8 == 0) { _squares[to] = 2; crowned = true; }
        if (piece == -1 && to / 8 == 7) { _squares[to] = -2; crowned = true; }
        if (move.Over >= 0)
        {
            _squares[move.Over] = 0;
            if (!crowned)
            {
                ContinueFrom = to;
                if (Legal(side).Count > 0) return "ok — jump again";
            }
        }
        ContinueFrom = null;
        Turn = side == DraughtSide.Black ? DraughtSide.White : DraughtSide.Black;
        if (Count(Turn) == 0 || Legal(Turn).Count == 0) Winner = side;
        return "ok";
    }

    public string Words
    {
        get
        {
            if (Winner != DraughtSide.None) return $"{Winner} wins{(NickOf(Winner).Length > 0 ? $" — {NickOf(Winner)}" : "")} · {Count(DraughtSide.Black)} v {Count(DraughtSide.White)}";
            var who = NickOf(Turn);
            return $"{Turn} to move{(who.Length > 0 ? $" ({who})" : "")}{(ContinueFrom is not null ? " — jump again" : "")} · {Count(DraughtSide.Black)} v {Count(DraughtSide.White)}";
        }
    }
}
