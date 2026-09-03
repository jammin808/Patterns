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

    /// <summary>Operator names for joined canvases ("Main wall"), keyed by their member set.</summary>
    public ObservableCollection<CanvasNameConfig> CanvasNames { get; init; } = new();
}

/// <summary>A custom name for one joined canvas. The key survives letters shifting as screens rearrange.</summary>
public sealed class CanvasNameConfig : Observable
{
    private string _memberKey = "";
    private string _name = "";
    private bool _useCustomPattern;

    /// <summary>Sorted member screen ids joined with '+' — stable identity for a given set of screens.</summary>
    public string MemberKey { get => _memberKey; set => Set(ref _memberKey, value); }
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>
    /// The canvas shows its own pattern (an <see cref="OutputAssignment"/> keyed by
    /// <see cref="MemberKey"/>) instead of the program — the same choice a single screen has.
    /// </summary>
    public bool UseCustomPattern { get => _useCustomPattern; set => Set(ref _useCustomPattern, value); }

    public static string KeyFor(IEnumerable<string> memberScreenIds)
        => string.Join('+', memberScreenIds.OrderBy(id => id, StringComparer.Ordinal));
}

/// <summary>One physical screen's place in the arrangement.</summary>
public sealed class ScreenPlacement : Observable
{
    private string _screenId = "";
    private string _customLabel = "";
    private int _x;
    private int _y;
    private bool _enabled = true;
    private bool _useCustomPattern;
    private bool _userPinned;
    private bool _planned;
    private int _plannedWidth = 1920;
    private int _plannedHeight = 1080;
    private OutputRotation _rotation = OutputRotation.None;
    private double _brightnessPct = 100;
    private double _gamma = 1.0;
    private double _trimRPct = 100;
    private double _trimGPct = 100;
    private double _trimBPct = 100;

    public string ScreenId { get => _screenId; set => Set(ref _screenId, value); }

    /// <summary>Operator label ("Stage left LED"); empty = the OS display name.</summary>
    public string CustomLabel { get => _customLabel; set => Set(ref _customLabel, value); }

    /// <summary>Arranged position in device pixels (top-left).</summary>
    public int X { get => _x; set => Set(ref _x, value); }
    public int Y { get => _y; set => Set(ref _y, value); }
    /// <summary>Disabled screens get no output window (e.g. the operator's own screen).</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>Show this screen's own pattern instead of the program (stand-alone screens only).</summary>
    public bool UseCustomPattern { get => _useCustomPattern; set => Set(ref _useCustomPattern, value); }
    /// <summary>Set once the user chose Enabled manually — stops automatic defaults overriding them.</summary>
    public bool UserPinned { get => _userPinned; set => Set(ref _userPinned, value); }

    /// <summary>
    /// A screen the operator added while pre-programming, with no hardware behind it yet. It
    /// takes part in the arrangement, editors, looks and multiview exactly like a real screen,
    /// but never opens an output window — at the venue it is adopted onto a real display.
    /// </summary>
    public bool Planned { get => _planned; set => Set(ref _planned, value); }

    /// <summary>The size a planned screen stands in for (the LED processor's canvas, the projector's native res).</summary>
    public int PlannedWidth { get => _plannedWidth; set => Set(ref _plannedWidth, Math.Clamp(value, 160, 16384)); }
    public int PlannedHeight { get => _plannedHeight; set => Set(ref _plannedHeight, Math.Clamp(value, 160, 16384)); }

    /// <summary>Id prefix that marks a placement as planned rather than a detected display.</summary>
    public const string PlannedIdPrefix = "planned:";

    private string _adoptTargetId = "";

    /// <summary>Runtime-only: the display chosen in the adopt picker for this planned screen.</summary>
    [JsonIgnore]
    public string AdoptTargetId { get => _adoptTargetId; set => Set(ref _adoptTargetId, value); }

