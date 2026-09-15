using Patterns.Rendering;
using SkiaSharp;

namespace Patterns.App.Rendering;

/// <summary>
/// While a calibration runs, every output shows the structured light instead of the show: the one
/// projector being read shows its pattern, the rest black. One place, read by every pipeline on
/// every frame, set by the calibration service between frames; nothing in the show model moves.
/// </summary>
public static class CalibrationOverlay
{
    private static readonly object Gate = new();
    private static bool _active;
    private static string _screenId = "";
    private static CalPattern _pattern;
    private static bool _hasPattern;

    public static bool Active
    {
        get { lock (Gate) return _active; }
    }

    /// <summary>The calibration begins: every output goes black until a pattern is shown.</summary>
    public static void Begin()
    {
        lock (Gate)
        {
            _active = true;
            _screenId = "";
            _hasPattern = false;
        }
    }

    /// <summary>One output shows one pattern; the rest stay black.</summary>
    public static void Show(string screenId, in CalPattern pattern)
    {
        lock (Gate)
        {
            _active = true;
            _screenId = screenId;
            _pattern = pattern;
            _hasPattern = true;
        }
    }

    /// <summary>The calibration is over: the outputs show the show again.</summary>
    public static void End()
    {
        lock (Gate)
        {
            _active = false;
            _screenId = "";
            _hasPattern = false;
        }
    }

    /// <summary>The pattern an output shows right now, or null for black.</summary>
    public static CalPattern? PatternFor(string? screenId)
    {
        lock (Gate)
        {
            return _active && _hasPattern && screenId is not null && screenId == _screenId ? _pattern : null;
        }
    }

    /// <summary>The pattern onto a raster: white stripes on black, exactly the pixels the code says.</summary>
    public static void Draw(SKCanvas canvas, in CalPattern pattern, int width, int height, SKPaint white)
    {
        switch (pattern.Step)
        {
            case CalStep.White:
                canvas.DrawRect(SKRect.Create(0, 0, width, height), white);
                break;
            case CalStep.Black:
                break;
            case CalStep.Column:
                foreach (var (start, length) in GrayCode.Runs(pattern, width)) canvas.DrawRect(SKRect.Create(start, 0, length, height), white);
                break;
            default:
                foreach (var (start, length) in GrayCode.Runs(pattern, height)) canvas.DrawRect(SKRect.Create(0, start, width, length), white);
                break;
        }
    }
}
