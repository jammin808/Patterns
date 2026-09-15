using System.Globalization;
using System.Text.RegularExpressions;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// What Windows says a display is being sent (round 65): the desktop rectangle that ties it to a
/// screen, the signal's active raster, the exact rate, and — where the path exposes them — the
/// encoding, the bits per channel and whether HDR is on. A property the path did not state is
/// null or 0 and stays unknown: an EDID that offers RGB is not a signal that is RGB.
/// </summary>
public sealed record SignalObservation(
    int X,
    int Y,
    int Width,
    int Height,
    int TargetWidth,
    int TargetHeight,
    SignalRate Rate,
    PixelEncoding? Encoding = null,
    int BitsPerChannel = 0,
    bool? HdrActive = null,
    bool? HdrCapable = null,
    bool Interlaced = false,
    string Monitor = "",
    int EdidManufacturerId = 0,
    int EdidProductCode = 0,
    string DevicePath = "",
    string Connector = "")
{
    /// <summary>Whether this is the display showing that desktop rectangle.</summary>
    public bool Covers(int x, int y, int width, int height) => X == x && Y == y && Width == width && Height == height;

    /// <summary>The signal's raster as words — the target's when the path gave it, else the desktop's; "" unknown.</summary>
    public string RasterWords => TargetWidth > 0 && TargetHeight > 0 ? $"{TargetWidth}×{TargetHeight}" : Width > 0 && Height > 0 ? $"{Width}×{Height}" : "";

    public int RasterWidth => TargetWidth > 0 ? TargetWidth : Width;
    public int RasterHeight => TargetHeight > 0 ? TargetHeight : Height;
}

/// <summary>How a contract and an observation compare.</summary>
public enum SignalVerdict
{
    /// <summary>No contract, or no evidence to hold it against.</summary>
    Unverified,
    Match,
    Mismatch,
}

/// <summary>One line of a screen's signal view — an item, its light, its value, a note with the fix.</summary>
public sealed record SignalLine(string Item, CheckLight Light, string Value, string Note = "");

/// <summary>
/// A screen's signal truth: DESIGN (the contract), REQUESTED (what Patterns asks Windows for),
/// OBSERVED (what Windows reports it sends, unknowns left unknown), RESULT — and the lines Super
/// Check shows under SIGNAL. ADVERTISED (the EDID) joins in round 65.7.
/// </summary>
public sealed record SignalReport(string Label, SignalVerdict Verdict, string Design, string Requested, string Observed, IReadOnlyList<SignalLine> Lines)
{
    public string Result => Words(Verdict);

    /// <summary>The verdict's word: MATCH, MISMATCH, UNVERIFIED.</summary>
    public static string Words(SignalVerdict verdict) => verdict switch
    {
        SignalVerdict.Match => "MATCH",
        SignalVerdict.Mismatch => "MISMATCH",
        _ => "UNVERIFIED",
    };

    /// <summary>The technical view as text, for the Screens page and the wire.</summary>
    public string Text => $"DESIGN\n{Design}\n\nREQUESTED\n{Requested}\n\nOBSERVED\n{Observed}\n\nRESULT\n{Result}"
        + (Lines.Count == 0 ? "" : "\n\n" + string.Join("\n", Lines.Select(l => $"{l.Item}: {l.Value}{(l.Note.Length > 0 ? " — " + l.Note : "")}")));
}

/// <summary>
/// The comparison (round 65): a contract held against what Windows is observed to send, property
/// by property, with evidence the only thing that moves a light. The raster and the rate decide
/// MATCH; the encoding, the depth, HDR and the connector are compared when the path states them
/// and read "not available from this Windows path" when it does not — never inferred from a
/// capability. The lights: red for a raster that is not the contract's (the wrong picture, an
/// LED processor scaling), amber for a rate, an encoding, a depth, HDR or a connector that is not
/// (the picture is there and degraded), grey for unknown.
/// </summary>
public static class SignalTruth
{
    /// <summary>The contract in words: "3840×2160 · 50 Hz · RGB 4:4:4 · 8-bit · SDR · stereo"; the unsaid parts left out; "none — the display's own" for an empty contract.</summary>
    public static string DesignWords(SignalContract? c)
    {
        if (c is null || !c.IsSet) return "none — the display's own";
        var parts = new List<string>();
        if (c.Width > 0 && c.Height > 0) parts.Add($"{c.Width}×{c.Height}");
        if (c.Rate.IsSet) parts.Add($"{c.Rate.Words} Hz");
        if (c.Encoding != PixelEncoding.Any) parts.Add(EncodingWords(c.Encoding));
        if (c.BitDepth > 0) parts.Add($"{c.BitDepth}-bit");
        if (c.Range != QuantizationRange.Any) parts.Add(c.Range == QuantizationRange.Full ? "full range" : "limited range");
        if (c.Dynamic != DynamicRange.Any) parts.Add(c.Dynamic.ToString());
        if (c.Colour != ColourSpace.Any) parts.Add(ColourWords(c.Colour));
        if (c.Audio != AudioPolicy.Any) parts.Add(AudioWords(c.Audio));
        if (c.Transport != SignalTransport.Any) parts.Add("over " + TransportWords(c.Transport));
        return string.Join(" · ", parts);
    }

