using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Patterns.Core.Model;

public sealed class OutputConfig : Observable
{
    private bool _topmost = true;
    private bool _hideCursor = true;
    private int _masterFps;

    public bool Topmost { get => _topmost; set => Set(ref _topmost, value); }
    public bool HideCursor { get => _hideCursor; set => Set(ref _hideCursor, value); }

    /// <summary>
    /// The show's frame rate: every output presents at this rate (a screen may override it), an
    /// NDI sender set to "master" sends at it, and the stream can follow it. 0 = every display's
    /// own refresh, as before.
    /// </summary>
    public int MasterFps { get => _masterFps; set => Set(ref _masterFps, Math.Clamp(value, 0, 240)); }

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

    private string _virtual = "";

    /// <summary>
    /// The feed this screen is the picture of — "ndi:&lt;sender id&gt;" or "stream" — or "" for a
    /// display. A virtual screen is planned (sized from the model, never opens a window) but
    /// owned: it follows its feed's size, is never adopted onto a display, never joins a canvas
    /// by touching one, and goes when its feed goes. It takes content like any screen — its own
    /// look, or the program.
    /// </summary>
    public string Virtual
    {
        get => _virtual;
        set
        {
            if (Set(ref _virtual, value ?? ""))
            {
                Raise(nameof(IsVirtual));
                Raise(nameof(IsPlannedDisplay));
                Raise(nameof(VirtualKind));
            }
        }
    }

    [JsonIgnore]
    public bool IsVirtual => _virtual.Length > 0;

    /// <summary>Planned and waiting for a display — the adoption list; a virtual screen never is.</summary>
    [JsonIgnore]
    public bool IsPlannedDisplay => _planned && _virtual.Length == 0;

    /// <summary>"NDI" or "STREAM" for a virtual screen, "" otherwise.</summary>
    [JsonIgnore]
    public string VirtualKind => _virtual.StartsWith("ndi:", StringComparison.Ordinal) ? "NDI" : _virtual.Length > 0 ? "STREAM" : "";

    private string _adoptTargetId = "";

    /// <summary>Runtime-only: the display chosen in the adopt picker for this planned screen.</summary>
    [JsonIgnore]
    public string AdoptTargetId { get => _adoptTargetId; set => Set(ref _adoptTargetId, value); }

    private bool _directOutput;

    /// <summary>
    /// Bypass the desktop compositor on this output: the window is handed straight to its
    /// display by Windows' flip path (no composition frame, no compositor jitter) when a
    /// hardware card drives the show and the window covers the display alone. The swap chain
    /// that makes it possible is chosen when Patterns starts, so a change takes the next start.
    /// </summary>
    public bool DirectOutput { get => _directOutput; set => Set(ref _directOutput, value); }

    /// <summary>Physical rotation — content is pre-rotated so a rotated display reads upright.</summary>
    public OutputRotation Rotation { get => _rotation; set => Set(ref _rotation, value); }

    private int _fpsOverride;

    /// <summary>This output's own frame rate; 0 = the show's master rate (or the display's refresh when that is 0 too).</summary>
    public int FpsOverride { get => _fpsOverride; set => Set(ref _fpsOverride, Math.Clamp(value, 0, 240)); }
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

    // Edge blend: the soft fade a projector's picture gets along the edges it shares with a
    // neighbour, so two overlapping projectors read as one picture. Automatic takes the widths
    // from the arrangement's overlaps (and lets this screen overlap its neighbours to join a
    // canvas); manual widths are for a rig measured by hand.
    private bool _blendAuto;
    private int _blendLeftPx; private int _blendTopPx; private int _blendRightPx; private int _blendBottomPx;
    private BlendCurve _blendCurve = BlendCurve.SCurve;
    private double _blendGamma = 1.0;

