using Patterns.Core.Model;
using Patterns.Core.Rendering;

namespace Patterns.Core.Services;

/// <summary>
/// The one line a caller and an operator both trust, on the Run surface: each output's frame
/// rate, its p95 frame time in the last minute and the frames it dropped; the twin, in a word or
/// two; the screens the room is short; the last box that said no; where the day stands against
/// the plan; the lock. Facts only, from the services that own them, joined with a dot — a part
/// that has nothing to say says nothing, so a caller node's line is the twin and the plan alone.
/// Pure: the words are a test.
/// </summary>
public static class Glance
{
    /// <summary>"OUT 1 60 fps · p95 8.1 ms" — "· 3 dropped" when the last minute lost frames; the preview reads "PVW".</summary>
    public static string SinkWords(FrameBudgetReading r)
    {
        var name = r.Kind switch
        {
            SinkKind.Preview => "PVW",
            SinkKind.Output => $"OUT {r.SinkIndex}",
            SinkKind.Monitor => "MON",
            _ => r.Kind.ToString().ToUpperInvariant(),
        };
        if (r.FramesInWindow == 0) return $"{name} idle";
        var fps = r.Fps >= 0 ? $"{r.Fps:0} fps" : "measuring";
        var p95 = r.P95Ms >= 0 ? $" · p95 {r.P95Ms:0.0} ms" : "";
        var dropped = r.Missed > 0 ? $" · {r.Missed} dropped" : "";
        return $"{name} {fps}{p95}{dropped}";
    }

    /// <summary>The line: the sinks first (outputs before the preview), then the words each service gave, empty ones left out.</summary>
    public static string Line(IReadOnlyList<FrameBudgetReading> sinks, string twin, string screens, string device, string plan, string lockWords)
    {
        var parts = new List<string>();
        foreach (var r in sinks.Where(r => r.Kind == SinkKind.Output).OrderBy(r => r.SinkIndex)) parts.Add(SinkWords(r));
        foreach (var r in sinks.Where(r => r.Kind == SinkKind.Preview)) parts.Add(SinkWords(r));
        foreach (var part in new[] { twin, screens, device, plan, lockWords })
        {
            if (part.Length > 0) parts.Add(part);
        }
        return string.Join(" · ", parts);
    }

    /// <summary>"LATE +2:14" / "ON PLAN" / "" — the plan's word for the strip, from the offset text the timing already makes.</summary>
    public static string PlanWords(string offsetText, bool isLate)
        => offsetText.Length == 0 ? "" : isLate ? $"LATE {offsetText}" : $"ON PLAN {offsetText}";
}
