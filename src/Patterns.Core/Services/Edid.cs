using System.Security.Cryptography;
using System.Text;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>One timing a display describes: a detailed timing descriptor's raster and clock, whichever block it came from.</summary>
public sealed record EdidTiming(int PixelClockKHz, int HActive, int HBlank, int VActive, int VBlank, bool Interlaced, bool Preferred, string Source)
{
    public int HTotal => HActive + HBlank;
    public int VTotal => VActive + VBlank;

    /// <summary>The vertical rate the clock and the totals make — the field rate when interlaced — as an exact rational snapped onto the canonical one.</summary>
    public SignalRate Rate => PixelClockKHz > 0 && HTotal > 0 && VTotal > 0 ? SignalRate.Of(PixelClockKHz * 1000L, (long)HTotal * VTotal) : SignalRate.None;

    public string Words => $"{HActive}×{VActive}{(Interlaced ? "i" : "p")}{Rate.Words}{(Preferred ? " (preferred)" : "")}";
}

public sealed record EdidStandardTiming(int Width, int Height, int Hz)
{
    public string Words => $"{Width}×{Height}@{Hz}";
}

/// <summary>A short audio descriptor of the CTA block: a format, its channels, its sample rates and — for LPCM — its bit depths.</summary>
public sealed record EdidAudio(string Format, int Channels, IReadOnlyList<int> SampleRatesKHz, IReadOnlyList<int> BitDepths)
{
    public string Words => $"{Format} {Channels}ch{(SampleRatesKHz.Count > 0 ? " " + string.Join("/", SampleRatesKHz) + " kHz" : "")}{(BitDepths.Count > 0 ? " " + string.Join("/", BitDepths) + "-bit" : "")}";
}

/// <summary>A CTA video format: the VIC, what it means, whether the display calls it native, and its 4:2:0 story.</summary>
public sealed record EdidVideoFormat(int Vic, int Width, int Height, SignalRate Rate, bool Interlaced, bool Native, bool Ycc420Only, bool Ycc420Also)
{
    public bool Known => Width > 0;

    public string Words => Known
        ? $"VIC {Vic} {Width}×{Height}{(Interlaced ? "i" : "p")}{Rate.Words}{(Native ? " native" : "")}{(Ycc420Only ? " 4:2:0 only" : Ycc420Also ? " (+4:2:0)" : "")}"
        : $"VIC {Vic}";
}

/// <summary>The HDMI LLC vendor block: the physical address, deep colour, the TMDS clock ceiling.</summary>
public sealed record HdmiInfo(string PhysicalAddress, bool DeepColour30, bool DeepColour36, bool DeepColour48, bool DeepColourY444, int MaxTmdsMHz);

/// <summary>The HDMI Forum block: the character-rate ceiling (HDMI 2.x), SCDC, 4:2:0 deep colour.</summary>
public sealed record HdmiForumInfo(int MaxTmdsCharRateMHz, bool Scdc, bool Ycc420DeepColour30, bool Ycc420DeepColour36, bool Ycc420DeepColour48);

/// <summary>The HDR static metadata block: the transfer functions the display accepts and its luminance, when stated.</summary>
public sealed record HdrInfo(bool Sdr, bool TraditionalHdr, bool Pq, bool Hlg, bool StaticMetadataType1, double? MaxLuminanceNits, double? MaxFrameAverageNits, double? MinLuminanceNits)
{
    public string Words
    {
        get
        {
            var parts = new List<string>();
            if (Pq) parts.Add("PQ (HDR10)");
            if (Hlg) parts.Add("HLG");
            if (TraditionalHdr) parts.Add("traditional HDR");
            if (parts.Count == 0) return Sdr ? "SDR only" : "none";
            var words = string.Join(", ", parts);
            if (MaxLuminanceNits is { } max) words += $" · up to {max:0} nits";
            return words;
        }
    }
}

/// <summary>The CTA-861 extension: what an HDMI source reads.</summary>
public sealed record CtaInfo(
    int Revision,
    bool Underscan,
    bool BasicAudio,
    bool YCbCr444,
    bool YCbCr422,
    int NativeCount,
    IReadOnlyList<EdidVideoFormat> Video,
    IReadOnlyList<EdidAudio> Audio,
    IReadOnlyList<string> Speakers,
    HdmiInfo? Hdmi,
    HdmiForumInfo? HdmiForum,
    IReadOnlyList<string> Colorimetry,
    HdrInfo? Hdr,
    bool QuantisationSelectableRgb,
    bool QuantisationSelectableYcc,
    IReadOnlyList<EdidTiming> Timings,
    IReadOnlyList<string> Blocks);

/// <summary>A tiled display's place in its wall, from a DisplayID tiled-topology block.</summary>
public sealed record TiledTopology(int TotalHorizontal, int TotalVertical, int Column, int Row, int TileWidth, int TileHeight, bool SingleEnclosure)
{
    public string Words => $"tile {Column},{Row} of {TotalHorizontal}×{TotalVertical} · {TileWidth}×{TileHeight} each{(SingleEnclosure ? " · one enclosure" : "")}";
}

/// <summary>The DisplayID extension: the modern timings (past 4095 pixels), the tiled topology, the blocks seen.</summary>
public sealed record DisplayIdInfo(string Version, string ProductType, IReadOnlyList<EdidTiming> Timings, TiledTopology? Tiled, IReadOnlyList<string> Blocks);

