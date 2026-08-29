using System.Collections.ObjectModel;

namespace Patterns.Core.Model;

public sealed class OutputConfig : Observable
{
    private OutputMode _mode = OutputMode.Duplicate;
    private bool _topmost = true;
    private bool _hideCursor = true;

    public OutputMode Mode { get => _mode; set => Set(ref _mode, value); }
    public bool Topmost { get => _topmost; set => Set(ref _topmost, value); }
    public bool HideCursor { get => _hideCursor; set => Set(ref _hideCursor, value); }

    /// <summary>Stable ids of the screens patterns go to (empty = all screens).</summary>
    public ObservableCollection<string> SelectedScreenIds { get; init; } = new();
}

/// <summary>Per-screen pattern in Independent mode.</summary>
public sealed class OutputAssignment : Observable
{
    private string _screenId = "";
    public string ScreenId { get => _screenId; set => Set(ref _screenId, value); }
    public PatternConfig Pattern { get; init; } = new();
}

public sealed class ClockOverlay : Observable
{
    private bool _enabled = false;
    private bool _twentyFourHour = true;
    private bool _showSeconds = true;
    private bool _showDate = true;
    private Anchor9 _anchor = Anchor9.TopRight;
    private double _sizePct = 8;
    private double _opacity = 1.0;
    private bool _pill = true;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool TwentyFourHour { get => _twentyFourHour; set => Set(ref _twentyFourHour, value); }
    public bool ShowSeconds { get => _showSeconds; set => Set(ref _showSeconds, value); }
    public bool ShowDate { get => _showDate; set => Set(ref _showDate, value); }
    public Anchor9 Anchor { get => _anchor; set => Set(ref _anchor, value); }
    /// <summary>Digit height as % of canvas height.</summary>
    public double SizePct { get => _sizePct; set => Set(ref _sizePct, Math.Clamp(value, 2, 40)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.05, 1)); }
    /// <summary>Draw a translucent pill behind the text.</summary>
    public bool Pill { get => _pill; set => Set(ref _pill, value); }
}

public sealed class LogoOverlay : Observable
{
    private bool _enabled = false;
    private Anchor9 _anchor = Anchor9.BottomRight;
    private double _heightPct = 12;
    private double _opacity = 0.9;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public Anchor9 Anchor { get => _anchor; set => Set(ref _anchor, value); }
    public double HeightPct { get => _heightPct; set => Set(ref _heightPct, Math.Clamp(value, 2, 100)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.05, 1)); }
}

public sealed class InfoOverlay : Observable
{
    private bool _enabled = false;
    private bool _showFps = true;
    private Anchor9 _anchor = Anchor9.BottomLeft;

    /// <summary>Canvas size, sink name and pattern name chip.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool ShowFps { get => _showFps; set => Set(ref _showFps, value); }
    public Anchor9 Anchor { get => _anchor; set => Set(ref _anchor, value); }
}

public sealed class MessageOverlay : Observable
{
    private bool _enabled = false;
    private string _text = "WELCOME";
    private Anchor9 _anchor = Anchor9.BottomCenter;
    private double _sizePct = 6;
    private bool _scroll = false;
    private double _scrollPxPerSec = 120;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public Anchor9 Anchor { get => _anchor; set => Set(ref _anchor, value); }
    public double SizePct { get => _sizePct; set => Set(ref _sizePct, Math.Clamp(value, 2, 30)); }
    public bool Scroll { get => _scroll; set => Set(ref _scroll, value); }
    public double ScrollPxPerSec { get => _scrollPxPerSec; set => Set(ref _scrollPxPerSec, Math.Clamp(value, 10, 4000)); }
}

public sealed class OverlaySet : Observable
{
    public ClockOverlay Clock { get; init; } = new();
    public LogoOverlay Logo { get; init; } = new();
    public InfoOverlay Info { get; init; } = new();
    public MessageOverlay Message { get; init; } = new();
}

