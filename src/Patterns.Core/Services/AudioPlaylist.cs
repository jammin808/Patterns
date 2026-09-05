using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The audio playlist's rules, pure: the order the player runs (the rows, then the folders'
/// files, shuffled by a seed when asked, one file written before the list existed as a fallback),
/// stepping through it, finding a track by number, id, name or file, the words a row reads, and
/// the migration of the old single track. The app's player owns the devices; this owns the list.
/// </summary>
public static class AudioPlaylist
{
    /// <summary>A folder is read this far — a music library dropped in whole never stalls the desk.</summary>
    public const int MaxFolderFiles = 2000;

    /// <summary>Something could play: a row with a file, a folder, or the old single track.</summary>
    public static bool HasTracks(AudioPlayerConfig cfg)
        => cfg.Items.Any(i => i.Path.Length > 0) || cfg.Folders.Any(f => !string.IsNullOrWhiteSpace(f)) || cfg.Path.Length > 0;

    /// <summary>
    /// The audio files under the folders, each folder's in name order, through the enumerator the
    /// caller provides (the app's file system; a test's list). A folder that cannot be read adds
    /// nothing; the count is capped.
    /// </summary>
    public static List<string> AudioFilesIn(IEnumerable<string> folders, Func<string, IEnumerable<string>> enumerate)
    {
        var files = new List<string>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            List<string> found;
            try
            {
                found = enumerate(folder).Where(PlaylistSequencer.IsAudioPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch
            {
                continue;
            }
            foreach (var f in found)
            {
                files.Add(f);
                if (files.Count >= MaxFolderFiles) return files;
            }
        }
        return files;
    }

    /// <summary>
    /// The order the player runs: the rows in the list's order, then the folders' files; the same
    /// file never twice (case-blind); the old single track when the list is empty; shuffled by the
    /// seed when asked — the same shuffle every time until the seed changes.
    /// </summary>
    public static List<string> BuildOrder(AudioPlayerConfig cfg, IReadOnlyList<string> folderFiles)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in cfg.Items)
        {
            if (item.Path.Length > 0 && seen.Add(item.Path)) order.Add(item.Path);
        }
        foreach (var f in folderFiles)
        {
            if (seen.Add(f)) order.Add(f);
        }
        if (order.Count == 0 && cfg.Path.Length > 0) order.Add(cfg.Path);
        if (cfg.Shuffle && order.Count > 1) Shuffle(order, cfg.ShuffleSeed);
        return order;
    }

    private static void Shuffle(List<string> order, int seed)
    {
        var rng = new Random(seed);
        for (var i = order.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
    }

    /// <summary>Changes when the order would: the rows, the folders' files, the shuffle and its seed, the old track.</summary>
    public static string OrderKey(AudioPlayerConfig cfg, IReadOnlyList<string> folderFiles)
        => $"{string.Join('|', cfg.Items.Select(i => i.Path))}#{string.Join('|', folderFiles)}#{cfg.Shuffle}#{cfg.ShuffleSeed}#{cfg.Path}";

    /// <summary>The next place after a step; null at an end without loop (the same arithmetic as the clicker list).</summary>
    public static int? Step(int current, int count, int delta, bool loop) => PresenterLogic.Advance(current, count, delta, loop);

    public static int IndexOf(IReadOnlyList<string> order, string path)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], path, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>
    /// A track's place in the order by its number (1-based), a row's id, a row's name, or a file's
    /// name with or without its extension — case-blind; -1 when none.
    /// </summary>
    public static int Find(AudioPlayerConfig cfg, IReadOnlyList<string> order, string target)
    {
        var t = (target ?? "").Trim();
        if (t.Length == 0) return -1;
        if (int.TryParse(t, out var n)) return n >= 1 && n <= order.Count ? n - 1 : -1;
        var item = FindItem(cfg, t);
        if (item is not null)
        {
            var idx = IndexOf(order, item.Path);
            if (idx >= 0) return idx;
        }
        for (var i = 0; i < order.Count; i++)
        {
            var file = System.IO.Path.GetFileName(order[i]);
            if (string.Equals(file, t, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(System.IO.Path.GetFileNameWithoutExtension(file), t, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>A row by its id, its name, or its file's name (with or without the extension); null when none.</summary>
    public static AudioTrackConfig? FindItem(AudioPlayerConfig cfg, string target)
    {
        var t = (target ?? "").Trim();
        if (t.Length == 0) return null;
        return cfg.Items.FirstOrDefault(i => i.Id == t)
               ?? cfg.Items.FirstOrDefault(i => string.Equals(i.DisplayName, t, StringComparison.OrdinalIgnoreCase))
               ?? cfg.Items.FirstOrDefault(i => string.Equals(System.IO.Path.GetFileName(i.Path), t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The name a row reads for a file: the row's own name when it has one, else the file's name without its extension.</summary>
    public static string NameOf(AudioPlayerConfig cfg, string path)
    {
        if (path.Length == 0) return "";
        var item = cfg.Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        return item?.DisplayName ?? System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Schema 8: the single track a file written before the list existed becomes the list's first
    /// row, and the old field is cleared so there is one truth; a row without an id (a hand-edited
    /// file) gets one. Idempotent.
    /// </summary>
    public static void Migrate(AudioPlayerConfig cfg)
    {
        if (cfg.Path.Length > 0 && cfg.Items.Count == 0) cfg.Items.Add(new AudioTrackConfig { Path = cfg.Path });
        if (cfg.Items.Count > 0) cfg.Path = "";
        foreach (var item in cfg.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
        }
    }
}
