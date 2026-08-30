using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Patterns.Core.Model;

public sealed class OutputConfig : Observable
{
    private bool _topmost = true;
    private bool _hideCursor = true;

    public bool Topmost { get => _topmost; set => Set(ref _topmost, value); }
    public bool HideCursor { get => _hideCursor; set => Set(ref _hideCursor, value); }

    /// <summary>
    /// The spatial screen arrangement. Screens dragged flush against each other form one
    /// spanned canvas; screens standing alone are independent outputs. Placement positions
    /// are in arrangement space (device pixels), unrelated to the OS desktop layout.
    /// </summary>
    public ObservableCollection<ScreenPlacement> Placements { get; init; } = new();
}

/// <summary>One physical screen's place in the arrangement.</summary>
public sealed class ScreenPlacement : Observable
{
    private string _screenId = "";
    private int _x;
    private int _y;
    private bool _enabled = true;
    private bool _useCustomPattern;
    private bool _userPinned;

    public string ScreenId { get => _screenId; set => Set(ref _screenId, value); }
    /// <summary>Arranged position in device pixels (top-left).</summary>
    public int X { get => _x; set => Set(ref _x, value); }
    public int Y { get => _y; set => Set(ref _y, value); }
    /// <summary>Disabled screens get no output window (e.g. the operator's own screen).</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>Show this screen's own pattern instead of the program (stand-alone screens only).</summary>
    public bool UseCustomPattern { get => _useCustomPattern; set => Set(ref _useCustomPattern, value); }
    /// <summary>Set once the user chose Enabled manually — stops automatic defaults overriding them.</summary>
    public bool UserPinned { get => _userPinned; set => Set(ref _userPinned, value); }
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
    private string _textColor = "";

    /// <summary>Clock text colour; empty = brand/theme text colour.</summary>
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }

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
    private string _textColor = "";

    /// <summary>Message text colour; empty = brand/theme text colour.</summary>
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }

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
    private string _textColor = "";

    /// <summary>Digits colour; empty = brand/theme text colour (urgency tint still applies).</summary>
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }

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

/// <summary>One advertised NDI source.</summary>
public sealed class NdiSenderConfig : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private bool _enabled = true;
    private string _name = "Patterns";
    private int _width = 1920;
    private int _height = 1080;
    private string _rateKey = "60";
    private string _sourceScreenId = "";
    private bool _tenBit;
    private string _status = "";

    public string Id { get => _id; set => Set(ref _id, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public int Width { get => _width; set => Set(ref _width, Math.Clamp(value, 16, 8192)); }
    public int Height { get => _height; set => Set(ref _height, Math.Clamp(value, 16, 8192)); }
    /// <summary>Frame-rate key from <c>NdiRateTable</c> ("23.98"…"60").</summary>
    public string RateKey { get => _rateKey; set => Set(ref _rateKey, value); }
    /// <summary>Empty = program; otherwise the screen whose pattern this sender mirrors.</summary>
    public string SourceScreenId { get => _sourceScreenId; set => Set(ref _sourceScreenId, value); }
    /// <summary>Send 10-bit P216 (renders internally at 10 bpc; heavier on CPU).</summary>
    public bool TenBit { get => _tenBit; set => Set(ref _tenBit, value); }

    /// <summary>Runtime status line for the UI.</summary>
    [JsonIgnore]
    public string Status { get => _status; set => Set(ref _status, value); }
}

public sealed class NdiConfig : Observable
{
    /// <summary>All configured NDI sources; each enabled one runs its own sender thread.</summary>
    public ObservableCollection<NdiSenderConfig> Senders { get; init; } = new();
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

    /// <summary>Media the operator has loaded — surfaces in the Library under "My media".</summary>
    public ObservableCollection<MediaLibraryEntry> MediaLibrary { get; init; } = new();
}

public sealed class MediaLibraryEntry : Observable
{
    private string _path = "";
    private bool _isVideo;

    public string Path { get => _path; set => Set(ref _path, value); }
    public bool IsVideo { get => _isVideo; set => Set(ref _isVideo, value); }
}
