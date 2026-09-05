using System.Globalization;
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
/// published through a <see cref="FrameSlot"/> for any sink to draw. The desk's pointer, wheel,
/// clicks and keys go in through the browser's own input protocol (the DevTools Input domain —
/// what every browser automation tool uses), so they are trusted events that reach links,
/// players, sliders and frames from other sites alike, whatever window has the focus. Everything
/// WebView2 happens on the UI thread (it is apartment-bound); the render threads only read the slot.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WebFrameSource : IWebSource, IDisposable
{
    /// <summary>How often the page's picture is grabbed. A grab encodes and decodes a frame, so this is a ceiling, not a promise.</summary>
    public const int CaptureFps = 20;

    /// <summary>What one wheel notch scrolls, in page pixels — the browser's own default.</summary>
    public const float WheelPixelsPerLine = 100;

    /// <summary>Presses at one spot within this count as a double (then a triple) click.</summary>
    public static readonly TimeSpan MultiClickWindow = TimeSpan.FromMilliseconds(500);

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
    private readonly LinkedList<(string Method, string Json)> _input = new();
    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private DispatcherTimer? _timer;
    private bool _capturing;
    private bool _pumping;
    private volatile bool _disposed;
    private volatile string _status = "Starting the browser…";
    private volatile string _title = "";
    private volatile string _currentUrl;
    private double _zoomPct = 100;
    private bool _muted;
    private SKPoint? _pointer;
    private long _lastClickTicks;
    private int _grabFailures;
    private int _inputFailures;
    private bool _pressed;
    private int _clickCount;
    private long _lastPressTicks;
    private (int X, int Y) _lastPress;

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
                // Nothing opens outside Patterns: a link that wants a new window opens here instead.
                e.Handled = true;
                _core.Navigate(e.Uri);
            };
            _core.ProcessFailed += (_, _) => _status = "The page's process failed — press Reload.";

            // The page believes it is the focused, active window even though ours never takes the
            // focus (that would steal it from the desk): a caret blinks in a field, a player's
            // shortcuts listen, document.hasFocus() is true — the way a browser automation session sees it.
            try
            {
                await _core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
            }
            catch (Exception ex)
            {
                Log.Warn("Web page focus emulation not enabled.", ex);
            }
            if (_disposed) return;

            NavigateCore(_currentUrl);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / CaptureFps) };
            _timer.Tick += (_, _) => _ = GrabAsync();
            _timer.Start();
            Log.Info($"Web page opened in the engine: {_currentUrl} ({_width}×{_height}).");
            _ = PumpAsync();   // anything the desk sent while the browser was starting
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
        Mouse("mouseMoved", nx, ny);
    }

    public void PointerDown(float nx, float ny)
    {
        _pointer = new SKPoint(nx, ny);
        Interlocked.Exchange(ref _lastClickTicks, DateTime.UtcNow.Ticks);
        var at = ToPixels(nx, ny);
        var now = DateTime.UtcNow.Ticks;
        var again = now - _lastPressTicks < MultiClickWindow.Ticks && Math.Abs(at.X - _lastPress.X) <= 4 && Math.Abs(at.Y - _lastPress.Y) <= 4;
        _clickCount = again ? Math.Min(3, _clickCount + 1) : 1;
        _lastPressTicks = now;
        _lastPress = at;
        _pressed = true;
        Mouse("mousePressed", nx, ny);
    }

    public void PointerUp(float nx, float ny)
    {
        _pointer = new SKPoint(nx, ny);
        Mouse("mouseReleased", nx, ny);
        _pressed = false;
    }

    public void PointerLeave() => _pointer = null;

    public void Wheel(float nx, float ny, float deltaLines, bool horizontal)
    {
        _pointer = new SKPoint(nx, ny);
        var (x, y) = ToPixels(nx, ny);
        // The desk's wheel reports a notch up as +1; the browser scrolls down for a positive delta.
        var px = -deltaLines * WheelPixelsPerLine;
        Enqueue("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        {
            type = "mouseWheel",
            x,
            y,
            deltaX = horizontal ? px : 0f,
            deltaY = horizontal ? 0f : px,
            button = "none",
            buttons = _pressed ? 1 : 0,
            modifiers = 0,
        }));
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (element == "\r") continue;
            // A character a US key types goes as that key — the page sees a real keystroke; anything else is inserted as text, as an IME would.
            if (element.Length == 1 && WebKeys.ForChar(element[0]) is { } press) Key(press);
            else Enqueue("Input.insertText", JsonSerializer.Serialize(new { text = element }));
        }
    }

    public void PressKey(string key)
    {
        if (!WebKeys.TryParse(key, out var press))
        {
            Log.Warn($"Web page key not understood: '{key}'.");
            return;
        }
        Key(press);
    }

    public void RunScript(string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return;
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

    public void Navigate(string url) => OnUi(() => NavigateCore(WebAddress.Normalize(url)));

    public void GoBack() => OnUi(() => { if (_core is { CanGoBack: true }) _core.GoBack(); });

    public void GoForward() => OnUi(() => { if (_core is { CanGoForward: true }) _core.GoForward(); });

    public void Reload() => OnUi(() => _core?.Reload());

    // ---- input, in order -----------------------------------------------------------------------

    private void Mouse(string type, float nx, float ny)
    {
        var (x, y) = ToPixels(nx, ny);
        var down = _pressed || type == "mousePressed";
        Enqueue("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        {
            type,
            x,
            y,
            button = down || type == "mouseReleased" ? "left" : "none",
            buttons = down && type != "mouseReleased" ? 1 : 0,
            clickCount = type == "mouseMoved" ? 0 : _clickCount,
            modifiers = 0,
        }));
    }

    private void Key(in WebKeyPress press)
    {
        var down = new Dictionary<string, object>
        {
            ["type"] = press.HasText ? "keyDown" : "rawKeyDown",
            ["modifiers"] = press.Modifiers,
            ["key"] = press.Key,
            ["code"] = press.Code,
            ["windowsVirtualKeyCode"] = press.VirtualKey,
            ["nativeVirtualKeyCode"] = press.VirtualKey,
        };
        if (press.HasText)
        {
            down["text"] = press.Text;
            down["unmodifiedText"] = press.Text;
        }
        Enqueue("Input.dispatchKeyEvent", JsonSerializer.Serialize(down));
        Enqueue("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
        {
            type = "keyUp",
            modifiers = press.Modifiers,
            key = press.Key,
            code = press.Code,
            windowsVirtualKeyCode = press.VirtualKey,
            nativeVirtualKeyCode = press.VirtualKey,
        }));
    }

    /// <summary>Queues one protocol call; a move that follows a move replaces it, so a fast hand never builds a backlog.</summary>
    private void Enqueue(string method, string json)
    {
        if (_disposed) return;
        OnUi(() =>
        {
            if (_disposed) return;
            if (json.Contains("\"mouseMoved\"", StringComparison.Ordinal) && _input.Last is { } last && last.Value.Json.Contains("\"mouseMoved\"", StringComparison.Ordinal))
            {
                _input.RemoveLast();
            }
            _input.AddLast((method, json));
            if (!_pumping) _ = PumpAsync();
        });
    }

    /// <summary>Sends the queued calls one after another, so a press never overtakes the move before it.</summary>
    private async Task PumpAsync()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (_input.First is { } next && !_disposed && _core is { } core)
            {
                _input.RemoveFirst();
                try
                {
                    await core.CallDevToolsProtocolMethodAsync(next.Value.Method, next.Value.Json);
                }
                catch (Exception ex)
                {
                    if (_inputFailures++ % 100 == 0) Log.Warn($"Web page input failed ({next.Value.Method}).", ex);
                }
            }
        }
        finally
        {
            _pumping = false;
        }
    }

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

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private (int X, int Y) ToPixels(float nx, float ny) => (
        (int)Math.Round(Math.Clamp(nx, 0, 1) * (_width - 1)),
        (int)Math.Round(Math.Clamp(ny, 0, 1) * (_height - 1)));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _timer?.Stop();
            _timer = null;
            _input.Clear();
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

    // ---- Win32 ---------------------------------------------------------------------------------

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
