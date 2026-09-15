using System.Globalization;
using System.Text.Json.Serialization;

namespace Patterns.Core.Model;

/// <summary>How the pixels travel the link: RGB, or YCbCr with its chroma subsampling. Any: the contract does not say.</summary>
public enum PixelEncoding
{
    Any,
    RGB,
    YCbCr444,
    YCbCr422,
    YCbCr420,
}

/// <summary>Full range (0–255 at 8 bit) or limited (16–235). Any: the contract does not say.</summary>
public enum QuantizationRange
{
    Any,
    Full,
    Limited,
}

/// <summary>SDR, or an HDR transfer. Any: the contract does not say.</summary>
public enum DynamicRange
{
    Any,
    SDR,
    HDR10,
    HLG,
}

/// <summary>What the link carries for sound: none, two channels, or more. Any: the contract does not say.</summary>
public enum AudioPolicy
{
    Any,
    None,
    Stereo,
    Multichannel,
}

/// <summary>The connector the link is meant to leave the machine on — compared with the one Windows says the display is on. Any: the contract does not say.</summary>
public enum SignalTransport
{
    Any,
    HDMI,
    DisplayPort,
    SDI,
    DVI,
    VGA,
    /// <summary>DisplayPort tunnelled over USB-C / USB4.</summary>
    UsbC,
    /// <summary>A laptop's own panel (eDP, LVDS).</summary>
    Internal,
}

/// <summary>The colour space the pictures are graded for. Any: the contract does not say. Windows does not report the active colorimetry; the EDID advertises it (round 65.7).</summary>
public enum ColourSpace
{
    Any,
    Rec709,
    DciP3,
    Rec2020,
}

/// <summary>
/// The families a timing belongs to. 59.94 and 60 are not one rate (round 65): a chain that
/// mixes them repeats or drops a frame every seventeen seconds, and 50 against either converts
/// or judders. The render pacer keeps its looser "same cadence" rule for its own purpose; this
/// is the engineering truth a contract and an observation are compared by.
/// </summary>
public enum TimingFamily
{
    Unknown,
    /// <summary>23.976, 24, 48.</summary>
    Film,
    /// <summary>25, 50, 100 — the PAL-derived integers.</summary>
    Pal,
    /// <summary>29.97, 59.94, 119.88 — the NTSC-derived fractions (n × 1000/1001).</summary>
    NtscFractional,
    /// <summary>30, 60, 120, 240 — the exact integers.</summary>
    Integer,
    /// <summary>144, 75, anything else a display offers.</summary>
    Other,
}

