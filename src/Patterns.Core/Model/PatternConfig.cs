using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Patterns.Core.Model;

/// <summary>The pattern canvas — the pixel space patterns are drawn in.</summary>
public sealed class CanvasConfig : Observable
{
    private bool _followOutput = true;
    private int _width = 1920;
    private int _height = 1080;
    private CanvasScaleMode _scaleMode = CanvasScaleMode.Fit;

    /// <summary>When true the canvas always matches the output (or span union / NDI) size — 1:1 pixels.</summary>
    public bool FollowOutput { get => _followOutput; set => Set(ref _followOutput, value); }
    public int Width { get => _width; set => Set(ref _width, Math.Clamp(value, 16, 16384)); }
    public int Height { get => _height; set => Set(ref _height, Math.Clamp(value, 16, 16384)); }
    /// <summary>How a canvas that differs from the sink size is mapped onto it.</summary>
    public CanvasScaleMode ScaleMode { get => _scaleMode; set => Set(ref _scaleMode, value); }
}

public sealed class GridOptions : Observable
{
    private int _cellSize = 96;
    private int _subdivisions = 0;
    private int _lineWidth = 1;
    private bool _showCenterCross = true;
    private bool _showBorder = true;
    private bool _showDiagonals = false;
    private bool _showLabel = true;

    /// <summary>Major grid pitch in pixels.</summary>
    public int CellSize { get => _cellSize; set => Set(ref _cellSize, Math.Clamp(value, 2, 4096)); }
    /// <summary>Minor lines inside each cell (0 = none).</summary>
    public int Subdivisions { get => _subdivisions; set => Set(ref _subdivisions, Math.Clamp(value, 0, 16)); }
    public int LineWidth { get => _lineWidth; set => Set(ref _lineWidth, Math.Clamp(value, 1, 8)); }
    public bool ShowCenterCross { get => _showCenterCross; set => Set(ref _showCenterCross, value); }
    public bool ShowBorder { get => _showBorder; set => Set(ref _showBorder, value); }
    public bool ShowDiagonals { get => _showDiagonals; set => Set(ref _showDiagonals, value); }
    public bool ShowLabel { get => _showLabel; set => Set(ref _showLabel, value); }
}

public sealed class CheckerOptions : Observable
{
    private int _cellSize = 64;
    private bool _animate = false;
    private double _intervalSeconds = 1.0;

    public int CellSize { get => _cellSize; set => Set(ref _cellSize, Math.Clamp(value, 1, 4096)); }
    /// <summary>Swap the two colours at the configured interval (LED latency / refresh check).</summary>
    public bool Animate { get => _animate; set => Set(ref _animate, value); }
    public double IntervalSeconds { get => _intervalSeconds; set => Set(ref _intervalSeconds, Math.Clamp(value, 0.05, 60)); }
}

public sealed class BarsOptions : Observable
{
    private BarsVariant _variant = BarsVariant.Smpte;
    private bool _fullRange = true;

    public BarsVariant Variant { get => _variant; set => Set(ref _variant, value); }
    /// <summary>
    /// SMPTE bars are defined in narrow range (16–235). Event pipelines are usually full range —
    /// when set, levels are stretched to 0–255. Full-height bar variants are always full range.
    /// </summary>
    public bool FullRange { get => _fullRange; set => Set(ref _fullRange, value); }
}

public sealed class RampOptions : Observable
{
    private RampVariant _variant = RampVariant.GrayHorizontal;
    private int _steps = 16;
    private bool _showMarkers = true;

    public RampVariant Variant { get => _variant; set => Set(ref _variant, value); }
    /// <summary>Step count for the stepped variant (banding / bit-depth check).</summary>
    public int Steps { get => _steps; set => Set(ref _steps, Math.Clamp(value, 2, 256)); }
    public bool ShowMarkers { get => _showMarkers; set => Set(ref _showMarkers, value); }
}

public sealed class FocusOptions : Observable
{
    private bool _showStar = true;
    private bool _showLinePairs = true;
    private bool _showText = true;

    public bool ShowStar { get => _showStar; set => Set(ref _showStar, value); }
    public bool ShowLinePairs { get => _showLinePairs; set => Set(ref _showLinePairs, value); }
    public bool ShowText { get => _showText; set => Set(ref _showText, value); }
}

public sealed class GeometryOptions : Observable
{
    private bool _showCircles = true;
    private bool _showSafeAreas = true;
    private bool _showAspectMarkers = false;
    private bool _showCrosshair = true;
    private bool _showDiagonals = true;
    private double _actionSafePct = 93;
    private double _titleSafePct = 90;

