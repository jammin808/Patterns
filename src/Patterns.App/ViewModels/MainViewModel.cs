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

/// <summary>Target chooser entries for the pattern editor ("Program" or one custom screen).</summary>
public sealed record EditTarget(string Label, string? ScreenId)
{
    public override string ToString() => Label;
}

/// <summary>
/// One tile in the Library: a factory pattern, a media file, a saved preset or a brand kit.
/// Identified by <see cref="Id"/> (never by name — two files of one name in two folders are
/// two tiles), filed under a <see cref="Section"/>, found by <see cref="SearchKey"/>, drawn from
/// <see cref="ThumbConfig"/> or a <see cref="Swatch"/>.
/// </summary>
public sealed class PresetItem : Observable
{
    private Bitmap? _thumbnail;

    public required string Id { get; init; }
    public required string Section { get; init; }
    public required string Category { get; init; }
    public required string Name { get; init; }
    public required Action Apply { get; init; }

    /// <summary>The pattern the thumbnail shows, built over the show's state; null for a swatch tile.</summary>
    public Func<ShowState, PatternConfig?>? ThumbConfig { get; init; }

    /// <summary>A brand kit's colours: the thumbnail is bands of them.</summary>
    public IReadOnlyList<string>? Swatch { get; init; }

    /// <summary>Takes the tile out of the library (a media entry); null for what cannot be removed here.</summary>
    public Action? Remove { get; init; }

    public bool CanRemove => Remove is not null;

    /// <summary>Lower-case words the search box matches against: the name, the category, the section.</summary>
    public string SearchKey => $"{Name} {Category} {Section}".ToLowerInvariant();

    public Bitmap? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
}

public sealed class MainViewModel : Observable
{
    private readonly AppServices _services;
    private EditTarget _editTarget = new("Program", null);
    private string _ndiStatus = "Off";
    private string _outputsStatus = "";
    private string _newPresetName = "";
    private ResolutionPreset? _selectedResolution;
    private int _selectedTileSize;
    private string _statusMessage = "";
    private ScreenPlacement? _selectedPlacement;

