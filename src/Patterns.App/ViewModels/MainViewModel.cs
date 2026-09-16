using System.Globalization;
using Patterns.Devices;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Rendering.Effects;
using Patterns.Core.Model;
using Patterns.Ndi;
using Patterns.Rendering.Particles;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;

namespace Patterns.App.ViewModels;

/// <summary>Target chooser entries for the pattern editor ("Program" or one custom screen).</summary>
public sealed record EditTarget(string Label, string? ScreenId)
{
    public override string ToString() => Label;
}

public sealed partial class MainViewModel : Observable, IArcadePage, INodesPage, IRunPage, ICuesPage, IStagePage, IRunPageOwner
{
    private readonly AppServices _services;
    private EditTarget _editTarget = new("Program", null);
    private string _ndiStatus = "Off";
    private string _outputsStatus = "";
    private string _newPresetName = "";
    private ResolutionPreset? _selectedResolution;
    private int _selectedTileSize;
    private string _statusMessage = "";

    /// <summary>The two statics this desk's view model points at itself through — the UI fault listener and the drag-reorder drop — kept so <see cref="ReleaseStaticHooks"/> can let go of exactly its own (round 64: a closed desk stayed alive through them).</summary>
    private readonly Action<string> _faultListener;
    private readonly Action<Avalonia.Controls.ItemsControl, int, int> _moved;

    /// <summary>The desk's one-second status poll: stopped with the window — a running dispatcher timer roots its owner, and this one kept every closed desk alive (round 64's census).</summary>
    private readonly Avalonia.Threading.DispatcherTimer _statusTimer;

    /// <summary>
    /// The window closed: the statics that pointed at this view model let go of it, and every
    /// timer the desk's editors run stops, so the closed desk is reclaimed whole. Another desk's
    /// hooks are left alone.
    /// </summary>
    public void OnWindowClosed()
    {
        if (ReferenceEquals(Patterns.App.Services.UiFaults.Listener, _faultListener)) Patterns.App.Services.UiFaults.Listener = null;
        if (ReferenceEquals(Views.Controls.DragReorder.Moved, _moved)) Views.Controls.DragReorder.Moved = null;
        _statusTimer.Stop();
        _previewTimer?.Stop();
        _tallyTimer?.Stop();
        Run.StopTimers();
        Cues.StopTimers();
        Screens.StopTimers();
    }