    public bool ShowCircles { get => _showCircles; set => Set(ref _showCircles, value); }
    public bool ShowSafeAreas { get => _showSafeAreas; set => Set(ref _showSafeAreas, value); }
    public bool ShowAspectMarkers { get => _showAspectMarkers; set => Set(ref _showAspectMarkers, value); }
    public bool ShowCrosshair { get => _showCrosshair; set => Set(ref _showCrosshair, value); }
    public bool ShowDiagonals { get => _showDiagonals; set => Set(ref _showDiagonals, value); }
    public double ActionSafePct { get => _actionSafePct; set => Set(ref _actionSafePct, Math.Clamp(value, 50, 100)); }
    public double TitleSafePct { get => _titleSafePct; set => Set(ref _titleSafePct, Math.Clamp(value, 50, 100)); }
}

public sealed class FlatFieldOptions : Observable
{
    private string _color = "#FFFFFF";
    private double _levelPct = 100;
    private bool _showLabel = true;
    private bool _showBorder = false;

    /// <summary>Base colour; the level percentage scales it (100 = as-is).</summary>
    public string Color { get => _color; set => Set(ref _color, value); }
    public double LevelPct { get => _levelPct; set => Set(ref _levelPct, Math.Clamp(value, 0, 100)); }
    public bool ShowLabel { get => _showLabel; set => Set(ref _showLabel, value); }
    public bool ShowBorder { get => _showBorder; set => Set(ref _showBorder, value); }
}

/// <summary>One panel in an irregular LED map (canvas-space pixels).</summary>
public sealed class LedTileConfig : Observable
{
    private int _x;
    private int _y;
    private int _width = 128;
    private int _height = 128;
    private string _label = "";

    public int X { get => _x; set => Set(ref _x, Math.Clamp(value, 0, 32768)); }
    public int Y { get => _y; set => Set(ref _y, Math.Clamp(value, 0, 32768)); }
    public int Width { get => _width; set => Set(ref _width, Math.Clamp(value, 8, 4096)); }
    public int Height { get => _height; set => Set(ref _height, Math.Clamp(value, 8, 4096)); }
    /// <summary>Optional label; empty = automatic number in list order.</summary>
    public string Label { get => _label; set => Set(ref _label, value); }
}

/// <summary>Defines an LED wall as tiles (panels/cabinets) of a fixed pixel size.</summary>
public sealed class LedWallOptions : Observable
{
    private int _tileWidth = 128;
    private int _tileHeight = 128;
    private bool _defineByCanvas = false;
    private int _columns = 10;
    private int _rows = 6;
    private int _canvasWidth = 1280;
    private int _canvasHeight = 768;
    private TileNumbering _numbering = TileNumbering.RowCol;
    private bool _showTileBorders = true;
    private bool _alternateTint = true;
    private bool _showPixelGrid = false;
    private int _pixelGridStep = 8;
    private bool _showCenterCross = true;
    private bool _showInfo = true;
    private bool _showTileDiagonals = false;
    private bool _useCustomMap;

    /// <summary>Irregular wall: use <see cref="CustomTiles"/> (mixed sizes, offsets, gaps) instead of the grid.</summary>
    public bool UseCustomMap { get => _useCustomMap; set => Set(ref _useCustomMap, value); }

    public ObservableCollection<LedTileConfig> CustomTiles { get; init; } = new();

    /// <summary>Pixel width of one LED panel/cabinet (free input; presets offered by the UI).</summary>
    public int TileWidth { get => _tileWidth; set => Set(ref _tileWidth, Math.Clamp(value, 8, 1024)); }
    public int TileHeight { get => _tileHeight; set => Set(ref _tileHeight, Math.Clamp(value, 8, 1024)); }
    /// <summary>
    /// false: wall is columns×rows of tiles (canvas derived).
    /// true: canvas size is given and the tile grid is derived (edge tiles may be partial — as on real walls).
    /// </summary>
    public bool DefineByCanvas { get => _defineByCanvas; set => Set(ref _defineByCanvas, value); }
    public int Columns { get => _columns; set => Set(ref _columns, Math.Clamp(value, 1, 512)); }
    public int Rows { get => _rows; set => Set(ref _rows, Math.Clamp(value, 1, 512)); }
    public int CanvasWidth { get => _canvasWidth; set => Set(ref _canvasWidth, Math.Clamp(value, 16, 16384)); }
    public int CanvasHeight { get => _canvasHeight; set => Set(ref _canvasHeight, Math.Clamp(value, 16, 16384)); }
    public TileNumbering Numbering { get => _numbering; set => Set(ref _numbering, value); }
    public bool ShowTileBorders { get => _showTileBorders; set => Set(ref _showTileBorders, value); }
    /// <summary>Subtle checkerboard tint so neighbouring tiles are distinguishable at distance.</summary>
    public bool AlternateTint { get => _alternateTint; set => Set(ref _alternateTint, value); }
    public bool ShowPixelGrid { get => _showPixelGrid; set => Set(ref _showPixelGrid, value); }
    public int PixelGridStep { get => _pixelGridStep; set => Set(ref _pixelGridStep, Math.Clamp(value, 2, 256)); }
    public bool ShowCenterCross { get => _showCenterCross; set => Set(ref _showCenterCross, value); }
    public bool ShowInfo { get => _showInfo; set => Set(ref _showInfo, value); }
    public bool ShowTileDiagonals { get => _showTileDiagonals; set => Set(ref _showTileDiagonals, value); }
}