    public static string ColourWords(ColourSpace c) => c switch
    {
        ColourSpace.Rec709 => "Rec. 709",
        ColourSpace.DciP3 => "DCI-P3",
        ColourSpace.Rec2020 => "Rec. 2020",
        _ => "any colour space",
    };

    public static string TransportWords(SignalTransport t) => t switch
    {
        SignalTransport.HDMI => "HDMI",
        SignalTransport.DisplayPort => "DisplayPort",
        SignalTransport.SDI => "SDI",
        SignalTransport.DVI => "DVI",
        SignalTransport.VGA => "VGA",
        SignalTransport.UsbC => "USB-C",
        SignalTransport.Internal => "the internal panel",
        _ => "any connector",
    };

    /// <summary>Windows' connector word (see DisplayObservation.Connector) as a transport; Any when it does not say.</summary>
    public static SignalTransport TransportOf(string connector) => connector switch
    {
        "HDMI" => SignalTransport.HDMI,
        "DisplayPort" => SignalTransport.DisplayPort,
        "DisplayPort over USB" => SignalTransport.UsbC,
        "SDI" => SignalTransport.SDI,
        "DVI" => SignalTransport.DVI,
        "VGA" => SignalTransport.VGA,
        "internal" or "eDP" or "LVDS" => SignalTransport.Internal,
        _ => SignalTransport.Any,
    };

    /// <summary>What Patterns asks Windows for: the screen's size and the rate the output presents at ("3840×2160 · 50 fps paced" / "· the display's own rate").</summary>
    public static string RequestedWords(int width, int height, int presentFps, int displayHz)
    {
        var raster = width > 0 && height > 0 ? $"{width}×{height}" : "no size yet";
        var rate = presentFps > 0 ? $"{presentFps} fps paced" : displayHz > 0 ? $"the display's own rate ({displayHz} Hz)" : "the display's own rate";
        return $"{raster} · {rate}";
    }

    /// <summary>What Windows reports it sends, unknowns as "unknown": "3840×2160 · 50 Hz · RGB 4:4:4 · 8-bit · SDR"; "not observed" for none.</summary>
    public static string ObservedWords(SignalObservation? o)
    {
        if (o is null) return "not observed";
        var parts = new List<string>();
        if (o.RasterWords.Length > 0) parts.Add(o.RasterWords + (o.Interlaced ? "i" : ""));
        parts.Add(o.Rate.IsSet ? $"{o.Rate.Words} Hz ({o.Rate.Rational})" : "rate unknown");
        parts.Add(o.Encoding is { } enc ? EncodingWords(enc) : "encoding unknown");
        parts.Add(o.BitsPerChannel > 0 ? $"{o.BitsPerChannel}-bit" : "bit depth unknown");
        parts.Add(o.HdrActive switch { true => "HDR on", false => "SDR", _ => "HDR unknown" });
        if (o.Connector.Length > 0) parts.Add("over " + o.Connector);
        return string.Join(" · ", parts);
    }

    public static string EncodingWords(PixelEncoding e) => e switch
    {
        PixelEncoding.RGB => "RGB 4:4:4",
        PixelEncoding.YCbCr444 => "YCbCr 4:4:4",
        PixelEncoding.YCbCr422 => "YCbCr 4:2:2",
        PixelEncoding.YCbCr420 => "YCbCr 4:2:0",
        _ => "any encoding",
    };