/// <summary>
/// An EDID read, not judged (round 65): the identity, the base block's parameters and timings,
/// the CTA extension's formats, audio, colour and HDR, the DisplayID extension's timings and
/// tiling, each block's checksum, and a hash of the whole. Every field is what the display
/// advertises — a capability, never the signal: an EDID that offers RGB is not a signal that is RGB.
/// </summary>
public sealed record EdidInfo(
    string Manufacturer,
    int ProductCode,
    uint SerialNumber,
    string SerialText,
    string Name,
    int Week,
    int Year,
    string Version,
    bool Digital,
    string Interface,
    int ColourDepthBits,
    int WidthCm,
    int HeightCm,
    double Gamma,
    IReadOnlyList<string> BaseEncodings,
    IReadOnlyList<string> EstablishedTimings,
    IReadOnlyList<EdidStandardTiming> StandardTimings,
    IReadOnlyList<EdidTiming> DetailedTimings,
    int ExtensionCount,
    IReadOnlyList<string> Blocks,
    IReadOnlyList<bool> BlockChecksums,
    CtaInfo? Cta,
    DisplayIdInfo? DisplayId,
    IReadOnlyList<string> Problems,
    string Hash,
    int Length)
{
    /// <summary>The preferred timing: the first detailed timing (E-EDID 1.4 makes it so; 1.3 says so with a flag).</summary>
    public EdidTiming? Preferred => DetailedTimings.FirstOrDefault(t => t.Preferred) ?? DetailedTimings.FirstOrDefault();

    public bool ChecksumsValid => BlockChecksums.Count > 0 && BlockChecksums.All(ok => ok);

    /// <summary>"DEL A0C0 · DELL U2415": the PNP id, the product code and the name.</summary>
    public string Identity => $"{Manufacturer} {ProductCode:X4}{(Name.Length > 0 ? " · " + Name : "")}";

    public string ShortHash => Hash.Length >= 8 ? Hash[..8] : Hash;

    /// <summary>Every timing the display describes, base and extensions together.</summary>
    public IEnumerable<EdidTiming> AllTimings => DetailedTimings.Concat(Cta?.Timings ?? Array.Empty<EdidTiming>()).Concat(DisplayId?.Timings ?? Array.Empty<EdidTiming>());

    /// <summary>The rates the display advertises: every detailed timing's, every known VIC's — a 60 Hz VIC carries 59.94 too, as CTA-861 has it.</summary>
    public IReadOnlyList<SignalRate> RatesOffered
    {
        get
        {
            var set = new HashSet<SignalRate>();
            foreach (var t in AllTimings) if (t.Rate.IsSet) set.Add(t.Rate);
            foreach (var v in Cta?.Video ?? Array.Empty<EdidVideoFormat>())
            {
                if (!v.Known) continue;
                set.Add(v.Rate);
                if (v.Rate.Family == TimingFamily.Integer && v.Rate.Denominator == 1) set.Add(SignalRate.Of(v.Rate.Numerator * 1000L, 1001));
            }
            foreach (var s in StandardTimings) set.Add(SignalRate.Of(s.Hz, 1));
            return set.OrderBy(r => r.Hz).ToList();
        }
    }

    /// <summary>The rasters the display advertises, largest first.</summary>
    public IReadOnlyList<(int Width, int Height)> RastersOffered
    {
        get
        {
            var set = new HashSet<(int, int)>();
            foreach (var t in AllTimings) if (t.HActive > 0) set.Add((t.HActive, t.VActive * (t.Interlaced ? 2 : 1)));
            foreach (var v in Cta?.Video ?? Array.Empty<EdidVideoFormat>()) if (v.Known) set.Add((v.Width, v.Height));
            foreach (var s in StandardTimings) set.Add((s.Width, s.Height));
            return set.OrderByDescending(r => (long)r.Item1 * r.Item2).ToList();
        }
    }

    public bool OffersRaster(int width, int height) => RastersOffered.Contains((width, height));

    public bool OffersRate(SignalRate rate) => rate.IsSet && RatesOffered.Contains(rate);

    /// <summary>The encodings advertised: RGB always for a digital display; YCbCr 4:4:4 / 4:2:2 from the base block or the CTA flags; 4:2:0 from the CTA blocks.</summary>
    public IReadOnlyList<string> EncodingsOffered
    {
        get
        {
            var list = new List<string>();
            if (Digital || BaseEncodings.Count > 0) list.Add("RGB 4:4:4");
            if (BaseEncodings.Contains("YCbCr 4:4:4") || Cta?.YCbCr444 == true) list.Add("YCbCr 4:4:4");
            if (BaseEncodings.Contains("YCbCr 4:2:2") || Cta?.YCbCr422 == true) list.Add("YCbCr 4:2:2");
            if (Cta?.Video.Any(v => v.Ycc420Only || v.Ycc420Also) == true) list.Add("YCbCr 4:2:0");
            return list;
        }
    }

    public bool OffersEncoding(PixelEncoding encoding) => encoding switch
    {
        PixelEncoding.RGB => EncodingsOffered.Contains("RGB 4:4:4"),
        PixelEncoding.YCbCr444 => EncodingsOffered.Contains("YCbCr 4:4:4"),
        PixelEncoding.YCbCr422 => EncodingsOffered.Contains("YCbCr 4:2:2"),
        PixelEncoding.YCbCr420 => EncodingsOffered.Contains("YCbCr 4:2:0"),
        _ => true,
    };

    /// <summary>The bits per channel advertised: 8 for any digital display, the deeper ones the HDMI block or the base block name.</summary>
    public IReadOnlyList<int> BitDepthsOffered
    {
        get
        {
            var set = new SortedSet<int>();
            if (Digital) set.Add(8);
            if (ColourDepthBits >= 6) set.Add(ColourDepthBits);
            if (Cta?.Hdmi is { } h)
            {
                if (h.DeepColour30) set.Add(10);
                if (h.DeepColour36) set.Add(12);
                if (h.DeepColour48) set.Add(16);
            }
            return set.ToList();
        }
    }

    public bool OffersBitDepth(int bits) => bits <= 0 || BitDepthsOffered.Contains(bits);

    /// <summary>Whether the display advertises an HDR transfer at all.</summary>
    public bool OffersHdr => Cta?.Hdr is { } hdr && (hdr.Pq || hdr.Hlg || hdr.TraditionalHdr);

    public bool OffersDynamic(DynamicRange d) => d switch
    {
        DynamicRange.HDR10 => Cta?.Hdr?.Pq == true,
        DynamicRange.HLG => Cta?.Hdr?.Hlg == true,
        _ => true,
    };

    /// <summary>Whether the colour space is advertised: Rec. 709 always (the default), the others from the colorimetry block.</summary>
    public bool OffersColour(ColourSpace c) => c switch
    {
        ColourSpace.Rec2020 => Cta?.Colorimetry.Any(w => w.StartsWith("BT.2020", StringComparison.Ordinal)) == true,
        ColourSpace.DciP3 => Cta?.Colorimetry.Contains("DCI-P3") == true,
        _ => true,
    };

    /// <summary>The most channels an audio descriptor offers; 0 when none (the display is silent, or says nothing).</summary>
    public int AudioChannels => Cta?.Audio.Count > 0 ? Cta.Audio.Max(a => a.Channels) : Cta?.BasicAudio == true ? 2 : 0;

    /// <summary>One line: "DEL A0C0 · DELL U2415 · 1920×1200p59.95 preferred · 1 extension · DisplayPort · 41A28F3C".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Identity };
            if (Preferred is { } p) parts.Add(p.Words.Replace(" (preferred)", " preferred"));
            parts.Add(ExtensionCount == 0 ? "no extension" : $"{ExtensionCount} extension{(ExtensionCount == 1 ? "" : "s")}");
            if (Interface.Length > 0) parts.Add(Interface);
            if (!ChecksumsValid) parts.Add("CHECKSUM BAD");
            parts.Add(ShortHash);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The ADVERTISED block of a screen's technical view: rasters, rates, encodings, depths, HDR, audio — capability, as the EDID states it.</summary>
    public string AdvertisedWords
    {
        get
        {
            var lines = new List<string>();
            var rasters = RastersOffered;
            if (rasters.Count > 0) lines.Add(string.Join(" / ", rasters.Take(6).Select(r => $"{r.Width}×{r.Height}")) + (rasters.Count > 6 ? $" … ({rasters.Count} rasters)" : ""));
            var rates = RatesOffered;
            if (rates.Count > 0) lines.Add(string.Join(" / ", rates.Select(r => r.Words)) + " Hz");
            var encodings = EncodingsOffered;
            if (encodings.Count > 0) lines.Add(string.Join(" / ", encodings));
            var depths = BitDepthsOffered;
            if (depths.Count > 0) lines.Add(string.Join(" / ", depths.Select(d => $"{d}-bit")));
            lines.Add(Cta?.Hdr is { } hdr ? "HDR: " + hdr.Words : "HDR: none advertised");
            if (Cta?.Colorimetry.Count > 0) lines.Add("colorimetry: " + string.Join(", ", Cta.Colorimetry));
            lines.Add(AudioChannels > 0 ? $"audio: up to {AudioChannels} channels" : "audio: none advertised");
            if (DisplayId?.Tiled is { } tiled) lines.Add(tiled.Words);
            return string.Join("\n", lines);
        }
    }
}

