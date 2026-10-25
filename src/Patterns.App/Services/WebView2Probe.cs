using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Web.WebView2.Core;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Round 89 (closes OPEN L26): <c>--verify-runtime</c>'s look at the web path, which no test can construct. The
/// runtime's version is read; then, on a thread of its own in a single-threaded apartment, a browser is made the way a
/// page source makes one — the same arguments, a window of its own kept off every screen, a user data folder of its
/// own — handed the round's document-created observer (<see cref="HeldPage.ObserverScript"/>) and a script that speaks
/// from every document, sent to a document of its own, and asked for that word and a script's answer. The thread turns
/// its own messages while it waits, so the browser's answers arrive, and every wait has the one deadline.
/// <para>
/// The runtime absent is said, and fails the check only when it was asked for (<c>--require-webview2</c>): a machine
/// may be meant to show no pages. The runtime present and the page not opening fails it: the exe that made it cannot
/// show a page here (its loader missing beside it, the browser refusing its arguments, a document script not running).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WebView2Probe
{
    /// <summary>How long the whole probe may take — a browser's first start on a cold machine is seconds, not minutes.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);

    /// <summary>What the probe found.</summary>
    public enum Outcome
    {
        /// <summary>A page opened, its document script spoke and a script answered.</summary>
        Ok,
        /// <summary>The WebView2 runtime is not installed: pages cannot be shown on this machine until it is.</summary>
        Absent,
        /// <summary>The runtime is there and the page did not open, or the loader is missing beside the exe.</summary>
        Failed,
    }

    /// <summary>The page the probe opens: no address, nothing fetched.</summary>
    private const string Page = "<!doctype html><html><head><title>Patterns probe</title></head><body>probe</body></html>";

    /// <summary>Put into every document before its first script, as the observer is: says the document's title once it is read.</summary>
    private const string DocumentScript =
        "document.addEventListener('DOMContentLoaded', function () { window.chrome.webview.postMessage('patterns-probe:' + document.title); }, { once: true });";

    private const string Spoken = "patterns-probe:Patterns probe";

    /// <summary>Runs the probe on a thread of its own in a single-threaded apartment, whatever the caller's thread is.</summary>
    public static (Outcome Outcome, string Words) Run()
    {
        (Outcome, string) result = (Outcome.Failed, "the probe did not run");
        var thread = new Thread(() =>
        {
            try
            {
                result = RunHere();
            }
            catch (Exception ex)
            {
                result = (Outcome.Failed, $"{ex.GetType().Name}: {ex.Message}");   // a fault of the probe's thread is the probe's verdict, never the process's end
            }
        }) { IsBackground = true, Name = "WebView2 probe" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(Deadline + TimeSpan.FromSeconds(15)) ? result : (Outcome.Failed, $"the probe did not finish within {Deadline.TotalSeconds:0} s");
    }

    private static (Outcome, string) RunHere()
    {
        string version;
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return (Outcome.Absent, WebFrameSource.RuntimeMissingNote);
        }
        catch (DllNotFoundException)
        {
            return (Outcome.Failed, "WebView2Loader.dll is missing beside Patterns.exe — the app folder was not copied whole");
        }
        if (string.IsNullOrEmpty(version)) return (Outcome.Absent, WebFrameSource.RuntimeMissingNote);

        var clock = Stopwatch.StartNew();
        var folder = Path.Combine(Path.GetTempPath(), "patterns-webview2-probe-" + Guid.NewGuid().ToString("N"));
        var hwnd = IntPtr.Zero;
        CoreWebView2Controller? controller = null;
        try
        {
            hwnd = CreateWindowExW(WsExToolWindow | WsExNoActivate, "STATIC", "Patterns web probe", WsPopup, -32000, -32000, 320, 180, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return (Outcome.Failed, $"runtime {version}, but no window could be made for the page (error {Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture)})");
            Directory.CreateDirectory(folder);
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = WebFrameSource.ArgumentsFor(hardwareDecoding: false) };
            var environment = Pump(CoreWebView2Environment.CreateAsync(null, folder, options), clock, "the browser's environment");
            controller = Pump(environment.CreateCoreWebView2ControllerAsync(hwnd), clock, "the browser");
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 320, 180);
            controller.IsVisible = true;
            var core = controller.CoreWebView2;
            Pump(core.AddScriptToExecuteOnDocumentCreatedAsync(HeldPage.ObserverScript), clock, "the window observer");
            Pump(core.AddScriptToExecuteOnDocumentCreatedAsync(DocumentScript), clock, "the document script");

            var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var landed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.WebMessageReceived += (_, e) =>
            {
                try
                {
                    heard.TrySetResult(e.TryGetWebMessageAsString() ?? "");
                }
                catch (ArgumentException)
                {
                    heard.TrySetResult("(a message that is not a string)");
                }
            };
            core.NavigationCompleted += (_, e) => landed.TrySetResult(e.IsSuccess ? "" : e.WebErrorStatus.ToString());
            core.NavigateToString(Page);

            var failed = Pump(landed.Task, clock, "the page");
            if (failed.Length > 0) return (Outcome.Failed, $"runtime {version}, but the page did not open ({failed})");
            var said = Pump(heard.Task, clock, "the document script's word");
            if (!string.Equals(said, Spoken, StringComparison.Ordinal)) return (Outcome.Failed, $"runtime {version}, but the document script said '{said}', not '{Spoken}'");
            var answer = Pump(core.ExecuteScriptAsync("6 * 7"), clock, "a script");
            if (!string.Equals(answer, "42", StringComparison.Ordinal)) return (Outcome.Failed, $"runtime {version}, but a script answered '{answer}', not 42");
            return (Outcome.Ok, $"runtime {version} — a page opened, its document script spoke and a script answered, in {clock.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
        }
        catch (TimeoutException ex)
        {
            return (Outcome.Failed, $"runtime {version}, but {ex.Message}");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (Outcome.Failed, $"runtime {version}, but {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                controller?.Close();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                _ = ex;   // the browser is going with the process either way
            }
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;   // the browser's own processes may hold the folder a moment longer: a temp folder the system clears
            }
        }
    }

    /// <summary>
    /// Waits for a browser answer on this thread, turning its messages meanwhile — the browser's callbacks arrive as
    /// messages to the thread that asked — until the answer or the probe's deadline.
    /// </summary>
    private static T Pump<T>(Task<T> task, Stopwatch clock, string what)
    {
        while (!task.IsCompleted)
        {
            var left = Deadline - clock.Elapsed;
            if (left <= TimeSpan.Zero) throw new TimeoutException($"{what} did not answer within {Deadline.TotalSeconds:0} s");
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PmRemove))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            if (task.IsCompleted) break;
            if (MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, (uint)Math.Clamp(left.TotalMilliseconds, 1, 50), QsAllInput, MwmoInputAvailable) == WaitFailed)
            {
                throw new InvalidOperationException($"waiting for {what} failed (error {Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture)})");
            }
        }
        #pragma warning disable VSTHRD002 // the task has completed: this reads its answer (or its fault), it never waits
        return task.GetAwaiter().GetResult();
        #pragma warning restore VSTHRD002
    }

    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint PmRemove = 0x0001;
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x0004;
    private const uint WaitFailed = 0xFFFFFFFF;

    /// <summary>MSG as user32 fills it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg msg);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg msg);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint milliseconds, uint wakeMask, uint flags);
}
