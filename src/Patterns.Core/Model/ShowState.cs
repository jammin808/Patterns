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
    public ShowCollection<ScreenPlacement> Placements { get; init; } = new();

    /// <summary>Operator names for joined canvases ("Main wall"), keyed by their member set.</summary>
    public ShowCollection<CanvasNameConfig> CanvasNames { get; init; } = new();
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

    private int _seamGapX;
    private int _seamGapY;

    /// <summary>
    /// Bezel compensation for a wall of displays joined into this canvas: the dead width, in
    /// pixels, between two members side by side (both bezels plus any air), and between two
    /// members one above the other. Content is laid out as if the strips were there and each
    /// member shows its own real pixels, so a line across the wall is straight in the room.
    /// </summary>
    public int SeamGapX { get => _seamGapX; set => Set(ref _seamGapX, Math.Clamp(value, 0, 4096)); }
    public int SeamGapY { get => _seamGapY; set => Set(ref _seamGapY, Math.Clamp(value, 0, 4096)); }

    public static string KeyFor(IEnumerable<string> memberScreenIds)
        => string.Join('+', memberScreenIds.OrderBy(id => id, StringComparer.Ordinal));
}

/// <summary>
/// A strip of a screen's wall with no pixels behind it: the air between two LED pillars, the
/// bezels of a video-wall controller's displays packed side by side in one raster. It stands
/// before raster pixel <see cref="At"/> and is <see cref="Size"/> pixels (of the wall's own
/// pitch) wide. Content is laid out across the strip and the output leaves it out, so a picture
/// that crosses the gap is continuous in the room.
/// </summary>
public sealed class WallGap : Observable
{
    private GapAxis _axis = GapAxis.Vertical;
    private int _at = 960;
    private int _size = 100;

    public GapAxis Axis { get => _axis; set => Set(ref _axis, value); }

    /// <summary>The first raster pixel after the gap: a vertical gap's x, a horizontal gap's y.</summary>
    public int At { get => _at; set => Set(ref _at, Math.Clamp(value, 1, 16384)); }

    /// <summary>The gap's width in pixels — the physical gap measured in the wall's pixel pitch.</summary>
    public int Size { get => _size; set => Set(ref _size, Math.Clamp(value, 1, 16384)); }
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

    // ---- what the screen remembers of its display: hot-plug ------------------------------------

    private string _displayKey = "";
    private string _displayOrigin = "";
    private int _displayHz;
    private DateTime? _lostAtUtc;
    private bool _wasEnabled;
    private bool _wasPinned;

    /// <summary>The display last seen behind this screen — its name and size ("DELL U2415|1920x1080") — so a display that re-indexed, or a lost one that comes back, is known again.</summary>
    public string DisplayKey { get => _displayKey; set => Set(ref _displayKey, value ?? ""); }

    /// <summary>Where that display sat on the desktop ("1920,0"): the tie-break between two displays of one name and size.</summary>
    public string DisplayOrigin { get => _displayOrigin; set => Set(ref _displayOrigin, value ?? ""); }

    /// <summary>That display's refresh rate when it was known, 0 otherwise — a substitute must match it, or be forced to.</summary>
    public int DisplayHz { get => _displayHz; set => Set(ref _displayHz, Math.Max(0, value)); }

    /// <summary>When the display was unplugged — the screen then waits, planned and off, for it or a substitute; null while it has its display.</summary>
    public DateTime? LostAtUtc { get => _lostAtUtc; set => Set(ref _lostAtUtc, value); }

    /// <summary>Whether the screen was on when its display went — restored when the display, or a substitute, is adopted.</summary>
    public bool WasEnabled { get => _wasEnabled; set => Set(ref _wasEnabled, value); }

    /// <summary>Whether the operator had pinned the screen's on/off before it was lost.</summary>
    public bool WasPinned { get => _wasPinned; set => Set(ref _wasPinned, value); }

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

    private ScreenRole _role = ScreenRole.Main;
    private bool _followsCues = true;
    private string _mirrorOf = "";

    /// <summary>What the screen is for: the wall tile's badge, and the default for <see cref="FollowsCues"/>.</summary>
    public ScreenRole Role { get => _role; set => Set(ref _role, value); }

    /// <summary>
    /// Looks, cues, the clicker, TAKE / CUT to all and a stinger's takeover change this screen.
    /// Off — locked — it keeps its own picture until something is sent to it by name: a
    /// confidence monitor or an info screen that must not change on a cue.
    /// </summary>
    public bool FollowsCues { get => _followsCues; set => Set(ref _followsCues, value); }

    /// <summary>
    /// The target this screen duplicates — a screen id or a joined canvas's key — or "" for its
    /// own content. A repeater draws its source's picture wherever the source's picture goes.
    /// </summary>
    public string MirrorOf { get => _mirrorOf; set => Set(ref _mirrorOf, value ?? ""); }

    /// <summary>
    /// The dead strips inside this screen's picture — the gaps of an LED wall whose panels are
    /// packed side by side in the processor's raster, the bezels of a wall controller's
    /// displays — in the picture's own pixels as the room sees it (after rotation). Content is
    /// laid out across them and this output leaves them out. A member of a joined canvas adds
    /// its strips to the canvas's, beside the seams the canvas compensates itself.
    /// </summary>
    public ShowCollection<WallGap> Gaps { get; init; } = new();

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

    // ---- the edge bends: a curved screen or a lens's bow, on top of the keystone ----------------

    private int _warpTopBow;
    private int _warpRightBow;
    private int _warpBottomBow;
    private int _warpLeftBow;

    /// <summary>How far the top edge bows outward at its middle, in this output's pixels (negative bows inward).</summary>
    public int WarpTopBow { get => _warpTopBow; set => Set(ref _warpTopBow, Math.Clamp(value, -4096, 4096)); }
    public int WarpRightBow { get => _warpRightBow; set => Set(ref _warpRightBow, Math.Clamp(value, -4096, 4096)); }
    public int WarpBottomBow { get => _warpBottomBow; set => Set(ref _warpBottomBow, Math.Clamp(value, -4096, 4096)); }
    public int WarpLeftBow { get => _warpLeftBow; set => Set(ref _warpLeftBow, Math.Clamp(value, -4096, 4096)); }

    [JsonIgnore]
    public bool HasBend => _warpTopBow != 0 || _warpRightBow != 0 || _warpBottomBow != 0 || _warpLeftBow != 0;

    // ---- the mesh: a lattice of points pulled into place, for a dome, a set piece, a lens ----------

    private int _warpMeshColumns = 5;
    private int _warpMeshRows = 5;
    private string _warpMesh = "";

    /// <summary>The lattice's density; the offsets are resampled when it changes.</summary>
    public int WarpMeshColumns { get => _warpMeshColumns; set => Set(ref _warpMeshColumns, Math.Clamp(value, 2, 17)); }
    public int WarpMeshRows { get => _warpMeshRows; set => Set(ref _warpMeshRows, Math.Clamp(value, 2, 17)); }

    /// <summary>Each point's pull from its rest in this output's pixels — "dx,dy;dx,dy;…" row by row; "" is a lattice at rest.</summary>
    public string WarpMesh { get => _warpMesh; set => Set(ref _warpMesh, value ?? ""); }

    [JsonIgnore]
    public bool HasMesh => _warpMesh.Length > 0;

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

    private double _blendBlackPct;

    /// <summary>
    /// Black-level matching: the pedestal this projector adds outside its blend zones, as a
    /// percentage of white, so the picture between the zones sits on the same floor as the
    /// overlaps (two blacks bright) and a grid's corner (four). 0 = off. Found on a black test
    /// picture: slide it until the bands and the middle square go.
    /// </summary>
    public double BlendBlackPct { get => _blendBlackPct; set => Set(ref _blendBlackPct, Math.Clamp(value, 0, 25)); }

    private string _blendMaskPath = "";

    /// <summary>A blend mask a camera calibration made — a grey picture over this output's raster that multiplies its light — or "" for the zones alone. A masked output blends, so it joins the canvas its overlaps make.</summary>
    public string BlendMaskPath { get => _blendMaskPath; set => Set(ref _blendMaskPath, value ?? ""); }

    public bool HasBlend => _blendAuto || _blendLeftPx > 0 || _blendTopPx > 0 || _blendRightPx > 0 || _blendBottomPx > 0 || _blendMaskPath.Length > 0;

    /// <summary>The overlaps this screen has join it to a canvas: on automatic blend, or with a camera's mask.</summary>
    [JsonIgnore]
    public bool BlendsOverlaps => _blendAuto || _blendMaskPath.Length > 0;
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

/// <summary>
/// An overlay with a place: the anchor it is held to and a nudge from it as a share of the canvas.
/// The drag's re-anchoring and the pages' pixel fields work through this rather than through seven
/// near-identical switch arms.
/// </summary>
public interface IAnchored
{
    Anchor9 Anchor { get; set; }
    double OffsetXPct { get; set; }
    double OffsetYPct { get; set; }
}

public sealed class ClockOverlay : Observable, IAnchored
{
    private bool _enabled = false;
    private bool _twentyFourHour = true;
    private bool _showSeconds = true;
    private bool _showDate = true;
    private Anchor9 _anchor = Anchor9.TopRight;
    private double _sizePct = 8;
    private double _offsetXPct;
    private double _offsetYPct;

    /// <summary>A nudge from the anchor as a share of the canvas (−100..100) — a drag on the PREVIEW pane writes it.</summary>
    public double OffsetXPct { get => _offsetXPct; set => Set(ref _offsetXPct, Math.Clamp(value, -100, 100)); }
    public double OffsetYPct { get => _offsetYPct; set => Set(ref _offsetYPct, Math.Clamp(value, -100, 100)); }
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

public sealed class LogoOverlay : Observable, IAnchored
{
    private bool _enabled = false;
    private Anchor9 _anchor = Anchor9.BottomRight;
    private double _heightPct = 12;
    private double _offsetXPct;
    private double _offsetYPct;

