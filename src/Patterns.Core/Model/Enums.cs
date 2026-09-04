namespace Patterns.Core.Model;

/// <summary>Which pattern family is being rendered.</summary>
public enum PatternKind
{
    Grid,
    Checkerboard,
    ColorBars,
    Ramp,
    Focus,
    Geometry,
    FlatField,
    LedWall,
    VideoWall,
    ProjectionBlend,
    Motion,
    ColorCycle,
    Media,
    Particles,
    Multiview,
    Fractal,
}

/// <summary>What one multiview tile shows.</summary>
public enum MultiviewSource
{
    Program,
    Screen,
    NdiFeed,
    Pip,
    Clock,
    Capture,
}

/// <summary>How a pattern canvas maps onto a differently sized sink.</summary>
public enum CanvasScaleMode
{
    /// <summary>Scale preserving aspect, letterboxed.</summary>
    Fit,
    /// <summary>Centred, unscaled, 1:1 device pixels (cropped or bordered).</summary>
    OneToOne,
}

public enum BarsVariant
{
    /// <summary>SMPTE RP 219‑style HD bars (three bands with PLUGE).</summary>
    Smpte,
    /// <summary>EBU 100% full-height bars.</summary>
    Ebu100,
    /// <summary>75% amplitude full-height bars.</summary>
    Bars75,
    /// <summary>100% amplitude full-height bars.</summary>
    Bars100,
}

public enum RampVariant
{
    GrayHorizontal,
    GrayVertical,
    Rgb,
    Steps,
}

public enum MotionVariant
{
    MovingBar,
    BouncingBox,
    FrameFlash,
    ZonePlate,
    ScrollingGrid,
}

public enum BlendCurve
{
    Linear,
    Cosine,
    SCurve,
    Gamma22,
}

public enum BlendOrientation
{
    Horizontal,
    Vertical,
}

public enum TileNumbering
{
    /// <summary>row-column, e.g. “3-7”.</summary>
    RowCol,
    /// <summary>Left-to-right, top-to-bottom counting from 1.</summary>
    Linear,
    /// <summary>Column-major snake (typical LED data run order).</summary>
    Serpentine,
}

public enum FitMode
{
    Fit,
    Fill,
    Stretch,
    Center,
    Tile,
}

public enum MediaSource
{
    Image,
    Video,
    Playlist,
    /// <summary>Receive a live NDI® feed from the network.</summary>
    NdiFeed,
    /// <summary>A capture device (HDMI/SDI cards, webcams) via DirectShow.</summary>
    Capture,
}

/// <summary>What a picture-in-picture inset shows.</summary>
public enum PipSource
{
    NdiFeed,
    Capture,
}

/// <summary>Physical output rotation (content is pre-rotated so viewers see it upright).</summary>
public enum OutputRotation
{
    None,
    /// <summary>90° clockwise — portrait, cable on the bottom-left.</summary>
    Rot90,
    /// <summary>Upside down (ceiling mount).</summary>
    Rot180,
    /// <summary>270° clockwise — portrait, cable on the top-right.</summary>
    Rot270,
}

public enum ToneMode
{
    /// <summary>Steady sine on the selected channels.</summary>
    Continuous,
    /// <summary>Channel ident: one pip LEFT, two pips RIGHT, repeating — with on-screen indicator.</summary>
    ChannelIdent,
}

public enum ToneChannels
{
    Both,
    Left,
    Right,
}

public enum FeedKind
{
    Auto,
    Rss,
    Csv,
    Ics,
}

/// <summary>What sits behind the message text.</summary>
public enum MessageBackground
{
    /// <summary>A chip behind a static message, nothing behind a scrolling ticker — the original behaviour.</summary>
    Auto,
    /// <summary>Text straight over the picture.</summary>
    None,
    /// <summary>A solid band: a pill behind a static message, a full-width bar behind a ticker.</summary>
    Chip,
    /// <summary>A soft band that is darkest at the anchored edge and fades into the picture.</summary>
    Fade,
}

public enum Anchor9
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Center,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

public enum CountdownEndBehavior
{
    /// <summary>Stay at 00:00.</summary>
    HoldZero,
    /// <summary>Flash 00:00.</summary>
    Flash,
    /// <summary>Show the configured end message.</summary>
    Message,
}

public enum CountdownTargetKind
{
    /// <summary>Count down to a wall-clock time (HH:mm).</summary>
    TimeOfDay,
    /// <summary>Count down a duration from the moment it is armed.</summary>
    Duration,
}

public enum ParticleShape
{
    Circle,
    Square,
    Star,
    Streak,
    Bokeh,
    Logo,
}

public enum ParticleEmitter
{
    TopEdge,
    BottomEdge,
    Center,
    FullArea,
}

/// <summary>Which kind of consumer is rendering right now.</summary>
public enum SinkKind
{
    Preview,
    Output,
    Ndi,
    Thumbnail,
    /// <summary>A desk monitor of one target (the wall's tiles, the large PGM/PVW pair): crossfades and PiP on, no identify badge, not an output for statistics.</summary>
    Monitor,
}

/// <summary>Pre-show programming versus running the show.</summary>
public enum ShowMode
{
    /// <summary>
    /// Pre-programming at the desk: screens, outputs, inputs and looks can be built for a rig
    /// that isn't plugged in yet, and outputs are held closed so nothing goes live by accident.
    /// </summary>
    Prep,
    /// <summary>At the venue: outputs open on the real displays.</summary>
    Show,
}

/// <summary>Which graphics card the app should render (and decode video) on.</summary>
public enum GpuPreferenceKind
{
    /// <summary>Detect and use the best card — most video memory, discrete first. The default.</summary>
    BestPerformance,
    /// <summary>The low-power (integrated) card — battery-friendly rehearsal mode.</summary>
    PowerSaving,
    /// <summary>One named adapter, picked in the Admin tab.</summary>
    Specific,
    /// <summary>No override at all.</summary>
    LetWindowsDecide,
}

/// <summary>What a media-library entry is. Unknown first — the tolerant-enum rule; the migration derives the rest from the path.</summary>
public enum LibraryMediaKind
{
    Unknown,
    Image,
    Video,
    Audio,
}

/// <summary>The fractal families the Fractal pattern draws.</summary>
public enum FractalKind
{
    Mandelbrot,
    Julia,
    BurningShip,
    Newton,
    DomainWarp,
}

/// <summary>Where a sound-reactive effect listens: nowhere, this computer's own sound, or an input.</summary>
public enum AudioSourceKind
{
    None,
    Internal,
    External,
}

/// <summary>How much the CPU path (NDI, thumbnails) spends on a fractal frame.</summary>
public enum FractalQuality
{
    Balanced,
    Fast,
    Fine,
}

/// <summary>What a library item is: a sound or clip on disk, or an effect pulse — a surge through the particles and fractals on screen.</summary>
public enum StingerSource
{
    File,
    EffectPulse,
}

/// <summary>The shape of an effect pulse: what surges, and how.</summary>
public enum PulsePreset
{
    Explosion,
    Rush,
    Flash,
    Bloom,
}
