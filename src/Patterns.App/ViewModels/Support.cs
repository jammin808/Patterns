using System.Text.RegularExpressions;
using System.Windows.Input;
using Patterns.Core.Model;

using Patterns.Core.LowerThirds;

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

    /// <summary>The lip-sync offset of this output, ms (Audio page slider): the track, VOGs and stingers on it leave this much later.</summary>
    public int DelayMs
    {
        get => _vm.State.AudioPlayer.DelayFor(Name);
        set
        {
            if (value == DelayMs) return;
            _vm.State.AudioPlayer.SetDelay(Name, value);
            Raise(nameof(DelayMs));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) _vm.AudioDeviceChanged(this);
        }
    }
}

/// <summary>One Spotify Connect device in the "Play on" picker (empty name = whichever is active).</summary>
public sealed record SpotifyDeviceChoice(string Name, string Label);

/// <summary>A row of chips on the Particles page: one factory pack, or "Custom" for the operator's saved particle presets.</summary>
public sealed record ParticlePackGroup(string Category, IReadOnlyList<ParticleChip> Chips);

/// <summary>One preset chip: its name and what pressing it does to the editing target.</summary>
public sealed record ParticleChip(string Name, Action Apply);

/// <summary>
/// One choice in a look's Music picker: leave it, pause it, or a break-music entry. The label
/// follows a rename in place, so a bound row never loses its selected item.
/// </summary>
public sealed class LookMusicChoice : Patterns.Core.Model.Observable
{
    private string _label;

    public LookMusicChoice(string id, string label)
    {
        Id = id;
        _label = label;
    }

    public string Id { get; }

    public string Label { get => _label; set => Set(ref _label, value); }
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
    private bool _isSelected;
    private bool _isSendTarget;
    private bool _isOwn;
    private bool _isArmed;
    private bool _isMonitored = true;
    private bool _isOnAir;
    private bool _isHeld;
    private bool _isLocked;

    public SwitcherTile(MainViewModel vm, string title, string? targetId, IReadOnlyList<string> memberIds,
        SkiaSharp.SKSizeI size, bool enabled, bool isSelected, bool isOwn, bool isArmed,
        bool isLocked = false, string roleBadge = "", string mirrorNote = "")
    {
        _vm = vm;
        Title = title;
        TargetId = targetId;
        MemberIds = memberIds;
        Size = size;
        _enabled = enabled;
        _isSelected = isSelected;
        _isOwn = isOwn;
        _isArmed = isArmed;
        _isLocked = isLocked;
        RoleBadge = roleBadge;
        MirrorNote = mirrorNote;
        SendHereCommand = new RelayCommand(() => _vm.SendSandboxToTile(this));
        PgmViewport = Patterns.App.Rendering.PipelineViewport.Monitor(targetId, size, title, previewSide: false);
        PvwViewport = Patterns.App.Rendering.PipelineViewport.Monitor(targetId, size, title, previewSide: true);
    }

    /// <summary>CONF / INFO / REP for a screen with a role; empty for a main screen.</summary>
    public string RoleBadge { get; }

    public bool HasBadge => RoleBadge.Length > 0;

    public string BadgeTip => RoleBadge switch
    {
        "CONF" => "Confidence — a stage monitor: its own picture, left alone by looks and cues",
        "INFO" => "Info — a foyer or info screen: its own picture, left alone by looks and cues",
        "REP" => "Repeater — a copy of another target",
        _ => "",
    };

    /// <summary>"↳ A · Main wall" when this screen repeats another target; empty otherwise.</summary>
    public string MirrorNote { get; }

    public bool IsMirror => MirrorNote.Length > 0;

    /// <summary>The bottom line of the tile: what it repeats, or its size.</summary>
    public string FootText => IsMirror ? MirrorNote : SizeText;

    /// <summary>SEND: the preview lands on this tile alone (the sandbox must be open).</summary>
    public RelayCommand SendHereCommand { get; }

    /// <summary>"PGM", "A · Main wall" or "2 · Stage left".</summary>
    public string Title { get; }

    /// <summary>The content target this tile stands for: a screen id, a canvas key (a+b), or null for the program.</summary>
    public string? TargetId { get; }