/// <summary>A video wall built from standard-resolution display elements.</summary>
public sealed class VideoWallOptions : Observable
{
    private int _elementWidth = 1920;
    private int _elementHeight = 1080;
    private bool _portrait = false;
    private int _columns = 2;
    private int _rows = 2;
    private int _bezelPx = 0;
    private bool _showNumbers = true;
    private bool _showBorders = true;
    private bool _showDiagonals = true;
    private bool _showCenters = true;
    private bool _showInfo = true;

    public int ElementWidth { get => _elementWidth; set => Set(ref _elementWidth, Math.Clamp(value, 16, 16384)); }
    public int ElementHeight { get => _elementHeight; set => Set(ref _elementHeight, Math.Clamp(value, 16, 16384)); }
    /// <summary>Rotate elements 90° (width/height swapped).</summary>
    public bool Portrait { get => _portrait; set => Set(ref _portrait, value); }
    public int Columns { get => _columns; set => Set(ref _columns, Math.Clamp(value, 1, 64)); }
    public int Rows { get => _rows; set => Set(ref _rows, Math.Clamp(value, 1, 64)); }
    /// <summary>Bezel/gap visualisation width in px, hatched inside each element edge.</summary>
    public int BezelPx { get => _bezelPx; set => Set(ref _bezelPx, Math.Clamp(value, 0, 512)); }
    public bool ShowNumbers { get => _showNumbers; set => Set(ref _showNumbers, value); }
    public bool ShowBorders { get => _showBorders; set => Set(ref _showBorders, value); }
    public bool ShowDiagonals { get => _showDiagonals; set => Set(ref _showDiagonals, value); }
    public bool ShowCenters { get => _showCenters; set => Set(ref _showCenters, value); }
    public bool ShowInfo { get => _showInfo; set => Set(ref _showInfo, value); }
}

/// <summary>Edge-blended projection row/column.</summary>
public sealed class BlendOptions : Observable
{
    private int _projectors = 2;
    private int _nativeWidth = 1920;
    private int _nativeHeight = 1200;
    private int _overlapPx = 320;
    private BlendOrientation _orientation = BlendOrientation.Horizontal;
    private BlendCurve _curve = BlendCurve.SCurve;
    private bool _showGrids = true;
    private int _gridSize = 96;
    private bool _showRamps = true;
    private bool _showMarkers = true;
    private bool _hueCode = true;
    private bool _grayCheck = false;
    private bool _showInfo = true;

    public int Projectors { get => _projectors; set => Set(ref _projectors, Math.Clamp(value, 2, 12)); }
    public int NativeWidth { get => _nativeWidth; set => Set(ref _nativeWidth, Math.Clamp(value, 320, 8192)); }
    public int NativeHeight { get => _nativeHeight; set => Set(ref _nativeHeight, Math.Clamp(value, 240, 8192)); }
    public int OverlapPx { get => _overlapPx; set => Set(ref _overlapPx, Math.Clamp(value, 8, 4096)); }
    public BlendOrientation Orientation { get => _orientation; set => Set(ref _orientation, value); }
    public BlendCurve Curve { get => _curve; set => Set(ref _curve, value); }
    public bool ShowGrids { get => _showGrids; set => Set(ref _showGrids, value); }
    public int GridSize { get => _gridSize; set => Set(ref _gridSize, Math.Clamp(value, 8, 1024)); }
    /// <summary>Draw the blend curve as luminance ramps inside each overlap zone.</summary>
    public bool ShowRamps { get => _showRamps; set => Set(ref _showRamps, value); }
    public bool ShowMarkers { get => _showMarkers; set => Set(ref _showMarkers, value); }
    /// <summary>Tint each projector's region with its own hue.</summary>
    public bool HueCode { get => _hueCode; set => Set(ref _hueCode, value); }
    /// <summary>Flat 50% grey full-canvas — reveals double brightness where a blend is wrong.</summary>
    public bool GrayCheck { get => _grayCheck; set => Set(ref _grayCheck, value); }
    public bool ShowInfo { get => _showInfo; set => Set(ref _showInfo, value); }
}

public sealed class MotionOptions : Observable
{
    private MotionVariant _variant = MotionVariant.MovingBar;
    private double _speedPxPerSec = 480;
    private int _pxPerFrame = 0;
    private int _barThickness = 64;
    private bool _vertical = false;
    private bool _showFps = true;
    private double _boxSizePct = 18;
    private double _zonePlateScale = 1.0;