    /// <summary>Blend widths follow the overlaps in the arrangement; overlapping this screen joins the canvas.</summary>
    public bool BlendAuto { get => _blendAuto; set => Set(ref _blendAuto, value); }
    public int BlendLeftPx { get => _blendLeftPx; set => Set(ref _blendLeftPx, Math.Clamp(value, 0, 4096)); }
    public int BlendTopPx { get => _blendTopPx; set => Set(ref _blendTopPx, Math.Clamp(value, 0, 4096)); }
    public int BlendRightPx { get => _blendRightPx; set => Set(ref _blendRightPx, Math.Clamp(value, 0, 4096)); }
    public int BlendBottomPx { get => _blendBottomPx; set => Set(ref _blendBottomPx, Math.Clamp(value, 0, 4096)); }
    /// <summary>The fade's shape across the zone (the same curves the Projection blend pattern draws).</summary>
    public BlendCurve BlendCurve { get => _blendCurve; set => Set(ref _blendCurve, value); }
    /// <summary>Compensates the projectors' gamma so the two ramps add up to flat light; 1 = the raw curve.</summary>
    public double BlendGamma { get => _blendGamma; set => Set(ref _blendGamma, Math.Clamp(value, 0.5, 3.0)); }

    public bool HasBlend => _blendAuto || _blendLeftPx > 0 || _blendTopPx > 0 || _blendRightPx > 0 || _blendBottomPx > 0;
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
    private MessageBackground _background = MessageBackground.Auto;
    private double _backgroundStrength = 0.7;
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

    /// <summary>What sits behind the text: the original chip-or-nothing, nothing, a solid band, or a soft fade.</summary>
    public MessageBackground Background { get => _background; set => Set(ref _background, value); }

    /// <summary>Peak opacity of the chip or fade band (0.1–1). Auto ignores it and keeps the theme chip.</summary>
    public double BackgroundStrength { get => _backgroundStrength; set => Set(ref _backgroundStrength, Math.Clamp(value, 0.1, 1)); }
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
    private double _cropLeftPct; private double _cropTopPct; private double _cropRightPct; private double _cropBottomPct;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public PipSource Source { get => _source; set => Set(ref _source, value); }

    /// <summary>Cut this share of the feed away on each side (0–45 %): the inset shows the rest at its cropped shape.</summary>
    public double CropLeftPct { get => _cropLeftPct; set => Set(ref _cropLeftPct, Math.Clamp(value, 0, 45)); }
    public double CropTopPct { get => _cropTopPct; set => Set(ref _cropTopPct, Math.Clamp(value, 0, 45)); }
    public double CropRightPct { get => _cropRightPct; set => Set(ref _cropRightPct, Math.Clamp(value, 0, 45)); }
    public double CropBottomPct { get => _cropBottomPct; set => Set(ref _cropBottomPct, Math.Clamp(value, 0, 45)); }
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
    /// <summary>
    /// Empty = program; otherwise the screen or joined canvas (member key) whose pattern this
    /// sender mirrors — or its <see cref="OwnScreenId"/>, the sender's own screen with a look of its own.
    /// </summary>
    public string SourceScreenId
    {
        get => _sourceScreenId;
        set
        {
            if (Set(ref _sourceScreenId, value ?? "")) Raise(nameof(UsesOwnScreen));
        }
    }

    /// <summary>Every sender owns a virtual screen on the rig; this is its id.</summary>
    [JsonIgnore]
    public string OwnScreenId => OwnScreenIdFor(_id);

    public static string OwnScreenIdFor(string senderId) => "ndi:" + senderId;

    /// <summary>The sender shows its own screen's look rather than mirroring another target.</summary>
    [JsonIgnore]
    public bool UsesOwnScreen => _sourceScreenId == OwnScreenId;
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
    private string _musicItemId = "";

    /// <summary>What "pause break music" looks like in <see cref="MusicItemId"/>.</summary>
    public const string PauseMusic = "pause";

    /// <summary>Stable identity (schema 4): renaming a look never breaks what references it.</summary>
    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>1–12 → F1–F12; 0 = no hotkey.</summary>
    public int Hotkey { get => _hotkey; set => Set(ref _hotkey, Math.Clamp(value, 0, 12)); }
    /// <summary>The captured state, stored as an opaque JSON blob (LookData).</summary>
    public string Json { get => _json; set => Set(ref _json, value); }

    /// <summary>
    /// Break music this look starts when it goes on air: empty leaves the music alone,
    /// <see cref="PauseMusic"/> pauses it, anything else is a break-music entry's id (Audio page).
    /// Loading a look into the preview never touches the music. A null (a picker that lost its
    /// items writes one) keeps the choice: only "" means "leave the music alone".
    /// </summary>
    public string MusicItemId { get => _musicItemId; set => Set(ref _musicItemId, value ?? _musicItemId); }

    private bool _isOnAir;
    private bool _isInPreview;
    private string _tallyText = "";

