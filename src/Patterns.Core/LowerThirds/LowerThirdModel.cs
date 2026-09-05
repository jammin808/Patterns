using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.Core.LowerThirds;

/// <summary>What an element of a lower third is.</summary>
public enum LowerThirdElementKind
{
    Text,
    /// <summary>A shape: a fill, a gradient, a border, a shadow, a glow, a chaser — the panel behind everything else.</summary>
    Bar,
    /// <summary>A picture from a file: a headshot, a flag, a badge.</summary>
    Image,
    /// <summary>The brand kit's logo.</summary>
    Logo,
    /// <summary>A clip (or a still) — silent b-roll behind the words, or a moving badge.</summary>
    Media,
    /// <summary>A particle scene inside the element's box, reacting to stings like the full-screen studio.</summary>
    Particles,
    /// <summary>A fractal inside the element's box.</summary>
    Fractal,
}

/// <summary>What a text element says: its own words, or one of the design's fields.</summary>
public enum LowerThirdTextKind
{
    Custom,
    Name,
    Role,
    Company,
    Date,
    Time,
    DateAndTime,
}

public enum LowerThirdAlign
{
    Left,
    Center,
    Right,
}

public enum LowerThirdFill
{
    None,
    Solid,
    Gradient,
}

public enum LowerThirdGradient
{
    LeftRight,
    TopBottom,
    Diagonal,
}

/// <summary>How a key is approached from the one before it.</summary>
public enum EaseKind
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
    /// <summary>Overshoots a little and settles.</summary>
    Back,
    Bounce,
    Elastic,
}

/// <summary>A ready-made way in or out; the keys it writes can be edited afterwards.</summary>
public enum LowerThirdMotion
{
    None,
    Fade,
    SlideLeft,
    SlideRight,
    SlideUp,
    SlideDown,
    /// <summary>Scales up from small with a little overshoot.</summary>
    Pop,
    /// <summary>Revealed from the left edge.</summary>
    Wipe,
    /// <summary>Falls in from above and bounces.</summary>
    Drop,
    /// <summary>Rises from below.</summary>
    Rise,
    Spin,
}

public enum LowerThirdPhase
{
    Before,
    In,
    Hold,
    Out,
    Gone,
}

/// <summary>
/// One key of an element's way in or out: where the element is at <see cref="U"/> (0 = the
/// start of the phase, 1 = its end) as offsets from its resting place, and how it gets there.
/// </summary>
public sealed class LowerThirdKeyframe : Observable
{
    private double _u;
    private double _x;
    private double _y;
    private double _opacity = 1;
    private double _scale = 1;
    private double _rotate;
    private double _reveal = 1;
    private EaseKind _ease = EaseKind.EaseInOut;

    /// <summary>Where in the phase this key sits, 0–1.</summary>
    public double U { get => _u; set => Set(ref _u, Math.Clamp(value, 0, 1)); }

    /// <summary>Offset from the resting place, design pixels.</summary>
    public double X { get => _x; set => Set(ref _x, Math.Clamp(value, -8192, 8192)); }
    public double Y { get => _y; set => Set(ref _y, Math.Clamp(value, -8192, 8192)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0, 1)); }
    public double Scale { get => _scale; set => Set(ref _scale, Math.Clamp(value, 0, 10)); }
    /// <summary>Degrees, clockwise.</summary>
    public double Rotate { get => _rotate; set => Set(ref _rotate, Math.Clamp(value, -720, 720)); }
    /// <summary>How much of the element shows from its left edge, 0–1 (a wipe).</summary>
    public double Reveal { get => _reveal; set => Set(ref _reveal, Math.Clamp(value, 0, 1)); }
    /// <summary>The easing from the previous key to this one.</summary>
    public EaseKind Ease { get => _ease; set => Set(ref _ease, value); }

    public LowerThirdKeyframe Clone() => new() { U = U, X = X, Y = Y, Opacity = Opacity, Scale = Scale, Rotate = Rotate, Reveal = Reveal, Ease = Ease };
}