    public MotionVariant Variant { get => _variant; set => Set(ref _variant, value); }
    public double SpeedPxPerSec { get => _speedPxPerSec; set => Set(ref _speedPxPerSec, Math.Clamp(value, 1, 20000)); }
    /// <summary>When &gt; 0 the bar advances exactly this many pixels per rendered frame (judder test).</summary>
    public int PxPerFrame { get => _pxPerFrame; set => Set(ref _pxPerFrame, Math.Clamp(value, 0, 512)); }
    public int BarThickness { get => _barThickness; set => Set(ref _barThickness, Math.Clamp(value, 1, 2048)); }
    /// <summary>Bar travels vertically instead of horizontally.</summary>
    public bool Vertical { get => _vertical; set => Set(ref _vertical, value); }
    public bool ShowFps { get => _showFps; set => Set(ref _showFps, value); }
    public double BoxSizePct { get => _boxSizePct; set => Set(ref _boxSizePct, Math.Clamp(value, 4, 60)); }
    public double ZonePlateScale { get => _zonePlateScale; set => Set(ref _zonePlateScale, Math.Clamp(value, 0.05, 20)); }
}

public sealed class ColorCycleOptions : Observable
{
    private double _intervalSeconds = 2.0;
    private bool _fade = false;
    private bool _useBrandColors = false;
    private string _colorsCsv = "#FF0000,#00FF00,#0000FF,#FFFFFF,#000000";
    private bool _showLabel = true;

    public double IntervalSeconds { get => _intervalSeconds; set => Set(ref _intervalSeconds, Math.Clamp(value, 0.1, 600)); }
    public bool Fade { get => _fade; set => Set(ref _fade, value); }
    public bool UseBrandColors { get => _useBrandColors; set => Set(ref _useBrandColors, value); }
    /// <summary>Comma-separated hex colours.</summary>
    public string ColorsCsv { get => _colorsCsv; set => Set(ref _colorsCsv, value); }
    public bool ShowLabel { get => _showLabel; set => Set(ref _showLabel, value); }
}

/// <summary>One entry in a media playlist.</summary>
public sealed class PlaylistItemConfig : Observable
{
    private string _path = "";
    private double _durationSeconds;
    private string _scheduledTime = "";
    private double _scheduledDurationSeconds = 60;
    private bool _isNowPlaying;

    public string Path { get => _path; set => Set(ref _path, value); }

    /// <summary>Runtime-only: this row is on screen right now (set by the playlist service).</summary>
    [JsonIgnore]
    public bool IsNowPlaying { get => _isNowPlaying; set => Set(ref _isNowPlaying, value); }
    /// <summary>Seconds to hold this item; 0 = default (image dwell, or the video's natural length).</summary>
    public double DurationSeconds { get => _durationSeconds; set => Set(ref _durationSeconds, Math.Clamp(value, 0, 24 * 3600)); }
    /// <summary>"HH:mm" — when set, this item interrupts the cycle daily at that time.</summary>
    public string ScheduledTime { get => _scheduledTime; set => Set(ref _scheduledTime, value); }
    /// <summary>How long a scheduled interruption holds before the cycle resumes.</summary>
    public double ScheduledDurationSeconds { get => _scheduledDurationSeconds; set => Set(ref _scheduledDurationSeconds, Math.Clamp(value, 1, 24 * 3600)); }
}

/// <summary>One named part of the show ("Walk-in", "Break") with its own files and folders.</summary>
public sealed class PlaylistSectionConfig : Observable
{
    private string _name = "Part 1";
    private string _startTime = "";
    private bool _isOnAir;

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>"HH:mm" — when set, this section takes over daily at that time.</summary>
    public string StartTime { get => _startTime; set => Set(ref _startTime, value); }

    /// <summary>Runtime-only: this is the section playing right now (drives the chip highlight).</summary>
    [JsonIgnore]
    public bool IsOnAir { get => _isOnAir; set => Set(ref _isOnAir, value); }

    public ObservableCollection<PlaylistItemConfig> Items { get; init; } = new();
    public ObservableCollection<string> Folders { get; init; } = new();
}

/// <summary>Media playlist: named sections for parts of the show; the active one loops.</summary>
public sealed class PlaylistOptions : Observable
{
    private double _imageDwellSeconds = 8;
    private bool _videoFullLength = true;
    private bool _shuffle;
    private int _shuffleSeed = 1;
    private bool _includeImages = true;
    private bool _includeVideos = true;
    private int _activeSection;

    /// <summary>The show's parts; exactly one plays at a time (see <see cref="ActiveSection"/>).</summary>
    public ObservableCollection<PlaylistSectionConfig> Sections { get; init; } = new();

    /// <summary>Index of the section on air (clamped by readers; persists across restarts).</summary>
    public int ActiveSection { get => _activeSection; set => Set(ref _activeSection, Math.Max(0, value)); }

    /// <summary>Legacy flat list (pre-sections) — migrated into the first section on load.</summary>
    public ObservableCollection<PlaylistItemConfig> Items { get; init; } = new();
    /// <summary>Legacy folder list (pre-sections) — migrated into the first section on load.</summary>
    public ObservableCollection<string> Folders { get; init; } = new();