    /// <summary>Runtime-only tally: this look is the picture on air (exactly, or edited since — see <see cref="TallyText"/>).</summary>
    [JsonIgnore]
    public bool IsOnAir
    {
        get => _isOnAir;
        set
        {
            if (Set(ref _isOnAir, value)) Raise(nameof(HasTally));
        }
    }

    /// <summary>Runtime-only tally: this look was loaded into the sandboxed preview and is what the operator is building on.</summary>
    [JsonIgnore]
    public bool IsInPreview
    {
        get => _isInPreview;
        set
        {
            if (Set(ref _isInPreview, value)) Raise(nameof(HasTally));
        }
    }

    /// <summary>"PROGRAM", "PROGRAM · EDITED", "PREVIEW", "PREVIEW · EDITED", both, or "" — what the tally chip reads.</summary>
    [JsonIgnore]
    public string TallyText { get => _tallyText; set => Set(ref _tallyText, value ?? ""); }

    [JsonIgnore]
    public bool HasTally => _isOnAir || _isInPreview;
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

/// <summary>The lip-sync offset of one audio output device: its sound leaves this much later.</summary>
public sealed class OutputDelayConfig : Observable
{
    private string _device = "";
    private int _delayMs;

    /// <summary>The device's friendly name, or the computer-output key.</summary>
    public string Device { get => _device; set => Set(ref _device, value ?? ""); }

    /// <summary>0–2000 ms.</summary>
    public int DelayMs { get => _delayMs; set => Set(ref _delayMs, Math.Clamp(value, 0, 2000)); }
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

    /// <summary>
    /// When armed, clicker keys (and remote NEXT/PREV) drive the clicker list. A runtime chip:
    /// a show always opens with the clicker disarmed, so nothing fires until someone arms it.
    /// </summary>
    [JsonIgnore]
    public bool Armed { get => _armed; set => Set(ref _armed, value); }

    /// <summary>Kept for files from before schema 5; the clicker list's LoopAtEnd carries it now.</summary>
    public bool Loop { get => _loop; set => Set(ref _loop, value); }

    /// <summary>Kept for files from before schema 5; migrated into the clicker list on load.</summary>
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

    private bool _syncLock = true;
    private int _videoAudioDelayMs;

    /// <summary>
    /// Lock every output to the master clock: each device's sound is resampled by the drift its
    /// clock shows against the show's, so a two-hour track ends when the pictures do. Off, the
    /// devices free-run as they used to.
    /// </summary>
    public bool SyncLock { get => _syncLock; set => Set(ref _syncLock, value); }

    /// <summary>The lip-sync offset of every video clip's soundtrack (libVLC), −1000–2000 ms; negative plays the sound earlier.</summary>
    public int VideoAudioDelayMs { get => _videoAudioDelayMs; set => Set(ref _videoAudioDelayMs, Math.Clamp(value, -1000, 2000)); }

    /// <summary>Per-output lip-sync offsets: the track, VOGs and stingers on that device leave this much later.</summary>
    public ObservableCollection<OutputDelayConfig> OutputDelays { get; init; } = new();

    /// <summary>The delay set for a device (its friendly name or the computer-output key), 0 when none.</summary>
    public int DelayFor(string device)
        => OutputDelays.FirstOrDefault(d => string.Equals(d.Device, device, StringComparison.OrdinalIgnoreCase))?.DelayMs ?? 0;

    /// <summary>Sets a device's delay; 0 removes the entry.</summary>
    public void SetDelay(string device, int ms)
    {
        var entry = OutputDelays.FirstOrDefault(d => string.Equals(d.Device, device, StringComparison.OrdinalIgnoreCase));
        if (ms <= 0)
        {
            if (entry is not null) OutputDelays.Remove(entry);
            return;
        }
        if (entry is null) OutputDelays.Add(new OutputDelayConfig { Device = device, DelayMs = ms });
        else entry.DelayMs = ms;
    }
}

/// <summary>
/// What a library item is. <c>Vog</c> is first on purpose: an unknown value written by a newer
/// build lands here through the tolerant enum converter in <see cref="Services.JsonUtil"/>, and a
/// VOG is exactly what every stinger did before the split — sound over everything, content carries on.
/// </summary>
public enum StingerKind
{
    /// <summary>Voice of God: plays over the show. The music ducks; a clip takes the screens and the content comes back.</summary>
    Vog,

