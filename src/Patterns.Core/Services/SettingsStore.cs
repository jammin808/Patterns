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

    public ShowState Load() => LoadFrom(SettingsPath) ?? new ShowState { SchemaVersion = ShowState.CurrentSchemaVersion };

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
                    Migrate(state);
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
        state.SchemaVersion = ShowState.CurrentSchemaVersion;
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