    public double ImageDwellSeconds { get => _imageDwellSeconds; set => Set(ref _imageDwellSeconds, Math.Clamp(value, 1, 3600)); }
    /// <summary>Play videos to their end (needs libVLC); off = videos get the image dwell time.</summary>
    public bool VideoFullLength { get => _videoFullLength; set => Set(ref _videoFullLength, value); }
    public bool Shuffle { get => _shuffle; set => Set(ref _shuffle, value); }
    public int ShuffleSeed { get => _shuffleSeed; set => Set(ref _shuffleSeed, value); }
    public bool IncludeImages { get => _includeImages; set => Set(ref _includeImages, value); }
    public bool IncludeVideos { get => _includeVideos; set => Set(ref _includeVideos, value); }
}

public sealed class MediaOptions : Observable
{
    private MediaSource _source = MediaSource.Image;
    private string _imagePath = "";
    private string _videoPath = "";
    private FitMode _fit = FitMode.Fit;
    private bool _loop = true;
    private bool _mute; // sound on by default — an operator mutes deliberately
    private double _volumePct = 100;
    private string _backgroundColor = "#000000";
    private string _ndiSourceName = "";
    private string _captureDevice = "";
    private string _webUrl = "";
    private int _webWidth = 1920;
    private int _webHeight = 1080;
    private double _webZoomPct = 100;
    private bool _webShowPointer = true;

    public MediaSource Source { get => _source; set => Set(ref _source, value); }
    public string ImagePath { get => _imagePath; set => Set(ref _imagePath, value); }
    public string VideoPath { get => _videoPath; set => Set(ref _videoPath, value); }
    public FitMode Fit { get => _fit; set => Set(ref _fit, value); }
    public bool Loop { get => _loop; set => Set(ref _loop, value); }
    public bool Mute { get => _mute; set => Set(ref _mute, value); }
    /// <summary>Playback volume (0–125%; above 100 uses libVLC's software gain).</summary>
    public double VolumePct { get => _volumePct; set => Set(ref _volumePct, Math.Clamp(value, 0, 125)); }
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }

    /// <summary>NDI source to receive ("MACHINE (Sender)") when <see cref="Source"/> is NdiFeed.</summary>
    public string NdiSourceName { get => _ndiSourceName; set => Set(ref _ndiSourceName, value); }
    /// <summary>DirectShow video device name (HDMI/SDI capture) when <see cref="Source"/> is Capture.</summary>
    public string CaptureDevice { get => _captureDevice; set => Set(ref _captureDevice, value); }

    /// <summary>The page shown when <see cref="Source"/> is Web: https://… (a bare host is taken as https) or a local HTML file.</summary>
    public string WebUrl { get => _webUrl; set => Set(ref _webUrl, value ?? ""); }
    /// <summary>The page's own viewport in CSS pixels — what it lays itself out for; the picture is then fitted like any other.</summary>
    public int WebWidth { get => _webWidth; set => Set(ref _webWidth, Math.Clamp(value, 320, 7680)); }
    public int WebHeight { get => _webHeight; set => Set(ref _webHeight, Math.Clamp(value, 240, 4320)); }
    /// <summary>The browser's zoom (25–400 %): larger type for a schedule on a big screen without touching the page. Applied live.</summary>
    [TransitionNeutral] public double WebZoomPct { get => _webZoomPct; set => Set(ref _webZoomPct, Math.Clamp(value, 25, 400)); }
    /// <summary>Draw the desk's pointer and its clicks on the page wherever it is shown — off for a page nobody drives.</summary>
    [TransitionNeutral] public bool WebShowPointer { get => _webShowPointer; set => Set(ref _webShowPointer, value); }

    public PlaylistOptions Playlist { get; init; } = new();
}

/// <summary>The particle mini-studio parameters.</summary>
public sealed class ParticleOptions : Observable
{
    private string _preset = "Snow";
    private int _count = 800;
    private ParticleEmitter _emitter = ParticleEmitter.TopEdge;
    private ParticleShape _shape = ParticleShape.Circle;
    private double _sizeMin = 2;
    private double _sizeMax = 6;
    private double _speedMin = 30;
    private double _speedMax = 90;
    private double _directionDeg = 90;
    private double _spreadDeg = 25;
    private double _gravityY = 0;
    private double _windX = 10;
    private double _wobble = 0.6;
    private double _rotationSpeed = 0;
    private bool _glow = false;
    private bool _useBrandColors = false;
    private string _colorsCsv = "#FFFFFF";
    private string _backgroundColor = "#000000";
    private int _seed = 20260829;