    /// <summary>Screens this tile stands for ("send selected" targets; empty for the program tile).</summary>
    public IReadOnlyList<string> MemberIds { get; }

    public bool IsProgramTile => TargetId is null;

    /// <summary>The target's real pixel size — the miniatures and the big panes take this shape.</summary>
    public SkiaSharp.SKSizeI Size { get; }

    public double Ratio => Size.Height > 0 ? (double)Size.Width / Size.Height : 16.0 / 9.0;

    public string SizeText => $"{Size.Width}×{Size.Height}";

    /// <summary>What is on air for this target (never the sandbox).</summary>
    public Patterns.App.Rendering.PipelineViewport PgmViewport { get; }

    /// <summary>What the next TAKE would put there (the sandbox while it is open, else the same as PGM).</summary>
    public Patterns.App.Rendering.PipelineViewport PvwViewport { get; }

    /// <summary>Screen this tile edits when clicked; null = the program.</summary>
    public string? EditScreenId => IsOwn ? TargetId : null;

    /// <summary>OUTPUT: the member screens on or off, live.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value)) _vm.SetTileEnabled(this, value);
        }
    }

    /// <summary>Highlight: the big panes and the editors follow this tile.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Sandbox send target tick (visible only while the sandbox is open).</summary>
    public bool IsSendTarget
    {
        get => _isSendTarget;
        set => Set(ref _isSendTarget, value);
    }

    /// <summary>OWN: this target shows its own pattern instead of the program.</summary>
    public bool IsOwn
    {
        get => _isOwn;
        set
        {
            if (Set(ref _isOwn, value))
            {
                Raise(nameof(EditScreenId));
                _vm.SetTileOwn(this, value);
            }
        }
    }

    /// <summary>ARM: the next CUT / TAKE changes this target. Un-armed, it keeps its picture.</summary>
    public bool IsArmed
    {
        get => _isArmed;
        set
        {
            if (Set(ref _isArmed, value)) _vm.SetTileArmed(this, value);
        }
    }

    /// <summary>LOCK: this target keeps its picture through looks, cues, TAKE ALL and stingers (a confidence or info screen). Saved with the show.</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (Set(ref _isLocked, value)) _vm.SetTileLocked(this, value);
        }
    }

    /// <summary>MON: the wall draws this target's PGM and PVW miniatures (off saves GPU on a big rig).</summary>
    public bool IsMonitored
    {
        get => _isMonitored;
        set => Set(ref _isMonitored, value);
    }

    /// <summary>Tally red: the audience can see this target right now.</summary>
    public bool IsOnAir
    {
        get => _isOnAir;
        private set => Set(ref _isOnAir, value);
    }

    /// <summary>Tally amber: a send is being built and this target is un-armed, so it will keep its picture.</summary>
    public bool IsHeld
    {
        get => _isHeld;
        private set => Set(ref _isHeld, value);
    }

    /// <summary>Refreshes live state without rebuilding the wall (keeps focus, ticks and MON).</summary>
    public void RefreshExternal(bool enabled, bool isSelected, bool isOwn, bool isArmed, bool onAir, bool held, bool locked)
    {
        if (_isLocked != locked)
        {
            _isLocked = locked;
            Raise(nameof(IsLocked)); // reflects — no SetTileLocked echo
        }
        if (_enabled != enabled)
        {
            _enabled = enabled;
            Raise(nameof(Enabled)); // no SetTileEnabled echo — this reflects, not commands
        }
        if (_isOwn != isOwn)
        {
            _isOwn = isOwn;
            Raise(nameof(IsOwn));
            Raise(nameof(EditScreenId));
        }
        if (_isArmed != isArmed)
        {
            _isArmed = isArmed;
            Raise(nameof(IsArmed));
        }
        IsSelected = isSelected;
        IsOnAir = onAir;
        IsHeld = held;
    }
}