    /// <summary>A nudge from the anchor as a share of the canvas (−100..100) — a drag on the PREVIEW pane writes it.</summary>
    public double OffsetXPct { get => _offsetXPct; set => Set(ref _offsetXPct, Math.Clamp(value, -100, 100)); }
    public double OffsetYPct { get => _offsetYPct; set => Set(ref _offsetYPct, Math.Clamp(value, -100, 100)); }
    private double _opacity = 0.9;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public Anchor9 Anchor { get => _anchor; set => Set(ref _anchor, value); }
    public double HeightPct { get => _heightPct; set => Set(ref _heightPct, Math.Clamp(value, 2, 100)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.05, 1)); }
}

/// <summary>
/// The Patterns badge: the app's own mark — the test-card icon, the PATTERNS wordmark and a line
/// under it, in the app's neon colours — drawn by the engine on every sink over a test pattern,
/// so a test card carries its maker's name at an expo or on a rig day. On by default, centred in
/// the lower third; it travels with looks like every overlay and drags on the PREVIEW pane. Off
/// for a client's media (video, images, decks, web pages) and the multiview unless asked.
/// </summary>
public sealed class BadgeOverlay : Observable, IAnchored
{
    public const string DefaultLine = "rig · playback · show control";

    /// <summary>
    /// What the line said before. A settings file already carries the words it was written with,
    /// so without this the maker's own line would never change on a machine that has run the app
    /// once — the change would land in the source and nowhere an operator can see it.
    /// <see cref="SettingsStore.Migrate"/> moves a line that is still exactly this to the current
    /// one, and leaves anything typed over it alone: a venue that put its address there keeps it.
    /// </summary>
    public const string LegacyLine = "Show display · test cards · playback";

    private bool _enabled = true;
    private Anchor9 _anchor = Anchor9.BottomCenter;
    private double _heightPct = 9;
    private double _opacity = 0.95;
    private double _offsetXPct;
    private double _offsetYPct = -12;
    private bool _onMediaToo;
    private bool _showLine = true;
    private string _line = DefaultLine;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public Anchor9 Anchor { get => _anchor; set => Set(ref _anchor, value); }

    /// <summary>The badge's height as a share of the canvas height (3–40 %); its width follows the words.</summary>
    public double HeightPct { get => _heightPct; set => Set(ref _heightPct, Math.Clamp(value, 3, 40)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.05, 1)); }

    /// <summary>A nudge from the anchor as a share of the canvas (−100..100) — a drag on the PREVIEW pane writes it. The default lifts it off the bottom edge into the middle of the lower third.</summary>
    public double OffsetXPct { get => _offsetXPct; set => Set(ref _offsetXPct, Math.Clamp(value, -100, 100)); }
    public double OffsetYPct { get => _offsetYPct; set => Set(ref _offsetYPct, Math.Clamp(value, -100, 100)); }

    /// <summary>Draw it over the operator's own content too — a video, image, deck, web page or a reactive scene; off, it keeps to test patterns.</summary>
    public bool OnMediaToo { get => _onMediaToo; set => Set(ref _onMediaToo, value); }

    /// <summary>The line under the name (<see cref="Line"/>); off, the badge is the icon and the name.</summary>
    public bool ShowLine { get => _showLine; set => Set(ref _showLine, value); }

    /// <summary>The words under the name — the app's own by default; a venue may put its address there.</summary>
    public string Line { get => _line; set => Set(ref _line, value ?? ""); }

    /// <summary>
    /// The badge goes on the app's own pictures and stays off everything else's.
    ///
    /// Every pattern this app draws carries it — the grids and the ramps, the particles, the
    /// fractals and the reactive scenes alike: they are all Patterns' own generated pictures, and
    /// a scene was wrong to be the one exception. What it stays off is content that is not the
    /// app's to sign: someone else's media (until they ask for it with <see cref="OnMediaToo"/>),
    /// and the multiview, which is a monitoring picture rather than a picture of the show.
    ///
    /// The test card is the third: it carries the mark inside itself, as part of the card, so the
    /// overlay on top of it would be a second one — and a card with two logos on it is a card
    /// somebody made a mistake on.
    /// </summary>
    public bool ShowsOn(PatternKind kind)
        => Enabled && kind != PatternKind.Multiview && kind != PatternKind.TestCard
           && (OnMediaToo || kind != PatternKind.Media);
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

public sealed class MessageOverlay : Observable, IAnchored
{
    private bool _enabled = false;
    private string _text = "WELCOME";
    private Anchor9 _anchor = Anchor9.BottomCenter;
    private double _sizePct = 6;
    private double _offsetXPct;
    private double _offsetYPct;

    /// <summary>A nudge from the anchor as a share of the canvas (−100..100) — a drag on the PREVIEW pane writes it; a ticker takes the vertical one.</summary>
    public double OffsetXPct { get => _offsetXPct; set => Set(ref _offsetXPct, Math.Clamp(value, -100, 100)); }
    public double OffsetYPct { get => _offsetYPct; set => Set(ref _offsetYPct, Math.Clamp(value, -100, 100)); }
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

/// <summary>
/// The weather chip: the hour, the rest of today or tomorrow for the venue, drawn by the engine
/// on every sink like the clock. What it looks like lives here (a look carries it); where the
/// venue is and where the forecast comes from live in <see cref="WeatherSettings"/> on the show.
/// </summary>
public sealed class WeatherOverlay : Observable, IAnchored
{
    private bool _enabled;
    private WeatherView _view = WeatherView.Now;
    private Anchor9 _anchor = Anchor9.TopLeft;
    private double _sizePct = 7;
    private double _offsetXPct;
    private double _offsetYPct;
    private double _opacity = 1.0;
    private bool _pill = true;
    private bool _showPlace = true;
    private bool _showDetail = true;
    private bool _showCredit = true;
    private string _textColor = "";

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public WeatherView View { get => _view; set => Set(ref _view, value); }
    public Anchor9 Anchor { get => _anchor; set => Set(ref _anchor, value); }

    /// <summary>The big figure's height as % of canvas height.</summary>
    public double SizePct { get => _sizePct; set => Set(ref _sizePct, Math.Clamp(value, 2, 40)); }

    /// <summary>A nudge from the anchor as a share of the canvas (−100..100) — a drag on the PREVIEW pane writes it.</summary>
    public double OffsetXPct { get => _offsetXPct; set => Set(ref _offsetXPct, Math.Clamp(value, -100, 100)); }
    public double OffsetYPct { get => _offsetYPct; set => Set(ref _offsetYPct, Math.Clamp(value, -100, 100)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.05, 1)); }

    /// <summary>Draw a translucent pill behind the chip.</summary>
    public bool Pill { get => _pill; set => Set(ref _pill, value); }

    /// <summary>The place's name over the figure.</summary>
    public bool ShowPlace { get => _showPlace; set => Set(ref _showPlace, value); }

    /// <summary>The line under the figure: the sky in words, the wind, the chance of rain, the hours.</summary>
    public bool ShowDetail { get => _showDetail; set => Set(ref _showDetail, value); }

    /// <summary>The source's credit in small type — both sources' licences ask for it.</summary>
    public bool ShowCredit { get => _showCredit; set => Set(ref _showCredit, value); }

    /// <summary>Text colour; empty = brand/theme text colour.</summary>
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }
}

/// <summary>
/// The venue and the forecast's source — the show's, not a look's: the place the operator
/// picked (a name, and the coordinates a search or a hand filled in), the units the audience
/// reads, which service answers, its key when one is needed, and how often it is asked.
/// </summary>
public sealed class WeatherSettings : Observable
{
    private string _place = "";
    private double _latitude;
    private double _longitude;
    private WeatherUnits _units = WeatherUnits.Celsius;
    private WeatherProvider _provider = WeatherProvider.MetNorway;
    private string _apiKey = "";
    private string _contact = "";
    private double _refreshMinutes = 20;

    /// <summary>The place as the audience reads it ("Manchester") — what a search filled in, or typed over.</summary>
    public string Place { get => _place; set => Set(ref _place, value ?? ""); }

    /// <summary>Decimal degrees; 0 / 0 together means "not set" (the Gulf of Guinea can be typed as 0.0001).</summary>
    public double Latitude { get => _latitude; set => Set(ref _latitude, Math.Clamp(value, -90, 90)); }
    public double Longitude { get => _longitude; set => Set(ref _longitude, Math.Clamp(value, -180, 180)); }

    public bool HasLocation => _latitude != 0 || _longitude != 0;

    public WeatherUnits Units { get => _units; set => Set(ref _units, value); }
    public WeatherProvider Provider { get => _provider; set => Set(ref _provider, value); }

    /// <summary>Open-Meteo's commercial key (blank = the free, non-commercial endpoint); MET Norway needs none.</summary>
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, (value ?? "").Trim()); }

    /// <summary>A contact (an email or a site) sent in the User-Agent — MET Norway asks who is calling.</summary>
    public string Contact { get => _contact; set => Set(ref _contact, (value ?? "").Trim()); }

    /// <summary>How often the forecast is asked for again; the services update about hourly, so ten minutes is the floor.</summary>
    public double RefreshMinutes { get => _refreshMinutes; set => Set(ref _refreshMinutes, Math.Clamp(value, 10, 24 * 60)); }
}

/// <summary>
/// How overlays, or a layer, arrive and leave (round 63): a fade by default, a cut, or a slide in
/// from the edge they sit at — over the show's transition time unless a time of their own is set.
/// Switched on live or by a TAKE that changes them alone, they used to pop; a TAKE that changes
/// the whole picture carries them in its own transition and this does not run again on top.
/// </summary>
public sealed class AppearanceConfig : Observable
{
    private AppearKind _kind = AppearKind.Fade;
    private double _durationMs;

    public AppearKind Kind { get => _kind; set => Set(ref _kind, value); }

    /// <summary>Milliseconds; 0 = the show's transition time.</summary>
    public double DurationMs { get => _durationMs; set => Set(ref _durationMs, Math.Clamp(double.IsFinite(value) ? value : 0, 0, 3000)); }
}

public sealed class OverlaySet : Observable
{
    /// <summary>How every overlay and the countdown arrive and leave.</summary>
    public AppearanceConfig Appear { get; init; } = new();
    public ClockOverlay Clock { get; init; } = new();
    public LogoOverlay Logo { get; init; } = new();
    /// <summary>The Patterns badge — the app's own mark on a test pattern; on by default, in every look like the rest.</summary>
    public BadgeOverlay Badge { get; init; } = new();
    public InfoOverlay Info { get; init; } = new();
    public MessageOverlay Message { get; init; } = new();
    public PipOverlay Pip { get; init; } = new();
    public WeatherOverlay Weather { get; init; } = new();
}