    /// <summary>A transition hit: the music fades out instead of ducking, and an after-policy runs when it lands.</summary>
    Sting,
}

/// <summary>
/// What the show does when a stinger lands. <c>Return</c> is first on purpose: it is today's
/// behaviour and the safe landing for any value this build does not understand — the show comes
/// back rather than holding on a dead frame.
/// </summary>
public enum StingerAfter
{
    /// <summary>The content that was on air when the sting started comes back.</summary>
    Return,

    /// <summary>Hold the sting on the screens until the operator TAKEs the preview or GOes a cue.</summary>
    Manual,

    /// <summary>GO a cue list — <see cref="StingerItemConfig.AfterTarget"/> names it; blank = the caller's stack.</summary>
    Next,

    /// <summary>Apply a named look, or fire a named cue — <see cref="StingerItemConfig.AfterTarget"/> is its id.</summary>
    Custom,
}

/// <summary>One library item: a sound or clip fired over the show with a single press — a VOG or a stinger.</summary>
public sealed class StingerItemConfig : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "";
    private string _path = "";
    private double _volumePct = 100;
    private StingerKind _kind = StingerKind.Vog;
    private StingerAfter _after = StingerAfter.Return;
    private string _afterTarget = "";
    private bool _musicReturns = true;
    private StingerSource _source = StingerSource.File;
    private PulsePreset _pulsePreset = PulsePreset.Explosion;
    private int _pulseMs = 900;

    /// <summary>Stable identity (schema 4) — names fall back to file names and need not be unique.</summary>
    public string Id { get => _id; set => Set(ref _id, value); }

    /// <summary>A sound or clip on disk, or an effect pulse — a surge through the particles and fractals on screen.</summary>
    public StingerSource Source
    {
        get => _source;
        set
        {
            if (Set(ref _source, value))
            {
                Raise(nameof(IsPulse));
                Raise(nameof(IsFile));
                Raise(nameof(IsSting));
                Raise(nameof(KindLabel));
                Raise(nameof(DisplayName));
            }
        }
    }

    /// <summary>Pulse only: its shape.</summary>
    public PulsePreset PulsePreset
    {
        get => _pulsePreset;
        set
        {
            if (Set(ref _pulsePreset, value)) Raise(nameof(DisplayName));
        }
    }

    /// <summary>Pulse only: how long the surge runs before the picture settles, 100 – 5000 ms.</summary>
    public int PulseMs { get => _pulseMs; set => Set(ref _pulseMs, Math.Clamp(value, 100, 5000)); }

    [JsonIgnore]
    public bool IsPulse => _source == StingerSource.EffectPulse;

    [JsonIgnore]
    public bool IsFile => _source == StingerSource.File;

    private bool _isOnAir;
    private string _onAirText = "";
    private double _onAirProgress = -1;

    /// <summary>Runtime-only tally: this item is playing right now — its sound, its clip, its held frame, or its surge.</summary>
    [JsonIgnore]
    public bool IsOnAir { get => _isOnAir; set => Set(ref _isOnAir, value); }

    /// <summary>"ON AIR · 12 s", "HOLDING", "SURGING · 0.4 s left", or "" — what the row's chip reads.</summary>
    [JsonIgnore]
    public string OnAirText { get => _onAirText; set => Set(ref _onAirText, value ?? ""); }

    /// <summary>A surge's progress, 0–1; −1 when there is no bar to show (a sound or clip has no known end here).</summary>
    [JsonIgnore]
    public double OnAirProgress
    {
        get => _onAirProgress;
        set
        {
            if (Set(ref _onAirProgress, value)) Raise(nameof(ShowsProgress));
        }
    }

    [JsonIgnore]
    public bool ShowsProgress => _onAirProgress >= 0;

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

    /// <summary>VOG (over the show) or Stinger (a transition hit). New and migrated items are VOGs.</summary>
    public StingerKind Kind
    {
        get => _kind;
        set
        {
            if (Set(ref _kind, value))
            {
                Raise(nameof(IsSting));
                Raise(nameof(KindLabel));
                Raise(nameof(AfterKey));
            }
        }
    }

    /// <summary>Stinger only: what the show does when it lands. A VOG never reads this.</summary>
    public StingerAfter After
    {
        get => _after;
        set
        {
            if (Set(ref _after, value)) Raise(nameof(AfterKey));
        }
    }

