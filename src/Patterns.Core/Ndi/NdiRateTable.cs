namespace Patterns.Core.Ndi;

/// <summary>Frame-rate presets keyed by the label stored in settings.</summary>
public static class NdiRateTable
{
    /// <summary>The key that follows the show's master frame rate (60 when the master is unlimited).</summary>
    public const string MasterKey = "master";

    public static readonly string[] Keys = { MasterKey, "23.98", "24", "25", "29.97", "30", "50", "59.94", "60" };

    public static (int N, int D) Resolve(string? key, int masterFps = 0) => key switch
    {
        MasterKey => masterFps > 0 ? (masterFps * 1000, 1000) : (60000, 1000),
        "23.98" => (24000, 1001),
        "24" => (24000, 1000),
        "25" => (25000, 1000),
        "29.97" => (30000, 1001),
        "30" => (30000, 1000),
        "50" => (50000, 1000),
        "59.94" => (60000, 1001),
        _ => (60000, 1000),
    };
}
