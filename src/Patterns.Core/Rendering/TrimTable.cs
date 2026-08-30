using Patterns.Core.Model;

namespace Patterns.Core.Rendering;

/// <summary>
/// Per-output colour trims as 256-entry lookup tables (applied via an SKColorFilter layer):
/// v' = ((v/255)^gamma) · channelGain · brightness. Pure and unit tested.
/// </summary>
public static class TrimTable
{
    public static byte[] Build(double brightnessPct, double gamma, double channelGainPct)
    {
        var table = new byte[256];
        var brightness = brightnessPct / 100.0;
        var gain = channelGainPct / 100.0;
        var g = Math.Clamp(gamma, 0.1, 5.0);
        for (var i = 0; i < 256; i++)
        {
            var v = Math.Pow(i / 255.0, g) * gain * brightness;
            table[i] = (byte)Math.Clamp((int)Math.Round(v * 255.0), 0, 255);
        }
        return table;
    }

    public static (byte[] R, byte[] G, byte[] B) BuildRgb(ScreenPlacement p) => (
        Build(p.BrightnessPct, p.Gamma, p.TrimRPct),
        Build(p.BrightnessPct, p.Gamma, p.TrimGPct),
        Build(p.BrightnessPct, p.Gamma, p.TrimBPct));

    /// <summary>A single value describing the trim configuration, used as a cache key.</summary>
    public static string KeyOf(ScreenPlacement p)
        => $"{p.BrightnessPct:0.##}|{p.Gamma:0.###}|{p.TrimRPct:0.##}|{p.TrimGPct:0.##}|{p.TrimBPct:0.##}";
}