/// <summary>Picture-in-picture inset: a second live input composited over whatever is showing.</summary>
public sealed class PipOverlay : Observable, IAnchored
{
    private bool _enabled;
    private PipSource _source = PipSource.NdiFeed;
    private string _ndiSourceName = "";
    private string _captureDevice = "";
    private Anchor9 _anchor = Anchor9.BottomRight;
    private double _widthPct = 25;
    private double _offsetXPct;
    private double _offsetYPct;

    /// <summary>A nudge from the anchor as a share of the viewport (−100..100) — a drag on the PREVIEW pane writes it.</summary>
    public double OffsetXPct { get => _offsetXPct; set => Set(ref _offsetXPct, Math.Clamp(value, -100, 100)); }
    public double OffsetYPct { get => _offsetYPct; set => Set(ref _offsetYPct, Math.Clamp(value, -100, 100)); }
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

public sealed class CountdownConfig : Observable, IAnchored
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
    private double _offsetXPct;
    private double _offsetYPct;

    /// <summary>A nudge from the anchor as a share of the canvas (−100..100) — a drag on the PREVIEW pane writes it.</summary>
    public double OffsetXPct { get => _offsetXPct; set => Set(ref _offsetXPct, Math.Clamp(value, -100, 100)); }
    public double OffsetYPct { get => _offsetYPct; set => Set(ref _offsetYPct, Math.Clamp(value, -100, 100)); }
    private string _textColor = "";

    /// <summary>Digits colour; empty = brand/theme text colour (urgency tint still applies).</summary>
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>e.g. “BACK FROM LUNCH AT”, “REHEARSAL RESUMES IN”, “DOORS IN”.</summary>
    public string Label { get => _label; set => Set(ref _label, value); }
    private bool _followPlan;
    /// <summary>
    /// The countdown follows the running order: its target is the standby cue's planned start, as a
    /// time of day, and it moves when the plan does — a slip, a resume, a catch-up, a GO that moves
    /// the standby — so the speaker timer, the stage display and the info screen read the one clock the
    /// caller is keeping. A standby cue with no planned start leaves the countdown as it was.
    /// </summary>
    public bool FollowPlan { get => _followPlan; set => Set(ref _followPlan, value); }
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

/// <summary>A message to the stage — the speaker's display, or the crew's — and whether it was seen.</summary>
public sealed class StageMessage : Observable
{
    private string _id = Guid.NewGuid().ToString("N")[..8];
    private string _text = "";
    private string _channel = "speaker";
    private DateTime _sentUtc = DateTime.UtcNow;
    private DateTime? _ackUtc;
    private string _from = "";
    private bool _flash;

    public string Id { get => _id; set => Set(ref _id, value ?? ""); }
    public string Text { get => _text; set => Set(ref _text, value ?? ""); }
    /// <summary>"speaker" — the presenter's display; "crew" — the stage manager's and the technicians'.</summary>
    public string Channel { get => _channel; set => Set(ref _channel, (value ?? "speaker").Trim().ToLowerInvariant() is { Length: > 0 } c ? c : "speaker"); }
    public DateTime SentUtc { get => _sentUtc; set => Set(ref _sentUtc, value); }
    /// <summary>When the display's ACK was pressed; null while unseen.</summary>
    public DateTime? AckUtc { get => _ackUtc; set => Set(ref _ackUtc, value); }
    /// <summary>Who sent it — the desk, a caller, a cue.</summary>
    public string From { get => _from; set => Set(ref _from, value ?? ""); }
    /// <summary>Flash the display until it is acknowledged.</summary>
    public bool Flash { get => _flash; set => Set(ref _flash, value); }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Seen => _ackUtc is not null;
}

/// <summary>
/// The stage: a professional stage timer and messages to stage, on the countdown's own clock. The
/// timer is the countdown overlay's — one clock for the wall, the confidence screen, the stage page
/// and the caller — with the thresholds that colour it, a pause that keeps what is left, and the
/// segments the running order gives it. The messages go to the speaker's display or the crew's and
/// come back with a receipt when the display's ACK is pressed.
/// </summary>
public sealed class StageConfig : Observable
{
    private int _amberSeconds = 120;
    private int _redSeconds = 60;
    private bool _speakerSeesSegment;
    private bool _crewSeesSegment = true;
    private bool _speakerSeesClock = true;
    private bool _paused;
    private double _pausedRemainingSeconds;
    private DateTime? _flashUntilUtc;

    /// <summary>The timer turns amber with this many seconds left.</summary>
    public int AmberSeconds { get => _amberSeconds; set => Set(ref _amberSeconds, Math.Clamp(value, 0, 3600)); }

    /// <summary>The timer turns red with this many seconds left, and stays red past zero.</summary>
    public int RedSeconds { get => _redSeconds; set => Set(ref _redSeconds, Math.Clamp(value, 0, 3600)); }

    /// <summary>The speaker's display shows the running order's segment under the time.</summary>
    public bool SpeakerSeesSegment { get => _speakerSeesSegment; set => Set(ref _speakerSeesSegment, value); }

    /// <summary>The crew's display shows the segment, the next cue and the drift.</summary>
    public bool CrewSeesSegment { get => _crewSeesSegment; set => Set(ref _crewSeesSegment, value); }

    /// <summary>The speaker's display shows the time of day beside the timer.</summary>
    public bool SpeakerSeesClock { get => _speakerSeesClock; set => Set(ref _speakerSeesClock, value); }

    /// <summary>The timer is paused: what was left is kept in <see cref="PausedRemainingSeconds"/> and shown still.</summary>
    public bool Paused { get => _paused; set => Set(ref _paused, value); }

    public double PausedRemainingSeconds { get => _pausedRemainingSeconds; set => Set(ref _pausedRemainingSeconds, Math.Max(0, value)); }

    /// <summary>The words one press sends to the speaker.</summary>
    public ShowCollection<string> Presets { get; init; } = new() { "Wrap up", "5 minutes", "1 minute", "Q&A next", "Louder please", "Slower please", "Look at camera 1" };

    /// <summary>The messages sent this show, newest last, capped by the service.</summary>
    public ShowCollection<StageMessage> Messages { get; init; } = new();

    /// <summary>Runtime: the displays flash until this moment — a FLASH without a message.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? FlashUntilUtc { get => _flashUntilUtc; set => Set(ref _flashUntilUtc, value); }
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
    public ShowCollection<NdiSenderConfig> Senders { get; init; } = new();
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

    /// <summary>What a look does to the stream, if anything.</summary>
    public enum LookStream
    {
        Leave,
        Start,
        Stop,
    }

    private LookStream _stream;

    /// <summary>
    /// The stream this look starts or stops when it goes on air; Leave (the default) does not
    /// touch it. The same shape as <see cref="MusicItemId"/> and for the same reason: a look is a
    /// picture, and the two things an operator wants to happen *with* a picture — the break music
    /// under it and the broadcast of it — are worth carrying with it rather than remembering.
    ///
    /// This is what gives an F-key, the presenter's clicker list and a permanent install's
    /// schedule a stream command: all three recall a look, and none of them carries an action
    /// list of its own. Loading a look into the preview never touches the stream, exactly as it
    /// never touches the music — nothing that has not gone to air may reach the internet.
    /// </summary>
    public LookStream Stream { get => _stream; set => Set(ref _stream, value); }
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
    public ShowCollection<LookConfig> Looks { get; init; } = new();
    public ShowCollection<CueConfig> Cues { get; init; } = new();
}

/// <summary>Crossfade between content changes on every sink (looks, playlist advances, blackout).</summary>
public sealed class TransitionConfig : Observable
{
    private bool _enabled = true;
    private double _durationMs = 400;
    private TransitionKind _kind = TransitionKind.Dissolve;
    private TransitionDirection _direction = TransitionDirection.Right;
    private ReactiveScene _scene = ReactiveScene.Plasma;
    private double _softness = 0.18;
    private bool _dipUsesBrand = true;
    private string _dipColor = "#000000";

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public double DurationMs { get => _durationMs; set => Set(ref _durationMs, Math.Clamp(value, 100, 3000)); }

    /// <summary>How one picture becomes the next; Dissolve is what the desk has always done and stays the default.</summary>
    public TransitionKind Kind { get => _kind; set => Set(ref _kind, value); }

    /// <summary>Which way a wipe or a push travels.</summary>
    public TransitionDirection Direction { get => _direction; set => Set(ref _direction, value); }

    /// <summary>The reactive scene a Reactive transition uses as its matte.</summary>
    public ReactiveScene Scene { get => _scene; set => Set(ref _scene, value); }

    /// <summary>How soft a wipe's or a matte's edge is, as a share of the picture (0 = a hard line).</summary>
    public double Softness { get => _softness; set => Set(ref _softness, Math.Clamp(value, 0, 0.6)); }

    /// <summary>A dip goes through the show's brand background; untick to name a colour of its own.</summary>
    public bool DipUsesBrand { get => _dipUsesBrand; set => Set(ref _dipUsesBrand, value); }

    /// <summary>The colour a dip goes through when it is not the brand's.</summary>
    public string DipColor { get => _dipColor; set => Set(ref _dipColor, value ?? _dipColor); }
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
    public ShowCollection<PresenterStepConfig> Steps { get; init; } = new();

    /// <summary>Runtime-only: the step currently applied (-1 = not started).</summary>
    [JsonIgnore]
    public int CurrentIndex { get => _currentIndex; set => Set(ref _currentIndex, value); }
}

/// <summary>One row of the audio playlist: a file, and the name the desk reads for it.</summary>
public sealed class AudioTrackConfig : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _path = "";
    private string _name = "";
    private bool _isNowPlaying;

    /// <summary>Stable identity: a cue names a track by it, so a rename or a re-order never breaks the cue.</summary>
    public string Id { get => _id; set => Set(ref _id, value); }

    public string Path { get => _path; set { if (Set(ref _path, value ?? "")) Raise(nameof(DisplayName)); } }

    /// <summary>An operator name for the row; empty = the file's name without its extension.</summary>
    public string Name { get => _name; set { if (Set(ref _name, value ?? "")) Raise(nameof(DisplayName)); } }

    [JsonIgnore]
    public string DisplayName => _name.Length > 0 ? _name : System.IO.Path.GetFileNameWithoutExtension(_path);

