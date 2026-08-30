using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>A resolved playlist entry (explicit item or a file found in a folder).</summary>
public sealed record PlaylistEntry(string Path, bool IsVideo, double DurationSeconds, string ScheduledTime, double ScheduledDurationSeconds);

/// <summary>
/// Pure playlist ordering and advance logic — the app service feeds it wall time, resolved
/// file lists and "video ended" signals; it decides what plays. Deterministic for a given
/// options/seed, so it is fully unit tested.
/// </summary>
public sealed class PlaylistSequencer
{
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif" };
    public static readonly string[] VideoExtensions = { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".mpg", ".mpeg", ".wmv" };
    public static readonly string[] AudioExtensions = { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma", ".aiff", ".aif" };

    private List<PlaylistEntry> _order = new();
    private int _index = -1;
    private DateTime _itemStartedUtc;
    private double _itemDuration;
    private PlaylistEntry? _scheduledOverride;
    private DateTime _overrideEndsUtc;
    private readonly Dictionary<string, DateTime> _scheduleFired = new();

    public PlaylistEntry? Current => _scheduledOverride ?? (_index >= 0 && _index < _order.Count ? _order[_index] : null);
    public int CurrentIndex => _scheduledOverride is not null ? -1 : _index;
    public int Count => _order.Count;
    public DateTime ItemStartedUtc => _scheduledOverride is not null ? _overrideStartUtc : _itemStartedUtc;
    public double ItemDurationSeconds => _scheduledOverride is not null ? (_overrideEndsUtc - _overrideStartUtc).TotalSeconds : _itemDuration;
    private DateTime _overrideStartUtc;

    public static bool IsVideoPath(string path)
        => VideoExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    public static bool IsAudioPath(string path)
        => AudioExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Media that needs the libVLC decoder and plays to a natural end (video or audio).</summary>
    public static bool IsDecodedPath(string path) => IsVideoPath(path) || IsAudioPath(path);

    public static bool IsMediaPath(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext) || AudioExtensions.Contains(ext);
    }

    /// <summary>Moves legacy flat items/folders into a first section; guarantees at least one section.</summary>
    public static void Normalize(PlaylistOptions options)
    {
        if (options.Sections.Count == 0)
        {
            var section = new PlaylistSectionConfig
            {
                Name = options.Items.Count > 0 || options.Folders.Count > 0 ? "Main" : "Part 1",
            };
            foreach (var item in options.Items) section.Items.Add(item);
            foreach (var folder in options.Folders) section.Folders.Add(folder);
            options.Sections.Add(section);
            options.Items.Clear();
            options.Folders.Clear();
        }
        if (options.ActiveSection >= options.Sections.Count)
        {
            options.ActiveSection = options.Sections.Count - 1;
        }
    }

    /// <summary>The section on air (normalizing legacy lists on the way).</summary>
    public static PlaylistSectionConfig ActiveSectionOf(PlaylistOptions options)
    {
        Normalize(options);
        return options.Sections[Math.Clamp(options.ActiveSection, 0, options.Sections.Count - 1)];
    }

    /// <summary>Every item across every section (scheduled interruptions fire from any part).</summary>
    public static IEnumerable<PlaylistItemConfig> AllItems(PlaylistOptions options)
        => options.Sections.SelectMany(s => s.Items).Concat(options.Items);

    private readonly Dictionary<int, DateTime> _sectionStartFired = new();

    /// <summary>Section due to take over at this minute (once per day each), or null.</summary>
    public int? SectionDue(PlaylistOptions options, DateTime localNow)
    {
        for (var i = 0; i < options.Sections.Count; i++)
        {
            var section = options.Sections[i];
            if (string.IsNullOrWhiteSpace(section.StartTime)) continue;
            if (!CountdownService.TryParseTime(section.StartTime, out var tod)) continue;
            if (localNow.Hour != tod.Hours || localNow.Minute != tod.Minutes) continue;
            if (_sectionStartFired.TryGetValue(i, out var fired) && fired.Date == localNow.Date) continue;
            _sectionStartFired[i] = localNow;
            return i;
        }
        return null;
    }

