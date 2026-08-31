using System.Text.RegularExpressions;
using System.Windows.Input;
using Patterns.Core.Model;

namespace Patterns.App.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;

    public RelayCommand(Action<T?> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute(parameter is T t ? t : default);
}

/// <summary>One audio output device row with a live selection checkbox.</summary>
public sealed class AudioDeviceChoice : Patterns.Core.Model.Observable
{
    private readonly MainViewModel _vm;
    private bool _isSelected;

    public AudioDeviceChoice(MainViewModel vm, string name, bool selected, string? label = null)
    {
        _vm = vm;
        Name = name;
        Label = label ?? name;
        _isSelected = selected;
    }

    /// <summary>The stored key (a device's friendly name, or the computer-output sentinel).</summary>
    public string Name { get; }

    /// <summary>What the checkbox shows.</summary>
    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) _vm.AudioDeviceChanged(this);
        }
    }
}

/// <summary>
/// One tile in the switcher strip between the program and preview panes: the program
/// itself, a joined canvas, or a single screen — with its label, live on/off toggle,
/// edit-target highlight, and (while the sandbox is open) a send-target tick.
/// </summary>
public sealed class SwitcherTile : Patterns.Core.Model.Observable
{
    private readonly MainViewModel _vm;
    private bool _enabled;
    private bool _isEditTarget;
    private bool _isSendTarget;

    public SwitcherTile(MainViewModel vm, string title, string? editScreenId, IReadOnlyList<string> memberIds, bool enabled, bool isEditTarget)
    {
        _vm = vm;
        Title = title;
        EditScreenId = editScreenId;
        MemberIds = memberIds;
        _enabled = enabled;
        _isEditTarget = isEditTarget;
    }

    /// <summary>"PGM · Program", "A · Main wall" or "2 · Stage left".</summary>
    public string Title { get; }

    /// <summary>Screen this tile edits when clicked; null = the program.</summary>
    public string? EditScreenId { get; }

    /// <summary>Screens this tile stands for ("send selected" targets; empty for the program tile).</summary>
    public IReadOnlyList<string> MemberIds { get; }

    public bool IsProgramTile => MemberIds.Count == 0;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value)) _vm.SetTileEnabled(this, value);
        }
    }

    /// <summary>Highlight: the editor panels currently work on this tile's content.</summary>
    public bool IsEditTarget
    {
        get => _isEditTarget;
        set => Set(ref _isEditTarget, value);
    }

    /// <summary>Sandbox send target tick (visible only while the sandbox is open).</summary>
    public bool IsSendTarget
    {
        get => _isSendTarget;
        set => Set(ref _isSendTarget, value);
    }

    /// <summary>Refreshes live state without rebuilding the strip (keeps focus and ticks).</summary>
    public void RefreshExternal(bool enabled, bool isEditTarget)
    {
        if (_enabled != enabled)
        {
            _enabled = enabled;
            Raise(nameof(Enabled)); // no SetTileEnabled echo — this reflects, not commands
        }
        IsEditTarget = isEditTarget;
    }
}

