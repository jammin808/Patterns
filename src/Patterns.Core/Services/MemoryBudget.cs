namespace Patterns.Core.Services;

/// <summary>How much machine this is, for every budget that scales: under 8 GB small, under 32 GB standard, big past that.</summary>
public enum MachineClass
{
    Small,
    Standard,
    Big,
}

/// <summary>The ceilings a machine of this size gets: the app's working set, the picture cache (count and bytes), the decoder pool, a frame pool's bytes per source, the GPU resource cache.</summary>
public sealed record MemoryCeilings(double TotalMB, double AppCeilingMB, int ImageCachePictures, int DecoderCap,
                                    long PictureCacheBytes = 0, long FramePoolBytesPerSource = 0, long GpuCacheBytes = 0, MachineClass Class = MachineClass.Standard);

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

    private const long MB = 1024L * 1024;

    /// <summary>
    /// The machine's memory in megabytes as the runtime sees it, read once at the start (the
    /// tests and the nodes may set it): every budget that scales with the machine reads this
    /// before the first metrics sample exists.
    /// </summary>
    public static double MachineMB { get; set; } = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);

    /// <summary>The class of a machine with this much memory; no reading is standard.</summary>
    public static MachineClass ClassOf(double totalMB) => totalMB <= 0 ? MachineClass.Standard : totalMB < 8 * 1024 ? MachineClass.Small : totalMB < 32 * 1024 ? MachineClass.Standard : MachineClass.Big;

    /// <summary>The decoded pictures' budget: 128 MB on a small machine, 256 standard, 512 big — a 4K picture is thirty-odd megabytes.</summary>
    public static long PictureCacheBytes(double totalMB) => ClassOf(totalMB) switch { MachineClass.Small => 128 * MB, MachineClass.Big => 512 * MB, _ => 256 * MB };

    /// <summary>A live source's frame pool: 48 MB small, 64 standard, 96 big — eight 1080p frames on a standard machine, four at 4K (the floor).</summary>
    public static long FramePoolBytesPerSource(double totalMB) => ClassOf(totalMB) switch { MachineClass.Small => 48 * MB, MachineClass.Big => 96 * MB, _ => 64 * MB };

    /// <summary>Skia's GPU resource cache (textures, the compositor's surfaces): 64 MB small, 128 standard, 256 big.</summary>
    public static long GpuCacheBytes(double totalMB) => ClassOf(totalMB) switch { MachineClass.Small => 64 * MB, MachineClass.Big => 256 * MB, _ => 128 * MB };

    public static MemoryCeilings For(double totalMB, int imageCachePictures, int decoderCap)
    {
        var app = totalMB > 0 ? Math.Clamp(totalMB * AppShareOfRam, AppFloorMB, AppCapMB) : AppCapMB;
        return new MemoryCeilings(totalMB, app, imageCachePictures, decoderCap,
            PictureCacheBytes(totalMB), FramePoolBytesPerSource(totalMB), GpuCacheBytes(totalMB), ClassOf(totalMB));
    }

    public static CheckLight Light(double appMB, MemoryCeilings c)
    {
        if (appMB < 0) return CheckLight.Grey;
        if (appMB > c.AppCeilingMB * 1.25) return CheckLight.Red;
        if (appMB > c.AppCeilingMB) return CheckLight.Amber;
        return CheckLight.Green;
    }

    /// <summary>
    /// "This app 412 MB of a 1.5 GB ceiling (16 GB machine) · pictures 3 of 10 cached · decoders 2 of 4 · 0 frames retiring"
    /// — and with the bytes known, "pictures 3 of 32 cached (84 MB of 256 MB)", "frame pools 116 MB (2 sources) + 64 MB
    /// retiring" and "8 MB retiring behind the fence (3 frames)".
    /// </summary>
    public static string Describe(double appMB, MemoryCeilings c, int imagesCached, int decoders, int heldFrames,
                                  long pictureBytes = -1, long framePoolBytes = -1, int framePools = 0,
                                  long retiringPoolBytes = 0, long retiringFrameBytes = -1)
    {
        var machine = c.TotalMB > 0 ? $" ({c.TotalMB / 1024:0.#} GB machine)" : "";
        var app = appMB >= 0 ? $"This app {Mb(appMB)} of a {Mb(c.AppCeilingMB)} ceiling{machine}" : $"This app: no reading yet · ceiling {Mb(c.AppCeilingMB)}{machine}";
        var parts = new List<string> { app };
        if (imagesCached >= 0)
        {
            var bytes = pictureBytes >= 0 && c.PictureCacheBytes > 0 ? $" ({Mb(pictureBytes / (1024.0 * 1024.0))} of {Mb(c.PictureCacheBytes / (1024.0 * 1024.0))})" : "";
            parts.Add($"pictures {imagesCached} of {c.ImageCachePictures} cached{bytes}");
        }
        if (decoders >= 0) parts.Add($"decoders {decoders} of {c.DecoderCap}");
        if (framePoolBytes >= 0 && (framePools > 0 || retiringPoolBytes > 0))
        {
            parts.Add($"frame pools {Mb(framePoolBytes / (1024.0 * 1024.0))} ({framePools} source{(framePools == 1 ? "" : "s")})"
                      + (retiringPoolBytes > 0 ? $" + {Mb(retiringPoolBytes / (1024.0 * 1024.0))} retiring" : ""));
        }
        parts.Add(retiringFrameBytes >= 0
            ? $"{Mb(retiringFrameBytes / (1024.0 * 1024.0))} retiring behind the fence ({heldFrames} frame{(heldFrames == 1 ? "" : "s")})"
            : $"{heldFrames} frame{(heldFrames == 1 ? "" : "s")} retiring");
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