    /// <summary>
    /// Builds the play order for one section: explicit items first (custom order preserved),
    /// then folder files name-sorted; shuffle permutes everything deterministically by seed.
    /// Filters by kind and drops videos entirely when video playback is unavailable.
    /// </summary>
    public static List<PlaylistEntry> BuildOrder(
        PlaylistOptions options, IReadOnlyList<PlaylistItemConfig> items,
        IReadOnlyList<string> folderFiles, bool videoPlaybackAvailable)
    {
        var entries = new List<PlaylistEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Path) || !seen.Add(item.Path)) continue;
            entries.Add(new PlaylistEntry(item.Path, IsDecodedPath(item.Path), item.DurationSeconds,
                item.ScheduledTime, item.ScheduledDurationSeconds));
        }

        foreach (var file in folderFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsMediaPath(file) || !seen.Add(file)) continue;
            entries.Add(new PlaylistEntry(file, IsDecodedPath(file), 0, "", 0));
        }

        entries.RemoveAll(e => e.IsVideo ? !options.IncludeVideos || !videoPlaybackAvailable : !options.IncludeImages);

        if (options.Shuffle && entries.Count > 1)
        {
            var rng = new Random(options.ShuffleSeed);
            for (var i = entries.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (entries[i], entries[j]) = (entries[j], entries[i]);
            }
        }

        return entries;
    }

    /// <summary>Replaces the order; keeps playing the current path when it survives the rebuild.</summary>
    public void SetOrder(List<PlaylistEntry> order, DateTime utcNow)
    {
        var currentPath = Current?.Path;
        _order = order;
        if (_scheduledOverride is not null) return;
        var idx = currentPath is null ? -1 : _order.FindIndex(e => string.Equals(e.Path, currentPath, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            _index = idx;
        }
        else
        {
            _index = _order.Count > 0 ? 0 : -1;
            StartItem(utcNow);
        }
    }

    private void StartItem(DateTime utcNow)
    {
        _itemStartedUtc = utcNow;
        _itemDuration = 0;
    }

    /// <summary>
    /// Advances the playlist. Returns true when the on-screen item changed.
    /// <paramref name="videoEnded"/> reports natural end of the current video;
    /// <paramref name="videoLengthSeconds"/> is the decoder-reported length when known.
    /// </summary>
    public bool Tick(PlaylistOptions options, DateTime localNow, DateTime utcNow, bool videoEnded, double videoLengthSeconds)
    {
        var changed = false;

        // Scheduled interruptions fire once per day at their minute — from any section.
        foreach (var item in AllItems(options))
        {
            if (string.IsNullOrWhiteSpace(item.ScheduledTime) || string.IsNullOrWhiteSpace(item.Path)) continue;
            if (!CountdownService.TryParseTime(item.ScheduledTime, out var tod)) continue;
            if (localNow.Hour != tod.Hours || localNow.Minute != tod.Minutes) continue;
            if (_scheduleFired.TryGetValue(item.Path, out var fired) && fired.Date == localNow.Date) continue;

            _scheduleFired[item.Path] = localNow;
            _scheduledOverride = new PlaylistEntry(item.Path, IsDecodedPath(item.Path), item.DurationSeconds,
                item.ScheduledTime, item.ScheduledDurationSeconds);
            _overrideStartUtc = utcNow;
            _overrideEndsUtc = utcNow.AddSeconds(Math.Max(1, item.ScheduledDurationSeconds));
            return true;
        }

        if (_scheduledOverride is not null)
        {
            if (utcNow >= _overrideEndsUtc || (_scheduledOverride.IsVideo && videoEnded))
            {
                _scheduledOverride = null;
                changed = true;
                StartItem(utcNow); // resume the cycle on the current item, restarted
            }
            else
            {
                return false;
            }
        }

        if (_order.Count == 0)
        {
            if (_index != -1)
            {
                _index = -1;
                changed = true;
            }
            return changed;
        }

        if (_index < 0)
        {
            _index = 0;
            StartItem(utcNow);
            return true;
        }

        var current = _order[_index];
        var due = false;
        if (current.IsVideo && options.VideoFullLength && current.DurationSeconds <= 0)
        {
            due = videoEnded;
        }
        else
        {
            var hold = current.DurationSeconds > 0
                ? current.DurationSeconds
                : current.IsVideo && videoLengthSeconds > 0 && options.VideoFullLength
                    ? videoLengthSeconds
                    : options.ImageDwellSeconds;
            _itemDuration = hold;
            due = (utcNow - _itemStartedUtc).TotalSeconds >= hold;
        }

        if (due)
        {
            _index = (_index + 1) % _order.Count;
            StartItem(utcNow);
            changed = true;
        }

        return changed;
    }
}