    /// <summary>Physical rotation — content is pre-rotated so a rotated display reads upright.</summary>
    public OutputRotation Rotation { get => _rotation; set => Set(ref _rotation, value); }
    public double BrightnessPct { get => _brightnessPct; set => Set(ref _brightnessPct, Math.Clamp(value, 10, 200)); }
    /// <summary>Midtone gamma trim; 1.0 = neutral, above darkens mids, below lifts them.</summary>
    public double Gamma { get => _gamma; set => Set(ref _gamma, Math.Clamp(value, 0.4, 2.5)); }
    public double TrimRPct { get => _trimRPct; set => Set(ref _trimRPct, Math.Clamp(value, 25, 175)); }
    public double TrimGPct { get => _trimGPct; set => Set(ref _trimGPct, Math.Clamp(value, 25, 175)); }
    public double TrimBPct { get => _trimBPct; set => Set(ref _trimBPct, Math.Clamp(value, 25, 175)); }

    public bool HasTrims =>
        Math.Abs(_brightnessPct - 100) > 0.01 || Math.Abs(_gamma - 1.0) > 0.001 ||
        Math.Abs(_trimRPct - 100) > 0.01 || Math.Abs(_trimGPct - 100) > 0.01 || Math.Abs(_trimBPct - 100) > 0.01;

    // 4-corner warp: pixel offsets applied to each corner of the physical output
    // (a light keystone for casually placed projectors — not a full warp engine).
    private int _warpTlx; private int _warpTly; private int _warpTrx; private int _warpTry;
    private int _warpBlx; private int _warpBly; private int _warpBrx; private int _warpBry;

    public int WarpTlx { get => _warpTlx; set => Set(ref _warpTlx, Math.Clamp(value, -4096, 4096)); }
    public int WarpTly { get => _warpTly; set => Set(ref _warpTly, Math.Clamp(value, -4096, 4096)); }
    public int WarpTrx { get => _warpTrx; set => Set(ref _warpTrx, Math.Clamp(value, -4096, 4096)); }
    public int WarpTry { get => _warpTry; set => Set(ref _warpTry, Math.Clamp(value, -4096, 4096)); }
    public int WarpBlx { get => _warpBlx; set => Set(ref _warpBlx, Math.Clamp(value, -4096, 4096)); }
    public int WarpBly { get => _warpBly; set => Set(ref _warpBly, Math.Clamp(value, -4096, 4096)); }
    public int WarpBrx { get => _warpBrx; set => Set(ref _warpBrx, Math.Clamp(value, -4096, 4096)); }
    public int WarpBry { get => _warpBry; set => Set(ref _warpBry, Math.Clamp(value, -4096, 4096)); }

    public bool HasWarp =>
        _warpTlx != 0 || _warpTly != 0 || _warpTrx != 0 || _warpTry != 0 ||
        _warpBlx != 0 || _warpBly != 0 || _warpBrx != 0 || _warpBry != 0;
}

/// <summary>Per-screen pattern in Independent mode.</summary>
public sealed class OutputAssignment : Observable
{
    private string _screenId = "";
    private bool _pinnedByTake;

    /// <summary>The content target this pattern belongs to: a screen id, or a canvas member key.</summary>
    public string ScreenId { get => _screenId; set => Set(ref _screenId, value); }
    public PatternConfig Pattern { get; init; } = new();

    /// <summary>
    /// Written by a scoped TAKE to keep an un-armed target on its old picture. The next TAKE
    /// that arms the target lifts the pin so it follows the program again; a pattern the
    /// operator chose for the target is never pinned and never lifted.
    /// </summary>
    public bool PinnedByTake { get => _pinnedByTake; set => Set(ref _pinnedByTake, value); }
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
    private bool _useFeed;
    private string _feedSource = "";
    private FeedKind _feedKind = FeedKind.Auto;
    private double _feedRefreshMinutes = 10;
    private string _feedSeparator = "   •   ";
    private int _feedMaxItems = 30;

