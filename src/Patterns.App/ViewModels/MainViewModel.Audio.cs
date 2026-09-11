using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Particles;
using Patterns.Core.Media;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Patterns.Core.LowerThirds;

namespace Patterns.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---- audio track player -------------------------------------------------

    public ObservableCollection<AudioDeviceChoice> AudioDevices { get; } = new();

    private string _audioPlayerStatus = "";
    public string AudioPlayerStatus { get => _audioPlayerStatus; private set => Set(ref _audioPlayerStatus, value); }

    private string _syncStatus = "";

    /// <summary>The master clock's line on the Audio page: the lock, and every playing output's clock against it.</summary>
    public string SyncStatus { get => _syncStatus; private set => Set(ref _syncStatus, value); }

    private string BuildSyncStatus()
    {
        var head = State.AudioPlayer.SyncLock ? "Locked to the master clock." : "Outputs free-run (lock off).";
        var lines = _services.AudioPlayer.SyncReport();
        return lines.Count == 0 ? head + " Play the track to measure each output's clock." : head + " " + string.Join(" · ", lines);
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
            StatusMessage = value
                ? "Sync check on: every sink flashes and the tone output clicks every two seconds on the master clock."
                : "Sync check off.";
        }
    }

    private string _remoteStatus = "";
    public string RemoteStatus { get => _remoteStatus; private set => Set(ref _remoteStatus, value); }

    private string _oscStatus = "";
    /// <summary>The OSC port, where feedback goes, the counts and the last message — the Remote page's line.</summary>
    public string OscStatus { get => _oscStatus; private set => Set(ref _oscStatus, value); }

    private string _beaconStatus = "";
    /// <summary>The beacon going out, the listener, and what it makes of the main machine — the Machine page's line.</summary>
    public string BeaconStatus { get => _beaconStatus; private set => Set(ref _beaconStatus, value); }

    private bool _reviewSeen;

    /// <summary>
    /// The preview fills every multiview — a screen's own multiview pattern, an NDI send of it,
    /// /multiview — so the next look is checked on the monitor wall before the TAKE. A runtime
    /// flag on the bus (never saved), set through the action layer like every switch on the desk.
    /// </summary>
    public bool ReviewOnMultiview
    {
        get => _services.Bus.ReviewOnMultiview;
        set
        {
            if (value == _services.Bus.ReviewOnMultiview) return;
            _services.Actions.Execute(value ? ShowActionKind.ReviewOn : ShowActionKind.ReviewOff, ActionOrigin.Desk);
            _reviewSeen = _services.Bus.ReviewOnMultiview;
            Raise(nameof(ReviewOnMultiview));
        }
    }

    // ---- VOG / stingers ----------------------------------------------------

    /// <summary>The Show panel's two chip grids: one library, split by kind, in library order.</summary>
    public ObservableCollection<StingerItemConfig> VogChips { get; } = new();
    public ObservableCollection<StingerItemConfig> StingChips { get; } = new();

    public EnumItem[] StingerKinds => Lists.StingerKinds;
    public EnumItem[] StingerAfters => Lists.StingerAfters;

    /// <summary>Cue lists for a stinger's "GO the next cue" target; the first row, with an empty id, is the caller's list.</summary>
    public ObservableCollection<PickItem> AfterListChoices { get; } = new();

    /// <summary>Looks then cues, for "A look or cue I name…"; the first row, with an empty id, is "nothing chosen".</summary>
    public ObservableCollection<PickItem> AfterLookOrCueChoices { get; } = new();

    private bool _stingerHolding;
    public bool StingerHolding { get => _stingerHolding; private set => Set(ref _stingerHolding, value); }

    private string _stingerHoldText = "";
    public string StingerHoldText { get => _stingerHoldText; private set => Set(ref _stingerHoldText, value); }

    private string _stingerChipKey = "";
    private string _afterChoiceKey = "";

    /// <summary>Regroups the chips only when the library really moved — no per-item subscriptions to leak.</summary>
    private void RefreshStingerGroups()
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
    private void RefreshAfterChoices()
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

    // ---- break music (Spotify) ---------------------------------------------

    private string _spotifyStatus = "Off.";
    public string SpotifyStatus { get => _spotifyStatus; private set => Set(ref _spotifyStatus, value); }

    private string _spotifyAccountText = "Not connected.";
    public string SpotifyAccountText { get => _spotifyAccountText; private set => Set(ref _spotifyAccountText, value); }

    private string _spotifyNowPlaying = "";
    public string SpotifyNowPlaying { get => _spotifyNowPlaying; private set => Set(ref _spotifyNowPlaying, value); }

    /// <summary>The operator's own Client ID; the setter writes the sidecar beside the settings, never the show.</summary>
    public string SpotifyClientId
    {
        get => _services.Spotify.ClientId;
        set
        {
            if (_services.Spotify.ClientId == (value ?? "").Trim()) return;
            _services.Spotify.ClientId = value ?? "";
            Raise(nameof(SpotifyClientId));
        }
    }

    /// <summary>The three redirect URIs to register on the Spotify app — one per loopback port CONNECT may use.</summary>
    public string SpotifyRedirectUris => string.Join("\n", LoopbackCallback.Ports.Select(SpotifyEndpoints.RedirectUri));

    public ObservableCollection<SpotifyDeviceChoice> SpotifyDevices { get; } = new();

    private SpotifyDeviceChoice? _selectedSpotifyDevice;
    public SpotifyDeviceChoice? SelectedSpotifyDevice
    {
        get => _selectedSpotifyDevice;
        set
        {
            if (value is null) return; // a rebuilt picker clears itself first; the show's choice stands
            if (!Set(ref _selectedSpotifyDevice, value)) return;
            State.Spotify.DeviceName = value.Name;
        }
    }

    public ObservableCollection<SpotifyPlaylistRef> SpotifyPlaylists { get; } = new();

    private SpotifyPlaylistRef? _selectedSpotifyPlaylist;
    public SpotifyPlaylistRef? SelectedSpotifyPlaylist { get => _selectedSpotifyPlaylist; set => Set(ref _selectedSpotifyPlaylist, value); }

    private string _musicLinkDraft = "";
    public string MusicLinkDraft { get => _musicLinkDraft; set => Set(ref _musicLinkDraft, value ?? ""); }

    private IReadOnlyList<SpotifyDevice>? _spotifyDevicesSeen;
    private IReadOnlyList<SpotifyPlaylistRef>? _spotifyPlaylistsSeen;

    // ---- browse & search (desk only; a free account can do this much) --------

    public ObservableCollection<SpotifyTrackRef> SpotifyTracks { get; } = new();

    private SpotifyTrackRef? _selectedSpotifyTrack;
    public SpotifyTrackRef? SelectedSpotifyTrack { get => _selectedSpotifyTrack; set => Set(ref _selectedSpotifyTrack, value); }

    public ObservableCollection<SpotifySearchHit> SpotifySearchHits { get; } = new();

    private SpotifySearchHit? _selectedSpotifySearchHit;
    public SpotifySearchHit? SelectedSpotifySearchHit { get => _selectedSpotifySearchHit; set => Set(ref _selectedSpotifySearchHit, value); }

    private string _musicSearchDraft = "";
    public string MusicSearchDraft { get => _musicSearchDraft; set => Set(ref _musicSearchDraft, value ?? ""); }

    private string _spotifyBrowseStatus = "";
    public string SpotifyBrowseStatus { get => _spotifyBrowseStatus; private set => Set(ref _spotifyBrowseStatus, value); }

    public RelayCommand BrowseSpotifyPlaylistCommand { get; }
    public RelayCommand BrowseSpotifyLinkCommand { get; }
    public RelayCommand AddSpotifyTrackCommand { get; }
    public RelayCommand SearchSpotifyCommand { get; }
    public RelayCommand AddSpotifySearchHitCommand { get; }

    private IReadOnlyList<SpotifyTrackRef>? _spotifyTracksSeen;
    private IReadOnlyList<SpotifySearchHit>? _spotifySearchSeen;

    private void RefreshSpotifyBrowse()
    {
        var tracks = _services.Spotify.Tracks;
        if (!ReferenceEquals(_spotifyTracksSeen, tracks))
        {
            _spotifyTracksSeen = tracks;
            SpotifyTracks.Clear();
            foreach (var t in tracks) SpotifyTracks.Add(t);
            SelectedSpotifyTrack = null;
        }
        var hits = _services.Spotify.SearchHits;
        if (!ReferenceEquals(_spotifySearchSeen, hits))
        {
            _spotifySearchSeen = hits;
            SpotifySearchHits.Clear();
            foreach (var h in hits) SpotifySearchHits.Add(h);
            SelectedSpotifySearchHit = null;
        }
        SpotifyBrowseStatus = _services.Spotify.BrowseStatus;
    }

    private async Task BrowseSpotifyAsync(string uri)
    {
        try
        {
            await _services.Spotify.LoadTracksAsync(uri);
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify browse issue.", ex);
        }
        RefreshSpotifyBrowse();
    }

    private async Task SearchSpotifyAsync()
    {
        try
        {
            await _services.Spotify.SearchAsync(MusicSearchDraft);
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify search issue.", ex);
        }
        RefreshSpotifyBrowse();
    }

    /// <summary>A browsed song or a search hit becomes a one-press entry; the same link twice stays one entry.</summary>
    private void AddMusicEntry(string uri, string name)
    {
        if (!SpotifyUri.TryParse(uri, out var r)) return;
        if (State.Spotify.Items.FirstOrDefault(i => i.Uri == r.Uri) is { } existing)
        {
            StatusMessage = $"'{existing.DisplayName}' is already in break music.";
            return;
        }
        State.Spotify.Items.Add(new SpotifyItemConfig { Uri = r.Uri, Name = name });
        StatusMessage = $"Added '{name}' to break music.";
        RefreshLookMusicChoices();
    }

    // ---- music on a look -----------------------------------------------------

    /// <summary>The Music picker on every look: leave it, pause it, or one of the break-music entries.</summary>
    public ObservableCollection<LookMusicChoice> LookMusicChoices { get; } = new();

    /// <summary>
    /// Kept in step with the break-music list by adding and relabelling in place. A choice is
    /// removed only when no look names it — a bound picker that loses its selected item writes
    /// the loss back into the look — so an entry a look still names stays offered, marked, until
    /// the look is pointed elsewhere.
    /// </summary>
    private void RefreshLookMusicChoices()
    {
        var wanted = new List<(string Id, string Label)> { ("", "Leave the music alone"), (LookConfig.PauseMusic, "Pause break music") };
        foreach (var m in State.Spotify.Items) wanted.Add((m.Id, "▶ " + m.DisplayName));
        foreach (var look in State.LooksAndCues.Looks)
        {
            var id = look.MusicItemId;
            if (id.Length > 0 && wanted.All(w => w.Id != id)) wanted.Add((id, "▶ (no longer in break music)"));
        }
        for (var i = LookMusicChoices.Count - 1; i >= 0; i--)
        {
            if (wanted.All(w => w.Id != LookMusicChoices[i].Id)) LookMusicChoices.RemoveAt(i);
        }
        foreach (var (id, label) in wanted)
        {
            var existing = LookMusicChoices.FirstOrDefault(c => c.Id == id);
            if (existing is null) LookMusicChoices.Add(new LookMusicChoice(id, label));
            else if (existing.Label != label) existing.Label = label;
        }
    }

    /// <summary>
    /// The "Play on" picker: whichever device is active, then Spotify's devices, then the show's
    /// choice when it is not on Spotify right now (so a loaded show never loses its device).
    /// Rebuilt in place only when the entries really moved — clearing a bound picker drops its
    /// selection, and the selection setter ignores that null.
    /// </summary>
    private void RefreshSpotifyDevices()
    {
        var chosen = State.Spotify.DeviceName;
        var devices = _services.Spotify.Devices;
        _spotifyDevicesSeen = devices;
        var wanted = new List<SpotifyDeviceChoice> { new("", "Whichever device is active") };
        foreach (var d in devices)
        {
            wanted.Add(new SpotifyDeviceChoice(d.Name, d.IsActive ? $"{d.Name} (active)" : d.Name));
        }
        if (chosen.Length > 0 && !wanted.Any(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase)))
        {
            wanted.Add(new SpotifyDeviceChoice(chosen, $"{chosen} (not on Spotify right now)"));
        }
        if (SpotifyDevices.Count != wanted.Count || !SpotifyDevices.SequenceEqual(wanted))
        {
            SpotifyDevices.Clear();
            foreach (var c in wanted) SpotifyDevices.Add(c);
        }
        _selectedSpotifyDevice = SpotifyDevices.FirstOrDefault(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase))
                                 ?? SpotifyDevices[0];
        Raise(nameof(SelectedSpotifyDevice));
    }

    private void RefreshSpotifyPlaylists()
    {
        var lists = _services.Spotify.Playlists;
        _spotifyPlaylistsSeen = lists;
        if (SpotifyPlaylists.Count == lists.Count && SpotifyPlaylists.SequenceEqual(lists)) return;
        SpotifyPlaylists.Clear();
        foreach (var l in lists) SpotifyPlaylists.Add(l);
        SelectedSpotifyPlaylist = SpotifyPlaylists.FirstOrDefault();
    }

    private async Task ConnectSpotifyAsync()
    {
        try
        {
            await _services.Spotify.ConnectAsync();
            RefreshSpotifyDevices();
            await _services.Spotify.RefreshPlaylistsAsync();
            RefreshSpotifyPlaylists();
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify connect issue.", ex);
        }
    }

    private async Task RefreshSpotifyDevicesAsync()
    {
        try
        {
            await _services.Spotify.RefreshDevicesAsync();
            RefreshSpotifyDevices();
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify device refresh issue.", ex);
        }
    }

    private async Task RefreshSpotifyPlaylistsAsync()
    {
        try
        {
            await _services.Spotify.RefreshPlaylistsAsync();
            RefreshSpotifyPlaylists();
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify playlist refresh issue.", ex);
        }
    }

    private string _healthText = "";
    public string HealthText { get => _healthText; private set => Set(ref _healthText, value); }

    private string _streamStatus = "";
    public string StreamStatus { get => _streamStatus; private set => Set(ref _streamStatus, value); }

    private StreamHealth _streamHealth = StreamHealth.Read(StreamFacts.None);

    /// <summary>
    /// The stream's health this second, read once on the poll and shown everywhere — the foot of
    /// the rail, the Stream page, the Show panel, the phone. One reading, so no two of them can
    /// say different things about the same stream.
    /// </summary>
    public StreamHealth StreamHealth
    {
        get => _streamHealth;
        private set
        {
            if (_streamHealth == value) return;
            _streamHealth = value;
            Raise();
            Raise(nameof(StreamWord));
            Raise(nameof(StreamLine));
            Raise(nameof(StreamUptime));
            Raise(nameof(StreamHue));
            Raise(nameof(StreamOnAir));
            Raise(nameof(StreamTrouble));
            Raise(nameof(StreamRailText));
            Raise(nameof(StreamCounts));
        }
    }

    /// <summary>"LIVE", "SLOW", "FAULT", "UP…", "OFF" — the one word a rail has room for.</summary>
    public string StreamWord => _streamHealth.Word;

    public string StreamLine => _streamHealth.Line;

    public string StreamUptime => _streamHealth.Uptime;

    public string StreamHue => _streamHealth.Hue;

    public bool StreamOnAir => _streamHealth.IsOnAir;

    public bool StreamTrouble => _streamHealth.IsTrouble;

    /// <summary>The numbers under the line on the Stream page: frames in, the rate, the restarts.</summary>
    public string StreamCounts
    {
        get
        {
            var f = _streamHealth.Facts;
            if (!f.Wanted || f.Frames == 0) return "";
            var parts = new List<string>
            {
                $"{f.Frames:N0} frames encoded",
                f.TargetFps > 0 ? $"{f.Fps:0.#} of {f.TargetFps} fps in" : $"{f.Fps:0.#} fps in",
                f.Destinations == 1 ? "1 destination" : $"{f.Destinations} destinations",
            };
            if (f.Restarts > 0) parts.Add($"{f.Restarts} encoder restart{(f.Restarts == 1 ? "" : "s")}");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The rail's foot: the word, and how long it has been up under it.</summary>
    public string StreamRailText => _streamHealth.Uptime.Length > 0 ? $"{_streamHealth.Word}\n{_streamHealth.Uptime}" : _streamHealth.Word;

    public string RemoteUrlsText => string.Join("\n", _services.Control.RemoteUrls());

    private void RefreshAudioDevices()
    {
        var selected = State.AudioPlayer.Devices;
        AudioDevices.Clear();
        // Pinned first: the computer's own output — the feed usually wired to the venue PA.
        AudioDevices.Add(new AudioDeviceChoice(this, AudioPlayerService.DefaultDeviceKey,
            selected.Contains(AudioPlayerService.DefaultDeviceKey),
            "Computer audio output (default device — venue sound feed)"));
        foreach (var name in AudioPlayerService.OutputDevices())
        {
            AudioDevices.Add(new AudioDeviceChoice(this, name, selected.Contains(name)));
        }
        RefreshMonitorDevices();   // the operator's own output comes off the same enumeration
    }

    /// <summary>Device checkbox changes → the model's device list (empty = default device).</summary>
    internal void AudioDeviceChanged(AudioDeviceChoice choice)
    {
        var devices = State.AudioPlayer.Devices;
        if (choice.IsSelected && !devices.Contains(choice.Name)) devices.Add(choice.Name);
        if (!choice.IsSelected) devices.Remove(choice.Name);
    }

    // ---- effect pulses ------------------------------------------------------

    public EnumItem[] PulsePresets => Lists.PulsePresets;

    private RelayCommand? _addEffectPulse;

    /// <summary>A stinger with no file: a surge through the particles and fractals on screen, fired like any other.</summary>
    public RelayCommand AddEffectPulseCommand => _addEffectPulse ??= new RelayCommand(() =>
    {
        State.Stingers.Items.Add(new StingerItemConfig { Source = StingerSource.EffectPulse, Kind = StingerKind.Sting });
        RefreshStingerGroups();
        StatusMessage = "Effect pulse added — fire it like any stinger; it surges through the particles and fractals on screen.";
    });
    public EnumItem[] FractalKinds => Lists.FractalKinds;
    public EnumItem[] AudioSources => Lists.AudioSources;
    public EnumItem[] FractalQualities => Lists.FractalQualities;

    private RelayCommand<string>? _applyFractalPreset;

    public RelayCommand<string> ApplyFractalPresetCommand => _applyFractalPreset ??= new RelayCommand<string>(name =>
    {
        if (name is null) return;
        _services.BulkEdit(() => FractalPresets.Apply(name, ActivePattern.Fractal));
    });

    /// <summary>
    /// The inputs a sound-reactive pattern can listen to — a microphone, a line, a USB capture
    /// card, an interface channel — with the machine's own default first, and the show's choice
    /// kept in the list when the box it names is not plugged in here. A rig gets patched in the
    /// order the crew reach it, so a name that is not on this machine today is a device to wait
    /// for, not a setting to quietly lose.
    /// </summary>
    public ObservableCollection<string> AudioCaptureDevices { get; } = new();

    private RelayCommand? _refreshAudioCaptureDevices;

    public RelayCommand RefreshAudioCaptureDevicesCommand => _refreshAudioCaptureDevices ??= new RelayCommand(RefreshAudioCaptureDevices);

    /// <summary>The input the pattern being edited is set to listen to, whichever sound-reactive kind it is.</summary>
    public string ChosenAudioDevice => AudioAnalyserService.Asked(ActivePattern).Device;

    public void RefreshAudioCaptureDevices()
    {
        var wanted = new List<string> { AudioInput.DefaultDevice };
        wanted.AddRange(AudioAnalyserService.CaptureDevices());
        var chosen = ChosenAudioDevice;
        if (!AudioInput.WantsDefault(chosen) && AudioInput.IndexOf(wanted, chosen) < 0) wanted.Add(chosen);
        if (AudioCaptureDevices.Count == wanted.Count && AudioCaptureDevices.SequenceEqual(wanted)) return;
        AudioCaptureDevices.Clear();
        foreach (var d in wanted) AudioCaptureDevices.Add(d);
    }

    private string _fractalAudioStatus = "Off.";

    /// <summary>What the analyser says it is doing — the Pattern page's sound line.</summary>
    public string FractalAudioStatus { get => _fractalAudioStatus; private set => Set(ref _fractalAudioStatus, value); }

    public ResolutionPreset? SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            // Apply-style combo: applying resets the selection, so the same preset can be
            // re-applied after switching edit targets.
            if (Set(ref _selectedResolution, value) && value is not null)
            {
                ActivePattern.Canvas.FollowOutput = false;
                ActivePattern.Canvas.Width = value.W;
                ActivePattern.Canvas.Height = value.H;
                _selectedResolution = null;
                Raise();
            }
        }
    }

    public int[] TileSizes => Lists.TileSizes;

    public int SelectedTileSize
    {
        get => _selectedTileSize;
        set
        {
            if (Set(ref _selectedTileSize, value) && value > 0)
            {
                ActivePattern.LedWall.TileWidth = value;
                ActivePattern.LedWall.TileHeight = value;
                _selectedTileSize = 0;
                Raise();
            }
        }
    }

    private ResolutionPreset? _selectedVideoWallResolution;

    public ResolutionPreset? SelectedVideoWallResolution
    {
        get => _selectedVideoWallResolution;
        set
        {
            if (Set(ref _selectedVideoWallResolution, value) && value is not null)
            {
                ActivePattern.VideoWall.ElementWidth = value.W;
                ActivePattern.VideoWall.ElementHeight = value.H;
                _selectedVideoWallResolution = null;
                Raise();
            }
        }
    }
}