    /// <summary>
    /// Next → a cue-list id (blank = the caller's stack). Custom → a look id or a cue id. A cleared
    /// picker writes null; that becomes empty rather than a stored null.
    /// </summary>
    public string AfterTarget
    {
        get => _afterTarget;
        set
        {
            if (Set(ref _afterTarget, value ?? "")) Raise(nameof(AfterKey));
        }
    }

    /// <summary>Changes whenever the kind, the policy or the target does: what the Audio page's read-back line binds to.</summary>
    [JsonIgnore]
    public string AfterKey => $"{_id}|{_kind}|{_after}|{_afterTarget}";

    /// <summary>Stinger only: the music fades back up when it lands. Off = the track stops.</summary>
    public bool MusicReturns { get => _musicReturns; set => Set(ref _musicReturns, value); }

    /// <summary>Row-level panel visibility in XAML without a converter: a stinger file, whose ending is the operator's to choose. A pulse has no ending.</summary>
    [JsonIgnore]
    public bool IsSting => _kind == StingerKind.Sting && _source == StingerSource.File;

    /// <summary>The tag beside a fire button and in the cue picker.</summary>
    [JsonIgnore]
    public string KindLabel => _source == StingerSource.EffectPulse ? "PULSE" : _kind == StingerKind.Sting ? "STING" : "VOG";

    /// <summary>What fire buttons show. Splits both separators — show files travel between OSes. A nameless pulse is named by its shape.</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (_name.Length > 0) return _name;
            if (_source == StingerSource.EffectPulse) return $"{_pulsePreset} pulse";
            var cut = _path.LastIndexOfAny(new[] { '/', '\\' });
            return cut >= 0 ? _path[(cut + 1)..] : _path;
        }
    }
}

/// <summary>
/// The library of one-press sounds and clips — announcements and transition hits anyone can fire
/// without touching the audio desk. A VOG plays over everything (the music track ducks underneath)
/// and a VOG clip hands the screens back when it ends; a stinger fades the music out instead and
/// runs its after-policy when it lands. One collection and one numbering for both kinds, so
/// "STINGER 3", a saved Companion preset and a cue target never change meaning.
/// </summary>
public sealed class StingerConfig : Observable
{
    private double _duckPct = 20;
    private int _fadeMs = 400;
    private int _stopFadeMs = 200;
    private int _holdSeconds;   // 0 = hold until the operator takes it
    private string _playingName = "";
    private double _duckToPct = 10;
    private int _duckFadeMs = 300;
    private bool _duckActive;

    public ObservableCollection<StingerItemConfig> Items { get; init; } = new();

    /// <summary>Music-track level (as % of its own volume) while a <em>VOG sound</em> plays. A stinger fades the music out instead.</summary>
    public double DuckPct { get => _duckPct; set => Set(ref _duckPct, Math.Clamp(value, 0, 100)); }

    /// <summary>How fast the music fades out under a stinger (and back afterwards), and the crossfade into a clip. 0 = a hard cut.</summary>
    public int FadeMs { get => _fadeMs; set => Set(ref _fadeMs, Math.Clamp(value, 0, 2000)); }

    /// <summary>How a stopped or superseded sound or clip leaves the air: a fade to silence over this long, never a cut (50–1000 ms).</summary>
    public int StopFadeMs { get => _stopFadeMs; set => Set(ref _stopFadeMs, Math.Clamp(value, 50, 1000)); }

    /// <summary>A held stinger gives the show back by itself after this long. 0 (the default) = hold until you take it.</summary>
    public int HoldSeconds { get => _holdSeconds; set => Set(ref _holdSeconds, Math.Clamp(value, 0, 600)); }

    /// <summary>Runtime-only: name of the stinger on air ("" = none).</summary>
    [JsonIgnore]
    public string PlayingName { get => _playingName; set => Set(ref _playingName, value); }

    /// <summary>The live duck: what everything but a VOG holds at (as % of its own level) while an announcement is made from the room.</summary>
    public double DuckToPct { get => _duckToPct; set => Set(ref _duckToPct, Math.Clamp(value, 0, 100)); }

    /// <summary>How fast the live duck goes down and comes back (0 = a step).</summary>
    public int DuckFadeMs { get => _duckFadeMs; set => Set(ref _duckFadeMs, Math.Clamp(value, 0, 2000)); }