    /// <summary>Message text colour; empty = brand/theme text colour.</summary>
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }

    /// <summary>Replace the static text with a live feed (RSS/Atom, CSV lines, or ICS calendar).</summary>
    public bool UseFeed { get => _useFeed; set => Set(ref _useFeed, value); }
    /// <summary>http(s) URL or a local file path.</summary>
    public string FeedSource { get => _feedSource; set => Set(ref _feedSource, value); }
    public FeedKind FeedKind { get => _feedKind; set => Set(ref _feedKind, value); }
    public double FeedRefreshMinutes { get => _feedRefreshMinutes; set => Set(ref _feedRefreshMinutes, Math.Clamp(value, 0.5, 24 * 60)); }
    public string FeedSeparator { get => _feedSeparator; set => Set(ref _feedSeparator, value); }
    public int FeedMaxItems { get => _feedMaxItems; set => Set(ref _feedMaxItems, Math.Clamp(value, 1, 200)); }

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
    public PipOverlay Pip { get; init; } = new();
}

/// <summary>Picture-in-picture inset: a second live input composited over whatever is showing.</summary>
public sealed class PipOverlay : Observable
{
    private bool _enabled;
    private PipSource _source = PipSource.NdiFeed;
    private string _ndiSourceName = "";
    private string _captureDevice = "";
    private Anchor9 _anchor = Anchor9.BottomRight;
    private double _widthPct = 25;
    private double _opacity = 1.0;
    private bool _showBorder = true;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public PipSource Source { get => _source; set => Set(ref _source, value); }
    public string NdiSourceName { get => _ndiSourceName; set => Set(ref _ndiSourceName, value); }
    public string CaptureDevice { get => _captureDevice; set => Set(ref _captureDevice, value); }
    public Anchor9 Anchor { get => _anchor; set => Set(ref _anchor, value); }
    /// <summary>Inset width as a percentage of the viewport width.</summary>
    public double WidthPct { get => _widthPct; set => Set(ref _widthPct, Math.Clamp(value, 10, 50)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.1, 1.0)); }
    public bool ShowBorder { get => _showBorder; set => Set(ref _showBorder, value); }
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
    private string _fontFamily = "";

    /// <summary>System font family for overlay text (clock, countdown, message); empty = built-in Inter.</summary>
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }

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

/// <summary>Soundcheck tone generator (Windows audio device).</summary>
public sealed class ToneConfig : Observable
{
    private bool _enabled;
    private double _frequencyHz = 1000;
    private double _levelDb = -18;
    private ToneMode _mode = ToneMode.ChannelIdent;
    private ToneChannels _channels = ToneChannels.Both;

    /// <summary>Reset to off at startup — a tone must never auto-start with the app.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public double FrequencyHz { get => _frequencyHz; set => Set(ref _frequencyHz, Math.Clamp(value, 20, 20000)); }
    /// <summary>Output level in dBFS.</summary>
    public double LevelDb { get => _levelDb; set => Set(ref _levelDb, Math.Clamp(value, -60, 0)); }
    public ToneMode Mode { get => _mode; set => Set(ref _mode, value); }
    /// <summary>Channel routing for continuous mode (ident alternates L/R by itself).</summary>
    public ToneChannels Channels { get => _channels; set => Set(ref _channels, value); }
}

/// <summary>A saved content state ("look"): pattern, per-screen patterns, overlays, countdown, blackout.</summary>
public sealed class LookConfig : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "Look";
    private int _hotkey;
    private string _json = "";

    /// <summary>Stable identity (schema 4): renaming a look never breaks what references it.</summary>
    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>1–12 → F1–F12; 0 = no hotkey.</summary>
    public int Hotkey { get => _hotkey; set => Set(ref _hotkey, Math.Clamp(value, 0, 12)); }
    /// <summary>The captured state, stored as an opaque JSON blob (LookData).</summary>
    public string Json { get => _json; set => Set(ref _json, value); }
}

/// <summary>A scheduled recall: apply a look at a time of day, daily.</summary>
public sealed class CueConfig : Observable
{
    private bool _enabled = true;
    private string _time = "18:00";
    private string _lookName = "";
    private DateTime? _lastFiredDate;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>"HH:mm" local.</summary>
    public string Time { get => _time; set => Set(ref _time, value); }
    public string LookName { get => _lookName; set => Set(ref _lookName, value); }

    /// <summary>Runtime-only: the date this cue last fired (fires once per day).</summary>
    [JsonIgnore]
    public DateTime? LastFiredDate { get => _lastFiredDate; set => Set(ref _lastFiredDate, value); }
}

