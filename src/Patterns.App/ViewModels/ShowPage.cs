using System.Collections.ObjectModel;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// The Show page's own state: the looks (saved, updated, fired to air or into the preview), the
/// caller's VT clock (the clip on air, what is left, the OUT call), and the show file's earlier
/// versions. Built by the desk and reached as <c>Show.X</c>; the desk's tick asks the clock to
/// read once a second. The desk keeps the editors, the status line and the show-replaced refresh,
/// and the page asks it for those.
/// </summary>
public sealed class ShowPage : Observable
{
    private readonly MainViewModel _desk;
    private readonly AppServices _services;

    private ShowState State => _services.State;

    public ShowPage(MainViewModel desk, AppServices services)
    {
        _desk = desk;
        _services = services;

        SaveLookCommand = new RelayCommand(SaveLook);
        ApplyLookToPreviewCommand = new RelayCommand<LookConfig>(look =>
        {
            if (look is not null) ApplyLookToPreview(look);
        });
        ApplyLookCommand = new RelayCommand<LookConfig>(look =>
        {
            if (look is not null) ApplyLook(look);
        });
        UpdateLookCommand = new RelayCommand<LookConfig>(look =>
        {
            if (look is null) return;
            look.Json = LookService.Capture(State);
            _desk.StatusMessage = $"Look '{look.Name}' updated with the current state.";
        });
        DeleteLookCommand = new RelayCommand<LookConfig>(look =>
        {
            if (look is null) return;
            // Orphaned references fail silently at show time; refuse and say what points here.
            var refs = LookService.References(State, look);
            if (refs.Count > 0)
            {
                _desk.StatusMessage = $"'{look.Name}' is still used by {string.Join(", ", refs)} — remove those first.";
                return;
            }
            State.LooksAndCues.Looks.Remove(look);
            RaiseLookNames();
        });
        RestoreBackupCommand = new RelayCommand(RestoreBackup);
        OpenBackupsFolderCommand = new RelayCommand(OpenBackupsFolder);
        RefreshBackups();
    }

    public RelayCommand SaveLookCommand { get; }
    public RelayCommand<LookConfig> ApplyLookCommand { get; }
    public RelayCommand<LookConfig> ApplyLookToPreviewCommand { get; }
    public RelayCommand<LookConfig> UpdateLookCommand { get; }
    public RelayCommand<LookConfig> DeleteLookCommand { get; }
    public RelayCommand RestoreBackupCommand { get; }
    public RelayCommand OpenBackupsFolderCommand { get; }

    // ---- looks ---------------------------------------------------------------------

    private string _newLookName = "";
    public string NewLookName { get => _newLookName; set => Set(ref _newLookName, value); }

    private int _newLookHotkey;
    public int NewLookHotkey { get => _newLookHotkey; set => Set(ref _newLookHotkey, value); }

    public int[] HotkeySlots { get; } = Enumerable.Range(0, 13).ToArray();

    public List<string> LookNames => State.LooksAndCues.Looks.Select(l => l.Name).ToList();

    /// <summary>The look list changed under the page — a show loaded, a look pasted in by the assistant, one deleted.</summary>
    public void RaiseLookNames() => Raise(nameof(LookNames));

    private void SaveLook()
    {
        var name = string.IsNullOrWhiteSpace(NewLookName) ? $"Look {State.LooksAndCues.Looks.Count + 1}" : NewLookName.Trim();
        // "Walk-in" and "walk-in" are the same look: the resolver is case-insensitive, so the save must be too.
        var existing = LookService.Find(State, name);
        var json = LookService.Capture(State);
        if (existing is not null)
        {
            existing.Json = json;
            if (NewLookHotkey > 0) existing.Hotkey = NewLookHotkey;
        }
        else
        {
            // A hotkey can only belong to one look.
            if (NewLookHotkey > 0)
            {
                foreach (var l in State.LooksAndCues.Looks.Where(l => l.Hotkey == NewLookHotkey))
                {
                    l.Hotkey = 0;
                }
            }
            State.LooksAndCues.Looks.Add(new LookConfig { Name = name, Hotkey = NewLookHotkey, Json = json });
        }
        NewLookName = "";
        NewLookHotkey = 0;
        _desk.StatusMessage = $"Look '{name}' saved.";
        RaiseLookNames();
    }

    /// <summary>
    /// Fires a look to air — F-keys, look buttons, scheduled cues, presenter steps and
    /// remotes all land here. EDIT SAFE protects what you are <em>building</em>, not what you
    /// <em>fire</em>: with the sandbox open the audience gets the look and the preview keeps
    /// showing the operator's in-progress edit. Use "→ PVW" to load one into the editors.
    /// </summary>
    public void ApplyLook(LookConfig look) => _services.Actions.ApplyLook(look, ActionOrigin.Desk);

    /// <summary>Loads a look into the editors (the sandboxed preview) instead of putting it on air.</summary>
    public void ApplyLookToPreview(LookConfig look)
        => _services.Actions.Execute(ShowActionKind.ApplyLookToPreview, ActionOrigin.Desk, look.Id);

    // ---- the caller's VT clock: the clip on air, what is left, the rehearsal's skip --------------

