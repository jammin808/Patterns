using System.Globalization;
using System.Text;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// What a planned screen's EDID says (round 65.8): the raster and the exact rate the contract
/// asks, the encoding, the depth, HDR, the colour space, the audio and the connector — the identity
/// Patterns gives it. A processor input or a PC loads it as a custom EDID so a source reads the plan
/// and sends exactly that; nothing else is advertised, because a narrow EDID is how a source is
/// made to choose the intended mode (the guide's design intent).
/// </summary>
public sealed record EdidPlan(
    int Width,
    int Height,
    SignalRate Rate,
    string Name,
    string Manufacturer = "PTN",
    int ProductCode = 1,
    uint Serial = 1,
    string SerialText = "",
    PixelEncoding Encoding = PixelEncoding.RGB,
    int BitDepth = 8,
    DynamicRange Dynamic = DynamicRange.SDR,
    ColourSpace Colour = ColourSpace.Rec709,
    AudioPolicy Audio = AudioPolicy.Stereo,
    SignalTransport Transport = SignalTransport.HDMI,
    QuantizationRange Range = QuantizationRange.Any,
    int WidthCm = 0,
    int HeightCm = 0,
    int Year = 0)
{
    /// <summary>
    /// The plan a screen's contract makes: the contract's words where it has them, the screen's own
    /// size and a conservative choice where it does not (RGB, 8-bit, SDR, Rec. 709, stereo, HDMI),
    /// and the screen's identity in the product code.
    /// </summary>
    public static EdidPlan ForContract(SignalContract? c, string name, int screenWidth, int screenHeight, int productCode, uint serial = 1)
    {
        c ??= new SignalContract();
        var width = c.Width > 0 ? c.Width : screenWidth;
        var height = c.Height > 0 ? c.Height : screenHeight;
        var rate = c.Rate.IsSet ? c.Rate : SignalRate.Of(60, 1);
        return new EdidPlan(width, height, rate, name, ProductCode: productCode, Serial: serial,
            Encoding: c.Encoding == PixelEncoding.Any ? PixelEncoding.RGB : c.Encoding,
            BitDepth: c.BitDepth > 0 ? c.BitDepth : 8,
            Dynamic: c.Dynamic == DynamicRange.Any ? DynamicRange.SDR : c.Dynamic,
            Colour: c.Colour == ColourSpace.Any ? ColourSpace.Rec709 : c.Colour,
            Audio: c.Audio == AudioPolicy.Any ? AudioPolicy.Stereo : c.Audio,
            Transport: c.Transport == SignalTransport.Any ? SignalTransport.HDMI : c.Transport,
            Range: c.Range);
    }

    /// <summary>A stable 16-bit product code from a screen's id (FNV-1a), never 0.</summary>
    public static int ProductCodeFor(string screenId)
    {
        uint h = 2166136261;
        foreach (var c in screenId) h = (h ^ c) * 16777619;
        var code = (int)(h & 0xFFFF);
        return code == 0 ? 1 : code;
    }

    /// <summary>The plan in the contract's words: "3840x2160 50 RGB 8 SDR 709 STEREO HDMI".</summary>
    public string Words
    {
        get
        {
            var c = new SignalContract
            {
                Width = Width, Height = Height, Rate = Rate, Encoding = Encoding, BitDepth = BitDepth,
                Dynamic = Dynamic, Colour = Colour, Audio = Audio, Transport = Transport, Range = Range,
            };
            return SignalWords.Of(c);
        }
    }
}

/// <summary>A video timing as the EDID carries it: the clock and the totals, with the porches and syncs that make them.</summary>
public sealed record EdidTimingPlan(int PixelClockKHz, int HActive, int HBlank, int HFront, int HSync, int VActive, int VBlank, int VFront, int VSync, bool Standard)
{
    public int HTotal => HActive + HBlank;
    public int VTotal => VActive + VBlank;

    /// <summary>The rate the clock and the totals make, exactly.</summary>
    public SignalRate Rate => SignalRate.Of(PixelClockKHz * 1000L, (long)HTotal * VTotal);

    /// <summary>Whether an 18-byte detailed timing descriptor can carry it: 12-bit actives and blanks, a 16-bit clock in 10 kHz.</summary>
    public bool FitsDtd => HActive <= 4095 && HBlank <= 4095 && VActive <= 4095 && VBlank <= 4095 && HFront <= 1023 && HSync <= 1023 && VFront <= 63 && VSync <= 63 && PixelClockKHz <= 655350;