    public string Preset { get => _preset; set => Set(ref _preset, value); }
    public int Count { get => _count; set => Set(ref _count, Math.Clamp(value, 1, 20000)); }
    public ParticleEmitter Emitter { get => _emitter; set => Set(ref _emitter, value); }
    public ParticleShape Shape { get => _shape; set => Set(ref _shape, value); }
    public double SizeMin { get => _sizeMin; set => Set(ref _sizeMin, Math.Clamp(value, 0.5, 512)); }
    public double SizeMax { get => _sizeMax; set => Set(ref _sizeMax, Math.Clamp(value, 0.5, 512)); }
    /// <summary>Initial speed range, px/s.</summary>
    public double SpeedMin { get => _speedMin; set => Set(ref _speedMin, Math.Clamp(value, 0, 5000)); }
    public double SpeedMax { get => _speedMax; set => Set(ref _speedMax, Math.Clamp(value, 0, 5000)); }
    /// <summary>Emission direction in degrees; 0 = right, 90 = down.</summary>
    public double DirectionDeg { get => _directionDeg; set => Set(ref _directionDeg, value); }
    public double SpreadDeg { get => _spreadDeg; set => Set(ref _spreadDeg, Math.Clamp(value, 0, 360)); }
    /// <summary>Downward acceleration px/s² (negative = rise).</summary>
    public double GravityY { get => _gravityY; set => Set(ref _gravityY, Math.Clamp(value, -3000, 3000)); }
    public double WindX { get => _windX; set => Set(ref _windX, Math.Clamp(value, -3000, 3000)); }
    /// <summary>Sideways sinusoidal drift amount (0–1).</summary>
    public double Wobble { get => _wobble; set => Set(ref _wobble, Math.Clamp(value, 0, 1)); }
    /// <summary>Rotation speed in turns/s (for square/star/streak/logo).</summary>
    public double RotationSpeed { get => _rotationSpeed; set => Set(ref _rotationSpeed, Math.Clamp(value, -10, 10)); }
    /// <summary>Additive blending — reads beautifully on LED walls.</summary>
    public bool Glow { get => _glow; set => Set(ref _glow, value); }
    public bool UseBrandColors { get => _useBrandColors; set => Set(ref _useBrandColors, value); }
    public string ColorsCsv { get => _colorsCsv; set => Set(ref _colorsCsv, value); }
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    public int Seed { get => _seed; set => Set(ref _seed, value); }
}

/// <summary>One multiview tile: a source plus an optional label override.</summary>
public sealed class MultiviewTileConfig : Observable
{
    private MultiviewSource _source = MultiviewSource.Program;
    private string _screenId = "";
    private string _label = "";
    private string _input = "";

    public MultiviewSource Source { get => _source; set => Set(ref _source, value); }

    /// <summary>
    /// Which content target, when <see cref="Source"/> is Screen: a screen id, or a joined
    /// canvas's sorted member key (<c>a+b</c>) — the same id an output assignment and a wall
    /// tile use. A member screen of a joined canvas renders that member's slice of the canvas.
    /// Empty = nothing picked yet. A cleared picker writes null; that lands as empty.
    /// </summary>
    public string ScreenId { get => _screenId; set => Set(ref _screenId, value ?? ""); }

    /// <summary>
    /// Which input, when <see cref="Source"/> is NdiFeed (source name) or Capture (device
    /// name). Empty NdiFeed = the first NDI source the show references.
    /// </summary>
    public string Input { get => _input; set => Set(ref _input, value); }

    /// <summary>Label override; empty = automatic (screen label, input nickname, source name).</summary>
    public string Label { get => _label; set => Set(ref _label, value); }
}

/// <summary>
/// The customisable multiview: a tiled monitor wall — program, per-screen content, live
/// inputs and a clock — rendered by the engine, so it goes anywhere a pattern goes
/// (an operator screen, an NDI sender) and to the remote /multiview page.
/// </summary>
public sealed class MultiviewOptions : Observable
{
    private int _columns;
    private bool _showLabels = true;
    private bool _showTally = true;

    public ObservableCollection<MultiviewTileConfig> Tiles { get; init; } = new();

    /// <summary>Grid columns; 0 = automatic (square-ish).</summary>
    public int Columns { get => _columns; set => Set(ref _columns, Math.Clamp(value, 0, 8)); }
    public bool ShowLabels { get => _showLabels; set => Set(ref _showLabels, value); }
    /// <summary>Red border on tiles that are on air (outputs live and screen enabled).</summary>
    public bool ShowTally { get => _showTally; set => Set(ref _showTally, value); }
}

/// <summary>What a layer draws. First member is the fallback for a value this build does not know.</summary>
public enum LayerSource
{
    Image,
    Video,
    NdiFeed,
    Capture,
    /// <summary>Another target's picture — a screen or a joined canvas, by id.</summary>
    Screen,
    /// <summary>A web page rendered inside the engine (WebView2), driven from the desk's PREVIEW pane.</summary>
    Web,
}

