using System.Text.Json;
using System.Text.Json.Serialization;

namespace Patterns.Core.Services;

/// <summary>
/// One change to what the audience sees (or hears), with who caused it. Round 79: <paramref name="Visibility"/>
/// (OutputsLive / OutputsOff / Blackout) and <paramref name="Effect"/> (Changed / OwnOnly / Nothing) as the
/// executor stamped them — fields, so a report reads them without parsing the words; absent on rows written
/// before round 79 and on rows nothing stamped, and left out of the JSON then.
/// </summary>
public sealed record ShowLogEntry(
    DateTime AtUtc,
    string Origin,
    string Kind,
    string Target,
    string Outcome,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Visibility = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Effect = null)
{
    [JsonIgnore]
    public DateTime AtLocal => AtUtc.ToLocalTime();
}

/// <summary>
/// The show journal: an append-only JSON-lines file beside the settings that records every
/// air change with its origin. It survives a crash, feeds the history a caller reads after a
/// relaunch, and gives the client a post-show report. Never throws — a journal that could
/// stop a show would be worse than no journal.
/// </summary>
public sealed class ShowLog
{
    public const string FileName = "patterns.showlog.jsonl";
    private const long RotateAtBytes = 4L * 1024 * 1024;

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Converters = { new TolerantEnumConverterFactory() },
    };

    private readonly object _gate = new();

    public ShowLog(string directory) => Path = System.IO.Path.Combine(directory, FileName);

    public string Path { get; }

    public void Record(ShowLogEntry entry)
    {
        try
        {
            lock (_gate)
            {
                RotateIfLarge();
                File.AppendAllText(Path, JsonSerializer.Serialize(entry, Compact) + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Show log write failed.", ex);
        }
    }

    public void Record(string origin, string kind, string target, string outcome, string message = "", string? visibility = null, string? effect = null)
        => Record(new ShowLogEntry(DateTime.UtcNow, origin, kind, target, outcome, message, visibility, effect));

    /// <summary>The most recent entries, oldest first. Unreadable lines are skipped.</summary>
    public IReadOnlyList<ShowLogEntry> Tail(int count)
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(Path)) return Array.Empty<ShowLogEntry>();
                var lines = File.ReadAllLines(Path);
                var result = new List<ShowLogEntry>(Math.Min(count, lines.Length));
                for (var i = lines.Length - 1; i >= 0 && result.Count < count; i--)
                {
                    if (lines[i].Length == 0) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<ShowLogEntry>(lines[i], Compact);
                        if (entry is not null) result.Add(entry);
                    }
                    catch
                    {
                        // A torn last line after a crash is expected; skip it, keep counting.
                    }
                }
                result.Reverse();
                return result;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Show log read failed.", ex);
            return Array.Empty<ShowLogEntry>();
        }
    }

    /// <summary>
    /// Round 76: rows an older build wrote with the admin passcode as the target of RESTART or UPDATE
    /// APPLY (the row keeps the kind and the outcome and blanks the target since round 75) are blanked
    /// in the file and its rotated half, once, at boot. A quick scan first, so a journal with nothing
    /// to scrub costs one read; a rewrite goes through a temp file moved over the old one. Returns the
    /// rows blanked; never throws.
    /// </summary>
    public int ScrubSecrets()
    {
        var total = 0;
        try
        {
            lock (_gate)
            {
                foreach (var file in new[] { Path, Path + ".1" })
                {
                    total += ScrubFile(file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Show log scrub failed.", ex);
        }
        return total;
    }

    /// <summary>Whether a row's kind is one whose target was the admin passcode.</summary>
    public static bool KindCarriesSecret(string kind)
        => Enum.TryParse<Model.ShowActionKind>(kind, ignoreCase: false, out var k) && ActionSpec.CarriesSecret(k);

    private static int ScrubFile(string file)
    {
        if (!File.Exists(file)) return 0;
        var text = File.ReadAllText(file);
        if (!text.Contains("\"Kind\":\"Restart\"", StringComparison.Ordinal) && !text.Contains("\"Kind\":\"UpdateApply\"", StringComparison.Ordinal)) return 0;
        var lines = text.Split('\n');
        var changed = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            ShowLogEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<ShowLogEntry>(line, Compact);
            }
            catch (JsonException)
            {
                continue;   // a torn line stays as it is
            }
            if (entry is null || entry.Target.Length == 0 || !KindCarriesSecret(entry.Kind)) continue;
            lines[i] = JsonSerializer.Serialize(entry with { Target = "" }, Compact);
            changed++;
        }
        if (changed == 0) return 0;
        AtomicFile.WriteAllText(file, string.Join('\n', lines));
        Log.Info($"Show log: {changed} old row{(changed == 1 ? "" : "s")} carrying an admin passcode blanked in {System.IO.Path.GetFileName(file)}.");
        return changed;
    }

    private void RotateIfLarge()
    {
        var info = new FileInfo(Path);
        if (!info.Exists || info.Length < RotateAtBytes) return;
        File.Move(Path, Path + ".1", overwrite: true);
    }
}