    public string Words => $"{HActive}×{VActive}p{Rate.Words} · {PixelClockKHz / 1000.0:0.###} MHz · {HTotal}×{VTotal} total{(Standard ? " (CTA-861 timing)" : " (CVT reduced blanking)")}";
}

/// <summary>
/// The EDID writer (round 65.8): an E-EDID 1.4 base block, a CTA-861 extension, and a DisplayID 2.0
/// extension when the raster or the clock is past what an 18-byte descriptor can say. The timing is
/// the CTA-861 standard one where the raster and rate are a known format (so a processor sees the
/// totals it expects), else CVT reduced blanking with the vertical total chosen so the clock lands
/// on a 10 kHz step and the rate is exact. Every block's checksum computed; the result parses back
/// through <see cref="Edid.Parse"/>, which is the test.
/// </summary>
public static class EdidWriter
{
    /// <summary>The timing for a raster at a rate: the standard totals when CTA-861 names the format, else CVT reduced blanking.</summary>
    public static EdidTimingPlan Timing(int width, int height, SignalRate rate)
    {
        if (!rate.IsSet) rate = SignalRate.Of(60, 1);
        // The standard totals when a descriptor can hold them: 2160p50's 1056-pixel front porch is past the descriptor's 10-bit field, so that one is spelt with reduced blanking and the VIC carries the standard timing.
        if (Standard(width, height, rate) is { } standard && standard.FitsDtd) return standard;
        return Cvt(width, height, rate);
    }

    /// <summary>The CTA-861 totals for the formats a processor expects to see spelt the standard way.</summary>
    private static EdidTimingPlan? Standard(int width, int height, SignalRate rate)
    {
        var hz = (int)Math.Round(rate.Hz);
        var fractional = rate.Denominator != 1;
        (int HBlank, int HFront, int HSync, int VBlank, int VFront, int VSync)? t = (width, height, hz) switch
        {
            (1280, 720, 60) => (370, 110, 40, 30, 5, 5),
            (1280, 720, 50) => (700, 440, 40, 30, 5, 5),
            (1280, 720, 30) => (2020, 1760, 40, 30, 5, 5),
            (1280, 720, 25) => (2680, 2420, 40, 30, 5, 5),
            (1280, 720, 24) => (2020, 1760, 40, 30, 5, 5),
            (1920, 1080, 60) => (280, 88, 44, 45, 4, 5),
            (1920, 1080, 50) => (720, 528, 44, 45, 4, 5),
            (1920, 1080, 30) => (280, 88, 44, 45, 4, 5),
            (1920, 1080, 25) => (720, 528, 44, 45, 4, 5),
            (1920, 1080, 24) => (830, 638, 44, 45, 4, 5),
            (1920, 1080, 120) => (280, 88, 44, 45, 4, 5),
            (1920, 1080, 100) => (720, 528, 44, 45, 4, 5),
            (3840, 2160, 60) => (560, 176, 88, 90, 8, 10),
            (3840, 2160, 50) => (1440, 1056, 88, 90, 8, 10),
            (3840, 2160, 30) => (560, 176, 88, 90, 8, 10),
            (3840, 2160, 25) => (1440, 1056, 88, 90, 8, 10),
            (3840, 2160, 24) => (1660, 1276, 88, 90, 8, 10),
            (4096, 2160, 60) => (304, 88, 88, 90, 8, 10),
            (4096, 2160, 50) => (1184, 968, 88, 90, 8, 10),
            (4096, 2160, 24) => (1404, 1020, 88, 90, 8, 10),
            _ => null,
        };
        if (t is null) return null;
        var (hBlank, hFront, hSync, vBlank, vFront, vSync) = t.Value;
        var hTotal = width + hBlank;
        var vTotal = height + vBlank;
        // The clock the totals make at the exact rate — 148.5 MHz at 60, 148.35 at 59.94 (the 10 kHz step the descriptor has; the parser snaps it back).
        var clockKHz = (int)Math.Round(rate.Numerator * (double)hTotal * vTotal / rate.Denominator / 1000.0 / 10.0) * 10;
        if (!fractional) clockKHz = (int)(hz * (long)hTotal * vTotal / 1000);
        return new EdidTimingPlan(clockKHz, width, hBlank, hFront, hSync, height, vBlank, vFront, vSync, true);
    }