    /// <summary>Runtime-only: the live duck is on. A restart never comes up ducked.</summary>
    [JsonIgnore]
    public bool DuckActive { get => _duckActive; set => Set(ref _duckActive, value); }
}

/// <summary>One entry in the break-music library: a Spotify playlist, album or track by URI.</summary>
public sealed class SpotifyItemConfig : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "";
    private string _uri = "";
    private bool _shuffle;

    /// <summary>Stable identity — names need not be unique (the <see cref="StingerItemConfig"/> rule).</summary>
    public string Id { get => _id; set => Set(ref _id, value); }

    /// <summary>Button label ("Interval bed"); empty = what the link says it is.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value)) Raise(nameof(DisplayName));
        }
    }

    /// <summary>Canonical "spotify:playlist:ID" / "spotify:album:ID" / "spotify:track:ID" / "spotify:artist:ID".</summary>
    public string Uri
    {
        get => _uri;
        set
        {
            if (Set(ref _uri, value))
            {
                Raise(nameof(DisplayName));
                Raise(nameof(KindLabel));
            }
        }
    }

    /// <summary>Shuffle the context when this plays (a playlist of beds, not a running order).</summary>
    public bool Shuffle { get => _shuffle; set => Set(ref _shuffle, value); }

    /// <summary>What fire buttons show.</summary>
    [JsonIgnore]
    public string DisplayName => _name.Length > 0 ? _name : Services.SpotifyUri.Describe(_uri);

    /// <summary>LIST / ALBUM / SONG / ARTIST / "" — the row's kind chip, like the stinger's media kind.</summary>
    [JsonIgnore]
    public string KindLabel => Services.SpotifyUri.TryParse(_uri, out var r) ? r.KindLabel : "";
}

/// <summary>
/// Break music: Spotify playing the room between the show's own content. Patterns does not decode
/// Spotify audio (DRM forbids it) — it drives the Spotify app on this machine, or any Spotify
/// Connect device, through the Web API. Premium and the operator's own Client ID are required; the
/// Audio page says so plainly. Sound only: nothing here is sandboxed and nothing here rides the
/// snapshot. Off until switched on.
/// </summary>
public sealed class SpotifyConfig : Observable
{
    private bool _enabled;
    private double _levelPct = 60;
    private string _deviceName = "";
    private bool _playing;
    private string _playingId = "";

    /// <summary>Off by default: a show that does not use break music behaves exactly as before.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public ObservableCollection<SpotifyItemConfig> Items { get; init; } = new();

    /// <summary>The Spotify device's own volume, 0–100 (Spotify's range, not the file player's 0–125).</summary>
    public double LevelPct { get => _levelPct; set => Set(ref _levelPct, Math.Clamp(value, 0, 100)); }

    /// <summary>Preferred Connect device by name (ids rotate every Spotify session); empty = whichever is active.</summary>
    public string DeviceName { get => _deviceName; set => Set(ref _deviceName, value ?? ""); }

    /// <summary>Runtime-only intent: break music never auto-starts with the app.</summary>
    [JsonIgnore]
    public bool Playing { get => _playing; set => Set(ref _playing, value); }

    /// <summary>Runtime-only: the library entry last asked for ("" = resume whatever is loaded).</summary>
    [JsonIgnore]
    public string PlayingId { get => _playingId; set => Set(ref _playingId, value); }
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

    /// <summary>
    /// What is streamed: "" = the first enabled display, captured off the desktop; a display's id,
    /// captured the same way; <see cref="OwnScreenId"/> = the stream's own screen, rendered by the
    /// engine with a look of its own; a joined canvas key, rendered by the engine.
    /// </summary>
    public string SourceScreenId
    {
        get => _sourceScreenId;
        set
        {
            if (Set(ref _sourceScreenId, value ?? _sourceScreenId)) Raise(nameof(UsesOwnScreen));
        }
    }

    /// <summary>The stream's own virtual screen on the rig — present while the stream is set to it.</summary>
    public const string OwnScreenId = "stream:own";

    [JsonIgnore]
    public bool UsesOwnScreen => _sourceScreenId == OwnScreenId;
    public int Width { get => _width; set => Set(ref _width, Math.Clamp(value, 320, 3840)); }
    public int Height { get => _height; set => Set(ref _height, Math.Clamp(value, 180, 2160)); }
    public int Fps { get => _fps; set => Set(ref _fps, Math.Clamp(value, 10, 60)); }