/// <summary>
/// One of the two layers over a target's pattern: any picture — a still, a clip, an NDI feed, a
/// capture device, another target's picture — in a box given as a share of the canvas, with a
/// fit, a crop, corners, a border and an opacity. Off by default; drawn after the pattern and
/// under the overlays on every sink that shows the canvas, so spans, NDI and the stream carry it.
/// The box is transition-neutral: dragging a layer never starts a crossfade, a new picture does.
/// </summary>
public sealed class LayerConfig : Observable
{
    private bool _enabled;
    private LayerSource _source = LayerSource.Image;
    private string _imagePath = "";
    private string _videoPath = "";
    private string _ndiSourceName = "";
    private string _captureDevice = "";
    private string _targetId = "";
    private string _webUrl = "";
    private int _webWidth = 1280;
    private int _webHeight = 720;
    private double _webZoomPct = 100;
    private bool _webShowPointer = true;
    private double _xPct = 5;
    private double _yPct = 5;
    private double _wPct = 40;
    private double _hPct = 40;
    private FitMode _fit = FitMode.Fill;
    private double _opacity = 1;
    private double _cornerPx;
    private double _borderPx;
    private string _borderColor = "#FFFFFF";
    private double _cropLeftPct; private double _cropTopPct; private double _cropRightPct; private double _cropBottomPct;
    private bool _loop = true;
    private bool _mute = true;
    private double _volumePct = 100;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public LayerSource Source { get => _source; set => Set(ref _source, value); }
    public string ImagePath { get => _imagePath; set => Set(ref _imagePath, value ?? ""); }
    public string VideoPath { get => _videoPath; set => Set(ref _videoPath, value ?? ""); }
    public string NdiSourceName { get => _ndiSourceName; set => Set(ref _ndiSourceName, value ?? ""); }
    public string CaptureDevice { get => _captureDevice; set => Set(ref _captureDevice, value ?? ""); }
    /// <summary>The screen id or canvas key a Screen layer shows.</summary>
    public string TargetId { get => _targetId; set => Set(ref _targetId, value ?? ""); }

    /// <summary>The page a Web layer shows (https://…, a bare host taken as https, or a local HTML file).</summary>
    public string WebUrl { get => _webUrl; set => Set(ref _webUrl, value ?? ""); }
    /// <summary>The page's own viewport in CSS pixels; the picture is then fitted into the box like any other.</summary>
    public int WebWidth { get => _webWidth; set => Set(ref _webWidth, Math.Clamp(value, 320, 7680)); }
    public int WebHeight { get => _webHeight; set => Set(ref _webHeight, Math.Clamp(value, 240, 4320)); }
    [TransitionNeutral] public double WebZoomPct { get => _webZoomPct; set => Set(ref _webZoomPct, Math.Clamp(value, 25, 400)); }
    /// <summary>Draw the desk's pointer and its clicks on the page wherever the layer is shown.</summary>
    [TransitionNeutral] public bool WebShowPointer { get => _webShowPointer; set => Set(ref _webShowPointer, value); }

    /// <summary>The box, as a share of the canvas: its top-left (may sit partly off the canvas) and its size.</summary>
    [TransitionNeutral] public double XPct { get => _xPct; set => Set(ref _xPct, Math.Clamp(value, -100, 100)); }
    [TransitionNeutral] public double YPct { get => _yPct; set => Set(ref _yPct, Math.Clamp(value, -100, 100)); }
    [TransitionNeutral] public double WPct { get => _wPct; set => Set(ref _wPct, Math.Clamp(value, 1, 200)); }
    [TransitionNeutral] public double HPct { get => _hPct; set => Set(ref _hPct, Math.Clamp(value, 1, 200)); }

    /// <summary>How the picture sits in the box: Fill crops to cover it, Fit letterboxes, Stretch ignores the shape, Center draws 1:1.</summary>
    public FitMode Fit { get => _fit; set => Set(ref _fit, value); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0, 1)); }
    /// <summary>Rounded corners, in canvas pixels.</summary>
    public double CornerPx { get => _cornerPx; set => Set(ref _cornerPx, Math.Clamp(value, 0, 500)); }
    /// <summary>A border drawn inside the box, in canvas pixels; 0 = none.</summary>
    public double BorderPx { get => _borderPx; set => Set(ref _borderPx, Math.Clamp(value, 0, 200)); }
    public string BorderColor { get => _borderColor; set => Set(ref _borderColor, value ?? ""); }

    /// <summary>Cut this share of the picture away on each side (0–45 %) before it is fitted.</summary>
    public double CropLeftPct { get => _cropLeftPct; set => Set(ref _cropLeftPct, Math.Clamp(value, 0, 45)); }
    public double CropTopPct { get => _cropTopPct; set => Set(ref _cropTopPct, Math.Clamp(value, 0, 45)); }
    public double CropRightPct { get => _cropRightPct; set => Set(ref _cropRightPct, Math.Clamp(value, 0, 45)); }
    public double CropBottomPct { get => _cropBottomPct; set => Set(ref _cropBottomPct, Math.Clamp(value, 0, 45)); }

    /// <summary>A clip loops; its sound is off unless asked for (b-roll under the pattern).</summary>
    public bool Loop { get => _loop; set => Set(ref _loop, value); }
    public bool Mute { get => _mute; set => Set(ref _mute, value); }
    public double VolumePct { get => _volumePct; set => Set(ref _volumePct, Math.Clamp(value, 0, 125)); }

    /// <summary>Something is chosen for the source (a path, a name, a target).</summary>
    [JsonIgnore]
    public bool HasSource => _source switch
    {
        LayerSource.Image => _imagePath.Length > 0,
        LayerSource.Video => _videoPath.Length > 0,
        LayerSource.NdiFeed => _ndiSourceName.Length > 0,
        LayerSource.Capture => _captureDevice.Length > 0,
        LayerSource.Web => _webUrl.Length > 0,
        _ => _targetId.Length > 0,
    };
}