    public static string AudioWords(AudioPolicy a) => a switch
    {
        AudioPolicy.None => "no audio",
        AudioPolicy.Stereo => "stereo",
        AudioPolicy.Multichannel => "multichannel audio",
        _ => "any audio",
    };

    /// <summary>The words Windows cannot answer with on this path — said instead of a guess.</summary>
    public const string NotAvailable = "not available from this Windows path";

    /// <summary>
    /// One screen's report. <paramref name="screenWidth"/> / <paramref name="screenHeight"/> are the
    /// screen's pixels (the contract's raster stands in when it says one); <paramref name="presentFps"/>
    /// the rate the output presents at (0: the display's own); <paramref name="displayHz"/> the
    /// display's refresh as Windows' mode says it (0 unknown); <paramref name="clockHz"/> the render
    /// clock as measured (≤ 0 not measured).
    /// </summary>
    public static SignalReport Compare(string label, SignalContract? contract, int screenWidth, int screenHeight, int presentFps, int displayHz, SignalObservation? observed, double clockHz)
    {
        var lines = new List<SignalLine>();
        var design = DesignWords(contract);
        var requested = RequestedWords(screenWidth, screenHeight, presentFps, displayHz);
        var observedWords = ObservedWords(observed);
        var set = contract is { IsSet: true };

        if (!set)
        {
            // No contract: evidence alone, no verdict — and a line only when there is evidence to show.
            if (observed is not null) lines.Add(new SignalLine("Signal", CheckLight.Grey, observedWords, "no contract to hold it against — Screens page, SIGNAL CONTRACT"));
            return new SignalReport(label, SignalVerdict.Unverified, design, requested, observedWords, lines);
        }

        lines.Add(new SignalLine("Contract", CheckLight.Green, design));
        if (observed is null)
        {
            lines.Add(new SignalLine("Observed", CheckLight.Grey, "not observed", "the display is not attached, or Windows did not answer for it — nothing is verified"));
            return new SignalReport(label, SignalVerdict.Unverified, design, requested, observedWords, lines);
        }

        var mismatch = false;
        var rasterKnown = false;
        var rateKnown = false;

        // The raster: the contract's, else the screen's own pixels.
        var wantW = contract!.Width > 0 ? contract.Width : screenWidth;
        var wantH = contract.Height > 0 ? contract.Height : screenHeight;
        if (observed.RasterWidth > 0 && observed.RasterHeight > 0 && wantW > 0 && wantH > 0)
        {
            rasterKnown = true;
            var same = observed.RasterWidth == wantW && observed.RasterHeight == wantH;
            mismatch |= !same;
            lines.Add(new SignalLine("Raster", same ? CheckLight.Green : CheckLight.Red, $"expected {wantW}×{wantH} · detected {observed.RasterWords}{(observed.Interlaced ? " interlaced" : "")}",
                same ? "" : "FIX: pick the contract's mode on the Screens page (Display mode), or a processor input that offers it"));
        }
        else
        {
            lines.Add(new SignalLine("Raster", CheckLight.Grey, $"expected {wantW}×{wantH} · detected unknown"));
        }

        // The rate: exact rationals, never "close enough".
        if (contract.Rate.IsSet)
        {
            if (observed.Rate.IsSet)
            {
                rateKnown = true;
                var same = observed.Rate == contract.Rate;
                mismatch |= !same;
                lines.Add(new SignalLine("Rate", same ? CheckLight.Green : CheckLight.Amber,
                    same ? $"{contract.Rate.Words} Hz ({contract.Rate.Rational})" : $"{contract.Rate.Words} Hz asked · {observed.Rate.Words} Hz observed",
                    same ? "" : RateNote(contract.Rate, observed.Rate)));
            }
            else
            {
                lines.Add(new SignalLine("Rate", CheckLight.Grey, $"{contract.Rate.Words} Hz asked · observed unknown"));
            }
        }
        else if (observed.Rate.IsSet)
        {
            rateKnown = true;
            lines.Add(new SignalLine("Rate", CheckLight.Green, $"{observed.Rate.Words} Hz — the display's own, as the contract leaves it"));
        }

        // The encoding, the depth, HDR: compared where the path states them, unknown where it does not.
        if (contract.Encoding != PixelEncoding.Any)
        {
            if (observed.Encoding is { } enc)
            {
                var same = enc == contract.Encoding;
                mismatch |= !same;
                lines.Add(new SignalLine("Encoding", same ? CheckLight.Green : CheckLight.Amber, same ? EncodingWords(enc) : $"{EncodingWords(contract.Encoding)} asked · {EncodingWords(enc)} observed",
                    same ? "" : "FIX: the GPU's output colour format (the driver's control panel), or the processor's input format — 4:2:0 softens text and fine lines"));
            }
            else
            {
                lines.Add(new SignalLine("Encoding", CheckLight.Grey, $"{EncodingWords(contract.Encoding)} asked · observed unknown", NotAvailable));
            }
        }
        if (contract.BitDepth > 0)
        {
            if (observed.BitsPerChannel > 0)
            {
                var same = observed.BitsPerChannel == contract.BitDepth;
                mismatch |= !same;
                lines.Add(new SignalLine("Bit depth", same ? CheckLight.Green : CheckLight.Amber, same ? $"{contract.BitDepth}-bit" : $"{contract.BitDepth}-bit asked · {observed.BitsPerChannel}-bit observed",
                    same ? "" : "FIX: the GPU's output depth (the driver's control panel) — a link without the bandwidth drops to 8-bit or 4:2:0 by itself"));
            }
            else
            {
                lines.Add(new SignalLine("Bit depth", CheckLight.Grey, $"{contract.BitDepth}-bit asked · observed unknown", NotAvailable));
            }
        }
        if (contract.Dynamic != DynamicRange.Any)
        {
            var wantsHdr = contract.Dynamic != DynamicRange.SDR;
            if (observed.HdrActive is { } active)
            {
                var same = active == wantsHdr;
                mismatch |= !same;
                lines.Add(new SignalLine("Dynamic range", same ? CheckLight.Green : CheckLight.Amber,
                    same ? contract.Dynamic.ToString() : $"{contract.Dynamic} asked · {(active ? "HDR on" : "SDR")} observed",
                    same ? "" : active ? "FIX: Windows' HDR switch is on for this display (Settings → Display) and the contract is SDR — the picture is tone-mapped somewhere" : "FIX: Windows' HDR switch is off for this display and the contract asks for HDR"));
            }
            else
            {
                lines.Add(new SignalLine("Dynamic range", CheckLight.Grey, $"{contract.Dynamic} asked · observed unknown", NotAvailable));
            }
            if (observed.HdrCapable == true && !wantsHdr && observed.HdrActive != true)
            {
                lines.Add(new SignalLine("HDR capability", CheckLight.Grey, "the receiver advertises HDR; the contract is SDR", "capability, not the signal — nothing to fix unless the picture says so"));
            }
        }

        if (contract.Colour != ColourSpace.Any)
        {
            // Windows does not report the active colorimetry: the contract's word stands, the EDID's advertisement joins in 65.7.
            lines.Add(new SignalLine("Colour space", CheckLight.Grey, $"{ColourWords(contract.Colour)} asked · observed unknown", NotAvailable));
        }
        if (contract.Transport != SignalTransport.Any)
        {
            var seen = TransportOf(observed.Connector);
            if (seen != SignalTransport.Any)
            {
                var same = seen == contract.Transport;
                mismatch |= !same;
                lines.Add(new SignalLine("Transport", same ? CheckLight.Green : CheckLight.Amber,
                    same ? TransportWords(seen) : $"{TransportWords(contract.Transport)} asked · {TransportWords(seen)} observed",
                    same ? "" : "FIX: the display is on another connector than the contract names — the wrong port on the card, or an adapter in the path"));
            }
            else
            {
                lines.Add(new SignalLine("Transport", CheckLight.Grey, $"{TransportWords(contract.Transport)} asked · observed unknown", NotAvailable));
            }
        }

        // The render clock against the rate the output needs (round 64's rule, from the observed rate when it is known).
        var needed = contract.Rate.IsSet ? (int)Math.Round(contract.Rate.Hz) : displayHz;
        var limit = OutputRate.ClockLimit(presentFps, observed.Rate.IsSet ? (int)Math.Round(observed.Rate.Hz) : needed, clockHz);
        if (clockHz > 0 && needed > 0)
        {
            lines.Add(limit.Limited
                ? new SignalLine("Render clock", CheckLight.Amber, $"{clockHz:0.0} Hz · {limit.NeededHz} Hz needed", "LIMITED BY RENDER CLOCK — make the display that needs the higher rate the one the render clock follows")
                : new SignalLine("Render clock", CheckLight.Green, $"{clockHz:0.0} Hz"));
        }

        var verdict = mismatch ? SignalVerdict.Mismatch : rasterKnown && rateKnown ? SignalVerdict.Match : SignalVerdict.Unverified;
        return new SignalReport(label, verdict, design, requested, observedWords, lines);
    }

