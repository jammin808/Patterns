using System.Text.Json;
using System.Text.Json.Serialization;

namespace Patterns.Core.Services;

/// <summary>One change to what the audience sees (or hears), with who caused it.</summary>
public sealed record ShowLogEntry(
    DateTime AtUtc,
    string Origin,
    string Kind,
    string Target,
    string Outcome,
    string Message)
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

    /// <summary>Raised on the recording thread after a line is appended (UI thread in the app).</summary>
    public event Action<ShowLogEntry>? Recorded;

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
        Recorded?.Invoke(entry);
    }

    public void Record(string origin, string kind, string target, string outcome, string message = "")
        => Record(new ShowLogEntry(DateTime.UtcNow, origin, kind, target, outcome, message));

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

    private void RotateIfLarge()
    {
        var info = new FileInfo(Path);
        if (!info.Exists || info.Length < RotateAtBytes) return;
        File.Move(Path, Path + ".1", overwrite: true);
    }
}