/// <summary>Everything that describes what is drawn on the canvas (minus overlays).</summary>
public sealed class PatternConfig : Observable
{
    private PatternKind _kind = PatternKind.Grid;

    public PatternKind Kind { get => _kind; set => Set(ref _kind, value); }

    public CanvasConfig Canvas { get; init; } = new();
    public GridOptions Grid { get; init; } = new();
    public CheckerOptions Checker { get; init; } = new();
    public BarsOptions Bars { get; init; } = new();
    public RampOptions Ramp { get; init; } = new();
    public FocusOptions Focus { get; init; } = new();
    public GeometryOptions Geometry { get; init; } = new();
    public FlatFieldOptions FlatField { get; init; } = new();
    public LedWallOptions LedWall { get; init; } = new();
    public VideoWallOptions VideoWall { get; init; } = new();
    public BlendOptions Blend { get; init; } = new();
    public MotionOptions Motion { get; init; } = new();
    public ColorCycleOptions ColorCycle { get; init; } = new();
    public MediaOptions Media { get; init; } = new();
    public ParticleOptions Particles { get; init; } = new();
    public MultiviewOptions Multiview { get; init; } = new();
    public FractalOptions Fractal { get; init; } = new();

    /// <summary>Two pictures over the pattern, whatever its kind — see <see cref="LayerConfig"/>.</summary>
    public LayerConfig Layer1 { get; init; } = new();
    public LayerConfig Layer2 { get; init; } = new();
}

/// <summary>The Fractal pattern: a family, a view of the plane, a palette, motion, and the sound it listens to.</summary>
public sealed class FractalOptions : Observable
{
    private FractalKind _kind = FractalKind.Mandelbrot;
    private string _preset = "Mandelbrot classic";
    private double _zoom = 1;
    private double _centerX = -0.6;
    private double _centerY = 0;
    private int _iterations = 96;
    private double _speed = 0.5;
    private double _juliaReal = -0.72;
    private double _juliaImag = 0.27;
    private string _colorsCsv = "#0B0C2A,#1E3A8A,#3EC1F3,#FFFFFF,#FFB020";
    private double _audioAmount = 0.6;
    private AudioSourceKind _audioSource = AudioSourceKind.None;
    private string _audioDevice = "";
    private FractalQuality _quality = FractalQuality.Balanced;

    public FractalKind Kind { get => _kind; set => Set(ref _kind, value); }

    /// <summary>The scene last applied; a label, never read back.</summary>
    public string Preset { get => _preset; set => Set(ref _preset, value ?? ""); }

    /// <summary>1 = the whole set fits the canvas height; higher is closer in.</summary>
    public double Zoom { get => _zoom; set => Set(ref _zoom, Math.Clamp(value, 0.2, 1_000_000)); }

    public double CenterX { get => _centerX; set => Set(ref _centerX, Math.Clamp(value, -4, 4)); }

    public double CenterY { get => _centerY; set => Set(ref _centerY, Math.Clamp(value, -4, 4)); }

    /// <summary>Escape-time depth: more is finer and slower.</summary>
    public int Iterations { get => _iterations; set => Set(ref _iterations, Math.Clamp(value, 8, 1024)); }

    /// <summary>How fast the picture breathes and the palette drifts; 0 holds still.</summary>
    public double Speed { get => _speed; set => Set(ref _speed, Math.Clamp(value, 0, 5)); }

    public double JuliaReal { get => _juliaReal; set => Set(ref _juliaReal, Math.Clamp(value, -2, 2)); }

    public double JuliaImag { get => _juliaImag; set => Set(ref _juliaImag, Math.Clamp(value, -2, 2)); }

    /// <summary>Two to five colours the picture cycles through, as the particle studio writes them.</summary>
    public string ColorsCsv { get => _colorsCsv; set => Set(ref _colorsCsv, value ?? ""); }

    /// <summary>How much the sound moves the picture (0 = not at all).</summary>
    public double AudioAmount { get => _audioAmount; set => Set(ref _audioAmount, Math.Clamp(value, 0, 1)); }

    public AudioSourceKind AudioSource { get => _audioSource; set => Set(ref _audioSource, value); }

    /// <summary>The input to listen to when the source is External; a null from a picker that lost its items keeps the choice.</summary>
    public string AudioDevice { get => _audioDevice; set => Set(ref _audioDevice, value ?? _audioDevice); }

    public FractalQuality Quality { get => _quality; set => Set(ref _quality, value); }
}