    /// <summary>
    /// CVT reduced blanking (VESA CVT 1.2 RB): 160 pixels of horizontal blanking, at least 460 µs of
    /// vertical, the vertical total then walked up a few lines until the clock at the exact rate lands
    /// on a 10 kHz step — so a 1920 × 1200 wall at 50 Hz is exactly 50 Hz, not 49.98.
    /// </summary>
    public static EdidTimingPlan Cvt(int width, int height, SignalRate rate)
    {
        const int hBlank = 160, hFront = 48, hSync = 32, vFront = 3, minVBack = 6;
        var vSync = VSyncFor(width, height);
        var rateHz = rate.Hz;
        var hPeriodEst = ((1_000_000.0 / rateHz) - 460.0) / (height + vFront);   // µs per line, estimated
        var vbiLines = (int)Math.Floor(460.0 / hPeriodEst) + 1;
        var vBlank = Math.Max(vbiLines, vFront + vSync + minVBack);
        var hTotal = width + hBlank;
        // Walk the vertical total up to forty lines for an exact clock on the 10 kHz step; take the nearest when none lands.
        var chosen = vBlank;
        var exact = false;
        for (var extra = 0; extra <= 40; extra++)
        {
            var vTotal = height + vBlank + extra;
            var num = rate.Numerator * (long)hTotal * vTotal;    // clock Hz × denominator
            if (num % (10_000L * rate.Denominator) == 0)
            {
                chosen = vBlank + extra;
                exact = true;
                break;
            }
        }
        var totalV = height + chosen;
        var clockKHz = exact
            ? (int)(rate.Numerator * (long)hTotal * totalV / rate.Denominator / 1000)
            : (int)Math.Round(rate.Numerator * (double)hTotal * totalV / rate.Denominator / 10_000.0) * 10;
        return new EdidTimingPlan(clockKHz, width, hBlank, hFront, hSync, height, chosen, vFront, vSync, false);
    }

    private static int VSyncFor(int width, int height)
    {
        var aspect = (double)width / height;
        if (Math.Abs(aspect - 4.0 / 3) < 0.02) return 4;
        if (Math.Abs(aspect - 16.0 / 9) < 0.02) return 5;
        if (Math.Abs(aspect - 16.0 / 10) < 0.02) return 6;
        if (Math.Abs(aspect - 5.0 / 4) < 0.02) return 7;
        if (Math.Abs(aspect - 15.0 / 9) < 0.02) return 7;
        return 5;
    }

    /// <summary>The EDID for the plan: 256 bytes, or 384 with a DisplayID extension when the raster or the clock is past a descriptor's reach.</summary>
    public static byte[] Build(EdidPlan plan)
    {
        var timing = Timing(plan.Width, plan.Height, plan.Rate);
        var needsDisplayId = !timing.FitsDtd;
        // A descriptor that cannot hold the raster carries a 1920 × 1080 at the plan's rate instead: a source that reads no DisplayID lands on a standard picture, not on nothing.
        var baseTiming = needsDisplayId ? Timing(1920, 1080, plan.Rate) : timing;
        var blocks = new List<byte[]> { BaseBlock(plan, baseTiming, needsDisplayId ? 2 : 1) };
        blocks.Add(CtaBlock(plan, timing));
        if (needsDisplayId) blocks.Add(DisplayIdBlock(timing));
        return blocks.SelectMany(b => b).ToArray();
    }

