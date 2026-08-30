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
                result.Add(new ArrangedScreen(p.ScreenId, SKRectI.Create(p.X, p.Y, info.Bounds.Width, info.Bounds.Height)));
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
    }

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
                _services.PreviewScreenId = value.ScreenId;
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
                EditTargets.Add(new EditTarget($"Screen {info.Index + 1} — {info.Label}", p.ScreenId));
            }
        }
        EditTarget = EditTargets.FirstOrDefault(t => t.ScreenId == current) ?? EditTargets[0];
        Raise(nameof(ShowEditTargets));
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
        Raise(nameof(CanvasInfo));
        Raise(nameof(HeaderClock));
        Raise(nameof(CountdownPreview));
    }

    private void OnSnapshotPublished()
    {
        Raise(nameof(CanvasInfo));
        Raise(nameof(ShowCanvasPanel));
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

    private static readonly FilePickerFileType VideoTypes = new("Video")
    {
        Patterns = new[] { "*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.m4v", "*.mpg", "*.mpeg", "*.wmv" },
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