    private bool _fpsFollowsMaster;

    /// <summary>Encode at the show's master frame rate (clamped to 10–60) instead of <see cref="Fps"/>.</summary>
    public bool FpsFollowsMaster { get => _fpsFollowsMaster; set => Set(ref _fpsFollowsMaster, value); }

    public int VideoKbps { get => _videoKbps; set => Set(ref _videoKbps, Math.Clamp(value, 500, 20000)); }
    public int AudioKbps { get => _audioKbps; set => Set(ref _audioKbps, Math.Clamp(value, 64, 320)); }

    /// <summary>Optional DirectShow audio capture device name ("" = video only).</summary>
    public string AudioDevice { get => _audioDevice; set => Set(ref _audioDevice, value); }

    private int _audioDelayMs;

    /// <summary>The stream's lip-sync offset, −1000–2000 ms: the sound is held back (or brought forward) against the picture in the encode.</summary>
    public int AudioDelayMs { get => _audioDelayMs; set => Set(ref _audioDelayMs, Math.Clamp(value, -1000, 2000)); }

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

/// <summary>
/// How the desk is laid out: the dividers the operator dragged (the page column's width, how the
/// PROGRAM and PREVIEW panes share their column) and whether the page has the room with the
/// screens reduced to a strip on the right. Travels with the show file; absent in an older file,
/// so every value has the classic layout as its default.
/// </summary>
public sealed class DeskLayoutConfig : Observable
{
    public const double DefaultEditorWidth = 470;
    public const double MinEditorWidth = 360;
    public const double MaxEditorWidth = 1400;
    public const double DefaultProgramShare = 0.4;
    public const double MinProgramShare = 0.2;
    public const double MaxProgramShare = 0.8;

    /// <summary>The screens column never goes narrower than this in the classic layout — the wall's TAKE stays on screen.</summary>
    public const double MinScreensWidth = 420;

    /// <summary>The screens column's width with the work area wide: the wall and the panes, reduced.</summary>
    public const double WideScreensWidth = 300;

    private double _editorWidth = DefaultEditorWidth;
    private double _programShare = DefaultProgramShare;
    private bool _wideWorkArea;
    private bool _showHints;

    /// <summary>The page column's width in pixels (the divider between the page and the screens).</summary>
    public double EditorWidth
    {
        get => _editorWidth;
        set => Set(ref _editorWidth, Math.Clamp(double.IsFinite(value) ? value : DefaultEditorWidth, MinEditorWidth, MaxEditorWidth));
    }

    /// <summary>How much of the screens column's flexible height the PROGRAM pane takes; the PREVIEW pane takes the rest.</summary>
    public double ProgramShare
    {
        get => _programShare;
        set => Set(ref _programShare, Math.Clamp(double.IsFinite(value) ? value : DefaultProgramShare, MinProgramShare, MaxProgramShare));
    }

    /// <summary>The page takes the room; the screens shrink to a strip on the right.</summary>
    public bool WideWorkArea { get => _wideWorkArea; set => Set(ref _wideWorkArea, value); }

    /// <summary>
    /// The pages' explanations shown inline. Off (the default) they live behind ? TIPS on the
    /// page strip, and the room goes to the controls.
    /// </summary>
    public bool ShowHints { get => _showHints; set => Set(ref _showHints, value); }
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
    private bool _remotesMayArm;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public int HttpPort { get => _httpPort; set => Set(ref _httpPort, Math.Clamp(value, 1024, 65535)); }
    public int TcpPort { get => _tcpPort; set => Set(ref _tcpPort, Math.Clamp(value, 1024, 65535)); }

    /// <summary>A remote may ARM / disarm the caller's cue stack (CUE ARM ON / OFF). Off by default: arming is a deliberate act at the desk.</summary>
    public bool RemotesMayArm { get => _remotesMayArm; set => Set(ref _remotesMayArm, value); }
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

/// <summary>The mode a capture device opens in — what the card's driver calls a stream capability.</summary>
public sealed class CaptureFormatConfig : Observable
{
    private string _device = "";
    private string _format = "";

    /// <summary>The DirectShow friendly name.</summary>
    public string Device { get => _device; set => Set(ref _device, value ?? ""); }