    public MainViewModel(AppServices services)
    {
        _services = services;
        _services.RegisterViewModel(this);                                      // the desk stops this view model's timers when it shuts down, window or no window
        Screens = new ScreensPage(this, _services);
        Media = new MediaPage(this, _services);
        Show = new ShowPage(this, _services);

        // A contained UI fault reaches the operator at once on the status line; the health line
        // and the Machine page keep the count and the log has the stack.
        _faultListener = words =>
            StatusMessage = $"A fault was contained and the desk carried on — {words}. patterns.log has the stack; the Machine page counts it.";
        Patterns.App.Services.UiFaults.Listener = _faultListener;

        // Every verb goes through the action layer: one code path for the desk, the keyboard,
        // the remotes and the schedule, one journal, one place to resync the editors from.
        _services.Actions.Performed += OnActionPerformed;
        HookArcadeWindow();
        GoCommand = new RelayCommand(GoLive);
        StopCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.OutputsOff, ActionOrigin.Desk));
        IdentifyCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.Identify, ActionOrigin.Desk));
        BlackoutCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.BlackoutToggle, ActionOrigin.Desk));
        ArmCountdownCommand = new RelayCommand(() => BulkEdit(() =>
        {
            State.Countdown.ArmedAtUtc = DateTime.UtcNow;
            State.Countdown.Enabled = true;
        }));
        SaveShowCommand = new RelayCommand(() => _ = SaveShowAsync());
        LoadShowCommand = new RelayCommand(() => _ = LoadShowAsync());
        SavePresetCommand = new RelayCommand(SaveUserPreset);
        VideoToEndCommand = new RelayCommand(() => Report(_services.Actions.Execute(ShowActionKind.VideoToEnd, ActionOrigin.Desk)));
        VideoRestartCommand = new RelayCommand(() => Report(_services.Actions.Execute(ShowActionKind.VideoRestart, ActionOrigin.Desk)));
        BrowseLogoCommand = new RelayCommand(() => _ = PickFileAsync("Choose logo (PNG with alpha)", FilePickerFileTypes.ImageAll, p => State.Brand.LogoPath = p));
        BrowseLayerImageCommand = new RelayCommand<LayerConfig>(layer =>
        {
            if (layer is null) return;
            _ = PickFileAsync("Choose the layer's image", FilePickerFileTypes.ImageAll, p =>
            {
                BulkEdit(() =>
                {
                    layer.ImagePath = p;
                    layer.Source = LayerSource.Image;
                    layer.Enabled = true;
                });
                AddToMediaLibrary(p, isVideo: false);
            });
        });
        BrowseLayerVideoCommand = new RelayCommand<LayerConfig>(layer =>
        {
            if (layer is null) return;
            _ = PickFileAsync("Choose the layer's clip", VideoTypes, p =>
            {
                BulkEdit(() =>
                {
                    layer.VideoPath = p;
                    layer.Source = LayerSource.Video;
                    layer.Enabled = true;
                });
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
        ApplyPresetCommand = new RelayCommand<PresetItem>(ApplyLibraryItem);
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

        // Presenter click-through: the clicker list on the Cues page, stepped from here
        PresenterNextCommand = new RelayCommand(() => _services.Actions.PresenterAdvance(+1, ActionOrigin.Desk));
        PresenterPrevCommand = new RelayCommand(() => _services.Actions.PresenterAdvance(-1, ActionOrigin.Desk));
        PresenterResetCommand = new RelayCommand(() =>
        {
            _services.Actions.Execute(ShowActionKind.ListReset, ActionOrigin.Desk, CueStacks.Clicker(State).Id);
            Raise(nameof(PresenterStepText));
            Raise(nameof(ProgressionText));
        });

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
            if (!Enum.TryParse<LowerThirdElementKind>(kind, true, out var k)) return;
            var added = AddElement(k);
            // A picture or a clip element is nothing at all until it has a file, and the file row
            // lives a pop-out away: + PICTURE asks for the picture. Cancel and the empty element
            // stays, with Choose… still there — one gesture, not three.
            if (added is not null && k is LowerThirdElementKind.Image or LowerThirdElementKind.Media)
            {
                _ = PickElementFileAsync();
            }
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
        // The Interactive area: Arduinos over serial, Raspberry Pis and controllers over IP.
        AddSerialDeviceCommand = new RelayCommand(() => AddDevice(DeviceLink.Serial));
        AddIpDeviceCommand = new RelayCommand(() => AddDevice(DeviceLink.Tcp));
        AddMidiDeviceCommand = new RelayCommand(() => AddDevice(DeviceLink.Midi));
        LearnMidiCommand = new RelayCommand<DeviceConfig>(LearnMidi);
        SeedMidiCommand = new RelayCommand<DeviceConfig>(SeedMidi);
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
        _services.TakeScopeWords = () => SelectedTakeScope.Words;
        _services.NextTake.Changed += () =>
        {
            Raise(nameof(TakeButtonText));
            Raise(nameof(NextTakeWords));
            _services.Eye.Refresh();
        };
        _services.TickedTargets = () => SwitcherTiles.Where(t => t.IsSendTarget && t.TargetId is not null).Select(t => t.TargetId!).ToList();
        LookBackCommand = new RelayCommand(() => StatusMessage = _services.Actions.Execute(ShowActionKind.LookBack, ActionOrigin.Desk).Message);
        WalkRoles = Enum.GetValues<DeskRole>().Select(r => new WalkRoleChip(this, r)).ToList();
        RebuildWalkList();
        if (Walkthroughs.For(_walkRole).FirstOrDefault() is { } firstWalk) StartWalkthrough(firstWalk.Id);

        // Help: the catalogue's section chips, the search, every card
        HelpGroups = new[] { new HelpGroupChip(this, null) }
            .Concat(HelpTopics.Groups.Select(g => new HelpGroupChip(this, g)))
            .ToList();
        ClearHelpCommand = new RelayCommand(() => HelpQuery = "");
        RefreshHelpRows();

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
        HookMonitor();
        RefreshMonitorDevices();
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
            // Round 67: the tile clicked is the editing target — its own picture when it has one, an inert
            // copy of the programme until its first edit makes it its own; the big panes, the editors, the
            // menus and CUT / TAKE with FOCUSED all mean this tile. The PGM tile is the programme.
            if (tile.TargetId is { } wanted && EditTargets.All(t => t.ScreenId != wanted)) RebuildEditTargets();
            EditTarget = EditTargets.FirstOrDefault(t => t.ScreenId == tile.TargetId) ?? EditTargets[0];
            SelectTarget(tile.TargetId);
            StatusMessage = EditTargetBanner;
        });
        ArmAllCommand = new RelayCommand(() =>
        {
            _services.Arming.ArmAll();
            StatusMessage = "Every target armed — the next CUT / TAKE goes everywhere.";
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

        // Feed, trims
        RefreshFeedCommand = new RelayCommand(() => _services.Feeds.RefreshNow());
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
        Assistant = new AssistantPage(this, _services);
        Music = new MusicPage(this, _services);
        Audio = new AudioPage(this, _services);
        _services.ShowMirrored += RefreshAfterMirror;

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
        SelectGroupCommand = new RelayCommand<ShellGroup>(SelectGroup);
        SelectPageCommand = new RelayCommand<int>(SelectPage);
        // A row dragged in a reorderable list, named by the list it came from: a cue's step, which
        // moves without taking its wait with it; a multiview tile, whose place in the list is what
        // makes it one of the wall's large ones.
        _moved = (host, from, to) =>
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
        Views.Controls.DragReorder.Moved = _moved;
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
        _services.HotPlug.Offers.CollectionChanged += (_, _) => Raise(nameof(HasHotPlug));

        _statusTimer = global::Patterns.App.Services.DeskTimers.Make(TimeSpan.FromSeconds(1));
        _statusTimer.Tick += (_, _) => PollStatus();
        _statusTimer.Start();

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
    public RelayCommand VideoToEndCommand { get; }
    public RelayCommand VideoRestartCommand { get; }
    public RelayCommand BrowseLogoCommand { get; }
    public RelayCommand<string> ApplyParticlePresetCommand { get; }
    public RelayCommand<string> ApplyCountdownLabelCommand { get; }
    public RelayCommand<PresetItem> ApplyPresetCommand { get; }
    public RelayCommand SaveBrandKitCommand { get; }
    public RelayCommand LoadBrandKitCommand { get; }
    public RelayCommand ResetLayoutCommand { get; }
    public RelayCommand AddNdiSenderCommand { get; }
    public RelayCommand<NdiSenderConfig> RemoveNdiSenderCommand { get; }
    public RelayCommand AddCueCommand { get; }
    public RelayCommand<CueConfig> RemoveCueCommand { get; }
    public RelayCommand RefreshFeedCommand { get; }
    public RelayCommand AddLedTileCommand { get; }
    public RelayCommand RemoveLedTileCommand { get; }
    public RelayCommand ImportGridToMapCommand { get; }
    public RelayCommand ImportCueSheetCommand { get; }
    public RelayCommand ImportCueSheetAppendCommand { get; }
    public RelayCommand ExportCueSheetCommand { get; }
    public RelayCommand SaveCueTemplateCommand { get; }
    public RelayCommand PresenterNextCommand { get; }
    public RelayCommand PresenterPrevCommand { get; }
    public RelayCommand PresenterResetCommand { get; }
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
    public RelayCommand AddSerialDeviceCommand { get; }
    public RelayCommand AddIpDeviceCommand { get; }
    public RelayCommand<DeviceConfig> RemoveDeviceCommand { get; }
    public RelayCommand<DeviceConfig> TestDeviceCommand { get; }
    public RelayCommand<DeviceConfig> ResendDeviceCommand { get; }
    public RelayCommand<DeviceConfig> AddTriggerCommand { get; }
    public RelayCommand<DeviceTriggerConfig> RemoveTriggerCommand { get; }
    public RelayCommand AddMidiDeviceCommand { get; }

    /// <summary>Press a control on the surface and the row writes itself — the desk asks rather than assuming.</summary>
    public RelayCommand<DeviceConfig> LearnMidiCommand { get; }

    /// <summary>The published numbers for a known surface, as rows the operator can read and edit.</summary>
    public RelayCommand<DeviceConfig> SeedMidiCommand { get; }
    public RelayCommand AddProgrammeCommand { get; }
    public RelayCommand AddAdvertCommand { get; }
    public RelayCommand AddAnnouncementCommand { get; }
    public RelayCommand<ScheduleSlotConfig> RemoveSlotCommand { get; }
    public RelayCommand<ScheduleSlotConfig> PlaySlotCommand { get; }
    public RelayCommand EndInstallOverrideCommand { get; }
    public RelayCommand SupportBundleCommand { get; }
    public RelayCommand CheckInNowCommand { get; }
    public RelayCommand ApplyUpdateCommand { get; }
    public RelayCommand WalkNextCommand { get; }
    public RelayCommand WalkBackCommand { get; }
    public RelayCommand WalkRestartCommand { get; }
    public RelayCommand FreezeCommand { get; }
    public RelayCommand FadeToBlackCommand { get; }
    public RelayCommand FadeUpCommand { get; }
    public RelayCommand LookBackCommand { get; }
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

    /// <summary>A send consumes its targets — the next look starts from a clean strip.</summary>
    private void ClearSendTargets()
    {
        foreach (var tile in SwitcherTiles)
        {
            tile.IsSendTarget = false;
        }
    }


    internal static readonly FilePickerFileType AudioTypes = new("Audio")
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

    public string HeaderClock => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string CountdownPreview
    {
        get
        {
            var s = CountdownService.Evaluate(State.Countdown, _services.Clock.Now, _services.Clock.UtcNow);
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

    internal static readonly FilePickerFileType VideoTypes = new("Video & audio")
    {
        Patterns = Glob(PlaylistSequencer.VideoExtensions, PlaylistSequencer.AudioExtensions),
    };

    /// <summary>What a picture element can draw. No audio, no decks: offering what cannot be drawn is a trap.</summary>
    private static readonly FilePickerFileType PictureTypes = new("Pictures")
    {
        Patterns = Glob(PlaylistSequencer.ImageExtensions),
    };

    /// <summary>What a clip element can draw: a short video, or a still.</summary>
    private static readonly FilePickerFileType ClipOrStillTypes = new("Video & pictures")
    {
        Patterns = Glob(PlaylistSequencer.VideoExtensions, PlaylistSequencer.ImageExtensions),
    };

    internal static readonly FilePickerFileType MediaTypes = new("Images, video, audio & decks (PDF, PowerPoint)")
    {
        Patterns = Glob(PlaylistSequencer.ImageExtensions, PlaylistSequencer.VideoExtensions, PlaylistSequencer.AudioExtensions, PlaylistSequencer.DeckExtensions),
    };

    internal static readonly FilePickerFileType DeckTypes = new("Deck — PDF, PowerPoint, Keynote or Impress")
    {
        Patterns = Glob(PlaylistSequencer.DeckExtensions),
    };

    private static readonly FilePickerFileType ShowTypes = new("Patterns show")
    {
        Patterns = new[] { "*.patshow.json", "*.json" },
    };

    internal async Task PickFileAsync(string title, FilePickerFileType type, Action<string> assign)
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
        StatusMessage = await ImportCueSheetFromAsync(path, append);
    }

    /// <summary>The sheet read and parsed off the desk's thread — a big sheet on a share holds no click — then its rows applied once, as one edit.</summary>
    public async Task<string> ImportCueSheetFromAsync(string path, bool append)
    {
        var (table, problem) = await Task.Run(() => ReadSheet(path));
        if (table is null) return problem;
        return ApplySheet(table, path, append);
    }

    /// <summary>Reads a CSV or the first sheet of an .xlsx into the selected list on this thread; returns the words for the status line.</summary>
    public string ImportCueSheetFrom(string path, bool append)
    {
        var (table, problem) = ReadSheet(path);
        return table is null ? problem : ApplySheet(table, path, append);
    }

    /// <summary>The file read and parsed — on whatever thread calls, so the command calls from a worker — with the parse timed into the files budget.</summary>
    private (TableData? Table, string Problem) ReadSheet(string path)
    {
        var at = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var table = path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? XlsxTable.Read(File.ReadAllBytes(path))
                : CsvTable.Parse(File.ReadAllText(path));
            _services.Files.Record(FileBudget.CueSheetParse, System.Diagnostics.Stopwatch.GetElapsedTime(at).TotalMilliseconds, onDeskThread: Avalonia.Threading.Dispatcher.UIThread.CheckAccess());
            return (table, "");
        }
        catch (Exception ex)
        {
            Log.Error("Cue sheet read failed.", ex);
            return (null, $"Could not read {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private string ApplySheet(TableData table, string path, bool append)
    {
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
            await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false)); // the text carries its own BOM for Excel
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
            StatusMessage = await SaveShowToAsync(path);
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
        StatusMessage = await LoadShowFromAsync(path);
    }

    /// <summary>
    /// A show file read and parsed on a worker — a big show on a share holds no click, no frame —
    /// then applied once on the desk as one edit; the parse timed into the files budget. Returns
    /// the words for the status line.
    /// </summary>
    public async Task<string> LoadShowFromAsync(string path)
    {
        var store = _services.Store;
        var files = _services.Files;
        var loaded = await Task.Run(() =>
        {
            var at = System.Diagnostics.Stopwatch.GetTimestamp();
            var state = store.LoadFrom(path);
            files.Record(FileBudget.ShowLoadParse, System.Diagnostics.Stopwatch.GetElapsedTime(at).TotalMilliseconds);
            return state;
        });
        if (loaded is null) return "Show file could not be read.";
        ApplyLoadedShow(loaded, $"Show loaded: {Path.GetFileName(path)}");
        return StatusMessage;
    }

    /// <summary>A show saved where the operator said: serialised on the desk's thread, where the model is consistent, and written by a worker. Returns the words for the status line.</summary>
    public async Task<string> SaveShowToAsync(string path)
    {
        var store = _services.Store;
        var files = _services.Files;
        var at = System.Diagnostics.Stopwatch.GetTimestamp();
        var json = JsonUtil.Serialize(State);
        files.Record(FileBudget.ShowSaveSerialise, System.Diagnostics.Stopwatch.GetElapsedTime(at).TotalMilliseconds, onDeskThread: true);
        try
        {
            await Task.Run(() =>
            {
                var t = System.Diagnostics.Stopwatch.GetTimestamp();
                store.SaveJsonTo(path, json);
                files.Record(FileBudget.ShowSaveWrite, System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            });
            return $"Show saved: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Log.Error("Show save failed.", ex);
            return $"Show save failed: {ex.Message}";
        }
    }

    /// <summary>A show read from a file becomes the show: the model copied over, every list started over, the desk refreshed.</summary>
    internal void ApplyLoadedShow(ShowState loaded, string status)
    {
        _services.BulkEdit(() => ModelCopier.Copy(loaded, State));
        RefreshAfterShowReplaced();
        StatusMessage = status;
    }

    /// <summary>
    /// Round 72: the operator's session on the last show is the last show's. A show opens as a show
    /// opens — nothing pending, every target armed, the programme focused — so nothing the last
    /// session left behind runs against, or lands on, the show that replaced it:
    ///   the one-shot (a TAKE NEXT set for the last show's take),
    ///   a sting's ticket (a TAKE waiting under a clip — the clip and its saved pictures are the last show's),
    ///   the ticks on the wall tiles (a tile rebuild carries them; a replaced show does not),
    ///   the focus (the panes and CUT / TAKE FOCUSED mean the programme again),
    ///   the arming (runtime only, back to everything armed, as a show opens),
    ///   the edit watchers (they watched the last show's staged copies; the copies went with its state — the
    ///   loaded show's own pictures stand as its file says, nothing is staged),
    ///   and the programme the audience has: with EDIT SAFE open the frozen programme was the last show's — a
    ///   discard would have put the last show's pictures into the loaded one — so the loaded show is frozen
    ///   as the programme too, and the preview is the same show.
    /// </summary>
    private void ResetSession()
    {
        _services.NextTake.Set(null);
        _services.Stingers.ForgetSession();
        ClearSendTargets();
        _editWatches.Clear();
        _services.Arming.ArmAll();
        if (EditTargets.Count > 0) EditTarget = EditTargets[0];      // the programme is the editing target, as a show opens
        SelectTarget(null);
        if (_services.Sandbox.Active) _services.Sandbox.RestoreProgram(JsonUtil.Clone(State));
    }

    /// <summary>The model under the desk is another show now — loaded from a file, restored from a version, mirrored from a twin: every list starts over and every hook is re-tied.</summary>
    private void RefreshAfterShowReplaced()
    {
        Raise(nameof(HasLowerThirds));
        // A different show: nothing the last one left waiting may run against it.
        _services.Tail.DropAll();
        ResetSession();
        HookTransition();
        HookMonitor();
        RefreshWallDestinations();   // another show, another set of walls and outputs
        _services.Cues.Reset(); // every list starts over, disarmed
        Cues.OnShowLoaded();
        Music.OnShowLoaded();
        Audio.OnShowLoaded();
        ReconcilePlacements();
        BuildLibrary();
    }
}
