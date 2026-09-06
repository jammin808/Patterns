using System.Runtime.InteropServices;

namespace Patterns.App.Services;

/// <summary>
/// Windows only: asks the scheduler for a millisecond timer while this process lives. Without the
/// request a sleep of a millisecond is the 15.6 ms tick — every paced loop in the desk (the NDI
/// senders, the stream renderer) would step on that grid, and a host waiting on the frame ring would
/// wake a tick late. Every media player asks for the same; on current Windows the request is this
/// process's alone, and it ends with the process.
/// </summary>
internal static class TimerResolution
{
    private static bool _raised;

    public static void Raise()
    {
        if (!OperatingSystem.IsWindows() || _raised) return;
        try
        {
            _raised = timeBeginPeriod(1) == 0;
        }
        catch (Exception)
        {
            // No winmm on this system: the tick stays what it is.
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);
}