/// <summary>
/// The EDID parser (round 65): E-EDID 1.3 / 1.4 base block, CTA-861 extension (video, audio,
/// speakers, HDMI LLC and HDMI Forum vendor blocks, video capability, colorimetry, HDR static
/// metadata, YCbCr 4:2:0 video and capability-map blocks, detailed timings), DisplayID 1.3 / 2.0
/// extension (Type I / Type VII detailed timings, tiled topology). Read-only and tolerant: a bad
/// checksum or a truncated block is a problem named in the result, never a throw, and whatever
/// parsed stands. The hash is SHA-256 over every byte, so two reads of one display agree and a
/// display swapped for its twin does not.
/// </summary>
public static class Edid
{
    public const int BlockSize = 128;

    private static readonly byte[] Header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    /// <summary>SHA-256 of the bytes as upper-case hex; "" for nothing.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => bytes.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Whether a 128-byte block's bytes sum to zero, as every EDID block must.</summary>
    public static bool ChecksumValid(ReadOnlySpan<byte> block)
    {
        if (block.Length < BlockSize) return false;
        var sum = 0;
        for (var i = 0; i < BlockSize; i++) sum += block[i];
        return (sum & 0xFF) == 0;
    }

    /// <summary>The byte that makes a block sum to zero.</summary>
    public static byte ChecksumFor(ReadOnlySpan<byte> first127)
    {
        var sum = 0;
        for (var i = 0; i < Math.Min(127, first127.Length); i++) sum += first127[i];
        return (byte)((256 - (sum & 0xFF)) & 0xFF);
    }

    /// <summary>Whether the bytes start as an EDID does.</summary>
    public static bool LooksLikeEdid(ReadOnlySpan<byte> bytes) => bytes.Length >= BlockSize && bytes[..8].SequenceEqual(Header);

