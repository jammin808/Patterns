using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// Portable persistence: everything lives beside the executable. Writes are atomic
/// (temp file + rename) with a .bak generation; a corrupt file is quarantined and can
/// never prevent startup.
/// </summary>
public sealed class SettingsStore
{
    public string BaseDirectory { get; }
    public string SettingsPath { get; }
    public string PresetsDirectory { get; }
    public string BrandKitsDirectory { get; }

    public SettingsStore(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? ResolvePortableDirectory();
        SettingsPath = Path.Combine(BaseDirectory, "patterns.settings.json");
        PresetsDirectory = Path.Combine(BaseDirectory, "presets");
        BrandKitsDirectory = Path.Combine(BaseDirectory, "brandkits");
    }

    public static string ResolvePortableDirectory()
    {
        // Environment.ProcessPath is the real exe location even for single-file publishes.
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;

        // If the app sits somewhere read-only (e.g. Program Files), fall back to LocalAppData
        // so saving still works; the portable case (USB stick, desktop folder) stays beside the exe.
        try
        {
            var probe = Path.Combine(dir, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return dir;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Patterns");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public ShowState Load() => LoadFrom(SettingsPath) ?? Fresh();

    /// <summary>A new show: current schema, both cue lists present.</summary>
    public static ShowState Fresh()
    {
        var state = new ShowState { SchemaVersion = ShowState.CurrentSchemaVersion };
        CueStacks.Caller(state);
        CueStacks.Clicker(state);
        return state;
    }

    /// <summary>
    /// True when the last load upgraded an older file. Ids minted during that upgrade (looks,
    /// stingers) only become stable once the file is written back — the app saves once.
    /// </summary>
    public bool LastLoadMigrated { get; private set; }

    public ShowState? LoadFrom(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var state = JsonUtil.Deserialize<ShowState>(File.ReadAllText(candidate));
                if (state is not null)
                {
                    LastLoadMigrated = state.SchemaVersion < ShowState.CurrentSchemaVersion;
                    Migrate(state);
                    if (state.Name.Length == 0) state.Name = ShowNameFor(path);
                    return state;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Settings file '{candidate}' unreadable — quarantining.", ex);
                Quarantine(candidate);
            }
        }
        return null;
    }

    /// <summary>Upgrades files written by older builds in place.</summary>
    public static void Migrate(ShowState state)
    {
        if (state.SchemaVersion < 2)
        {
            // v0/v1 wrote Mute=true as a silent default (the "no audio" field report) —
            // reset it to the new sound-on default; a deliberate mute is one click away.
            state.Pattern.Media.Mute = false;
            foreach (var a in state.Independent)
            {
                a.Pattern.Media.Mute = false;
            }
        }

        // v3: playlists gained sections — flat item/folder lists become the first section.
        PlaylistSequencer.Normalize(state.Pattern.Media.Playlist);
        foreach (var a in state.Independent)
        {
            PlaylistSequencer.Normalize(a.Pattern.Media.Playlist);
        }

        // v4: looks and stingers carry stable ids (minted by their initialisers when a file
        // lacks them; a blank one from a hand-edited file is minted here) and the show has a
        // name. The app writes an upgraded file back once so the minted ids stick.
        foreach (var look in state.LooksAndCues.Looks)
        {
            if (string.IsNullOrWhiteSpace(look.Id)) look.Id = Guid.NewGuid().ToString("N");
        }
        foreach (var stinger in state.Stingers.Items)
        {
            if (string.IsNullOrWhiteSpace(stinger.Id)) stinger.Id = Guid.NewGuid().ToString("N");
        }

        // Break music: hand-edited or hand-copied entries get an id and a canonical URI, like
        // stingers. Unconditional and idempotent — no schema step, because there is nothing to
        // convert: a file written before break music existed simply has no block and defaults off.
        foreach (var item in state.Spotify.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
            if (SpotifyUri.TryParse(item.Uri, out var r)) item.Uri = r.Uri; // a pasted link becomes the URI form
        }

        // v5: the presenter click-through becomes the clicker list — one Apply Look cue per
        // step — and the show always holds the caller's stack beside it.
        if (state.SchemaVersion < 5 || state.Presenter.Steps.Count > 0)
        {
            CueStacks.MigratePresenter(state);
        }
        CueStacks.Caller(state);
        CueStacks.Clicker(state);

        // v6: the stinger library splits into VOG (today's behaviour — over the show, the music
        // ducks, the content carries on) and Stinger (a transition hit with an after-policy).
        // Everything an older file holds behaved as a VOG, so that is what it becomes, written
        // explicitly so a hand-edited or half-written file lands somewhere defined rather than on
        // a property initialiser.
        if (state.SchemaVersion < 6)
        {
            foreach (var item in state.Stingers.Items)
            {
                item.Kind = StingerKind.Vog;
                item.After = StingerAfter.Return;
                item.AfterTarget = "";
                item.MusicReturns = true;
            }
        }

        // v7: the media library's entries carry an id, a kind and a date (the Library page's
        // sections, search and thumbnails key on them). Derived from the path and minted where
        // missing, idempotent, so a hand-edited file lands somewhere defined; the app writes an
        // upgraded file back once so the minted ids stick.
        foreach (var media in state.MediaLibrary)
        {
            if (string.IsNullOrWhiteSpace(media.Id)) media.Id = Guid.NewGuid().ToString("N");
            if (media.Kind == LibraryMediaKind.Unknown) media.Kind = MediaLibraryEntry.KindOf(media.Path, media.IsVideo);
            if (media.AddedUtc == default) media.AddedUtc = DateTime.UtcNow;
        }

        // Every NDI sender owns a virtual screen (and the stream, while set to its own): brought
        // in step on every load, idempotently — an older show's senders get theirs, mirroring
        // exactly what they mirrored before.
        VirtualScreens.Sync(state);

        state.SchemaVersion = ShowState.CurrentSchemaVersion;
    }

    /// <summary>"awards-2026.patshow.json" → "awards-2026"; the settings file itself has no name.</summary>
    public static string ShowNameFor(string path)
    {
        // Show files travel between machines: split on both separators, whatever the host uses.
        var cut = path.LastIndexOfAny(new[] { '/', '\\' });
        var file = cut >= 0 ? path[(cut + 1)..] : path;
        if (string.Equals(file, "patterns.settings.json", StringComparison.OrdinalIgnoreCase)) return "";
        var name = Path.GetFileNameWithoutExtension(file);
        if (name.EndsWith(".patshow", StringComparison.OrdinalIgnoreCase)) name = name[..^".patshow".Length];
        return name;
    }

    public void Save(ShowState state) => SaveTo(SettingsPath, state);

    public void SaveTo(string path, ShowState state)
    {
        var json = JsonUtil.Serialize(state);
        WriteAtomic(path, json);
    }

    public static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static void Quarantine(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            }
        }
        catch
        {
            // Even quarantine failing must not stop startup.
        }
    }

