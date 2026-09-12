using System.Diagnostics;
using Patterns.Core.Model;

namespace Patterns.Core.Rendering;

/// <summary>
/// The areas of one frame the engine times: which of them took the longest is what the frame
/// budget names beside a slow frame, so "31 ms" reads "31 ms — the Fractal pattern" on the
/// Machine page and the super-check, and the operator knows what to lower.
/// </summary>
public static class FrameStage
{
    public const string Pattern = "pattern";
    public const string Layers = "layers";
    public const string Overlays = "overlays";
    public const string LowerThird = "lower third";
    public const string Viewport = "viewport overlays";
    public const string Fade = "fade";
    public const string Wall = "wall";
    public const string Freeze = "freeze";

    private static readonly string[] PatternNames = BuildPatternNames();

    private static string[] BuildPatternNames()
    {
        var kinds = Enum.GetValues<PatternKind>();
        var names = new string[kinds.Max(k => (int)k) + 1];
        foreach (var k in kinds) names[(int)k] = Pattern + ":" + k;
        return names;
    }

    /// <summary>The pattern stage, named by the pattern it drew: "pattern:Fractal". Named once per kind — this is read on every frame of every sink.</summary>
    public static string PatternOf(PatternKind kind)
    {
        var i = (int)kind;
        return (uint)i < (uint)PatternNames.Length && PatternNames[i] is { } name ? name : Pattern + ":" + kind;
    }

    /// <summary>The stage in the desk's words: "the Fractal pattern", "the lower third", "the crossfade".</summary>
    public static string Words(string stage)
    {
        if (stage.Length == 0) return "";
        if (stage.StartsWith(Pattern + ":", StringComparison.Ordinal)) return $"the {stage[(Pattern.Length + 1)..]} pattern";
        return stage switch
        {
            Layers => "the layers",
            Overlays => "the overlays",
            LowerThird => "the lower third",
            Viewport => "the chip and badges",
            Fade => "the crossfade",
            Wall => "the wall's strips",
            Freeze => "the frozen frame",
            _ => stage,
        };
    }
}

/// <summary>
/// One sink's stage timings for the frame being drawn: the engine notes each stage as it ends,
/// and the slowest one goes into the frame budget with the frame's time. Owned by the sink,
/// touched on its render thread only, nothing allocated per frame.
/// </summary>
public sealed class FrameStages
{
    /// <summary>The slowest stage of the frame so far; "" until one is noted.</summary>
    public string SlowestStage { get; private set; } = "";

    /// <summary>How long it took, ms; -1 until one is noted.</summary>
    public double SlowestMs { get; private set; } = -1;

    /// <summary>How many stages the frame noted (tests read it).</summary>
    public int Noted { get; private set; }

    /// <summary>The start of a top-level frame: nested draws (a layer's screen, a fade source) keep noting into it.</summary>
    public void Begin()
    {
        SlowestStage = "";
        SlowestMs = -1;
        Noted = 0;
    }

    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>A stage ended: how long since its start.</summary>
    public void Note(string stage, long startedAt) => Note(stage, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

    public void Note(string stage, double ms)
    {
        Noted++;
        if (ms > SlowestMs)
        {
            SlowestMs = ms;
            SlowestStage = stage;
        }
    }
}