/// <summary>
/// A vertical rate as a rational — 60000/1001 is 59.94, never 59.94 rounded — so two rates are
/// equal only when they are the same rate. Made through <see cref="Of"/>, <see cref="Parse"/>
/// or <see cref="FromHz"/>, which reduce and snap a driver's spelling (59940/1000) onto the
/// canonical rational; the default is "not set".
/// </summary>
public readonly record struct SignalRate(int Numerator, int Denominator)
{
    public static readonly SignalRate None = default;

    /// <summary>The rates a display or a contract commonly names, as exact rationals.</summary>
    public static readonly SignalRate[] Canonical =
    {
        Reduced(24000, 1001), Reduced(24, 1), Reduced(25, 1), Reduced(30000, 1001), Reduced(30, 1), Reduced(48, 1), Reduced(50, 1),
        Reduced(60000, 1001), Reduced(60, 1), Reduced(100, 1), Reduced(120000, 1001), Reduced(120, 1), Reduced(144, 1), Reduced(240, 1),
    };

    public bool IsSet => Numerator > 0 && Denominator > 0;

    public double Hz => IsSet ? (double)Numerator / Denominator : 0;

    /// <summary>A rational reduced, and snapped onto the canonical rate it spells (within 0.02 %: 59940/1000 is 60000/1001).</summary>
    public static SignalRate Of(long numerator, long denominator)
    {
        var r = Reduced(numerator, denominator);
        if (!r.IsSet) return None;
        foreach (var c in Canonical)
        {
            if (Math.Abs(r.Hz - c.Hz) <= c.Hz * 0.0002) return c;
        }
        return r;
    }

    /// <summary>A rate from a decimal reading (59.94, 23.976, 50.0): the canonical rational when it spells one; 0 or less is not set.</summary>
    public static SignalRate FromHz(double hz) => hz <= 0 || double.IsNaN(hz) || double.IsInfinity(hz) ? None : Of((long)Math.Round(hz * 1000), 1000);

    /// <summary>
    /// "50", "59.94", "23.976", "60000/1001", "50p", "50 Hz", "p50": a rate, or not set for anything
    /// else — a number under 20 is not a rate a display runs at, so "8" is left to mean bits.
    /// </summary>
    public static SignalRate Parse(string? words)
    {
        var s = (words ?? "").Trim().ToLowerInvariant().Replace("hz", "").Replace("fps", "").Trim();
        if (s.StartsWith('p')) s = s[1..];
        if (s.EndsWith('p') || s.EndsWith('i')) s = s[..^1];
        s = s.Trim();
        if (s.Length == 0) return None;
        var slash = s.IndexOf('/');
        if (slash > 0)
        {
            return long.TryParse(s[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
                && long.TryParse(s[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var den)
                && num > 0 && den > 0 && num / (double)den >= 20
                ? Of(num, den)
                : None;
        }
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz) || hz < 20 || hz > 1000) return None;
        return FromHz(hz);
    }

    /// <summary>The rate in a video engineer's words: "50", "59.94", "23.976", "119.88"; "" when not set.</summary>
    public string Words
    {
        get
        {
            if (!IsSet) return "";
            if (Denominator == 1) return Numerator.ToString(CultureInfo.InvariantCulture);
            if (this == Reduced(24000, 1001)) return "23.976";
            if (this == Reduced(30000, 1001)) return "29.97";
            if (this == Reduced(60000, 1001)) return "59.94";
            if (this == Reduced(120000, 1001)) return "119.88";
            return Hz.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The rational spelt out: "60000/1001", "50/1"; "" when not set.</summary>
    public string Rational => IsSet ? $"{Numerator}/{Denominator}" : "";

    public TimingFamily Family
    {
        get
        {
            if (!IsSet) return TimingFamily.Unknown;
            if (this == Reduced(24000, 1001) || this == Reduced(24, 1) || this == Reduced(48, 1)) return TimingFamily.Film;
            if (this == Reduced(25, 1) || this == Reduced(50, 1) || this == Reduced(100, 1)) return TimingFamily.Pal;
            if (this == Reduced(30000, 1001) || this == Reduced(60000, 1001) || this == Reduced(120000, 1001)) return TimingFamily.NtscFractional;
            if (this == Reduced(30, 1) || this == Reduced(60, 1) || this == Reduced(120, 1) || this == Reduced(240, 1)) return TimingFamily.Integer;
            return TimingFamily.Other;
        }
    }

    /// <summary>The family's words for a note: "PAL-derived (25 / 50 / 100)".</summary>
    public static string FamilyWords(TimingFamily family) => family switch
    {
        TimingFamily.Film => "film (23.976 / 24 / 48)",
        TimingFamily.Pal => "PAL-derived (25 / 50 / 100)",
        TimingFamily.NtscFractional => "NTSC-derived fractional (29.97 / 59.94 / 119.88)",
        TimingFamily.Integer => "integer (30 / 60 / 120)",
        TimingFamily.Other => "outside the common families",
        _ => "unknown",
    };

    public override string ToString() => Words;

    private static SignalRate Reduced(long numerator, long denominator)
    {
        if (numerator <= 0 || denominator <= 0) return None;
        var g = Gcd(numerator, denominator);
        var num = numerator / g;
        var den = denominator / g;
        return num > int.MaxValue || den > int.MaxValue ? None : new SignalRate((int)num, (int)den);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}

/// <summary>
/// The signal a screen's link is meant to carry (round 65): the transport raster, the exact rate,
/// the encoding, the depth, the range, the dynamic range, the audio — the intent, kept apart from
/// what the display advertises (its EDID) and from what Windows is observed to send. Every field
/// may be left unsaid (0 / Any): a contract says only what the engineer decided. The transport
/// raster is not the canvas — an LED wall of 7680 × 2160 is two links of 3840 × 2160 — so a
/// contract belongs to a screen, and a canvas is its members' contracts side by side.
/// </summary>
public sealed class SignalContract : Observable
{
    private int _width;
    private int _height;
    private int _rateNumerator;
    private int _rateDenominator;
    private PixelEncoding _encoding;
    private int _bitDepth;
    private QuantizationRange _range;
    private DynamicRange _dynamic;
    private AudioPolicy _audio;
    private SignalTransport _transport;
    private ColourSpace _colour;
    private string _fallback = "";

    /// <summary>The transport raster; 0 = the screen's own size.</summary>
    public int Width { get => _width; set => Set(ref _width, Math.Clamp(value, 0, 16384)); }
    public int Height { get => _height; set => Set(ref _height, Math.Clamp(value, 0, 16384)); }

    /// <summary>The vertical rate as a rational (60000 / 1001 = 59.94); 0 / 0 = the display's own.</summary>
    public int RateNumerator { get => _rateNumerator; set => Set(ref _rateNumerator, Math.Max(0, value)); }
    public int RateDenominator { get => _rateDenominator; set => Set(ref _rateDenominator, Math.Max(0, value)); }

    [JsonIgnore]
    public SignalRate Rate
    {
        get => SignalRate.Of(_rateNumerator, _rateDenominator);
        set
        {
            RateNumerator = value.Numerator;
            RateDenominator = value.Denominator;
        }
    }

    public PixelEncoding Encoding { get => _encoding; set => Set(ref _encoding, value); }

    /// <summary>Bits per colour channel: 8, 10, 12 or 16; 0 = not said.</summary>
    public int BitDepth { get => _bitDepth; set => Set(ref _bitDepth, value is 8 or 10 or 12 or 16 ? value : 0); }

    public QuantizationRange Range { get => _range; set => Set(ref _range, value); }
    public DynamicRange Dynamic { get => _dynamic; set => Set(ref _dynamic, value); }
    public AudioPolicy Audio { get => _audio; set => Set(ref _audio, value); }
    public SignalTransport Transport { get => _transport; set => Set(ref _transport, value); }
    public ColourSpace Colour { get => _colour; set => Set(ref _colour, value); }

    /// <summary>"" none; "diagnostic": the conservative profile to test the route with when the contract does not light up (1920 × 1080 · 50 · RGB · 8-bit · SDR).</summary>
    public string Fallback { get => _fallback; set => Set(ref _fallback, (value ?? "").Trim().ToLowerInvariant()); }

    /// <summary>Whether the contract says anything at all.</summary>
    [JsonIgnore]
    public bool IsSet => _width > 0 || _height > 0 || Rate.IsSet || _encoding != PixelEncoding.Any || _bitDepth > 0
        || _range != QuantizationRange.Any || _dynamic != DynamicRange.Any || _audio != AudioPolicy.Any
        || _transport != SignalTransport.Any || _colour != ColourSpace.Any;

    public void Clear()
    {
        Width = 0;
        Height = 0;
        RateNumerator = 0;
        RateDenominator = 0;
        Encoding = PixelEncoding.Any;
        BitDepth = 0;
        Range = QuantizationRange.Any;
        Dynamic = DynamicRange.Any;
        Audio = AudioPolicy.Any;
        Transport = SignalTransport.Any;
        Colour = ColourSpace.Any;
        Fallback = "";
    }

    public void CopyFrom(SignalContract other)
    {
        Width = other.Width;
        Height = other.Height;
        RateNumerator = other.RateNumerator;
        RateDenominator = other.RateDenominator;
        Encoding = other.Encoding;
        BitDepth = other.BitDepth;
        Range = other.Range;
        Dynamic = other.Dynamic;
        Audio = other.Audio;
        Transport = other.Transport;
        Colour = other.Colour;
        Fallback = other.Fallback;
    }

    /// <summary>The conservative profile the guide recommends for separating a capability problem from a path problem: 1080p50, RGB, 8-bit, SDR, stereo.</summary>
    public static SignalContract Diagnostic() => new()
    {
        Width = 1920, Height = 1080, RateNumerator = 50, RateDenominator = 1,
        Encoding = PixelEncoding.RGB, BitDepth = 8, Dynamic = DynamicRange.SDR, Audio = AudioPolicy.Stereo,
    };
}
