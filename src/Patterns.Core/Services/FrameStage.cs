using Patterns.Core.Model;

namespace Patterns.Core.Services;

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