/// <summary>One thing on a lower third: where it sits in the design box, what it is, how it looks, how it moves.</summary>
public sealed class LowerThirdElement : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "";
    private LowerThirdElementKind _kind = LowerThirdElementKind.Text;
    private bool _enabled = true;
    private double _x;
    private double _y;
    private double _w = 400;
    private double _h = 80;
    private double _opacity = 1;
    private int _delayMs;

    // Text
    private LowerThirdTextKind _textKind = LowerThirdTextKind.Custom;
    private string _text = "";
    private double _fontSizePx = 48;
    private bool _bold = true;
    private bool _uppercase;
    private bool _shrink = true;
    private LowerThirdAlign _align = LowerThirdAlign.Left;
    private string _textColor = "#FFFFFF";
    private string _fontFamily = "";

    // Pictures and clips
    private string _path = "";
    private FitMode _fit = FitMode.Fill;
    private bool _mediaMute = true;
    private double _mediaVolumePct = 100;

    // Style
    private LowerThirdFill _fill = LowerThirdFill.None;
    private string _fillColor = "#1B2130";
    private string _fillColor2 = "primary";
    private LowerThirdGradient _gradient = LowerThirdGradient.LeftRight;
    private double _cornerPx;
    private double _borderPx;
    private string _borderColor = "#FFFFFF";
    private double _shadowPx;
    private string _shadowColor = "#000000A0";
    private double _shadowDx;
    private double _shadowDy = 8;
    private double _glowPx;
    private string _glowColor = "primary";
    private bool _chaser;
    private string _chaserColor = "#FFFFFF";
    private double _chaserSpeed = 0.5;
    private double _chaserLengthPct = 15;

    /// <summary>Stable identity: the per-sink caches and the designer's list key on it.</summary>
    public string Id { get => _id; set => Set(ref _id, string.IsNullOrWhiteSpace(value) ? _id : value); }

    /// <summary>What the designer's list calls it ("Name", "Panel", "Headshot").</summary>
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public LowerThirdElementKind Kind { get => _kind; set => Set(ref _kind, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>The element's box inside the design, design pixels (the design is drawn at 1080 lines and scaled).</summary>
    public double X { get => _x; set => Set(ref _x, Math.Clamp(value, -4096, 8192)); }
    public double Y { get => _y; set => Set(ref _y, Math.Clamp(value, -4096, 8192)); }
    public double W { get => _w; set => Set(ref _w, Math.Clamp(value, 1, 8192)); }
    public double H { get => _h; set => Set(ref _h, Math.Clamp(value, 1, 8192)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0, 1)); }

    /// <summary>Starts its way in this long after the design does (a stagger); it still lands with the rest.</summary>
    public int DelayMs { get => _delayMs; set => Set(ref _delayMs, Math.Clamp(value, 0, 10000)); }

    public LowerThirdTextKind TextKind { get => _textKind; set => Set(ref _textKind, value); }
    /// <summary>The words of a Custom text element.</summary>
    public string Text { get => _text; set => Set(ref _text, value ?? ""); }
    public double FontSizePx { get => _fontSizePx; set => Set(ref _fontSizePx, Math.Clamp(value, 6, 600)); }
    public bool Bold { get => _bold; set => Set(ref _bold, value); }
    public bool Uppercase { get => _uppercase; set => Set(ref _uppercase, value); }
    /// <summary>Shrink the text to fit the box rather than run out of it.</summary>
    public bool Shrink { get => _shrink; set => Set(ref _shrink, value); }
    public LowerThirdAlign Align { get => _align; set => Set(ref _align, value); }
    /// <summary>A hex colour, or a brand word: primary, secondary, accent, text, background.</summary>
    public string TextColor { get => _textColor; set => Set(ref _textColor, value ?? ""); }
    /// <summary>System font family; empty = the brand kit's font (or the built-in Inter).</summary>
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value ?? ""); }

    /// <summary>The file of an Image or Media element.</summary>
    public string Path { get => _path; set => Set(ref _path, value ?? ""); }
    public FitMode Fit { get => _fit; set => Set(ref _fit, value); }
    /// <summary>A Media element's clip plays silent unless told otherwise — it is b-roll under the words.</summary>
    public bool MediaMute { get => _mediaMute; set => Set(ref _mediaMute, value); }
    public double MediaVolumePct { get => _mediaVolumePct; set => Set(ref _mediaVolumePct, Math.Clamp(value, 0, 125)); }

    public LowerThirdFill Fill { get => _fill; set => Set(ref _fill, value); }
    public string FillColor { get => _fillColor; set => Set(ref _fillColor, value ?? ""); }
    /// <summary>The gradient's second colour.</summary>
    public string FillColor2 { get => _fillColor2; set => Set(ref _fillColor2, value ?? ""); }
    public LowerThirdGradient Gradient { get => _gradient; set => Set(ref _gradient, value); }
    public double CornerPx { get => _cornerPx; set => Set(ref _cornerPx, Math.Clamp(value, 0, 1024)); }
    public double BorderPx { get => _borderPx; set => Set(ref _borderPx, Math.Clamp(value, 0, 64)); }
    public string BorderColor { get => _borderColor; set => Set(ref _borderColor, value ?? ""); }
    /// <summary>The shadow's softness; 0 with an offset is a hard shadow.</summary>
    public double ShadowPx { get => _shadowPx; set => Set(ref _shadowPx, Math.Clamp(value, 0, 128)); }
    public string ShadowColor { get => _shadowColor; set => Set(ref _shadowColor, value ?? ""); }
    public double ShadowDx { get => _shadowDx; set => Set(ref _shadowDx, Math.Clamp(value, -256, 256)); }
    public double ShadowDy { get => _shadowDy; set => Set(ref _shadowDy, Math.Clamp(value, -256, 256)); }
    /// <summary>A soft light around the box (or the letters of a plain text element); a sting's glow brightens it.</summary>
    public double GlowPx { get => _glowPx; set => Set(ref _glowPx, Math.Clamp(value, 0, 128)); }
    public string GlowColor { get => _glowColor; set => Set(ref _glowColor, value ?? ""); }
    /// <summary>A bright run travelling round the box's edge.</summary>
    public bool Chaser { get => _chaser; set => Set(ref _chaser, value); }
    public string ChaserColor { get => _chaserColor; set => Set(ref _chaserColor, value ?? ""); }
    /// <summary>Laps per second; negative runs the other way.</summary>
    public double ChaserSpeed { get => _chaserSpeed; set => Set(ref _chaserSpeed, Math.Clamp(value, -5, 5)); }
    /// <summary>The run's length as a share of the edge, 1–90 %.</summary>
    public double ChaserLengthPct { get => _chaserLengthPct; set => Set(ref _chaserLengthPct, Math.Clamp(value, 1, 90)); }

    /// <summary>A Particles element's scene.</summary>
    public ParticleOptions Particles { get; init; } = new();

    /// <summary>A Fractal element's picture.</summary>
    public FractalOptions Fractal { get; init; } = new();

    /// <summary>The way in, 0 = the start of the in phase, 1 = at rest. Empty = a plain fade.</summary>
    public ObservableCollection<LowerThirdKeyframe> In { get; init; } = new();

    /// <summary>The way out, 0 = at rest, 1 = gone. Empty = a plain fade.</summary>
    public ObservableCollection<LowerThirdKeyframe> Out { get; init; } = new();

    [JsonIgnore]
    public bool HasBox => Kind == LowerThirdElementKind.Bar || Fill != LowerThirdFill.None
        || (Kind != LowerThirdElementKind.Text && (BorderPx > 0 || GlowPx > 0 || ShadowPx > 0 || ShadowDx != 0 || ShadowDy != 0 || Chaser));

    public LowerThirdElement Clone(bool newId = false)
    {
        var copy = JsonUtil.Clone(this);
        if (newId) copy.Id = Guid.NewGuid().ToString("N");
        return copy;
    }
}

