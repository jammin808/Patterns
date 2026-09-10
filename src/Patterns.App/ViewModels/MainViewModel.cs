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

public sealed partial class MainViewModel : Observable
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

        // A contained UI fault reaches the operator at once on the status line; the health line
        // and the Machine page keep the count and the log has the stack.
        Patterns.App.Services.UiFaults.Listener = words =>
            StatusMessage = $"A fault was contained and the desk carried on — {words}. patterns.log has the stack; the Machine page counts it.";

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
        // A deck: a PDF — or a PowerPoint through LibreOffice — a page at a time; the desk's buttons turn the deck the pattern shows.
        BrowseDeckCommand = new RelayCommand(() => _ = PickFileAsync("Choose a deck — a PDF or a PowerPoint", DeckTypes, p =>
        {
            BulkEdit(() =>
            {
                ActivePattern.Kind = PatternKind.Media;
                ActivePattern.Media.Source = MediaSource.Deck;
                ActivePattern.Media.DeckPath = p;
            });
            AddToMediaLibrary(p, isVideo: false);
            StatusMessage = DeckConversion.NeedsConversion(p)
                ? $"{System.IO.Path.GetFileName(p)} is the pattern — LibreOffice converts it to PDF once, then the click-through turns its pages on air."
                : $"{System.IO.Path.GetFileName(p)} is the pattern — the click-through turns its pages once it is on air.";
        }));
        ReloadDeckCommand = new RelayCommand(() =>
        {
            var path = ActivePattern.Media.DeckPath;
            if (path.Length == 0)
            {
                StatusMessage = "Choose a deck first.";
                return;
            }
            _services.DeckIn.Reload(path);
            _services.ReconcileInputs();
            _services.PublishRuntime();
            RefreshDeck();
            StatusMessage = DeckConversion.NeedsConversion(path)
                ? $"{System.IO.Path.GetFileName(path)} is being read and converted again."
                : $"{System.IO.Path.GetFileName(path)} is being read again.";
        });
        DeckNextCommand = new RelayCommand(() => TurnDeskDeck("next"));
        DeckPrevCommand = new RelayCommand(() => TurnDeskDeck("prev"));
        VideoToEndCommand = new RelayCommand(() => Report(_services.Actions.Execute(ShowActionKind.VideoToEnd, ActionOrigin.Desk)));
        VideoRestartCommand = new RelayCommand(() => Report(_services.Actions.Execute(ShowActionKind.VideoRestart, ActionOrigin.Desk)));
        DeckFirstCommand = new RelayCommand(() => TurnDeskDeck("first"));
        DeckLastCommand = new RelayCommand(() => TurnDeskDeck("last"));
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
            var typed = WebAddress.Normalize(State.Web.Url);
            if (typed.Length == 0)
            {
                StatusMessage = "Enter a page address first.";
                return;
            }
            // A YouTube, Vimeo or Slides link goes on as the player or the deck alone — the streamlined path;
            // the Media page shows the address and can put the typed one back. The service named
            // here and the CLEAN tick travel with it, so the page lands the way it was set up.
            var pick = State.Web.Service;
            var url = WebPresets.FullFrame(typed, pick);
            var preset = WebPresets.For(url, pick);
            var clean = State.Web.Clean;
            _services.BulkEdit(() =>
            {
                ActivePattern.Kind = PatternKind.Media;
                ActivePattern.Media.Source = MediaSource.Web;
                ActivePattern.Media.WebUrl = url;
                ActivePattern.Media.WebService = pick;
                ActivePattern.Media.WebClean = clean;
            });
            if (!State.Web.SavedUrls.Contains(typed)) State.Web.SavedUrls.Add(typed);
            RefreshWebControls();
            StatusMessage = preset.Service == PageService.Page
                ? $"{WebAddress.ShortName(url)} is the pattern now — drive it on the PREVIEW pane; its settings are on the Media page."
                : $"{preset.Name} is the pattern now{(url == typed ? "" : ", full frame — the player or the deck alone")}. Drive it on the PREVIEW pane, with PAGE CONTROLS, the phone, cues or KEYS → PAGE.";
        });
        WebFullFrameCommand = new RelayCommand(() =>
        {
            var pick = ActivePattern.Media.WebService;
            var url = WebAddress.Normalize(ActivePattern.Media.WebUrl);
            var full = WebPresets.FullFrame(url, pick);
            if (url.Length == 0 || full == url)
            {
                StatusMessage = url.Length == 0 ? "Enter a page address first." : "That address is already the page alone.";
                return;
            }
            BulkEdit(() => ActivePattern.Media.WebUrl = full);
            RefreshWebControls();
            StatusMessage = $"{WebPresets.For(full, pick).Name} full frame: {full}";
        });
        WebActionCommand = new RelayCommand<string>(id => RunWebAction(id ?? ""));

        // Presenter click-through: the clicker list on the Cues page, stepped from here
        PresenterNextCommand = new RelayCommand(() => _services.Actions.PresenterAdvance(+1, ActionOrigin.Desk));
        PresenterPrevCommand = new RelayCommand(() => _services.Actions.PresenterAdvance(-1, ActionOrigin.Desk));
        PresenterResetCommand = new RelayCommand(() =>
        {
            _services.Actions.Execute(ShowActionKind.ListReset, ActionOrigin.Desk, CueStacks.Clicker(State).Id);
            Raise(nameof(PresenterStepText));
            Raise(nameof(ProgressionText));
        });

        // The audio playlist
        AddAudioFilesCommand = new RelayCommand(() => _ = AddAudioFilesAsync());
        AddAudioFolderCommand = new RelayCommand(() => _ = AddAudioFolderAsync());
        RemoveAudioItemCommand = new RelayCommand<AudioTrackConfig>(item =>
        {
            if (item is not null) State.AudioPlayer.Items.Remove(item);
        });
        RemoveAudioFolderCommand = new RelayCommand<string>(folder =>
        {
            if (folder is not null) State.AudioPlayer.Folders.Remove(folder);
        });
        PlayAudioItemCommand = new RelayCommand<AudioTrackConfig>(item =>
        {
            if (item is null) return;
            if (AudioDevices.Count == 0) RefreshAudioDevices();
            Report(_services.Actions.Execute(ShowActionKind.AudioPlay, ActionOrigin.Desk, item.Id));
        });
        MoveAudioItemUpCommand = new RelayCommand<AudioTrackConfig>(item => MoveAudioItem(item, -1));
        MoveAudioItemDownCommand = new RelayCommand<AudioTrackConfig>(item => MoveAudioItem(item, +1));
        AudioNextCommand = new RelayCommand(() => Report(_services.Actions.Execute(ShowActionKind.AudioNext, ActionOrigin.Desk)));
        AudioPrevCommand = new RelayCommand(() => Report(_services.Actions.Execute(ShowActionKind.AudioPrev, ActionOrigin.Desk)));
        ReshuffleAudioCommand = new RelayCommand(() =>
        {
            State.AudioPlayer.ShuffleSeed = Random.Shared.Next(1, int.MaxValue);
            State.AudioPlayer.Shuffle = true;
            StatusMessage = "The audio playlist dealt a new order.";
        });
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
        PreviewLowerThirdCommand = new RelayCommand<LowerThirdDesign>(d => { if (d is not null) PreviewLowerThird(d); });
        TakeLowerThirdCommand = new RelayCommand(() => TakeLowerThird());
        UpdateLowerThirdCommand = new RelayCommand(() => UpdateLowerThird());
        ClearLowerThirdPreviewCommand = new RelayCommand(() => ClearLowerThirdPreview());
        SetDefaultLowerThirdCommand = new RelayCommand<LowerThirdDesign>(d => { if (d is not null) SetDefaultLowerThird(d); });
        ChipLowerThirdCommand = new RelayCommand<LowerThirdDesign>(d =>
        {
            if (d is null) return;
            if (LowerThirdChipsToPreview) PreviewLowerThird(d);
            else ShowLowerThird(d);
        });
        ChipEntryCommand = new RelayCommand<LowerThirdEntry>(e =>
        {
            if (e is null) return;
            if (LowerThirdChipsToPreview) PreviewEntry(e, null);
            else ShowEntry(e, null);
        });
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
        NewEntryCommand = new RelayCommand(() => NewEntry());
        DeleteEntryCommand = new RelayCommand<LowerThirdEntry>(DeleteEntry);
        UseEntryCommand = new RelayCommand<LowerThirdEntry>(e => { if (e is not null) UseEntry(e); });
        ShowEntryCommand = new RelayCommand<LowerThirdEntry>(e => { if (e is not null) ShowEntry(e, SelectedLowerThird); });
        ShowEntryOnAirCommand = new RelayCommand<LowerThirdEntry>(e => { if (e is not null) ShowEntry(e, null); });
        PreviewEntryCommand = new RelayCommand<LowerThirdEntry>(e => { if (e is not null) PreviewEntry(e, SelectedLowerThird); });
        BrowseEntryPhotoCommand = new RelayCommand(() => _ = BrowseEntryPhotoAsync());
        ImportPeopleCommand = new RelayCommand(() => _ = ImportPeopleAsync(append: false));
        ImportPeopleAppendCommand = new RelayCommand(() => _ = ImportPeopleAsync(append: true));
        ExportPeopleCommand = new RelayCommand(() => _ = SaveTextAsync("Export the people library", "people.csv", ExportPeopleCsv(), "People exported"));
        SavePeopleTemplateCommand = new RelayCommand(() => _ = SaveTextAsync("Save the people template", "people-template.csv", LowerThirdLibrary.Template(), "Template saved"));
        PreviewRestartCommand = new RelayCommand(() => PreviewTimeMs = 0);
        ClearCropCommand = new RelayCommand(ClearCrop);
        CropPresetCommand = new RelayCommand<string>(p => ApplyCropPreset(p ?? ""));
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

        // The Interactive area: Arduinos over serial, Raspberry Pis and controllers over IP.
        AddSerialDeviceCommand = new RelayCommand(() => AddDevice(DeviceLink.Serial));
        AddIpDeviceCommand = new RelayCommand(() => AddDevice(DeviceLink.Tcp));
        RemoveDeviceCommand = new RelayCommand<DeviceConfig>(RemoveDevice);
        TestDeviceCommand = new RelayCommand<DeviceConfig>(TestDevice);
        ResendDeviceCommand = new RelayCommand<DeviceConfig>(d =>
        {
            if (d is null) return;
            _services.Devices.Resend(d);
            StatusMessage = $"{d.Name}: every fact of the show sent again.";
        });
        AddTriggerCommand = new RelayCommand<DeviceConfig>(AddTrigger);
        RemoveTriggerCommand = new RelayCommand<DeviceTriggerConfig>(RemoveTrigger);

        // The Install page: the rota, adverts and announcements, remote administration, updates.
        AddProgrammeCommand = new RelayCommand(() => AddSlot(SlotKind.Programme));
        AddAdvertCommand = new RelayCommand(() => AddSlot(SlotKind.Advert));
        AddAnnouncementCommand = new RelayCommand(() => AddSlot(SlotKind.Announcement));
        RemoveSlotCommand = new RelayCommand<ScheduleSlotConfig>(RemoveSlot);
        PlaySlotCommand = new RelayCommand<ScheduleSlotConfig>(PlaySlot);
        EndInstallOverrideCommand = new RelayCommand(EndInstallOverride);
        SupportBundleCommand = new RelayCommand(BuildSupportBundle);
        CheckInNowCommand = new RelayCommand(() =>
        {
            _services.Management.CheckInNow();
            StatusMessage = State.Install.ManagementUrl.Length == 0 ? "Type the management server's check-in URL first." : "Checking in…";
        });
        ApplyUpdateCommand = new RelayCommand(ApplyUpdate);
        AddGapCommand = new RelayCommand(AddGap);
        RemoveGapCommand = new RelayCommand<WallGap>(RemoveGap);
        SetGapsFromGridCommand = new RelayCommand(SetGapsFromGrid);
        ClearGapsCommand = new RelayCommand(ClearGaps);

        // Walkthroughs on the Help page: the roles, the first role's scenarios, the first scenario open.
        WalkNextCommand = new RelayCommand(WalkNext);
        WalkBackCommand = new RelayCommand(WalkBack);
        WalkRestartCommand = new RelayCommand(WalkRestart);

        // Freeze, the timed fade, the previous look, the show file's earlier versions.
        FreezeCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.FreezeToggle, ActionOrigin.Desk));
        // The fade lands where the picker says: every screen, the focused tile, the ticked tiles or
        // the ticked groups; the action's own words (a refusal says what to tick) go to the status line.
        FadeToBlackCommand = new RelayCommand(() => StatusMessage = _services.Actions.Execute(ShowActionKind.FadeToBlack, ActionOrigin.Desk, SelectedFadeScope.Words, FadeSecondsText()).Message);
        FadeUpCommand = new RelayCommand(() => StatusMessage = _services.Actions.Execute(ShowActionKind.FadeUp, ActionOrigin.Desk, SelectedFadeScope.Words, FadeSecondsText()).Message);
        // A scoped FADE reads the wall's focus and ticks through the services, never the other way round.
        _services.FocusedTarget = () => _selectedTargetId;
        _services.TickedTargets = () => SwitcherTiles.Where(t => t.IsSendTarget && t.TargetId is not null).Select(t => t.TargetId!).ToList();
        LookBackCommand = new RelayCommand(() => StatusMessage = _services.Actions.Execute(ShowActionKind.LookBack, ActionOrigin.Desk).Message);
        RestoreBackupCommand = new RelayCommand(RestoreBackup);
        OpenBackupsFolderCommand = new RelayCommand(OpenBackupsFolder);
        RefreshBackups();
        WalkRoles = Enum.GetValues<DeskRole>().Select(r => new WalkRoleChip(this, r)).ToList();
        RebuildWalkList();
        if (Walkthroughs.For(_walkRole).FirstOrDefault() is { } firstWalk) StartWalkthrough(firstWalk.Id);

        // Help: the catalogue's section chips, the search, every card
        HelpGroups = new[] { new HelpGroupChip(this, null) }
            .Concat(HelpTopics.Groups.Select(g => new HelpGroupChip(this, g)))
            .ToList();
        ClearHelpCommand = new RelayCommand(() => HelpQuery = "");
        RefreshHelpRows();

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

        // The monitor walls (SETUP → Multiview): the show's own, up to two, each on however many
        // outputs are ticked for it.
        BuildMultiviewCommands();

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
        State.Admin.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AdminConfig.VideoDecoding)) Raise(nameof(VideoDecodingText));
        };
        HookTransition();
        RebuildGpuRows();

        // Switcher: sandbox sends, CUT/TAKE, tile selection. CUT and TAKE land where the wall's picker
        // says (every armed screen, the focused tile, the ticked tiles, the ticked groups); the Show
        // panel's SEND TO ALL is always everything.
        TakeCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, SelectedTakeScope.Words));
        CutCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.Cut, ActionOrigin.Desk, SelectedTakeScope.Words));
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
            StatusMessage = $"Sent to {titles} as their own pattern — every other target stays as it was, and the preview keeps the picture.";
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
        Cues.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CueEditor.SelectedCue)) RefreshPopOut();   // the settings column follows the selected cue
        };
        Run = new RunViewModel(_services, this);

        // The caller's home: a running order in and out of the Cues page
        var cues = Cues;
        ImportCueSheetCommand = new RelayCommand(() => _ = ImportCueSheetAsync(append: false));
        ImportCueSheetAppendCommand = new RelayCommand(() => _ = ImportCueSheetAsync(append: true));
        ExportCueSheetCommand = new RelayCommand(() => _ = SaveTextAsync("Export the cue list", (cues.SelectedStack?.Name ?? "cues") + ".csv", cues.ExportCsv(), "Cue list exported"));
        SaveCueTemplateCommand = new RelayCommand(() => _ = SaveTextAsync("Save the cue sheet template", "cue-sheet-template.csv", CueSheet.Template(), "Template saved"));
        _services.Cues.Changed += () =>
        {
            Raise(nameof(ClickerArmed));
            Raise(nameof(PresenterStepText));
            Raise(nameof(ProgressionText));
        };
        ShowControls = new ShowControls(_services, m => StatusMessage = m);
        CaptureFormat = new CaptureFormatPicker(() => State, () => ActivePattern.Media.CaptureDevice, () => _services.RepublishNow());
        PipCaptureFormat = new CaptureFormatPicker(() => State, () => State.Overlays.Pip.CaptureDevice, () => _services.RepublishNow());
        ApplyDisplayModeCommand = new RelayCommand(ApplyDisplayMode);
        KeepDisplayModeCommand = new RelayCommand(KeepDisplayMode);
        RevertDisplayModeCommand = new RelayCommand(RevertDisplayMode);
        SelectGroupCommand = new RelayCommand<ShellGroup>(SelectGroup);
        SelectPageCommand = new RelayCommand<int>(SelectPage);
        // A row dragged in a reorderable list, named by the list it came from: a cue's step, which
        // moves without taking its wait with it; a multiview tile, whose place in the list is what
        // makes it one of the wall's large ones.
        Views.Controls.DragReorder.Moved = (host, from, to) =>
        {
            if (from < 0) return;
            switch (host.Name)
            {
                case "ActionList" when from < Cues.ActionRows.Count:
                    Cues.MoveActionTo(Cues.ActionRows[from], to);
                    break;
                case "WallTiles" when SelectedWall is { } wall && from < wall.Tiles.Count:
                    MoveWallTileTo(wall.Tiles[from], to);
                    break;
            }
        };
        // The rail's foot and anything else that knows where it wants to go by name rather than
        // by the page's number, which moves whenever a page is added.
        SelectPageByNameCommand = new RelayCommand<string>(name =>
        {
            if (name is null) return;
            var index = Shell.IndexOf(name);
            if (index >= 0) SelectPage(index);
        });
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
        _services.Startup.Mark(StartupBudget.ViewModel);
    }

    public ShowState State => _services.State;
    public AppServices Services => _services;

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
    public RelayCommand BrowseDeckCommand { get; }
    public RelayCommand ReloadDeckCommand { get; }
    public RelayCommand DeckNextCommand { get; }
    public RelayCommand DeckPrevCommand { get; }
    public RelayCommand VideoToEndCommand { get; }
    public RelayCommand VideoRestartCommand { get; }
    public RelayCommand DeckFirstCommand { get; }
    public RelayCommand DeckLastCommand { get; }
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
    public RelayCommand<string> LoadWebUrlCommand { get; }
    public RelayCommand<string> RemoveWebUrlCommand { get; }
    public RelayCommand SendWebTextCommand { get; }
    public RelayCommand<string> WebKeyCommand { get; }
    public RelayCommand WebBackCommand { get; }
    public RelayCommand WebForwardCommand { get; }
    public RelayCommand WebReloadCommand { get; }
    public RelayCommand RememberWebUrlCommand { get; }
    public RelayCommand PutWebPageOnPatternCommand { get; }
    public RelayCommand WebFullFrameCommand { get; }
    public RelayCommand<string> WebActionCommand { get; }
    public RelayCommand ImportCueSheetCommand { get; }
    public RelayCommand ImportCueSheetAppendCommand { get; }
    public RelayCommand ExportCueSheetCommand { get; }
    public RelayCommand SaveCueTemplateCommand { get; }
    public RelayCommand PresenterNextCommand { get; }
    public RelayCommand PresenterPrevCommand { get; }
    public RelayCommand PresenterResetCommand { get; }
    public RelayCommand AddAudioFilesCommand { get; }
    public RelayCommand AddAudioFolderCommand { get; }
    public RelayCommand<AudioTrackConfig> RemoveAudioItemCommand { get; }
    public RelayCommand<string> RemoveAudioFolderCommand { get; }
    public RelayCommand<AudioTrackConfig> PlayAudioItemCommand { get; }
    public RelayCommand<AudioTrackConfig> MoveAudioItemUpCommand { get; }
    public RelayCommand<AudioTrackConfig> MoveAudioItemDownCommand { get; }
    public RelayCommand AudioNextCommand { get; }
    public RelayCommand AudioPrevCommand { get; }
    public RelayCommand ReshuffleAudioCommand { get; }
    public RelayCommand PlayAudioCommand { get; }
    public RelayCommand StopAudioCommand { get; }
    public RelayCommand RefreshAudioDevicesCommand { get; }
    public RelayCommand NewLowerThirdCommand { get; }
    public RelayCommand<LowerThirdDesign> DuplicateLowerThirdCommand { get; }
    public RelayCommand<LowerThirdDesign> DeleteLowerThirdCommand { get; }
    public RelayCommand<LowerThirdDesign> ShowLowerThirdCommand { get; }
    public RelayCommand HideLowerThirdCommand { get; }
    public RelayCommand<LowerThirdDesign> PreviewLowerThirdCommand { get; }
    public RelayCommand TakeLowerThirdCommand { get; }
    public RelayCommand UpdateLowerThirdCommand { get; }
    public RelayCommand ClearLowerThirdPreviewCommand { get; }
    public RelayCommand<LowerThirdDesign> SetDefaultLowerThirdCommand { get; }
    /// <summary>The Show panel's chips: to air, or with PVW FIRST to the preview.</summary>
    public RelayCommand<LowerThirdDesign> ChipLowerThirdCommand { get; }
    public RelayCommand<LowerThirdEntry> ChipEntryCommand { get; }
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
    public RelayCommand ClearCropCommand { get; }
    public RelayCommand<string> CropPresetCommand { get; }
    public RelayCommand NewEntryCommand { get; }
    public RelayCommand<LowerThirdEntry> DeleteEntryCommand { get; }
    public RelayCommand<LowerThirdEntry> UseEntryCommand { get; }
    public RelayCommand<LowerThirdEntry> ShowEntryCommand { get; }
    public RelayCommand<LowerThirdEntry> ShowEntryOnAirCommand { get; }
    public RelayCommand<LowerThirdEntry> PreviewEntryCommand { get; }
    public RelayCommand BrowseEntryPhotoCommand { get; }
    public RelayCommand ImportPeopleCommand { get; }
    public RelayCommand ImportPeopleAppendCommand { get; }
    public RelayCommand ExportPeopleCommand { get; }
    public RelayCommand SavePeopleTemplateCommand { get; }
    public RelayCommand PreviewRestartCommand { get; }
    public RelayCommand ResetWarpCommand { get; }
    public RelayCommand ResetBlendCommand { get; }
    public RelayCommand AddSerialDeviceCommand { get; }
    public RelayCommand AddIpDeviceCommand { get; }
    public RelayCommand<DeviceConfig> RemoveDeviceCommand { get; }
    public RelayCommand<DeviceConfig> TestDeviceCommand { get; }
    public RelayCommand<DeviceConfig> ResendDeviceCommand { get; }
    public RelayCommand<DeviceConfig> AddTriggerCommand { get; }
    public RelayCommand<DeviceTriggerConfig> RemoveTriggerCommand { get; }
    public RelayCommand AddProgrammeCommand { get; }
    public RelayCommand AddAdvertCommand { get; }
    public RelayCommand AddAnnouncementCommand { get; }
    public RelayCommand<ScheduleSlotConfig> RemoveSlotCommand { get; }
    public RelayCommand<ScheduleSlotConfig> PlaySlotCommand { get; }
    public RelayCommand EndInstallOverrideCommand { get; }
    public RelayCommand SupportBundleCommand { get; }
    public RelayCommand CheckInNowCommand { get; }
    public RelayCommand ApplyUpdateCommand { get; }
    public RelayCommand AddGapCommand { get; }
    public RelayCommand<WallGap> RemoveGapCommand { get; }
    public RelayCommand SetGapsFromGridCommand { get; }
    public RelayCommand ClearGapsCommand { get; }
    public RelayCommand WalkNextCommand { get; }
    public RelayCommand WalkBackCommand { get; }
    public RelayCommand WalkRestartCommand { get; }
    public RelayCommand FreezeCommand { get; }
    public RelayCommand FadeToBlackCommand { get; }
    public RelayCommand FadeUpCommand { get; }
    public RelayCommand LookBackCommand { get; }
    public RelayCommand RestoreBackupCommand { get; }
    public RelayCommand OpenBackupsFolderCommand { get; }
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

    // ---- file dialogs -------------------------------------------------------

    private static string[] Glob(params string[][] extensionSets)
        => extensionSets.SelectMany(set => set.Select(e => "*" + e)).ToArray();

    private static readonly FilePickerFileType VideoTypes = new("Video & audio")
    {
        Patterns = Glob(PlaylistSequencer.VideoExtensions, PlaylistSequencer.AudioExtensions),
    };

    private static readonly FilePickerFileType MediaTypes = new("Images, video, audio & decks (PDF, PowerPoint)")
    {
        Patterns = Glob(PlaylistSequencer.ImageExtensions, PlaylistSequencer.VideoExtensions, PlaylistSequencer.AudioExtensions, PlaylistSequencer.DeckExtensions),
    };

    private static readonly FilePickerFileType DeckTypes = new("Deck — PDF, PowerPoint, Keynote or Impress")
    {
        Patterns = Glob(PlaylistSequencer.DeckExtensions),
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

    private static readonly FilePickerFileType CueSheetTypes = new("Cue sheet (CSV or Excel)") { Patterns = new[] { "*.csv", "*.xlsx", "*.txt" } };
    private static readonly FilePickerFileType CsvTypes = new("CSV") { Patterns = new[] { "*.csv" } };
    private static readonly FilePickerFileType PeopleTypes = new("People list (CSV or Excel)") { Patterns = new[] { "*.csv", "*.xlsx", "*.txt" } };

    private async Task ImportCueSheetAsync(bool append)
    {
        var path = await PickOpenPathAsync(append ? "Append a cue sheet" : "Import a cue sheet", CueSheetTypes, null);
        if (path is null) return;
        StatusMessage = ImportCueSheetFrom(path, append);
    }

    /// <summary>Reads a CSV or the first sheet of an .xlsx into the selected list; returns the words for the status line.</summary>
    public string ImportCueSheetFrom(string path, bool append)
    {
        TableData table;
        try
        {
            table = path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? XlsxTable.Read(File.ReadAllBytes(path))
                : CsvTable.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Error("Cue sheet read failed.", ex);
            return $"Could not read {Path.GetFileName(path)}: {ex.Message}";
        }
        var report = Cues.ImportRows(table, append);
        return $"{report.Split('\n')[0]} ({Path.GetFileName(path)})";
    }

    private async Task SaveTextAsync(string title, string suggestedName, string text, string doneWord)
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = new[] { CsvTypes },
            });
            var path = file?.TryGetLocalPath();
            if (path is null) return;
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false)); // the text carries its own BOM for Excel
            StatusMessage = $"{doneWord}: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Log.Error($"{title} failed.", ex);
            StatusMessage = $"{title} failed: {ex.Message}";
        }
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
        ApplyLoadedShow(loaded, $"Show loaded: {Path.GetFileName(path)}");
    }

    /// <summary>A show read from a file becomes the show: the model copied over, every list started over, the desk refreshed.</summary>
    private void ApplyLoadedShow(ShowState loaded, string status)
    {
        // A different show: nothing the last one left waiting may run against it.
        _services.Tail.DropAll();
        _services.BulkEdit(() => ModelCopier.Copy(loaded, State));
        HookTransition();
        RefreshWallDestinations();   // another show, another set of walls and outputs
        _services.Cues.Reset(); // every list starts over, disarmed
        Cues.OnShowLoaded();
        RefreshSpotifyDevices();
        RefreshStingerGroups();
        RefreshAfterChoices();
        ReconcilePlacements();
        BuildLibrary();
        StatusMessage = status;
    }
}
