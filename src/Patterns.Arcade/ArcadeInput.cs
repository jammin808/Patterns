using Patterns.Core.Arcade;
namespace Patterns.Arcade;

/// <summary>A pad's buttons: a d-pad, two buttons and START — a keyboard, an XInput pad, a Companion key or a phone's touch pad all end here.</summary>
[Flags]
public enum PadButtons : byte
{
    None = 0,
    Up = 1,
    Down = 2,
    Left = 4,
    Right = 8,
    A = 16,
    B = 32,
    Start = 64,
}

/// <summary>What one pad holds at a simulation step.</summary>
public struct PadState
{
    public PadButtons Buttons;

    public readonly bool Has(PadButtons b) => (Buttons & b) != 0;

    /// <summary>A button by its word on the wire or a page: UP, DOWN, LEFT, RIGHT, A (FIRE), B, START.</summary>
    public static PadButtons ParseButton(string? word) => (word ?? "").Trim().ToUpperInvariant() switch
    {
        "UP" or "U" => PadButtons.Up,
        "DOWN" or "D" => PadButtons.Down,
        "LEFT" or "L" => PadButtons.Left,
        "RIGHT" or "R" => PadButtons.Right,
        "A" or "FIRE" or "SPACE" or "SERVE" => PadButtons.A,
        "B" => PadButtons.B,
        "START" or "ENTER" or "GO" => PadButtons.Start,
        _ => PadButtons.None,
    };
}

/// <summary>
/// The match's own random source — xorshift64*, seeded per match — so the same seed and the same
/// presses give the same game on any build: the attract mode is a replay, a bug report is a seed
/// and a recording.
/// </summary>
public sealed class ArcadeRandom
{
    private ulong _s;

    public ArcadeRandom(long seed)
    {
        _s = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0xD1B54A32D192ED03UL);
        if (_s == 0) _s = 0x2545F4914F6CDD1DUL;
    }

    public ulong NextRaw()
    {
        _s ^= _s >> 12;
        _s ^= _s << 25;
        _s ^= _s >> 27;
        return unchecked(_s * 0x2545F4914F6CDD1DUL);
    }

    public double NextDouble() => (NextRaw() >> 11) * (1.0 / (1UL << 53));

    public int Next(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(NextDouble() * maxExclusive);

    public float Range(float min, float max) => min + (float)NextDouble() * (max - min);

    public bool Chance(double p) => NextDouble() < p;
}

/// <summary>One pad's state at the step it changed — the recording a match can be replayed from.</summary>
public readonly record struct ArcadeInputEvent(long Step, byte Player, PadButtons Buttons);