    /// <summary>The note under a rate that is not the contract's: the families, or the slip a near miss costs.</summary>
    public static string RateNote(SignalRate asked, SignalRate observed)
    {
        var gap = Math.Abs(asked.Hz - observed.Hz);
        if (gap > 0 && gap < 0.5)
        {
            return $"{observed.Words} is not {asked.Words}: a frame repeats or drops every {1 / gap:0.0} s somewhere down the chain — pick the exact mode on the Screens page (Display mode)";
        }
        var a = asked.Family;
        var o = observed.Family;
        return a != o && a != TimingFamily.Unknown && o != TimingFamily.Unknown
            ? $"{asked.Words} is {SignalRate.FamilyWords(a)}, {observed.Words} is {SignalRate.FamilyWords(o)}: a conversion, judder, repeats or drops down the chain — pick the contract's mode on the Screens page (Display mode), or change the contract"
            : "FIX: pick the contract's mode on the Screens page (Display mode), or change the contract";
    }
}

/// <summary>
/// The contract in words, both ways: "3840x2160 50 RGB 8 SDR FULL 8CH" on the wire, the Screens
/// page and a cue's notes. WxH is the raster, a number of 20 or more (50, 59.94, 60000/1001) the
/// rate, RGB / 444 / 422 / 420 the encoding, 8 / 10 / 12 (or 8-bit) the depth, FULL / LIMITED
/// the range, SDR / HDR10 / HLG the dynamic range, STEREO / 8CH / NOAUDIO the audio, DIAGNOSTIC
/// the fallback profile, CLEAR the empty contract.
/// </summary>
public static class SignalWords
{
    private static readonly Regex Raster = new(@"^(\d{3,5})[x×](\d{3,5})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Bits = new(@"^(8|10|12|16)(-?bits?)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public const string Vocabulary = "WxH, a rate (50, 59.94, 60000/1001), RGB / 444 / 422 / 420, 8 / 10 / 12 bit, FULL / LIMITED, SDR / HDR10 / HLG, 709 / P3 / 2020, STEREO / 8CH / NOAUDIO, HDMI / DP / SDI / DVI / VGA / USBC, DIAGNOSTIC, or CLEAR";

    /// <summary>Applies the words to the contract — each word sets its property, the rest stay. "" on success, else the word that was not one and the vocabulary.</summary>
    public static string Apply(string? words, SignalContract into)
    {
        var tokens = (words ?? "").Split(new[] { ' ', '·', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var raw in tokens)
        {
            var t = raw.ToUpperInvariant();
            if (t is "CLEAR" or "NONE" or "OFF")
            {
                into.Clear();
                continue;
            }
            if (t is "DIAGNOSTIC" or "FALLBACK")
            {
                into.Fallback = "diagnostic";
                continue;
            }
            if (t is "NOFALLBACK")
            {
                into.Fallback = "";
                continue;
            }
            if (Raster.Match(raw) is { Success: true } m)
            {
                into.Width = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                into.Height = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                continue;
            }
            if (Bits.Match(raw) is { Success: true } b)
            {
                into.BitDepth = int.Parse(b.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }
            switch (t)
            {
                case "RGB": case "444RGB": into.Encoding = PixelEncoding.RGB; continue;
                case "444": case "4:4:4": case "YCBCR": case "YCBCR444": case "YCC444": into.Encoding = PixelEncoding.YCbCr444; continue;
                case "422": case "4:2:2": case "YCBCR422": case "YCC422": into.Encoding = PixelEncoding.YCbCr422; continue;
                case "420": case "4:2:0": case "YCBCR420": case "YCC420": into.Encoding = PixelEncoding.YCbCr420; continue;
                case "FULL": into.Range = QuantizationRange.Full; continue;
                case "LIMITED": case "VIDEO": into.Range = QuantizationRange.Limited; continue;
                case "SDR": into.Dynamic = DynamicRange.SDR; continue;
                case "HDR": case "HDR10": case "PQ": into.Dynamic = DynamicRange.HDR10; continue;
                case "HLG": into.Dynamic = DynamicRange.HLG; continue;
                case "NOAUDIO": case "MUTE": case "SILENT": into.Audio = AudioPolicy.None; continue;
                case "STEREO": case "2CH": into.Audio = AudioPolicy.Stereo; continue;
                case "MULTI": case "MULTICHANNEL": case "8CH": case "6CH": case "5.1": case "7.1": into.Audio = AudioPolicy.Multichannel; continue;
                case "709": case "REC709": case "BT709": case "BT.709": case "REC.709": into.Colour = ColourSpace.Rec709; continue;
                case "P3": case "DCI-P3": case "DCIP3": into.Colour = ColourSpace.DciP3; continue;
                case "2020": case "REC2020": case "BT2020": case "BT.2020": case "REC.2020": into.Colour = ColourSpace.Rec2020; continue;
                case "HDMI": into.Transport = SignalTransport.HDMI; continue;
                case "DP": case "DISPLAYPORT": into.Transport = SignalTransport.DisplayPort; continue;
                case "SDI": into.Transport = SignalTransport.SDI; continue;
                case "DVI": into.Transport = SignalTransport.DVI; continue;
                case "VGA": into.Transport = SignalTransport.VGA; continue;
                case "USBC": case "USB-C": case "USB4": into.Transport = SignalTransport.UsbC; continue;
                case "INTERNAL": case "EDP": case "PANEL": into.Transport = SignalTransport.Internal; continue;
            }
            var rate = SignalRate.Parse(raw);
            if (rate.IsSet)
            {
                into.Rate = rate;
                continue;
            }
            return $"'{raw}' is not a signal word — {Vocabulary}";
        }
        return "";
    }

    /// <summary>The contract as the words that make it again: "3840x2160 50 RGB 8 SDR FULL 8CH"; "" for an empty one.</summary>
    public static string Of(SignalContract? c)
    {
        if (c is null || !c.IsSet) return "";
        var parts = new List<string>();
        if (c.Width > 0 && c.Height > 0) parts.Add($"{c.Width}x{c.Height}");
        if (c.Rate.IsSet) parts.Add(c.Rate.Words);
        switch (c.Encoding)
        {
            case PixelEncoding.RGB: parts.Add("RGB"); break;
            case PixelEncoding.YCbCr444: parts.Add("444"); break;
            case PixelEncoding.YCbCr422: parts.Add("422"); break;
            case PixelEncoding.YCbCr420: parts.Add("420"); break;
        }
        if (c.BitDepth > 0) parts.Add(c.BitDepth.ToString(CultureInfo.InvariantCulture));
        if (c.Range == QuantizationRange.Full) parts.Add("FULL");
        else if (c.Range == QuantizationRange.Limited) parts.Add("LIMITED");
        if (c.Dynamic != DynamicRange.Any) parts.Add(c.Dynamic.ToString().ToUpperInvariant());
        switch (c.Colour)
        {
            case ColourSpace.Rec709: parts.Add("709"); break;
            case ColourSpace.DciP3: parts.Add("P3"); break;
            case ColourSpace.Rec2020: parts.Add("2020"); break;
        }
        switch (c.Audio)
        {
            case AudioPolicy.None: parts.Add("NOAUDIO"); break;
            case AudioPolicy.Stereo: parts.Add("STEREO"); break;
            case AudioPolicy.Multichannel: parts.Add("8CH"); break;
        }
        switch (c.Transport)
        {
            case SignalTransport.HDMI: parts.Add("HDMI"); break;
            case SignalTransport.DisplayPort: parts.Add("DP"); break;
            case SignalTransport.SDI: parts.Add("SDI"); break;
            case SignalTransport.DVI: parts.Add("DVI"); break;
            case SignalTransport.VGA: parts.Add("VGA"); break;
            case SignalTransport.UsbC: parts.Add("USBC"); break;
            case SignalTransport.Internal: parts.Add("INTERNAL"); break;
        }
        if (c.Fallback == "diagnostic") parts.Add("DIAGNOSTIC");
        return string.Join(' ', parts);
    }
}
