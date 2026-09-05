using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using Patterns.Core.Media;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// A web page as an engine input. WebView2 — the browser engine Windows 10 and 11 ship — renders
/// into a window of its own kept off every screen; its picture is grabbed at a steady rate and
/// published through a <see cref="FrameSlot"/> for any sink to draw. The desk's pointer, wheel
/// and clicks are posted to the browser's window as real mouse messages, so links, players,
/// sliders and embedded frames all respond; typed text and named keys go by script to the field
/// the page has focused. Everything WebView2 happens on the UI thread (it is apartment-bound);
/// the render threads only read the slot.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WebFrameSource : IWebSource, IDisposable
{
    /// <summary>How often the page's picture is grabbed. A grab encodes and decodes a frame, so this is a ceiling, not a promise.</summary>
    public const int CaptureFps = 20;

    public const string RuntimeMissingNote =
        "WebView2 runtime not found — Windows Update brings it, or install the Evergreen runtime from microsoft.com/edge/webview2.";

    /// <summary>
    /// Chromium stops painting a window it thinks nobody can see, and ours is off every screen on
    /// purpose; these keep it drawing, keep timers honest and let a page play its video.
    /// </summary>
    public const string BrowserArguments =
        "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling --disable-renderer-backgrounding " +
        "--disable-background-timer-throttling --autoplay-policy=no-user-gesture-required";

    private readonly FrameSlot _slot = new();
    private readonly string _userDataFolder;
    private readonly int _width;
    private readonly int _height;
    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private DispatcherTimer? _timer;
    private bool _capturing;
    private volatile bool _disposed;
    private volatile string _status = "Starting the browser…";
    private volatile string _title = "";
    private volatile string _currentUrl;
    private double _zoomPct = 100;
    private bool _muted;
    private SKPoint? _pointer;
    private long _lastClickTicks;
    private int _grabFailures;

    private WebFrameSource(string url, int width, int height, string userDataFolder)
    {
        _currentUrl = url;
        _width = width;
        _height = height;
        _userDataFolder = userDataFolder;
    }

    /// <summary>Opens a page for a wanted input ("1920x1080" in its Format; a bad format falls back to 1080p).</summary>
    public static WebFrameSource Create(MediaLocator.WantedInput wanted, string userDataFolder)
    {
        var (w, h) = WebEngine.ParseSize(wanted.Format);
        var source = new WebFrameSource(wanted.Target, w, h, userDataFolder) { _zoomPct = wanted.Zoom, _muted = wanted.Mute };
        _ = source.StartAsync();
        return source;
    }

    /// <summary>Whether pages can be shown here — the WebView2 runtime and its loader are present. The note says what is missing.</summary>
    public static bool Probe(out string note)
    {
        note = "";
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrEmpty(version))
            {
                note = RuntimeMissingNote;
                return false;
            }
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            note = RuntimeMissingNote;
            return false;
        }
        catch (DllNotFoundException)
        {
            note = "WebView2Loader.dll is missing beside Patterns.exe — copy the app folder whole.";
            return false;
        }
        catch (Exception ex)
        {
            note = "WebView2 could not start: " + ex.Message;
            return false;
        }
    }

    // ---- start-up ------------------------------------------------------------------------------

    private async Task StartAsync()
    {
        try
        {
            _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "Patterns web page", WS_POPUP,
                -32000, -32000, _width, _height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _status = "Could not create the page's window.";
                return;
            }
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);

            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = BrowserArguments };
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, options);
            if (_disposed) return;
            var controller = await environment.CreateCoreWebView2ControllerAsync(_hwnd);
            if (_disposed)
            {
                controller.Close();
                return;
            }
            _controller = controller;
            _core = controller.CoreWebView2;

            try
            {
                // CSS pixels = raw pixels, whatever the desk's display scale: the page lays out for the size asked.
                controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
                controller.RasterizationScale = 1.0;
                controller.ShouldDetectMonitorScaleChanges = false;
            }
            catch (Exception ex)
            {
                Log.Warn("Web page scale settings not applied.", ex);
            }
            controller.Bounds = new System.Drawing.Rectangle(0, 0, _width, _height);
            controller.IsVisible = true;
            ApplyZoom();
            ApplyMute();

            var settings = _core.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsPinchZoomEnabled = false;
            settings.IsSwipeNavigationEnabled = false;

            _core.NavigationStarting += (_, e) => _status = "Loading " + WebAddress.ShortName(e.Uri) + "…";
            _core.NavigationCompleted += (_, e) =>
            {
                _currentUrl = _core.Source;
                _status = e.IsSuccess ? "Showing" : $"The page failed: {e.WebErrorStatus}";
            };
            _core.DocumentTitleChanged += (_, _) => _title = _core.DocumentTitle ?? "";
            _core.NewWindowRequested += (_, e) =>
            {
                // A link that wants a new window opens here instead — there is only this window.
                e.Handled = true;
                _core.Navigate(e.Uri);
            };
            _core.ProcessFailed += (_, _) => _status = "The page's process failed — press Reload.";
            await _core.AddScriptToExecuteOnDocumentCreatedAsync(DeskScript);
            if (_disposed) return;

            NavigateCore(_currentUrl);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / CaptureFps) };
            _timer.Tick += (_, _) => _ = GrabAsync();
            _timer.Start();
            Log.Info($"Web page opened in the engine: {_currentUrl} ({_width}×{_height}).");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _status = RuntimeMissingNote;
            WebInput.AvailabilityNote = RuntimeMissingNote;
        }
        catch (Exception ex)
        {
            _status = "The browser could not start: " + ex.Message;
            Log.Error("Web page start failed.", ex);
        }
    }

    private async Task GrabAsync()
    {
        if (_capturing || _disposed || _core is null) return;
        _capturing = true;
        try
        {
            using var stream = new MemoryStream();
            await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
            if (_disposed) return;
            var bytes = stream.ToArray();
            var image = await Task.Run(() => Decode(bytes));
            if (image is null) return;
            if (_disposed)
            {
                image.Dispose();
                return;
            }
            _slot.Publish(image);
        }
        catch (Exception ex)
        {
            if (_grabFailures++ % 200 == 0) Log.Warn("Web page capture failed.", ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    private static SKImage? Decode(byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap is null) return null;
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    // ---- the frame source ----------------------------------------------------------------------

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint) => _slot.Draw(canvas, dest, paint, FrameCrop.None);

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop) => _slot.Draw(canvas, dest, paint, in crop);

    public SKSizeI? FrameSize => _slot.Size;

    public bool IsPlaying
    {
        get
        {
            var last = _slot.PublishedUtcTicks;
            return last != 0 && DateTime.UtcNow.Ticks - last < TimeSpan.TicksPerSecond * 3;
        }
    }

    public bool IsEnded => false;

    public double DurationSeconds => 0;

    public string StatusText => _slot.HasFrame ? _status : _status + " (no picture yet)";

    // ---- the web source ------------------------------------------------------------------------

    public SKSizeI PageSize => new(_width, _height);

    public SKPoint? PointerNorm => _pointer;

    public DateTime? LastClickUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastClickTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public string CurrentUrl => _currentUrl;

    public string Title => _title;

    public double ZoomPct
    {
        get => _zoomPct;
        set
        {
            var clamped = Math.Clamp(value, 25, 400);
            if (Math.Abs(clamped - _zoomPct) < 0.01) return;
            _zoomPct = clamped;
            OnUi(ApplyZoom);
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            if (_muted == value) return;
            _muted = value;
            OnUi(ApplyMute);
        }
    }

    public void PointerMove(float nx, float ny)
    {
        _pointer = new SKPoint(nx, ny);
        Post(WM_MOUSEMOVE, nx, ny, 0);
    }

    public void PointerDown(float nx, float ny)
    {
        _pointer = new SKPoint(nx, ny);
        Interlocked.Exchange(ref _lastClickTicks, DateTime.UtcNow.Ticks);
        Post(WM_LBUTTONDOWN, nx, ny, MK_LBUTTON);
    }

    public void PointerUp(float nx, float ny)
    {
        _pointer = new SKPoint(nx, ny);
        Post(WM_LBUTTONUP, nx, ny, 0);
    }

    public void PointerLeave()
    {
        _pointer = null;
        if (_hwnd != IntPtr.Zero) PostMessageW(DeepestChild(_hwnd, 0, 0), WM_MOUSELEAVE, IntPtr.Zero, IntPtr.Zero);
    }

    public void Wheel(float nx, float ny, float deltaLines, bool horizontal)
    {
        _pointer = new SKPoint(nx, ny);
        if (_hwnd == IntPtr.Zero) return;
        var (x, y) = ToPixels(nx, ny);
        var target = DeepestChild(_hwnd, x, y);
        // The wheel message carries screen coordinates; the window sits off-screen, so ask where that is.
        var pt = new POINT { X = x, Y = y };
        ClientToScreen(_hwnd, ref pt);
        var delta = (short)Math.Clamp(Math.Round(deltaLines * WHEEL_DELTA), short.MinValue, short.MaxValue);
        var wParam = (IntPtr)(((ushort)delta << 16) | 0);
        PostMessageW(target, horizontal ? WM_MOUSEHWHEEL : WM_MOUSEWHEEL, wParam, MakeLParam(pt.X, pt.Y));
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        RunScript($"window.__patterns && window.__patterns.type({JsonSerializer.Serialize(text)})");
    }

    public void PressKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        RunScript($"window.__patterns && window.__patterns.key({JsonSerializer.Serialize(key.Trim())})");
    }

    public void Navigate(string url) => OnUi(() => NavigateCore(WebAddress.Normalize(url)));

    public void GoBack() => OnUi(() => { if (_core is { CanGoBack: true }) _core.GoBack(); });

    public void GoForward() => OnUi(() => { if (_core is { CanGoForward: true }) _core.GoForward(); });

    public void Reload() => OnUi(() => _core?.Reload());

    // ---- plumbing ------------------------------------------------------------------------------

    private void NavigateCore(string url)
    {
        if (_core is null || _disposed) return;
        try
        {
            var target = url;
            if (!target.Contains("://") && File.Exists(target)) target = new Uri(Path.GetFullPath(target)).AbsoluteUri;
            _currentUrl = target;
            _core.Navigate(target);
        }
        catch (Exception ex)
        {
            _status = "Could not open that address: " + ex.Message;
            Log.Warn($"Web page navigate failed: {url}", ex);
        }
    }

    private void ApplyZoom()
    {
        try
        {
            if (_controller is { } c) c.ZoomFactor = _zoomPct / 100.0;
        }
        catch (Exception ex)
        {
            Log.Warn("Web page zoom not applied.", ex);
        }
    }

    private void ApplyMute()
    {
        try
        {
            if (_core is { } core) core.IsMuted = _muted;
        }
        catch (Exception ex)
        {
            Log.Warn("Web page mute not applied.", ex);
        }
    }

    private void RunScript(string script)
    {
        OnUi(async () =>
        {
            if (_core is null || _disposed) return;
            try
            {
                await _core.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Log.Warn("Web page script failed.", ex);
            }
        });
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private (int X, int Y) ToPixels(float nx, float ny) => (
        (int)Math.Round(Math.Clamp(nx, 0, 1) * (_width - 1)),
        (int)Math.Round(Math.Clamp(ny, 0, 1) * (_height - 1)));

    private void Post(uint message, float nx, float ny, int keys)
    {
        if (_hwnd == IntPtr.Zero) return;
        var (x, y) = ToPixels(nx, ny);
        var target = DeepestChild(_hwnd, x, y);
        var pt = new POINT { X = x, Y = y };
        ClientToScreen(_hwnd, ref pt);
        ScreenToClient(target, ref pt);
        PostMessageW(target, message, (IntPtr)keys, MakeLParam(pt.X, pt.Y));
    }

    /// <summary>The browser's innermost window under a point of ours — the one that handles mouse messages.</summary>
    private static IntPtr DeepestChild(IntPtr root, int x, int y)
    {
        var current = root;
        var screen = new POINT { X = x, Y = y };
        ClientToScreen(root, ref screen);
        for (var depth = 0; depth < 8; depth++)
        {
            var local = screen;
            ScreenToClient(current, ref local);
            var next = RealChildWindowFromPoint(current, local);
            if (next == IntPtr.Zero || next == current) break;
            current = next;
        }
        return current;
    }

    private static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _timer?.Stop();
            _timer = null;
            _controller?.Close();
            _controller = null;
            _core = null;
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Web page close issue.", ex);
        }
        _slot.Dispose();
    }

    /// <summary>
    /// Runs in every document the page loads: typed text goes into the focused field, named keys
    /// do what a keyboard would — submit a form, follow a link, delete, move focus, scroll.
    /// Frames from another site are out of reach (the browser keeps them apart); the mouse
    /// messages still reach them.
    /// </summary>
    private const string DeskScript = """
        window.__patterns = {
          type: function (t) {
            var a = document.activeElement;
            if (a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA' || a.isContentEditable)) {
              if (!document.execCommand('insertText', false, t)) {
                a.value = (a.value || '') + t;
                a.dispatchEvent(new Event('input', { bubbles: true }));
              }
              return 1;
            }
            return 0;
          },
          key: function (k) {
            var a = document.activeElement || document.body;
            var mk = function (type) { return new KeyboardEvent(type, { key: k, code: k, bubbles: true, cancelable: true }); };
            var swallowed = !a.dispatchEvent(mk('keydown'));
            a.dispatchEvent(mk('keypress'));
            a.dispatchEvent(mk('keyup'));
            if (swallowed) return 1;
            var editable = a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA' || a.isContentEditable);
            if (k === 'Enter') {
              if (a && a.tagName === 'INPUT' && a.form) { if (a.form.requestSubmit) a.form.requestSubmit(); else a.form.submit(); }
              else if (a && (a.tagName === 'A' || a.tagName === 'BUTTON')) a.click();
              else if (editable && a.tagName !== 'INPUT') document.execCommand('insertText', false, '\n');
            } else if (k === 'Backspace') {
              if (editable) document.execCommand('delete');
            } else if (k === 'Escape') {
              if (a && a.blur) a.blur();
            } else if (k === 'Tab') {
              var f = Array.prototype.filter.call(document.querySelectorAll('a[href],button,input,select,textarea,[tabindex]'),
                function (e) { return !e.disabled && e.tabIndex >= 0 && e.offsetParent !== null; });
              if (f.length) f[(f.indexOf(a) + 1) % f.length].focus();
            } else if (!editable) {
              var page = window.innerHeight * 0.9;
              var d = { ArrowDown: [0, 60], ArrowUp: [0, -60], ArrowLeft: [-60, 0], ArrowRight: [60, 0],
                        PageDown: [0, page], PageUp: [0, -page], Space: [0, page] }[k];
              if (d) window.scrollBy(d[0], d[1]);
              else if (k === 'Home') window.scrollTo(0, 0);
              else if (k === 'End') window.scrollTo(0, document.body.scrollHeight);
            } else if (k === 'Space') {
              document.execCommand('insertText', false, ' ');
            }
            return 1;
          }
        };
        """;

    // ---- Win32 ---------------------------------------------------------------------------------

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MOUSEHWHEEL = 0x020E;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const int MK_LBUTTON = 0x0001;
    private const int WHEEL_DELTA = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr RealChildWindowFromPoint(IntPtr parent, POINT point);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
}