    public static EdidInfo Parse(byte[]? bytes)
    {
        bytes ??= Array.Empty<byte>();
        var problems = new List<string>();
        var hash = Hash(bytes);
        if (bytes.Length < BlockSize)
        {
            problems.Add(bytes.Length == 0 ? "no EDID" : $"only {bytes.Length} bytes — a base block is 128");
            return Empty(problems, hash, bytes.Length);
        }
        var b = bytes.AsSpan(0, BlockSize);
        if (!b[..8].SequenceEqual(Header)) problems.Add("the header is not an EDID's (00 FF FF FF FF FF FF 00)");

        // Identity.
        var id = (b[8] << 8) | b[9];
        var manufacturer = new string(new[] { (char)('A' - 1 + ((id >> 10) & 0x1F)), (char)('A' - 1 + ((id >> 5) & 0x1F)), (char)('A' - 1 + (id & 0x1F)) });
        if (manufacturer.Any(c => c < 'A' || c > 'Z')) manufacturer = "???";
        var product = b[10] | (b[11] << 8);
        var serial = (uint)(b[12] | (b[13] << 8) | (b[14] << 16) | (b[15] << 24));
        var week = b[16];
        var year = b[17] + 1990;
        var version = $"{b[18]}.{b[19]}";

        // Video input and the basic parameters.
        var digital = (b[20] & 0x80) != 0;
        var depth = 0;
        var iface = "";
        if (digital && b[18] == 1 && b[19] >= 4)
        {
            depth = ((b[20] >> 4) & 0x7) switch { 1 => 6, 2 => 8, 3 => 10, 4 => 12, 5 => 14, 6 => 16, _ => 0 };
            iface = (b[20] & 0xF) switch { 1 => "DVI", 2 => "HDMI-a", 3 => "HDMI-b", 4 => "MDDI", 5 => "DisplayPort", _ => "" };
        }
        else if (!digital)
        {
            iface = "analogue";
        }
        var widthCm = b[21];
        var heightCm = b[22];
        var gamma = b[23] == 0xFF ? 0 : (b[23] + 100) / 100.0;
        var features = b[24];
        var encodings = new List<string>();
        if (digital)
        {
            encodings.Add("RGB 4:4:4");
            if ((features & 0x08) != 0) encodings.Add("YCbCr 4:4:4");
            if ((features & 0x10) != 0) encodings.Add("YCbCr 4:2:2");
        }
        var preferredFlag = (features & 0x02) != 0 || (b[18] == 1 && b[19] >= 4);

        // Established timings.
        var established = new List<string>();
        string[] est35 = { "800×600@60", "800×600@56", "640×480@75", "640×480@72", "640×480@67", "640×480@60", "720×400@88", "720×400@70" };
        string[] est36 = { "1280×1024@75", "1024×768@75", "1024×768@70", "1024×768@60", "1024×768i@87", "832×624@75", "800×600@75", "800×600@72" };
        for (var i = 0; i < 8; i++) if ((b[35] & (1 << i)) != 0) established.Add(est35[i]);
        for (var i = 0; i < 8; i++) if ((b[36] & (1 << i)) != 0) established.Add(est36[i]);
        if ((b[37] & 0x80) != 0) established.Add("1152×870@75");

        // Standard timings.
        var standard = new List<EdidStandardTiming>();
        for (var i = 38; i < 54; i += 2) AddStandard(standard, b[i], b[i + 1], version);

        // The four descriptors.
        var detailed = new List<EdidTiming>();
        var name = "";
        var serialText = "";
        for (var i = 54; i <= 108; i += 18)
        {
            var d = b.Slice(i, 18);
            if (d[0] != 0 || d[1] != 0)
            {
                var timing = ParseDtd(d, detailed.Count == 0 && preferredFlag, "base");
                if (timing is not null) detailed.Add(timing);
                continue;
            }
            switch (d[3])
            {
                case 0xFC: name = DescriptorText(d); break;
                case 0xFF: serialText = DescriptorText(d); break;
                case 0xFA:
                    for (var j = 5; j < 17; j += 2) AddStandard(standard, d[j], d[j + 1], version);
                    break;
            }
        }

        var extensionCount = b[126];
        var checksums = new List<bool> { ChecksumValid(b) };
        if (!checksums[0]) problems.Add("the base block's checksum is wrong");
        var blocks = new List<string> { "base" };
        CtaInfo? cta = null;
        DisplayIdInfo? displayId = null;
        var available = (bytes.Length / BlockSize) - 1;
        if (available < extensionCount) problems.Add($"the base block says {extensionCount} extension{(extensionCount == 1 ? "" : "s")}, {available} present");
        for (var n = 1; n <= Math.Min(extensionCount, available); n++)
        {
            var block = bytes.AsSpan(n * BlockSize, BlockSize);
            var ok = ChecksumValid(block);
            checksums.Add(ok);
            if (!ok) problems.Add($"extension {n}'s checksum is wrong");
            switch (block[0])
            {
                case 0x02:
                    blocks.Add("CTA-861");
                    cta = ParseCta(block, problems);
                    break;
                case 0x70:
                    blocks.Add("DisplayID");
                    displayId = ParseDisplayId(block, problems);
                    break;
                case 0xF0: blocks.Add("block map"); break;
                case 0x10: blocks.Add("VTB"); break;
                case 0x40: blocks.Add("DI"); break;
                case 0x50: blocks.Add("LS"); break;
                case 0x60: blocks.Add("DPVL"); break;
                default: blocks.Add($"tag 0x{block[0]:X2}"); break;
            }
        }

        return new EdidInfo(manufacturer, product, serial, serialText, name, week, year, version, digital, iface, depth, widthCm, heightCm, gamma,
            encodings, established, standard, detailed, extensionCount, blocks, checksums, cta, displayId, problems, hash, bytes.Length);
    }

