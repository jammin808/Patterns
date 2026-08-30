using System.Diagnostics;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Opens web pages on chosen screens as managed browser windows (Edge, else Chrome) —
/// full screen (kiosk) or windowed — using a private profile folder so the windows belong
/// to Patterns: they never touch the operator's own browser and close cleanly with one
/// click. Web windows sit on top of the OS, not inside the engine (no spans/NDI of pages).
/// </summary>
public sealed class WebService : IDisposable
{
    private readonly List<(Process Process, string ProfileDir)> _processes = new();
    private string _status = "No web pages open.";

    public string Status => _status;

    /// <summary>Command-line for the managed browser window. Pure — unit tested.</summary>
    public static string BuildArgs(string url, bool kiosk, int x, int y, int w, int h, string userDataDir, bool isEdge)
    {
        var quotedUrl = '"' + url.Replace("\"", "%22") + '"';
        var common = $"--no-first-run --new-window --user-data-dir=\"{userDataDir}\" --window-position={x},{y}";
        if (kiosk)
        {
            var edgeKiosk = isEdge ? " --edge-kiosk-type=fullscreen" : "";
            return $"--kiosk {quotedUrl}{edgeKiosk} {common}";
        }
        return $"--app={quotedUrl} {common} --window-size={w},{h}";
    }

    /// <summary>Normalises operator input: bare "example.com" becomes https.</summary>
    public static string NormalizeUrl(string input)
    {
        var s = input.Trim();
        if (s.Length == 0) return s;
        if (s.Contains("://") || File.Exists(s)) return s;
        return "https://" + s;
    }

    public void Open(string url, ScreenInfo? screen, bool kiosk)
    {
        url = NormalizeUrl(url);
        if (string.IsNullOrWhiteSpace(url))
        {
            _status = "Enter a page address first.";
            return;
        }

        var bounds = screen?.Bounds;
        var x = bounds?.X ?? 0;
        var y = bounds?.Y ?? 0;
        var w = bounds?.Width ?? 1280;
        var h = bounds?.Height ?? 800;
        if (!kiosk)
        {
            // Windowed: a comfortable window inset on the target screen.
            x += w / 8; y += h / 8; w = w * 3 / 4; h = h * 3 / 4;
        }

        var browser = FindBrowser(out var isEdge);
        try
        {
            if (browser is null)
            {
                // Default browser fallback — no positioning or management possible.
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                _status = "Opened in the default browser (Edge/Chrome not found — window can't be placed or closed from here).";
                Log.Warn(_status);
                return;
            }

            var userDataDir = Path.Combine(Path.GetTempPath(), "patterns-web", Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(userDataDir);

            var p = Process.Start(new ProcessStartInfo(browser, BuildArgs(url, kiosk, x, y, w, h, userDataDir, isEdge))
            {
                UseShellExecute = false,
            });
            if (p is not null) _processes.Add((p, userDataDir));
            Prune();
            _status = $"{_processes.Count} web window(s) open — {(kiosk ? "full screen" : "windowed")} on {screen?.Label ?? "primary"}.";
            Log.Info($"Web page opened: {url} ({(kiosk ? "kiosk" : "windowed")}, {screen?.Label ?? "primary"}).");
        }
        catch (Exception ex)
        {
            _status = $"Could not open the page: {ex.Message}";
            Log.Error("Web open failed.", ex);
        }
    }

    public void CloseAll()
    {
        foreach (var (p, dir) in _processes)
        {
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Log.Warn("Web window close issue.", ex);
            }
            p.Dispose();
            TryDeleteProfile(dir);
        }
        _processes.Clear();
        _status = "Web windows closed.";
    }

    private void Prune()
    {
        for (var i = _processes.Count - 1; i >= 0; i--)
        {
            try
            {
                if (_processes[i].Process.HasExited)
                {
                    _processes[i].Process.Dispose();
                    TryDeleteProfile(_processes[i].ProfileDir);
                    _processes.RemoveAt(i);
                }
            }
            catch
            {
                _processes.RemoveAt(i);
            }
        }
    }

    public int OpenCount
    {
        get
        {
            Prune();
            return _processes.Count;
        }
    }

    private static string? FindBrowser(out bool isEdge)
    {
        isEdge = false;
        if (!OperatingSystem.IsWindows()) return null;

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var edge in new[]
                 {
                     Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
                     Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
                 })
        {
            if (File.Exists(edge))
            {
                isEdge = true;
                return edge;
            }
        }

        foreach (var chrome in new[]
                 {
                     Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                     Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
                     Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
                 })
        {
            if (File.Exists(chrome)) return chrome;
        }

        return null;
    }

    private static void TryDeleteProfile(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // The browser may still be letting go of files — temp cleanup gets it later.
        }
    }

    public void Dispose() => CloseAll();
}