    /// <summary>The bytes as sixteen hex pairs a line — the form processor tools and EDID editors import.</summary>
    public static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < bytes.Length; i += 16)
        {
            sb.AppendLine(string.Join(' ', bytes.Skip(i).Take(16).Select(b => b.ToString("X2"))));
        }
        return sb.ToString();
    }

    /// <summary>The words that go beside the file: the plan, the timing, what the EDID advertises, its hash — and how to use it.</summary>
    public static string Summary(EdidPlan plan, byte[] bytes)
    {
        var timing = Timing(plan.Width, plan.Height, plan.Rate);
        var info = Edid.Parse(bytes);
        var sb = new StringBuilder();
        sb.AppendLine($"Patterns planned-screen EDID · {plan.Name}");
        sb.AppendLine($"contract: {plan.Words}");
        sb.AppendLine($"timing: {timing.Words}");
        sb.AppendLine($"identity: {info.Identity} · serial {(plan.SerialText.Length > 0 ? plan.SerialText : plan.Serial.ToString(CultureInfo.InvariantCulture))}");
        sb.AppendLine($"blocks: {string.Join(", ", info.Blocks)} · {bytes.Length} bytes · checksums {(info.ChecksumsValid ? "valid" : "BAD")}");
        sb.AppendLine($"sha-256: {info.Hash}");
        sb.AppendLine("advertised:");
        foreach (var line in info.AdvertisedWords.Split('\n')) sb.AppendLine("  " + line);
        if (info.Problems.Count > 0) sb.AppendLine("problems: " + string.Join("; ", info.Problems));
        sb.AppendLine("use: load the .bin (or the .hex) as the custom EDID of the processor input or the PC's port that this screen's link lands on; the source then reads this plan and sends it. Patterns reads the EDID Windows sees and says whether it is this one.");
        return sb.ToString();
    }

    /// <summary>
    /// Writes the three files a processor's or a PC's EDID tool wants — the bytes (.bin), the hex
    /// (.hex) and the summary (.txt) — beside each other, and answers their paths. The base name
    /// is the file's without an extension (<see cref="SafeFileName"/> makes one from a label); the
    /// directory is made when missing.
    /// </summary>
    public static IReadOnlyList<string> Export(string directory, string baseName, EdidPlan plan, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var bin = Path.Combine(directory, baseName + ".bin");
        var hex = Path.Combine(directory, baseName + ".hex");
        var txt = Path.Combine(directory, baseName + ".txt");
        File.WriteAllBytes(bin, bytes);
        File.WriteAllText(hex, Hex(bytes));
        File.WriteAllText(txt, Summary(plan, bytes));
        return new[] { bin, hex, txt };
    }

    /// <summary>"patterns-edid-main-wall": the screen's label as a file name.</summary>
    public static string SafeFileName(string label)
    {
        var sb = new StringBuilder("patterns-edid-");
        var dash = true;                                   // the prefix ends with one: a leading separator adds none
        foreach (var c in (label ?? "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                dash = false;
            }
            else if (!dash)
            {
                sb.Append('-');
                dash = true;
            }
        }
        return sb.ToString().TrimEnd('-');
    }

    // ---- the blocks --------------------------------------------------------------------------

    private static byte[] BaseBlock(EdidPlan plan, EdidTimingPlan timing, int extensions)
    {
        var b = new byte[128];
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }.CopyTo(b, 0);
        var id = ManufacturerId(plan.Manufacturer);
        b[8] = (byte)(id >> 8);
        b[9] = (byte)(id & 0xFF);
        b[10] = (byte)(plan.ProductCode & 0xFF);
        b[11] = (byte)((plan.ProductCode >> 8) & 0xFF);
        b[12] = (byte)(plan.Serial & 0xFF);
        b[13] = (byte)((plan.Serial >> 8) & 0xFF);
        b[14] = (byte)((plan.Serial >> 16) & 0xFF);
        b[15] = (byte)((plan.Serial >> 24) & 0xFF);
        b[16] = 0;
        var year = plan.Year > 0 ? plan.Year : DateTime.UtcNow.Year;
        b[17] = (byte)Math.Clamp(year - 1990, 0, 255);
        b[18] = 1;
        b[19] = 4;
        var depthCode = plan.BitDepth switch { 6 => 1, 8 => 2, 10 => 3, 12 => 4, 14 => 5, 16 => 6, _ => 2 };
        var iface = plan.Transport switch
        {
            SignalTransport.HDMI => 2,
            SignalTransport.DisplayPort or SignalTransport.UsbC => 5,
            SignalTransport.DVI => 1,
            _ => 0,
        };
        b[20] = (byte)(0x80 | (depthCode << 4) | iface);
        b[21] = (byte)Math.Clamp(plan.WidthCm, 0, 255);
        b[22] = (byte)Math.Clamp(plan.HeightCm, 0, 255);
        b[23] = 0x78;                                        // gamma 2.2
        var colourBits = plan.Encoding switch
        {
            PixelEncoding.YCbCr444 => 0x08,
            PixelEncoding.YCbCr422 => 0x10,
            _ => 0x00,                                       // RGB only: a source cannot choose a chroma-subsampled mode the plan did not ask for
        };
        b[24] = (byte)(colourBits | 0x02 | (plan.Colour == ColourSpace.Rec709 ? 0x04 : 0x00));
        Chromaticity(plan.Colour).CopyTo(b, 25);
        for (var i = 38; i < 54; i++) b[i] = 0x01;           // no standard timings
        Dtd(timing).CopyTo(b, 54);
        Descriptor(0xFC, plan.Name).CopyTo(b, 72);
        RangeLimits(timing, plan.Rate).CopyTo(b, 90);
        Descriptor(0xFF, plan.SerialText.Length > 0 ? plan.SerialText : $"{plan.Manufacturer}-{plan.ProductCode:X4}").CopyTo(b, 108);
        b[126] = (byte)extensions;
        b[127] = Edid.ChecksumFor(b.AsSpan(0, 127));
        return b;
    }

    /// <summary>The three-letter PnP id as the two bytes the base block holds ("PTN" → 0x428E).</summary>
    public static int ManufacturerId(string letters)
    {
        var s = (letters ?? "PTN").ToUpperInvariant().PadRight(3, 'X')[..3];
        var id = 0;
        foreach (var c in s)
        {
            var v = c is >= 'A' and <= 'Z' ? c - 'A' + 1 : 24;
            id = (id << 5) | v;
        }
        return id & 0x7FFF;
    }

    /// <summary>The chromaticity bytes for a colour space's primaries and D65, 10 bits each, packed as E-EDID has them.</summary>
    public static byte[] Chromaticity(ColourSpace colour)
    {
        var (rx, ry, gx, gy, bx, by) = colour switch
        {
            ColourSpace.Rec2020 => (0.708, 0.292, 0.170, 0.797, 0.131, 0.046),
            ColourSpace.DciP3 => (0.680, 0.320, 0.265, 0.690, 0.150, 0.060),
            _ => (0.640, 0.330, 0.300, 0.600, 0.150, 0.060),
        };
        const double wx = 0.3127, wy = 0.3290;
        int Q(double v) => (int)Math.Round(v * 1024) & 0x3FF;
        int[] q = { Q(rx), Q(ry), Q(gx), Q(gy), Q(bx), Q(by), Q(wx), Q(wy) };
        var bytes = new byte[10];
        bytes[0] = (byte)(((q[0] & 3) << 6) | ((q[1] & 3) << 4) | ((q[2] & 3) << 2) | (q[3] & 3));
        bytes[1] = (byte)(((q[4] & 3) << 6) | ((q[5] & 3) << 4) | ((q[6] & 3) << 2) | (q[7] & 3));
        for (var i = 0; i < 8; i++) bytes[2 + i] = (byte)(q[i] >> 2);
        return bytes;
    }

    /// <summary>An 18-byte detailed timing descriptor for the timing; digital separate sync, positive polarities.</summary>
    public static byte[] Dtd(EdidTimingPlan t)
    {
        var d = new byte[18];
        var clock = Math.Clamp(t.PixelClockKHz / 10, 1, 65535);
        d[0] = (byte)(clock & 0xFF);
        d[1] = (byte)(clock >> 8);
        d[2] = (byte)(t.HActive & 0xFF);
        d[3] = (byte)(t.HBlank & 0xFF);
        d[4] = (byte)(((t.HActive >> 8) << 4) | ((t.HBlank >> 8) & 0xF));
        d[5] = (byte)(t.VActive & 0xFF);
        d[6] = (byte)(t.VBlank & 0xFF);
        d[7] = (byte)(((t.VActive >> 8) << 4) | ((t.VBlank >> 8) & 0xF));
        d[8] = (byte)(t.HFront & 0xFF);
        d[9] = (byte)(t.HSync & 0xFF);
        d[10] = (byte)(((t.VFront & 0xF) << 4) | (t.VSync & 0xF));
        d[11] = (byte)(((t.HFront >> 8) << 6) | (((t.HSync >> 8) & 3) << 4) | (((t.VFront >> 4) & 3) << 2) | ((t.VSync >> 4) & 3));
        d[17] = 0x1E;
        return d;
    }

    private static byte[] Descriptor(byte tag, string text)
    {
        var d = new byte[18];
        d[3] = tag;
        var i = 5;
        foreach (var c in text.Take(13)) d[i++] = c is >= ' ' and <= '~' ? (byte)c : (byte)'_';
        if (i < 18) d[i++] = 0x0A;
        while (i < 18) d[i++] = 0x20;
        return d;
    }

    /// <summary>The range-limits descriptor: the rates and frequencies around the timing, no timing formula.</summary>
    private static byte[] RangeLimits(EdidTimingPlan t, SignalRate rate)
    {
        var d = new byte[18];
        d[3] = 0xFD;
        var hz = rate.IsSet ? rate.Hz : 60;
        d[5] = (byte)Math.Clamp((int)Math.Floor(Math.Min(hz, 23)), 1, 255);
        d[6] = (byte)Math.Clamp((int)Math.Ceiling(Math.Max(hz, 60)), 1, 255);
        var hKHz = t.PixelClockKHz / (double)t.HTotal;
        d[7] = (byte)Math.Clamp((int)Math.Floor(Math.Min(hKHz, 15)), 1, 255);
        d[8] = (byte)Math.Clamp((int)Math.Ceiling(hKHz) + 1, 1, 255);
        d[9] = (byte)Math.Clamp((int)Math.Ceiling(t.PixelClockKHz / 10_000.0), 1, 255);
        d[10] = 0x01;                                        // range limits only
        d[11] = 0x0A;
        for (var i = 12; i < 18; i++) d[i] = 0x20;
        return d;
    }

    private static byte[] CtaBlock(EdidPlan plan, EdidTimingPlan timing)
    {
        var b = new byte[128];
        b[0] = 0x02;
        b[1] = 0x03;
        var blocks = new List<byte>();
        var vic = VicFor(plan.Width, plan.Height, plan.Rate);
        if (vic > 0)
        {
            if (plan.Encoding == PixelEncoding.YCbCr420) blocks.AddRange(new byte[] { 0xE2, 0x0E, (byte)vic });         // 4:2:0 only: the source has no other way to send the format
            else blocks.AddRange(new byte[] { 0x41, (byte)(vic <= 64 ? vic | 0x80 : vic) });                            // the one format, native
        }
        switch (plan.Audio)
        {
            case AudioPolicy.Stereo:
                blocks.AddRange(new byte[] { 0x23, 0x09, 0x07, 0x07 });                                               // LPCM 2ch 32/44.1/48 kHz 16/20/24-bit
                blocks.AddRange(new byte[] { 0x83, 0x01, 0x00, 0x00 });
                break;
            case AudioPolicy.Multichannel:
                blocks.AddRange(new byte[] { 0x23, 0x0F, 0x07, 0x07 });                                               // LPCM 8ch
                blocks.AddRange(new byte[] { 0x83, 0x4F, 0x00, 0x00 });                                               // 7.1
                break;
        }
        if (plan.Transport == SignalTransport.HDMI)
        {
            var dc = (plan.BitDepth >= 10 ? 0x10 : 0) | (plan.BitDepth >= 12 ? 0x20 : 0) | (plan.BitDepth >= 16 ? 0x40 : 0);
            if (dc != 0 && plan.Encoding == PixelEncoding.YCbCr444) dc |= 0x08;
            var tmds = (byte)Math.Clamp((int)Math.Ceiling(timing.PixelClockKHz / 1000.0 / 5.0), 1, 255);
            blocks.AddRange(new byte[] { 0x67, 0x03, 0x0C, 0x00, 0x10, 0x00, (byte)dc, tmds });                          // HDMI LLC: 1.0.0.0, deep colour, TMDS ceiling
            if (timing.PixelClockKHz > 340_000 || plan.Encoding == PixelEncoding.YCbCr420)
            {
                var dc420 = plan.Encoding == PixelEncoding.YCbCr420 ? (plan.BitDepth >= 10 ? 0x01 : 0) | (plan.BitDepth >= 12 ? 0x02 : 0) : 0;
                blocks.AddRange(new byte[] { 0x67, 0xD8, 0x5D, 0xC4, 0x01, tmds, 0x80, (byte)dc420 });                  // HDMI Forum: HDMI 2.x rate, SCDC
            }
        }
        if (plan.Range != QuantizationRange.Any)
        {
            blocks.AddRange(new byte[] { 0xE2, 0x00, (byte)(0x40 | (plan.Encoding == PixelEncoding.RGB ? 0 : 0x80)) }); // quantisation selectable: the source honours the range it is told
        }
        switch (plan.Colour)
        {
            case ColourSpace.Rec2020: blocks.AddRange(new byte[] { 0xE3, 0x05, 0xC0, 0x00 }); break;                  // BT.2020 YCC + RGB
            case ColourSpace.DciP3: blocks.AddRange(new byte[] { 0xE3, 0x05, 0x00, 0x80 }); break;
        }
        if (plan.Dynamic is DynamicRange.HDR10 or DynamicRange.HLG)
        {
            var eotf = (byte)(0x01 | (plan.Dynamic == DynamicRange.HDR10 ? 0x04 : 0x08));
            blocks.AddRange(new byte[] { 0xE6, 0x06, eotf, 0x01, 0x8A, 0x00, 0x00 });                                  // SDR + the transfer, static metadata type 1, ~1000 nits
        }
        var dtdOffset = 4 + blocks.Count;
        b[2] = (byte)dtdOffset;
        var flags = (plan.Audio != AudioPolicy.None ? 0x40 : 0)
            | (plan.Encoding == PixelEncoding.YCbCr444 ? 0x20 : 0)
            | (plan.Encoding == PixelEncoding.YCbCr422 ? 0x10 : 0)
            | 0x01;                                          // one native detailed timing — the base block's
        b[3] = (byte)flags;
        blocks.CopyTo(b, 4);
        b[127] = Edid.ChecksumFor(b.AsSpan(0, 127));
        return b;
    }

    /// <summary>The CTA-861 VIC for a progressive raster at a rate — the 60 Hz code for 59.94, the 30 for 29.97, the 24 for 23.976; 0 when CTA names none.</summary>
    public static int VicFor(int width, int height, SignalRate rate)
    {
        if (!rate.IsSet) return 0;
        var nominal = SignalRate.Of((long)Math.Round(rate.Hz), 1);
        for (var vic = 1; vic <= 219; vic++)
        {
            var (w, h, r, interlaced) = Edid.Vic(vic);
            if (w == width && h == height && !interlaced && r == nominal) return vic;
        }
        return 0;
    }

    private static byte[] DisplayIdBlock(EdidTimingPlan t)
    {
        var b = new byte[128];
        b[0] = 0x70;
        b[1] = 0x20;                                         // DisplayID 2.0
        b[3] = 0x02;                                         // generic display
        var blocks = new List<byte> { 0x22, 0x00, 20 };
        var units = t.PixelClockKHz - 1;
        blocks.AddRange(new byte[]
        {
            (byte)(units & 0xFF), (byte)((units >> 8) & 0xFF), (byte)((units >> 16) & 0xFF),
            0x80,
            (byte)((t.HActive - 1) & 0xFF), (byte)((t.HActive - 1) >> 8),
            (byte)((t.HBlank - 1) & 0xFF), (byte)((t.HBlank - 1) >> 8),
            (byte)((t.HFront - 1) & 0xFF), (byte)(((t.HFront - 1) >> 8) | 0x80),
            (byte)((t.HSync - 1) & 0xFF), (byte)((t.HSync - 1) >> 8),
            (byte)((t.VActive - 1) & 0xFF), (byte)((t.VActive - 1) >> 8),
            (byte)((t.VBlank - 1) & 0xFF), (byte)((t.VBlank - 1) >> 8),
            (byte)((t.VFront - 1) & 0xFF), (byte)(((t.VFront - 1) >> 8) | 0x80),
            (byte)((t.VSync - 1) & 0xFF), (byte)((t.VSync - 1) >> 8),
        });
        b[2] = (byte)blocks.Count;
        blocks.CopyTo(b, 5);
        var sum = 0;
        for (var i = 1; i < 5 + blocks.Count; i++) sum += b[i];
        b[5 + blocks.Count] = (byte)((256 - (sum & 0xFF)) & 0xFF);
        b[127] = Edid.ChecksumFor(b.AsSpan(0, 127));
        return b;
    }
}

/// <summary>
/// The EDID a screen is planned to present (round 65.8): its number in the wall, its label, the
/// plan its contract made, the bytes, their hash — and the EDID the display presents now, when one
/// was read, so the two can be told apart.
/// </summary>
public sealed record PlannedEdid(int Number, string Label, EdidPlan Plan, byte[] Bytes, string Hash, EdidInfo? Presented)
{
    public string Hex => EdidWriter.Hex(Bytes);

    public string Summary => EdidWriter.Summary(Plan, Bytes);

    /// <summary>"patterns-edid-main-wall": the file name the export suggests.</summary>
    public string FileBase => EdidWriter.SafeFileName(Label);

    /// <summary>Whether the display presents this very EDID; null when none was read.</summary>
    public bool? PresentedMatches => Presented is null ? null : string.Equals(Presented.Hash, Hash, StringComparison.OrdinalIgnoreCase);
}