/// <summary>
/// A lower third: a box of elements, anchored on the canvas, with a way in, a hold and a way
/// out, and the fields its text elements read (a name, a role, a company, a date, a time).
/// Designed at 1080 lines and scaled to the canvas, so one design fits every screen.
/// </summary>
public sealed class LowerThirdDesign : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "Untitled";
    private string _preset = "";
    private double _width = 960;
    private double _height = 220;
    private Anchor9 _anchor = Anchor9.BottomLeft;
    private double _marginX = 60;
    private double _marginY = 60;
    private double _scalePct = 100;
    private int _inMs = 600;
    private int _holdMs;
    private int _outMs = 500;
    private string _personName = "Jane Doe";
    private string _personRole = "Head of Something";
    private string _company = "";
    private string _dateText = "";
    private string _timeText = "";

    public string Id { get => _id; set => Set(ref _id, string.IsNullOrWhiteSpace(value) ? _id : value); }
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    /// <summary>The preset it started from; a label.</summary>
    public string Preset { get => _preset; set => Set(ref _preset, value ?? ""); }

    /// <summary>The design box, design pixels.</summary>
    public double Width { get => _width; set => Set(ref _width, Math.Clamp(value, 40, 8192)); }
    public double Height { get => _height; set => Set(ref _height, Math.Clamp(value, 20, 4320)); }
    public Anchor9 Anchor { get => _anchor; set => Set(ref _anchor, value); }
    /// <summary>The box's distance from the anchored edges, design pixels.</summary>
    public double MarginX { get => _marginX; set => Set(ref _marginX, Math.Clamp(value, -2048, 4096)); }
    public double MarginY { get => _marginY; set => Set(ref _marginY, Math.Clamp(value, -2048, 4096)); }
    /// <summary>On top of the canvas scale: 100 = the design's own size at 1080 lines.</summary>
    public double ScalePct { get => _scalePct; set => Set(ref _scalePct, Math.Clamp(value, 10, 400)); }

    /// <summary>The way in takes this long.</summary>
    public int InMs { get => _inMs; set => Set(ref _inMs, Math.Clamp(value, 0, 20000)); }
    /// <summary>Stays this long before leaving by itself; 0 = until hidden.</summary>
    public int HoldMs { get => _holdMs; set => Set(ref _holdMs, Math.Clamp(value, 0, 600000)); }
    public int OutMs { get => _outMs; set => Set(ref _outMs, Math.Clamp(value, 0, 20000)); }

    public string PersonName { get => _personName; set => Set(ref _personName, value ?? ""); }
    public string PersonRole { get => _personRole; set => Set(ref _personRole, value ?? ""); }
    /// <summary>Empty = the brand kit's company name.</summary>
    public string Company { get => _company; set => Set(ref _company, value ?? ""); }
    /// <summary>Empty = today's date, live.</summary>
    public string DateText { get => _dateText; set => Set(ref _dateText, value ?? ""); }
    /// <summary>Empty = the time of day, live.</summary>
    public string TimeText { get => _timeText; set => Set(ref _timeText, value ?? ""); }

    public ObservableCollection<LowerThirdElement> Elements { get; init; } = new();

    /// <summary>In, hold and out together, ms (the hold counts only when it ends by itself).</summary>
    [JsonIgnore]
    public int TotalMs => InMs + HoldMs + OutMs;

    private bool _isOnAir;
    private string _onAirText = "";

    /// <summary>Runtime tally: this design is on screen right now (never saved).</summary>
    [JsonIgnore]
    public bool IsOnAir { get => _isOnAir; set => Set(ref _isOnAir, value); }

    /// <summary>Runtime tally: "ON AIR", "ARRIVING", "LEAVING", or "" (never saved).</summary>
    [JsonIgnore]
    public string OnAirText { get => _onAirText; set => Set(ref _onAirText, value ?? ""); }

    public LowerThirdDesign Clone(bool newId = true)
    {
        var copy = JsonUtil.Clone(this);
        if (newId)
        {
            copy.Id = Guid.NewGuid().ToString("N");
            foreach (var e in copy.Elements) e.Id = Guid.NewGuid().ToString("N");
        }
        return copy;
    }
}