public sealed class LooksConfig : Observable
{
    public ObservableCollection<LookConfig> Looks { get; init; } = new();
    public ObservableCollection<CueConfig> Cues { get; init; } = new();
}

/// <summary>Crossfade between content changes on every sink (looks, playlist advances, blackout).</summary>
public sealed class TransitionConfig : Observable
{
    private bool _enabled = true;
    private double _durationMs = 400;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public double DurationMs { get => _durationMs; set => Set(ref _durationMs, Math.Clamp(value, 100, 3000)); }
}

/// <summary>One presenter step: a look recalled by a clicker press.</summary>
public sealed class PresenterStepConfig : Observable
{
    private string _lookName = "";
    private string _label = "";

    public string LookName { get => _lookName; set => Set(ref _lookName, value); }
    /// <summary>Optional note shown to the operator ("Sponsor stings", "Q&amp;A slide").</summary>
    public string Label { get => _label; set => Set(ref _label, value); }
}

/// <summary>
/// Presenter click-through: an ordered list of looks a presenter advances with a clicker
/// (Page Down / Page Up — the keys USB presentation remotes send).
/// </summary>
public sealed class PresenterConfig : Observable
{
    private bool _armed;
    private bool _loop;
    private int _currentIndex = -1;

    /// <summary>When armed, clicker keys (and remote NEXT/PREV) drive the steps.</summary>
    public bool Armed { get => _armed; set => Set(ref _armed, value); }
    public bool Loop { get => _loop; set => Set(ref _loop, value); }

    public ObservableCollection<PresenterStepConfig> Steps { get; init; } = new();

    /// <summary>Runtime-only: the step currently applied (-1 = not started).</summary>
    [JsonIgnore]
    public int CurrentIndex { get => _currentIndex; set => Set(ref _currentIndex, value); }
}

/// <summary>
/// Independent audio track player — plays regardless of what is on screen, to the default
/// device or any set of Windows audio outputs (HDMI screens are audio devices too).
/// </summary>
public sealed class AudioPlayerConfig : Observable
{
    private string _path = "";
    private bool _loop = true;
    private double _volumePct = 100;
    private bool _playing;

    public string Path { get => _path; set => Set(ref _path, value); }
    public bool Loop { get => _loop; set => Set(ref _loop, value); }
    public double VolumePct { get => _volumePct; set => Set(ref _volumePct, Math.Clamp(value, 0, 125)); }

    /// <summary>Output device names to play on; empty = the default device.</summary>
    public ObservableCollection<string> Devices { get; init; } = new();

    /// <summary>Runtime-only: playback never auto-starts with the app.</summary>
    [JsonIgnore]
    public bool Playing { get => _playing; set => Set(ref _playing, value); }
}

/// <summary>One stinger: a sound or clip fired over the show with a single press.</summary>
public sealed class StingerItemConfig : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "";
    private string _path = "";
    private double _volumePct = 100;

    /// <summary>Stable identity (schema 4) — names fall back to file names and need not be unique.</summary>
    public string Id { get => _id; set => Set(ref _id, value); }

    /// <summary>Button label ("Take your seats"); empty = the file name.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value)) Raise(nameof(DisplayName));
        }
    }

    public string Path
    {
        get => _path;
        set
        {
            if (Set(ref _path, value)) Raise(nameof(DisplayName));
        }
    }

    public double VolumePct { get => _volumePct; set => Set(ref _volumePct, Math.Clamp(value, 0, 125)); }

    /// <summary>What fire buttons show. Splits both separators — show files travel between OSes.</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (_name.Length > 0) return _name;
            var cut = _path.LastIndexOfAny(new[] { '/', '\\' });
            return cut >= 0 ? _path[(cut + 1)..] : _path;
        }
    }
}

/// <summary>
/// The stinger library — announcements and clips anyone can fire without touching the audio
/// desk. Audio stingers play over everything (the music track ducks underneath); video
/// stingers take over every screen and the previous content returns when the clip ends.
/// </summary>
public sealed class StingerConfig : Observable
{
    private double _duckPct = 20;
    private string _playingName = "";

    public ObservableCollection<StingerItemConfig> Items { get; init; } = new();

