using System.Diagnostics;

namespace Patterns.Core.Services;

/// <summary>
/// One monotonic clock shared by every sink so animations stay phase-locked across
/// preview, all output windows and NDI.
/// </summary>
public static class ShowClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    public static double Seconds => Watch.Elapsed.TotalSeconds;
}