    private static EdidInfo Empty(List<string> problems, string hash, int length)
        => new("???", 0, 0, "", "", 0, 0, "", false, "", 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<EdidStandardTiming>(),
            Array.Empty<EdidTiming>(), 0, Array.Empty<string>(), Array.Empty<bool>(), null, null, problems, hash, length);

    private static void AddStandard(List<EdidStandardTiming> into, byte b0, byte b1, string version)
    {
        if (b0 == 0x01 && b1 == 0x01) return;   // unused
        if (b0 == 0 && b1 == 0) return;
        var width = (b0 + 31) * 8;
        var aspect = (b1 >> 6) & 0x3;
        var height = aspect switch
        {
            0 => version is "1.0" or "1.1" or "1.2" ? width : width * 10 / 16,
            1 => width * 3 / 4,
            2 => width * 4 / 5,
            _ => width * 9 / 16,
        };
        into.Add(new EdidStandardTiming(width, height, (b1 & 0x3F) + 60));
    }

    private static string DescriptorText(ReadOnlySpan<byte> d)
    {
        var sb = new StringBuilder();
        for (var i = 5; i < 18; i++)
        {
            if (d[i] == 0x0A) break;
            if (d[i] >= 0x20 && d[i] < 0x7F) sb.Append((char)d[i]);
        }
        return sb.ToString().Trim();
    }

    /// <summary>An 18-byte detailed timing descriptor; null when its clock is zero.</summary>
    public static EdidTiming? ParseDtd(ReadOnlySpan<byte> d, bool preferred, string source)
    {
        if (d.Length < 18) return null;
        var clock10kHz = d[0] | (d[1] << 8);
        if (clock10kHz == 0) return null;
        var hActive = d[2] | ((d[4] >> 4) << 8);
        var hBlank = d[3] | ((d[4] & 0xF) << 8);
        var vActive = d[5] | ((d[7] >> 4) << 8);
        var vBlank = d[6] | ((d[7] & 0xF) << 8);
        var interlaced = (d[17] & 0x80) != 0;
        return new EdidTiming(clock10kHz * 10, hActive, hBlank, vActive, vBlank, interlaced, preferred, source);
    }

    // ---- CTA-861 ---------------------------------------------------------------------------

