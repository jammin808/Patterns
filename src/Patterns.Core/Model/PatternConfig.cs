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

/// <summary>Media playlist: explicit items (in custom order) plus scanned folders, looped.</summary>
public sealed class PlaylistOptions : Observable
{
    private double _imageDwellSeconds = 8;
    private bool _videoFullLength = true;
    private bool _shuffle;
    private int _shuffleSeed = 1;
    private bool _includeImages = true;
    private bool _includeVideos = true;

    public ObservableCollection<PlaylistItemConfig> Items { get; init; } = new();
    /// <summary>Folders scanned (recursively) for media; results play after the explicit items, name-sorted.</summary>
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
}