/// <summary>A labelled enum value for combo boxes.</summary>
public sealed record EnumItem(object Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One advisor line on the Admin tab, with its severity colour resolved.</summary>
public sealed record SuggestionRow(string Title, string Detail, Avalonia.Media.IBrush Dot);

/// <summary>One detected graphics adapter row on the Admin tab.</summary>
public sealed record GpuRow(string Name, string Detail);

public sealed record ResolutionPreset(string Label, int W, int H)
{
    public override string ToString() => Label;
}

public static class Lists
{
    private static string Pretty(string name)
        => Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");

    public static EnumItem[] Of<T>() where T : struct, Enum
        => Enum.GetValues<T>().Select(v => new EnumItem(v, Pretty(v.ToString()))).ToArray();

    public static readonly EnumItem[] PatternKinds =
    {
        new(PatternKind.Grid, "Grid"),
        new(PatternKind.Checkerboard, "Checkerboard"),
        new(PatternKind.ColorBars, "Colour bars"),
        new(PatternKind.Ramp, "Ramps & steps"),
        new(PatternKind.Focus, "Focus"),
        new(PatternKind.Geometry, "Geometry & safe areas"),
        new(PatternKind.FlatField, "Flat field"),
        new(PatternKind.LedWall, "LED wall"),
        new(PatternKind.VideoWall, "Video wall"),
        new(PatternKind.ProjectionBlend, "Projection blend"),
        new(PatternKind.Motion, "Motion"),
        new(PatternKind.ColorCycle, "Colour cycle"),
        new(PatternKind.Media, "Media (image / video)"),
        new(PatternKind.Particles, "Particles"),
        new(PatternKind.Multiview, "Multiview (monitor wall)"),
    };

    public static readonly EnumItem[] MultiviewSources =
    {
        new(MultiviewSource.Program, "Program"),
        new(MultiviewSource.Screen, "A screen's content"),
        new(MultiviewSource.NdiFeed, "NDI feed (Media tab)"),
        new(MultiviewSource.Pip, "PiP input"),
        new(MultiviewSource.Clock, "Clock"),
    };

    public static readonly EnumItem[] GpuPreferences =
    {
        new(GpuPreferenceKind.BestPerformance, "Best performance (auto-detect)"),
        new(GpuPreferenceKind.PowerSaving, "Power saving (integrated)"),
        new(GpuPreferenceKind.Specific, "A specific adapter…"),
        new(GpuPreferenceKind.LetWindowsDecide, "Let Windows decide"),
    };

    public static readonly EnumItem[] Anchors = Of<Anchor9>();
    public static readonly EnumItem[] FitModes = Of<FitMode>();
    public static readonly EnumItem[] BarsVariants =
    {
        new(BarsVariant.Smpte, "SMPTE RP 219 style"),
        new(BarsVariant.Ebu100, "EBU 100% (8 bars)"),
        new(BarsVariant.Bars75, "75% bars"),
        new(BarsVariant.Bars100, "100% bars"),
    };
    public static readonly EnumItem[] RampVariants =
    {
        new(RampVariant.GrayHorizontal, "Grey ramp — horizontal"),
        new(RampVariant.GrayVertical, "Grey ramp — vertical"),
        new(RampVariant.Rgb, "RGB + grey ramps"),
        new(RampVariant.Steps, "Grey steps (banding)"),
    };
    public static readonly EnumItem[] MotionVariants =
    {
        new(MotionVariant.MovingBar, "Moving bar / judder"),
        new(MotionVariant.BouncingBox, "Bouncing box + FPS"),
        new(MotionVariant.FrameFlash, "Frame flash (drop check)"),
        new(MotionVariant.ZonePlate, "Zone plate (animated)"),
        new(MotionVariant.ScrollingGrid, "Scrolling grid"),
    };
    public static readonly EnumItem[] BlendCurves = Of<BlendCurve>();
    public static readonly EnumItem[] BlendOrientations = Of<BlendOrientation>();
    public static readonly EnumItem[] TileNumberings =
    {
        new(TileNumbering.RowCol, "Row-column (2-3)"),
        new(TileNumbering.Linear, "Linear (1,2,3…)"),
        new(TileNumbering.Serpentine, "Serpentine data run"),
    };
    public static readonly EnumItem[] MediaSources =
    {
        new(MediaSource.Image, "Image"),
        new(MediaSource.Video, "Video / audio file"),
        new(MediaSource.Playlist, "Playlist"),
        new(MediaSource.NdiFeed, "NDI feed (network)"),
        new(MediaSource.Capture, "Capture device (HDMI / SDI / webcam)"),
    };
    public static readonly EnumItem[] ParticleShapes = Of<ParticleShape>();
    public static readonly EnumItem[] ParticleEmitters =
    {
        new(ParticleEmitter.TopEdge, "Falling (top edge)"),
        new(ParticleEmitter.BottomEdge, "Rising (bottom edge)"),
        new(ParticleEmitter.Center, "Starfield (center)"),
        new(ParticleEmitter.FullArea, "Drifting (full area)"),
    };
    public static readonly EnumItem[] CountdownKinds =
    {
        new(CountdownTargetKind.TimeOfDay, "To a time of day"),
        new(CountdownTargetKind.Duration, "Run a duration"),
    };
    public static readonly EnumItem[] CountdownEnds =
    {
        new(CountdownEndBehavior.HoldZero, "Hold at 00:00"),
        new(CountdownEndBehavior.Flash, "Flash 00:00"),
        new(CountdownEndBehavior.Message, "Show message"),
    };
    public static readonly EnumItem[] ScaleModes =
    {
        new(CanvasScaleMode.Fit, "Fit output (letterbox)"),
        new(CanvasScaleMode.OneToOne, "1:1 pixels (centre)"),
    };

    public static readonly EnumItem[] Rotations =
    {
        new(OutputRotation.None, "Landscape (no rotation)"),
        new(OutputRotation.Rot90, "Portrait — rotate 90°"),
        new(OutputRotation.Rot270, "Portrait — rotate 270°"),
        new(OutputRotation.Rot180, "Upside down (180°)"),
    };

    public static readonly EnumItem[] ToneModes =
    {
        new(ToneMode.ChannelIdent, "Channel ident (L pip · R pip-pip)"),
        new(ToneMode.Continuous, "Continuous tone"),
    };

    public static readonly EnumItem[] ToneChannelsList =
    {
        new(ToneChannels.Both, "Left + Right"),
        new(ToneChannels.Left, "Left only"),
        new(ToneChannels.Right, "Right only"),
    };

    public static readonly EnumItem[] PipSources =
    {
        new(PipSource.NdiFeed, "NDI feed (network)"),
        new(PipSource.Capture, "Capture device (HDMI / SDI / webcam)"),
    };

    public static readonly EnumItem[] FeedKinds =
    {
        new(FeedKind.Auto, "Auto-detect"),
        new(FeedKind.Rss, "RSS / Atom"),
        new(FeedKind.Csv, "CSV / plain lines"),
        new(FeedKind.Ics, "ICS calendar"),
    };

    public static readonly ResolutionPreset[] Resolutions =
    {
        new("HD 720p — 1280×720", 1280, 720),
        new("HD 1080p — 1920×1080", 1920, 1080),
        new("2K DCI — 2048×1080", 2048, 1080),
        new("QHD — 2560×1440", 2560, 1440),
        new("UW QHD — 3440×1440", 3440, 1440),
        new("UHD 4K — 3840×2160", 3840, 2160),
        new("4K DCI — 4096×2160", 4096, 2160),
        new("WUXGA — 1920×1200", 1920, 1200),
        new("Portrait 1080 — 1080×1920", 1080, 1920),
        new("Square — 1080×1080", 1080, 1080),
    };

    public static readonly int[] TileSizes = { 64, 96, 104, 128, 160, 168, 176, 192, 200, 256 };

    public static readonly string[] CountdownLabels =
    {
        "BACK FROM LUNCH AT", "DINNER BREAK — BACK IN", "REHEARSAL RESUMES IN",
        "DOORS OPEN IN", "SHOW STARTS IN", "STARTING SOON",
    };
}