    /// <summary>Runtime-only: the row the player is on right now (the ▶ NOW marker).</summary>
    [JsonIgnore]
    public bool IsNowPlaying { get => _isNowPlaying; set => Set(ref _isNowPlaying, value); }
}

/// <summary>
/// The audio playlist — an independent bed of music or announcements that plays whatever is on
/// screen, to the default device or any set of Windows audio outputs (HDMI screens are audio
/// devices too): the rows in their order, then every audio file of the folders named, in name
/// order; shuffled by a seed when asked; the list looping at its end or stopping there. One
/// file written before the list existed (<see cref="Path"/>) becomes its first row on load.
/// </summary>
/// <summary>
/// The desk's monitor bus: which picture's sound the operator is hearing.
///
/// A show can have a clip on the programme, another on a confidence screen's own picture and a
/// third loaded into the preview, and every one of them has a soundtrack. Played together they are
/// a mix nobody asked for, over the top of the operator's own headphones. So one of them is heard
/// at a time, and it is the programme unless the operator says otherwise — the sound the room is
/// hearing is the sound the desk is checking against.
///
/// It is a monitoring choice, never content: it changes nothing on any output, nothing on the
/// stream, and nothing a look or a cue carries.
/// </summary>
public sealed class MonitorConfig : Observable
{
    private AudioMonitor _source = AudioMonitor.Program;
    private string _outputId = "";
    private string _device = "";
    private double _volumePct = 100;

    public AudioMonitor Source { get => _source; set => Set(ref _source, value); }

    /// <summary>The content target listened to when <see cref="Source"/> is Output; empty = the programme.</summary>
    public string OutputId { get => _outputId; set => Set(ref _outputId, value ?? ""); }

    /// <summary>
    /// The operator's own output — a headphone socket, the machine's own speakers, a spare card —
    /// kept off the programme's devices on purpose. Empty means there isn't one, and then nothing
    /// but the programme can be heard: on a desk with a single output, auditioning a picture the
    /// room is not watching would mean taking the room's sound away to do it.
    /// </summary>
    public string Device { get => _device; set => Set(ref _device, value ?? ""); }

    /// <summary>How loud the monitor is, without touching what the room hears.</summary>
    public double VolumePct { get => _volumePct; set => Set(ref _volumePct, Math.Clamp(value, 0, 125)); }
}

/// <summary>What a VOG does to everything else on a destination while it plays.</summary>
public enum AudioVogMode
{
    /// <summary>Everything else steps down to the duck level underneath the announcement and comes back after it.</summary>
    Duck,
    /// <summary>The announcement alone: everything else goes to silence under it and comes back after it.</summary>
    Replace,
    /// <summary>The announcement never reaches this destination — a stream that stays clean, a screen with its own soundtrack.</summary>
    Leave,
}

/// <summary>
/// One place sound goes: a Windows output by name ("dev:Speakers (Realtek…)", "dev:(computer output)"),
/// or an NDI send by its id ("ndi:…"). Its trim, its lip-sync delay, its mute, and what a VOG does on it.
/// </summary>
public sealed class AudioDestinationConfig : Observable
{
    private string _key = "";
    private string _label = "";
    private double _trimDb;
    private int _delayMs;
    private bool _mute;
    private AudioVogMode _vogMode = AudioVogMode.Duck;
    private double? _vogDuckDb;
    private int _attackMs = 40;
    private int _releaseMs = 600;

    public string Key { get => _key; set => Set(ref _key, value ?? ""); }

    /// <summary>The operator's name for it ("Room desk", "Info screen"); empty = the device's own.</summary>
    public string Label { get => _label; set => Set(ref _label, value ?? ""); }

    /// <summary>A trim on everything that reaches it, −60…+12 dB.</summary>
    public double TrimDb { get => _trimDb; set => Set(ref _trimDb, Services.Db.ClampLevel(value)); }

    /// <summary>Its lip-sync offset: its sound leaves this much later, 0–2000 ms.</summary>
    public int DelayMs { get => _delayMs; set => Set(ref _delayMs, Math.Clamp(value, 0, 2000)); }

    public bool Mute { get => _mute; set => Set(ref _mute, value); }

    public AudioVogMode VogMode { get => _vogMode; set => Set(ref _vogMode, value); }

    /// <summary>The level everything else drops to under a VOG here, in dB (−60…0); null = the show's own duck level.</summary>
    public double? VogDuckDb { get => _vogDuckDb; set => Set(ref _vogDuckDb, value is { } v ? Math.Clamp(v, -60, 0) : null); }

    /// <summary>How fast the duck engages, 5–2000 ms.</summary>
    public int AttackMs { get => _attackMs; set => Set(ref _attackMs, Math.Clamp(value, 5, 2000)); }

    /// <summary>How fast the others come back after the VOG, 20–10000 ms.</summary>
    public int ReleaseMs { get => _releaseMs; set => Set(ref _releaseMs, Math.Clamp(value, 20, 10000)); }
}

/// <summary>One crosspoint: a source on a destination at a level.</summary>
public sealed class AudioRouteConfig : Observable
{
    private string _source = "";
    private string _destination = "";
    private double _levelDb;
    private bool _enabled = true;

    /// <summary>A source id: programme, screen:&lt;target&gt;, preview, music, vog, sting, tone.</summary>
    public string Source { get => _source; set => Set(ref _source, value ?? ""); }

    /// <summary>A destination key: dev:&lt;name&gt; or ndi:&lt;id&gt;.</summary>
    public string Destination { get => _destination; set => Set(ref _destination, value ?? ""); }

    /// <summary>−60…+12 dB; 0 is unity.</summary>
    public double LevelDb { get => _levelDb; set => Set(ref _levelDb, Services.Db.ClampLevel(value)); }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
}

/// <summary>
/// Which soundtrack goes where. Off — the default, and every show made before it — the desk keeps
/// round 26's two wires: the programme's sound on the programme's outputs, the operator's own on
/// the monitor. On, the matrix is in charge: each destination lists the sources it carries and at
/// what level, and a VOG ducks, replaces or leaves each one as it says.
/// </summary>
public sealed class AudioRoutingConfig : Observable
{
    private bool _enabled;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public ShowCollection<AudioDestinationConfig> Destinations { get; init; } = new();

    public ShowCollection<AudioRouteConfig> Routes { get; init; } = new();
}

public sealed class AudioPlayerConfig : Observable
{
    private string _path = "";
    private bool _loop = true;
    private bool _shuffle;
    private int _shuffleSeed = 1;
    private double _volumePct = 100;
    private bool _playing;

    /// <summary>The single track of a file written before the list existed (schema 7 and older); migrated into <see cref="Items"/> and cleared.</summary>
    public string Path { get => _path; set => Set(ref _path, value ?? ""); }

    /// <summary>The rows, in the order they play (unless shuffled).</summary>
    public ShowCollection<AudioTrackConfig> Items { get; init; } = new();

    /// <summary>Folders whose audio files play after the rows, in name order — dropped in live, they are seen within half a minute.</summary>
    public ShowCollection<string> Folders { get; init; } = new();

    /// <summary>The list loops at its end; off, it stops there.</summary>
    public bool Loop { get => _loop; set => Set(ref _loop, value); }

    /// <summary>The order shuffled by <see cref="ShuffleSeed"/> — the same order every time until the seed changes (RESHUFFLE).</summary>
    public bool Shuffle { get => _shuffle; set => Set(ref _shuffle, value); }

    public int ShuffleSeed { get => _shuffleSeed; set => Set(ref _shuffleSeed, value); }

    public double VolumePct { get => _volumePct; set => Set(ref _volumePct, Math.Clamp(value, 0, 125)); }

    /// <summary>
    /// The PROGRAMME's output devices — what the room hears: a USB or dedicated interface, the
    /// HDMI screens, or the machine's own output. Empty = the default device. The operator's own
    /// monitoring is a separate destination (<see cref="MonitorConfig.Device"/>) so the two can be
    /// different wires.
    /// </summary>
    public ShowCollection<string> Devices { get; init; } = new();

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
    public ShowCollection<OutputDelayConfig> OutputDelays { get; init; } = new();

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

    public ShowCollection<StingerItemConfig> Items { get; init; } = new();

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

    public ShowCollection<SpotifyItemConfig> Items { get; init; } = new();

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

    public ShowCollection<StreamDestinationConfig> Destinations { get; init; } = new();

    /// <summary>Runtime-only: streaming never auto-starts with the app.</summary>
    [JsonIgnore]
    public bool Active { get => _active; set => Set(ref _active, value); }

    private string _lastError = "";

    /// <summary>Runtime-only: why the stream stopped by itself (the encoder failed, the destination refused); "" while it runs or after a start.</summary>
    [JsonIgnore]
    public string LastError { get => _lastError; set => Set(ref _lastError, value ?? ""); }
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

    private bool _fadeAudioWithBlack = true;

    /// <summary>
    /// A fade to black that darkens the whole rig — FADE TO BLACK on every screen, or the last
    /// lit target going — fades the programme's sound (the music and a clip's soundtrack) over
    /// the same seconds, and a fade up brings it back. On by default; off leaves the sound alone.
    /// A plain BLACKOUT never touches the sound.
    /// </summary>
    public bool FadeAudioWithBlack { get => _fadeAudioWithBlack; set => Set(ref _fadeAudioWithBlack, value); }
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

    /// <summary>The screens strip's width with the work area wide (◧ WIDE, or a Machine / Help page): the wall and the panes, reduced — the default; the divider sets <see cref="WideScreensWidth"/>.</summary>
    public const double DefaultWideScreensWidth = 300;
    public const double MinWideScreensWidth = 200;
    public const double MaxWideScreensWidth = 1000;

    /// <summary>The Run area: how much of its width the cue stack takes — about a third by default, the wall the rest.</summary>
    public const double DefaultRunCueShare = 0.35;
    public const double MinRunCueShare = 0.2;
    public const double MaxRunCueShare = 0.7;

