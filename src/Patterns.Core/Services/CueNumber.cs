using System.Globalization;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// Cue numbers are labels ("03.020": section 3, step 20). Auto-assigned on insert, stepping by
/// ten, editable as text, compared numerically for display checks and remotes — never used to
/// sort the list, whose order is the truth.
/// </summary>
public static class CueNumber
{
    public const int Step = 10;

    /// <summary>(section, step) or null when the text is not a number.</summary>
    public static (int Section, int Step)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        var dot = s.IndexOf('.');
        if (dot < 0)
        {
            return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var whole) ? (whole, 0) : null;
        }
        var a = s[..dot];
        var b = s[(dot + 1)..];
        if (!int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var section)) return null;
        if (!int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var step)) return null;
        return (section, step);
    }

    public static string Format(int section, int step) => $"{section:00}.{step:000}";

    /// <summary>Numeric order; unparseable numbers sort last, then by text.</summary>
    public static int Compare(string? a, string? b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        if (pa is null && pb is null) return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        if (pa is null) return 1;
        if (pb is null) return -1;
        var c = pa.Value.Section.CompareTo(pb.Value.Section);
        return c != 0 ? c : pa.Value.Step.CompareTo(pb.Value.Step);
    }

    /// <summary>The number after a cue: the same section, ten steps on ("01.010" when there is no previous cue).</summary>
    public static string Next(string? previous)
    {
        var p = Parse(previous);
        if (p is null) return Format(1, Step);
        return Format(p.Value.Section, p.Value.Step + Step);
    }

    /// <summary>A number between two cues when one fits, else the next after the previous.</summary>
    public static string Between(string? previous, string? next)
    {
        var p = Parse(previous);
        var n = Parse(next);
        if (p is null) return n is null ? Format(1, Step) : Format(n.Value.Section, Math.Max(1, n.Value.Step - Step));
        if (n is not null && n.Value.Section == p.Value.Section && n.Value.Step - p.Value.Step >= 2)
        {
            return Format(p.Value.Section, p.Value.Step + (n.Value.Step - p.Value.Step) / 2);
        }
        return Next(previous);
    }

    /// <summary>Renumbers a whole list in order: 01.010, 01.020, … keeping each cue's section when it has one.</summary>
    public static void Renumber(IList<RunCueConfig> cues)
    {
        var section = 1;
        var step = 0;
        var first = true;
        foreach (var cue in cues)
        {
            var parsed = Parse(cue.Number);
            if (parsed is { } p && (first || p.Section > section))
            {
                section = p.Section;
                step = 0;
            }
            first = false;
            step += Step;
            cue.Number = Format(section, step);
        }
    }

    /// <summary>Numbers that repeat or run backwards — worth a warning, never a re-sort.</summary>
    public static List<string> Warnings(IList<RunCueConfig> cues)
    {
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        foreach (var cue in cues)
        {
            if (!seen.Add(cue.Number)) warnings.Add($"Cue number {cue.Number} is used twice.");
            if (previous is not null && Compare(previous, cue.Number) > 0)
            {
                warnings.Add($"Cue {cue.Number} runs after {previous} — numbers are out of order (the list order still counts).");
            }
            previous = cue.Number;
        }
        return warnings;
    }
}
