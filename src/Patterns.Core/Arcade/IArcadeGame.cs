using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Arcade;

/// <summary>The games' own space: every game simulates and draws in 1920×1080 units; the engine fits that to whatever size it renders at, so a match is the same match at any size.</summary>
public static class ArcadeStage
{
    public const float Width = 1920f;
    public const float Height = 1080f;
}

/// <summary>What the catalogue says of a game.</summary>
public sealed record ArcadeGameInfo(string Id, string Title, int MaxPlayers, string Blurb);

/// <summary>
/// One game: reset for a match, stepped at the engine's fixed rate with the pads as they are at
/// that step, drawn between two steps by the interpolation factor. Seats the pads do not own are
/// the house's — the game plays them itself — so a match with no one on the pads is the attract
/// mode, and a match with one player has an opponent.
/// </summary>
public interface IArcadeGame
{
    string Id { get; }
    string Title { get; }
    /// <summary>The pad's words for the attract screen: "▲ ▼ move · first to 7".</summary>
    string Blurb { get; }
    int MaxPlayers { get; }
    /// <summary>Seats in this match (human and house).</summary>
    int Seats { get; }
    bool IsOver { get; }
    /// <summary>The seat that won, 1-based; 0 when nobody did (a solo game, a draw).</summary>
    int Winner { get; }
    IReadOnlyList<int> Scores { get; }
    /// <summary>Whether a seat is a person on a pad (else the house).</summary>
    bool IsHuman(int seat);
    /// <summary>"PONG — 3 : 5", the status line's words.</summary>
    string Words { get; }
    /// <summary>A hint for the engine's difficulty after a match: +1 the people won easily, −1 the house did, 0 a fair one.</summary>
    int DifficultyHint { get; }
    /// <summary>A match: which seats are people (none = the house plays itself), the match's random source, the difficulty 0..1.</summary>
    void Reset(ReadOnlySpan<bool> humans, ArcadeRandom rng, float difficulty);
    /// <summary>One fixed step: the pads as they are, the step's length, the step's number.</summary>
    void Step(ReadOnlySpan<PadState> pads, float dt, long step);
    /// <summary>The picture between the last two steps; <paramref name="alpha"/> 0..1 is how far into the next step the frame is.</summary>
    void Draw(SKCanvas c, float alpha, PaintCache paints);
}

/// <summary>Text the games share: the one font, sized and coloured per call, no allocation.</summary>
public static class ArcadeText
{
    public static void Draw(SKCanvas c, PaintCache p, string s, float x, float y, float size, SKColor colour, SKTextAlign align = SKTextAlign.Center, bool bold = true)
    {
        var font = bold ? p.FontBold : p.FontRegular;
        font.Size = size;
        c.DrawText(s, x, y, align, font, p.Text(colour));
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
