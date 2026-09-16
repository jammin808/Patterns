using System.Globalization;
using System.Text.RegularExpressions;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>What a box said its input receives (round 65.11): the device, the screen its input carries, the words, the raw answer, when.</summary>
public sealed record InputStatusReport(string Device, string Screen, SignalContract Received, string Raw, DateTime AtUtc);

/// <summary>
/// The input-status adapter (round 65.11): a processor, a scaler or a projector asked what its input
/// receives, its answer read through a pattern into the contract's own words — the third witness
/// beside the engineer's contract and Windows' observation. The words that ask and the pattern that
/// reads come from the device (typed by the engineer for a box with its own API) or from the profile
/// where the protocol is published (PJLink class 2's IRES ?); a box with neither is asked nothing,
/// and the engineer's own reading of its panel (SCREEN n RECEIVED …) stands in.
/// </summary>
public static class InputStatus
{
    public static readonly TimeSpan DefaultEvery = TimeSpan.FromSeconds(30);

    /// <summary>The words that ask — the device's own, else the profile's; null when there is nothing to ask, the screen is unset, or the device says "-".</summary>
    public static string? QueryFor(DeviceConfig d, ProfileSession session)
    {
        if (d.InputScreen.Length == 0) return null;
        var own = d.InputQuery.Trim();
        if (own == "-") return null;
        return own.Length > 0 ? own : session.InputQuery;
    }

    /// <summary>The pattern that reads the answer — the device's own, else the profile's; null when neither has one.</summary>
    public static string? PatternFor(DeviceConfig d, ProfileSession session)
    {
        var own = d.InputPattern.Trim();
        return own.Length > 0 ? own : session.InputPattern;
    }

    public static TimeSpan EveryFor(DeviceConfig d) => d.InputEverySeconds > 0 ? TimeSpan.FromSeconds(d.InputEverySeconds) : DefaultEvery;

    /// <summary>
    /// Reads a box's answer through the pattern: the groups w and h make the raster, hz the rate (50,
    /// 59.94, 60000/1001), enc the encoding (RGB, 444, 422, 420, YCbCr…), bits the depth. True when the
    /// pattern matched and at least the raster or the rate was read; the problem names a pattern that
    /// does not compile.
    /// </summary>
    public static bool TryRead(string pattern, string text, out SignalContract received, out string problem)
    {
        received = new SignalContract();
        problem = "";
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException ex)
        {
            problem = $"the input pattern does not compile: {ex.Message}";
            return false;
        }
        Match m;
        try
        {
            m = regex.Match(text ?? "");
        }
        catch (RegexMatchTimeoutException)
        {
            problem = "the input pattern took too long on the answer";
            return false;
        }
        if (!m.Success) return false;
        var any = false;
        if (m.Groups["w"].Success && m.Groups["h"].Success
            && int.TryParse(m.Groups["w"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(m.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0)
        {
            received.Width = w;
            received.Height = h;
            any = true;
        }
        if (m.Groups["hz"].Success)
        {
            var rate = SignalRate.Parse(m.Groups["hz"].Value.Trim());
            if (rate.IsSet)
            {
                received.Rate = rate;
                any = true;
            }
        }
        if (m.Groups["enc"].Success)
        {
            var draft = new SignalContract();
            if (SignalWords.Apply(m.Groups["enc"].Value.Trim().Replace(':', ' ').Replace(" ", ""), draft).Length == 0 && draft.Encoding != PixelEncoding.Any)
            {
                received.Encoding = draft.Encoding;
                any = true;
            }
        }
        if (m.Groups["bits"].Success && int.TryParse(m.Groups["bits"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) && bits is 8 or 10 or 12 or 16)
        {
            received.BitDepth = bits;
            any = true;
        }
        return any;
    }
}