/// <summary>A labelled enum value for combo boxes.</summary>
public sealed record EnumItem(object Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One advisor line on the Admin tab, with its severity colour resolved.</summary>
public sealed record SuggestionRow(string Title, string Detail, Avalonia.Media.IBrush Dot);

/// <summary>One row of the super-check on the Admin page: the section, the item, its light as a brush, the value and a note.</summary>
public sealed record CheckRowView(string Section, string Item, string Value, string Note, Avalonia.Media.IBrush Dot)
{
    public bool HasNote => Note.Length > 0;
}

/// <summary>One detected graphics adapter row on the Admin tab.</summary>
public sealed record GpuRow(string Name, string Detail);

public sealed record ResolutionPreset(string Label, int W, int H)
{
    public override string ToString() => Label;
}

/// <summary>A frame-rate choice: 0 means "follow" (the display's refresh for the master, the master for a screen).</summary>
public sealed record FpsOption(int Value, string Label)
{
    public override string ToString() => Label;

    public static readonly FpsOption[] Master =
    {
        new(0, "Every display's own refresh (unlimited)"),
        new(24, "24 fps"), new(25, "25 fps"), new(30, "30 fps"), new(50, "50 fps"), new(60, "60 fps"),
    };

    public static readonly FpsOption[] Screen =
    {
        new(0, "The show's master rate"),
        new(24, "24 fps"), new(25, "25 fps"), new(30, "30 fps"), new(50, "50 fps"), new(60, "60 fps"),
    };
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
        new(PatternKind.Fractal, "Fractal (sound-reactive)"),
        new(PatternKind.Multiview, "Multiview (monitor wall)"),
    };

    public static readonly EnumItem[] MultiviewSources =
    {
        new(MultiviewSource.Program, "Program"),
        new(MultiviewSource.Screen, "A screen or canvas"),
        new(MultiviewSource.NdiFeed, "NDI feed"),
        new(MultiviewSource.Capture, "Capture device"),
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

    public static readonly EnumItem[] StingerKinds =
    {
        new(StingerKind.Vog, "VOG — over the show, the music ducks"),
        new(StingerKind.Sting, "Stinger — a transition hit, the music fades"),
    };

    public static readonly EnumItem[] PulsePresets =
    {
        new(PulsePreset.Explosion, "Explosion — a burst from the emitter, a flash, then settle"),
        new(PulsePreset.Rush, "Rush — everything speeds up and dives in"),
        new(PulsePreset.Flash, "Flash — a white hit and a glow"),
        new(PulsePreset.Bloom, "Bloom — a swell of size and glow"),
        new(PulsePreset.Shockwave, "Shockwave — a hit, then a ring rolls out through the field and the fractal punches out and back"),
        new(PulsePreset.Vortex, "Vortex — the field spins up into a whirl, the fractal turns, then it all lets go"),
        new(PulsePreset.Strobe, "Strobe — eight hits with the colours flipping between them"),
        new(PulsePreset.Supernova, "Supernova — a blast, everything falls upward and the colours sweep the wheel"),
        new(PulsePreset.Freeze, "Freeze — slow motion and a cold shift, then the release"),
        new(PulsePreset.Gust, "Gust — a wind slams through one way and back the other"),
        new(PulsePreset.Rainbow, "Rainbow — a full turn of the colours with a glow, no hit"),
        new(PulsePreset.Quake, "Quake — the picture shakes, a ripple runs, it settles"),
    };

    public static readonly EnumItem[] StingerAfters =
    {
        new(StingerAfter.Return, "Back to what was on"),
        new(StingerAfter.Manual, "Hold — I'll TAKE or GO"),
        new(StingerAfter.Next, "GO the next cue"),
        new(StingerAfter.Custom, "A look or cue I name…"),
    };

    public static readonly EnumItem[] Anchors = Of<Anchor9>();

    // ---- lower thirds ----
    public static readonly EnumItem[] LowerThirdKinds =
    {
        new(LowerThirdElementKind.Text, "Text"),
        new(LowerThirdElementKind.Bar, "Bar / panel"),
        new(LowerThirdElementKind.Image, "Picture (a file)"),
        new(LowerThirdElementKind.Logo, "Brand logo"),
        new(LowerThirdElementKind.Media, "Clip or still (a file)"),
        new(LowerThirdElementKind.Particles, "Particles"),
        new(LowerThirdElementKind.Fractal, "Fractal"),
    };

    public static readonly EnumItem[] LowerThirdTextKinds =
    {
        new(LowerThirdTextKind.Custom, "Your own words"),
        new(LowerThirdTextKind.Name, "The name"),
        new(LowerThirdTextKind.Role, "The role"),
        new(LowerThirdTextKind.Company, "The company (brand kit when empty)"),
        new(LowerThirdTextKind.Date, "The date"),
        new(LowerThirdTextKind.Time, "The time"),
        new(LowerThirdTextKind.DateAndTime, "Date and time"),
    };

    public static readonly EnumItem[] LowerThirdAligns = Of<LowerThirdAlign>();

    public static readonly EnumItem[] LowerThirdFills =
    {
        new(LowerThirdFill.None, "None"),
        new(LowerThirdFill.Solid, "Solid colour"),
        new(LowerThirdFill.Gradient, "Gradient"),
    };

    public static readonly EnumItem[] LowerThirdGradients =
    {
        new(LowerThirdGradient.LeftRight, "Left to right"),
        new(LowerThirdGradient.TopBottom, "Top to bottom"),
        new(LowerThirdGradient.Diagonal, "Diagonal"),
    };

    public static readonly EnumItem[] EaseKinds =
    {
        new(EaseKind.Linear, "Linear"),
        new(EaseKind.EaseIn, "Ease in"),
        new(EaseKind.EaseOut, "Ease out"),
        new(EaseKind.EaseInOut, "Ease in and out"),
        new(EaseKind.Back, "Back (a little overshoot)"),
        new(EaseKind.Bounce, "Bounce"),
        new(EaseKind.Elastic, "Elastic"),
    };

    /// <summary>The ready-made ways in and out, as the motion chips name them.</summary>
    public static readonly string[] LowerThirdMotionNames = Enum.GetNames<LowerThirdMotion>();

    public static readonly EnumItem[] LowerThirdEmitters = Of<ParticleEmitter>();
    public static readonly EnumItem[] LowerThirdFractalKinds = Of<FractalKind>();
    public static readonly EnumItem[] LowerThirdQualities = Of<FractalQuality>();
    public static readonly EnumItem[] MessageBackgrounds =
    {
        new(MessageBackground.Auto, "Auto — chip when static, none when scrolling"),
        new(MessageBackground.None, "None — text over the picture"),
        new(MessageBackground.Chip, "Solid — a chip, or a bar behind a ticker"),
        new(MessageBackground.Fade, "Fade — soft band, darkest at the edge"),
    };
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
    public static readonly EnumItem[] FractalKinds =
    {
        new(FractalKind.Mandelbrot, "Mandelbrot"),
        new(FractalKind.Julia, "Julia"),
        new(FractalKind.BurningShip, "Burning ship"),
        new(FractalKind.Newton, "Newton"),
        new(FractalKind.DomainWarp, "Domain warp (flowing noise)"),
    };
    public static readonly EnumItem[] AudioSources =
    {
        new(AudioSourceKind.None, "No sound — just the motion"),
        new(AudioSourceKind.Internal, "This computer's sound (what it plays)"),
        new(AudioSourceKind.External, "An input — microphone, line, interface"),
    };
    public static readonly EnumItem[] FractalQualities =
    {
        new(FractalQuality.Balanced, "Balanced"),
        new(FractalQuality.Fast, "Fast (NDI and thumbnails coarser)"),
        new(FractalQuality.Fine, "Fine (NDI and thumbnails cost more CPU)"),
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
        new(MediaSource.Web, "Web page (inside the engine)"),
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

    public static readonly EnumItem[] LayerSources =
    {
        new(LayerSource.Image, "Image (still)"),
        new(LayerSource.Video, "Video clip"),
        new(LayerSource.NdiFeed, "NDI feed (network)"),
        new(LayerSource.Capture, "Capture device (HDMI / SDI / webcam)"),
        new(LayerSource.Screen, "Another screen or canvas"),
        new(LayerSource.Web, "Web page"),
    };

    public static readonly EnumItem[] ScreenRoles =
    {
        new(ScreenRole.Main, Patterns.Core.Services.ScreenRoles.Label(ScreenRole.Main)),
        new(ScreenRole.Confidence, Patterns.Core.Services.ScreenRoles.Label(ScreenRole.Confidence)),
        new(ScreenRole.Info, Patterns.Core.Services.ScreenRoles.Label(ScreenRole.Info)),
        new(ScreenRole.Repeater, Patterns.Core.Services.ScreenRoles.Label(ScreenRole.Repeater)),
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