    private static CtaInfo ParseCta(ReadOnlySpan<byte> block, List<string> problems)
    {
        var revision = block[1];
        var dtdOffset = block[2];
        var flags = block[3];
        var underscan = (flags & 0x80) != 0;
        var basicAudio = (flags & 0x40) != 0;
        var ycc444 = (flags & 0x20) != 0;
        var ycc422 = (flags & 0x10) != 0;
        var nativeCount = flags & 0x0F;

        var video = new List<EdidVideoFormat>();
        var audio = new List<EdidAudio>();
        var speakers = new List<string>();
        var colorimetry = new List<string>();
        var names = new List<string>();
        HdmiInfo? hdmi = null;
        HdmiForumInfo? forum = null;
        HdrInfo? hdr = null;
        var qsRgb = false;
        var qsYcc = false;
        var ycc420Only = new List<int>();
        byte[]? ycc420Map = null;
        var ycc420All = false;

        if (revision >= 3 && dtdOffset >= 4)
        {
            var i = 4;
            var end = Math.Min((int)dtdOffset, BlockSize - 1);
            while (i < end)
            {
                var tag = block[i] >> 5;
                var len = block[i] & 0x1F;
                if (i + 1 + len > end)
                {
                    problems.Add("a CTA data block runs past the descriptors");
                    break;
                }
                var p = block.Slice(i + 1, len);
                switch (tag)
                {
                    case 1:
                        names.Add("audio");
                        for (var j = 0; j + 2 < len; j += 3) audio.Add(ParseSad(p.Slice(j, 3)));
                        break;
                    case 2:
                        names.Add("video");
                        for (var j = 0; j < len; j++)
                        {
                            var v = p[j];
                            var native = v is >= 129 and <= 192;          // VICs 1–64 carry their native flag in bit 7
                            var vic = native ? v - 128 : v;
                            if (vic == 0) continue;
                            var (w, h, rate, interlaced) = Vic(vic);
                            video.Add(new EdidVideoFormat(vic, w, h, rate, interlaced, native, false, false));
                        }
                        break;
                    case 3:
                        if (len >= 3)
                        {
                            var oui = p[0] | (p[1] << 8) | (p[2] << 16);
                            if (oui == 0x000C03 && len >= 5)
                            {
                                names.Add("HDMI");
                                var f = len > 5 ? p[5] : (byte)0;
                                hdmi = new HdmiInfo($"{p[3] >> 4}.{p[3] & 0xF}.{p[4] >> 4}.{p[4] & 0xF}", (f & 0x10) != 0, (f & 0x20) != 0, (f & 0x40) != 0, (f & 0x08) != 0, len > 6 ? p[6] * 5 : 0);
                            }
                            else if (oui == 0xC45DD8 && len >= 7)
                            {
                                names.Add("HDMI Forum");
                                forum = new HdmiForumInfo(p[4] * 5, (p[5] & 0x80) != 0, (p[6] & 0x01) != 0, (p[6] & 0x02) != 0, (p[6] & 0x04) != 0);
                            }
                            else
                            {
                                names.Add($"vendor {oui:X6}");
                            }
                        }
                        break;
                    case 4:
                        names.Add("speakers");
                        if (len >= 1)
                        {
                            string[] spk = { "FL/FR", "LFE", "FC", "RL/RR", "RC", "FLC/FRC", "RLC/RRC", "FLW/FRW" };
                            for (var bit = 0; bit < 8; bit++) if ((p[0] & (1 << bit)) != 0) speakers.Add(spk[bit]);
                        }
                        break;
                    case 7:
                        if (len >= 1)
                        {
                            switch (p[0])
                            {
                                case 0:
                                    names.Add("video capability");
                                    if (len >= 2)
                                    {
                                        qsRgb = (p[1] & 0x40) != 0;
                                        qsYcc = (p[1] & 0x80) != 0;
                                    }
                                    break;
                                case 5:
                                    names.Add("colorimetry");
                                    if (len >= 2)
                                    {
                                        string[] col = { "xvYCC601", "xvYCC709", "sYCC601", "opYCC601", "opRGB", "BT.2020 cYCC", "BT.2020 YCC", "BT.2020 RGB" };
                                        for (var bit = 0; bit < 8; bit++) if ((p[1] & (1 << bit)) != 0) colorimetry.Add(col[bit]);
                                        if (len >= 3 && (p[2] & 0x80) != 0) colorimetry.Add("DCI-P3");
                                    }
                                    break;
                                case 6:
                                    names.Add("HDR static metadata");
                                    if (len >= 3)
                                    {
                                        // The luminance codes: 50 × 2^(code / 32) cd/m² for the maxima, the minimum as a fraction of the maximum.
                                        double? max = len > 3 && p[3] != 0 ? 50 * Math.Pow(2, p[3] / 32.0) : null;
                                        double? avg = len > 4 && p[4] != 0 ? 50 * Math.Pow(2, p[4] / 32.0) : null;
                                        double? min = len > 5 && p[5] != 0 && max is { } m ? m * Math.Pow(p[5] / 255.0, 2) / 100 : null;
                                        hdr = new HdrInfo((p[1] & 0x01) != 0, (p[1] & 0x02) != 0, (p[1] & 0x04) != 0, (p[1] & 0x08) != 0, (p[2] & 0x01) != 0, max, avg, min);
                                    }
                                    break;
                                case 7: names.Add("HDR dynamic metadata"); break;
                                case 1: names.Add("vendor video"); break;
                                case 14:
                                    names.Add("YCbCr 4:2:0 video");
                                    for (var j = 1; j < len; j++) if (p[j] != 0) ycc420Only.Add(p[j] is >= 129 and <= 192 ? p[j] - 128 : p[j]);
                                    break;
                                case 15:
                                    names.Add("YCbCr 4:2:0 capability map");
                                    if (len == 1) ycc420All = true;
                                    else ycc420Map = p[1..].ToArray();
                                    break;
                                case 0x78: names.Add("HDMI Forum sink capability"); break;
                                case 0x79: names.Add("HDMI Forum EDID extension override"); break;
                                default: names.Add($"extended {p[0]}"); break;
                            }
                        }
                        break;
                    default:
                        names.Add($"tag {tag}");
                        break;
                }
                i += 1 + len;
            }
        }

        // The 4:2:0 story onto the video formats: the map's bit i is the i-th SVD; the 4:2:0-only VICs are their own formats.
        for (var k = 0; k < video.Count; k++)
        {
            var also = ycc420All || (ycc420Map is not null && k / 8 < ycc420Map.Length && (ycc420Map[k / 8] & (1 << (k % 8))) != 0);
            if (also) video[k] = video[k] with { Ycc420Also = true };
        }
        foreach (var vic in ycc420Only)
        {
            var (w, h, rate, interlaced) = Vic(vic);
            video.Add(new EdidVideoFormat(vic, w, h, rate, interlaced, false, true, false));
        }

        // The detailed timings after the data blocks.
        var timings = new List<EdidTiming>();
        if (dtdOffset >= 4)
        {
            for (var i = dtdOffset; i + 18 <= BlockSize - 1; i += 18)
            {
                var t = ParseDtd(block.Slice(i, 18), false, "CTA");
                if (t is null) break;
                timings.Add(t);
            }
        }

        return new CtaInfo(revision, underscan, basicAudio, ycc444, ycc422, nativeCount, video, audio, speakers, hdmi, forum, colorimetry, hdr, qsRgb, qsYcc, timings, names);
    }

    private static EdidAudio ParseSad(ReadOnlySpan<byte> s)
    {
        var format = (s[0] >> 3) & 0xF;
        var channels = (s[0] & 0x7) + 1;
        var rates = new List<int>();
        int[] rateTable = { 32, 44, 48, 88, 96, 176, 192 };
        for (var bit = 0; bit < 7; bit++) if ((s[1] & (1 << bit)) != 0) rates.Add(rateTable[bit]);
        var depths = new List<int>();
        if (format == 1)
        {
            if ((s[2] & 0x1) != 0) depths.Add(16);
            if ((s[2] & 0x2) != 0) depths.Add(20);
            if ((s[2] & 0x4) != 0) depths.Add(24);
        }
        var name = format switch
        {
            1 => "LPCM", 2 => "AC-3", 3 => "MPEG-1", 4 => "MP3", 5 => "MPEG-2", 6 => "AAC LC", 7 => "DTS", 8 => "ATRAC",
            9 => "One Bit Audio", 10 => "E-AC-3", 11 => "DTS-HD", 12 => "MAT", 13 => "DST", 14 => "WMA Pro", 15 => "extended",
            _ => $"format {format}",
        };
        return new EdidAudio(name, channels, rates, depths);
    }