    private double _editorWidth = DefaultEditorWidth;
    private double _programShare = DefaultProgramShare;
    private bool _wideWorkArea;
    private double _wideScreensWidth = DefaultWideScreensWidth;
    private bool _showHints;
    private double _runCueShare = DefaultRunCueShare;
    private bool _runWallCollapsed;
    private bool _runPadOpen;
    private string _runMonitor = "";

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
    /// The screens strip's width in pixels with the work area wide — ◧ WIDE on, or a Machine / Help
    /// page, which take the room on their own. The divider drags it and the show remembers it; the
    /// page's own width (<see cref="EditorWidth"/>) is a separate number the strip never touches.
    /// Absent in an older file, so the strip opens at its default.
    /// </summary>
    public double WideScreensWidth
    {
        get => _wideScreensWidth;
        set => Set(ref _wideScreensWidth, Math.Clamp(double.IsFinite(value) ? value : DefaultWideScreensWidth, MinWideScreensWidth, MaxWideScreensWidth));
    }

    /// <summary>
    /// The pages' explanations shown inline. Off (the default) they live behind ? TIPS on the
    /// page strip, and the room goes to the controls.
    /// </summary>
    public bool ShowHints { get => _showHints; set => Set(ref _showHints, value); }

    /// <summary>The cue stack's share of the Run area's width (the divider between the wall and the stack); the wall takes the rest.</summary>
    public double RunCueShare
    {
        get => _runCueShare;
        set => Set(ref _runCueShare, Math.Clamp(double.IsFinite(value) ? value : DefaultRunCueShare, MinRunCueShare, MaxRunCueShare));
    }

    /// <summary>The Run area's wall tiles collapsed to vertical title bars — the tally and the name on their side — so the stack has the room.</summary>
    public bool RunWallCollapsed { get => _runWallCollapsed; set => Set(ref _runWallCollapsed, value); }

    /// <summary>The caller's pad open on the Run surface.</summary>
    public bool RunPadOpen { get => _runPadOpen; set => Set(ref _runPadOpen, value); }

    /// <summary>
    /// The wall tiles collapsed one by one to their title bars (▸ on a tile's title row; ▾ on the
    /// bar opens it again): their target ids — a screen id, a canvas key, "" for PGM. Absent in an
    /// older file, so every tile opens full.
    /// </summary>
    /// <summary>
    /// What the RUN surface's monitor shows (round 62): "" the main screen, "PGM" the programme,
    /// "OFF" hidden, else a screen id or a canvas key. RUN MONITOR on the wire, the right-click
    /// on the monitor on the desk; the show remembers.
    /// </summary>
    public string RunMonitor { get => _runMonitor; set => Set(ref _runMonitor, value ?? ""); }

    public ShowCollection<string> CollapsedTiles { get; init; } = new();
}

/// <summary>Watchdog: the supervisor process that restarts the show after a crash or hang.</summary>
public sealed class WatchdogConfig : Observable
{
    private bool _enabled = true;
    private bool _autoRestore = true;
    private bool _takeOverOutputs = true;

    /// <summary>Run under the supervisor (takes effect on the next start).</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>After a watchdog restart, put live outputs (and a playing track) back automatically.</summary>
    public bool AutoRestore { get => _autoRestore; set => Set(ref _autoRestore, value); }

    /// <summary>
    /// A start that finds the previous run's render windows still playing takes them back: it asks
    /// that process for them and, when it has stopped answering, ends it — so the screens are this
    /// desk's to stop and to change, instead of playing on with nobody at the controls. Off leaves
    /// them where they are and says so.
    /// </summary>
    public bool TakeOverOutputs { get => _takeOverOutputs; set => Set(ref _takeOverOutputs, value); }

    private bool _beaconEnabled;
    private string _beaconHost = "255.255.255.255";
    private int _beaconPort = 9700;
    private bool _beaconListen;
    private int _beaconListenPort = 9700;
    private string _beaconName = "";

    /// <summary>Send a heartbeat beacon once a second — this is the main machine, and a second machine may watch it.</summary>
    public bool BeaconEnabled { get => _beaconEnabled; set => Set(ref _beaconEnabled, value); }
    /// <summary>Where the beacon goes: a host, an address, or the broadcast address (everyone on this network).</summary>
    public string BeaconHost { get => _beaconHost; set => Set(ref _beaconHost, (value ?? "").Trim()); }
    public int BeaconPort { get => _beaconPort; set => Set(ref _beaconPort, Math.Clamp(value, 1, 65535)); }
    /// <summary>Listen for another machine's beacon — this is the backup, and the health line says whether the main is alive.</summary>
    public bool BeaconListen { get => _beaconListen; set => Set(ref _beaconListen, value); }
    public int BeaconListenPort { get => _beaconListenPort; set => Set(ref _beaconListenPort, Math.Clamp(value, 1024, 65535)); }
    /// <summary>How this machine names itself in its beacon; empty = the computer's name.</summary>
    public string BeaconName { get => _beaconName; set => Set(ref _beaconName, (value ?? "").Trim()); }
}

/// <summary>
/// The twin link: a second Patterns kept in step with this one — every edit mirrored as it lands,
/// the air record with it — with its outputs held closed until the main goes silent or the
/// operator says TAKE OVER. On this machine it is a second process in its own folder (a second
/// crash domain); on another machine it is the beacon's backup, in step instead of merely told.
/// </summary>
public sealed class TwinConfig : Observable
{
    private bool _sendSecrets = true;
    private TwinRole _role;
    private int _port = 9699;
    private string _mainHost = "";
    private string _key = "";
    private bool _autoTakeOver;
    private bool _localStandby;
    private string _takeOverCue = "";
    private string _takeBackCue = "";
    private bool _acceptCallers = true;

    /// <summary>Off, the main that lets a standby join, or the standby that follows one.</summary>
    public TwinRole Role { get => _role; set => Set(ref _role, value); }

    /// <summary>The port the main listens on and the standby dials.</summary>
    public int Port { get => _port; set => Set(ref _port, Math.Clamp(value, 1024, 65535)); }

    /// <summary>The main's address or name for a standby; empty = whichever main's beacon this machine hears.</summary>
    public string MainHost { get => _mainHost; set => Set(ref _mainHost, (value ?? "").Trim()); }

    /// <summary>A word both machines must share for the link to open. A main with none is given one (<see cref="Patterns.Core.Services.TwinKeys"/>) before its port opens: a keyless main would let any machine on the network hold its outputs.</summary>
    public string Key { get => _key; set => Set(ref _key, (value ?? "").Trim()); }

    /// <summary>The standby takes the show by itself once the main has been silent past the limit; off, TAKE OVER is the operator's press. From another machine, only with a wall-switch cue.</summary>
    public bool AutoTakeOver { get => _autoTakeOver; set => Set(ref _autoTakeOver, value); }

    /// <summary>
    /// The cue this desk fires once it has taken the show over: the wall switched to this machine's
    /// input — a switcher's HTTP or OSC verb, a PJLink input, a matrix route — so whichever desk the
    /// room shows is the one running the show. The room's own fence, and what taking over by itself
    /// from another machine needs. Named by its number, its name or its id; empty = none.
    /// </summary>
    public string TakeOverCue { get => _takeOverCue; set => Set(ref _takeOverCue, (value ?? "").Trim()); }

    /// <summary>The cue the main fires on TAKE BACK: the wall switched back to this machine's input. Empty = none.</summary>
    public string TakeBackCue { get => _takeBackCue; set => Set(ref _takeBackCue, (value ?? "").Trim()); }

    /// <summary>
    /// Whether the credentials the show carries — a projector's PJLink password, the weather key —
    /// travel to the standby. On: the standby runs the show with nothing typed twice. Off: they are
    /// blanked on the wire and the standby keeps the ones typed on it, for a link on a network that
    /// is not the show's own. The twin's key, the admin passcode and the management token never travel either way.
    /// </summary>
    public bool SendSecrets { get => _sendSecrets; set => Set(ref _sendSecrets, value); }

    /// <summary>
    /// A caller node may link to this desk: the desk sends and hears the beacon so the two find each
    /// other, and listens on the twin's port for callers whatever its twin role — a caller joins
    /// with this desk's key, follows the show and calls it, and never holds an output. Off, the
    /// beacon stays as the Machine page sets it and no caller can link.
    /// </summary>
    public bool AcceptCallers { get => _acceptCallers; set => Set(ref _acceptCallers, value); }

    /// <summary>
    /// A main runs its own standby as a second process on this machine: the same build, its own
    /// folder beside the show (twin-standby), dialled to this desk, taking over by itself when this
    /// desk stops beating, restarted by this desk when it exits. Off, a standby is started by hand
    /// or lives on another machine.
    /// </summary>
    public bool LocalStandby { get => _localStandby; set => Set(ref _localStandby, value); }
}

/// <summary>
/// The show lock: what Patterns holds off on this machine while the show runs — the things that
/// have interrupted shows: a toast over the desk, a new-mail chime through the PA, a Teams call
/// ringing, Sticky Keys popping up when the caller hammers Shift, the screen going to sleep, the
/// Windows key opening Start. Each is its own switch; the lock goes on with the outputs by
/// default and comes off with them, or by hand.
/// </summary>
public sealed class LockConfig : Observable
{
    private bool _autoWithOutputs = true;
    private bool _notifications = true;
    private bool _sounds = true;
    private bool _otherAudio = true;
    private bool _shortcuts = true;
    private bool _keepAwake = true;
    private bool _windowsKey = true;
    private string _allowedAudio = "Spotify";

    /// <summary>Lock when the outputs open, unlock when they close.</summary>
    public bool AutoWithOutputs { get => _autoWithOutputs; set => Set(ref _autoWithOutputs, value); }

    /// <summary>Windows toasts and banners off for this user.</summary>
    public bool Notifications { get => _notifications; set => Set(ref _notifications, value); }

    /// <summary>The system sound scheme silenced: no chime, no ding, no default beep.</summary>
    public bool Sounds { get => _sounds; set => Set(ref _sounds, value); }

    /// <summary>Every other app's audio session muted, and kept muted as new ones start — Teams' ring, Outlook's alert, a browser.</summary>
    public bool OtherAudio { get => _otherAudio; set => Set(ref _otherAudio, value); }

    /// <summary>The Sticky Keys, Filter Keys and Toggle Keys shortcuts off, so a caller hammering Shift never gets a dialog and a beep.</summary>
    public bool Shortcuts { get => _shortcuts; set => Set(ref _shortcuts, value); }

    /// <summary>The machine kept awake and the display on; the screensaver off.</summary>
    public bool KeepAwake { get => _keepAwake; set => Set(ref _keepAwake, value); }

