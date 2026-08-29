using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Particles;
using Patterns.Core.Rendering;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>Row shown in the Outputs screen list.</summary>
public sealed class ScreenRow : Observable
{
    private readonly MainViewModel _vm;
    private bool _isSelected;

    public ScreenRow(MainViewModel vm, ScreenInfo info, bool selected)
    {
        _vm = vm;
        Info = info;
        _isSelected = selected;
    }

    public ScreenInfo Info { get; }
    public string Title => $"{Info.Index + 1} · {Info.Label}";
    public string Detail => Info.Description;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) _vm.OnScreenSelectionChanged();
        }
    }
}

/// <summary>Target chooser entries for Independent mode ("Program" or one screen).</summary>
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
    private RatePreset? _selectedNdiRate;
    private int _selectedTileSize;
    private string _statusMessage = "";

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
        BrowseImageCommand = new RelayCommand(() => _ = PickFileAsync("Choose image", FilePickerFileTypes.ImageAll, p => ActivePattern.Media.ImagePath = p));
        BrowseVideoCommand = new RelayCommand(() => _ = PickFileAsync("Choose video", VideoTypes, p => ActivePattern.Media.VideoPath = p));
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

        _services.SnapshotPublished += OnSnapshotPublished;
        _services.Outputs.LiveChanged += RefreshOutputsStatus;
        _services.Screens.Changed += RebuildScreenRows;
        State.Output.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OutputConfig.Mode)) OnModeChanged();
        };

        var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statusTimer.Tick += (_, _) => PollStatus();
        statusTimer.Start();

        RebuildScreenRows();
        BuildLibrary();
        RefreshOutputsStatus();
    }

    public ShowState State => _services.State;
    public AppServices Services => _services;

    // ---- pattern editing target (Independent mode) --------------------------

    public ObservableCollection<EditTarget> EditTargets { get; } = new() { new EditTarget("Program", null) };

    public EditTarget EditTarget
    {
        get => _editTarget;
        set
        {
            if (Set(ref _editTarget, value))
            {
                Raise(nameof(ActivePattern));
                _services.PreviewScreenId = value?.ScreenId;
            }
        }
    }

    /// <summary>The pattern the editor panels bind to (program, or the selected screen's).</summary>
    public PatternConfig ActivePattern
    {
        get
        {
            if (State.Output.Mode == OutputMode.Independent && _editTarget.ScreenId is { } id)
            {
                var a = State.Independent.FirstOrDefault(x => x.ScreenId == id);
                if (a is not null) return a.Pattern;
            }
            return State.Pattern;
        }
    }

    public bool IsIndependent => State.Output.Mode == OutputMode.Independent;

    /// <summary>Called from the Outputs view when the mode combo changes.</summary>
    public void OnModeChanged()
    {
        EnsureIndependentAssignments();
        RebuildEditTargets();
        Raise(nameof(IsIndependent));
        Raise(nameof(ActivePattern));
    }

    private void EnsureIndependentAssignments()
    {
        if (State.Output.Mode != OutputMode.Independent) return;
        var screens = _services.Screens.Resolve(State.Output.SelectedScreenIds);
        foreach (var s in screens)
        {
            if (State.Independent.All(a => a.ScreenId != s.Id))
            {
                var assignment = new OutputAssignment { ScreenId = s.Id };
                ModelCopier.Copy(State.Pattern, assignment.Pattern);
                State.Independent.Add(assignment);
            }
        }
    }

    private void RebuildEditTargets()
    {
        EditTargets.Clear();
        EditTargets.Add(new EditTarget("Program", null));
        if (State.Output.Mode == OutputMode.Independent)
        {
            foreach (var s in _services.Screens.Resolve(State.Output.SelectedScreenIds))
            {
                EditTargets.Add(new EditTarget($"Screen {s.Index + 1} — {s.Label}", s.Id));
            }
        }
        EditTarget = EditTargets[0];
    }

    // ---- screens ------------------------------------------------------------

    public ObservableCollection<ScreenRow> ScreenRows { get; } = new();

    private void RebuildScreenRows()
    {
        var selected = State.Output.SelectedScreenIds.ToHashSet();
        ScreenRows.Clear();
        foreach (var s in _services.Screens.All)
        {
            ScreenRows.Add(new ScreenRow(this, s, selected.Count == 0 || selected.Contains(s.Id)));
        }
        RebuildEditTargets();
        RefreshOutputsStatus();
    }

    internal void OnScreenSelectionChanged()
    {
        var all = ScreenRows.Count > 0 && ScreenRows.All(r => r.IsSelected);
        State.Output.SelectedScreenIds.Clear();
        if (!all)
        {
            foreach (var r in ScreenRows.Where(r => r.IsSelected))
            {
                State.Output.SelectedScreenIds.Add(r.Info.Id);
            }
        }
        EnsureIndependentAssignments();
        RebuildEditTargets();
    }

    // ---- lists for the views ------------------------------------------------

    public EnumItem[] PatternKinds => Lists.PatternKinds;
    public EnumItem[] OutputModes => Lists.OutputModes;
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
    public RatePreset[] NdiRates => Lists.NdiRates;
    public string[] CountdownLabels => Lists.CountdownLabels;
    public string[] ParticlePresetNames => ParticlePresets.Names;

    public ResolutionPreset? SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            if (Set(ref _selectedResolution, value) && value is not null)
            {
                ActivePattern.Canvas.FollowOutput = false;
                ActivePattern.Canvas.Width = value.W;
                ActivePattern.Canvas.Height = value.H;
            }
        }
    }

    public RatePreset? SelectedNdiRate
    {
        get => _selectedNdiRate;
        set
        {
            if (Set(ref _selectedNdiRate, value) && value is not null)
            {
                State.Ndi.FrameRateN = value.N;
                State.Ndi.FrameRateD = value.D;
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
            var size = CanvasResolver.Resolve(p, new SkiaSharp.SKSizeI(1920, 1080));
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
        NdiStatus = State.Ndi.Enabled ? _services.Ndi.Status : NdiRuntimeFound ? "Off" : "Runtime not found";
        Raise(nameof(CanvasInfo));
        Raise(nameof(HeaderClock));
        Raise(nameof(CountdownPreview));
    }

    private void OnSnapshotPublished()
    {
        Raise(nameof(CanvasInfo));
        Raise(nameof(ShowCanvasPanel));
    }

    private void RefreshOutputsStatus()
    {
        var screens = _services.Screens.All.Count;
        OutputsStatus = _services.Outputs.IsLive
            ? $"LIVE — outputs on screens ({State.Output.Mode})"
            : $"{screens} screen{(screens == 1 ? "" : "s")} detected — press GO";
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
        foreach (var (name, path) in _services.Store.ListPresets())
        {
            var p = path;
            var item = new PresetItem
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
            };
            Library.Add(item);
        }

        _ = RenderThumbnailsAsync();
    }

    private async Task RenderThumbnailsAsync()
    {
        var baseState = JsonUtil.Clone(State);
        foreach (var item in Library.ToList())
        {
            var config = JsonUtil.ClonePattern(baseState.Pattern);
            var builtIn = BuiltInPresets.All.FirstOrDefault(b => b.Name == item.Name && b.Category == item.Category);
            if (builtIn is not null)
            {
                builtIn.Apply(config);
            }
            else
            {
                var stored = _services.Store.ListPresets().FirstOrDefault(x => x.Name == item.Name);
                if (stored.Path is null) continue;
                var loaded = _services.Store.LoadPreset(stored.Path);
                if (loaded is null) continue;
                config = loaded;
            }

            var bmp = await Task.Run(() => ThumbnailRenderer.Render(baseState, config));
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
        OnModeChanged();
        RebuildScreenRows();
        StatusMessage = $"Show loaded: {Path.GetFileName(path)}";
    }
}