public sealed class CountdownConfig : Observable
{
    private bool _enabled = false;
    private string _label = "SHOW STARTS IN";
    private CountdownTargetKind _targetKind = CountdownTargetKind.TimeOfDay;
    private string _targetTime = "19:30";
    private double _durationMinutes = 15;
    private DateTime? _armedAtUtc;
    private CountdownEndBehavior _endBehavior = CountdownEndBehavior.Flash;
    private string _endMessage = "STARTING NOW";
    private bool _showProgressBar = false;
    private Anchor9 _anchor = Anchor9.Center;
    private double _sizePct = 18;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>e.g. “BACK FROM LUNCH AT”, “REHEARSAL RESUMES IN”, “DOORS IN”.</summary>
    public string Label { get => _label; set => Set(ref _label, value); }
    public CountdownTargetKind TargetKind { get => _targetKind; set => Set(ref _targetKind, value); }
    /// <summary>Wall-clock target “HH:mm” (24 h), local time.</summary>
    public string TargetTime { get => _targetTime; set => Set(ref _targetTime, value); }
    public double DurationMinutes { get => _durationMinutes; set => Set(ref _durationMinutes, Math.Clamp(value, 0.1, 24 * 60)); }
    /// <summary>Set when a duration countdown is (re)armed.</summary>
    public DateTime? ArmedAtUtc { get => _armedAtUtc; set => Set(ref _armedAtUtc, value); }
    public CountdownEndBehavior EndBehavior { get => _endBehavior; set => Set(ref _endBehavior, value); }
    public string EndMessage { get => _endMessage; set => Set(ref _endMessage, value); }
    public bool ShowProgressBar { get => _showProgressBar; set => Set(ref _showProgressBar, value); }
    public Anchor9 Anchor { get => _anchor; set => Set(ref _anchor, value); }
    public double SizePct { get => _sizePct; set => Set(ref _sizePct, Math.Clamp(value, 4, 45)); }
}

/// <summary>Corporate branding used across patterns, particles and overlays.</summary>
public sealed class BrandKit : Observable
{
    private string _companyName = "";
    private string _primaryColor = "#3EC1F3";
    private string _secondaryColor = "#F03EAE";
    private string _accentColor = "#FFB020";
    private string _backgroundColor = "#000000";
    private string _textColor = "#FFFFFF";
    private string _logoPath = "";
    private bool _applyToPatterns = false;

    public string CompanyName { get => _companyName; set => Set(ref _companyName, value); }
    public string PrimaryColor { get => _primaryColor; set => Set(ref _primaryColor, value); }
    public string SecondaryColor { get => _secondaryColor; set => Set(ref _secondaryColor, value); }
    public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }
    public string LogoPath { get => _logoPath; set => Set(ref _logoPath, value); }
    /// <summary>Use brand colours in patterns (grids, cycles, particles) instead of defaults.</summary>
    public bool ApplyToPatterns { get => _applyToPatterns; set => Set(ref _applyToPatterns, value); }
}

public sealed class NdiConfig : Observable
{
    private bool _enabled = false;
    private string _senderName = "Patterns";
    private int _width = 1920;
    private int _height = 1080;
    private int _frameRateN = 60000;
    private int _frameRateD = 1000;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string SenderName { get => _senderName; set => Set(ref _senderName, value); }
    public int Width { get => _width; set => Set(ref _width, Math.Clamp(value, 16, 8192)); }
    public int Height { get => _height; set => Set(ref _height, Math.Clamp(value, 16, 8192)); }
    public int FrameRateN { get => _frameRateN; set => Set(ref _frameRateN, Math.Clamp(value, 1, 240000)); }
    public int FrameRateD { get => _frameRateD; set => Set(ref _frameRateD, Math.Clamp(value, 1, 1001)); }
}

/// <summary>Root of everything the operator can configure. Serialized as the portable settings/show file.</summary>
public sealed class ShowState : Observable
{
    private bool _blackout = false;

    /// <summary>Instant black on every sink. Checked before any pattern code runs.</summary>
    public bool Blackout { get => _blackout; set => Set(ref _blackout, value); }

    public OutputConfig Output { get; init; } = new();
    /// <summary>The program pattern (Duplicate/Span modes, preview, NDI).</summary>
    public PatternConfig Pattern { get; init; } = new();
    /// <summary>Per-screen patterns for Independent mode.</summary>
    public ObservableCollection<OutputAssignment> Independent { get; init; } = new();
    public OverlaySet Overlays { get; init; } = new();
    public CountdownConfig Countdown { get; init; } = new();
    public BrandKit Brand { get; init; } = new();
    public NdiConfig Ndi { get; init; } = new();
}
