using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Patterns.App.Services;
using Patterns.Core.Effects;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// The Audio page and the Show panel's sound: the tone, the track player and the outputs it
/// plays on, the sync check, VOGs and stingers with their "after" pickers, and the input a
/// sound-reactive pattern listens to. Built by the desk and reached from the views as
/// <c>Audio.X</c>; polled once a second from the desk tick. Every button goes through the same
/// verbs a cue and the remote use; the desk is asked only for its status line, its media
/// library and the lists that read the same devices.
/// </summary>
public sealed class AudioPage : Observable
{
    private readonly MainViewModel _desk;
    private readonly AppServices _services;

    internal ShowState State => _services.State;

    public AudioPage(MainViewModel desk, AppServices services)
    {
        _desk = desk;
        _services = services;

        // The tone
        ToneFrequencyCommand = new RelayCommand<string>(f =>
        {
            if (double.TryParse(f, out var hz)) State.Tone.FrequencyHz = hz;
        });

        // The audio playlist
        AddFilesCommand = new RelayCommand(() => _ = AddFilesAsync());
        AddFolderCommand = new RelayCommand(() => _ = AddFolderAsync());
        RemoveItemCommand = new RelayCommand<AudioTrackConfig>(item =>
        {
            if (item is not null) State.AudioPlayer.Items.Remove(item);
        });
        RemoveFolderCommand = new RelayCommand<string>(folder =>
        {
            if (folder is not null) State.AudioPlayer.Folders.Remove(folder);
        });
        PlayItemCommand = new RelayCommand<AudioTrackConfig>(item =>
        {
            if (item is null) return;
            if (Devices.Count == 0) RefreshDevices();
            Report(_services.Actions.Execute(ShowActionKind.AudioPlay, ActionOrigin.Desk, item.Id));
        });
        MoveItemUpCommand = new RelayCommand<AudioTrackConfig>(item => MoveItem(item, -1));
        MoveItemDownCommand = new RelayCommand<AudioTrackConfig>(item => MoveItem(item, +1));
        NextCommand = new RelayCommand(() => Report(_services.Actions.Execute(ShowActionKind.AudioNext, ActionOrigin.Desk)));
        PrevCommand = new RelayCommand(() => Report(_services.Actions.Execute(ShowActionKind.AudioPrev, ActionOrigin.Desk)));
        ReshuffleCommand = new RelayCommand(() =>
        {
            _services.BulkEdit(() =>
            {
                State.AudioPlayer.ShuffleSeed = Random.Shared.Next(1, int.MaxValue);
                State.AudioPlayer.Shuffle = true;
            });
            _desk.StatusMessage = "The audio playlist dealt a new order.";
        });
        PlayCommand = new RelayCommand(() =>
        {
            if (Devices.Count == 0) RefreshDevices();
            _services.Actions.Execute(ShowActionKind.AudioPlay, ActionOrigin.Desk);
        });
        StopCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.AudioStop, ActionOrigin.Desk));
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);

        // VOGs and stingers
        AddStingerFilesCommand = new RelayCommand(() => _ = AddStingerFilesAsync());
        RemoveStingerCommand = new RelayCommand<StingerItemConfig>(item =>
        {
            if (item is null) return;
            // A cue that fires a deleted stinger fails at show time; refuse and say what points here.
            var refs = StingerLibrary.References(State, item);
            if (refs.Count > 0)
            {
                _desk.StatusMessage = $"'{item.DisplayName}' is still used by {string.Join(", ", refs)} — remove those first.";
                return;
            }
            State.Stingers.Items.Remove(item);
            RefreshStingerGroups();
        });
        FireStingerCommand = new RelayCommand<StingerItemConfig>(item =>
        {
            if (item is null) return;
            _services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id);
        });
        StopStingerCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.StingerStop, ActionOrigin.Desk));
        // The Show panel's own chips assert the kind, so a panel that is stale after a re-kind on
        // the Audio page refuses rather than surprises.
        FireVogCommand = new RelayCommand<StingerItemConfig>(item =>
        {
            if (item is null) return;
            _services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id, "vog");
        });
        FireStingCommand = new RelayCommand<StingerItemConfig>(item =>
        {
            if (item is null) return;
            _services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id, "sting");
        });
        AddEffectPulseCommand = new RelayCommand(() =>
        {
            State.Stingers.Items.Add(new StingerItemConfig { Source = StingerSource.EffectPulse, Kind = StingerKind.Sting });
            RefreshStingerGroups();
            _desk.StatusMessage = "Effect pulse added — fire it like any stinger; it surges through the particles and fractals on screen.";
        });
        RefreshCaptureDevicesCommand = new RelayCommand(RefreshCaptureDevices);

        RefreshStingerGroups();
        RefreshAfterChoices();
    }

    private void Report(ActionResult result)
    {
        if (result.Message.Length > 0) _desk.StatusMessage = result.Message;
    }

    // ---- the tone ----------------------------------------------------------

    private string _toneStatus = "Off";
    public string ToneStatus { get => _toneStatus; private set => Set(ref _toneStatus, value); }

    public EnumItem[] ToneModes => Lists.ToneModes;
    public EnumItem[] ToneChannelsList => Lists.ToneChannelsList;
    public RelayCommand<string> ToneFrequencyCommand { get; }

    // ---- the track player ----------------------------------------------------

    /// <summary>The outputs the track, VOGs and stingers play on, with the computer's own first.</summary>
    public ObservableCollection<AudioDeviceChoice> Devices { get; } = new();

    private string _playerStatus = "";
    public string PlayerStatus { get => _playerStatus; private set => Set(ref _playerStatus, value); }

    private string _syncStatus = "";

    /// <summary>The master clock's line on the Audio page: the lock, and every playing output's clock against it.</summary>
    public string SyncStatus { get => _syncStatus; private set => Set(ref _syncStatus, value); }

    /// <summary>The line's words, pure: the lock, then each measured output or the ask to play something.</summary>
    internal static string SyncLine(bool locked, IReadOnlyList<string> report)
    {
        var head = locked ? "Locked to the master clock." : "Outputs free-run (lock off).";
        return report.Count == 0 ? head + " Play the track to measure each output's clock." : head + " " + string.Join(" · ", report);
    }

    /// <summary>The sync check: a flash on every sink and a click on the tone output at the same master instants.</summary>
    public bool SyncCheck
    {
        get => SyncMarks.Enabled;
        set
        {
            if (SyncMarks.Enabled == value) return;
            SyncMarks.Enabled = value;
            Raise(nameof(SyncCheck));
            _services.RepublishNow(); // the sinks switch to continuous redraw so the flash lands on its frame
            _desk.StatusMessage = value
                ? "Sync check on: every sink flashes and the tone output clicks every two seconds on the master clock."
                : "Sync check off.";
        }
    }

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand<AudioTrackConfig> RemoveItemCommand { get; }
    public RelayCommand<string> RemoveFolderCommand { get; }
    public RelayCommand<AudioTrackConfig> PlayItemCommand { get; }
    public RelayCommand<AudioTrackConfig> MoveItemUpCommand { get; }
    public RelayCommand<AudioTrackConfig> MoveItemDownCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PrevCommand { get; }
    public RelayCommand ReshuffleCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }

    /// <summary>The device rows, rebuilt from what the machine has; the monitor's own list comes off the same enumeration.</summary>
    public void RefreshDevices()
    {
        var selected = State.AudioPlayer.Devices;
        Devices.Clear();
        // Pinned first: the computer's own output — the feed usually wired to the venue PA.
        Devices.Add(new AudioDeviceChoice(this, AudioPlayerService.DefaultDeviceKey,
            selected.Contains(AudioPlayerService.DefaultDeviceKey),
            "Computer audio output (default device — venue sound feed)"));
        foreach (var name in AudioPlayerService.OutputDevices())
        {
            Devices.Add(new AudioDeviceChoice(this, name, selected.Contains(name)));
        }
        _desk.RefreshMonitorDevices();
    }

    /// <summary>Device checkbox changes → the model's device list (empty = default device).</summary>
    internal void DeviceChanged(AudioDeviceChoice choice)
    {
        var devices = State.AudioPlayer.Devices;
        if (choice.IsSelected && !devices.Contains(choice.Name)) devices.Add(choice.Name);
        if (!choice.IsSelected) devices.Remove(choice.Name);
    }

    /// <summary>Tracks for the audio playlist: every audio file picked becomes a row (and a library entry); a file already in the list is left where it is.</summary>
    private async Task AddFilesAsync()
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add audio tracks",
                AllowMultiple = true,
                FileTypeFilter = new[] { MainViewModel.AudioTypes, FilePickerFileTypes.All },
            });
            var added = 0;
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path is null || !PlaylistSequencer.IsAudioPath(path)) continue;
                if (State.AudioPlayer.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                State.AudioPlayer.Items.Add(new AudioTrackConfig { Path = path });
                _desk.AddToMediaLibrary(path, isVideo: true);
                added++;
            }
            if (added > 0) _desk.StatusMessage = $"{added} track{(added == 1 ? "" : "s")} added to the audio playlist.";
        }
        catch (Exception ex)
        {
            Log.Error("Audio track picker failed.", ex);
        }
    }

    /// <summary>A folder for the audio playlist: its audio files play after the rows, in name order, and files dropped in later are seen.</summary>
    private async Task AddFolderAsync()
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Add a folder of audio tracks",
                AllowMultiple = true,
            });
            foreach (var folder in folders)
            {
                var path = folder.TryGetLocalPath();
                if (path is not null && !State.AudioPlayer.Folders.Contains(path)) State.AudioPlayer.Folders.Add(path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio folder picker failed.", ex);
        }
    }

    private void MoveItem(AudioTrackConfig? item, int delta)
    {
        if (item is null) return;
        var items = State.AudioPlayer.Items;
        var index = items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= items.Count) return;
        items.Move(index, target);
    }

    // ---- VOGs and stingers ----------------------------------------------------

    /// <summary>The Show panel's two chip grids: one library, split by kind, in library order.</summary>
    public ObservableCollection<StingerItemConfig> VogChips { get; } = new();
    public ObservableCollection<StingerItemConfig> StingChips { get; } = new();

    public EnumItem[] StingerKinds => Lists.StingerKinds;
    public EnumItem[] StingerAfters => Lists.StingerAfters;
    public EnumItem[] PulsePresets => Lists.PulsePresets;

    /// <summary>Cue lists for a stinger's "GO the next cue" target; the first row, with an empty id, is the caller's list.</summary>
    public ObservableCollection<PickItem> AfterListChoices { get; } = new();

    /// <summary>Looks then cues, for "A look or cue I name…"; the first row, with an empty id, is "nothing chosen".</summary>
    public ObservableCollection<PickItem> AfterLookOrCueChoices { get; } = new();

    private string _stingerStatus = "Ready.";
    public string StingerStatus { get => _stingerStatus; private set => Set(ref _stingerStatus, value); }

    private bool _stingerHolding;
    public bool StingerHolding { get => _stingerHolding; private set => Set(ref _stingerHolding, value); }

    private string _stingerHoldText = "";
    public string StingerHoldText { get => _stingerHoldText; private set => Set(ref _stingerHoldText, value); }

    public RelayCommand AddStingerFilesCommand { get; }
    public RelayCommand<StingerItemConfig> RemoveStingerCommand { get; }
    public RelayCommand<StingerItemConfig> FireStingerCommand { get; }
    public RelayCommand<StingerItemConfig> FireVogCommand { get; }
    public RelayCommand<StingerItemConfig> FireStingCommand { get; }
    public RelayCommand StopStingerCommand { get; }

    /// <summary>A stinger with no file: a surge through the particles and fractals on screen, fired like any other.</summary>
    public RelayCommand AddEffectPulseCommand { get; }

    private string _stingerChipKey = "";
    private string _afterChoiceKey = "";

    /// <summary>Regroups the chips only when the library really moved — no per-item subscriptions to leak.</summary>
    public void RefreshStingerGroups()
    {
        var key = string.Join('|', State.Stingers.Items.Select(s => $"{s.Id}:{(int)s.Kind}:{s.DisplayName}"));
        if (key == _stingerChipKey) return;
        _stingerChipKey = key;
        VogChips.Clear();
        StingChips.Clear();
        foreach (var s in State.Stingers.Items)
        {
            (s.Kind == StingerKind.Vog ? VogChips : StingChips).Add(s);
        }
    }

    /// <summary>
    /// The two "after" pickers, synced in place: a bound picker whose items are cleared drops its
    /// selection and writes that back into the row, so entries that are still wanted stay put.
    /// </summary>
    public void RefreshAfterChoices()
    {
        var key = string.Join('|', State.Stacks.Select(st => $"{st.Id}:{st.Name}:{string.Join(',', st.Cues.Select(c => $"{c.Id}{c.Number}{c.Name}"))}"))
                  + "#" + string.Join('|', State.LooksAndCues.Looks.Select(l => $"{l.Id}:{l.Name}"));
        if (key == _afterChoiceKey) return;
        _afterChoiceKey = key;
        var lists = new List<PickItem> { new("", "The caller's list") };
        lists.AddRange(State.Stacks.Select(st => new PickItem(st.Id, st.Name)));
        var targets = new List<PickItem> { new("", "Choose a look or cue…") };
        targets.AddRange(State.LooksAndCues.Looks.Select(l => new PickItem(l.Id, $"Look · {l.Name}")));
        foreach (var st in State.Stacks)
        {
            foreach (var c in st.Cues) targets.Add(new PickItem(c.Id, $"{st.Name} · {c.Number} {c.Name}"));
        }
        SyncPickItems(AfterListChoices, lists);
        SyncPickItems(AfterLookOrCueChoices, targets);
    }

    private static void SyncPickItems(ObservableCollection<PickItem> current, List<PickItem> wanted)
    {
        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(current[i])) current.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < current.Count && current[i] == wanted[i]) continue;
            var at = current.IndexOf(wanted[i]);
            if (at >= 0) current.Move(at, i);
            else current.Insert(i, wanted[i]);
        }
    }

    private async Task AddStingerFilesAsync()
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add VOGs / stingers (sounds or video clips)",
                AllowMultiple = true,
                FileTypeFilter = new[] { MainViewModel.MediaTypes, FilePickerFileTypes.All },
            });
            var skipped = 0;
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path is null) continue;
                if (!PlaylistSequencer.IsDecodedPath(path))
                {
                    skipped++; // images have no natural end — nothing to revert on
                    continue;
                }
                State.Stingers.Items.Add(new StingerItemConfig { Path = path });
                _desk.AddToMediaLibrary(path, PlaylistSequencer.IsVideoPath(path));
            }
            if (skipped > 0) _desk.StatusMessage = $"VOGs and stingers are sounds or video clips — {skipped} other file{(skipped == 1 ? "" : "s")} skipped.";
            RefreshStingerGroups();
        }
        catch (Exception ex)
        {
            Log.Error("Stinger file picker failed.", ex);
        }
    }

    // ---- the sound-reactive input --------------------------------------------

    /// <summary>
    /// The inputs a sound-reactive pattern can listen to — a microphone, a line, a USB capture
    /// card, an interface channel — with the machine's own default first, and the show's choice
    /// kept in the list when the box it names is not plugged in here. A rig gets patched in the
    /// order the crew reach it, so a name that is not on this machine today is a device to wait
    /// for, not a setting to quietly lose.
    /// </summary>
    public ObservableCollection<string> CaptureDevices { get; } = new();

    public RelayCommand RefreshCaptureDevicesCommand { get; }

    /// <summary>The input the pattern being edited is set to listen to, whichever sound-reactive kind it is.</summary>
    public string ChosenCaptureDevice => AudioAnalyserService.Asked(_desk.ActivePattern).Device;

    public void RefreshCaptureDevices()
    {
        var wanted = new List<string> { AudioInput.DefaultDevice };
        wanted.AddRange(AudioAnalyserService.CaptureDevices());
        var chosen = ChosenCaptureDevice;
        if (!AudioInput.WantsDefault(chosen) && AudioInput.IndexOf(wanted, chosen) < 0) wanted.Add(chosen);
        if (CaptureDevices.Count == wanted.Count && CaptureDevices.SequenceEqual(wanted)) return;
        CaptureDevices.Clear();
        foreach (var d in wanted) CaptureDevices.Add(d);
    }

    private string _analyserStatus = "Off.";

    /// <summary>What the analyser says it is doing — the Pattern page's sound line.</summary>
    public string AnalyserStatus { get => _analyserStatus; private set => Set(ref _analyserStatus, value); }

    // ---- the tick and the show ---------------------------------------------------

    /// <summary>Once a second from the desk tick: every status line, the chips and pickers when their lists moved, the inputs while a reactive pattern is edited.</summary>
    public void Poll()
    {
        ToneStatus = _services.Audio.Status;
        PlayerStatus = _services.AudioPlayer.Status;
        SyncStatus = SyncLine(State.AudioPlayer.SyncLock, _services.AudioPlayer.SyncReport());
        StingerStatus = _services.Stingers.Status;
        RefreshStingerGroups();
        RefreshAfterChoices();
        StingerHolding = _services.Stingers.Holding;
        StingerHoldText = StingerHolding ? $"'{_services.Stingers.HoldName}' is holding the screens." : "";
        AnalyserStatus = _services.Analyser.Status;
        if (_desk.ActivePattern.Kind is PatternKind.Fractal or PatternKind.Reactive) RefreshCaptureDevices();
    }

    /// <summary>A show read from a file became the show: the chips and pickers read the new lists.</summary>
    public void OnShowLoaded()
    {
        RefreshStingerGroups();
        RefreshAfterChoices();
    }
}