    public MainViewModel(AppServices services)
    {
        _services = services;

        // Every verb goes through the action layer: one code path for the desk, the keyboard,
        // the remotes and the schedule, one journal, one place to resync the editors from.
        _services.Actions.Performed += OnActionPerformed;
        GoCommand = new RelayCommand(GoLive);
        StopCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.OutputsOff, ActionOrigin.Desk));
        IdentifyCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.Identify, ActionOrigin.Desk));
        BlackoutCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.BlackoutToggle, ActionOrigin.Desk));
        ArmCountdownCommand = new RelayCommand(() =>
        {
            State.Countdown.ArmedAtUtc = DateTime.UtcNow;
            State.Countdown.Enabled = true;
        });
        SaveShowCommand = new RelayCommand(() => _ = SaveShowAsync());
        LoadShowCommand = new RelayCommand(() => _ = LoadShowAsync());
        SavePresetCommand = new RelayCommand(SaveUserPreset);
        BrowseImageCommand = new RelayCommand(() => _ = PickFileAsync("Choose image", FilePickerFileTypes.ImageAll, p =>
        {
            ActivePattern.Media.ImagePath = p;
            ActivePattern.Media.Source = MediaSource.Image;
            AddToMediaLibrary(p, isVideo: false);
        }));
        BrowseVideoCommand = new RelayCommand(() => _ = PickFileAsync("Choose video", VideoTypes, p =>
        {
            ActivePattern.Media.VideoPath = p;
            ActivePattern.Media.Source = MediaSource.Video;
            AddToMediaLibrary(p, isVideo: true);
        }));
        BrowseLogoCommand = new RelayCommand(() => _ = PickFileAsync("Choose logo (PNG with alpha)", FilePickerFileTypes.ImageAll, p => State.Brand.LogoPath = p));
        BrowseLayerImageCommand = new RelayCommand<LayerConfig>(layer =>
        {
            if (layer is null) return;
            _ = PickFileAsync("Choose the layer's image", FilePickerFileTypes.ImageAll, p =>
            {
                layer.ImagePath = p;
                layer.Source = LayerSource.Image;
                layer.Enabled = true;
                AddToMediaLibrary(p, isVideo: false);
            });
        });
        BrowseLayerVideoCommand = new RelayCommand<LayerConfig>(layer =>
        {
            if (layer is null) return;
            _ = PickFileAsync("Choose the layer's clip", VideoTypes, p =>
            {
                layer.VideoPath = p;
                layer.Source = LayerSource.Video;
                layer.Enabled = true;
                AddToMediaLibrary(p, isVideo: true);
            });
        });
        ApplyParticlePresetCommand = new RelayCommand<string>(name =>
        {
            if (name is null) return;
            _services.BulkEdit(() => ParticlePresets.Apply(name, ActivePattern.Particles));
        });
        ApplyCountdownLabelCommand = new RelayCommand<string>(label =>
        {
            if (label is not null) State.Countdown.Label = label;
        });
        ApplyPresetCommand = new RelayCommand<PresetItem>(item => item?.Apply());
        SaveBrandKitCommand = new RelayCommand(SaveBrandKit);
        LoadBrandKitCommand = new RelayCommand(() => _ = LoadBrandKitAsync());
        ResetLayoutCommand = new RelayCommand(ResetLayout);
        AddNdiSenderCommand = new RelayCommand(AddNdiSender);
        RemoveNdiSenderCommand = new RelayCommand<NdiSenderConfig>(cfg =>
        {
            if (cfg is null) return;
            State.Ndi.Senders.Remove(cfg);
            SyncVirtualScreens(); // its screen, and that screen's own content, go with it
        });

        // Playlist
        AddPlaylistFilesCommand = new RelayCommand(() => _ = AddPlaylistFilesAsync());
        AddPlaylistFolderCommand = new RelayCommand(() => _ = AddPlaylistFolderAsync());
        RemovePlaylistItemCommand = new RelayCommand<PlaylistItemConfig>(item =>
        {
            if (item is not null) ActivePlaylistSection.Items.Remove(item);
        });
        MovePlaylistItemUpCommand = new RelayCommand<PlaylistItemConfig>(item => MovePlaylistItem(item, -1));
        MovePlaylistItemDownCommand = new RelayCommand<PlaylistItemConfig>(item => MovePlaylistItem(item, +1));
        RemovePlaylistFolderCommand = new RelayCommand<string>(folder =>
        {
            if (folder is not null) ActivePlaylistSection.Folders.Remove(folder);
        });
        AddPlaylistSectionCommand = new RelayCommand(() =>
        {
            var playlist = ActivePattern.Media.Playlist;
            PlaylistSequencer.Normalize(playlist);
            playlist.Sections.Add(new PlaylistSectionConfig { Name = $"Part {playlist.Sections.Count + 1}" });
            playlist.ActiveSection = playlist.Sections.Count - 1;
            RaisePlaylistSection();
        });
        RemovePlaylistSectionCommand = new RelayCommand<PlaylistSectionConfig>(section =>
        {
            var playlist = ActivePattern.Media.Playlist;
            if (section is null || !playlist.Sections.Contains(section)) return;
            if (playlist.Sections.Count <= 1)
            {
                StatusMessage = "The playlist needs at least one part — clear its files instead.";
                return;
            }
            var index = playlist.Sections.IndexOf(section);
            playlist.Sections.Remove(section);
            if (playlist.ActiveSection >= index && playlist.ActiveSection > 0) playlist.ActiveSection--;
            RaisePlaylistSection();
        });
        SetPlaylistSectionCommand = new RelayCommand<PlaylistSectionConfig>(section =>
        {
            var playlist = ActivePattern.Media.Playlist;
            var index = section is null ? -1 : playlist.Sections.IndexOf(section);
            if (index < 0) return;
            playlist.ActiveSection = index;
            RaisePlaylistSection();
            StatusMessage = $"Playlist part '{section!.Name}' is on air.";
        });

        // Live inputs & web pages
        RefreshNdiSourcesCommand = new RelayCommand(() => RefreshNdiSources());
        RefreshCaptureDevicesCommand = new RelayCommand(() => RefreshCaptureDevices());
        OpenWebFullscreenCommand = new RelayCommand(() => OpenWeb(kiosk: true));
        OpenWebWindowedCommand = new RelayCommand(() => OpenWeb(kiosk: false));
        CloseWebCommand = new RelayCommand(() =>
        {
            _services.Web.CloseAll();
            WebStatus = _services.Web.Status;
        });
        LoadWebUrlCommand = new RelayCommand<string>(url =>
        {
            if (url is not null) State.Web.Url = url;
        });
        RemoveWebUrlCommand = new RelayCommand<string>(url =>
        {
            if (url is not null) State.Web.SavedUrls.Remove(url);
        });

        // Web pages inside the engine: the page the desk last pointed at (else the pattern's) takes typed text and keys
        SendWebTextCommand = new RelayCommand(() =>
        {
            if (CurrentWebSource() is not { } page)
            {
                StatusMessage = "No web page to type into — put one on the pattern or a layer first, then click into it on the PREVIEW pane.";
                return;
            }
            var text = WebTypedText;
            if (text.Length == 0) return;
            page.TypeText(text);
            WebTypedText = "";
            StatusMessage = $"Typed into {WebAddress.ShortName(page.CurrentUrl)} — Enter sends it, if the page wants that.";
        });
        WebKeyCommand = new RelayCommand<string>(key =>
        {
            if (key is not null && CurrentWebSource() is { } page) page.PressKey(key);
        });
        WebBackCommand = new RelayCommand(() => CurrentWebSource()?.GoBack());
        WebForwardCommand = new RelayCommand(() => CurrentWebSource()?.GoForward());
        WebReloadCommand = new RelayCommand(() => CurrentWebSource()?.Reload());
        RememberWebUrlCommand = new RelayCommand(() =>
        {
            var url = WebAddress.Normalize(ActivePattern.Media.WebUrl);
            if (url.Length == 0) return;
            if (!State.Web.SavedUrls.Contains(url)) State.Web.SavedUrls.Add(url);
            StatusMessage = $"Remembered {WebAddress.ShortName(url)} — it is in the saved pages here and on the Remote & web page.";
        });
        PutWebPageOnPatternCommand = new RelayCommand(() =>
        {
            var url = WebAddress.Normalize(State.Web.Url);
            if (url.Length == 0)
            {
                StatusMessage = "Enter a page address first.";
                return;
            }
            _services.BulkEdit(() =>
            {
                ActivePattern.Kind = PatternKind.Media;
                ActivePattern.Media.Source = MediaSource.Web;
                ActivePattern.Media.WebUrl = url;
            });
            if (!State.Web.SavedUrls.Contains(url)) State.Web.SavedUrls.Add(url);
            RefreshWebControls();
            StatusMessage = $"{WebAddress.ShortName(url)} is the pattern now — drive it on the PREVIEW pane; its settings are on the Media page.";
        });

        // Presenter click-through: the clicker list on the Cues page, stepped from here
        PresenterNextCommand = new RelayCommand(() => _services.Actions.PresenterAdvance(+1, ActionOrigin.Desk));
        PresenterPrevCommand = new RelayCommand(() => _services.Actions.PresenterAdvance(-1, ActionOrigin.Desk));
        PresenterResetCommand = new RelayCommand(() =>
        {
            _services.Actions.Execute(ShowActionKind.ListReset, ActionOrigin.Desk, CueStacks.Clicker(State).Id);
            Raise(nameof(PresenterStepText));
        });

        // Audio track player
        BrowseAudioTrackCommand = new RelayCommand(() => _ = PickFileAsync("Choose audio track", AudioTypes, p =>
        {
            State.AudioPlayer.Path = p;
            AddToMediaLibrary(p, isVideo: true);
        }));
        PlayAudioCommand = new RelayCommand(() =>
        {
            if (AudioDevices.Count == 0) RefreshAudioDevices();
            _services.Actions.Execute(ShowActionKind.AudioPlay, ActionOrigin.Desk);
        });
        StopAudioCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.AudioStop, ActionOrigin.Desk));
        RefreshAudioDevicesCommand = new RelayCommand(RefreshAudioDevices);
        NewLowerThirdCommand = new RelayCommand(() => NewLowerThird(NewLowerThirdPreset));
        DuplicateLowerThirdCommand = new RelayCommand<LowerThirdDesign>(DuplicateLowerThird);
        DeleteLowerThirdCommand = new RelayCommand<LowerThirdDesign>(DeleteLowerThird);
        ShowLowerThirdCommand = new RelayCommand<LowerThirdDesign>(d => { if (d is not null) ShowLowerThird(d); });
        HideLowerThirdCommand = new RelayCommand(HideLowerThird);
        AddElementCommand = new RelayCommand<string>(kind =>
        {
            if (Enum.TryParse<LowerThirdElementKind>(kind, true, out var k)) AddElement(k);
        });
        RemoveElementCommand = new RelayCommand<LowerThirdElement>(RemoveElement);
        MoveElementUpCommand = new RelayCommand<LowerThirdElement>(e => MoveElement(e, -1));
        MoveElementDownCommand = new RelayCommand<LowerThirdElement>(e => MoveElement(e, +1));
        MotionInCommand = new RelayCommand<string>(m => ApplyMotion(m, true));
        MotionOutCommand = new RelayCommand<string>(m => ApplyMotion(m, false));
        AddInKeyCommand = new RelayCommand(() => AddKey(true));
        AddOutKeyCommand = new RelayCommand(() => AddKey(false));
        RemoveInKeyCommand = new RelayCommand<LowerThirdKeyframe>(k => RemoveKey(k, true));
        RemoveOutKeyCommand = new RelayCommand<LowerThirdKeyframe>(k => RemoveKey(k, false));
        ElementColorWordCommand = new RelayCommand<string>(SetElementColorWord);
        PickElementFileCommand = new RelayCommand(() => _ = PickElementFileAsync());
        SaveLowerThirdFileCommand = new RelayCommand(SaveLowerThirdFile);
        LoadLowerThirdFileCommand = new RelayCommand<string>(path => LoadLowerThirdFile(path));
        PreviewRestartCommand = new RelayCommand(() => PreviewTimeMs = 0);
        ResetWarpCommand = new RelayCommand(() =>
        {
            if (_selectedPlacement is null) return;
            _selectedPlacement.WarpTlx = 0; _selectedPlacement.WarpTly = 0;
            _selectedPlacement.WarpTrx = 0; _selectedPlacement.WarpTry = 0;
            _selectedPlacement.WarpBlx = 0; _selectedPlacement.WarpBly = 0;
            _selectedPlacement.WarpBrx = 0; _selectedPlacement.WarpBry = 0;
            RaiseSelection();
        });
        ResetBlendCommand = new RelayCommand(ResetBlend);

        // Stingers
        AddStingerFilesCommand = new RelayCommand(() => _ = AddStingerFilesAsync());
        RemoveStingerCommand = new RelayCommand<StingerItemConfig>(item =>
        {
            if (item is null) return;
            // A cue that fires a deleted stinger fails at show time; refuse and say what points here.
            var refs = StingerLibrary.References(State, item);
            if (refs.Count > 0)
            {
                StatusMessage = $"'{item.DisplayName}' is still used by {string.Join(", ", refs)} — remove those first.";
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

        // Break music (Spotify): the desk's buttons go through the same verbs a cue and the remote use.
        SpotifyConnectCommand = new RelayCommand(() => _ = ConnectSpotifyAsync());
        SpotifyDisconnectCommand = new RelayCommand(() =>
        {
            _services.Spotify.Disconnect();
            RefreshSpotifyDevices();
            RefreshSpotifyPlaylists();
        });
        RefreshSpotifyDevicesCommand = new RelayCommand(() => _ = RefreshSpotifyDevicesAsync());
        RefreshSpotifyPlaylistsCommand = new RelayCommand(() => _ = RefreshSpotifyPlaylistsAsync());
        AddMusicItemCommand = new RelayCommand(() =>
        {
            if (!SpotifyUri.TryParse(MusicLinkDraft, out var r))
            {
                StatusMessage = "That is not a Spotify link — copy one from Spotify with Share → Copy link.";
                return;
            }
            State.Spotify.Items.Add(new SpotifyItemConfig { Uri = r.Uri });
            MusicLinkDraft = "";
        });
        AddSpotifyPlaylistCommand = new RelayCommand(() =>
        {
            if (SelectedSpotifyPlaylist is not { } list)
            {
                StatusMessage = "Choose one of your playlists first — press Refresh my playlists after CONNECT.";
                return;
            }
            if (!SpotifyUri.TryParse(list.Uri, out var r)) return;
            State.Spotify.Items.Add(new SpotifyItemConfig { Uri = r.Uri, Name = list.Name });
        });
        RemoveMusicItemCommand = new RelayCommand<SpotifyItemConfig>(item =>
        {
            if (item is null) return;
            // A cue that plays a deleted entry fails at show time; refuse and say what points here.
            var refs = SpotifyLibrary.References(State, item);
            if (refs.Count > 0)
            {
                StatusMessage = $"'{item.DisplayName}' is still used by {string.Join(", ", refs)} — remove those first.";
                return;
            }
            State.Spotify.Items.Remove(item);
        });
        PlayMusicItemCommand = new RelayCommand<SpotifyItemConfig>(item =>
        {
            if (item is null) return;
            _services.Actions.Execute(ShowActionKind.SpotifyPlay, ActionOrigin.Desk, item.Id);
        });
        ResumeMusicCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.SpotifyPlay, ActionOrigin.Desk));
        PauseMusicCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.SpotifyPause, ActionOrigin.Desk));
        SkipMusicCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.SpotifyNext, ActionOrigin.Desk));
        BrowseSpotifyPlaylistCommand = new RelayCommand(() =>
        {
            if (SelectedSpotifyPlaylist is not { } list)
            {
                StatusMessage = "Choose one of your playlists first — press Refresh my playlists after CONNECT.";
                return;
            }
            _ = BrowseSpotifyAsync(list.Uri);
        });
        BrowseSpotifyLinkCommand = new RelayCommand(() =>
        {
            if (!SpotifyUri.TryParse(MusicLinkDraft, out var r))
            {
                StatusMessage = "Paste a Spotify playlist, album or artist link to browse its songs.";
                return;
            }
            _ = BrowseSpotifyAsync(r.Uri);
        });
        AddSpotifyTrackCommand = new RelayCommand(() =>
        {
            if (SelectedSpotifyTrack is not { } track)
            {
                StatusMessage = "Pick a song in the list first.";
                return;
            }
            AddMusicEntry(track.Uri, track.Line);
        });
        SearchSpotifyCommand = new RelayCommand(() => _ = SearchSpotifyAsync());
        AddSpotifySearchHitCommand = new RelayCommand(() =>
        {
            if (SelectedSpotifySearchHit is not { } hit)
            {
                StatusMessage = "Pick a result first.";
                return;
            }
            AddMusicEntry(hit.Uri, hit.EntryName);
        });
        RefreshSpotifyDevices();
        RefreshLookMusicChoices();

        // VOG / stinger: the desk's own chips assert the kind, so a panel that is stale after a
        // re-kind on the Audio page refuses rather than surprises.
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
        RefreshStingerGroups();
        RefreshAfterChoices();
        _services.Stingers.Changed += RefreshTallies; // a session ending on the service's own timer lights the rows off

        // Streaming
        while (State.Stream.Destinations.Count < 2)
        {
            State.Stream.Destinations.Add(new StreamDestinationConfig());
        }
        StartStreamCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.StreamStart, ActionOrigin.Desk));
        StopStreamCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.StreamStop, ActionOrigin.Desk));

        // Multiview
        AddMultiviewTileCommand = new RelayCommand(() =>
            ActivePattern.Multiview.Tiles.Add(new MultiviewTileConfig()));
        RemoveMultiviewTileCommand = new RelayCommand<MultiviewTileConfig>(tile =>
        {
            if (tile is not null) ActivePattern.Multiview.Tiles.Remove(tile);
        });

        // Prep mode: planned screens and adoption
        AddPlannedScreenCommand = new RelayCommand(() => AddPlannedScreen());
        RemovePlannedScreenCommand = new RelayCommand<ScreenPlacement>(p =>
        {
            if (p is not null) RemovePlannedScreen(p);
        });
        AdoptPlannedScreenCommand = new RelayCommand<ScreenPlacement>(p =>
        {
            if (p is null) return;
            if (!AdoptPlannedScreen(p, p.AdoptTargetId))
            {
                StatusMessage = "Choose which detected display this planned screen becomes.";
            }
        });
        RefreshAdoptTargetsCommand = new RelayCommand(RefreshAdoptTargets);

        // Admin: graphics choice + restart + folder
        RestartAppCommand = new RelayCommand(RestartApp);
        OpenAppFolderCommand = new RelayCommand(OpenAppFolder);
        State.Admin.Graphics.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GraphicsConfig.Preference) or nameof(GraphicsConfig.AdapterName))
            {
                OnGraphicsChoiceChanged();
            }
        };
        RebuildGpuRows();

        // Switcher: sandbox sends, CUT/TAKE, tile selection
        TakeCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk));
        CutCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.Cut, ActionOrigin.Desk));
        SandboxSendAllCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk));
        SandboxSendSelectedCommand = new RelayCommand(() =>
        {
            if (!_services.Sandbox.Active) return;
            var picked = SwitcherTiles.Where(t => t.IsSendTarget && t.TargetId is not null).ToList();
            if (picked.Count == 0)
            {
                StatusMessage = "Tick the tiles to send to first.";
                return;
            }
            var titles = string.Join(", ", picked.Select(t => t.Title));
            // A tile is a content target: a joined canvas takes the look as one picture.
            _services.Sandbox.SendToTargets(picked.Select(t => t.TargetId!).ToList());
            ClearSendTargets();
            Raise(nameof(IsSandboxActive));
            RebuildEditTargets(); // the targets now show their own pattern — OWN lights up
            StatusMessage = $"Sandbox sent to {titles} as their own pattern." +
                            (_services.Sandbox.Active ? " EDIT SAFE re-armed." : "");
        });
        SelectTileCommand = new RelayCommand<SwitcherTile>(tile =>
        {
            if (tile is null) return;
            // The editors work on the tile's own pattern when it has one, else on the program;
            // the big panes show the tile either way.
            EditTarget = (tile.IsOwn ? EditTargets.FirstOrDefault(t => t.ScreenId == tile.TargetId) : null) ?? EditTargets[0];
            SelectTarget(tile.TargetId);
            StatusMessage = EditTargetBanner;
        });
        ArmAllCommand = new RelayCommand(() =>
        {
            _services.Arming.ArmAll();
            StatusMessage = "Every target armed — the next CUT / TAKE goes everywhere.";
        });

        // Looks & cues
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
            StatusMessage = $"Look '{look.Name}' updated with the current state.";
        });
        DeleteLookCommand = new RelayCommand<LookConfig>(look =>
        {
            if (look is null) return;
            // Orphaned references fail silently at show time; refuse and say what points here.
            var refs = LookService.References(State, look);
            if (refs.Count > 0)
            {
                StatusMessage = $"'{look.Name}' is still used by {string.Join(", ", refs)} — remove those first.";
                return;
            }
            State.LooksAndCues.Looks.Remove(look);
            Raise(nameof(LookNames));
        });
        AddCueCommand = new RelayCommand(() =>
        {
            State.LooksAndCues.Cues.Add(new CueConfig
            {
                LookName = State.LooksAndCues.Looks.FirstOrDefault()?.Name ?? "",
            });
        });
        RemoveCueCommand = new RelayCommand<CueConfig>(cue =>
        {
            if (cue is not null) State.LooksAndCues.Cues.Remove(cue);
        });

        // Audio, feed, trims
        ToneFrequencyCommand = new RelayCommand<string>(f =>
        {
            if (double.TryParse(f, out var hz)) State.Tone.FrequencyHz = hz;
        });
        RefreshFeedCommand = new RelayCommand(() => _services.Feeds.RefreshNow());
        ResetTrimsCommand = new RelayCommand(() =>
        {
            if (_selectedPlacement is null) return;
            _selectedPlacement.BrightnessPct = 100;
            _selectedPlacement.Gamma = 1.0;
            _selectedPlacement.TrimRPct = 100;
            _selectedPlacement.TrimGPct = 100;
            _selectedPlacement.TrimBPct = 100;
            RaiseSelection();
        });

        // LED map
        AddLedTileCommand = new RelayCommand(AddLedTile);
        RemoveLedTileCommand = new RelayCommand(() =>
        {
            if (SelectedLedTile is { } tile)
            {
                ActivePattern.LedWall.CustomTiles.Remove(tile);
                SelectedLedTile = ActivePattern.LedWall.CustomTiles.LastOrDefault();
            }
        });
        ImportGridToMapCommand = new RelayCommand(ImportGridToMap);

        _services.SnapshotPublished += OnSnapshotPublished;
        _services.Outputs.LiveChanged += RefreshOutputsStatus;
        _services.Outputs.LiveChanged += RefreshSwitcherTiles; // tally follows the outputs
        _services.Arming.Changed += () =>
        {
            RefreshSwitcherTiles();
            RefreshTakeScope();
        };
        Cues = new CueEditor(_services, message => StatusMessage = message);
        Run = new RunViewModel(_services, this);
        _services.Cues.Changed += () =>
        {
            Raise(nameof(ClickerArmed));
            Raise(nameof(PresenterStepText));
        };
        ShowControls = new ShowControls(_services, m => StatusMessage = m);
        CaptureFormat = new CaptureFormatPicker(() => State, () => ActivePattern.Media.CaptureDevice, () => _services.RepublishNow());
        PipCaptureFormat = new CaptureFormatPicker(() => State, () => State.Overlays.Pip.CaptureDevice, () => _services.RepublishNow());
        ApplyDisplayModeCommand = new RelayCommand(ApplyDisplayMode);
        KeepDisplayModeCommand = new RelayCommand(KeepDisplayMode);
        RevertDisplayModeCommand = new RelayCommand(RevertDisplayMode);
        SelectGroupCommand = new RelayCommand<ShellGroup>(SelectGroup);
        SelectPageCommand = new RelayCommand<int>(SelectPage);
        SelectPrepCommand = new RelayCommand(() =>
        {
            if (!LeaveRun()) return;
            IsPrepMode = true;
            RaiseShell();
        });
        SelectShowCommand = new RelayCommand(() =>
        {
            if (!LeaveRun()) return;
            IsPrepMode = false;
            RaiseShell();
        });
        SelectRunCommand = new RelayCommand(() => SelectPage(Shell.RunPage));
        PopOutRunCommand = new RelayCommand(() =>
        {
            if (_runWindow is { IsVisible: true })
            {
                _runWindow.Activate();
                return;
            }
            _runWindow = new Views.RunWindow { DataContext = this };
            _runWindow.Closed += (_, _) => _runWindow = null;
            _runWindow.Show();
            StatusMessage = "Run window opened — its Enter, ↑ ↓ and Esc work while it has focus.";
        });
        ToggleRunLayoutCommand = new RelayCommand(() => IsRunLayout = !IsRunLayout); // refused while armed, in SelectPage
        _services.Screens.Changed += OnScreensChanged;

        var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statusTimer.Tick += (_, _) => PollStatus();
        statusTimer.Start();

        ReconcilePlacements();
        BuildLibrary();
        RefreshOutputsStatus();
    }

    public ShowState State => _services.State;
    public AppServices Services => _services;

    // ---- screen arrangement -------------------------------------------------

    private void OnScreensChanged()
    {
        AdoptRenamedDisplay();
        ReconcilePlacements();
        RefreshOutputsStatus();
    }

    // ---- frame rate ---------------------------------------------------------

    public FpsOption[] MasterFpsOptions => FpsOption.Master;
    public FpsOption[] ScreenFpsOptions => FpsOption.Screen;

    /// <summary>The show's frame rate: outputs pace to it, an NDI sender on "master" sends at it, the stream can follow it.</summary>
    public int MasterFps
    {
        get => State.Output.MasterFps;
        set
        {
            if (State.Output.MasterFps == value) return;
            State.Output.MasterFps = value;
            Raise();
            if (_services.Outputs.IsLive) _services.Outputs.Apply(); // the windows re-read their viewports
        }
    }

    /// <summary>The selected screen's own rate; 0 follows the master.</summary>
    public int SelectedFpsOverride
    {
        get => _selectedPlacement?.FpsOverride ?? 0;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.FpsOverride == value) return;
            _selectedPlacement.FpsOverride = value;
            Raise();
            if (_services.Outputs.IsLive) _services.Outputs.Apply();
        }
    }

    // ---- display modes ------------------------------------------------------

    private string _displayModeStatus = "";
    private bool _displayModePending;
    private string _selectedDisplayModeLabel = "";
    private readonly List<DisplayMode> _displayModes = new();
    private (string Device, DisplayMode Previous, string ScreenId, int Index)? _modeChange;
    private DispatcherTimer? _modeRevertTimer;

    /// <summary>The modes the selected display offers ("1920×1080 @ 60 Hz"); its current mode first.</summary>
    public ObservableCollection<string> DisplayModeOptions { get; } = new();

    public string SelectedDisplayModeLabel { get => _selectedDisplayModeLabel; set => Set(ref _selectedDisplayModeLabel, value ?? ""); }

    /// <summary>A sentence about the display's mode: what it is in, what a change did, or why none is possible here.</summary>
    public string DisplayModeStatus { get => _displayModeStatus; private set => Set(ref _displayModeStatus, value); }

    /// <summary>A change was applied and waits for KEEP — REVERT, or fifteen seconds, puts the old mode back.</summary>
    public bool DisplayModePending { get => _displayModePending; private set => Set(ref _displayModePending, value); }

    public RelayCommand ApplyDisplayModeCommand { get; }
    public RelayCommand KeepDisplayModeCommand { get; }
    public RelayCommand RevertDisplayModeCommand { get; }

    private void RefreshDisplayModes()
    {
        _displayModes.Clear();
        DisplayModeOptions.Clear();
        if (_selectedPlacement is null || LiveInfo(_selectedPlacement) is not { IsPlanned: false } info)
        {
            DisplayModeStatus = "";
            return;
        }
        if (!DisplayModes.Supported)
        {
            DisplayModeStatus = "Display modes can only be changed on Windows.";
            return;
        }
        var device = DisplayModes.DeviceFor(info.Bounds);
        var current = device is null ? null : DisplayModes.Current(device);
        foreach (var m in device is null ? Array.Empty<DisplayMode>() : DisplayModes.List(device))
        {
            _displayModes.Add(m);
            DisplayModeOptions.Add(m.Label);
        }
        if (current is { } cur)
        {
            SelectedDisplayModeLabel = cur.Label;
            if (!_displayModePending) DisplayModeStatus = $"Now {cur.Label}.";
        }
        else
        {
            DisplayModeStatus = "This display could not be matched to a Windows display device.";
        }
    }

    private void ApplyDisplayMode()
    {
        if (_selectedPlacement is null || LiveInfo(_selectedPlacement) is not { IsPlanned: false } info) return;
        var pick = _displayModes.FirstOrDefault(m => m.Label == SelectedDisplayModeLabel);
        if (pick == default)
        {
            DisplayModeStatus = "Pick a mode first.";
            return;
        }
        var device = DisplayModes.DeviceFor(info.Bounds);
        if (device is null || DisplayModes.Current(device) is not { } previous)
        {
            DisplayModeStatus = "This display could not be matched to a Windows display device.";
            return;
        }
        if (previous == pick)
        {
            DisplayModeStatus = $"Already {pick.Label}.";
            return;
        }
        if (_services.Outputs.IsLive)
        {
            DisplayModeStatus = "Close the outputs (OUTPUTS OFF) before changing a display mode.";
            return;
        }
        _modeChange = (device, previous, _selectedPlacement.ScreenId, info.Index);
        var error = DisplayModes.Apply(device, pick);
        if (error.Length > 0)
        {
            _modeChange = null;
            DisplayModeStatus = error;
            return;
        }
        DisplayModePending = true;
        DisplayModeStatus = $"Changed to {pick.Label} — KEEP it, or it reverts to {previous.Label} in 15 s.";
        _modeRevertTimer?.Stop();
        _modeRevertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _modeRevertTimer.Tick += (_, _) => RevertDisplayMode();
        _modeRevertTimer.Start();
        Log.Info($"Display mode change on {device}: {previous.Label} → {pick.Label}.");
    }

    private void KeepDisplayMode()
    {
        if (!DisplayModePending) return;
        _modeRevertTimer?.Stop();
        _modeRevertTimer = null;
        DisplayModePending = false;
        // The rename has happened once the display is back under the id the change names; if
        // Windows' topology event is still on its way, the hook finishes and forgets the change.
        if (_modeChange is { } change && _services.Screens.Real.Any(s => s.Id == change.ScreenId)) _modeChange = null;
        DisplayModeStatus = $"Kept {SelectedDisplayModeLabel}.";
        Log.Info("Display mode kept.");
    }

    private void RevertDisplayMode()
    {
        _modeRevertTimer?.Stop();
        _modeRevertTimer = null;
        if (!DisplayModePending || _modeChange is not { } change) return;
        DisplayModePending = false;
        var error = DisplayModes.Apply(change.Device, change.Previous);
        DisplayModeStatus = error.Length > 0 ? $"Could not revert: {error}" : $"Reverted to {change.Previous.Label}.";
        // The rename hook below moves the placement back to the display's restored id.
        Log.Info("Display mode reverted.");
    }

    /// <summary>
    /// A display whose mode just changed comes back from Windows with a new id (ids embed the
    /// geometry). Before the arrangement is reconciled — which would add a fresh placement for
    /// the "new" display and orphan the old one — move everything programmed against the old id
    /// onto the new one: the placement, its pattern, its canvases, senders, tiles and looks.
    /// </summary>
    private void AdoptRenamedDisplay()
    {
        if (_modeChange is not { } change) return;
        // After KEEP or REVERT this is the last topology event the change may act on: a later
        // hot-plug must never move a placement onto whichever display took the index.
        var settled = !DisplayModePending;
        var placement = State.Output.Placements.FirstOrDefault(p => p.ScreenId == change.ScreenId);
        var survived = _services.Screens.Real.Any(s => s.Id == change.ScreenId); // the id survived: nothing moved
        var replacement = survived ? null : _services.Screens.Real.FirstOrDefault(s => s.Index == change.Index);
        if (placement is null || survived || replacement is null || State.Output.Placements.Any(p => p.ScreenId == replacement.Id))
        {
            if (settled) _modeChange = null;
            return;
        }

        var oldId = change.ScreenId;
        _services.BulkEdit(() => ContentTargets.RenameScreen(State, oldId, replacement.Id));
        if (_services.Sandbox.ProgramState is { } air) ContentTargets.RenameScreen(air, oldId, replacement.Id);
        _services.RepublishNow();
        _modeChange = settled ? null : change with { ScreenId = replacement.Id };
        if (_selectedPlacement == placement) RaiseSelection();
        Log.Info($"Display re-identified after a mode change: {oldId} → {replacement.Id}.");
    }

    public void ReconcilePlacements() => ReconcilePlacements(_services.Screens.All.ToList());

    /// <summary>
    /// Displays that physically exist among the given list. The "primary goes off when there
    /// are other screens" default must count these only — a planned screen has no hardware,
    /// so letting it tip the count would turn off the operator's one real output.
    /// </summary>
    private static int RealCount(IReadOnlyList<ScreenInfo> screens)
    {
        var n = 0;
        foreach (var s in screens)
        {
            if (!s.IsPlanned) n++;
        }
        return n;
    }

    /// <summary>
    /// Keeps placements in sync with detected screens: new screens appear to the right of the
    /// arrangement (disconnected), and — until the operator pins a choice — the primary screen
    /// defaults to disabled whenever other screens exist, so GO never covers the control UI.
    /// </summary>
    public void ReconcilePlacements(IReadOnlyList<ScreenInfo> screens)
    {
        var placements = State.Output.Placements;

        foreach (var screen in screens)
        {
            if (placements.All(p => p.ScreenId != screen.Id))
            {
                var maxRight = 0;
                foreach (var p in placements)
                {
                    var info = screens.FirstOrDefault(s => s.Id == p.ScreenId);
                    if (info is not null) maxRight = Math.Max(maxRight, p.X + info.Bounds.Width);
                }
                placements.Add(new ScreenPlacement
                {
                    ScreenId = screen.Id,
                    X = placements.Count == 0 ? 0 : maxRight + 120,
                    Y = 0,
                    Enabled = !(screen.IsPrimary && RealCount(screens) > 1),
                });
            }
        }

        // Re-evaluate the default for anything the user hasn't pinned.
        foreach (var p in placements)
        {
            if (p.UserPinned) continue;
            var info = screens.FirstOrDefault(s => s.Id == p.ScreenId);
            if (info is not null)
            {
                p.Enabled = !(info.IsPrimary && RealCount(screens) > 1);
            }
        }

        if (_selectedPlacement is null || placements.All(p => p != _selectedPlacement))
        {
            SelectedPlacement = placements.FirstOrDefault(p => LiveInfo(p) is not null);
        }

        EnsureAssignmentsForCustomScreens();
        RebuildEditTargets();
        RebuildNdiSources();
        RebuildWebScreens();
        RaiseArrangement();
        // Loading a show, or plugging a display in, can change the mode and the planned set.
        RefreshOutputsStatus();
    }

    public ScreenInfo? LiveInfo(ScreenPlacement placement)
        => _services.Screens.All.FirstOrDefault(s => s.Id == placement.ScreenId);

    public ScreenPlacement? SelectedPlacement
    {
        get => _selectedPlacement;
        set
        {
            if (Set(ref _selectedPlacement, value))
            {
                RaiseSelection();
            }
        }
    }

    public bool HasSelection => _selectedPlacement is not null && LiveInfo(_selectedPlacement) is not null;

    public string SelectedScreenTitle
    {
        get
        {
            if (_selectedPlacement is null) return "No screen selected";
            var info = LiveInfo(_selectedPlacement);
            return info is null
                ? "Offline screen (from a saved show)"
                : $"{info.Label} — {info.Bounds.Width}×{info.Bounds.Height} @ {info.Scaling:0.##}×{(info.IsPrimary ? " · primary" : "")}";
        }
    }

    public bool SelectedEnabled
    {
        get => _selectedPlacement?.Enabled ?? false;
        set
        {
            if (_selectedPlacement is null) return;
            _selectedPlacement.Enabled = value;
            _selectedPlacement.UserPinned = true;
            RaiseSelection();
        }
    }

    public bool SelectedUseCustom
    {
        get => _selectedPlacement?.UseCustomPattern ?? false;
        set
        {
            if (_selectedPlacement is null) return;
            _selectedPlacement.UseCustomPattern = value;
            if (value)
            {
                EnsureAssignment(_selectedPlacement.ScreenId);
            }
            RebuildEditTargets();
            if (value)
            {
                EditTarget = EditTargets.FirstOrDefault(t => t.ScreenId == _selectedPlacement.ScreenId) ?? EditTargets[0];
            }
            RaiseSelection();
        }
    }

    /// <summary>A display with hardware behind it — the only kind an output window opens on, so the only kind direct output applies to.</summary>
    public bool SelectedIsDisplay => _selectedPlacement is { Planned: false } p && LiveInfo(p) is { IsPlanned: false, IsVirtual: false };

    /// <summary>Bypass the desktop compositor on the selected output (the Screens page's tick).</summary>
    public bool SelectedDirectOutput
    {
        get => _selectedPlacement?.DirectOutput ?? false;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.DirectOutput == value) return;
            _selectedPlacement.DirectOutput = value;
            if (value) DirectOutputService.ClearFuse(); // ticking again after a held-off start is the retry
            _services.Outputs.OnScreensChanged();       // a live window takes its window-side part now
            RaiseSelection();
            Raise(nameof(DirectOutputSummary));
        }
    }

    /// <summary>The selected output's direct-output line: in force, waiting for a restart, or why not.</summary>
    public string DirectOutputStatus => _selectedPlacement is null ? "" : DirectOutputService.Status(State, _selectedPlacement);

    /// <summary>The Machine page's line: how many outputs ask, what is in force, what the next start does.</summary>
    public string DirectOutputSummary => DirectOutputService.Summary(State);

    /// <summary>Custom patterns only make sense on stand-alone screens (groups span the program).</summary>
    public bool SelectedIsGrouped
    {
        get
        {
            if (_selectedPlacement is null) return false;
            var arranged = BuildArranged();
            var mine = arranged.FirstOrDefault(a => a.Id == _selectedPlacement.ScreenId);
            if (mine.Id is null) return false;
            return ScreenLayout.Groups(arranged).First(g => g.Any(a => a.Id == mine.Id)).Count > 1;
        }
    }

    public List<ArrangedScreen> BuildArranged()
    {
        var result = new List<ArrangedScreen>();
        foreach (var p in State.Output.Placements)
        {
            var info = LiveInfo(p);
            if (info is not null && p.Enabled)
            {
                var size = OutputWindowManager.EffectiveSize(p, info);
                result.Add(new ArrangedScreen(p.ScreenId, SKRectI.Create(p.X, p.Y, size.Width, size.Height), p.BlendAuto));
            }
        }
        return result;
    }

    // ---- edge blend ---------------------------------------------------------

    public bool SelectedBlendAuto
    {
        get => _selectedPlacement?.BlendAuto ?? false;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.BlendAuto == value) return;
            _selectedPlacement.BlendAuto = value;
            ReconcilePlacements(); // an overlap may now join (or leave) a canvas
            RaiseSelection();
        }
    }

    public int SelectedBlendLeft
    {
        get => _selectedPlacement?.BlendLeftPx ?? 0;
        set { if (_selectedPlacement is { } p) { p.BlendLeftPx = value; RaiseBlend(); } }
    }

    public int SelectedBlendTop
    {
        get => _selectedPlacement?.BlendTopPx ?? 0;
        set { if (_selectedPlacement is { } p) { p.BlendTopPx = value; RaiseBlend(); } }
    }

    public int SelectedBlendRight
    {
        get => _selectedPlacement?.BlendRightPx ?? 0;
        set { if (_selectedPlacement is { } p) { p.BlendRightPx = value; RaiseBlend(); } }
    }

    public int SelectedBlendBottom
    {
        get => _selectedPlacement?.BlendBottomPx ?? 0;
        set { if (_selectedPlacement is { } p) { p.BlendBottomPx = value; RaiseBlend(); } }
    }

    public BlendCurve SelectedBlendCurve
    {
        get => _selectedPlacement?.BlendCurve ?? BlendCurve.SCurve;
        set { if (_selectedPlacement is { } p) { p.BlendCurve = value; RaiseBlend(); } }
    }

    public double SelectedBlendGamma
    {
        get => _selectedPlacement?.BlendGamma ?? 1.0;
        set { if (_selectedPlacement is { } p) { p.BlendGamma = value; RaiseBlend(); } }
    }

    /// <summary>The zones this output will actually fade, in its own pixels — derived from the overlaps when automatic.</summary>
    public string BlendReadback
    {
        get
        {
            if (_selectedPlacement is not { } p) return "";
            var arranged = BuildArranged();
            var mine = arranged.FirstOrDefault(a => a.Id == p.ScreenId);
            if (mine.Id is null) return "This screen is not in the arrangement.";
            var derived = EdgeBlend.Derive(mine.Rect, arranged.Where(a => a.Id != mine.Id).Select(a => a.Rect));
            var used = EdgeBlend.Resolve(p, derived);
            var words = $"left {used.Left} · top {used.Top} · right {used.Right} · bottom {used.Bottom} px";
            if (!p.BlendAuto) return used.Any ? $"Fading {words}." : "No blend on this output.";
            return used.Any
                ? $"Overlaps found: {words} — both projectors draw the overlap, faded."
                : "No overlap with another screen yet — drag this screen over its neighbour by the overlap width.";
        }
    }

    private void RaiseBlend()
    {
        Raise(nameof(BlendReadback));
        Raise(nameof(SelectedBlendLeft));
        Raise(nameof(SelectedBlendTop));
        Raise(nameof(SelectedBlendRight));
        Raise(nameof(SelectedBlendBottom));
        Raise(nameof(SelectedBlendCurve));
        Raise(nameof(SelectedBlendGamma));
        Raise(nameof(SelectedBlendAuto));
    }

    private void ResetBlend()
    {
        if (_selectedPlacement is not { } p) return;
        p.BlendAuto = false;
        p.BlendLeftPx = p.BlendTopPx = p.BlendRightPx = p.BlendBottomPx = 0;
        p.BlendCurve = BlendCurve.SCurve;
        p.BlendGamma = 1.0;
        ReconcilePlacements();
        RaiseSelection();
    }

    public string GroupSummary
    {
        get
        {
            var arranged = BuildArranged();
            if (arranged.Count == 0) return "No screens enabled — outputs would be empty.";
            var groups = ScreenLayout.Groups(arranged);
            var parts = new List<string>();
            var canvasIndex = 0;
            foreach (var g in groups.Where(g => g.Count > 1))
            {
                var u = ScreenLayout.Union(g);
                parts.Add($"Canvas {(char)('A' + canvasIndex++)}: {g.Count} screens · {u.Width}×{u.Height} px");
            }
            var singles = groups.Count(g => g.Count == 1);
            if (singles > 0) parts.Add($"{singles} single screen{(singles == 1 ? "" : "s")}");
            return string.Join("   ·   ", parts);
        }
    }

    private void ResetLayout()
    {
        var screens = _services.Screens.All.ToList();
        _services.BulkEdit(() =>
        {
            State.Output.Placements.Clear();
        });
        ReconcilePlacements(screens);
        StatusMessage = "Screen layout reset.";
    }

    private void RaiseArrangement()
    {
        Raise(nameof(GroupSummary));
        Raise(nameof(SelectedIsGrouped));
        Raise(nameof(BlendReadback));
    }

    private void RaiseSelection()
    {
        Raise(nameof(HasSelection));
        Raise(nameof(SelectedScreenTitle));
        Raise(nameof(SelectedEnabled));
        Raise(nameof(SelectedUseCustom));
        Raise(nameof(SelectedIsGrouped));
        Raise(nameof(GroupSummary));
        Raise(nameof(SelectedRotation));
        Raise(nameof(SelectedBrightness));
        Raise(nameof(SelectedGamma));
        Raise(nameof(SelectedTrimR));
        Raise(nameof(SelectedTrimG));
        Raise(nameof(SelectedTrimB));
        Raise(nameof(SelectedScreenLabel));
        Raise(nameof(SelectedCanvasName));
        Raise(nameof(SelectedIsInCanvas));
        Raise(nameof(SelectedFpsOverride));
        Raise(nameof(SelectedIsDisplay));
        Raise(nameof(SelectedDirectOutput));
        Raise(nameof(DirectOutputStatus));
        Raise(nameof(SelectedRole));
        Raise(nameof(SelectedFollowsCues));
        Raise(nameof(SelectedMirrorOf));
        RebuildMirrorSources();
        RaiseBlend();
        RefreshDisplayModes();
    }

    // ---- layers and drags on the PREVIEW pane ---------------------------------------

    public EnumItem[] LayerSources => Lists.LayerSources;

    public RelayCommand<LayerConfig> BrowseLayerImageCommand { get; }
    public RelayCommand<LayerConfig> BrowseLayerVideoCommand { get; }

    /// <summary>The pattern the PREVIEW pane shows, in the live model: the target's own, its source's when it repeats one, else the program.</summary>
    public PatternConfig PreviewPattern
    {
        get
        {
            var id = _services.PreviewScreenId;
            if (id is null) return State.Pattern;
            id = ScreenRoles.ResolveMirror(State, id);
            if (!ContentTargets.UsesOwnPattern(State, id)) return State.Pattern;
            return State.Independent.FirstOrDefault(a => a.ScreenId == id)?.Pattern ?? State.Pattern;
        }
    }

    public static string DragName(HitKind kind) => kind switch
    {
        HitKind.Layer1 => "Layer 1",
        HitKind.Layer2 => "Layer 2",
        HitKind.Logo => "The logo",
        HitKind.Clock => "The clock",
        HitKind.Countdown => "The countdown",
        HitKind.Message => "The message",
        HitKind.Pip => "The PiP inset",
        HitKind.WebPage => "The web page",
        _ => "The element",
    };

    /// <summary>Where a draggable thing sits now: a layer's box (a share of the canvas) or an overlay's nudge from its anchor.</summary>
    public (double X, double Y) DragPlaceOf(HitKind kind)
    {
        var p = PreviewPattern;
        var o = State.Overlays;
        return kind switch
        {
            HitKind.Layer1 => (p.Layer1.XPct, p.Layer1.YPct),
            HitKind.Layer2 => (p.Layer2.XPct, p.Layer2.YPct),
            HitKind.Logo => (o.Logo.OffsetXPct, o.Logo.OffsetYPct),
            HitKind.Clock => (o.Clock.OffsetXPct, o.Clock.OffsetYPct),
            HitKind.Countdown => (State.Countdown.OffsetXPct, State.Countdown.OffsetYPct),
            HitKind.Message => (o.Message.OffsetXPct, o.Message.OffsetYPct),
            HitKind.Pip => (o.Pip.OffsetXPct, o.Pip.OffsetYPct),
            _ => (0, 0),
        };
    }

    /// <summary>Puts a draggable thing at a place (the same units <see cref="DragPlaceOf"/> reads); the model publishes, the panes follow.</summary>
    public void DragPlace(HitKind kind, double x, double y)
    {
        var p = PreviewPattern;
        var o = State.Overlays;
        switch (kind)
        {
            case HitKind.Layer1: p.Layer1.XPct = x; p.Layer1.YPct = y; break;
            case HitKind.Layer2: p.Layer2.XPct = x; p.Layer2.YPct = y; break;
            case HitKind.Logo: o.Logo.OffsetXPct = x; o.Logo.OffsetYPct = y; break;
            case HitKind.Clock: o.Clock.OffsetXPct = x; o.Clock.OffsetYPct = y; break;
            case HitKind.Countdown: State.Countdown.OffsetXPct = x; State.Countdown.OffsetYPct = y; break;
            case HitKind.Message: o.Message.OffsetXPct = x; o.Message.OffsetYPct = y; break;
            case HitKind.Pip: o.Pip.OffsetXPct = x; o.Pip.OffsetYPct = y; break;
        }
    }

    // ---- web pages inside the engine -----------------------------------------------

    private string _webTypedText = "";
    public string WebTypedText { get => _webTypedText; set => Set(ref _webTypedText, value ?? ""); }

    private string _webControlsTarget = "";
    /// <summary>Which page the PAGE CONTROLS drive, in words.</summary>
    public string WebControlsTarget { get => _webControlsTarget; private set => Set(ref _webControlsTarget, value); }

    private string _webPageStatus = "";
    public string WebPageStatus { get => _webPageStatus; private set => Set(ref _webPageStatus, value); }

    private string _lastWebKey = "";

    /// <summary>The desk pointed at a page on the PREVIEW pane: the controls follow it.</summary>
    public void NoteWebPage(string key)
    {
        if (key.Length == 0 || key == _lastWebKey) return;
        _lastWebKey = key;
        RefreshWebControls();
    }

    /// <summary>The page the controls drive: the one last pointed at while it is mounted, else the pattern's page, else its first web layer.</summary>
    public string CurrentWebKey()
    {
        if (_lastWebKey.Length > 0 && InputBus.For(_lastWebKey) is IWebSource) return _lastWebKey;
        var p = ActivePattern;
        if (p.Kind == PatternKind.Media && p.Media.Source == MediaSource.Web && p.Media.WebUrl.Length > 0) return InputKeys.Web(p.Media.WebUrl);
        foreach (var l in new[] { p.Layer1, p.Layer2 })
        {
            if (l.Enabled && l.Source == LayerSource.Web && l.WebUrl.Length > 0) return InputKeys.Web(l.WebUrl);
        }
        return "";
    }

    public IWebSource? CurrentWebSource() => InputBus.For(CurrentWebKey()) as IWebSource;

    /// <summary>The Media page shows the page controls while a page is in play somewhere the desk can reach.</summary>
    public bool HasWebPage => CurrentWebKey().Length > 0;

    private string _savedWebPick = "";

    /// <summary>The saved pages combo on the Media page: picking one puts it in the pattern's page box.</summary>
    public string? SavedWebPick
    {
        get => _savedWebPick;
        set
        {
            var pick = value ?? "";
            if (pick == _savedWebPick) return;
            _savedWebPick = pick;
            if (pick.Length > 0) ActivePattern.Media.WebUrl = pick;
            Raise(nameof(SavedWebPick));
        }
    }

    /// <summary>Keeps the PAGE CONTROLS block honest: which page, whether it is up, what it shows. Cheap; runs on the 1 s poll.</summary>
    private void RefreshWebControls()
    {
        var key = CurrentWebKey();
        Raise(nameof(HasWebPage));
        if (key.Length == 0)
        {
            WebControlsTarget = "";
            WebPageStatus = "";
            return;
        }
        var page = InputBus.For(key) as IWebSource;
        var name = State.InputLabel(key, WebAddress.ShortName(key[4..]));
        WebControlsTarget = $"Controls drive: {name}" + (_lastWebKey == key ? " (the page you pointed at)" : "");
        WebPageStatus = page is null
            ? WebInput.AvailabilityNote.Length > 0 ? WebInput.AvailabilityNote : "Opening…"
            : $"{page.StatusText}{(page.Title.Length > 0 ? " — " + page.Title : "")} · {page.CurrentUrl}";
    }

    // ---- roles, locks and repeaters ---------------------------------------------

    public EnumItem[] ScreenRoleItems => Lists.ScreenRoles;

    /// <summary>What the selected screen is for; picking a role also picks its follow default.</summary>
    public ScreenRole SelectedRole
    {
        get => _selectedPlacement?.Role ?? ScreenRole.Main;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.Role == value) return;
            _selectedPlacement.Role = value;
            var follows = ScreenRoles.DefaultFollows(value);
            if (_selectedPlacement.FollowsCues != follows) SetLocked(_selectedPlacement.ScreenId, !follows);
            RaiseSelection();
        }
    }

    /// <summary>Off = locked: the screen keeps its picture through looks, cues, TAKE ALL and stingers.</summary>
    public bool SelectedFollowsCues
    {
        get => _selectedPlacement?.FollowsCues ?? true;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.FollowsCues == value) return;
            SetLocked(_selectedPlacement.ScreenId, !value);
            RaiseSelection();
        }
    }

    /// <summary>The target the selected screen repeats ("" = its own content); a repeater has no picture of its own.</summary>
    public string SelectedMirrorOf
    {
        get => _selectedPlacement?.MirrorOf ?? "";
        set
        {
            var wanted = value ?? "";
            if (_selectedPlacement is null || _selectedPlacement.MirrorOf == wanted) return;
            var placement = _selectedPlacement;
            _services.BulkEdit(() =>
            {
                placement.MirrorOf = wanted;
                if (wanted.Length > 0) placement.UseCustomPattern = false;
            });
            RebuildEditTargets();
            RaiseSelection();
        }
    }

    /// <summary>What the selected screen may repeat: nothing, or any other target that does not repeat it back.</summary>
    public ObservableCollection<EditTarget> MirrorSources { get; } = new();

    private void RebuildMirrorSources()
    {
        var wanted = new List<EditTarget> { new("— its own content", "") };
        var me = _selectedPlacement?.ScreenId;
        var geo = Rig.Geometry(State, _services.Screens.All);
        foreach (var key in geo.Targets)
        {
            if (key == me) continue;
            if (ContentTargets.IsCanvasKey(key))
            {
                if (me is not null && ContentTargets.Members(key).Contains(me)) continue;
            }
            else if (State.Output.Placements.FirstOrDefault(p => p.ScreenId == key)?.MirrorOf == me)
            {
                continue;
            }
            wanted.Add(new EditTarget(geo.LabelFor(State, key), key));
        }
        ReplaceIfChanged(MirrorSources, wanted);
    }

    /// <summary>A lock goes through the action layer (journaled, the sandbox and the air agree); OWN lights up when the lock gave the target its picture.</summary>
    private void SetLocked(string targetId, bool locked)
    {
        _services.Actions.Execute(new ShowAction(locked ? ShowActionKind.ScreenLock : ShowActionKind.ScreenUnlock, targetId), ActionOrigin.Desk);
        RebuildEditTargets();
        RefreshTakeScope();
    }

    // ---- custom labels ------------------------------------------------------

    /// <summary>The selected screen's operator label (Outputs page).</summary>
    public string SelectedScreenLabel
    {
        get => _selectedPlacement?.CustomLabel ?? "";
        set
        {
            if (_selectedPlacement is { } p && p.CustomLabel != value)
            {
                p.CustomLabel = value;
                RebuildEditTargets(); // labels ripple into the strip, targets and remotes
            }
        }
    }

    /// <summary>True when the selected screen is part of a joined canvas (shows the name box).</summary>
    public bool SelectedIsInCanvas =>
        _selectedPlacement is { } p && CanvasGroups().Any(g => g.Any(m => m.ScreenId == p.ScreenId));

    /// <summary>The name of the canvas containing the selected screen ("Main wall").</summary>
    public string SelectedCanvasName
    {
        get
        {
            if (_selectedPlacement is not { } p) return "";
            var group = CanvasGroups().FirstOrDefault(g => g.Any(m => m.ScreenId == p.ScreenId));
            if (group is null) return "";
            var key = CanvasNameConfig.KeyFor(group.Select(m => m.ScreenId));
            return State.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == key)?.Name ?? "";
        }
        set
        {
            if (_selectedPlacement is not { } p) return;
            var group = CanvasGroups().FirstOrDefault(g => g.Any(m => m.ScreenId == p.ScreenId));
            if (group is null) return;
            var key = CanvasNameConfig.KeyFor(group.Select(m => m.ScreenId));
            var entry = State.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == key);
            if (entry is null)
            {
                entry = new CanvasNameConfig { MemberKey = key };
                State.Output.CanvasNames.Add(entry);
            }
            if (entry.Name != value)
            {
                entry.Name = value;
                RebuildSwitcherTiles();
            }
        }
    }

    /// <summary>Nickname for the live input currently picked in Media (NDI feed or capture).</summary>
    public string InputNickname
    {
        get => CurrentInputKey() is { } key ? State.InputLabel(key, "") : "";
        set
        {
            if (CurrentInputKey() is not { } key) return;
            var entry = State.InputLabels.FirstOrDefault(l => l.Key == key);
            if (entry is null)
            {
                entry = new InputLabelConfig { Key = key };
                State.InputLabels.Add(entry);
            }
            entry.Label = value;
        }
    }

    private string? CurrentInputKey() => ActivePattern.Media.Source switch
    {
        MediaSource.NdiFeed when ActivePattern.Media.NdiSourceName.Length > 0 => "ndi:" + ActivePattern.Media.NdiSourceName,
        MediaSource.Capture when ActivePattern.Media.CaptureDevice.Length > 0 => "cap:" + ActivePattern.Media.CaptureDevice,
        MediaSource.Web when ActivePattern.Media.WebUrl.Length > 0 => InputKeys.Web(ActivePattern.Media.WebUrl),
        _ => null,
    };

    // ---- pattern editing target ---------------------------------------------

    public ObservableCollection<EditTarget> EditTargets { get; } = new() { new EditTarget("Program", null) };

    public EditTarget EditTarget
    {
        get => _editTarget;
        set
        {
            if (value is not null && Set(ref _editTarget, value))
            {
                Raise(nameof(ActivePattern));
                Raise(nameof(EditTargetBanner));
                // The panes follow the editors: a target with its own pattern is selected with
                // it; "Program" keeps the selected tile unless that tile shows its own picture.
                if (value.ScreenId is not null) SelectTarget(value.ScreenId);
                else if (_selectedTargetId is { } selected && ContentTargets.UsesOwnPattern(State, selected)) SelectTarget(null);
                RefreshSwitcherTiles();
                RaisePlaylistSection();
            }
        }
    }

    /// <summary>The pattern the editor panels bind to (program, or a custom screen's).</summary>
    public PatternConfig ActivePattern
    {
        get
        {
            if (_editTarget.ScreenId is { } id)
            {
                var a = State.Independent.FirstOrDefault(x => x.ScreenId == id);
                if (a is not null) return a.Pattern;
            }
            return State.Pattern;
        }
    }

    public bool ShowEditTargets => EditTargets.Count > 1;

    private void EnsureAssignmentsForCustomScreens()
    {
        foreach (var p in State.Output.Placements.Where(p => p.UseCustomPattern))
        {
            EnsureAssignment(p.ScreenId);
        }
        foreach (var c in State.Output.CanvasNames.Where(c => c.UseCustomPattern && c.MemberKey.Length > 0))
        {
            EnsureAssignment(c.MemberKey);
        }
    }

    private void EnsureAssignment(string targetId) => ContentTargets.EnsureAssignment(State, targetId);

    private void RebuildEditTargets()
    {
        RefreshAdoptTargets();
        var current = _editTarget?.ScreenId;
        EditTargets.Clear();
        EditTargets.Add(new EditTarget("Program", null));
        // A joined canvas with its own pattern is an edit target like a screen. A member screen
        // keeps its own entry too (its pattern is what the screen shows when split off again).
        var groups = CanvasGroups();
        for (var i = 0; i < groups.Count; i++)
        {
            var key = CanvasNameConfig.KeyFor(groups[i].Select(m => m.ScreenId));
            if (!ContentTargets.UsesOwnPattern(State, key)) continue;
            var letter = ((char)('A' + i)).ToString();
            EditTargets.Add(new EditTarget($"Canvas {letter} — {CanvasNameFor(groups[i], letter)}", key));
        }
        foreach (var p in State.Output.Placements.Where(p => p.UseCustomPattern))
        {
            var info = LiveInfo(p);
            if (info is not null)
            {
                EditTargets.Add(new EditTarget($"Screen {info.Index + 1} — {LabelFor(p, info)}", p.ScreenId));
            }
        }
        EditTarget = EditTargets.FirstOrDefault(t => t.ScreenId == current) ?? EditTargets[0];
        Raise(nameof(ShowEditTargets));
        RebuildSwitcherTiles();
    }

    // ---- media library ------------------------------------------------------

    private void AddToMediaLibrary(string path, bool isVideo)
    {
        if (State.MediaLibrary.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        State.MediaLibrary.Add(new MediaLibraryEntry
        {
            Path = path,
            IsVideo = isVideo,
            Kind = MediaLibraryEntry.KindOf(path, isVideo),
            AddedUtc = DateTime.UtcNow,
        });
        BuildLibrary();
    }

    // ---- playlist -----------------------------------------------------------

    private string _playlistStatus = "";
    public string PlaylistStatus { get => _playlistStatus; private set => Set(ref _playlistStatus, value); }

    private async Task AddPlaylistFilesAsync()
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add media to playlist",
                AllowMultiple = true,
                FileTypeFilter = new[] { MediaTypes, FilePickerFileTypes.All },
            });
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path is null) continue;
                ActivePlaylistSection.Items.Add(new PlaylistItemConfig { Path = path });
                AddToMediaLibrary(path, PlaylistSequencer.IsDecodedPath(path));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Playlist file picker failed.", ex);
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
                FileTypeFilter = new[] { MediaTypes, FilePickerFileTypes.All },
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
                AddToMediaLibrary(path, PlaylistSequencer.IsVideoPath(path));
            }
            if (skipped > 0) StatusMessage = $"VOGs and stingers are sounds or video clips — {skipped} other file{(skipped == 1 ? "" : "s")} skipped.";
            RefreshStingerGroups();
        }
        catch (Exception ex)
        {
            Log.Error("Stinger file picker failed.", ex);
        }
    }

    private async Task AddPlaylistFolderAsync()
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Add media folder",
                AllowMultiple = true,
            });
            foreach (var folder in folders)
            {
                var path = folder.TryGetLocalPath();
                if (path is not null && !ActivePlaylistSection.Folders.Contains(path))
                {
                    ActivePlaylistSection.Folders.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Playlist folder picker failed.", ex);
        }
    }

    private void MovePlaylistItem(PlaylistItemConfig? item, int delta)
    {
        if (item is null) return;
        var items = ActivePlaylistSection.Items;
        var index = items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= items.Count) return;
        items.Move(index, target);
    }

    /// <summary>The playlist part the editor shows and files land in (normalizes legacy lists).</summary>
    public PlaylistSectionConfig ActivePlaylistSection => PlaylistSequencer.ActiveSectionOf(ActivePattern.Media.Playlist);

    private PlaylistSectionConfig? _lastRaisedSection;

    /// <summary>Re-binds the section editor when the on-air part actually changes; keeps chips lit.</summary>
    private void RaisePlaylistSection(bool onlyOnChange = false)
    {
        var current = ActivePlaylistSection;
        foreach (var section in ActivePattern.Media.Playlist.Sections)
        {
            section.IsOnAir = ReferenceEquals(section, current);
        }
        if (onlyOnChange && ReferenceEquals(current, _lastRaisedSection)) return;
        _lastRaisedSection = current;
        Raise(nameof(ActivePlaylistSection));
    }

    private PlaylistItemConfig? _selectedPlaylistItem;
    public PlaylistItemConfig? SelectedPlaylistItem
    {
        get => _selectedPlaylistItem;
        set
        {
            if (Set(ref _selectedPlaylistItem, value)) Raise(nameof(HasPlaylistItemSelection));
        }
    }

    public bool HasPlaylistItemSelection => _selectedPlaylistItem is not null;

    /// <summary>Drag-reorder target from the playlist list (index clamped; no-ops in place).</summary>
    public void MovePlaylistItemTo(PlaylistItemConfig item, int targetIndex)
    {
        var items = ActivePlaylistSection.Items;
        var index = items.IndexOf(item);
        if (index < 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, items.Count - 1);
        if (targetIndex == index) return;
        items.Move(index, targetIndex);
    }

    // ---- NDI feed & capture inputs -----------------------------------------

    public ObservableCollection<string> NdiSourceOptions { get; } = new();
    public ObservableCollection<string> CaptureDeviceOptions { get; } = new();
    private bool _captureListLoaded;
    private int _ndiPollTick;

    private void RefreshNdiSources(bool quiet = false)
    {
        // Clearing the list nulls the combo selection, which would write "" into the
        // model — capture and restore the operator's choice around the rebuild.
        var current = ActivePattern.Media.NdiSourceName;
        var found = _services.NdiIn.DiscoverSources();
        NdiSourceOptions.Clear();
        foreach (var s in found)
        {
            NdiSourceOptions.Add(s);
        }
        if (!string.IsNullOrWhiteSpace(current) && !NdiSourceOptions.Contains(current))
        {
            NdiSourceOptions.Insert(0, current);
        }
        ActivePattern.Media.NdiSourceName = current;
        if (!quiet)
        {
            StatusMessage = found.Count == 0
                ? "No NDI sources visible yet — senders appear within a few seconds of starting."
                : $"{found.Count} NDI source{(found.Count == 1 ? "" : "s")} on the network.";
        }
    }

    private void RefreshCaptureDevices(bool quiet = false)
    {
        _captureListLoaded = true;
        var current = ActivePattern.Media.CaptureDevice;
        var found = CaptureDevices.List();
        CaptureDeviceOptions.Clear();
        foreach (var d in found)
        {
            CaptureDeviceOptions.Add(d);
        }
        if (!string.IsNullOrWhiteSpace(current) && !CaptureDeviceOptions.Contains(current))
        {
            CaptureDeviceOptions.Insert(0, current);
        }
        ActivePattern.Media.CaptureDevice = current;
        if (!quiet)
        {
            StatusMessage = found.Count == 0
                ? "No capture devices found (device lists need Windows)."
                : $"{found.Count} capture device{(found.Count == 1 ? "" : "s")} found.";
        }
    }

    // ---- web pages ----------------------------------------------------------

    public ObservableCollection<EditTarget> WebScreens { get; } = new();

    private string _webStatus = "";
    public string WebStatus { get => _webStatus; private set => Set(ref _webStatus, value); }

    private void RebuildWebScreens()
    {
        var current = State.Web.TargetScreenId;
        WebScreens.Clear();
        WebScreens.Add(new EditTarget("Primary screen", ""));
        // Real displays only: a browser window or a stream capture needs somewhere to land.
        foreach (var s in _services.Screens.Real)
        {
            WebScreens.Add(new EditTarget($"Screen {s.Index + 1} — {s.Label}", s.Id));
        }
        State.Web.TargetScreenId = current;
        RebuildStreamSources();

        RebuildMultiviewTargets();
    }

    // ---- multiview ----------------------------------------------------------

    /// <summary>Target choices for multiview tiles, in wall order: joined canvases, then every screen.</summary>
    public ObservableCollection<EditTarget> MultiviewTargets { get; } = new();

    private void RebuildMultiviewTargets()
    {
        var geo = Rig.Geometry(State, _services.Screens.All);
        var wanted = new List<EditTarget>();
        foreach (var key in geo.Targets)
        {
            if (ContentTargets.IsCanvasKey(key)) wanted.Add(new EditTarget(geo.LabelFor(State, key), key));
        }
        foreach (var s in geo.Screens)
        {
            wanted.Add(new EditTarget(geo.LabelFor(State, s.Id), s.Id));
        }
        ReplaceIfChanged(MultiviewTargets, wanted);
    }

    /// <summary>
    /// A picker bound two-way to a model id is rebuilt in place only when its entries really
    /// moved: clearing a ComboBox's items drops its selection, and the binding would write that
    /// empty selection back into the show. Same entries, same order = nothing happens.
    /// </summary>
    private static void ReplaceIfChanged(ObservableCollection<EditTarget> current, List<EditTarget> wanted)
    {
        if (current.Count == wanted.Count && current.SequenceEqual(wanted)) return;
        current.Clear();
        foreach (var t in wanted) current.Add(t);
    }

    // ---- presenter click-through -------------------------------------------

    /// <summary>Advances the presenter steps and applies the step's look. False = no move.</summary>
    public bool PresenterAdvance(int delta) => _services.Actions.PresenterAdvance(delta, ActionOrigin.Desk);

    public string PresenterStepText
    {
        get
        {
            var clicker = CueStacks.Clicker(State);
            var rt = _services.Cues.For(clicker);
            var count = clicker.Cues.Count;
            if (count == 0) return "No clicker cues yet — add them on the Cues page.";
            if (rt.CurrentIndex < 0) return $"Ready — {count} cue{(count == 1 ? "" : "s")}, click to start.";
            var cue = rt.CurrentIndex < count ? clicker.Cues[rt.CurrentIndex] : null;
            return cue is null ? $"Cue {rt.CurrentIndex + 1} of {count}" : $"Cue {rt.CurrentIndex + 1} of {count}: {cue.Name}";
        }
    }

    /// <summary>The clicker list's arm — a runtime chip, never saved: the app always opens disarmed.</summary>
    public bool ClickerArmed
    {
        get => _services.Cues.For(CueStacks.Clicker(State)).Armed;
        set
        {
            var rt = _services.Cues.For(CueStacks.Clicker(State));
            if (rt.Armed == value) return;
            _services.Actions.Execute(value ? ShowActionKind.ListArm : ShowActionKind.ListDisarm, ActionOrigin.Desk, CueStacks.Clicker(State).Id);
            Raise(nameof(ClickerArmed));
        }
    }

    /// <summary>The Cues page.</summary>
    public CueEditor Cues { get; }

    /// <summary>The Run surface's state and commands.</summary>
    public RunViewModel Run { get; }

    private bool _isRunLayout;

    /// <summary>
    /// The caller's one-column layout instead of the editors and the switcher. It is the Run
    /// page of the SHOW group: setting it selects that page (or the last Build page on the way
    /// out), and leaving is refused while the caller's stack is armed.
    /// </summary>
    public bool IsRunLayout
    {
        get => _isRunLayout;
        set
        {
            if (value == _isRunLayout) return;
            SelectPage(value ? Shell.RunPage : _lastBuildPage);
        }
    }

    private void SetRunLayout(bool value)
    {
        if (_isRunLayout == value) return;
        _isRunLayout = value;
        Raise(nameof(IsRunLayout));
        Raise(nameof(RunLayoutButtonText));
        if (value)
        {
            Run.Refresh();
            if (_services.RecoveryBanner.Length > 0)
            {
                Run.Banner = _services.RecoveryBanner;
                _services.RecoveryBanner = "";
            }
            StatusMessage = "RUN — Enter is GO while armed, ↑ ↓ move standby, Esc twice is STOP ALL. Space is still blackout.";
        }
    }

    // ---- the shell: five groups on the rail, a page strip, the PREP · SHOW · RUN selector ----

    private int _page = Shell.PanelPage;
    private ShellGroup _group = ShellGroup.Show;
    private int _lastBuildPage = Shell.PanelPage;
    private readonly Dictionary<ShellGroup, int> _lastPage = new() { [ShellGroup.Show] = Shell.PanelPage };

    /// <summary>The selected page as the window's TabControl index (two-way: a test picking a tab by header lands here too).</summary>
    public int SelectedPageIndex
    {
        get => _page;
        set => SelectPage(value);
    }

    public ShellGroup SelectedGroup => _group;

    /// <summary>The group buttons on the rail.</summary>
    public IReadOnlyList<GroupChip> GroupStrip => Shell.Groups.Select(g => new GroupChip(g.Group, g.Label, g.Hue, g.Hint, g.Group == _group)).ToList();

    /// <summary>The pages of the current group, as the strip shows them.</summary>
    public IReadOnlyList<PageChip> PageStrip => Shell.Pages.Where(p => p.Group == _group).Select(p => new PageChip(p.Index, p.Header, p.Hue, p.Index == _page)).ToList();

    public string GroupHint => Shell.Info(_group).Hint;

    /// <summary>
    /// Select a page. The Run page is the Run layout; leaving it is refused while the caller's
    /// stack is armed (the strip and the tab snap back), so a stray click cannot take the
    /// surface away mid-show.
    /// </summary>
    public void SelectPage(int index)
    {
        if (index < 0 || index >= Shell.Pages.Count) return;
        var run = index == Shell.RunPage;
        if (!run && _isRunLayout && _services.CueStack.Armed)
        {
            StatusMessage = "Disarm the cue stack before leaving the Run surface.";
            RaiseShell();
            // The TabControl that asked is mid-write on its two-way binding, which ignores a
            // source change raised inside that write: snap it back once the write has finished.
            Dispatcher.UIThread.Post(() =>
            {
                Raise(nameof(SelectedPageIndex));
                RaiseShell();
            });
            return;
        }
        var page = Shell.Pages[index];
        _page = index;
        _lastPage[page.Group] = index;
        if (!run) _lastBuildPage = index;
        _group = page.Group;
        Raise(nameof(SelectedPageIndex));
        SetRunLayout(run);
        RaiseShell();
        Raise(nameof(PageWantsRoom));
    }

    /// <summary>A group button: the group's last page, or its first; SHOW pressed while in Run goes to the panel.</summary>
    public void SelectGroup(ShellGroup group)
    {
        var index = _lastPage.TryGetValue(group, out var last) ? last : Shell.FirstPage(group);
        if (group == _group && _isRunLayout) index = Shell.PanelPage;
        SelectPage(index);
    }

    public RelayCommand<ShellGroup> SelectGroupCommand { get; private set; } = null!;
    public RelayCommand<int> SelectPageCommand { get; private set; } = null!;

    /// <summary>PREP · SHOW · RUN in the header: the first two are the show mode, RUN is the layout.</summary>
    public bool IsPrepSelected => !_isRunLayout && IsPrepMode;
    public bool IsShowSelected => !_isRunLayout && !IsPrepMode;
    public bool IsRunSelected => _isRunLayout;

    public RelayCommand SelectPrepCommand { get; private set; } = null!;
    public RelayCommand SelectShowCommand { get; private set; } = null!;
    public RelayCommand SelectRunCommand { get; private set; } = null!;

    /// <summary>Leave the Run layout for the last Build page; false when the armed stack refuses it.</summary>
    private bool LeaveRun()
    {
        if (!_isRunLayout) return true;
        SelectPage(_lastBuildPage);
        return !_isRunLayout;
    }

    private void RaiseShell()
    {
        Raise(nameof(SelectedGroup));
        Raise(nameof(GroupStrip));
        Raise(nameof(PageStrip));
        Raise(nameof(GroupHint));
        Raise(nameof(IsPrepSelected));
        Raise(nameof(IsShowSelected));
        Raise(nameof(IsRunSelected));
    }

    /// <summary>The SHOW CONTROLS drawer beside the switcher.</summary>
    public ShowControls ShowControls { get; private set; } = null!;

    /// <summary>The Format picker for the Media page's capture device.</summary>
    public CaptureFormatPicker CaptureFormat { get; private set; } = null!;

    /// <summary>The Format picker for the PiP inset's capture device.</summary>
    public CaptureFormatPicker PipCaptureFormat { get; private set; } = null!;

    public string RunLayoutButtonText => _isRunLayout ? "EXIT RUN" : "RUN";

    public RelayCommand ToggleRunLayoutCommand { get; private set; } = null!;

    private Views.RunWindow? _runWindow;

    /// <summary>The Run surface as a second window for a caller's own monitor.</summary>
    public RelayCommand PopOutRunCommand { get; private set; } = null!;

    /// <summary>The open pop-out, for tests and the warning about output displays.</summary>
    public Views.RunWindow? RunWindow => _runWindow;

    // ---- remote screen/group switching --------------------------------------

    private List<(ScreenPlacement Placement, ScreenInfo Info)> OrderedLivePlacements(IReadOnlyList<ScreenInfo>? screens = null)
        => Rig.OrderedLivePlacements(State, screens ?? _services.Screens.All);

    /// <summary>Remote: screen by its overview number → enabled/disabled/toggled.</summary>
    public bool SetScreenEnabled(int number, bool? target, IReadOnlyList<ScreenInfo>? screens = null)
        => _services.Actions.SetScreenEnabled(number, target, screens);

    /// <summary>Joined-canvas letters (A, B, …) → their member placements, arrangement order.</summary>
    private List<List<ScreenPlacement>> CanvasGroups(IReadOnlyList<ScreenInfo>? screens = null)
        => Rig.CanvasGroups(State, screens ?? _services.Screens.All);

    /// <summary>Remote: every screen of canvas 'A'/'B'… on or off at once.</summary>
    public bool SetGroupEnabled(string letter, bool enabled, IReadOnlyList<ScreenInfo>? screens = null)
        => _services.Actions.SetGroupEnabled(letter, enabled, screens);

    /// <summary>Screen rows for the remote-state JSON. UI thread.</summary>
    public object[] RemoteScreens(IReadOnlyList<ScreenInfo>? screens = null)
        => _services.Actions.RemoteScreens(screens);

    // ---- switcher (program / preview, the wall, sandbox) --------------------

    public ObservableCollection<SwitcherTile> SwitcherTiles { get; } = new();

    private string? _selectedTargetId;
    private SKSizeI _selectedTargetSize = Rig.DefaultTargetSize;
    private string _selectedTargetLabel = "PGM";

    /// <summary>The content target the big PROGRAM and PREVIEW panes show (null = the program bus).</summary>
    public string? SelectedTargetId => _selectedTargetId;

    /// <summary>Its real pixel size — the panes are true miniatures of it, letterboxed to fit.</summary>
    public SKSizeI SelectedTargetSize
    {
        get => _selectedTargetSize;
        private set
        {
            if (Set(ref _selectedTargetSize, value)) Raise(nameof(SelectedTargetRatio));
        }
    }

    public double SelectedTargetRatio =>
        _selectedTargetSize.Height > 0 ? (double)_selectedTargetSize.Width / _selectedTargetSize.Height : 16.0 / 9.0;

    /// <summary>"PGM", "A · Main wall" or "2 · Stage left" — the pane headers name what they show.</summary>
    public string SelectedTargetLabel { get => _selectedTargetLabel; private set => Set(ref _selectedTargetLabel, value); }

    public string SelectedTargetSizeText => $"{_selectedTargetSize.Width}×{_selectedTargetSize.Height}";

    /// <summary>Points the panes (and the preview pipeline) at a target; the editors are set separately.</summary>
    private void SelectTarget(string? targetId)
    {
        _selectedTargetId = targetId;
        _services.PreviewScreenId = targetId; // the preview resolves own-pattern vs program itself
        var tile = SwitcherTiles.FirstOrDefault(t => t.TargetId == targetId);
        SelectedTargetSize = tile?.Size ?? Rig.TargetSize(State, _services.Screens.All, targetId);
        SelectedTargetLabel = tile is null || tile.IsProgramTile ? "PGM" : tile.Title;
        Raise(nameof(SelectedTargetId));
        Raise(nameof(SelectedTargetSizeText));
        RefreshSwitcherTiles();
    }

    /// <summary>How many targets the next CUT / TAKE leaves alone — shown beside the buttons.</summary>
    /// <summary>Tiles the next CUT / TAKE leaves alone: un-armed, or locked (a confidence or info screen).</summary>
    public int HeldCount => SwitcherTiles.Count(t => !t.IsProgramTile && (!t.IsArmed || t.IsLocked));

    public bool AnyHeld => HeldCount > 0;

    public string TakeScopeText => HeldCount is var n && n > 0 ? $"{n} held" : "";

    private void RefreshTakeScope()
    {
        Raise(nameof(HeldCount));
        Raise(nameof(AnyHeld));
        Raise(nameof(TakeScopeText));
    }

    /// <summary>The desk's BLACKOUT toggles go through the action layer, so they are journaled like every other origin.</summary>
    public bool IsBlackout
    {
        get => State.Blackout;
        set
        {
            if (value == State.Blackout) return;
            _services.Actions.Execute(value ? ShowActionKind.BlackoutOn : ShowActionKind.BlackoutOff, ActionOrigin.Desk);
            Raise(nameof(IsBlackout));
        }
    }

    /// <summary>The page takes the room and the screens reduce to a strip on the right (the show remembers it).</summary>
    public bool WideWorkArea
    {
        get => State.Desk.WideWorkArea;
        set
        {
            if (State.Desk.WideWorkArea == value) return;
            State.Desk.WideWorkArea = value;
            Raise(nameof(WideWorkArea));
        }
    }

    /// <summary>
    /// The pages' explanations inline (the show remembers it). Off, they sit behind ? TIPS on
    /// the page strip and the controls have the room.
    /// </summary>
    public bool ShowHints
    {
        get => State.Desk.ShowHints;
        set
        {
            if (State.Desk.ShowHints == value) return;
            State.Desk.ShowHints = value;
            Raise(nameof(ShowHints));
        }
    }

    /// <summary>The EDIT toggle: on = open the sandbox, off = discard. CUT/TAKE close it too.</summary>

    public bool IsSandboxActive
    {
        get => _services.Sandbox.Active;
        set
        {
            if (value == _services.Sandbox.Active) return;
            if (value)
            {
                _services.Sandbox.Enter();
                ClearSendTargets(); // fresh session, fresh targets
                StatusMessage = "EDIT SAFE — build the look here; outputs keep showing the program.";
            }
            else
            {
                _services.Sandbox.Discard();
                StatusMessage = State.Switcher.EditSafeByDefault
                    ? "EDIT SAFE off — the preview now mirrors what is on air (edits go live)."
                    : "Sandbox discarded — outputs untouched.";
            }
            Raise(nameof(IsSandboxActive));
            RefreshSwitcherTiles(); // HOLD tallies only mean something while a send is being built
        }
    }

    /// <summary>The label a screen shows everywhere: the operator's name, or the OS one.</summary>
    public string LabelFor(ScreenPlacement placement, ScreenInfo? info = null)
        => Rig.LabelFor(placement, info ?? LiveInfo(placement));

    /// <summary>The stored (or automatic) name of the canvas containing a set of members.</summary>
    public string CanvasNameFor(IReadOnlyList<ScreenPlacement> members, string letter)
    {
        var key = CanvasNameConfig.KeyFor(members.Select(m => m.ScreenId));
        var stored = State.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == key)?.Name;
        return string.IsNullOrWhiteSpace(stored) ? $"Canvas {letter}" : stored!;
    }

    /// <summary>Rebuilds the wall: PGM tile, then joined canvases, then single screens.</summary>
    public void RebuildSwitcherTiles(IReadOnlyList<ScreenInfo>? screens = null)
    {
        var known = screens ?? _services.Screens.All;
        var keepTargets = SwitcherTiles.Where(t => t.IsSendTarget && t.TargetId is not null)
            .Select(t => t.TargetId!).ToHashSet();
        var monitorOff = SwitcherTiles.Where(t => !t.IsMonitored).Select(t => t.TargetId ?? "").ToHashSet();
        SwitcherTiles.Clear();

        var arming = _services.Arming;
        var geo = Rig.Geometry(State, known);
        var groups = CanvasGroups(known);
        var grouped = groups.SelectMany(g => g).Select(p => p.ScreenId).ToHashSet();
        var ordered = OrderedLivePlacements(known);
        var numberOf = ordered.Select((x, i) => (x.Placement.ScreenId, N: i + 1))
            .ToDictionary(x => x.ScreenId, x => x.N);
        var targets = new List<string>();

        SwitcherTiles.Add(new SwitcherTile(this, "PGM", null, Array.Empty<string>(),
            Rig.TargetSize(State, known, null),
            enabled: true, isSelected: _selectedTargetId is null, isOwn: false, isArmed: true)
        {
            IsMonitored = !monitorOff.Contains(""),
        });

        for (var i = 0; i < groups.Count; i++)
        {
            var letter = ((char)('A' + i)).ToString();
            var members = groups[i];
            var key = CanvasNameConfig.KeyFor(members.Select(m => m.ScreenId));
            targets.Add(key);
            SwitcherTiles.Add(new SwitcherTile(this,
                $"{letter} · {CanvasNameFor(members, letter)}",
                key,
                members.Select(m => m.ScreenId).ToList(),
                Rig.TargetSize(State, known, key),
                members.All(m => m.Enabled),
                isSelected: _selectedTargetId == key,
                isOwn: ContentTargets.UsesOwnPattern(State, key),
                isArmed: arming.IsArmed(key),
                isLocked: ScreenRoles.IsLocked(State, key),
                roleBadge: members.Select(m => m.Role).Distinct().Count() == 1 ? ScreenRoles.Badge(members[0].Role) : "")
            {
                IsSendTarget = keepTargets.Contains(key),
                IsMonitored = !monitorOff.Contains(key),
            });
        }

        foreach (var (placement, info) in ordered)
        {
            var id = placement.ScreenId;
            if (grouped.Contains(id)) continue;
            targets.Add(id);
            SwitcherTiles.Add(new SwitcherTile(this,
                $"{numberOf[id]} · {LabelFor(placement, info)}",
                id,
                new[] { id },
                OutputWindowManager.EffectiveSize(placement, info),
                placement.Enabled,
                isSelected: _selectedTargetId == id,
                isOwn: placement.UseCustomPattern,
                isArmed: arming.IsArmed(id),
                isLocked: !placement.FollowsCues,
                roleBadge: ScreenRoles.Badge(placement.Role),
                mirrorNote: placement.MirrorOf.Length > 0 && ContentTargets.IsInRig(State, placement.MirrorOf)
                    ? "↳ " + geo.LabelFor(State, placement.MirrorOf)
                    : "")
            {
                IsSendTarget = keepTargets.Contains(id),
                IsMonitored = !monitorOff.Contains(id),
            });
        }

        arming.Prune(targets); // a screen that left the rig cannot hold a stale un-arm
        // Re-measure the selection (a join, a split or a rotation changes its shape); a
        // selection that left the rig falls back to the program.
        SelectTarget(_selectedTargetId is { } selected && targets.Contains(selected) ? selected : null);
        Raise(nameof(EditTargetBanner));
        RefreshTakeScope();
        // A join creates and destroys canvases, so the tile picker's targets move with the wall.
        RebuildMultiviewTargets();
        RebuildMirrorSources();
    }

    /// <summary>Live refresh without rebuilding (keeps ticks, focus and MON; called each poll and on every change that moves a tally).</summary>
    private void RefreshSwitcherTiles()
    {
        var byId = State.Output.Placements.ToDictionary(p => p.ScreenId);
        var live = _services.Outputs.IsLive && !State.Blackout;
        var building = _services.Sandbox.Active;
        foreach (var tile in SwitcherTiles)
        {
            if (tile.TargetId is not { } target)
            {
                tile.RefreshExternal(true, _selectedTargetId is null, isOwn: false, isArmed: true, onAir: live, held: false, locked: false);
                continue;
            }
            var members = tile.MemberIds.Select(id => byId.GetValueOrDefault(id)).Where(p => p is not null).ToList();
            var enabled = members.Count > 0 && members.All(p => p!.Enabled);
            var armed = _services.Arming.IsArmed(target);
            var locked = ScreenRoles.IsLocked(State, target);
            tile.RefreshExternal(enabled, target == _selectedTargetId,
                ContentTargets.UsesOwnPattern(State, target), armed,
                onAir: live && enabled, held: building && (!armed || locked), locked: locked);
        }
    }

    /// <summary>LOCK on a tile: the target keeps its picture through looks, cues, TAKE ALL and stingers; through the action layer, so it is journaled.</summary>
    internal void SetTileLocked(SwitcherTile tile, bool locked)
    {
        if (tile.TargetId is not { } target) return;
        if (ScreenRoles.IsLocked(State, target) == locked) return;
        SetLocked(target, locked);
    }

    /// <summary>SEND on a tile: the preview lands on this target alone as its own pattern; everything else stays.</summary>
    internal void SendSandboxToTile(SwitcherTile tile)
    {
        if (tile.TargetId is not { } target) return;
        if (!_services.Sandbox.Active)
        {
            StatusMessage = "Open EDIT SAFE and build the picture first — then SEND puts it on this tile alone.";
            return;
        }
        _services.Sandbox.SendToTargets(new[] { target });
        ClearSendTargets();
        Raise(nameof(IsSandboxActive));
        RebuildEditTargets(); // the target now shows its own pattern — OWN lights up
        StatusMessage = $"Sent to {tile.Title} as its own pattern — every other target stays as it was.";
    }

    /// <summary>A tile's on/off switch: every member screen follows, pinned as a user choice.</summary>
    internal void SetTileEnabled(SwitcherTile tile, bool enabled)
    {
        foreach (var id in tile.MemberIds)
        {
            var placement = State.Output.Placements.FirstOrDefault(p => p.ScreenId == id);
            if (placement is null) continue;
            placement.Enabled = enabled;
            placement.UserPinned = true;
        }
        RefreshSwitcherTiles();
    }

    /// <summary>ARM off keeps the target's picture through the next CUT / TAKE. Runtime only — a show always opens fully armed.</summary>
    internal void SetTileArmed(SwitcherTile tile, bool armed)
    {
        if (tile.TargetId is not { } target) return;
        _services.Arming.Set(target, armed);
        StatusMessage = armed
            ? $"{tile.Title} armed — the next CUT / TAKE changes it."
            : $"{tile.Title} held — it keeps its picture through the next CUT / TAKE.";
    }

    /// <summary>OWN on gives the target its own pattern (a copy of the program, so nothing jumps) and hands it to the editors.</summary>
    internal void SetTileOwn(SwitcherTile tile, bool on)
    {
        if (tile.TargetId is not { } target) return;
        if (ContentTargets.UsesOwnPattern(State, target) == on) return;
        _services.BulkEdit(() =>
        {
            if (on) ContentTargets.EnsureAssignment(State, target);
            ContentTargets.SetOwnPattern(State, target, on);
        });
        RebuildEditTargets();
        if (on)
        {
            EditTarget = EditTargets.FirstOrDefault(t => t.ScreenId == target) ?? EditTargets[0];
            StatusMessage = $"{tile.Title} now shows its own pattern — the editors work on it.";
        }
        else
        {
            SelectTarget(target);
            StatusMessage = $"{tile.Title} follows the program again.";
        }
    }

    /// <summary>Big banner over the editor: what the panels currently change.</summary>
    public string EditTargetBanner
    {
        get
        {
            if (_editTarget.ScreenId is not { } id) return "EDITING: PROGRAM — every target without its own pattern";
            if (ContentTargets.IsCanvasKey(id))
            {
                var groups = CanvasGroups();
                for (var i = 0; i < groups.Count; i++)
                {
                    if (CanvasNameConfig.KeyFor(groups[i].Select(m => m.ScreenId)) != id) continue;
                    var letter = ((char)('A' + i)).ToString();
                    return $"EDITING: CANVAS {letter} · {CanvasNameFor(groups[i], letter)} (its own pattern)";
                }
                return "EDITING: PROGRAM";
            }
            var placement = State.Output.Placements.FirstOrDefault(p => p.ScreenId == id);
            if (placement is null) return "EDITING: PROGRAM";
            var ordered = OrderedLivePlacements();
            var n = ordered.FindIndex(x => x.Placement.ScreenId == placement.ScreenId) + 1;
            return $"EDITING: SCREEN {n} · {LabelFor(placement)} (its own pattern)";
        }
    }

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

    private string _stingerStatus = "Ready.";
    public string StingerStatus { get => _stingerStatus; private set => Set(ref _stingerStatus, value); }

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
    }

    /// <summary>Device checkbox changes → the model's device list (empty = default device).</summary>
    internal void AudioDeviceChanged(AudioDeviceChoice choice)
    {
        var devices = State.AudioPlayer.Devices;
        if (choice.IsSelected && !devices.Contains(choice.Name)) devices.Add(choice.Name);
        if (!choice.IsSelected) devices.Remove(choice.Name);
    }

    private void OpenWeb(bool kiosk)
    {
        var screens = _services.Screens.All;
        var screen = screens.FirstOrDefault(s => s.Id == State.Web.TargetScreenId)
                     ?? screens.FirstOrDefault(s => s.IsPrimary)
                     ?? screens.FirstOrDefault();
        _services.Web.Open(State.Web.Url, screen, kiosk);
        var normalized = WebService.NormalizeUrl(State.Web.Url);
        if (!string.IsNullOrWhiteSpace(normalized) && !State.Web.SavedUrls.Contains(normalized))
        {
            State.Web.SavedUrls.Add(normalized);
        }
        WebStatus = _services.Web.Status;
    }

    // ---- looks & cues -------------------------------------------------------

    private string _newLookName = "";
    public string NewLookName { get => _newLookName; set => Set(ref _newLookName, value); }

    private int _newLookHotkey;
    public int NewLookHotkey { get => _newLookHotkey; set => Set(ref _newLookHotkey, value); }

    public int[] HotkeySlots { get; } = Enumerable.Range(0, 13).ToArray();

    private string _nextCueText = "No cues scheduled.";
    public string NextCueText { get => _nextCueText; private set => Set(ref _nextCueText, value); }

    public List<string> LookNames => State.LooksAndCues.Looks.Select(l => l.Name).ToList();

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
        StatusMessage = $"Look '{name}' saved.";
        Raise(nameof(LookNames));
    }

    /// <summary>
    /// Fires a look to air — F-keys, look buttons, scheduled cues, presenter steps and
    /// remotes all land here. EDIT SAFE protects what you are <em>building</em>, not what you
    /// <em>fire</em>: with the sandbox open the audience gets the look and the preview keeps
    /// showing the operator's in-progress edit. Use "→ PVW" to load one into the editors.
    /// </summary>
    public void ApplyLook(LookConfig look) => _services.Actions.ApplyLook(look, ActionOrigin.Desk);

    /// <summary>
    /// Every action, from every origin, lands here once it has run: the status line and the
    /// editor resyncs live in one place instead of in each caller.
    /// </summary>
    private void OnActionPerformed(ShowAction action, ActionOrigin origin, ActionResult result)
    {
        if (result.Message.Length > 0) StatusMessage = result.Message;
        RefreshTallies(); // a look from anywhere, a TAKE, a fired stinger: the desk lights up at once
        switch (action.Kind)
        {
            case ShowActionKind.ApplyLook:
            case ShowActionKind.ApplyLookHotkey:
                // Unsandboxed, the look landed in the live model the editors bind to.
                if (result.Ok && !_services.Sandbox.Active)
                {
                    RebuildEditTargets();
                    Raise(nameof(ActivePattern));
                }
                break;
            case ShowActionKind.ApplyLookToPreview:
                if (result.Ok)
                {
                    RebuildEditTargets();
                    Raise(nameof(ActivePattern));
                }
                break;
            case ShowActionKind.PresenterNext:
            case ShowActionKind.PresenterPrev:
            case ShowActionKind.ListGo:
            case ShowActionKind.ListBack:
            case ShowActionKind.CueFire:
                Raise(nameof(PresenterStepText));
                if (result.Ok && !_services.Sandbox.Active)
                {
                    RebuildEditTargets();
                    Raise(nameof(ActivePattern));
                }
                break;
            case ShowActionKind.ListArm:
            case ShowActionKind.ListDisarm:
            case ShowActionKind.ListReset:
                Raise(nameof(ClickerArmed));
                Raise(nameof(PresenterStepText));
                break;
            case ShowActionKind.Take:
            case ShowActionKind.Cut:
                if (result.Ok)
                {
                    ClearSendTargets();
                    Raise(nameof(IsSandboxActive));
                    RebuildEditTargets(); // a scoped send pins / lifts own patterns — OWN follows
                }
                break;
            case ShowActionKind.OutputsOn:
            case ShowActionKind.OutputsOff:
                RefreshOutputsStatus();
                break;
            case ShowActionKind.PlaylistPart:
                RaisePlaylistSection();
                break;
        }
    }

    /// <summary>After the watchdog restored the air content into the model, resync the editors.</summary>
    public void RefreshAfterRecovery()
    {
        RebuildEditTargets();
        Raise(nameof(ActivePattern));
    }

    /// <summary>Loads a look into the editors (the sandboxed preview) instead of putting it on air.</summary>
    public void ApplyLookToPreview(LookConfig look)
        => _services.Actions.Execute(ShowActionKind.ApplyLookToPreview, ActionOrigin.Desk, look.Id);

    // ---- the tally: which look is in use, which VOG, stinger or sting is playing ----------------

    private DispatcherTimer? _tallyTimer;
    private readonly Dictionary<string, string> _lookFingerprints = new();

    /// <summary>
    /// Lights the rows and chips: the look on air (exactly, or edited since), the look loaded into
    /// the preview, and every VOG, stinger or effect sting playing right now. Runs on the status
    /// poll, after every action, on the stinger service's changes — and on its own 200 ms timer
    /// while something plays, so a surge's bar and a clip's seconds move.
    /// </summary>
    public void RefreshTallies()
    {
        RefreshLookTallies();
        var live = RefreshStingerTallies() | RefreshLowerThirdTallies();
        if (live && _tallyTimer is null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (_, _) =>
            {
                if (RefreshStingerTallies() | RefreshLowerThirdTallies()) return;
                timer.Stop();
                if (ReferenceEquals(_tallyTimer, timer)) _tallyTimer = null;
            };
            _tallyTimer = timer;
            timer.Start();
        }
        else if (!live && _tallyTimer is { } running)
        {
            running.Stop();
            _tallyTimer = null;
        }
    }

    private string FingerprintOf(LookConfig look)
    {
        if (_lookFingerprints.TryGetValue(look.Json, out var fp)) return fp;
        if (_lookFingerprints.Count > 256) _lookFingerprints.Clear();
        fp = LookService.Fingerprint(look.Json);
        _lookFingerprints[look.Json] = fp;
        return fp;
    }

    private void RefreshLookTallies()
    {
        var looks = State.LooksAndCues.Looks;
        if (looks.Count == 0) return;

        // Program: the look last put on air, edited or not; with none recorded, the look whose picture this is.
        var airFingerprint = LookService.Fingerprint(_services.AirState);
        var onAir = looks.FirstOrDefault(l => l.Id == _services.AirLookId);
        var airEdited = false;
        if (onAir is not null) airEdited = FingerprintOf(onAir) != airFingerprint;
        else onAir = looks.FirstOrDefault(l => FingerprintOf(l) == airFingerprint);

        // Preview: only a look loaded with → PVW, while the sandbox is open.
        LookConfig? inPreview = null;
        var previewEdited = false;
        if (_services.Sandbox.Active && _services.PreviewLookId is { Length: > 0 } previewId)
        {
            inPreview = looks.FirstOrDefault(l => l.Id == previewId);
            if (inPreview is not null) previewEdited = FingerprintOf(inPreview) != LookService.Fingerprint(State);
        }

        foreach (var look in looks)
        {
            var air = ReferenceEquals(look, onAir);
            var pvw = ReferenceEquals(look, inPreview);
            look.IsOnAir = air;
            look.IsInPreview = pvw;
            look.TallyText = (air, pvw) switch
            {
                (true, true) => airEdited || previewEdited ? "PROGRAM · PREVIEW · EDITED" : "PROGRAM · PREVIEW",
                (true, false) => airEdited ? "PROGRAM · EDITED" : "PROGRAM",
                (false, true) => previewEdited ? "PREVIEW · EDITED" : "PREVIEW",
                _ => "",
            };
        }
    }

    /// <summary>Lights the library rows and the panel chips; true while anything is playing.</summary>
    private bool RefreshStingerTallies()
    {
        var stingers = _services.Stingers;
        var now = stingers.NowUtc();
        var clock = ShowClock.Seconds;
        var pulse = EffectImpulses.Current;
        var any = false;
        foreach (var item in State.Stingers.Items)
        {
            var on = false;
            var text = "";
            var progress = -1.0;
            if (item.IsPulse)
            {
                if (stingers.PulseId == item.Id && !pulse.IsNone && clock >= pulse.StartSeconds && clock < pulse.EndSeconds)
                {
                    on = true;
                    progress = Math.Clamp((clock - pulse.StartSeconds) / pulse.LengthSeconds, 0, 1);
                    text = $"SURGING · {pulse.EndSeconds - clock:0.0} s left";
                }
            }
            else if (stingers.SessionId == item.Id)
            {
                on = true;
                text = stingers.Holding ? "HOLDING" : $"ON AIR · {Math.Max(0, (now - stingers.SessionStartUtc).TotalSeconds):0} s";
            }
            else if (stingers.VogSoundId == item.Id)
            {
                on = true;
                text = $"ON AIR · {Math.Max(0, (now - stingers.VogSoundStartUtc).TotalSeconds):0} s";
            }
            item.IsOnAir = on;
            item.OnAirText = text;
            item.OnAirProgress = progress;
            any |= on;
        }
        return any;
    }

    /// <summary>F1–F12 from the main window or an output window. False = no look on that key.</summary>
    public bool ApplyLookHotkey(int slot) => _services.Actions.ApplyLookHotkey(slot, ActionOrigin.Keyboard);

    private void CheckCues()
    {
        // Cues fire to air whether or not the sandbox is open: the action layer targets the
        // program, so the schedule runs the show while the operator programs in safety.
        var now = DateTime.Now;
        _services.Actions.RunSchedule(now);
        NextCueText = ShowActions.NextScheduledText(State, now);
    }

    // ---- audio / fonts / feed / LED map ------------------------------------

    public EnumItem[] ToneModes => Lists.ToneModes;
    public EnumItem[] ToneChannelsList => Lists.ToneChannelsList;
    public EnumItem[] FeedKinds => Lists.FeedKinds;
    public EnumItem[] MessageBackgrounds => Lists.MessageBackgrounds;
    public EnumItem[] Rotations => Lists.Rotations;
    public EnumItem[] PipSources => Lists.PipSources;

    private string _toneStatus = "Off";
    public string ToneStatus { get => _toneStatus; private set => Set(ref _toneStatus, value); }

    private string _feedStatus = "";
    public string FeedStatus { get => _feedStatus; private set => Set(ref _feedStatus, value); }

    public const string BuiltInFontLabel = "Inter (built-in)";

    private List<string>? _fontFamilies;
    public List<string> FontFamilies => _fontFamilies ??= BuildFontFamilies();

    private static List<string> BuildFontFamilies()
    {
        var list = new List<string> { BuiltInFontLabel };
        try
        {
            list.AddRange(SkiaSharp.SKFontManager.Default.FontFamilies
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct()
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Warn("System font enumeration failed.", ex);
        }
        return list;
    }

    /// <summary>Maps the empty model value ⇄ the built-in entry for the combo.</summary>
    public string? SelectedFontFamily
    {
        get => string.IsNullOrEmpty(State.Brand.FontFamily) ? BuiltInFontLabel : State.Brand.FontFamily;
        set
        {
            // The combo coerces values missing from its list to null (e.g. a show file made
            // on a machine with fonts this one lacks) — never let that clear the model; the
            // renderer already falls back to the built-in font for missing families.
            if (value is null) return;
            State.Brand.FontFamily = value == BuiltInFontLabel ? "" : value;
            Raise();
        }
    }

    private LedTileConfig? _selectedLedTile;
    public LedTileConfig? SelectedLedTile
    {
        get => _selectedLedTile;
        set
        {
            if (Set(ref _selectedLedTile, value)) Raise(nameof(HasLedTileSelection));
        }
    }

    public bool HasLedTileSelection => _selectedLedTile is not null;

    private void AddLedTile()
    {
        var led = ActivePattern.LedWall;
        var tiles = led.CustomTiles;
        var x = tiles.Count == 0 ? 0 : tiles.Max(t => t.X + t.Width);
        var tile = new LedTileConfig { X = x, Y = 0, Width = led.TileWidth, Height = led.TileHeight };
        tiles.Add(tile);
        led.UseCustomMap = true;
        SelectedLedTile = tile;
    }

    /// <summary>Seeds the custom map from the current regular grid — then edit the exceptions.</summary>
    private void ImportGridToMap()
    {
        var led = ActivePattern.LedWall;
        var layout = CanvasResolver.Led(led);
        _services.BulkEdit(() =>
        {
            led.CustomTiles.Clear();
            for (var r = 0; r < layout.Rows; r++)
            {
                for (var col = 0; col < layout.Columns; col++)
                {
                    led.CustomTiles.Add(new LedTileConfig
                    {
                        X = col * layout.TileWidth,
                        Y = r * layout.TileHeight,
                        Width = layout.TileWidth,
                        Height = layout.TileHeight,
                    });
                }
            }
            led.UseCustomMap = true;
        });
        SelectedLedTile = led.CustomTiles.FirstOrDefault();
        StatusMessage = $"Imported {led.CustomTiles.Count} tiles from the grid — drag or edit the exceptions.";
    }

    // ---- rotation & trims for the selected screen ---------------------------

    public OutputRotation SelectedRotation
    {
        get => _selectedPlacement?.Rotation ?? OutputRotation.None;
        set
        {
            if (_selectedPlacement is null) return;
            _selectedPlacement.Rotation = value;
            RaiseSelection();
        }
    }

    public double SelectedBrightness
    {
        get => _selectedPlacement?.BrightnessPct ?? 100;
        set { if (_selectedPlacement is not null) { _selectedPlacement.BrightnessPct = value; Raise(); } }
    }

    public double SelectedGamma
    {
        get => _selectedPlacement?.Gamma ?? 1.0;
        set { if (_selectedPlacement is not null) { _selectedPlacement.Gamma = value; Raise(); } }
    }

    public double SelectedTrimR
    {
        get => _selectedPlacement?.TrimRPct ?? 100;
        set { if (_selectedPlacement is not null) { _selectedPlacement.TrimRPct = value; Raise(); } }
    }

    public double SelectedTrimG
    {
        get => _selectedPlacement?.TrimGPct ?? 100;
        set { if (_selectedPlacement is not null) { _selectedPlacement.TrimGPct = value; Raise(); } }
    }

    public double SelectedTrimB
    {
        get => _selectedPlacement?.TrimBPct ?? 100;
        set { if (_selectedPlacement is not null) { _selectedPlacement.TrimBPct = value; Raise(); } }
    }

    // ---- NDI ----------------------------------------------------------------

    // NDI source entries use "" (not null) for Program, matching NdiSenderConfig.SourceScreenId.
    public ObservableCollection<EditTarget> NdiSources { get; } = new() { new EditTarget("Program", "") };

    public string[] NdiRateKeys => NdiRateTable.Keys;

    private void RebuildNdiSources()
    {
        var wanted = new List<EditTarget> { new("Program", "") };
        // Additive: every entry the picker offered before is still offered, so a stored
        // SourceScreenId can never be blanked by the combo's two-way binding.
        var geo = Rig.Geometry(State, _services.Screens.All);
        foreach (var key in geo.Targets)
        {
            if (ContentTargets.IsCanvasKey(key)) wanted.Add(new EditTarget(geo.LabelFor(State, key), key));
        }
        foreach (var s in _services.Screens.All)
        {
            if (s.IsVirtual) continue; // the feeds' own screens are listed by their feed below
            wanted.Add(new EditTarget($"Screen {s.Index + 1} — {s.Label}", s.Id));
        }
        foreach (var sender in State.Ndi.Senders)
        {
            var name = string.IsNullOrWhiteSpace(sender.Name) ? "Patterns" : sender.Name.Trim();
            wanted.Add(new EditTarget($"Its own screen — NDI · {name} (a look of its own)", sender.OwnScreenId));
        }
        if (State.Stream.UsesOwnScreen) wanted.Add(new EditTarget("The stream's own screen", StreamConfig.OwnScreenId));
        ReplaceIfChanged(NdiSources, wanted);
    }

    private void AddNdiSender()
    {
        var n = State.Ndi.Senders.Count + 1;
        State.Ndi.Senders.Add(new NdiSenderConfig
        {
            Name = n == 1 ? "Patterns" : $"Patterns {n}",
            Enabled = false,
        });
        SyncVirtualScreens(); // every send owns a screen of its own from the moment it exists
        StatusMessage = "NDI sender added — it owns a screen on the rig: mirror any target, or give it a look of its own.";
    }

    // ---- lists for the views ------------------------------------------------

    public EnumItem[] PatternKinds => Lists.PatternKinds;
    public EnumItem[] MultiviewSourceKinds => Lists.MultiviewSources;
    public EnumItem[] Anchors => Lists.Anchors;
    public EnumItem[] FitModes => Lists.FitModes;
    public EnumItem[] BarsVariants => Lists.BarsVariants;
    public EnumItem[] RampVariants => Lists.RampVariants;
    public EnumItem[] MotionVariants => Lists.MotionVariants;
    public EnumItem[] BlendCurves => Lists.BlendCurves;
    public EnumItem[] BlendOrientations => Lists.BlendOrientations;
    public EnumItem[] TileNumberings => Lists.TileNumberings;
    public EnumItem[] MediaSources => Lists.MediaSources;
    public EnumItem[] ParticleShapes => Lists.ParticleShapes;
    public EnumItem[] ParticleEmitters => Lists.ParticleEmitters;
    public EnumItem[] CountdownKinds => Lists.CountdownKinds;
    public EnumItem[] CountdownEnds => Lists.CountdownEnds;
    public EnumItem[] ScaleModes => Lists.ScaleModes;
    public ResolutionPreset[] Resolutions => Lists.Resolutions;
    public string[] CountdownLabels => Lists.CountdownLabels;
    public string[] ParticlePresetNames => ParticlePresets.Names;

    // ---- fractal ------------------------------------------------------------

    public string[] FractalPresetNames => FractalPresets.Names;

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

    /// <summary>The inputs a sound-reactive effect can listen to, with the show's choice kept when it is not here.</summary>
    public ObservableCollection<string> AudioCaptureDevices { get; } = new();

    private RelayCommand? _refreshAudioCaptureDevices;

    public RelayCommand RefreshAudioCaptureDevicesCommand => _refreshAudioCaptureDevices ??= new RelayCommand(RefreshAudioCaptureDevices);

    public void RefreshAudioCaptureDevices()
    {
        var wanted = AudioAnalyserService.CaptureDevices().ToList();
        var chosen = ActivePattern.Fractal.AudioDevice;
        if (chosen.Length > 0 && !wanted.Contains(chosen, StringComparer.OrdinalIgnoreCase)) wanted.Add(chosen);
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

    // ---- commands -----------------------------------------------------------

    public RelayCommand GoCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand IdentifyCommand { get; }
    public RelayCommand BlackoutCommand { get; }
    public RelayCommand ArmCountdownCommand { get; }
    public RelayCommand SaveShowCommand { get; }
    public RelayCommand LoadShowCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand BrowseImageCommand { get; }
    public RelayCommand BrowseVideoCommand { get; }
    public RelayCommand BrowseLogoCommand { get; }
    public RelayCommand<string> ApplyParticlePresetCommand { get; }
    public RelayCommand<string> ApplyCountdownLabelCommand { get; }
    public RelayCommand<PresetItem> ApplyPresetCommand { get; }
    public RelayCommand SaveBrandKitCommand { get; }
    public RelayCommand LoadBrandKitCommand { get; }
    public RelayCommand ResetLayoutCommand { get; }
    public RelayCommand AddNdiSenderCommand { get; }
    public RelayCommand<NdiSenderConfig> RemoveNdiSenderCommand { get; }
    public RelayCommand AddPlaylistFilesCommand { get; }
    public RelayCommand AddPlaylistFolderCommand { get; }
    public RelayCommand<PlaylistItemConfig> RemovePlaylistItemCommand { get; }
    public RelayCommand<PlaylistItemConfig> MovePlaylistItemUpCommand { get; }
    public RelayCommand<PlaylistItemConfig> MovePlaylistItemDownCommand { get; }
    public RelayCommand<string> RemovePlaylistFolderCommand { get; }
    public RelayCommand AddPlaylistSectionCommand { get; }
    public RelayCommand<PlaylistSectionConfig> RemovePlaylistSectionCommand { get; }
    public RelayCommand<PlaylistSectionConfig> SetPlaylistSectionCommand { get; }
    public RelayCommand SaveLookCommand { get; }
    public RelayCommand<LookConfig> ApplyLookCommand { get; }
    public RelayCommand<LookConfig> ApplyLookToPreviewCommand { get; }
    public RelayCommand<LookConfig> UpdateLookCommand { get; }
    public RelayCommand<LookConfig> DeleteLookCommand { get; }
    public RelayCommand AddCueCommand { get; }
    public RelayCommand<CueConfig> RemoveCueCommand { get; }
    public RelayCommand<string> ToneFrequencyCommand { get; }
    public RelayCommand RefreshFeedCommand { get; }
    public RelayCommand ResetTrimsCommand { get; }
    public RelayCommand AddLedTileCommand { get; }
    public RelayCommand RemoveLedTileCommand { get; }
    public RelayCommand ImportGridToMapCommand { get; }
    public RelayCommand RefreshNdiSourcesCommand { get; }
    public RelayCommand RefreshCaptureDevicesCommand { get; }
    public RelayCommand OpenWebFullscreenCommand { get; }
    public RelayCommand OpenWebWindowedCommand { get; }
    public RelayCommand CloseWebCommand { get; }
    public RelayCommand<string> LoadWebUrlCommand { get; }
    public RelayCommand<string> RemoveWebUrlCommand { get; }
    public RelayCommand SendWebTextCommand { get; }
    public RelayCommand<string> WebKeyCommand { get; }
    public RelayCommand WebBackCommand { get; }
    public RelayCommand WebForwardCommand { get; }
    public RelayCommand WebReloadCommand { get; }
    public RelayCommand RememberWebUrlCommand { get; }
    public RelayCommand PutWebPageOnPatternCommand { get; }
    public RelayCommand PresenterNextCommand { get; }
    public RelayCommand PresenterPrevCommand { get; }
    public RelayCommand PresenterResetCommand { get; }
    public RelayCommand BrowseAudioTrackCommand { get; }
    public RelayCommand PlayAudioCommand { get; }
    public RelayCommand StopAudioCommand { get; }
    public RelayCommand RefreshAudioDevicesCommand { get; }
    public RelayCommand NewLowerThirdCommand { get; }
    public RelayCommand<LowerThirdDesign> DuplicateLowerThirdCommand { get; }
    public RelayCommand<LowerThirdDesign> DeleteLowerThirdCommand { get; }
    public RelayCommand<LowerThirdDesign> ShowLowerThirdCommand { get; }
    public RelayCommand HideLowerThirdCommand { get; }
    public RelayCommand<string> AddElementCommand { get; }
    public RelayCommand<LowerThirdElement> RemoveElementCommand { get; }
    public RelayCommand<LowerThirdElement> MoveElementUpCommand { get; }
    public RelayCommand<LowerThirdElement> MoveElementDownCommand { get; }
    public RelayCommand<string> MotionInCommand { get; }
    public RelayCommand<string> MotionOutCommand { get; }
    public RelayCommand AddInKeyCommand { get; }
    public RelayCommand AddOutKeyCommand { get; }
    public RelayCommand<LowerThirdKeyframe> RemoveInKeyCommand { get; }
    public RelayCommand<LowerThirdKeyframe> RemoveOutKeyCommand { get; }
    public RelayCommand<string> ElementColorWordCommand { get; }
    public RelayCommand PickElementFileCommand { get; }
    public RelayCommand SaveLowerThirdFileCommand { get; }
    public RelayCommand<string> LoadLowerThirdFileCommand { get; }
    public RelayCommand PreviewRestartCommand { get; }
    public RelayCommand ResetWarpCommand { get; }
    public RelayCommand ResetBlendCommand { get; }
    public RelayCommand AddStingerFilesCommand { get; }
    public RelayCommand<StingerItemConfig> RemoveStingerCommand { get; }
    public RelayCommand<StingerItemConfig> FireStingerCommand { get; }
    public RelayCommand<StingerItemConfig> FireVogCommand { get; }
    public RelayCommand<StingerItemConfig> FireStingCommand { get; }
    public RelayCommand StopStingerCommand { get; }
    public RelayCommand SpotifyConnectCommand { get; }
    public RelayCommand SpotifyDisconnectCommand { get; }
    public RelayCommand RefreshSpotifyDevicesCommand { get; }
    public RelayCommand RefreshSpotifyPlaylistsCommand { get; }
    public RelayCommand AddMusicItemCommand { get; }
    public RelayCommand AddSpotifyPlaylistCommand { get; }
    public RelayCommand<SpotifyItemConfig> RemoveMusicItemCommand { get; }
    public RelayCommand<SpotifyItemConfig> PlayMusicItemCommand { get; }
    public RelayCommand ResumeMusicCommand { get; }
    public RelayCommand PauseMusicCommand { get; }
    public RelayCommand SkipMusicCommand { get; }
    public RelayCommand SandboxSendAllCommand { get; }
    public RelayCommand SandboxSendSelectedCommand { get; }
    public RelayCommand TakeCommand { get; }
    public RelayCommand CutCommand { get; }
    public RelayCommand<SwitcherTile> SelectTileCommand { get; }
    public RelayCommand ArmAllCommand { get; }
    public RelayCommand AddMultiviewTileCommand { get; }
    public RelayCommand<MultiviewTileConfig> RemoveMultiviewTileCommand { get; }
    public RelayCommand StartStreamCommand { get; }
    public RelayCommand StopStreamCommand { get; }
    public RelayCommand RestartAppCommand { get; }
    public RelayCommand OpenAppFolderCommand { get; }
    public RelayCommand AddPlannedScreenCommand { get; }
    public RelayCommand<ScreenPlacement> RemovePlannedScreenCommand { get; }
    public RelayCommand<ScreenPlacement> AdoptPlannedScreenCommand { get; }
    public RelayCommand RefreshAdoptTargetsCommand { get; }

    /// <summary>A send consumes its targets — the next look starts from a clean strip.</summary>
    private void ClearSendTargets()
    {
        foreach (var tile in SwitcherTiles)
        {
            tile.IsSendTarget = false;
        }
    }


    private static readonly FilePickerFileType AudioTypes = new("Audio")
    {
        Patterns = Glob(PlaylistSequencer.AudioExtensions),
    };

    // ---- status -------------------------------------------------------------

    public string NdiStatus { get => _ndiStatus; private set => Set(ref _ndiStatus, value); }
    public string OutputsStatus { get => _outputsStatus; private set => Set(ref _outputsStatus, value); }
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public string NewPresetName { get => _newPresetName; set => Set(ref _newPresetName, value); }

    public bool NdiRuntimeFound => NdiSender.RuntimeAvailable;
    public string NdiRuntimeNote => NdiRuntimeFound
        ? $"NDI runtime found{(NdiInterop.RuntimePath.Length > 0 ? $": {NdiInterop.RuntimePath}" : "")}"
        : NdiSender.RuntimeHelp;

    public string CanvasInfo
    {
        get
        {
            var p = ActivePattern;
            var size = CanvasResolver.Resolve(p, new SKSizeI(1920, 1080));
            return p.Kind switch
            {
                PatternKind.LedWall => $"Wall canvas: {size.Width} × {size.Height} px",
                PatternKind.VideoWall => $"Wall canvas: {size.Width} × {size.Height} px",
                PatternKind.ProjectionBlend => $"Blend canvas: {size.Width} × {size.Height} px",
                _ => p.Canvas.FollowOutput ? "Canvas follows each output (1:1)" : $"Canvas: {p.Canvas.Width} × {p.Canvas.Height} px",
            };
        }
    }

    /// <summary>The canvas size panel only applies to non-wall patterns (walls define their own).</summary>
    public bool ShowCanvasPanel => ActivePattern.Kind is not (PatternKind.LedWall or PatternKind.VideoWall or PatternKind.ProjectionBlend);

    public string HeaderClock => DateTime.Now.ToString("HH:mm:ss");

    public string CountdownPreview
    {
        get
        {
            var s = CountdownService.Evaluate(State.Countdown, DateTime.Now, DateTime.UtcNow);
            return s.Phase switch
            {
                CountdownPhase.Running => $"Live: {CountdownService.Format(s.Remaining)} remaining",
                CountdownPhase.Over => "Live: reached zero",
                _ => State.Countdown.Enabled ? "Waiting — check target time" : "Countdown off",
            };
        }
    }

    /// <summary>The status-timer body, callable directly (tests drive it without waiting on the clock).</summary>
    // ---- prep mode -----------------------------------------------------------

    /// <summary>Pre-programming at the desk: outputs are held closed, planned screens stand in for the rig.</summary>
    public bool IsPrepMode
    {
        get => State.Mode == ShowMode.Prep;
        set
        {
            var mode = value ? ShowMode.Prep : ShowMode.Show;
            if (State.Mode == mode) return;
            if (value && _services.Outputs.IsLive)
            {
                _services.Outputs.CloseAll(); // prep never leaves something on the screens
            }
            State.Mode = mode;
            RaiseModeChanged();
            StatusMessage = value
                ? "PREP — build the rig, screens, inputs and looks; the outputs stay held until you switch to SHOW."
                : "SHOW — outputs can open. Planned screens still need adopting onto real displays.";
            Log.Info(StatusMessage);
        }
    }

    public string ModeBanner => IsPrepMode
        ? "PREP MODE — pre-programming; outputs are held closed"
        : "SHOW MODE";

    /// <summary>Planned screens that have no display behind them yet (blocks a clean GO). A feed's own screen is not one.</summary>
    public int PlannedScreenCount => State.Output.Placements.Count(p => p.IsPlannedDisplay);

    /// <summary>The feeds' own screens on the rig: one per NDI send, one for the stream while it is set to its own.</summary>
    public int VirtualScreenCount => State.Output.Placements.Count(p => p.IsVirtual);

    /// <summary>Keeps the feeds' screens in step with the senders and the stream; on the poll, and after every add or remove.</summary>
    public void SyncVirtualScreens()
    {
        if (!VirtualScreens.Sync(State)) return;
        _services.Screens.Refresh();
        RebuildEditTargets();
        RebuildNdiSources();
        RebuildStreamSources();
        Raise(nameof(VirtualScreenCount));
        Raise(nameof(PlannedScreenCount));
        RaiseModeChanged();
    }

    /// <summary>What the stream can show: a display captured off the desktop, or a rig target rendered by the engine.</summary>
    public ObservableCollection<EditTarget> StreamSources { get; } = new() { new EditTarget("Primary screen (desktop capture)", "") };

    private void RebuildStreamSources()
    {
        var wanted = new List<EditTarget> { new("Primary screen (desktop capture)", "") };
        foreach (var s in _services.Screens.Real)
        {
            wanted.Add(new EditTarget($"Screen {s.Index + 1} — {s.Label} (desktop capture)", s.Id));
        }
        wanted.Add(new EditTarget("Its own screen — rendered, a look of its own", StreamConfig.OwnScreenId));
        var geo = Rig.Geometry(State, _services.Screens.All);
        foreach (var key in geo.Targets)
        {
            if (ContentTargets.IsCanvasKey(key)) wanted.Add(new EditTarget($"{geo.LabelFor(State, key)} (rendered)", key));
        }
        foreach (var p in State.Output.Placements)
        {
            if (p.IsPlannedDisplay) wanted.Add(new EditTarget($"{LabelFor(p)} (planned, rendered)", p.ScreenId));
        }
        ReplaceIfChanged(StreamSources, wanted);
    }

    public string PrepSummary
    {
        get
        {
            var planned = PlannedScreenCount;
            var real = _services.Screens.Real.Count;
            return planned == 0
                ? $"{real} display{(real == 1 ? "" : "s")} detected · no planned screens"
                : $"{real} display{(real == 1 ? "" : "s")} detected · {planned} planned screen{(planned == 1 ? "" : "s")} waiting to be adopted";
        }
    }

    private void RaiseModeChanged()
    {
        RefreshOutputsStatus();
        RaiseShell();
    }

    private void GoLive() => _services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);

    /// <summary>Adds a screen that does not exist yet, so the whole rig can be built at the desk.</summary>
    public ScreenPlacement AddPlannedScreen(int width = 1920, int height = 1080, string label = "")
    {
        var placement = new ScreenPlacement
        {
            ScreenId = ScreenPlacement.PlannedIdPrefix + Guid.NewGuid().ToString("N")[..8],
            Planned = true,
            PlannedWidth = width,
            PlannedHeight = height,
            CustomLabel = label,
            Enabled = true,
            UserPinned = true,
            X = NextPlannedX(),
        };
        State.Output.Placements.Add(placement);
        _services.Screens.Refresh();
        RebuildEditTargets();
        RaiseModeChanged();
        StatusMessage = $"Planned screen added ({width}×{height}). Arrange, pattern and label it like any other.";
        return placement;
    }

    /// <summary>Places a new planned screen to the right of everything already arranged.</summary>
    private int NextPlannedX()
    {
        var right = 0;
        foreach (var (placement, info) in OrderedLivePlacements())
        {
            right = Math.Max(right, placement.X + OutputWindowManager.EffectiveSize(placement, info).Width);
        }
        return right;
    }

    public void RemovePlannedScreen(ScreenPlacement placement)
    {
        if (!placement.IsPlannedDisplay) return; // a feed's own screen goes with its feed, never on its own
        State.Output.Placements.Remove(placement);
        var assignment = State.Independent.FirstOrDefault(a => a.ScreenId == placement.ScreenId);
        if (assignment is not null) State.Independent.Remove(assignment);
        _services.Screens.Refresh();
        RebuildEditTargets();
        RaiseModeChanged();
        StatusMessage = "Planned screen removed.";
    }

    /// <summary>
    /// At the venue: bind a planned screen onto a real display. Everything programmed against
    /// it — position, label, per-screen pattern, trims, warp, rotation and any look that
    /// names it — follows onto the hardware, so the desk work is not redone.
    /// </summary>
    public bool AdoptPlannedScreen(ScreenPlacement planned, string realScreenId)
    {
        if (!planned.IsPlannedDisplay || realScreenId.Length == 0) return false; // a feed's own screen is never a display
        if (_services.Screens.Real.All(s => s.Id != realScreenId)) return false;

        var oldId = planned.ScreenId;
        if (State.Output.Placements.FirstOrDefault(p => p.ScreenId == realScreenId) is { } existing)
        {
            // That display already has a placement — retire it and let the planned one take over.
            State.Output.Placements.Remove(existing);
            var stale = State.Independent.FirstOrDefault(a => a.ScreenId == realScreenId);
            if (stale is not null) State.Independent.Remove(stale);
        }

        _services.BulkEdit(() =>
        {
            planned.Planned = false;
            ContentTargets.RenameScreen(State, oldId, realScreenId);
        });

        // The rig lives in the frozen program too while EDIT SAFE is on — adopt there as well,
        // or the audience keeps the planned screen the operator just replaced.
        if (_services.Sandbox.ProgramState is { } air)
        {
            foreach (var p in air.Output.Placements.Where(p => p.ScreenId == oldId))
            {
                p.Planned = false;
            }
            ContentTargets.RenameScreen(air, oldId, realScreenId);
            _services.RepublishNow();
        }

        _services.Screens.Refresh();
        RebuildEditTargets();
        RaiseModeChanged();
        var info = _services.Screens.Real.First(s => s.Id == realScreenId);
        StatusMessage = $"Adopted onto {info.Label} ({info.Bounds.Width}×{info.Bounds.Height}) — everything programmed for it carried over.";
        Log.Info(StatusMessage);
        return true;
    }

    /// <summary>Real displays not already claimed by a placement — the adopt targets.</summary>
    public ObservableCollection<EditTarget> AdoptTargets { get; } = new();

    public void RefreshAdoptTargets()
    {
        AdoptTargets.Clear();
        foreach (var s in _services.Screens.Real)
        {
            AdoptTargets.Add(new EditTarget($"{s.Label} · {s.Bounds.Width}×{s.Bounds.Height}", s.Id));
        }
    }

    // ---- live-input pool -----------------------------------------------------

    private string _activeInputsText = "No live inputs mounted.";
    public string ActiveInputsText { get => _activeInputsText; private set => Set(ref _activeInputsText, value); }

    private void RefreshActiveInputs()
    {
        var rows = new List<string>();
        foreach (var (key, status) in _services.Video.MountStatuses.Concat(_services.NdiIn.MountStatuses).Concat(_services.WebIn.MountStatuses))
        {
            var bare = key.Length > 4 ? key[4..] : key;
            var label = key.StartsWith("vid:", StringComparison.Ordinal)
                ? Path.GetFileName(bare)
                : key.StartsWith("web:", StringComparison.Ordinal)
                    ? State.InputLabel(key, WebAddress.ShortName(bare))
                    : State.InputLabel(key, bare);
            rows.Add($"{label} — {status}");
        }
        var notes = string.Join("  ",
            new[] { _services.Video.LimitNote, _services.NdiIn.LimitNote, _services.WebIn.LimitNote }.Where(s => s.Length > 0));
        var text = rows.Count == 0
            ? "No live inputs mounted."
            : $"Live inputs ({rows.Count}): {string.Join("  ·  ", rows)}";
        ActiveInputsText = notes.Length > 0 ? $"{text}  {notes}" : text;
    }

    // ---- admin ---------------------------------------------------------------

    private const double SparkW = 196;
    private const double SparkH = 40;

    private string _adminCpuText = "—";
    private string _adminMemText = "—";
    private string _adminGpuText = "—";
    private string _adminRenderText = "—";
    private string _adminExtrasText = "—";
    private string _gpuActiveText = "";
    private string _graphicsApplyStatus = "";
    private string _machineOverview = "";
    private Avalonia.Points _adminCpuSpark = new();
    private Avalonia.Points _adminRamSpark = new();
    private Avalonia.Points _adminFpsSpark = new();
    private string _suggestionsKey = "";
    private string? _cpuNameCache;
    private int _statusTicks;

    public string AdminCpuText { get => _adminCpuText; private set => Set(ref _adminCpuText, value); }
    public string AdminMemText { get => _adminMemText; private set => Set(ref _adminMemText, value); }
    public string AdminGpuText { get => _adminGpuText; private set => Set(ref _adminGpuText, value); }
    public string AdminRenderText { get => _adminRenderText; private set => Set(ref _adminRenderText, value); }
    public string AdminExtrasText { get => _adminExtrasText; private set => Set(ref _adminExtrasText, value); }
    public string GpuActiveText { get => _gpuActiveText; private set => Set(ref _gpuActiveText, value); }
    public string GraphicsApplyStatus { get => _graphicsApplyStatus; private set => Set(ref _graphicsApplyStatus, value); }
    public string MachineOverview { get => _machineOverview; private set => Set(ref _machineOverview, value); }
    public Avalonia.Points AdminCpuSpark { get => _adminCpuSpark; private set => Set(ref _adminCpuSpark, value); }
    public Avalonia.Points AdminRamSpark { get => _adminRamSpark; private set => Set(ref _adminRamSpark, value); }
    public Avalonia.Points AdminFpsSpark { get => _adminFpsSpark; private set => Set(ref _adminFpsSpark, value); }

    public ObservableCollection<SuggestionRow> AdminSuggestions { get; } = new();
    public ObservableCollection<GpuRow> GpuRows { get; } = new();
    public ObservableCollection<string> GpuAdapterNames { get; } = new();

    public bool GpuSpecificVisible => State.Admin.Graphics.Preference == GpuPreferenceKind.Specific;

    /// <summary>The Copy support info payload (also used by tests to sanity-check content).</summary>
    public string BuildSupportInfo() => _services.Metrics.SupportInfo();

    // ---- lower thirds --------------------------------------------------------------------------

    private LowerThirdDesign? _selectedLowerThird;
    private LowerThirdElement? _selectedElement;
    private string _newLowerThirdPreset = "Clean";
    private double _previewTimeMs;
    private bool _previewPlaying;
    private DispatcherTimer? _previewTimer;
    private string _lowerThirdStatus = "No lower third on air.";

    /// <summary>The presets a new design starts from, plus an empty box.</summary>
    public IReadOnlyList<string> LowerThirdPresetNames { get; } = LowerThirdPresets.Names.Concat(new[] { "Blank" }).ToList();

    public string NewLowerThirdPreset { get => _newLowerThirdPreset; set => Set(ref _newLowerThirdPreset, string.IsNullOrWhiteSpace(value) ? "Clean" : value); }

    /// <summary>Designs saved as files in the lowerthirds folder (Id = the path).</summary>
    public ObservableCollection<PickItem> LowerThirdFiles { get; } = new();

    /// <summary>What is on screen, for the pages.</summary>
    public string LowerThirdStatus { get => _lowerThirdStatus; private set => Set(ref _lowerThirdStatus, value); }

    public LowerThirdDesign? SelectedLowerThird
    {
        get => _selectedLowerThird;
        set
        {
            var old = _selectedLowerThird;
            if (!Set(ref _selectedLowerThird, value)) return;
            if (old is not null) old.PropertyChanged -= OnSelectedLowerThirdChanged;
            if (value is not null) value.PropertyChanged += OnSelectedLowerThirdChanged;
            SelectedElement = value?.Elements.FirstOrDefault();
            PreviewTimeMs = value?.InMs ?? 0;
            Raise(nameof(HasLowerThird));
            Raise(nameof(PreviewLengthMs));
        }
    }

    private void OnSelectedLowerThirdChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LowerThirdDesign.InMs) or nameof(LowerThirdDesign.HoldMs) or nameof(LowerThirdDesign.OutMs))
        {
            Raise(nameof(PreviewLengthMs));
            PreviewTimeMs = Math.Min(PreviewTimeMs, PreviewLengthMs);
        }
    }

    public bool HasLowerThird => _selectedLowerThird is not null;

    public LowerThirdElement? SelectedElement
    {
        get => _selectedElement;
        set
        {
            var old = _selectedElement;
            if (!Set(ref _selectedElement, value)) return;
            if (old is not null) old.PropertyChanged -= OnSelectedElementChanged;
            if (value is not null) value.PropertyChanged += OnSelectedElementChanged;
            RaiseElementKind();
        }
    }

    private void OnSelectedElementChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LowerThirdElement.Kind)) RaiseElementKind();
    }

    private void RaiseElementKind()
    {
        Raise(nameof(HasElement));
        Raise(nameof(ElementIsText));
        Raise(nameof(ElementHasFile));
        Raise(nameof(ElementIsMedia));
        Raise(nameof(ElementIsParticles));
        Raise(nameof(ElementIsFractal));
    }

    public bool HasElement => _selectedElement is not null;
    public bool ElementIsText => _selectedElement?.Kind == LowerThirdElementKind.Text;
    public bool ElementHasFile => _selectedElement?.Kind is LowerThirdElementKind.Image or LowerThirdElementKind.Media;
    public bool ElementIsMedia => _selectedElement?.Kind == LowerThirdElementKind.Media;
    public bool ElementIsParticles => _selectedElement?.Kind == LowerThirdElementKind.Particles;
    public bool ElementIsFractal => _selectedElement?.Kind == LowerThirdElementKind.Fractal;

    /// <summary>The scrubber's range: the way in, a hold (its own, or 1.5 s when it waits to be hidden), the way out.</summary>
    public double PreviewLengthMs
        => _selectedLowerThird is null ? 1000 : _selectedLowerThird.InMs + (_selectedLowerThird.HoldMs > 0 ? _selectedLowerThird.HoldMs : 1500) + _selectedLowerThird.OutMs;

    /// <summary>Where the preview stands on the design's own timeline.</summary>
    public double PreviewTimeMs { get => _previewTimeMs; set => Set(ref _previewTimeMs, Math.Clamp(value, 0, Math.Max(1, PreviewLengthMs))); }

    /// <summary>Runs the preview round and round.</summary>
    public bool PreviewPlaying
    {
        get => _previewPlaying;
        set
        {
            if (!Set(ref _previewPlaying, value)) return;
            if (value)
            {
                _previewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                _previewTimer.Tick -= PreviewTick;
                _previewTimer.Tick += PreviewTick;
                _previewTimer.Start();
            }
            else
            {
                _previewTimer?.Stop();
            }
        }
    }

    private void PreviewTick(object? sender, EventArgs e)
    {
        var next = _previewTimeMs + 33;
        PreviewTimeMs = next >= PreviewLengthMs ? 0 : next;
    }

    /// <summary>A new design from a preset (or an empty box), named so it never collides, selected.</summary>
    public LowerThirdDesign NewLowerThird(string preset)
    {
        var design = preset == "Blank" ? LowerThirdPresets.Blank() : LowerThirdPresets.Create(preset);
        design.Name = UniqueLowerThirdName(design.Name);
        State.LowerThirds.Designs.Add(design);
        SelectedLowerThird = design;
        StatusMessage = $"Lower third '{design.Name}' added.";
        return design;
    }

    private string UniqueLowerThirdName(string name)
    {
        var candidate = name;
        var n = 2;
        while (State.LowerThirds.Designs.Any(d => string.Equals(d.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{name} {n++}";
        }
        return candidate;
    }

    private void DuplicateLowerThird(LowerThirdDesign? design)
    {
        if (design is null) return;
        var copy = design.Clone();
        copy.Name = UniqueLowerThirdName(design.Name);
        State.LowerThirds.Designs.Add(copy);
        SelectedLowerThird = copy;
    }

    private void DeleteLowerThird(LowerThirdDesign? design)
    {
        if (design is null) return;
        if (State.LowerThirds.ActiveId == design.Id) State.LowerThirds.Hide(ShowClock.UtcNow);
        var index = State.LowerThirds.Designs.IndexOf(design);
        // The page's list clears its selection the moment the item goes: decide before, reselect after.
        var wasSelected = ReferenceEquals(SelectedLowerThird, design);
        State.LowerThirds.Designs.Remove(design);
        if (wasSelected)
        {
            var designs = State.LowerThirds.Designs;
            SelectedLowerThird = designs.Count == 0 ? null : designs[Math.Clamp(index, 0, designs.Count - 1)];
        }
        StatusMessage = $"Lower third '{design.Name}' deleted.";
    }

    /// <summary>On air now, through the action layer (journaled, sandbox-aware).</summary>
    public void ShowLowerThird(LowerThirdDesign design)
        => _services.Actions.Execute(ShowActionKind.LowerThirdShow, ActionOrigin.Desk, design.Id);

    public void HideLowerThird() => _services.Actions.Execute(ShowActionKind.LowerThirdHide, ActionOrigin.Desk);

    /// <summary>A new element of a kind, sized to the design and given a plain fade both ways, selected.</summary>
    public LowerThirdElement? AddElement(LowerThirdElementKind kind)
    {
        var d = SelectedLowerThird;
        if (d is null) return null;
        var bar = kind == LowerThirdElementKind.Bar;
        var full = kind is LowerThirdElementKind.Bar or LowerThirdElementKind.Particles or LowerThirdElementKind.Fractal or LowerThirdElementKind.Media;
        var e = new LowerThirdElement
        {
            Kind = kind,
            Name = kind.ToString(),
            X = 0,
            Y = 0,
            W = full ? d.Width : Math.Min(kind == LowerThirdElementKind.Text ? 600 : 200, d.Width),
            H = full ? d.Height : Math.Min(kind == LowerThirdElementKind.Text ? 80 : 200, d.Height),
            Fill = bar ? LowerThirdFill.Solid : LowerThirdFill.None,
        };
        if (kind == LowerThirdElementKind.Text) e.Text = "Text";
        LowerThirdMotions.Apply(e, d, LowerThirdMotion.Fade, LowerThirdMotion.Fade);
        d.Elements.Add(e);
        SelectedElement = e;
        return e;
    }

    private void RemoveElement(LowerThirdElement? e)
    {
        var d = SelectedLowerThird;
        if (d is null || e is null) return;
        var index = d.Elements.IndexOf(e);
        var wasSelected = ReferenceEquals(SelectedElement, e);
        d.Elements.Remove(e);
        if (wasSelected)
        {
            SelectedElement = d.Elements.Count == 0 ? null : d.Elements[Math.Clamp(index, 0, d.Elements.Count - 1)];
        }
    }

    private void MoveElement(LowerThirdElement? e, int delta)
    {
        var d = SelectedLowerThird;
        if (d is null || e is null) return;
        var index = d.Elements.IndexOf(e);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= d.Elements.Count) return;
        // The page's list sees a move as a removal and an insert and drops its selection on the way: put it back.
        var selected = SelectedElement;
        d.Elements.Move(index, target);
        if (selected is not null && !ReferenceEquals(SelectedElement, selected)) SelectedElement = selected;
    }

    /// <summary>A motion chip: the ready-made keys for the way in or out, editable afterwards.</summary>
    public void ApplyMotion(string? motionName, bool isIn)
    {
        var d = SelectedLowerThird;
        var e = SelectedElement;
        if (d is null || e is null || !Enum.TryParse<LowerThirdMotion>(motionName, true, out var motion)) return;
        LowerThirdMotions.Apply(e, motion, isIn, LowerThirdMotions.DefaultDistance(motion, d));
        PreviewTimeMs = isIn ? d.InMs * 0.5 : d.InMs + (d.HoldMs > 0 ? d.HoldMs : 1500) + d.OutMs * 0.5;
    }

    private void AddKey(bool isIn)
    {
        var e = SelectedElement;
        if (e is null) return;
        var keys = isIn ? e.In : e.Out;
        var last = keys.Count == 0 ? null : keys[^1];
        var key = last?.Clone() ?? new LowerThirdKeyframe { U = isIn ? 0 : 1 };
        if (last is not null) key.U = Math.Min(1, last.U + 0.25);
        keys.Add(key);
    }

    private void RemoveKey(LowerThirdKeyframe? key, bool isIn)
    {
        var e = SelectedElement;
        if (e is null || key is null) return;
        (isIn ? e.In : e.Out).Remove(key);
    }

    /// <summary>"TextColor:primary" — a brand word into one of the element's colour fields.</summary>
    private void SetElementColorWord(string? spec)
    {
        var e = SelectedElement;
        if (e is null || string.IsNullOrWhiteSpace(spec)) return;
        var parts = spec.Split(':', 2);
        if (parts.Length != 2) return;
        var word = parts[1];
        switch (parts[0])
        {
            case nameof(LowerThirdElement.TextColor): e.TextColor = word; break;
            case nameof(LowerThirdElement.FillColor): e.FillColor = word; break;
            case nameof(LowerThirdElement.FillColor2): e.FillColor2 = word; break;
            case nameof(LowerThirdElement.BorderColor): e.BorderColor = word; break;
            case nameof(LowerThirdElement.GlowColor): e.GlowColor = word; break;
            case nameof(LowerThirdElement.ChaserColor): e.ChaserColor = word; break;
            case nameof(LowerThirdElement.ShadowColor): e.ShadowColor = word; break;
        }
    }

    private async Task PickElementFileAsync()
    {
        var e = SelectedElement;
        var window = _services.MainWindow;
        if (e is null || window is null) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = e.Kind == LowerThirdElementKind.Media ? "Choose a clip or still for this element" : "Choose a picture for this element",
                AllowMultiple = false,
                FileTypeFilter = new[] { MediaTypes, FilePickerFileTypes.All },
            });
            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (path is null) return;
            e.Path = path;
            AddToMediaLibrary(path, isVideo: PlaylistSequencer.IsVideoPath(path));
        }
        catch (Exception ex)
        {
            Log.Warn("Element file pick failed.", ex);
        }
    }

    /// <summary>The selected design as a file of its own in the lowerthirds folder (its name is the file name).</summary>
    public void SaveLowerThirdFile()
    {
        var d = SelectedLowerThird;
        if (d is null) return;
        try
        {
            var path = _services.Store.SaveLowerThird(d.Name, d);
            RefreshLowerThirdFiles();
            StatusMessage = $"Saved '{Path.GetFileName(path)}' in the lowerthirds folder.";
        }
        catch (Exception ex)
        {
            Log.Warn("Lower third save failed.", ex);
            StatusMessage = $"Could not save the lower third: {ex.Message}";
        }
    }

    /// <summary>A saved file into the show as a new design (fresh ids, a name that never collides), selected.</summary>
    public LowerThirdDesign? LoadLowerThirdFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var loaded = _services.Store.LoadLowerThird(path);
        if (loaded is null)
        {
            StatusMessage = $"Could not read '{Path.GetFileName(path)}'.";
            return null;
        }
        var design = loaded.Clone();
        design.Name = UniqueLowerThirdName(loaded.Name.Length > 0 ? loaded.Name : Path.GetFileNameWithoutExtension(path));
        State.LowerThirds.Designs.Add(design);
        SelectedLowerThird = design;
        StatusMessage = $"Lower third '{design.Name}' loaded from file.";
        return design;
    }

    public void RefreshLowerThirdFiles()
    {
        LowerThirdFiles.Clear();
        foreach (var (name, path) in _services.Store.ListLowerThirds())
        {
            LowerThirdFiles.Add(new PickItem(path, name));
        }
    }

    /// <summary>The tally: the design on screen lights its row and chip with its phase; true while one is on.</summary>
    private bool RefreshLowerThirdTallies()
    {
        var air = _services.AirState.LowerThirds;
        var now = ShowClock.UtcNow;
        var active = air.Active;
        var phase = LowerThirdPhase.Gone;
        if (active is not null && LowerThirdClock.Instants(air) is { } at)
        {
            phase = LowerThirdClock.Evaluate(active, at.ShownAt, at.HiddenAt, ShowClock.SecondsAt(now)).Phase;
        }
        var live = phase is LowerThirdPhase.In or LowerThirdPhase.Hold or LowerThirdPhase.Out;
        var text = phase switch
        {
            LowerThirdPhase.In => "ARRIVING",
            LowerThirdPhase.Out => "LEAVING",
            LowerThirdPhase.Hold => "ON AIR",
            _ => "",
        };
        foreach (var d in State.LowerThirds.Designs)
        {
            var on = live && active is not null && d.Id == active.Id;
            d.IsOnAir = on;
            d.OnAirText = on ? text : "";
        }
        LowerThirdStatus = live && active is not null
            ? $"On air: {active.Name} ({text.ToLowerInvariant()})."
            : "No lower third on air.";
        return live;
    }

    // ---- the super-check ----------------------------------------------------------------------

    private RelayCommand? _runSuperCheck;
    private string _superCheckHeadline = "";
    private string _superCheckLevelText = "";
    private string _superCheckSavedText = "";
    private string _superCheckText = "";
    private Avalonia.Media.IBrush _superCheckDot = Avalonia.Media.Brushes.Gray;

    public RelayCommand RunSuperCheckCommand => _runSuperCheck ??= new RelayCommand(RunSuperCheck);

    public ObservableCollection<CheckRowView> SuperCheckRows { get; } = new();

    public string SuperCheckHeadline { get => _superCheckHeadline; private set => Set(ref _superCheckHeadline, value); }
    public string SuperCheckLevelText { get => _superCheckLevelText; private set => Set(ref _superCheckLevelText, value); }
    public string SuperCheckSavedText { get => _superCheckSavedText; private set => Set(ref _superCheckSavedText, value); }

    /// <summary>The report as plain text (the Copy button's payload).</summary>
    public string SuperCheckText { get => _superCheckText; private set => Set(ref _superCheckText, value); }

    public Avalonia.Media.IBrush SuperCheckDot { get => _superCheckDot; private set => Set(ref _superCheckDot, value); }

    public bool HasSuperCheck => SuperCheckRows.Count > 0;

    /// <summary>One press: every fact, every light, the level — on the page and in a file beside the exe.</summary>
    public void RunSuperCheck()
    {
        var report = _services.Metrics.RunSuperCheck();
        SuperCheckRows.Clear();
        foreach (var row in report.Rows)
        {
            SuperCheckRows.Add(new CheckRowView(row.Section, row.Item, row.Value, row.Note, LightBrush(row.Light)));
        }
        SuperCheckHeadline = report.Headline;
        SuperCheckDot = LightBrush(report.Overall);
        SuperCheckLevelText = report.Level.Reasons.Count > 0
            ? $"Level: {report.Level.Name} (score {report.Level.Score}) — {string.Join("; ", report.Level.Reasons)}"
            : $"Level: {report.Level.Name} (score {report.Level.Score})";
        SuperCheckText = SuperCheck.ToText(report);
        var path = _services.Metrics.LastReportPath;
        SuperCheckSavedText = path.Length > 0 ? $"Saved: {path}" : "The report could not be written beside the exe — copy it instead.";
        Raise(nameof(HasSuperCheck));
        StatusMessage = $"Super-check: {report.Headline}";
    }

    private static Avalonia.Media.IBrush LightBrush(CheckLight light) => light switch
    {
        CheckLight.Green => Avalonia.Media.Brush.Parse("#2EE68A"),
        CheckLight.Amber => Avalonia.Media.Brush.Parse("#FFC24D"),
        CheckLight.Red => Avalonia.Media.Brush.Parse("#E0342E"),
        _ => Avalonia.Media.Brush.Parse("#4A505E"),
    };

    /// <summary>The Admin pages (the machine, help) take the room: the screens reduce to a strip while one is selected.</summary>
    public bool PageWantsRoom => !_isRunLayout && Shell.Pages[_page].Group == ShellGroup.Admin;

    private void OnGraphicsChoiceChanged()
    {
        GpuService.RecordAppliedPath(State);
        var registry = GpuService.ApplyWindowsPreference(State.Admin.Graphics);
        GraphicsApplyStatus = (registry.Length > 0 ? registry + " " : "") +
                              "Takes effect at the next start — use Restart app below.";
        RebuildGpuRows();
        Raise(nameof(GpuSpecificVisible));
    }

    private void RebuildGpuRows()
    {
        GpuRows.Clear();
        GpuAdapterNames.Clear();
        var adapters = GpuService.Adapters;
        var best = GpuSelector.ChooseBest(adapters);
        for (var i = 0; i < adapters.Count; i++)
        {
            var gpu = adapters[i];
            var badges = new List<string>();
            if (gpu.DedicatedVideoMemoryMB > 0) badges.Add($"{gpu.DedicatedVideoMemoryMB / 1024.0:0.#} GB");
            badges.Add(gpu.VendorName);
            if (i == best) badges.Add("best");
            if (gpu.IsSoftware) badges.Add("software fallback");
            if (string.Equals(gpu.Name, GpuService.ActiveAdapterName, StringComparison.OrdinalIgnoreCase) ||
                (GpuService.ActiveAdapterName.Length == 0 && i == GpuService.RequestedIndex))
            {
                badges.Add("selected");
            }
            GpuRows.Add(new GpuRow(gpu.Name, string.Join(" · ", badges)));
            if (!gpu.IsSoftware) GpuAdapterNames.Add(gpu.Name);
        }
        if (adapters.Count == 0)
        {
            GpuRows.Add(new GpuRow("No adapters detected", "GPU detection runs on Windows."));
        }
        if (GpuAdapterNames.Count == 0) GpuAdapterNames.Add("");
        GpuActiveText = GpuService.ActiveAdapterName.Length > 0
            ? $"Rendering on: {GpuService.ActiveAdapterName}"
            : GpuService.RequestedName.Length > 0
                ? $"Will render on: {GpuService.RequestedName}"
                : "Adapter choice: Windows default";
    }

    private void RestartApp()
    {
        if (!LaunchOptions.IsChild)
        {
            StatusMessage = "Restart in place needs the watchdog (see Stability below) — with it off, close and reopen Patterns instead.";
            return;
        }
        var code = _services.PrepareRestart();
        StatusMessage = "Restarting — the show comes straight back…";
        Log.Info("Restart requested from the Machine page.");
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IControlledApplicationLifetime)?.Shutdown(code);
    }

    private void OpenAppFolder()
    {
        var dir = _services.Store.BaseDirectory;
        StatusMessage = $"App folder: {dir}";
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open the app folder.", ex);
        }
    }

    private void PollAdmin()
    {
        var metrics = _services.Metrics;
        if (metrics.Current is not { } s) return;

        AdminCpuText = $"this app {Pct(s.CpuAppPct)} · whole computer {Pct(s.CpuSystemPct)}";
        AdminMemText = $"this app {Mb(s.RamAppMB)} · computer {Pct(s.RamSystemPct)}" +
                       (s.RamTotalMB > 0 ? $" of {s.RamTotalMB / 1024.0:0.0} GB" : "");
        var vram = s.VramTotalMB > 0 ? $"video memory {Mb(s.VramUsedMB)} of {Mb(s.VramTotalMB)}" : "video memory n/a";
        AdminGpuText = $"busy {Pct(s.GpuBusyPct)} · {vram}";
        AdminRenderText = s.OutputWindows > 0
            ? $"outputs {s.OutputFps:0} fps × {s.OutputWindows} window{(s.OutputWindows == 1 ? "" : "s")} · " +
              $"preview {s.PreviewFps:0} fps · worst frame {s.WorstFrameMs:0.0} ms" +
              (s.SlowFrames > 0 ? $" · {s.SlowFrames} slow" : "")
            : $"preview {s.PreviewFps:0} fps — outputs closed";
        AdminExtrasText = $"threads {s.Threads} · handles {s.Handles}" +
                          (s.GcPausePct >= 0 ? $" · GC pause {s.GcPausePct:0.0}%" : "") +
                          (s.DiskFreeGB >= 0 ? $" · disk free {s.DiskFreeGB:0.0} GB" : "") +
                          $" · {(s.OnBattery ? $"ON BATTERY{(s.BatteryPct >= 0 ? $" {s.BatteryPct}%" : "")}" : "mains power")}";

        AdminCpuSpark = Spark(metrics.History.Tail(180, x => x.CpuSystemPct), 100);
        AdminRamSpark = Spark(metrics.History.Tail(180, x => x.RamAppMB), null);
        AdminFpsSpark = Spark(metrics.History.Tail(180, x => x.OutputWindows > 0 ? x.OutputFps : x.PreviewFps), 66);

        var key = string.Join("|", metrics.Suggestions.Select(x => x.Id + (int)x.Severity));
        if (key != _suggestionsKey)
        {
            _suggestionsKey = key;
            AdminSuggestions.Clear();
            foreach (var advice in metrics.Suggestions)
            {
                AdminSuggestions.Add(new SuggestionRow(advice.Title, advice.Detail, SeverityBrush(advice)));
            }
        }

        if (MachineOverview.Length == 0 || _statusTicks % 30 == 0)
        {
            MachineOverview = BuildMachineOverview(s);
        }

        static string Pct(double v) => v < 0 ? "n/a" : $"{v:0}%";
        static string Mb(double v) => v < 0 ? "n/a" : v >= 1024 ? $"{v / 1024.0:0.0} GB" : $"{v:0} MB";
    }

    private static Avalonia.Media.IBrush SeverityBrush(HealthSuggestion s) => s switch
    {
        { Severity: HealthSeverity.Warning } => Avalonia.Media.Brush.Parse("#FF5C7A"),
        { Severity: HealthSeverity.Advice } => Avalonia.Media.Brush.Parse("#FFC24D"),
        { Id: "all-clear" } => Avalonia.Media.Brush.Parse("#2EE68A"),
        _ => Avalonia.Media.Brush.Parse("#9AA7B8"),
    };

    private static Avalonia.Points Spark(IReadOnlyList<double> values, double? fixedMax)
    {
        var points = new Avalonia.Points();
        foreach (var (x, y) in SparklinePath.Points(values, SparkW, SparkH, fixedMax))
        {
            points.Add(new Avalonia.Point(x, y));
        }
        return points;
    }

    private string BuildMachineOverview(MetricSample s)
    {
        try
        {
            _cpuNameCache ??= WinRegistry.ReadCpuName();
            var parts = new List<string>
            {
                $"{Environment.MachineName} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
                $"CPU: {(_cpuNameCache.Length > 0 ? _cpuNameCache : "unknown")} · {Environment.ProcessorCount} threads",
            };
            if (s.RamTotalMB > 0) parts.Add($"RAM: {s.RamTotalMB / 1024.0:0.0} GB");
            var screens = _services.Screens.All;
            if (screens.Count > 0)
            {
                parts.Add("Screens: " + string.Join(", ",
                    screens.Select(sc => $"{sc.Bounds.Width}×{sc.Bounds.Height}{(sc.IsPrimary ? "★" : "")}")));
            }
            parts.Add($"App: Patterns · .NET {Environment.Version} · folder {_services.Store.BaseDirectory}");
            return string.Join(Environment.NewLine, parts);
        }
        catch (Exception ex)
        {
            Log.Warn("Machine overview failed.", ex);
            return "";
        }
    }

    public void PollNow() => PollStatus();

    private void PollStatus()
    {
        ShowControls?.Refresh();
        var active = _services.Ndi.ActiveCount;
        foreach (var cfg in State.Ndi.Senders)
        {
            cfg.Status = _services.Ndi.StatusFor(cfg.Id);
        }
        NdiStatus = active > 0
            ? $"{active} sender{(active == 1 ? "" : "s")} active"
            : NdiRuntimeFound ? "Off" : "Runtime not found";
        PlaylistStatus = _services.Playlist.Status;
        ToneStatus = _services.Audio.Status;
        FeedStatus = _services.Feeds.Status;
        WebStatus = _services.Web.Status;
        AudioPlayerStatus = _services.AudioPlayer.Status;
        SyncStatus = BuildSyncStatus();
        Raise(nameof(DirectOutputSummary));
        StingerStatus = _services.Stingers.Status;
        RefreshStingerGroups();
        RefreshAfterChoices();
        RefreshTallies();
        SyncVirtualScreens();
        StingerHolding = _services.Stingers.Holding;
        StingerHoldText = StingerHolding ? $"'{_services.Stingers.HoldName}' is holding the screens." : "";
        SpotifyStatus = _services.Spotify.Status;
        SpotifyAccountText = _services.Spotify.AccountText;
        SpotifyNowPlaying = _services.Spotify.NowPlaying;
        if (!ReferenceEquals(_spotifyDevicesSeen, _services.Spotify.Devices)) RefreshSpotifyDevices();       // CONNECT filled them in
        if (!ReferenceEquals(_spotifyPlaylistsSeen, _services.Spotify.Playlists)) RefreshSpotifyPlaylists();
        if (!ReferenceEquals(_spotifyTracksSeen, _services.Spotify.Tracks) ||
            !ReferenceEquals(_spotifySearchSeen, _services.Spotify.SearchHits) ||
            SpotifyBrowseStatus != _services.Spotify.BrowseStatus)
        {
            RefreshSpotifyBrowse();
        }
        RefreshLookMusicChoices(); // a renamed or added entry, a loaded show
        FractalAudioStatus = _services.Analyser.Status;
        if (ActivePattern.Kind == PatternKind.Fractal) RefreshAudioCaptureDevices();
        HealthText = HealthMonitor.Summary(DateTime.UtcNow);
        StreamStatus = _services.Stream.Status;
        _statusTicks++;
        PollAdmin();
        RefreshActiveInputs();
        RefreshSwitcherTiles();
        Run.Tick();
        RemoteStatus = State.Control.Enabled
            ? $"Remote: {_services.Control.RemoteUrls().Skip(1).FirstOrDefault() ?? _services.Control.RemoteUrls()[0]}"
            : "Remote control off.";
        _services.Video.SweepRetired();
        _services.NdiIn.SweepRetired();
        _services.WebIn.SweepRetired();
        CheckCues();

        // Now-playing marker on explicit playlist rows.
        var nowPath = _services.Bus.PlaylistNow?.Path;
        foreach (var item in PlaylistSequencer.AllItems(ActivePattern.Media.Playlist))
        {
            item.IsNowPlaying = nowPath is not null && string.Equals(item.Path, nowPath, StringComparison.OrdinalIgnoreCase);
        }
        RaisePlaylistSection(onlyOnChange: true);

        // Keep pick lists warm while their panels are in use (NDI discovery is push-based
        // and cheap to read; capture enumeration is COM, so on demand + first need only).
        if (ActivePattern.Media.Source == MediaSource.NdiFeed && ++_ndiPollTick % 3 == 0)
        {
            RefreshNdiSources(quiet: true);
        }
        if (ActivePattern.Media.Source == MediaSource.Capture && !_captureListLoaded)
        {
            RefreshCaptureDevices(quiet: true);
        }
        // The Format pickers follow their device; a refresh is free while the device is unchanged.
        if (ActivePattern.Media.Source == MediaSource.Capture) CaptureFormat.Refresh();
        if (State.Overlays.Pip.Enabled && State.Overlays.Pip.Source == PipSource.Capture) PipCaptureFormat.Refresh();
        RefreshWebControls();
        Raise(nameof(CanvasInfo));
        Raise(nameof(HeaderClock));
        Raise(nameof(CountdownPreview));
    }

    private void OnSnapshotPublished()
    {
        // The sandbox can open or close without going through the toggle (startup arming, the
        // re-arm after a send, a discard from a service) — keep the switcher honest about it.
        Raise(nameof(IsSandboxActive));
        Raise(nameof(CanvasInfo));
        Raise(nameof(ShowCanvasPanel));
        Raise(nameof(InputNickname));
        RaiseArrangement();
        Raise(nameof(IsBlackout)); // Space, Shift+F8, a remote or an output-window key moved it
        RefreshSwitcherTiles(); // tally: blackout, a screen switched, the sandbox opened or closed
    }

    private void RefreshOutputsStatus()
    {
        // Planned screens are counted separately — an operator must never read "4 detected"
        // and believe four displays are plugged in.
        var detected = _services.Screens.Real.Count;
        var planned = PlannedScreenCount;
        var enabled = State.Output.Placements.Count(p => p.Enabled && !p.Planned && LiveInfo(p) is not null);
        var plannedText = planned > 0 ? $" · {planned} planned" : "";
        OutputsStatus = _services.Outputs.IsLive
            ? "LIVE — outputs running"
            : IsPrepMode
                ? $"PREP — {detected} display{(detected == 1 ? "" : "s")} detected{plannedText} · outputs held"
                : $"{detected} screen{(detected == 1 ? "" : "s")} detected{plannedText} · {enabled} enabled — press OUTPUTS ON";
        Raise(nameof(IsLive));

        // Every path that can change the mode or the planned set — a show load, a display
        // hot-plug, adoption — reaches here, so the mode UI is refreshed in one place.
        Raise(nameof(IsPrepMode));
        Raise(nameof(ModeBanner));
        Raise(nameof(PlannedScreenCount));
        Raise(nameof(VirtualScreenCount));
        Raise(nameof(PrepSummary));
    }

    public bool IsLive => _services.Outputs.IsLive;

    // ---- library ------------------------------------------------------------

    /// <summary>The section chips, in the order they are shown; "All" first.</summary>
    public static readonly string[] SectionNames = { "All", "Patterns", "Images", "Videos", "Audio", "Particles", "Presets", "Brand kits" };

    public string[] LibrarySections => SectionNames;

    /// <summary>Every tile, whatever the chips and the search say.</summary>
    public List<PresetItem> LibraryAll { get; } = new();

    /// <summary>The tiles the page shows: <see cref="LibraryAll"/> through the section chip and the search box.</summary>
    public ObservableCollection<PresetItem> Library { get; } = new();

    private string _selectedLibrarySection = "All";
    public string SelectedLibrarySection
    {
        get => _selectedLibrarySection;
        set
        {
            if (!Set(ref _selectedLibrarySection, value ?? "All")) return;
            ApplyLibraryFilter();
        }
    }

    private string _librarySearch = "";
    public string LibrarySearch
    {
        get => _librarySearch;
        set
        {
            if (!Set(ref _librarySearch, value ?? "")) return;
            ApplyLibraryFilter();
        }
    }

    private string _librarySummary = "";

    /// <summary>"74 tiles", or "12 of 74 · Images · 'logo'".</summary>
    public string LibrarySummary { get => _librarySummary; private set => Set(ref _librarySummary, value); }

    /// <summary>The thumbnails in flight for the current library — awaited by tests and the screenshot pass.</summary>
    public Task LibraryThumbnails { get; private set; } = Task.CompletedTask;

    private RelayCommand<PresetItem>? _removeLibraryItem;

    public RelayCommand<PresetItem> RemoveLibraryItemCommand => _removeLibraryItem ??= new RelayCommand<PresetItem>(item =>
    {
        if (item?.Remove is null) return;
        item.Remove();
        StatusMessage = $"'{item.Name}' taken out of the library — the file itself stays.";
        RefreshLibrary();
    });

    private RelayCommand? _refreshLibrary;

    public RelayCommand RefreshLibraryCommand => _refreshLibrary ??= new RelayCommand(RefreshLibrary);

    /// <summary>Rebuilds every tile — the factory table, the show's media, the saved presets, the brand kits — and re-renders the thumbnails.</summary>
    public void RefreshLibrary() => BuildLibrary();

    // ---- particle scenes, by pack ----------------------------------------------

    /// <summary>The Particles page's chips: every factory pack in order, then "Custom" — the operator's saved particle presets.</summary>
    public ObservableCollection<ParticlePackGroup> ParticlePackGroups { get; } = new();

    private RelayCommand<ParticleChip>? _applyParticleChip;

    public RelayCommand<ParticleChip> ApplyParticleChipCommand => _applyParticleChip ??= new RelayCommand<ParticleChip>(chip => chip?.Apply());

    private void RefreshParticlePackGroups()
    {
        var groups = new List<ParticlePackGroup>();
        foreach (var category in ParticlePresets.Categories)
        {
            groups.Add(new ParticlePackGroup(category, ParticlePresets.In(category)
                .Select(pack => new ParticleChip(pack.Name, () => _services.BulkEdit(() => ParticlePresets.Apply(pack.Name, ActivePattern.Particles))))
                .ToList()));
        }
        var custom = new List<ParticleChip>();
        foreach (var (name, path) in _services.Store.ListPresets())
        {
            var p = path;
            if (_services.Store.LoadPreset(p) is not { Kind: PatternKind.Particles }) continue;
            custom.Add(new ParticleChip(name, () =>
            {
                if (_services.Store.LoadPreset(p) is not { } cfg) return;
                _services.BulkEdit(() => ModelCopier.Copy(cfg.Particles, ActivePattern.Particles));
            }));
        }
        if (custom.Count > 0) groups.Add(new ParticlePackGroup("Custom", custom));
        if (ParticlePackGroups.Count == groups.Count &&
            ParticlePackGroups.Zip(groups).All(z => z.First.Category == z.Second.Category &&
                                                    z.First.Chips.Select(c => c.Name).SequenceEqual(z.Second.Chips.Select(c => c.Name))))
        {
            return; // the same chips: leave the page alone
        }
        ParticlePackGroups.Clear();
        foreach (var g in groups) ParticlePackGroups.Add(g);
    }

    private void BuildLibrary()
    {
        RefreshParticlePackGroups();
        LibraryAll.Clear();
        foreach (var b in BuiltInPresets.All)
        {
            var preset = b;
            LibraryAll.Add(new PresetItem
            {
                Id = $"builtin:{preset.Category}:{preset.Name}",
                Section = preset.Section,
                Category = preset.Category,
                Name = preset.Name,
                Apply = () => _services.BulkEdit(() => preset.Apply(ActivePattern)),
                ThumbConfig = baseState =>
                {
                    var config = JsonUtil.ClonePattern(baseState.Pattern);
                    preset.Apply(config);
                    return config;
                },
            });
        }

        foreach (var media in State.MediaLibrary.ToList())
        {
            var entry = media;
            var kind = entry.Kind == LibraryMediaKind.Unknown ? MediaLibraryEntry.KindOf(entry.Path, entry.IsVideo) : entry.Kind;
            var (section, category) = kind switch
            {
                LibraryMediaKind.Video => ("Videos", "My videos"),
                LibraryMediaKind.Audio => ("Audio", "My audio"),
                _ => ("Images", "My images"),
            };
            LibraryAll.Add(new PresetItem
            {
                Id = "media:" + entry.Id,
                Section = section,
                Category = category,
                Name = entry.DisplayName,
                Apply = () => _services.BulkEdit(() => ApplyMedia(ActivePattern, entry, kind)),
                ThumbConfig = baseState =>
                {
                    var config = JsonUtil.ClonePattern(baseState.Pattern);
                    ApplyMedia(config, entry, kind);
                    return config;
                },
                Remove = () => State.MediaLibrary.Remove(entry),
            });
        }

        foreach (var (name, path) in _services.Store.ListPresets())
        {
            var p = path;
            LibraryAll.Add(new PresetItem
            {
                Id = "preset:" + p,
                Section = "Presets",
                Category = "My presets",
                Name = name,
                Apply = () =>
                {
                    var cfg = _services.Store.LoadPreset(p);
                    if (cfg is not null) _services.BulkEdit(() => ModelCopier.Copy(cfg, ActivePattern));
                },
                ThumbConfig = _ => _services.Store.LoadPreset(p),
            });
        }

        foreach (var (name, path) in _services.Store.ListBrandKits())
        {
            var p = path;
            var kit = _services.Store.LoadBrandKit(p);
            if (kit is null) continue;
            var kitName = name;
            LibraryAll.Add(new PresetItem
            {
                Id = "brand:" + p,
                Section = "Brand kits",
                Category = "Brand kit",
                Name = kitName,
                Apply = () =>
                {
                    var fresh = _services.Store.LoadBrandKit(p);
                    if (fresh is null) return;
                    _services.BulkEdit(() => ModelCopier.Copy(fresh, State.Brand));
                    StatusMessage = $"Brand kit '{kitName}' applied.";
                },
                Swatch = new[] { kit.PrimaryColor, kit.SecondaryColor, kit.AccentColor, kit.BackgroundColor, kit.TextColor },
            });
        }

        ApplyLibraryFilter();
        LibraryThumbnails = RenderThumbnailsAsync(LibraryAll.ToList());
    }

    /// <summary>A media tile on a pattern: an image shows; a video or an audio file plays through the decoder.</summary>
    private static void ApplyMedia(PatternConfig target, MediaLibraryEntry entry, LibraryMediaKind kind)
    {
        target.Kind = PatternKind.Media;
        if (kind == LibraryMediaKind.Image)
        {
            target.Media.Source = MediaSource.Image;
            target.Media.ImagePath = entry.Path;
        }
        else
        {
            target.Media.Source = MediaSource.Video;
            target.Media.VideoPath = entry.Path;
        }
    }

    /// <summary>The chip and the search box together: every search word must appear in the tile's name, category or section.</summary>
    private void ApplyLibraryFilter()
    {
        var words = LibrarySearch.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var section = SelectedLibrarySection;
        var shown = LibraryAll
            .Where(i => section == "All" || i.Section == section)
            .Where(i => words.All(w => i.SearchKey.Contains(w, StringComparison.Ordinal)))
            .ToList();
        Library.Clear();
        foreach (var i in shown) Library.Add(i);
        var where = section == "All" ? "" : $" · {section}";
        var searched = words.Length == 0 ? "" : $" · '{LibrarySearch.Trim()}'";
        LibrarySummary = shown.Count == LibraryAll.Count
            ? $"{LibraryAll.Count} tiles"
            : $"{shown.Count} of {LibraryAll.Count}{where}{searched}";
    }

    /// <summary>One thumbnail per tile, keyed by the tile itself — two files of one name in two folders each get their own.</summary>
    private async Task RenderThumbnailsAsync(IReadOnlyList<PresetItem> items)
    {
        var baseState = JsonUtil.Clone(State);
        foreach (var item in items)
        {
            try
            {
                Bitmap? bmp = null;
                if (item.Swatch is { } swatch)
                {
                    var caption = item.Name;
                    bmp = await Task.Run(() => ThumbnailRenderer.Swatch(swatch, caption));
                }
                else if (item.ThumbConfig?.Invoke(baseState) is { } cfg)
                {
                    bmp = await Task.Run(() => ThumbnailRenderer.Render(baseState, cfg));
                }
                if (bmp is not null) item.Thumbnail = bmp;
            }
            catch (Exception ex)
            {
                Log.Warn($"Thumbnail for '{item.Name}' failed.", ex);
            }
        }
    }

    private void SaveUserPreset()
    {
        var name = string.IsNullOrWhiteSpace(NewPresetName) ? $"Preset {DateTime.Now:HHmmss}" : NewPresetName.Trim();
        try
        {
            _services.Store.SavePreset(name, ActivePattern);
            StatusMessage = $"Preset '{name}' saved.";
            NewPresetName = "";
            BuildLibrary();
        }
        catch (Exception ex)
        {
            Log.Error("Preset save failed.", ex);
            StatusMessage = $"Preset save failed: {ex.Message}";
        }
    }

    private void SaveBrandKit()
    {
        var name = string.IsNullOrWhiteSpace(State.Brand.CompanyName) ? "brand" : State.Brand.CompanyName;
        try
        {
            _services.Store.SaveBrandKit(name, State.Brand);
            StatusMessage = $"Brand kit '{name}' saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Brand kit save failed: {ex.Message}";
        }
    }

    private async Task LoadBrandKitAsync()
    {
        var path = await PickOpenPathAsync("Load brand kit", new FilePickerFileType("Brand kit") { Patterns = new[] { "*.json" } },
            _services.Store.BrandKitsDirectory);
        if (path is null) return;
        var kit = _services.Store.LoadBrandKit(path);
        if (kit is not null)
        {
            _services.BulkEdit(() => ModelCopier.Copy(kit, State.Brand));
            StatusMessage = "Brand kit loaded.";
        }
    }

    // ---- file dialogs -------------------------------------------------------

    private static string[] Glob(params string[][] extensionSets)
        => extensionSets.SelectMany(set => set.Select(e => "*" + e)).ToArray();

    private static readonly FilePickerFileType VideoTypes = new("Video & audio")
    {
        Patterns = Glob(PlaylistSequencer.VideoExtensions, PlaylistSequencer.AudioExtensions),
    };

    private static readonly FilePickerFileType MediaTypes = new("Images, video & audio")
    {
        Patterns = Glob(PlaylistSequencer.ImageExtensions, PlaylistSequencer.VideoExtensions, PlaylistSequencer.AudioExtensions),
    };

    private static readonly FilePickerFileType ShowTypes = new("Patterns show")
    {
        Patterns = new[] { "*.patshow.json", "*.json" },
    };

    private async Task PickFileAsync(string title, FilePickerFileType type, Action<string> assign)
    {
        var path = await PickOpenPathAsync(title, type, null);
        if (path is not null) assign(path);
    }

    private async Task<string?> PickOpenPathAsync(string title, FilePickerFileType type, string? suggestedDir)
    {
        var window = _services.MainWindow;
        if (window is null) return null;
        try
        {
            IStorageFolder? start = null;
            if (suggestedDir is not null && Directory.Exists(suggestedDir))
            {
                start = await window.StorageProvider.TryGetFolderFromPathAsync(suggestedDir);
            }
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[] { type, FilePickerFileTypes.All },
                SuggestedStartLocation = start,
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            Log.Error("File picker failed.", ex);
            return null;
        }
    }

    private async Task SaveShowAsync()
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save show",
                SuggestedFileName = "show.patshow.json",
                FileTypeChoices = new[] { ShowTypes },
            });
            var path = file?.TryGetLocalPath();
            if (path is null) return;
            if (State.Name.Length == 0) State.Name = SettingsStore.ShowNameFor(path);
            _services.Store.SaveTo(path, State);
            StatusMessage = $"Show saved: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Log.Error("Show save failed.", ex);
            StatusMessage = $"Show save failed: {ex.Message}";
        }
    }

    private async Task LoadShowAsync()
    {
        var path = await PickOpenPathAsync("Load show", ShowTypes, null);
        if (path is null) return;
        var loaded = _services.Store.LoadFrom(path);
        if (loaded is null)
        {
            StatusMessage = "Show file could not be read.";
            return;
        }
        _services.BulkEdit(() => ModelCopier.Copy(loaded, State));
        _services.Cues.Reset(); // every list starts over, disarmed
        Cues.OnShowLoaded();
        RefreshSpotifyDevices();
        RefreshStingerGroups();
        RefreshAfterChoices();
        ReconcilePlacements();
        BuildLibrary();
        StatusMessage = $"Show loaded: {Path.GetFileName(path)}";
    }
}
