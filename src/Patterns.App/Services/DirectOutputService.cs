using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Direct output, the process side. Before Avalonia starts it reads the saved show and the
/// cards and decides — through the pure <see cref="DirectOutput"/> rules — whether this start
/// asks Windows for the low-latency flip-model swap chain. That is a process-wide choice, so it
/// is made once, from what the outputs asked for when the show was last saved. A fuse file
/// guards the start: written before the swap chain is asked for, removed once the desk is up;
/// a start that never reaches the desk leaves it behind, and the next start composes and says
/// why. Per window, live, it sets the desktop attributes a flipped window wants. Windows-only:
/// everywhere else it composes and says so.
/// </summary>
public static class DirectOutputService
{
    /// <summary>What this process runs with — decided at start, never changed while it runs.</summary>
    public static DirectOutputMode ModeInForce { get; private set; } = DirectOutputMode.Composed;

    /// <summary>The decision made at this start.</summary>
    public static DirectOutputPlan PlanAtStart { get; private set; } = new(DirectOutputMode.Composed, false, "Not decided yet.");

    /// <summary>Test seam: every window prepared, with whether it was asked to be direct.</summary>
    public static Action<Window, bool>? WindowHook { get; set; }

    private static bool _initialized;
    private static bool _isWindows = OperatingSystem.IsWindows();
    private static int _build = OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : 0;
    private static bool _fuseTripped;
    private static bool _armed;
    private static string _fusePath = "";

    /// <summary>Called from Program.Main before Avalonia starts, after the GPU service. Never throws.</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        try
        {
            var store = new SettingsStore();
            ShowState state;
            try
            {
                state = store.Load();
            }
            catch
            {
                state = new ShowState();
            }
            Initialize(store.BaseDirectory, state, GpuService.Adapters, GpuService.RequestedName,
                OperatingSystem.IsWindows(), OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Direct output setup failed — composing.", ex);
        }
    }

    /// <summary>
    /// The decision from explicit facts (the real start, and the tests): reads the fuse, decides,
    /// and arms the fuse when the swap chain is asked for.
    /// </summary>
    public static DirectOutputPlan Initialize(string baseDirectory, ShowState state, IReadOnlyList<GpuAdapterInfo> adapters,
        string activeAdapterName, bool isWindows, int windowsBuild)
    {
        _initialized = true;
        _isWindows = isWindows;
        _build = windowsBuild;
        _fusePath = Path.Combine(baseDirectory, DirectOutput.FuseFileName);
        _fuseTripped = File.Exists(_fusePath);
        PlanAtStart = DirectOutput.Decide(Facts(state, adapters, activeAdapterName));
        ModeInForce = PlanAtStart.Mode;
        _armed = false;
        if (ModeInForce == DirectOutputMode.LowLatencySwapChain)
        {
            try
            {
                File.WriteAllText(_fusePath, DateTime.UtcNow.ToString("O"));
                _armed = true;
            }
            catch (Exception ex)
            {
                Log.Warn("Direct output fuse could not be written.", ex);
            }
        }
        Log.Info($"Direct output: {PlanAtStart.Reason}");
        return PlanAtStart;
    }

    private static DirectOutputFacts Facts(ShowState state, IReadOnlyList<GpuAdapterInfo> adapters, string activeAdapterName)
        => new(_isWindows, _build, Asking(state) > 0, adapters, activeAdapterName, _fuseTripped);

    /// <summary>The outputs that ask: enabled displays — a planned screen or a feed's never opens a window.</summary>
    public static int Asking(ShowState state)
    {
        var n = 0;
        foreach (var p in state.Output.Placements)
        {
            if (p.DirectOutput && p.Enabled && !p.Planned) n++;
        }
        return n;
    }

    /// <summary>What the next start would do with the show as it is now.</summary>
    public static DirectOutputPlan Wanted(ShowState state)
        => DirectOutput.Decide(Facts(state, GpuService.Adapters,
            GpuService.ActiveAdapterName.Length > 0 ? GpuService.ActiveAdapterName : GpuService.RequestedName));