    /// <summary>"1920x1080@60" — a <c>CaptureFormat</c> key; empty means the device's default.</summary>
    public string Format { get => _format; set => Set(ref _format, value ?? ""); }
}

/// <summary>Root of everything the operator can configure. Serialized as the portable settings/show file.</summary>
public sealed class ShowState : Observable
{
    public const int CurrentSchemaVersion = 7;

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

    /// <summary>The caller's cue stack and the speaker's clicker list (schema 5); see <see cref="Services.CueStacks"/>.</summary>
    public ObservableCollection<CueStackConfig> Stacks { get; init; } = new();
    public AudioPlayerConfig AudioPlayer { get; init; } = new();
    public ControlConfig Control { get; init; } = new();
    public StingerConfig Stingers { get; init; } = new();

    /// <summary>Break music (Spotify) — background sound between the show's own content.</summary>
    public SpotifyConfig Spotify { get; init; } = new();

    public WatchdogConfig Watchdog { get; init; } = new();
    public StreamConfig Stream { get; init; } = new();
    public AdminConfig Admin { get; init; } = new();
    public SwitcherConfig Switcher { get; init; } = new();
    public DeskLayoutConfig Desk { get; init; } = new();

    /// <summary>The lower thirds: the designs, and the one on air since when.</summary>
    public global::Patterns.Core.LowerThirds.LowerThirdsConfig LowerThirds { get; init; } = new();

    /// <summary>Operator nicknames for live inputs, keyed "ndi:&lt;source&gt;" / "cap:&lt;device&gt;".</summary>
    public ObservableCollection<InputLabelConfig> InputLabels { get; init; } = new();

    /// <summary>The mode each capture device opens in ("1920x1080@60"), by device name; absent = the device's default.</summary>
    public ObservableCollection<CaptureFormatConfig> CaptureFormats { get; init; } = new();

    /// <summary>The stored mode key for a capture device, or "" for the device's default.</summary>
    public string CaptureFormatFor(string device)
    {
        foreach (var f in CaptureFormats)
        {
            if (string.Equals(f.Device, device, StringComparison.OrdinalIgnoreCase)) return f.Format;
        }
        return "";
    }

    /// <summary>Sets (or clears, with "") the mode a capture device opens in.</summary>
    public void SetCaptureFormat(string device, string format)
    {
        if (string.IsNullOrWhiteSpace(device)) return;
        var existing = CaptureFormats.FirstOrDefault(f => string.Equals(f.Device, device, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(format))
        {
            if (existing is not null) CaptureFormats.Remove(existing);
            return;
        }
        if (existing is null) CaptureFormats.Add(new CaptureFormatConfig { Device = device, Format = format });
        else existing.Format = format;
    }

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
    private string _id = Guid.NewGuid().ToString("N");
    private string _path = "";
    private bool _isVideo;
    private LibraryMediaKind _kind;
    private string _name = "";
    private DateTime _addedUtc;

    /// <summary>Stable identity (schema 7): thumbnails and the Library page key on it, never on the file name.</summary>
    public string Id { get => _id; set => Set(ref _id, value); }

    public string Path { get => _path; set => Set(ref _path, value); }

    /// <summary>Decoded by libVLC (a video or an audio file) rather than shown as a picture. Kept for older files; <see cref="Kind"/> is the finer truth.</summary>
    public bool IsVideo { get => _isVideo; set => Set(ref _isVideo, value); }

    /// <summary>Image, video or audio (schema 7; derived from the path for older files).</summary>
    public LibraryMediaKind Kind { get => _kind; set => Set(ref _kind, value); }

    /// <summary>An optional operator name; empty = the file name.</summary>
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }

    public DateTime AddedUtc { get => _addedUtc; set => Set(ref _addedUtc, value); }

    [JsonIgnore]
    public string DisplayName => _name.Length > 0 ? _name : System.IO.Path.GetFileName(_path);

    /// <summary>What a path is, by its extension — the decoded flag decides only when the extension says nothing.</summary>
    public static LibraryMediaKind KindOf(string path, bool isVideo)
    {
        if (Services.PlaylistSequencer.IsVideoPath(path)) return LibraryMediaKind.Video;
        if (Services.PlaylistSequencer.IsAudioPath(path)) return LibraryMediaKind.Audio;
        if (Services.PlaylistSequencer.IsMediaPath(path)) return LibraryMediaKind.Image; // a known picture extension
        return isVideo ? LibraryMediaKind.Video : LibraryMediaKind.Image;
    }
}