    private string _videoClockTag = "";
    private string _videoClockName = "";
    private string _videoClockTimes = "";
    private string _videoClockCall = "";
    private double _videoClockFraction;
    private bool _videoClockOut;
    private bool _hasVideoClock;

    /// <summary>"VT", "AUDIO", "STINGER CLIP", "PLAYLIST" — what kind of clip the clock reads.</summary>
    public string VideoClockTag { get => _videoClockTag; private set => Set(ref _videoClockTag, value); }

    /// <summary>The clip's file name.</summary>
    public string VideoClockName { get => _videoClockName; private set => Set(ref _videoClockName, value); }

    /// <summary>"1:02 / 3:30 · 2:28 left", "1:02 · loop", "ended".</summary>
    public string VideoClockTimes { get => _videoClockTimes; private set => Set(ref _videoClockTimes, value); }

    /// <summary>"OUT IN 7" in the clip's last ten seconds; empty before that.</summary>
    public string VideoClockCall { get => _videoClockCall; private set => Set(ref _videoClockCall, value); }

    /// <summary>How far through the clip is, 0–1 — the bar under the words.</summary>
    public double VideoClockFraction { get => _videoClockFraction; private set => Set(ref _videoClockFraction, value); }

    /// <summary>The clip is in its last ten seconds and will end: the row goes red.</summary>
    public bool VideoClockOut { get => _videoClockOut; private set => Set(ref _videoClockOut, value); }

    /// <summary>A clip is on air: the panel shows the clock.</summary>
    public bool HasVideoClock { get => _hasVideoClock; private set => Set(ref _hasVideoClock, value); }

    /// <summary>The one line: "VT sponsor.mp4 · 1:02 / 3:30 · 2:28 left".</summary>
    public string VideoClockText => HasVideoClock ? $"{VideoClockTag} {VideoClockName} · {VideoClockTimes}" : "";

    /// <summary>Every second: the reading, and only the words that changed are raised — the panel never flinches for a clip that is not moving.</summary>
    public void RefreshVideoClock()
    {
        var r = _services.VideoOnAir();
        if (r is null)
        {
            HasVideoClock = false;
            VideoClockTag = "";
            VideoClockName = "";
            VideoClockTimes = "";
            VideoClockCall = "";
            VideoClockFraction = 0;
            VideoClockOut = false;
            return;
        }
        VideoClockTag = VideoClock.Tag(r);
        VideoClockName = r.Name;
        VideoClockTimes = VideoClock.Times(r);
        VideoClockCall = VideoClock.Call(r);
        VideoClockFraction = r.Fraction;
        VideoClockOut = r.InLast(VideoClock.OutWarningSeconds);
        HasVideoClock = true;
    }

    // ---- the show file's earlier versions ---------------------------------------------

    /// <summary>One kept version of the show file, as the Machine page lists it.</summary>
    public sealed record BackupChoice(string Label, string Path)
    {
        public override string ToString() => Label;
    }

    public ObservableCollection<BackupChoice> BackupChoices { get; } = new();

    private BackupChoice? _selectedBackup;

    public BackupChoice? SelectedBackup { get => _selectedBackup; set => Set(ref _selectedBackup, value); }

    private string _backupsSummary = "";

    /// <summary>"The previous save and 12 earlier versions, the oldest from Tue 14:02, in …\backups".</summary>
    public string BackupsSummary { get => _backupsSummary; private set => Set(ref _backupsSummary, value); }

    /// <summary>Reads the kept versions: the previous save first, then the timed copies, newest first.</summary>
    public void RefreshBackups()
    {
        var store = _services.Store;
        var keep = _selectedBackup?.Path;
        BackupChoices.Clear();
        if (store.PreviousSavePath is { } bak)
        {
            BackupChoices.Add(new BackupChoice($"The previous save — {File.GetLastWriteTime(bak):ddd HH:mm:ss}", bak));
        }
        var kept = store.ListBackups();
        foreach (var (when, path) in kept)
        {
            BackupChoices.Add(new BackupChoice($"{when:ddd d MMM HH:mm:ss}", path));
        }
        SelectedBackup = BackupChoices.FirstOrDefault(c => c.Path == keep) ?? BackupChoices.FirstOrDefault();
        BackupsSummary = BackupChoices.Count == 0
            ? "No earlier version yet — one is kept the first time the show file changes."
            : $"{(store.PreviousSavePath is null ? "" : "The previous save and ")}{kept.Count} earlier version{(kept.Count == 1 ? "" : "s")}"
              + (kept.Count > 0 ? $", the oldest from {kept[^1].When:ddd d MMM HH:mm}" : "")
              + $", in {store.BackupsDirectory}";
    }

    /// <summary>The selected version becomes the show — every list starts over, exactly as Load show does.</summary>
    private void RestoreBackup()
    {
        if (_selectedBackup is not { } choice) return;
        var loaded = _services.Store.LoadFrom(choice.Path);
        if (loaded is null)
        {
            _desk.StatusMessage = "That version could not be read.";
            return;
        }
        _desk.ApplyLoadedShow(loaded, $"Show restored: {choice.Label}");
        RefreshBackups();
    }

    private void OpenBackupsFolder()
    {
        try
        {
            Directory.CreateDirectory(_services.Store.BackupsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_services.Store.BackupsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _desk.StatusMessage = $"Could not open the folder: {ex.Message}";
        }
    }
}