/// <summary>The show's lower thirds: the designs, and which one is on air since when.</summary>
public sealed class LowerThirdsConfig : Observable
{
    private string _activeId = "";
    private DateTime? _shownAtUtc;
    private DateTime? _hiddenAtUtc;

    public ObservableCollection<LowerThirdDesign> Designs { get; init; } = new();

    /// <summary>The design on air (or last on air).</summary>
    public string ActiveId { get => _activeId; set => Set(ref _activeId, value ?? ""); }

    /// <summary>When the active design was shown (the master clock's wall time); null = never.</summary>
    public DateTime? ShownAtUtc { get => _shownAtUtc; set => Set(ref _shownAtUtc, value); }

    /// <summary>When it was told to leave; null while it stays.</summary>
    public DateTime? HiddenAtUtc { get => _hiddenAtUtc; set => Set(ref _hiddenAtUtc, value); }

    [JsonIgnore]
    public LowerThirdDesign? Active => Find(ActiveId);

    /// <summary>Shown and not told to leave (it may still be leaving by itself after its hold).</summary>
    [JsonIgnore]
    public bool IsShowing => Active is not null && ShownAtUtc is not null && HiddenAtUtc is null;

    /// <summary>A design by id, then by name (case-insensitive), then by 1-based number.</summary>
    public LowerThirdDesign? Find(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        foreach (var d in Designs)
        {
            if (d.Id == idOrName) return d;
        }
        foreach (var d in Designs)
        {
            if (string.Equals(d.Name, idOrName, StringComparison.OrdinalIgnoreCase)) return d;
        }
        if (int.TryParse(idOrName, out var n) && n >= 1 && n <= Designs.Count) return Designs[n - 1];
        return null;
    }

    /// <summary>Puts a design on air now (showing it again restarts its way in).</summary>
    public void Show(LowerThirdDesign design, DateTime utcNow)
    {
        ActiveId = design.Id;
        HiddenAtUtc = null;
        ShownAtUtc = utcNow;
    }

    /// <summary>Tells the design on air to leave now; nothing to do when nothing is on.</summary>
    public void Hide(DateTime utcNow)
    {
        if (ShownAtUtc is null || HiddenAtUtc is not null) return;
        HiddenAtUtc = utcNow;
    }
}