    /// <summary>What a CTA-861 VIC means: the raster, the nominal rate, interlace. Unknown VICs read as 0×0.</summary>
    public static (int Width, int Height, SignalRate Rate, bool Interlaced) Vic(int vic)
    {
        static (int, int, SignalRate, bool) P(int w, int h, int hz, bool i = false) => (w, h, SignalRate.Of(hz, 1), i);
        return vic switch
        {
            1 => P(640, 480, 60),
            2 or 3 => P(720, 480, 60),
            4 => P(1280, 720, 60),
            5 => P(1920, 1080, 60, true),
            6 or 7 => P(720, 480, 60, true),
            8 or 9 => P(1440, 240, 60),
            10 or 11 => P(2880, 480, 60, true),
            12 or 13 => P(2880, 240, 60),
            14 or 15 => P(1440, 480, 60),
            16 => P(1920, 1080, 60),
            17 or 18 => P(720, 576, 50),
            19 => P(1280, 720, 50),
            20 => P(1920, 1080, 50, true),
            21 or 22 => P(720, 576, 50, true),
            23 or 24 => P(1440, 288, 50),
            25 or 26 => P(2880, 576, 50, true),
            27 or 28 => P(2880, 288, 50),
            29 or 30 => P(1440, 576, 50),
            31 => P(1920, 1080, 50),
            32 => P(1920, 1080, 24),
            33 => P(1920, 1080, 25),
            34 => P(1920, 1080, 30),
            35 or 36 => P(2880, 480, 60),
            37 or 38 => P(2880, 576, 50),
            39 => P(1920, 1080, 50, true),
            40 => P(1920, 1080, 100, true),
            41 => P(1280, 720, 100),
            42 or 43 => P(720, 576, 100),
            44 or 45 => P(720, 576, 100, true),
            46 => P(1920, 1080, 120, true),
            47 => P(1280, 720, 120),
            48 or 49 => P(720, 480, 120),
            50 or 51 => P(720, 480, 120, true),
            52 or 53 => P(720, 576, 200),
            54 or 55 => P(720, 576, 200, true),
            56 or 57 => P(720, 480, 240),
            58 or 59 => P(720, 480, 240, true),
            60 => P(1280, 720, 24),
            61 => P(1280, 720, 25),
            62 => P(1280, 720, 30),
            63 => P(1920, 1080, 120),
            64 => P(1920, 1080, 100),
            65 => P(1280, 720, 24),
            66 => P(1280, 720, 25),
            67 => P(1280, 720, 30),
            68 => P(1280, 720, 50),
            69 => P(1280, 720, 60),
            70 => P(1280, 720, 100),
            71 => P(1280, 720, 120),
            72 => P(1920, 1080, 24),
            73 => P(1920, 1080, 25),
            74 => P(1920, 1080, 30),
            75 => P(1920, 1080, 50),
            76 => P(1920, 1080, 60),
            77 => P(1920, 1080, 100),
            78 => P(1920, 1080, 120),
            79 => P(1680, 720, 24),
            80 => P(1680, 720, 25),
            81 => P(1680, 720, 30),
            82 => P(1680, 720, 50),
            83 => P(1680, 720, 60),
            84 => P(1680, 720, 100),
            85 => P(1680, 720, 120),
            86 => P(2560, 1080, 24),
            87 => P(2560, 1080, 25),
            88 => P(2560, 1080, 30),
            89 => P(2560, 1080, 50),
            90 => P(2560, 1080, 60),
            91 => P(2560, 1080, 100),
            92 => P(2560, 1080, 120),
            93 => P(3840, 2160, 24),
            94 => P(3840, 2160, 25),
            95 => P(3840, 2160, 30),
            96 => P(3840, 2160, 50),
            97 => P(3840, 2160, 60),
            98 => P(4096, 2160, 24),
            99 => P(4096, 2160, 25),
            100 => P(4096, 2160, 30),
            101 => P(4096, 2160, 50),
            102 => P(4096, 2160, 60),
            103 => P(3840, 2160, 24),
            104 => P(3840, 2160, 25),
            105 => P(3840, 2160, 30),
            106 => P(3840, 2160, 50),
            107 => P(3840, 2160, 60),
            108 => P(1280, 720, 48),
            109 => P(1280, 720, 48),
            110 => P(1680, 720, 48),
            111 => P(1920, 1080, 48),
            112 => P(1920, 1080, 48),
            113 => P(2560, 1080, 48),
            114 => P(3840, 2160, 48),
            115 => P(4096, 2160, 48),
            116 => P(3840, 2160, 48),
            117 => P(3840, 2160, 100),
            118 => P(3840, 2160, 120),
            119 => P(3840, 2160, 100),
            120 => P(3840, 2160, 120),
            121 => P(5120, 2160, 24),
            122 => P(5120, 2160, 25),
            123 => P(5120, 2160, 30),
            124 => P(5120, 2160, 48),
            125 => P(5120, 2160, 50),
            126 => P(5120, 2160, 60),
            127 => P(5120, 2160, 100),
            193 => P(5120, 2160, 120),
            194 => P(7680, 4320, 24),
            195 => P(7680, 4320, 25),
            196 => P(7680, 4320, 30),
            197 => P(7680, 4320, 48),
            198 => P(7680, 4320, 50),
            199 => P(7680, 4320, 60),
            200 => P(7680, 4320, 100),
            201 => P(7680, 4320, 120),
            202 => P(7680, 4320, 24),
            203 => P(7680, 4320, 25),
            204 => P(7680, 4320, 30),
            205 => P(7680, 4320, 48),
            206 => P(7680, 4320, 50),
            207 => P(7680, 4320, 60),
            208 => P(7680, 4320, 100),
            209 => P(7680, 4320, 120),
            210 => P(10240, 4320, 24),
            211 => P(10240, 4320, 25),
            212 => P(10240, 4320, 30),
            213 => P(10240, 4320, 48),
            214 => P(10240, 4320, 50),
            215 => P(10240, 4320, 60),
            216 => P(10240, 4320, 100),
            217 => P(10240, 4320, 120),
            218 => P(4096, 2160, 100),
            219 => P(4096, 2160, 120),
            _ => (0, 0, SignalRate.None, false),
        };
    }