    /// <summary>One output's line for the Screens page.</summary>
    public static string Status(ShowState state, ScreenPlacement placement)
        => DirectOutput.Status(placement.DirectOutput, ModeInForce, Wanted(state));

    /// <summary>The line for the Machine page and the super-check.</summary>
    public static string Summary(ShowState state)
        => DirectOutput.Summary(Asking(state), ModeInForce, Wanted(state));

    /// <summary>Whether a failed start holds direct output off until the operator ticks it again.</summary>
    public static bool FuseTripped => _fuseTripped;

    /// <summary>The desk is up: a start that asked for the swap chain worked, so the fuse comes out.</summary>
    public static void MarkStarted()
    {
        if (!_armed) return;
        _armed = false;
        try
        {
            File.Delete(_fusePath);
        }
        catch (Exception ex)
        {
            Log.Warn("Direct output fuse could not be removed.", ex);
        }
    }

    /// <summary>The operator ticks direct output again after a held-off start: the next start tries again.</summary>
    public static void ClearFuse()
    {
        if (!_fuseTripped) return;
        _fuseTripped = false;
        try
        {
            if (_fusePath.Length > 0) File.Delete(_fusePath);
        }
        catch (Exception ex)
        {
            Log.Warn("Direct output fuse could not be cleared.", ex);
        }
    }

    /// <summary>The composition modes for this start: the swap chain first when in force, then the defaults as fallbacks.</summary>
    public static IReadOnlyList<Win32CompositionMode> CompositionModes()
        => ModeInForce == DirectOutputMode.LowLatencySwapChain
            ? new[]
            {
                Win32CompositionMode.LowLatencyDxgiSwapChain,
                Win32CompositionMode.WinUIComposition,
                Win32CompositionMode.DirectComposition,
                Win32CompositionMode.RedirectionSurface,
            }
            : new[]
            {
                Win32CompositionMode.WinUIComposition,
                Win32CompositionMode.DirectComposition,
                Win32CompositionMode.RedirectionSurface,
            };

    // ---- the window side ----------------------------------------------------------------------

    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmwaTransitionsForceDisabled = 3;
    private const int DwmwaExcludedFromPeek = 12;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmNcrpUseWindowStyle = 0;
    private const int DwmNcrpDisabled = 1;
    private const int DwmwcpDefault = 0;
    private const int DwmwcpDoNotRound = 1;

    /// <summary>
    /// Per output, live: a direct window gets the desktop's transitions turned off (no fade-in
    /// over the room), stays out of peek, draws no non-client frame and keeps square corners —
    /// the things that keep a window on the flip path. An output unticked gets the defaults back.
    /// </summary>
    public static void Prepare(Window window, bool direct)
    {
        WindowHook?.Invoke(window, direct);
        if (!OperatingSystem.IsWindows()) return;
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        try
        {
            SetAttribute(handle, DwmwaTransitionsForceDisabled, direct ? 1 : 0);
            SetAttribute(handle, DwmwaExcludedFromPeek, direct ? 1 : 0);
            SetAttribute(handle, DwmwaNcRenderingPolicy, direct ? DwmNcrpDisabled : DwmNcrpUseWindowStyle);
            SetAttribute(handle, DwmwaWindowCornerPreference, direct ? DwmwcpDoNotRound : DwmwcpDefault); // Windows 11; older builds refuse it, harmlessly
        }
        catch (Exception ex)
        {
            Log.Warn("Direct output window attributes failed.", ex);
        }
    }

    private static void SetAttribute(IntPtr handle, int attribute, int value)
    {
        var v = value;
        _ = DwmSetWindowAttribute(handle, attribute, ref v, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Test seam: back to a fresh process.</summary>
    public static void ResetForTests()
    {
        _initialized = false;
        _isWindows = OperatingSystem.IsWindows();
        _build = OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : 0;
        _fuseTripped = false;
        _armed = false;
        _fusePath = "";
        ModeInForce = DirectOutputMode.Composed;
        PlanAtStart = new DirectOutputPlan(DirectOutputMode.Composed, false, "Not decided yet.");
        WindowHook = null;
    }
}