    /// <summary>The Windows key swallowed, so nothing opens Start over the desk.</summary>
    public bool WindowsKey { get => _windowsKey; set => Set(ref _windowsKey, value); }

    /// <summary>Apps whose audio the lock lets through, by process name — the break-music player the show itself drives.</summary>
    public string AllowedAudio { get => _allowedAudio; set => Set(ref _allowedAudio, value ?? ""); }
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
    private string _libreOfficePath = "";
    private VideoDecodingKind _videoDecoding = VideoDecodingKind.Auto;
    private QualityMode _quality = QualityMode.Auto;

    public GraphicsConfig Graphics { get; init; } = new();

    /// <summary>The effects' quality ladder — see <see cref="QualityMode"/>; applied at once, on every sink alike.</summary>
    public QualityMode Quality { get => _quality; set => Set(ref _quality, value); }

    /// <summary>Append a performance sample to patterns.metrics.csv every 30 s (rotated at 1 MB).</summary>
    public bool MetricsCsv { get => _metricsCsv; set => Set(ref _metricsCsv, value); }

    /// <summary>Where clips decode — see <see cref="VideoDecodingKind"/>; a change reaches the next decoder opened, never a clip mid-play.</summary>
    public VideoDecodingKind VideoDecoding { get => _videoDecoding; set => Set(ref _videoDecoding, value); }

    /// <summary>Where LibreOffice is on this machine when Patterns cannot find it by itself (soffice.exe, or its folder) — PowerPoint decks convert through it.</summary>
    public string LibreOfficePath { get => _libreOfficePath; set => Set(ref _libreOfficePath, value ?? ""); }
}

/// <summary>
/// How the room's phones reach the audience port. Flat: each phone on its own address, the venue's
/// Wi-Fi handing them out, so the per-address budgets tell one runaway phone from the room. Venue
/// NAT: every phone arrives from one address — a NAT gateway, a captive portal's proxy, a hotel's
/// guest network — so the per-address budgets open to the room and the per-phone ones do the work.
/// </summary>
public enum AudienceNetwork
{
    Flat,
    VenueNat,
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

    private bool _announce = true;
    /// <summary>Announce this process on the network (mDNS, _patterns._tcp) so a Companion lists it under "Desk on the network" instead of asking for an address. On by default; a deliberate switch for a network that must stay quiet.</summary>
    public bool Announce { get => _announce; set => Set(ref _announce, value); }

    private bool _audienceEnabled;
    private int _audiencePort = 9701;
    private string _audienceBind = "";
    private int _audienceMaxPlayers = 500;
    private bool _assistantOnWire;

    /// <summary>
    /// The audience listener: a socket of its own, off by default, that answers the play pages and
    /// nothing else — never the control API. The one port a room of phones may reach.
    /// </summary>
    public bool AudienceEnabled { get => _audienceEnabled; set => Set(ref _audienceEnabled, value); }
    public int AudiencePort { get => _audiencePort; set => Set(ref _audiencePort, Math.Clamp(value, 1024, 65535)); }
    /// <summary>The address the audience listener binds ("" = every interface): the audience network's own, when the hub has two.</summary>
    public string AudienceBind { get => _audienceBind; set => Set(ref _audienceBind, (value ?? "").Trim()); }
    public int AudienceMaxPlayers { get => _audienceMaxPlayers; set => Set(ref _audienceMaxPlayers, Math.Clamp(value, 1, 5000)); }
    /// <summary>Whether a node on the wire may put a question to this desk's assistant (a hub's queue) — off, the key is the desk's alone.</summary>
    public bool AssistantOnWire { get => _assistantOnWire; set => Set(ref _assistantOnWire, value); }

    private AudienceNetwork _audienceNetwork;
    /// <summary>How the phones reach the audience port: each on its own address, or all behind one (a venue NAT), where the per-address budgets open to the room.</summary>
    public AudienceNetwork AudienceNetwork { get => _audienceNetwork; set => Set(ref _audienceNetwork, value); }

    private bool _oscEnabled;
    private int _oscPort = 9698;
    private string _oscFeedbackHost = "";
    private int _oscFeedbackPort = 9699;

    /// <summary>OSC in over UDP on <see cref="OscPort"/> while remote control is on. Off by default: another open port is a deliberate act.</summary>
    public bool OscEnabled { get => _oscEnabled; set => Set(ref _oscEnabled, value); }
    public int OscPort { get => _oscPort; set => Set(ref _oscPort, Math.Clamp(value, 1024, 65535)); }

    /// <summary>Where OSC feedback goes on every change — a host name or address; empty sends none (replies still go to whoever sent a command).</summary>
    public string OscFeedbackHost { get => _oscFeedbackHost; set => Set(ref _oscFeedbackHost, (value ?? "").Trim()); }
    public int OscFeedbackPort { get => _oscFeedbackPort; set => Set(ref _oscFeedbackPort, Math.Clamp(value, 1, 65535)); }
}

/// <summary>How a device is reached: an Arduino on a serial port, or a Raspberry Pi, ESP32 or show controller over IP.</summary>
public enum DeviceLink
{
    Serial,
    Tcp,
    Udp,
    /// <summary>
    /// A MIDI control surface — an APC40, a Launchpad, a nanoKONTROL, an X-Touch. It is a device
    /// like the rest because its messages are rendered as the same text lines an Arduino sends, so
    /// the trigger table, the action layer, the journal and the arm fence are the ones already here.
    /// </summary>
    Midi,
    /// <summary>A box with an HTTP API — a line is a request: GET /api/play, POST /cue {"n":3}; the answer's status and first line come back as a line.</summary>
    Http,
}

/// <summary>
/// What a device is, beyond where it is: the vocabulary its words are turned into. Plain lines
/// for a board or a script; a projector's PJLink; Disguise's show control over OSC; Pixera's
/// JSON-RPC; any OSC box. The profile is the reason a cue can say POWER ON to a projector and
/// PLAY to a media server in the same words a person would.
/// </summary>
public enum DeviceProfile
{
    Lines,
    PjLink,
    Disguise,
    Pixera,
    Osc,
    /// <summary>Bitfocus Companion's own TCP remote-control API: PAGE, PRESS, VAR — the desk driving the Stream Deck's pages and buttons.</summary>
    Companion,
}

/// <summary>The line break a device expects at the end of every line Patterns writes.</summary>
public enum LineEnding
{
    Lf,
    CrLf,
    Cr,
    None,
}

/// <summary>A line the device sends ("BTN1", "SENSOR *") and the show command it means ("CUE GO", "LOOK Walk-in", "MESSAGE *").</summary>
public sealed class DeviceTriggerConfig : Observable
{
    private string _match = "";
    private string _command = "";

    /// <summary>The device's line, whole and case-blind, or a prefix ending in *.</summary>
    public string Match { get => _match; set => Set(ref _match, value ?? ""); }

    /// <summary>The protocol line it fires; a command ending in * takes the rest of a prefix match as its tail.</summary>
    public string Command { get => _command; set => Set(ref _command, value ?? ""); }
}

/// <summary>
/// One device of the Interactive area: where it is, how its lines are framed, what its words
/// mean, and whether it hears the show back. The status is runtime, read from the service.
/// </summary>
/// <summary>
/// How sure the show wants to be that a box did what it was told — the four things a send can
/// establish, in order. A cue's step is journaled as dispatched the moment it goes; the receipt
/// that follows says which of these the box reached, and a fence (the twin's wall switch) waits
/// for the level the device is set to.
/// </summary>
public enum ConfirmLevel
{
    /// <summary>The bytes left this desk. All a UDP datagram or a MIDI note can say.</summary>
    Sent,

    /// <summary>The connection took them (TCP, serial), or the box answered at all (HTTP).</summary>
    Delivered,

    /// <summary>The box said yes: PJLink's OK, Pixera's result, an OK line, a 2xx.</summary>
    Accepted,

    /// <summary>Asked afterwards, the box's state is what was asked for.</summary>
    Observed,
}

