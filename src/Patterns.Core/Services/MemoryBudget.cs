namespace Patterns.Core.Services;

/// <summary>The ceilings a machine of this size gets: the app's working set, the picture cache, the decoder pool, the fade hold.</summary>
public sealed record MemoryCeilings(double TotalMB, double AppCeilingMB, int ImageCachePictures, int DecoderCap, int HeldFrameMs);

/// <summary>
/// Memory ceilings in numbers. The app's memory is bounded by design — a picture cache of a fixed
/// count, a decoder pool of a fixed size, decoded frames held for a fade and no longer, thumbnails
/// at one size — but the operator could not see the numbers, and a working set that climbs past
/// what the design allows is the one memory signal that matters on a long day. The ceiling for
/// the app is a quarter of the machine, never under 512 MB and never over 3 GB; past it the row
/// goes amber, past a quarter more it goes red, and the advice says what a climb means. Pure.
/// </summary>
public static class MemoryBudget
{
    public const double AppShareOfRam = 0.25;
    public const double AppFloorMB = 512;
    public const double AppCapMB = 3072;

    /// <summary>How long a retired decoded frame is held for a crossfade before it is freed.</summary>
    public const int HeldFrameMs = 400;

    public static MemoryCeilings For(double totalMB, int imageCachePictures, int decoderCap)
    {
        var app = totalMB > 0 ? Math.Clamp(totalMB * AppShareOfRam, AppFloorMB, AppCapMB) : AppCapMB;
        return new MemoryCeilings(totalMB, app, imageCachePictures, decoderCap, HeldFrameMs);
    }

    public static CheckLight Light(double appMB, MemoryCeilings c)
    {
        if (appMB < 0) return CheckLight.Grey;
        if (appMB > c.AppCeilingMB * 1.25) return CheckLight.Red;
        if (appMB > c.AppCeilingMB) return CheckLight.Amber;
        return CheckLight.Green;
    }

    /// <summary>"This app 412 MB of a 1.5 GB ceiling (16 GB machine) · pictures 3 of 10 cached · decoders 2 of 4 · 0 frames held for fades".</summary>
    public static string Describe(double appMB, MemoryCeilings c, int imagesCached, int decoders, int heldFrames)
    {
        var machine = c.TotalMB > 0 ? $" ({c.TotalMB / 1024:0.#} GB machine)" : "";
        var app = appMB >= 0 ? $"This app {Mb(appMB)} of a {Mb(c.AppCeilingMB)} ceiling{machine}" : $"This app: no reading yet · ceiling {Mb(c.AppCeilingMB)}{machine}";
        var parts = new List<string> { app };
        if (imagesCached >= 0) parts.Add($"pictures {imagesCached} of {c.ImageCachePictures} cached");
        if (decoders >= 0) parts.Add($"decoders {decoders} of {c.DecoderCap}");
        parts.Add($"{heldFrames} frame{(heldFrames == 1 ? "" : "s")} held for fades");
        return string.Join(" · ", parts);
    }

    /// <summary>The row's note: "" under the ceiling; what a climb means past it.</summary>
    public static string Advice(double appMB, MemoryCeilings c) => Light(appMB, c) switch
    {
        CheckLight.Red => "far over its ceiling — restart the app at the next break; the memory line above says whether it climbed steadily (a leak) or jumped (a very large picture or deck)",
        CheckLight.Amber => "over its ceiling — a very large picture set or deck, or a climb: the memory line above says which; RESTART APP between sessions clears it",
        _ => "",
    };

    /// <summary>"412 MB" under a gigabyte, "1.5 GB" from there.</summary>
    public static string Mb(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
}
