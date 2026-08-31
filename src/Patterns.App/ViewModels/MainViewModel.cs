using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Particles;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.ViewModels;

/// <summary>Target chooser entries for the pattern editor ("Program" or one custom screen).</summary>
public sealed record EditTarget(string Label, string? ScreenId)
{
    public override string ToString() => Label;
}

public sealed class PresetItem : Observable
{
    private Bitmap? _thumbnail;

    public required string Category { get; init; }
    public required string Name { get; init; }
    public required Action Apply { get; init; }

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

        GoCommand = new RelayCommand(() => { _services.Outputs.Apply(); RefreshOutputsStatus(); });
        StopCommand = new RelayCommand(() => { _services.Outputs.CloseAll(); RefreshOutputsStatus(); });
        IdentifyCommand = new RelayCommand(_services.Identify);
        BlackoutCommand = new RelayCommand(() => State.Blackout = !State.Blackout);
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
            if (cfg is not null) State.Ndi.Senders.Remove(cfg);
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

        // Presenter click-through
        AddPresenterStepCommand = new RelayCommand(() =>
        {
            var name = SelectedPresenterLook;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = State.LooksAndCues.Looks.FirstOrDefault()?.Name ?? "";
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                StatusMessage = "Save a look first — presenter steps recall looks.";
                return;
            }
            State.Presenter.Steps.Add(new PresenterStepConfig { LookName = name });
            Raise(nameof(PresenterStepText));
        });
        RemovePresenterStepCommand = new RelayCommand<PresenterStepConfig>(step =>
        {
            if (step is not null) State.Presenter.Steps.Remove(step);
            Raise(nameof(PresenterStepText));
        });
        MovePresenterStepUpCommand = new RelayCommand<PresenterStepConfig>(step =>
        {
            if (step is not null) MovePresenterStep(step, -1);
        });
        MovePresenterStepDownCommand = new RelayCommand<PresenterStepConfig>(step =>
        {
            if (step is not null) MovePresenterStep(step, +1);
        });
        PresenterNextCommand = new RelayCommand(() => PresenterAdvance(+1));
        PresenterPrevCommand = new RelayCommand(() => PresenterAdvance(-1));
        PresenterResetCommand = new RelayCommand(() =>
        {
            State.Presenter.CurrentIndex = -1;
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
            State.AudioPlayer.Playing = true;
        });
        StopAudioCommand = new RelayCommand(() => State.AudioPlayer.Playing = false);
        RefreshAudioDevicesCommand = new RelayCommand(RefreshAudioDevices);
        ResetWarpCommand = new RelayCommand(() =>
        {
            if (_selectedPlacement is null) return;
            _selectedPlacement.WarpTlx = 0; _selectedPlacement.WarpTly = 0;
            _selectedPlacement.WarpTrx = 0; _selectedPlacement.WarpTry = 0;
            _selectedPlacement.WarpBlx = 0; _selectedPlacement.WarpBly = 0;
            _selectedPlacement.WarpBrx = 0; _selectedPlacement.WarpBry = 0;
            RaiseSelection();
        });

        // Stingers
        AddStingerFilesCommand = new RelayCommand(() => _ = AddStingerFilesAsync());
        RemoveStingerCommand = new RelayCommand<StingerItemConfig>(item =>
        {
            if (item is not null) State.Stingers.Items.Remove(item);
        });
        FireStingerCommand = new RelayCommand<StingerItemConfig>(item =>
        {
            if (item is null) return;
            _services.Stingers.Fire(item);
            StatusMessage = _services.Stingers.Status;
        });
        StopStingerCommand = new RelayCommand(() =>
        {
            _services.Stingers.Stop();
            StatusMessage = _services.Stingers.Status;
        });

        // Streaming
        while (State.Stream.Destinations.Count < 2)
        {
            State.Stream.Destinations.Add(new StreamDestinationConfig());
        }
        StartStreamCommand = new RelayCommand(() =>
        {
            State.Stream.Active = true;
            StatusMessage = "Stream starting…";
        });
        StopStreamCommand = new RelayCommand(() =>
        {
            State.Stream.Active = false;
            StatusMessage = "Stream stopped.";
        });

        // Multiview
        AddMultiviewTileCommand = new RelayCommand(() =>
            ActivePattern.Multiview.Tiles.Add(new MultiviewTileConfig()));
        RemoveMultiviewTileCommand = new RelayCommand<MultiviewTileConfig>(tile =>
        {
            if (tile is not null) ActivePattern.Multiview.Tiles.Remove(tile);
        });

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
        TakeCommand = new RelayCommand(() => SendAllFromSandbox(cut: false));
        CutCommand = new RelayCommand(() => SendAllFromSandbox(cut: true));
        SandboxSendAllCommand = new RelayCommand(() => SendAllFromSandbox(cut: false));
        SandboxSendSelectedCommand = new RelayCommand(() =>
        {
            if (!_services.Sandbox.Active) return;
            var picked = SwitcherTiles.Where(t => t.IsSendTarget && !t.IsProgramTile).ToList();
            if (picked.Count == 0)
            {
                StatusMessage = "Tick the tiles to send to first.";
                return;
            }
            var ids = picked.SelectMany(t => t.MemberIds).Distinct().ToList();
            var inCanvas = CanvasGroups().SelectMany(g => g).Select(p => p.ScreenId).ToHashSet();
            var loneInCanvas = picked
                .Where(t => t.MemberIds.Count == 1 && inCanvas.Contains(t.MemberIds[0]))
                .Select(t => t.Title).ToList();
            _services.Sandbox.SendToScreens(ids);
            Raise(nameof(IsSandboxActive));
            StatusMessage = $"Sandbox sent to {string.Join(", ", picked.Select(t => t.Title))} as their own pattern." +
                            (loneInCanvas.Count > 0
                                ? " Note: screens inside a joined canvas keep showing the canvas content — split them apart to see their own look."
                                : "");
        });
        SelectTileCommand = new RelayCommand<SwitcherTile>(tile =>
        {
            if (tile is null) return;
            EditTarget = EditTargets.FirstOrDefault(t => t.ScreenId == tile.EditScreenId) ?? EditTargets[0];
            StatusMessage = EditTargetBanner;
        });

        // Looks & cues
        SaveLookCommand = new RelayCommand(SaveLook);
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
            if (look is not null) State.LooksAndCues.Looks.Remove(look);
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
        ReconcilePlacements();
        RefreshOutputsStatus();
    }

    public void ReconcilePlacements() => ReconcilePlacements(_services.Screens.All.ToList());

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
                    Enabled = !(screen.IsPrimary && screens.Count > 1),
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
                p.Enabled = !(info.IsPrimary && screens.Count > 1);
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
                result.Add(new ArrangedScreen(p.ScreenId, SKRectI.Create(p.X, p.Y, size.Width, size.Height)));
            }
        }
        return result;
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
                _services.PreviewScreenId = value.ScreenId;
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
    }

    private void EnsureAssignment(string screenId)
    {
        if (State.Independent.All(a => a.ScreenId != screenId))
        {
            var assignment = new OutputAssignment { ScreenId = screenId };
            ModelCopier.Copy(State.Pattern, assignment.Pattern);
            State.Independent.Add(assignment);
        }
    }

    private void RebuildEditTargets()
    {
        var current = _editTarget?.ScreenId;
        EditTargets.Clear();
        EditTargets.Add(new EditTarget("Program", null));
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
        State.MediaLibrary.Add(new MediaLibraryEntry { Path = path, IsVideo = isVideo });
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
                Title = "Add stingers (sounds or video clips)",
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
            if (skipped > 0) StatusMessage = $"Stingers are sounds or video clips — {skipped} other file{(skipped == 1 ? "" : "s")} skipped.";
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
        foreach (var s in _services.Screens.All)
        {
            WebScreens.Add(new EditTarget($"Screen {s.Index + 1} — {s.Label}", s.Id));
        }
        State.Web.TargetScreenId = current;

        MultiviewScreens.Clear();
        foreach (var x in OrderedLivePlacements())
        {
            MultiviewScreens.Add(new EditTarget($"{MultiviewScreens.Count + 1} — {LabelFor(x.Placement, x.Info)}", x.Placement.ScreenId));
        }
    }

    // ---- multiview ----------------------------------------------------------

    /// <summary>Screen choices for multiview tiles (arrangement order, custom labels).</summary>
    public ObservableCollection<EditTarget> MultiviewScreens { get; } = new();

    // ---- presenter click-through -------------------------------------------

    /// <summary>Advances the presenter steps and applies the step's look. False = no move.</summary>
    public bool PresenterAdvance(int delta)
    {
        var p = State.Presenter;
        if (PresenterLogic.Advance(p.CurrentIndex, p.Steps.Count, delta, p.Loop) is not { } idx) return false;
        var step = p.Steps[idx];
        p.CurrentIndex = idx;
        var look = State.LooksAndCues.Looks.FirstOrDefault(
            l => string.Equals(l.Name, step.LookName, StringComparison.OrdinalIgnoreCase));
        if (look is null)
        {
            StatusMessage = $"Presenter step {idx + 1}: look '{step.LookName}' not found.";
            return false;
        }
        ApplyLook(look);
        StatusMessage = $"Presenter {idx + 1}/{p.Steps.Count}: {(step.Label.Length > 0 ? step.Label : look.Name)}";
        Raise(nameof(PresenterStepText));
        return true;
    }

    public string PresenterStepText
    {
        get
        {
            var p = State.Presenter;
            if (p.Steps.Count == 0) return "No presenter steps yet.";
            return p.CurrentIndex < 0
                ? $"Ready — {p.Steps.Count} step{(p.Steps.Count == 1 ? "" : "s")}, click to start."
                : $"Step {p.CurrentIndex + 1} of {p.Steps.Count}";
        }
    }

    /// <summary>Moves a presenter step (drag/arrows in the list).</summary>
    public void MovePresenterStep(PresenterStepConfig step, int delta)
    {
        var steps = State.Presenter.Steps;
        var index = steps.IndexOf(step);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= steps.Count) return;
        steps.Move(index, target);
    }

    // ---- remote screen/group switching --------------------------------------

    private List<(ScreenPlacement Placement, ScreenInfo Info)> OrderedLivePlacements(IReadOnlyList<ScreenInfo>? screens = null)
    {
        var known = screens ?? _services.Screens.All;
        return State.Output.Placements
            .Select(p => (Placement: p, Info: known.FirstOrDefault(s => s.Id == p.ScreenId)))
            .Where(x => x.Info is not null)
            .Select(x => (x.Placement, Info: x.Info!))
            .OrderBy(x => x.Placement.X).ThenBy(x => x.Placement.Y)
            .ToList();
    }

    /// <summary>Remote: screen by its overview number → enabled/disabled/toggled.</summary>
    public bool SetScreenEnabled(int number, bool? target, IReadOnlyList<ScreenInfo>? screens = null)
    {
        var ordered = OrderedLivePlacements(screens);
        if (number < 1 || number > ordered.Count) return false;
        var placement = ordered[number - 1].Placement;
        placement.Enabled = target ?? !placement.Enabled;
        placement.UserPinned = true;
        return true;
    }

    /// <summary>Joined-canvas letters (A, B, …) → their member placements, arrangement order.</summary>
    private List<List<ScreenPlacement>> CanvasGroups(IReadOnlyList<ScreenInfo>? screens = null)
    {
        var live = OrderedLivePlacements(screens);
        var arranged = live
            .Select(x => new ArrangedScreen(x.Placement.ScreenId,
                SkiaSharp.SKRectI.Create(x.Placement.X, x.Placement.Y,
                    OutputWindowManager.EffectiveSize(x.Placement, x.Info).Width,
                    OutputWindowManager.EffectiveSize(x.Placement, x.Info).Height)))
            .ToList();
        var byId = live.ToDictionary(x => x.Placement.ScreenId, x => x.Placement);
        return ScreenLayout.Groups(arranged)
            .Where(g => g.Count > 1)
            .OrderBy(g => ScreenLayout.Union(g).Left).ThenBy(g => ScreenLayout.Union(g).Top)
            .Select(g => g.Select(m => byId[m.Id]).ToList())
            .ToList();
    }

    /// <summary>Remote: every screen of canvas 'A'/'B'… on or off at once.</summary>
    public bool SetGroupEnabled(string letter, bool enabled, IReadOnlyList<ScreenInfo>? screens = null)
    {
        if (letter.Length != 1) return false;
        var groups = CanvasGroups(screens);
        var index = letter[0] - 'A';
        if (index < 0 || index >= groups.Count) return false;
        foreach (var placement in groups[index])
        {
            placement.Enabled = enabled;
            placement.UserPinned = true;
        }
        return true;
    }

    /// <summary>Screen rows for the remote-state JSON. UI thread.</summary>
    public object[] RemoteScreens(IReadOnlyList<ScreenInfo>? screens = null)
    {
        var groups = CanvasGroups(screens);
        string? LetterOf(ScreenPlacement p)
        {
            for (var i = 0; i < groups.Count; i++)
            {
                if (groups[i].Contains(p)) return ((char)('A' + i)).ToString();
            }
            return null;
        }

        return OrderedLivePlacements(screens)
            .Select((x, i) => (object)new
            {
                n = i + 1,
                label = LabelFor(x.Placement, x.Info),
                enabled = x.Placement.Enabled,
                group = LetterOf(x.Placement),
            })
            .ToArray();
    }

    // ---- switcher (program / preview, strip, sandbox) -----------------------

    public ObservableCollection<SwitcherTile> SwitcherTiles { get; } = new();

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
                foreach (var tile in SwitcherTiles)
                {
                    tile.IsSendTarget = false; // fresh session, fresh targets
                }
                StatusMessage = "SANDBOX — build the look here; outputs keep showing the program.";
            }
            else
            {
                _services.Sandbox.Discard();
                StatusMessage = "Sandbox discarded — outputs untouched.";
            }
            Raise(nameof(IsSandboxActive));
        }
    }

    /// <summary>The label a screen shows everywhere: the operator's name, or the OS one.</summary>
    public string LabelFor(ScreenPlacement placement, ScreenInfo? info = null)
        => placement.CustomLabel.Length > 0
            ? placement.CustomLabel
            : (info ?? LiveInfo(placement))?.Label ?? placement.ScreenId;

    /// <summary>The stored (or automatic) name of the canvas containing a set of members.</summary>
    public string CanvasNameFor(IReadOnlyList<ScreenPlacement> members, string letter)
    {
        var key = CanvasNameConfig.KeyFor(members.Select(m => m.ScreenId));
        var stored = State.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == key)?.Name;
        return string.IsNullOrWhiteSpace(stored) ? $"Canvas {letter}" : stored!;
    }

    /// <summary>Rebuilds the strip: PGM tile, then joined canvases, then single screens.</summary>
    public void RebuildSwitcherTiles(IReadOnlyList<ScreenInfo>? screens = null)
    {
        var keepTargets = SwitcherTiles.Where(t => t.IsSendTarget)
            .SelectMany(t => t.MemberIds).ToHashSet();
        SwitcherTiles.Clear();

        SwitcherTiles.Add(new SwitcherTile(this, "PGM", null, Array.Empty<string>(),
            enabled: true, isEditTarget: EditTarget.ScreenId is null));

        var ordered = OrderedLivePlacements(screens);
        var numberOf = ordered.Select((x, i) => (x.Placement.ScreenId, N: i + 1))
            .ToDictionary(x => x.ScreenId, x => x.N);
        var groups = CanvasGroups(screens);
        var grouped = groups.SelectMany(g => g).Select(p => p.ScreenId).ToHashSet();

        for (var i = 0; i < groups.Count; i++)
        {
            var letter = ((char)('A' + i)).ToString();
            var members = groups[i];
            var tile = new SwitcherTile(this,
                $"{letter} · {CanvasNameFor(members, letter)}",
                null,
                members.Select(m => m.ScreenId).ToList(),
                members.All(m => m.Enabled),
                isEditTarget: false)
            {
                IsSendTarget = members.Any(m => keepTargets.Contains(m.ScreenId)),
            };
            SwitcherTiles.Add(tile);
        }

        foreach (var (placement, info) in ordered)
        {
            if (grouped.Contains(placement.ScreenId)) continue;
            var tile = new SwitcherTile(this,
                $"{numberOf[placement.ScreenId]} · {LabelFor(placement, info)}",
                placement.UseCustomPattern ? placement.ScreenId : null,
                new[] { placement.ScreenId },
                placement.Enabled,
                isEditTarget: EditTarget.ScreenId is not null && EditTarget.ScreenId == placement.ScreenId)
            {
                IsSendTarget = keepTargets.Contains(placement.ScreenId),
            };
            SwitcherTiles.Add(tile);
        }
        Raise(nameof(EditTargetBanner));
    }

    /// <summary>Live refresh without rebuilding (keeps ticks and focus; called each poll).</summary>
    private void RefreshSwitcherTiles()
    {
        var byId = State.Output.Placements.ToDictionary(p => p.ScreenId);
        foreach (var tile in SwitcherTiles)
        {
            if (tile.IsProgramTile)
            {
                tile.RefreshExternal(true, EditTarget.ScreenId is null);
                continue;
            }
            var members = tile.MemberIds.Select(id => byId.GetValueOrDefault(id)).Where(p => p is not null).ToList();
            var enabled = members.Count > 0 && members.All(p => p!.Enabled);
            var isTarget = tile.EditScreenId is not null && tile.EditScreenId == EditTarget.ScreenId;
            tile.RefreshExternal(enabled, isTarget);
        }
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
    }

    /// <summary>Big banner over the editor: what the panels currently change.</summary>
    public string EditTargetBanner
    {
        get
        {
            if (_editTarget.ScreenId is null) return "EDITING: PROGRAM — every screen without its own pattern";
            var placement = State.Output.Placements.FirstOrDefault(p => p.ScreenId == _editTarget.ScreenId);
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

    private string _remoteStatus = "";
    public string RemoteStatus { get => _remoteStatus; private set => Set(ref _remoteStatus, value); }

    private string _stingerStatus = "Ready.";
    public string StingerStatus { get => _stingerStatus; private set => Set(ref _stingerStatus, value); }

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
        var existing = State.LooksAndCues.Looks.FirstOrDefault(l => l.Name == name);
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

    public void ApplyLook(LookConfig look)
    {
        var ok = false;
        _services.BulkEdit(() => ok = LookService.Apply(look.Json, State));
        if (ok)
        {
            RebuildEditTargets();
            Raise(nameof(ActivePattern));
            StatusMessage = $"Look '{look.Name}' applied.";
        }
        else
        {
            StatusMessage = $"Look '{look.Name}' could not be applied.";
        }
    }

    /// <summary>F1–F12 from the main window or an output window. False = no look on that key.</summary>
    public bool ApplyLookHotkey(int slot)
    {
        var look = State.LooksAndCues.Looks.FirstOrDefault(l => l.Hotkey == slot);
        if (look is null) return false;
        ApplyLook(look);
        return true;
    }

    private void CheckCues()
    {
        if (_services.Sandbox.Active)
        {
            // A cue firing now would land in the sandbox, not on the outputs — hold them.
            NextCueText = "Cues held while the sandbox is open.";
            return;
        }

        var now = DateTime.Now;
        foreach (var cue in State.LooksAndCues.Cues)
        {
            if (!LookService.ShouldFire(cue, now)) continue;
            cue.LastFiredDate = now.Date;
            var look = State.LooksAndCues.Looks.FirstOrDefault(l => l.Name == cue.LookName);
            if (look is not null)
            {
                ApplyLook(look);
                StatusMessage = $"Cue {cue.Time}: look '{look.Name}' applied.";
                Log.Info(StatusMessage);
            }
        }

        var next = LookService.NextCue(State.LooksAndCues.Cues, now);
        NextCueText = next is { } n
            ? $"Next cue: '{n.Cue.LookName}' at {n.At:HH:mm}{(n.At.Date != now.Date ? " tomorrow" : "")}"
            : "No cues scheduled.";
    }

    // ---- audio / fonts / feed / LED map ------------------------------------

    public EnumItem[] ToneModes => Lists.ToneModes;
    public EnumItem[] ToneChannelsList => Lists.ToneChannelsList;
    public EnumItem[] FeedKinds => Lists.FeedKinds;
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
        NdiSources.Clear();
        NdiSources.Add(new EditTarget("Program", ""));
        foreach (var s in _services.Screens.All)
        {
            NdiSources.Add(new EditTarget($"Screen {s.Index + 1} — {s.Label}", s.Id));
        }
    }

    private void AddNdiSender()
    {
        var n = State.Ndi.Senders.Count + 1;
        State.Ndi.Senders.Add(new NdiSenderConfig
        {
            Name = n == 1 ? "Patterns" : $"Patterns {n}",
            Enabled = false,
        });
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
    public RelayCommand AddPresenterStepCommand { get; }
    public RelayCommand<PresenterStepConfig> RemovePresenterStepCommand { get; }
    public RelayCommand<PresenterStepConfig> MovePresenterStepUpCommand { get; }
    public RelayCommand<PresenterStepConfig> MovePresenterStepDownCommand { get; }
    public RelayCommand PresenterNextCommand { get; }
    public RelayCommand PresenterPrevCommand { get; }
    public RelayCommand PresenterResetCommand { get; }
    public RelayCommand BrowseAudioTrackCommand { get; }
    public RelayCommand PlayAudioCommand { get; }
    public RelayCommand StopAudioCommand { get; }
    public RelayCommand RefreshAudioDevicesCommand { get; }
    public RelayCommand ResetWarpCommand { get; }
    public RelayCommand AddStingerFilesCommand { get; }
    public RelayCommand<StingerItemConfig> RemoveStingerCommand { get; }
    public RelayCommand<StingerItemConfig> FireStingerCommand { get; }
    public RelayCommand StopStingerCommand { get; }
    public RelayCommand SandboxSendAllCommand { get; }
    public RelayCommand SandboxSendSelectedCommand { get; }
    public RelayCommand TakeCommand { get; }
    public RelayCommand CutCommand { get; }
    public RelayCommand<SwitcherTile> SelectTileCommand { get; }
    public RelayCommand AddMultiviewTileCommand { get; }
    public RelayCommand<MultiviewTileConfig> RemoveMultiviewTileCommand { get; }
    public RelayCommand StartStreamCommand { get; }
    public RelayCommand StopStreamCommand { get; }
    public RelayCommand RestartAppCommand { get; }
    public RelayCommand OpenAppFolderCommand { get; }

    /// <summary>TAKE (crossfade) / CUT (instant) — the sandbox becomes the program.</summary>
    private void SendAllFromSandbox(bool cut)
    {
        if (!_services.Sandbox.Active)
        {
            StatusMessage = "Open EDIT (sandbox) first — build the look, then CUT or TAKE it to air.";
            return;
        }
        _services.Sandbox.SendAll(cut);
        Raise(nameof(IsSandboxActive));
        StatusMessage = cut ? "CUT — sandbox is now the program on every screen." : "TAKE — sandbox faded up on every screen.";
    }

    private string _selectedPresenterLook = "";
    public string SelectedPresenterLook { get => _selectedPresenterLook; set => Set(ref _selectedPresenterLook, value); }

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
        Log.Info("Restart requested from the Admin tab.");
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
        StingerStatus = _services.Stingers.Status;
        HealthText = HealthMonitor.Summary(DateTime.UtcNow);
        StreamStatus = _services.Stream.Status;
        _statusTicks++;
        PollAdmin();
        RefreshSwitcherTiles();
        RemoteStatus = State.Control.Enabled
            ? $"Remote: {_services.Control.RemoteUrls().Skip(1).FirstOrDefault() ?? _services.Control.RemoteUrls()[0]}"
            : "Remote control off.";
        _services.Video.SweepRetired();
        _services.NdiIn.SweepRetired();
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
        Raise(nameof(CanvasInfo));
        Raise(nameof(HeaderClock));
        Raise(nameof(CountdownPreview));
    }

    private void OnSnapshotPublished()
    {
        Raise(nameof(CanvasInfo));
        Raise(nameof(ShowCanvasPanel));
        Raise(nameof(InputNickname));
        RaiseArrangement();
    }

    private void RefreshOutputsStatus()
    {
        var screens = _services.Screens.All.Count;
        var enabled = State.Output.Placements.Count(p => p.Enabled && LiveInfo(p) is not null);
        OutputsStatus = _services.Outputs.IsLive
            ? "LIVE — outputs running"
            : $"{screens} screen{(screens == 1 ? "" : "s")} detected · {enabled} enabled — press GO";
        Raise(nameof(IsLive));
    }

    public bool IsLive => _services.Outputs.IsLive;

    // ---- library ------------------------------------------------------------

    public ObservableCollection<PresetItem> Library { get; } = new();

    private void BuildLibrary()
    {
        Library.Clear();
        foreach (var b in BuiltInPresets.All)
        {
            var preset = b;
            var item = new PresetItem
            {
                Category = preset.Category,
                Name = preset.Name,
                Apply = () => _services.BulkEdit(() => preset.Apply(ActivePattern)),
            };
            Library.Add(item);
        }

        foreach (var media in State.MediaLibrary.ToList())
        {
            var entry = media;
            Library.Add(new PresetItem
            {
                Category = "My media",
                Name = Path.GetFileName(entry.Path),
                Apply = () => _services.BulkEdit(() =>
                {
                    ActivePattern.Kind = PatternKind.Media;
                    if (entry.IsVideo)
                    {
                        ActivePattern.Media.Source = MediaSource.Video;
                        ActivePattern.Media.VideoPath = entry.Path;
                    }
                    else
                    {
                        ActivePattern.Media.Source = MediaSource.Image;
                        ActivePattern.Media.ImagePath = entry.Path;
                    }
                }),
            });
        }

        foreach (var (name, path) in _services.Store.ListPresets())
        {
            var p = path;
            Library.Add(new PresetItem
            {
                Category = "My presets",
                Name = name,
                Apply = () =>
                {
                    var cfg = _services.Store.LoadPreset(p);
                    if (cfg is not null)
                    {
                        _services.BulkEdit(() => ModelCopier.Copy(cfg, ActivePattern));
                    }
                },
            });
        }

        _ = RenderThumbnailsAsync();
    }

    private async Task RenderThumbnailsAsync()
    {
        var baseState = JsonUtil.Clone(State);
        foreach (var item in Library.ToList())
        {
            PatternConfig? config = null;
            var builtIn = BuiltInPresets.All.FirstOrDefault(b => b.Name == item.Name && b.Category == item.Category);
            if (builtIn is not null)
            {
                config = JsonUtil.ClonePattern(baseState.Pattern);
                builtIn.Apply(config);
            }
            else if (item.Category == "My media")
            {
                var entry = State.MediaLibrary.FirstOrDefault(m => Path.GetFileName(m.Path) == item.Name);
                if (entry is null) continue;
                config = JsonUtil.ClonePattern(baseState.Pattern);
                config.Kind = PatternKind.Media;
                config.Media.Source = entry.IsVideo ? MediaSource.Video : MediaSource.Image;
                config.Media.ImagePath = entry.IsVideo ? "" : entry.Path;
                config.Media.VideoPath = entry.IsVideo ? entry.Path : "";
            }
            else
            {
                var stored = _services.Store.ListPresets().FirstOrDefault(x => x.Name == item.Name);
                if (stored.Path is null) continue;
                config = _services.Store.LoadPreset(stored.Path);
            }

            if (config is null) continue;
            var cfg = config;
            var bmp = await Task.Run(() => ThumbnailRenderer.Render(baseState, cfg));
            if (bmp is not null) item.Thumbnail = bmp;
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
        ReconcilePlacements();
        BuildLibrary();
        StatusMessage = $"Show loaded: {Path.GetFileName(path)}";
    }
}