    /// <summary>Music-track level (as % of its own volume) while an audio stinger plays.</summary>
    public double DuckPct { get => _duckPct; set => Set(ref _duckPct, Math.Clamp(value, 0, 100)); }

    /// <summary>Runtime-only: name of the stinger on air ("" = none).</summary>
    [JsonIgnore]
    public string PlayingName { get => _playingName; set => Set(ref _playingName, value); }
}

/// <summary>An operator nickname for a live input ("Camera 1" for an NDI source or capture card).</summary>
public sealed class InputLabelConfig : Observable
{
    private string _key = "";
    private string _label = "";

    public string Key { get => _key; set => Set(ref _key, value); }
    public string Label { get => _label; set => Set(ref _label, value); }
}

/// <summary>One streaming destination (RTMP/RTMPS URL with key, srt://host:port, udp://host:port).</summary>
public sealed class StreamDestinationConfig : Observable
{
    private bool _enabled;
    private string _url = "";

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
}

/// <summary>
/// Streaming output: one screen captured and encoded once (shared resolution/frame rate),
/// duplicated to up to two destinations through the bundled libVLC.
/// </summary>
public sealed class StreamConfig : Observable
{
    private string _sourceScreenId = "";
    private int _width = 1280;
    private int _height = 720;
    private int _fps = 30;
    private int _videoKbps = 4500;
    private int _audioKbps = 160;
    private string _audioDevice = "";
    private bool _active;

    /// <summary>Screen whose output is streamed ("" = the first enabled screen).</summary>
    public string SourceScreenId { get => _sourceScreenId; set => Set(ref _sourceScreenId, value); }
    public int Width { get => _width; set => Set(ref _width, Math.Clamp(value, 320, 3840)); }
    public int Height { get => _height; set => Set(ref _height, Math.Clamp(value, 180, 2160)); }
    public int Fps { get => _fps; set => Set(ref _fps, Math.Clamp(value, 10, 60)); }
    public int VideoKbps { get => _videoKbps; set => Set(ref _videoKbps, Math.Clamp(value, 500, 20000)); }
    public int AudioKbps { get => _audioKbps; set => Set(ref _audioKbps, Math.Clamp(value, 64, 320)); }

    /// <summary>Optional DirectShow audio capture device name ("" = video only).</summary>
    public string AudioDevice { get => _audioDevice; set => Set(ref _audioDevice, value); }

    public ObservableCollection<StreamDestinationConfig> Destinations { get; init; } = new();

    /// <summary>Runtime-only: streaming never auto-starts with the app.</summary>
    [JsonIgnore]
    public bool Active { get => _active; set => Set(ref _active, value); }
}

/// <summary>The program/preview switcher's behaviour.</summary>
public sealed class SwitcherConfig : Observable
{
    private bool _editSafeByDefault = true;

    /// <summary>
    /// Start (and stay) in EDIT SAFE: the preview opens sandboxed, and after every CUT/TAKE/
    /// SEND it re-arms, so edits never reach the audience until they are sent. Off = the
    /// classic live-mirror preview unless the operator toggles EDIT SAFE on.
    /// </summary>
    public bool EditSafeByDefault { get => _editSafeByDefault; set => Set(ref _editSafeByDefault, value); }
}

/// <summary>Watchdog: the supervisor process that restarts the show after a crash or hang.</summary>
public sealed class WatchdogConfig : Observable
{
    private bool _enabled = true;
    private bool _autoRestore = true;

    /// <summary>Run under the supervisor (takes effect on the next start).</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>After a watchdog restart, put live outputs (and a playing track) back automatically.</summary>
    public bool AutoRestore { get => _autoRestore; set => Set(ref _autoRestore, value); }
}

/// <summary>Which GPU renders the show. Applied at startup — changing it needs an app restart.</summary>
public sealed class GraphicsConfig : Observable
{
    private GpuPreferenceKind _preference = GpuPreferenceKind.BestPerformance;
    private string _adapterName = "";
    private string _lastAppliedExePath = "";

    public GpuPreferenceKind Preference { get => _preference; set => Set(ref _preference, value); }