public sealed class DeviceConfig : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private ConfirmLevel _confirm = ConfirmLevel.Accepted;
    private string _observeQuery = "";
    private string _observeExpect = "";
    private int _confirmTimeoutMs = 2000;
    private string _name = "Arduino";
    private DeviceLink _link = DeviceLink.Serial;
    private string _port = "";
    private int _baud = 115200;
    private int _netPort = 7000;
    private bool _enabled = true;
    private LineEnding _lineEnding = LineEnding.Lf;
    private bool _speaksProtocol = true;
    private string _starterSet = "";
    private bool _hearsShow = true;
    private bool _echoReplies = true;
    private string _testText = "PING";
    private string _status = "";
    private DeviceProfile _profile;
    private string _secret = "";

    public string Id { get => _id; set => Set(ref _id, value ?? ""); }

    /// <summary>The vocabulary the device speaks — plain lines, PJLink, Disguise, Pixera, OSC; see <see cref="Services.DeviceProfiles"/>.</summary>
    public DeviceProfile Profile
    {
        get => _profile;
        set
        {
            if (Set(ref _profile, value)) Raise(nameof(Words));
        }
    }

    /// <summary>A password the box asks for — a projector's PJLink password. Kept with the show, as the rig's other addresses are.</summary>
    public string Secret { get => _secret; set => Set(ref _secret, value ?? ""); }

    private string _surface = "";
    /// <summary>For a Companion: the surface a bare PAGE means — Companion's id for the Stream Deck, as its Surfaces page shows it (streamdeck:…, or emulator:emulator for the browser one).</summary>
    public string Surface { get => _surface; set => Set(ref _surface, (value ?? "").Trim()); }

    /// <summary>The words this device understands, for the page — the profile's list.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Words => Services.DeviceProfiles.Words(_profile);

    /// <summary>What the cues and the wire call it: DEVICE Arduino RELAY 1.</summary>
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }

    public DeviceLink Link { get => _link; set => Set(ref _link, value); }

    /// <summary>The serial port (COM3, /dev/ttyUSB0) or the host (192.168.1.50, pi.local, host:7000).</summary>
    public string Port { get => _port; set => Set(ref _port, (value ?? "").Trim()); }

    /// <summary>Serial speed; 115200 is what Arduino sketches usually open, 9600 the older habit.</summary>
    public int Baud { get => _baud; set => Set(ref _baud, Math.Clamp(value, 300, 4_000_000)); }

    /// <summary>The TCP or UDP port when the host carries none of its own.</summary>
    public int NetPort { get => _netPort; set => Set(ref _netPort, Math.Clamp(value, 1, 65535)); }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public LineEnding LineEnding { get => _lineEnding; set => Set(ref _lineEnding, value); }

    /// <summary>A line with no trigger row of its own is taken as a protocol command when it is one (GO, LOOK 3, BLACKOUT ON).</summary>
    public bool SpeaksProtocol { get => _speaksProtocol; set => Set(ref _speaksProtocol, value); }

    /// <summary>
    /// Which starter set the page's button puts in, for a MIDI surface. It is only ever a starting
    /// point: the rows it adds are this operator's own from the moment they land, editable and
    /// re-learnable, because a note map nobody has run against the hardware is a guess and the desk
    /// should not hide a guess behind a device name.
    /// </summary>
    public string StarterSet { get => _starterSet; set => Set(ref _starterSet, value ?? ""); }

    /// <summary>The device hears the show: BLACKOUT 1, LOOK Walk-in, CUE 01.020 … whenever a value changes.</summary>
    public bool HearsShow { get => _hearsShow; set => Set(ref _hearsShow, value); }

    /// <summary>The answer to a device's command — OK, or ERR and why — goes back to it.</summary>
    public bool EchoReplies { get => _echoReplies; set => Set(ref _echoReplies, value); }

    /// <summary>The line the page's SEND button writes — a handshake to try the wiring.</summary>
    public string TestText { get => _testText; set => Set(ref _testText, value ?? ""); }

    /// <summary>
    /// The confirmation the show wants of this box, capped at what its link and profile can give
    /// (<see cref="Services.DeviceConfirmation.Effective"/>): a receipt below it is a failure in the
    /// journal and on the card, and the twin's wall switch waits for it.
    /// </summary>
    public ConfirmLevel Confirm { get => _confirm; set => Set(ref _confirm, value); }

    /// <summary>For Observed: the words sent after the box accepted, asking what it did — INPUT ? on a projector, GET /api/route on a switcher.</summary>
    public string ObserveQuery { get => _observeQuery; set => Set(ref _observeQuery, value ?? ""); }

    /// <summary>For Observed: what the answer to the query must contain — 31, or "input":"hdmi1".</summary>
    public string ObserveExpect { get => _observeExpect; set => Set(ref _observeExpect, value ?? ""); }

    /// <summary>How long a receipt is waited for before it is a failure (200 ms to 30 s).</summary>
    public int ConfirmTimeoutMs { get => _confirmTimeoutMs; set => Set(ref _confirmTimeoutMs, Math.Clamp(value, 200, 30000)); }

    public ShowCollection<DeviceTriggerConfig> Triggers { get; init; } = new();

    /// <summary>Runtime: open, reconnecting, the last line in and out — set by the service, never saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Status { get => _status; set => Set(ref _status, value ?? ""); }

    private Services.DeviceRuntime _runtime = Services.DeviceRuntime.None;
    private string _historyText = "";

    /// <summary>What the box did lately — runtime alone, never in the file: the last line, reply, confirmation, observed state and failure with their times. The service writes it; the card, STATE and the assistant read it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Services.DeviceRuntime Runtime { get => _runtime; set => Set(ref _runtime, value ?? Services.DeviceRuntime.None); }

    /// <summary>The card's history line with the ages — refreshed by the desk's tick, never saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string HistoryText { get => _historyText; set => Set(ref _historyText, value ?? ""); }
}

/// <summary>The Interactive area: devices over serial and IP that fire commands and hear the show. Off by default — opening ports is a deliberate act.</summary>
public sealed class InteractiveConfig : Observable
{
    private bool _enabled;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public ShowCollection<DeviceConfig> Devices { get; init; } = new();
}

/// <summary>What a slot of a permanent install's schedule is.</summary>
public enum SlotKind
{
    /// <summary>The content of a stretch of the day: its look on air from its start to its end, on its days, between its dates.</summary>
    Programme,
    /// <summary>A placement: its look for its seconds — at its start, then every so many minutes until its end — and the programme back underneath. Optionally on named screens only.</summary>
    Advert,
    /// <summary>Words over the programme — a message, a VOG sound, a look of its own — for its seconds; fired by the clock or by hand (ANNOUNCE).</summary>
    Announcement,
}

/// <summary>
/// One row of an install's schedule. Days read like a rota ("Mon–Fri", "weekends", "every day"),
/// dates fence a seasonal row ("2026-12-01" to "2026-12-31"), times are "HH:mm" and a window
/// that ends at or before it starts runs past midnight (22:00–02:00). The status is runtime.
/// </summary>
public sealed class ScheduleSlotConfig : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "Programme";
    private SlotKind _kind = SlotKind.Programme;
    private bool _enabled = true;
    private string _days = "";
    private string _from = "";
    private string _until = "";
    private string _start = "09:00";
    private string _end = "17:00";
    private int _everyMinutes;
    private int _durationSeconds = 30;
    private string _look = "";
    private string _text = "";
    private string _sound = "";
    private string _screens = "";
    private string _status = "";

    public string Id { get => _id; set => Set(ref _id, value ?? ""); }

    /// <summary>What the page, the cues and the wire call it: ANNOUNCE Closing time, ADVERT Lunch offer.</summary>
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }

    public SlotKind Kind
    {
        get => _kind;
        set
        {
            if (Set(ref _kind, value))
            {
                Raise(nameof(IsProgramme));
                Raise(nameof(IsAdvert));
                Raise(nameof(IsAnnouncement));
                Raise(nameof(Repeats));
            }
        }
    }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>"" = every day; "Mon–Fri", "weekdays", "Sat Sun", "Mon, Wed, Fri", "weekends".</summary>
    public string Days { get => _days; set => Set(ref _days, value ?? ""); }

    /// <summary>First calendar day, "yyyy-MM-dd" ("" = open).</summary>
    public string From { get => _from; set => Set(ref _from, (value ?? "").Trim()); }

    /// <summary>Last calendar day, inclusive ("" = open).</summary>
    public string Until { get => _until; set => Set(ref _until, (value ?? "").Trim()); }

    /// <summary>"HH:mm": when the programme starts, or the first firing of an advert or announcement.</summary>
    public string Start { get => _start; set => Set(ref _start, (value ?? "").Trim()); }

    /// <summary>"HH:mm": when the programme ends, or the last moment an advert or announcement may fire.</summary>
    public string End { get => _end; set => Set(ref _end, (value ?? "").Trim()); }

    /// <summary>Advert / announcement: fires again every so many minutes until the end; 0 = once, at the start.</summary>
    public int EveryMinutes { get => _everyMinutes; set => Set(ref _everyMinutes, Math.Clamp(value, 0, 24 * 60)); }

    /// <summary>Advert / announcement: how long it holds before the programme comes back.</summary>
    public int DurationSeconds { get => _durationSeconds; set => Set(ref _durationSeconds, Math.Clamp(value, 1, 24 * 3600)); }

    /// <summary>The look (by name) — the programme's content, the advert's picture, an announcement's own picture (optional).</summary>
    public string Look { get => _look; set => Set(ref _look, (value ?? "").Trim()); }

    /// <summary>Announcement: the words — the message overlay's text while it runs.</summary>
    public string Text { get => _text; set => Set(ref _text, value ?? ""); }

    /// <summary>Announcement: a VOG from the Audio page's library, by name or number, played as it starts.</summary>
    public string Sound { get => _sound; set => Set(ref _sound, (value ?? "").Trim()); }

    /// <summary>Advert placement: the screens it goes to, by number or label ("1, 3" or "Window, Till") — "" = every screen. The others keep their picture.</summary>
    public string Screens { get => _screens; set => Set(ref _screens, value ?? ""); }

    /// <summary>Runtime: "on air since 09:00", "fired 12:30", "look not found" — set by the service, never saved.</summary>
    [JsonIgnore]
    public string Status { get => _status; set => Set(ref _status, value ?? ""); }

    /// <summary>A free-text announcement fired by hand (ANNOUNCE The store closes in 15 minutes) — never in the list.</summary>
    [JsonIgnore]
    public bool IsAdHoc { get; init; }

    [JsonIgnore]
    public bool IsProgramme => _kind == SlotKind.Programme;

    [JsonIgnore]
    public bool IsAdvert => _kind == SlotKind.Advert;

    [JsonIgnore]
    public bool IsAnnouncement => _kind == SlotKind.Announcement;

    /// <summary>Row-level visibility: the repeat and duration boxes belong to adverts and announcements.</summary>
    [JsonIgnore]
    public bool Repeats => _kind != SlotKind.Programme;
}

/// <summary>
/// A permanent install — a shop, a hotel lobby, a museum wall, a reception screen: the clock runs
/// the site. Off by default: on a show machine the schedule must never move the picture by itself.
/// Remote administration (a passcode for the web remote's ADMIN page), a management server the
/// site checks in with, and updates the watchdog applies at a quiet hour live here too.
/// </summary>
public sealed class InstallConfig : Observable
{
    private bool _enabled;
    private string _siteName = "";
    private string _idleLook = "";
    private int _announcementSeconds = 20;
    private string _adminPasscode = "";
    private string _managementUrl = "";
    private string _managementToken = "";
    private int _checkInMinutes = 5;
    private bool _autoUpdate;
    private string _updateWindow = "03:00";

    /// <summary>The schedule runs: programmes come and go by the clock, adverts and announcements fire at their times.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>How this site names itself to the management server and in the support bundle ("Lobby wall", "Store 12 window").</summary>
    public string SiteName { get => _siteName; set => Set(ref _siteName, (value ?? "").Trim()); }

    /// <summary>The look on air when no programme is scheduled (outside opening hours); "" = black.</summary>
    public string IdleLook { get => _idleLook; set => Set(ref _idleLook, (value ?? "").Trim()); }

    /// <summary>How long a free-text announcement (ANNOUNCE some words) stays up.</summary>
    public int AnnouncementSeconds { get => _announcementSeconds; set => Set(ref _announcementSeconds, Math.Clamp(value, 1, 3600)); }

    public ShowCollection<ScheduleSlotConfig> Slots { get; init; } = new();

