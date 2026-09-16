namespace Patterns.Core.Services;

/// <summary>
/// The GPU cache governor (round 69): Skia's resource cache — the textures a frame uploads, the
/// compositor's surfaces — bounded from the card and the machine's class rather than one number for
/// every machine, and shrunk as either memory presses: the app's media budget (the pressure ladder's
/// rung) or the card's own (what DXGI grants this process, read every second). The split between
/// graphics memory and system memory is by design — pixels live in the pools and the picture cache,
/// the GPU holds what a frame needs — so the governor's job is the bound and the words, never a swap.
/// Pure; the App applies the limit through the sinks' Skia lease and reads the usage back.
/// </summary>
public static class GpuGovernor
{
    private const long MB = 1024L * 1024;

    /// <summary>The cache never takes more than this share of the card's dedicated memory.</summary>
    public const double ShareOfDedicated = 1.0 / 8;

    /// <summary>Under this the cache thrashes: the floor whatever the pressure.</summary>
    public const long FloorBytes = 32 * MB;

    /// <summary>The class's own number: 64 MB small, 128 standard, 256 big — round 57's table.</summary>
    public static long ByClass(MachineClass cls) => cls switch
    {
        MachineClass.Small => 64 * MB,
        MachineClass.Big => 256 * MB,
        _ => 128 * MB,
    };

    /// <summary>The rung the card's memory stands on: what this process uses against the budget the OS grants it; none without a reading.</summary>
    public static MemoryPressure VramPressure(double usedMB, double budgetMB)
        => budgetMB <= 0 || usedMB < 0 ? MemoryPressure.None : MediaMemory.LevelOf((long)(usedMB * MB), (long)(budgetMB * MB));

    /// <summary>The worse of two rungs: the media ladder's and the card's.</summary>
    public static MemoryPressure Worse(MemoryPressure a, MemoryPressure b) => a > b ? a : b;

    /// <summary>
    /// The limit: the class's number, no more than an eighth of the card's dedicated memory when the
    /// card is known, three quarters of that at elevated pressure, half at high, a quarter at critical,
    /// never under the floor.
    /// </summary>
    public static long LimitBytes(MachineClass cls, long dedicatedVramMB, MemoryPressure pressure)
    {
        var limit = ByClass(cls);
        if (dedicatedVramMB > 0) limit = Math.Min(limit, (long)(dedicatedVramMB * MB * ShareOfDedicated));
        limit = pressure switch
        {
            MemoryPressure.None => limit,
            MemoryPressure.Elevated => limit * 3 / 4,
            MemoryPressure.High => limit / 2,
            _ => limit / 4,
        };
        return Math.Max(FloorBytes, limit);
    }

    /// <summary>At high and critical the unlocked resources are purged as well: the cache empties to what the frame in hand holds.</summary>
    public static bool PurgeAt(MemoryPressure pressure) => pressure >= MemoryPressure.High;

    /// <summary>The Eye's line, stable from one tick to the next — the bound and the rung, not the fill: "GPU cache limit 128 MB · rung none" / "GPU cache: no GPU context (software rendering)".</summary>
    public static string EyeWords(bool hasContext, long limitBytes, MemoryPressure rung)
        => !hasContext ? "GPU cache: no GPU context (software rendering)"
         : $"GPU cache limit {MemoryBudget.Mb(limitBytes / (double)MB)} · rung {MediaMemory.Word(rung)}";

    /// <summary>"GPU cache 48 MB of 128 MB (212 resources) · purged 3×" / "GPU cache: no GPU context (software rendering)".</summary>
    public static string Words(bool hasContext, long limitBytes, long usedBytes, int resources, int purges)
    {
        if (!hasContext) return "GPU cache: no GPU context (software rendering)";
        var line = $"GPU cache {MemoryBudget.Mb(usedBytes / (double)MB)} of {MemoryBudget.Mb(limitBytes / (double)MB)} ({resources} resource{(resources == 1 ? "" : "s")})";
        return purges > 0 ? $"{line} · purged {purges}×" : line;
    }
}