    /// <summary>Adapter name used when <see cref="Preference"/> is <see cref="GpuPreferenceKind.Specific"/>.</summary>
    public string AdapterName { get => _adapterName; set => Set(ref _adapterName, value); }

    /// <summary>Exe path whose Windows per-app GPU preference we last wrote — cleaned up when the exe moves.</summary>
    public string LastAppliedExePath { get => _lastAppliedExePath; set => Set(ref _lastAppliedExePath, value); }
}

/// <summary>Administration: graphics choice and the performance record.</summary>
public sealed class AdminConfig : Observable
{
    private bool _metricsCsv = true;

    public GraphicsConfig Graphics { get; init; } = new();

    /// <summary>Append a performance sample to patterns.metrics.csv every 30 s (rotated at 1 MB).</summary>
    public bool MetricsCsv { get => _metricsCsv; set => Set(ref _metricsCsv, value); }
}

/// <summary>Remote control server: web remote + TCP line protocol (Companion).</summary>
public sealed class ControlConfig : Observable
{
    private bool _enabled = true;
    private int _httpPort = 9696;
    private int _tcpPort = 9697;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public int HttpPort { get => _httpPort; set => Set(ref _httpPort, Math.Clamp(value, 1024, 65535)); }
    public int TcpPort { get => _tcpPort; set => Set(ref _tcpPort, Math.Clamp(value, 1024, 65535)); }
}

/// <summary>Web pages opened on outputs (managed browser windows, not engine-composited).</summary>
public sealed class WebConfig : Observable
{
    private string _url = "";
    private string _targetScreenId = "";

    /// <summary>The page to open (https://… or a local file path).</summary>
    public string Url { get => _url; set => Set(ref _url, value); }
    /// <summary>Screen the browser window opens on ("" = primary).</summary>
    public string TargetScreenId { get => _targetScreenId; set => Set(ref _targetScreenId, value); }

    /// <summary>Quick-recall URLs (session schedules, dashboards, wayfinding pages).</summary>
    public ObservableCollection<string> SavedUrls { get; init; } = new();
}

/// <summary>Root of everything the operator can configure. Serialized as the portable settings/show file.</summary>
public sealed class ShowState : Observable
{
    public const int CurrentSchemaVersion = 4;

    private bool _blackout = false;
    private int _schemaVersion; // absent in old files → 0 → migrations run
    private ShowMode _mode = ShowMode.Show;
    private string _name = "";

    /// <summary>The show's name as the caller sees it (defaults to the file name it was loaded from).</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>
    /// Prep (pre-programming, outputs held closed) or Show (at the venue). Saved with the show,
    /// so a file built at the desk reopens in prep and one saved at the venue reopens ready.
    /// </summary>
    public ShowMode Mode { get => _mode; set => Set(ref _mode, value); }

    /// <summary>File format version; bumped when a migration is needed on load.</summary>
    public int SchemaVersion { get => _schemaVersion; set => Set(ref _schemaVersion, value); }

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
    public ToneConfig Tone { get; init; } = new();
    public LooksConfig LooksAndCues { get; init; } = new();
    public WebConfig Web { get; init; } = new();
    public TransitionConfig Transition { get; init; } = new();
    public PresenterConfig Presenter { get; init; } = new();
    public AudioPlayerConfig AudioPlayer { get; init; } = new();
    public ControlConfig Control { get; init; } = new();
    public StingerConfig Stingers { get; init; } = new();
    public WatchdogConfig Watchdog { get; init; } = new();
    public StreamConfig Stream { get; init; } = new();
    public AdminConfig Admin { get; init; } = new();
    public SwitcherConfig Switcher { get; init; } = new();

    /// <summary>Operator nicknames for live inputs, keyed "ndi:&lt;source&gt;" / "cap:&lt;device&gt;".</summary>
    public ObservableCollection<InputLabelConfig> InputLabels { get; init; } = new();

    /// <summary>The nickname for an input key, or the fallback when none is set.</summary>
    public string InputLabel(string key, string fallback)
    {
        foreach (var l in InputLabels)
        {
            if (l.Key == key && l.Label.Length > 0) return l.Label;
        }
        return fallback;
    }

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