    /// <summary>The passcode the web remote's ADMIN page, RESTART and UPDATE APPLY ask for; "" = remote administration off.</summary>
    public string AdminPasscode { get => _adminPasscode; set => Set(ref _adminPasscode, (value ?? "").Trim()); }

    /// <summary>Where the site checks in (https://…/patterns/checkin); "" = it does not. The reply may carry commands and an update.</summary>
    public string ManagementUrl { get => _managementUrl; set => Set(ref _managementUrl, (value ?? "").Trim()); }

    /// <summary>A shared secret sent as X-Patterns-Token and expected back in every reply; "" = none.</summary>
    public string ManagementToken { get => _managementToken; set => Set(ref _managementToken, (value ?? "").Trim()); }

    public int CheckInMinutes { get => _checkInMinutes; set => Set(ref _checkInMinutes, Math.Clamp(value, 1, 24 * 60)); }

    /// <summary>A staged package (updates/*.zip) is applied by itself at the update window's minute.</summary>
    public bool AutoUpdate { get => _autoUpdate; set => Set(ref _autoUpdate, value); }

    private bool _rigDayGames;

    /// <summary>Rig day, gamified — opt-in, per operator: the show-ready bar on the health line, the alignment game, Blend Quest, the caller's on-time streak. Off, none of it shows.</summary>
    public bool RigDayGames { get => _rigDayGames; set => Set(ref _rigDayGames, value); }

    /// <summary>"HH:mm" — the quiet hour an automatic update lands in.</summary>
    public string UpdateWindow { get => _updateWindow; set => Set(ref _updateWindow, (value ?? "").Trim()); }
}

/// <summary>Web pages opened on outputs (managed browser windows, not engine-composited).</summary>
public sealed class WebConfig : Observable
{
    private string _url = "";
    private string _targetScreenId = "";
    private Services.PageServicePick _service;
    private bool _clean;
    private bool _showPointer;

    /// <summary>
    /// Draw the desk's pointer and its clicks on every web page the show puts up — a pattern's page
    /// and a layer's alike. Off by default: the room sees the page, not the operator's hand. The
    /// desk's own choice (round 62), kept with the show and never re-armed by a look, a preset, a
    /// target switch or a TAKE, which is why it lives here and not on the picture.
    /// </summary>
    public bool ShowPointer { get => _showPointer; set => Set(ref _showPointer, value); }

    /// <summary>The page the Remote &amp; web page puts on the pattern (https://… or a local file path).</summary>
    public string Url { get => _url; set => Set(ref _url, value); }

    /// <summary>What that page is treated as when it lands; Auto reads the address.</summary>
    public Services.PageServicePick Service { get => _service; set => Set(ref _service, value); }

    /// <summary>Whether that page lands with its service's furniture taken off.</summary>
    public bool Clean { get => _clean; set => Set(ref _clean, value); }
    /// <summary>Kept for show files from before pages lived inside the engine; nothing opens outside Patterns any more.</summary>
    public string TargetScreenId { get => _targetScreenId; set => Set(ref _targetScreenId, value); }

    /// <summary>Quick-recall URLs (session schedules, dashboards, wayfinding pages).</summary>
    public ShowCollection<string> SavedUrls { get; init; } = new();
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

    private bool _lowLatency;

    /// <summary>
    /// The decoder opens the device with no input buffer and no clock smoothing: a frame reaches
    /// the screen as soon as it is decoded, and a hitch drops one rather than shows it late. For a
    /// camera the room sees beside the speaker (IMAG); off for a confidence feed that must not skip.
    /// </summary>
    public bool LowLatency { get => _lowLatency; set => Set(ref _lowLatency, value); }
}

/// <summary>Root of everything the operator can configure. Serialized as the portable settings/show file.</summary>
public sealed class ShowState : Observable
{
    public const int CurrentSchemaVersion = 10;

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
    public ShowCollection<OutputAssignment> Independent { get; init; } = new();

    /// <summary>
    /// The monitor walls this show has — up to <see cref="Multiviews.Max"/> of them, each with its
    /// own layout and tiles. A wall is a thing of its own rather than a pattern belonging to one
    /// screen: the show's picture stays the show's picture, and however many outputs point at a
    /// wall — a spare display, an NDI send, the stream — they all draw the same one.
    /// </summary>
    public ShowCollection<MultiviewOptions> Multiviews { get; init; } = new();
    public OverlaySet Overlays { get; init; } = new();
    public CountdownConfig Countdown { get; init; } = new();

    /// <summary>The stage: the timer's thresholds and pauses, the messages to the speaker and the crew with their receipts, the presets.</summary>
    public StageConfig Stage { get; init; } = new();
    public BrandKit Brand { get; init; } = new();

    /// <summary>The venue and the forecast's source for the weather overlay — the show's, never a look's.</summary>
    public WeatherSettings Weather { get; init; } = new();
    public NdiConfig Ndi { get; init; } = new();
    public ToneConfig Tone { get; init; } = new();
    public LooksConfig LooksAndCues { get; init; } = new();
    public WebConfig Web { get; init; } = new();
    public TransitionConfig Transition { get; init; } = new();
    public PresenterConfig Presenter { get; init; } = new();

    /// <summary>The caller's cue stack and the speaker's clicker list (schema 5); see <see cref="Services.CueStacks"/>.</summary>
    public ShowCollection<CueStackConfig> Stacks { get; init; } = new();
    public AudioPlayerConfig AudioPlayer { get; init; } = new();
    public ControlConfig Control { get; init; } = new();

    /// <summary>The Interactive area: Arduinos over serial, Raspberry Pis and controllers over IP — commands in, the show back out.</summary>
    public InteractiveConfig Interactive { get; init; } = new();

    /// <summary>A permanent install: the schedule, adverts and announcements, remote administration, updates. Off by default.</summary>
    public InstallConfig Install { get; init; } = new();
    public StingerConfig Stingers { get; init; } = new();

    /// <summary>Break music (Spotify) — background sound between the show's own content.</summary>
    public SpotifyConfig Spotify { get; init; } = new();

    public WatchdogConfig Watchdog { get; init; } = new();

    /// <summary>A twin: a second Patterns kept in step with this one, on this machine or another, that can take the show. This machine's own — never mirrored.</summary>
    public TwinConfig Twin { get; init; } = new();

    /// <summary>The show lock: what the machine is held off during the show — this machine's own, never mirrored to a twin.</summary>
    public LockConfig Lock { get; init; } = new();
    public StreamConfig Stream { get; init; } = new();
    public AdminConfig Admin { get; init; } = new();
    public SwitcherConfig Switcher { get; init; } = new();

    /// <summary>What the desk's own speakers are listening to; the programme unless the operator says otherwise.</summary>
    public MonitorConfig Monitor { get; init; } = new();

    /// <summary>Which soundtrack goes where: sources × destinations, off until the show asks for it (<see cref="Services.AudioRouting"/>).</summary>
    public AudioRoutingConfig AudioRouting { get; init; } = new();
    public DeskLayoutConfig Desk { get; init; } = new();

    /// <summary>The lower thirds: the designs, and the one on air since when.</summary>
    public global::Patterns.Core.LowerThirds.LowerThirdsConfig LowerThirds { get; init; } = new();

    /// <summary>Operator nicknames for live inputs, keyed "ndi:&lt;source&gt;" / "cap:&lt;device&gt;".</summary>
    public ShowCollection<InputLabelConfig> InputLabels { get; init; } = new();

    /// <summary>The mode each capture device opens in ("1920x1080@60"), by device name; absent = the device's default.</summary>
    public ShowCollection<CaptureFormatConfig> CaptureFormats { get; init; } = new();

    /// <summary>The stored mode key for a capture device, or "" for the device's default.</summary>
    public string CaptureFormatFor(string device)
    {
        foreach (var f in CaptureFormats)
        {
            if (string.Equals(f.Device, device, StringComparison.OrdinalIgnoreCase)) return f.Format;
        }
        return "";
    }

    /// <summary>Sets (or clears, with "") the mode a capture device opens in. An entry with neither a mode nor the low-latency profile goes.</summary>
    public void SetCaptureFormat(string device, string format)
    {
        if (string.IsNullOrWhiteSpace(device)) return;
        var existing = CaptureFormats.FirstOrDefault(f => string.Equals(f.Device, device, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(format))
        {
            if (existing is null) return;
            if (existing.LowLatency) existing.Format = "";
            else CaptureFormats.Remove(existing);
            return;
        }
        if (existing is null) CaptureFormats.Add(new CaptureFormatConfig { Device = device, Format = format });
        else existing.Format = format;
    }

    /// <summary>Whether a capture device opens in the low-latency profile (<see cref="CaptureFormatConfig.LowLatency"/>).</summary>
    public bool CaptureLowLatencyFor(string device)
    {
        foreach (var f in CaptureFormats)
        {
            if (string.Equals(f.Device, device, StringComparison.OrdinalIgnoreCase)) return f.LowLatency;
        }
        return false;
    }

    /// <summary>Sets the low-latency profile for a capture device; an entry with neither a mode nor the profile goes.</summary>
    public void SetCaptureLowLatency(string device, bool lowLatency)
    {
        if (string.IsNullOrWhiteSpace(device)) return;
        var existing = CaptureFormats.FirstOrDefault(f => string.Equals(f.Device, device, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            if (lowLatency) CaptureFormats.Add(new CaptureFormatConfig { Device = device, LowLatency = true });
            return;
        }
        if (!lowLatency && existing.Format.Length == 0) CaptureFormats.Remove(existing);
        else existing.LowLatency = lowLatency;
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
    public ShowCollection<MediaLibraryEntry> MediaLibrary { get; init; } = new();
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

    public string Path { get => _path; set => Set(ref _path, value ?? ""); }

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
        if (Services.PlaylistSequencer.IsDeckPath(path)) return LibraryMediaKind.Deck;
        if (Services.PlaylistSequencer.IsVideoPath(path)) return LibraryMediaKind.Video;
        if (Services.PlaylistSequencer.IsAudioPath(path)) return LibraryMediaKind.Audio;
        if (Services.PlaylistSequencer.IsMediaPath(path)) return LibraryMediaKind.Image; // a known picture extension
        return isVideo ? LibraryMediaKind.Video : LibraryMediaKind.Image;
    }
}
