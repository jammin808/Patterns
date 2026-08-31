using Avalonia.Platform;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Which graphics card the show renders on. At startup (before Avalonia builds) the machine's
/// adapters are enumerated, the settings' preference is resolved to one of them, and Windows'
/// per-app GPU preference is written so in-process video decode follows the same card. When
/// Avalonia creates its D3D11 device it asks <see cref="SelectAdapter"/>, which answers with
/// that choice. Static because it must exist before any service does.
/// </summary>
public static class GpuService
{
    public static IReadOnlyList<GpuAdapterInfo> Adapters { get; private set; } = Array.Empty<GpuAdapterInfo>();

    /// <summary>Index into <see cref="Adapters"/> the settings resolve to (-1 = no override).</summary>
    public static int RequestedIndex { get; private set; } = -1;

    public static string RequestedName => RequestedIndex >= 0 && RequestedIndex < Adapters.Count
        ? Adapters[RequestedIndex].Name
        : "";

    /// <summary>The adapter the renderer actually got — set when Avalonia's callback runs.</summary>
    public static string ActiveAdapterName { get; private set; } = "";

    /// <summary>One-line result of the last Windows preference write, for the Admin tab.</summary>
    public static string RegistryStatus { get; private set; } = "";

    private static bool _initialized;

    /// <summary>Called from Program.Main before Avalonia starts. Never throws.</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Adapters = Dxgi.Enumerate();
            GraphicsConfig config;
            try
            {
                config = new SettingsStore().Load().Admin.Graphics;
            }
            catch
            {
                config = new GraphicsConfig();
            }
            RequestedIndex = GpuSelector.Resolve(config, Adapters);
            ApplyWindowsPreference(config);
            Log.Info($"GPU: {Adapters.Count} adapter(s) — " +
                     (RequestedIndex < 0 ? "no override, Windows decides." : $"using {RequestedName}."));
        }
        catch (Exception ex)
        {
            Log.Warn("GPU selection setup failed — using defaults.", ex);
        }
    }

    /// <summary>
    /// Writes (or clears) Windows' per-app GPU preference for this exe, and removes the entry
    /// for a previous exe location when the portable folder has moved. Returns a status line.
    /// </summary>
    public static string ApplyWindowsPreference(GraphicsConfig config)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
        {
            return RegistryStatus = "";
        }
        try
        {
            if (config.LastAppliedExePath is { Length: > 0 } old &&
                !string.Equals(old, exe, StringComparison.OrdinalIgnoreCase))
            {
                WinRegistry.DeleteUserGpuPreference(old);
            }

            var value = GpuSelector.RegistryValue(config, Adapters);
            WinRegistry.SetUserGpuPreference(exe, value);
            RegistryStatus = value switch
            {
                "" => "No Windows GPU preference set — Windows decides.",
                "GpuPreference=2;" => "Registered with Windows as high performance — video decode follows the same card.",
                "GpuPreference=1;" => "Registered with Windows as power saving.",
                _ => "Windows GPU preference left to auto for this adapter.",
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Windows GPU preference write failed.", ex);
            RegistryStatus = "Could not write the Windows GPU preference (it is optional — the renderer choice still applies).";
        }
        return RegistryStatus;
    }

    /// <summary>After settings load: recompute the requested adapter and remember this exe path.</summary>
    public static void RecordAppliedPath(ShowState state)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe) return;
        RequestedIndex = GpuSelector.Resolve(state.Admin.Graphics, Adapters);
        if (state.Admin.Graphics.LastAppliedExePath != exe)
        {
            state.Admin.Graphics.LastAppliedExePath = exe;
        }
    }

    /// <summary>
    /// Avalonia's adapter callback (AngleEgl + D3D11): map our chosen adapter onto the list the
    /// compositor enumerated, by LUID first, name second. Any trouble means index 0 — never
    /// break rendering over a preference.
    /// </summary>
    public static int SelectAdapter(IReadOnlyList<PlatformGraphicsDeviceAdapterDescription> adapters)
    {
        var pick = 0;
        try
        {
            if (adapters.Count == 0) return 0;
            if (RequestedIndex >= 0 && RequestedIndex < Adapters.Count)
            {
                var want = Adapters[RequestedIndex];
                for (var i = 0; i < adapters.Count; i++)
                {
                    var luid = adapters[i].DeviceLuid;
                    if (luid is { Length: 8 } && BitConverter.ToInt64(luid, 0) == want.Luid)
                    {
                        pick = i;
                        goto found;
                    }
                }
                for (var i = 0; i < adapters.Count; i++)
                {
                    if (string.Equals(adapters[i].Description, want.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        pick = i;
                        break;
                    }
                }
            }
        found:
            ActiveAdapterName = adapters[pick].Description ?? "";
            Log.Info($"Renderer GPU: {(ActiveAdapterName.Length > 0 ? ActiveAdapterName : "(unnamed adapter)")}.");
        }
        catch
        {
            pick = 0;
        }
        return pick;
    }

    // ---- facts for the health advisor and the Admin tab -------------------------------------

    public static bool DiscreteGpuPresent
    {
        get
        {
            foreach (var a in Adapters)
            {
                if (a.IsDiscreteVendor && !a.IsSoftware) return true;
            }
            return false;
        }
    }

    public static string BestGpuName
    {
        get
        {
            var best = GpuSelector.ChooseBest(Adapters);
            return best >= 0 ? Adapters[best].Name : "";
        }
    }

    /// <summary>Whether the renderer is (or will be) on the best adapter. True when unknown.</summary>
    public static bool UsingBestGpu
    {
        get
        {
            var best = GpuSelector.ChooseBest(Adapters);
            if (best < 0) return true;
            var active = ActiveAdapterName.Length > 0 ? ActiveAdapterName : RequestedName;
            if (active.Length == 0) return true;
            return string.Equals(active, Adapters[best].Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Test/screenshot seam: stage an adapter picture without touching DXGI.</summary>
    public static void Seed(IReadOnlyList<GpuAdapterInfo> adapters, int requestedIndex, string activeName)
    {
        Adapters = adapters;
        RequestedIndex = requestedIndex;
        ActiveAdapterName = activeName;
    }

    /// <summary>LUID whose video memory the metrics should watch (active > requested > first).</summary>
    public static long WatchedLuid
    {
        get
        {
            if (ActiveAdapterName.Length > 0)
            {
                foreach (var a in Adapters)
                {
                    if (string.Equals(a.Name, ActiveAdapterName, StringComparison.OrdinalIgnoreCase)) return a.Luid;
                }
            }
            if (RequestedIndex >= 0 && RequestedIndex < Adapters.Count) return Adapters[RequestedIndex].Luid;
            return Adapters.Count > 0 ? Adapters[0].Luid : 0;
        }
    }
}