    // ---- Pattern presets ----------------------------------------------------

    public IReadOnlyList<(string Name, string Path)> ListPresets()
    {
        try
        {
            if (!Directory.Exists(PresetsDirectory)) return Array.Empty<(string, string)>();
            return Directory.EnumerateFiles(PresetsDirectory, "*.json")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => (Path.GetFileNameWithoutExtension(p), p))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not list presets.", ex);
            return Array.Empty<(string, string)>();
        }
    }

    public void SavePreset(string name, PatternConfig pattern)
    {
        var safe = Sanitize(name);
        WriteAtomic(Path.Combine(PresetsDirectory, safe + ".json"), JsonUtil.Serialize(pattern));
    }

    public PatternConfig? LoadPreset(string path)
    {
        try { return JsonUtil.Deserialize<PatternConfig>(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            Log.Warn($"Preset '{path}' unreadable.", ex);
            return null;
        }
    }

    // ---- Brand kits ---------------------------------------------------------

    public IReadOnlyList<(string Name, string Path)> ListBrandKits()
    {
        try
        {
            if (!Directory.Exists(BrandKitsDirectory)) return Array.Empty<(string, string)>();
            return Directory.EnumerateFiles(BrandKitsDirectory, "*.json")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => (Path.GetFileNameWithoutExtension(p), p))
                .ToList();
        }
        catch
        {
            return Array.Empty<(string, string)>();
        }
    }

    public void SaveBrandKit(string name, BrandKit kit)
        => WriteAtomic(Path.Combine(BrandKitsDirectory, Sanitize(name) + ".json"), JsonUtil.Serialize(kit));

    public BrandKit? LoadBrandKit(string path)
    {
        try { return JsonUtil.Deserialize<BrandKit>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars);
        return string.IsNullOrWhiteSpace(safe) ? "preset" : safe;
    }
}