    // ---- DisplayID -------------------------------------------------------------------------

    private static DisplayIdInfo ParseDisplayId(ReadOnlySpan<byte> block, List<string> problems)
    {
        // As an EDID extension the DisplayID structure starts after the 0x70 tag: version, section length, product type, extension count, then the blocks.
        var versionByte = block[1];
        var version = versionByte >= 0x20 ? $"2.{versionByte & 0xF}" : $"1.{versionByte & 0xF}";
        var length = block[2];
        var productType = versionByte >= 0x20
            ? (block[3] switch { 0 => "extension", 1 => "test", 2 => "generic display", 3 => "television", 4 => "desktop monitor", 5 => "presentation display", 6 => "VR", 7 => "AR", _ => $"type {block[3]}" })
            : (block[3] switch { 0 => "extension", 1 => "test", 2 => "generic display", 3 => "television", 4 => "desktop monitor", 5 => "notebook", 6 => "mobile", 7 => "signage", _ => $"type {block[3]}" });
        var timings = new List<EdidTiming>();
        var names = new List<string>();
        TiledTopology? tiled = null;
        var i = 5;
        var end = Math.Min(5 + length, BlockSize - 1);
        while (i + 3 <= end)
        {
            var tag = block[i];
            var len = block[i + 2];
            if (tag == 0 && len == 0) break;
            if (i + 3 + len > end)
            {
                problems.Add("a DisplayID block runs past its section");
                break;
            }
            var p = block.Slice(i + 3, len);
            var v2 = versionByte >= 0x20;
            if ((v2 && tag == 0x22) || (!v2 && tag == 0x03))
            {
                names.Add(v2 ? "Type VII timings" : "Type I timings");
                for (var j = 0; j + 20 <= len; j += 20)
                {
                    var t = ParseDisplayIdTiming(p.Slice(j, 20), v2);
                    if (t is not null) timings.Add(t);
                }
            }
            else if ((v2 && tag == 0x28) || (!v2 && tag == 0x12))
            {
                names.Add("tiled topology");
                if (len >= 8)
                {
                    var single = (p[0] & 0x01) != 0;
                    var totalH = (p[1] & 0x0F) | ((p[3] & 0x03) << 4);
                    var totalV = ((p[1] >> 4) & 0x0F) | (((p[3] >> 2) & 0x03) << 4);
                    var locH = (p[2] & 0x0F) | (((p[3] >> 4) & 0x03) << 4);
                    var locV = ((p[2] >> 4) & 0x0F) | (((p[3] >> 6) & 0x03) << 4);
                    var tileW = (p[4] | (p[5] << 8)) + 1;
                    var tileH = (p[6] | (p[7] << 8)) + 1;
                    tiled = new TiledTopology(totalH + 1, totalV + 1, locH + 1, locV + 1, tileW, tileH, single);
                }
            }
            else
            {
                names.Add(v2
                    ? tag switch { 0x20 => "product identification", 0x21 => "display parameters", 0x23 => "Type VIII timings", 0x24 => "Type IX timings", 0x25 => "dynamic video timing range", 0x26 => "interface features", 0x27 => "stereo", 0x29 => "ContainerID", 0x2A => "Type X timings", 0x7E => "vendor", _ => $"block 0x{tag:X2}" }
                    : tag switch { 0x00 => "product identification", 0x01 => "display parameters", 0x02 => "color characteristics", 0x04 => "Type II timings", 0x05 => "Type III timings", 0x06 => "Type IV timings", 0x07 => "VESA timings", 0x08 => "CEA timings", 0x09 => "video timing range", 0x0A => "product serial", 0x0B => "general purpose string", 0x0C => "display device data", 0x0D => "interface power sequencing", 0x0E => "transfer characteristics", 0x0F => "display interface", 0x10 => "stereo", 0x11 => "Type V timings", 0x13 => "Type VI timings", 0x7F => "vendor", _ => $"block 0x{tag:X2}" });
            }
            i += 3 + len;
        }
        return new DisplayIdInfo(version, productType, timings, tiled, names);
    }

    /// <summary>A 20-byte DisplayID Type I (1.x, 10 kHz clock units) or Type VII (2.x, 1 kHz units) detailed timing.</summary>
    public static EdidTiming? ParseDisplayIdTiming(ReadOnlySpan<byte> t, bool version2)
    {
        if (t.Length < 20) return null;
        var clockUnits = (t[0] | (t[1] << 8) | (t[2] << 16)) + 1;
        var clockKHz = version2 ? clockUnits : clockUnits * 10;
        var preferred = (t[3] & 0x80) != 0;
        var interlaced = (t[3] & 0x10) != 0;
        var hActive = (t[4] | (t[5] << 8)) + 1;
        var hBlank = (t[6] | (t[7] << 8)) + 1;
        var vActive = (t[12] | (t[13] << 8)) + 1;
        var vBlank = (t[14] | (t[15] << 8)) + 1;
        return new EdidTiming(clockKHz, hActive, hBlank, vActive, vBlank, interlaced, preferred, version2 ? "DisplayID 2" : "DisplayID 1");
    }
}
