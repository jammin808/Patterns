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
/// into a window of its own kept off every screen; the browser's own screencast hands over every
/// frame its compositor draws (<see cref="ScreencastFrame"/>), decoded off the UI thread and
/// published through a <see cref="FrameSlot"/> for any sink to draw, with a screenshot poll as the
/// fallback when the screencast will not start. The desk's pointer, wheel,
/// clicks and keys go in through the browser's own input protocol (the DevTools Input domain —
/// what every browser automation tool uses), so they are trusted events that reach links,
/// players, sliders and frames from other sites alike, whatever window has the focus. Everything
/// WebView2 happens on the UI thread (it is apartment-bound); the render threads only read the slot.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WebFrameSource : IWebSource, IDisposable
{
    /// <summary>
    /// How often the page's picture is grabbed by screenshot when the screencast is not delivering.
    /// A grab stops the compositor, reads the page back and encodes it while the desk waits, so
    /// this is a ceiling for the fallback and never the rate a video is meant to play at.
    /// </summary>
    public const int CaptureFps = 20;

    /// <summary>A screencast that has sent nothing this long after starting is taken as not delivering, and the screenshot poll stands in.</summary>
    public static readonly TimeSpan ScreencastGrace = TimeSpan.FromSeconds(3);

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
        "--disable-backgrounding-occluded-windows --disable-background-timer-throttling --autoplay-policy=no-user-gesture-required";

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

    // The screencast: the browser pushes frames, each acked on arrival; the newest waits for the
    // decoder and an older one still waiting is dropped — a slow decode costs frames, never latency.
    private CoreWebView2DevToolsProtocolEventReceiver? _screencastEvents;
    private CoreWebView2DevToolsProtocolEventReceiver? _visibilityEvents;
    private volatile bool _screencastOn;
    private volatile bool _browserSaysHidden;
    private long _screencastStartTicks;
    private long _screencastFrames;
    private string? _pendingFrame;
    private int _decoding;
    private int _screencastFailures;
    private readonly FrameRateMeter _meter = new();
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
                // A new document is a new compositor: asked again so a page that arrived by a link keeps its rate.
                _ = StartScreencastAsync();
                if (_audioDevice.Length > 0) ApplyAudioDevice();
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

            await StartScreencastAsync();
            if (_disposed) return;

            NavigateCore(_currentUrl);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / CaptureFps) };
            _timer.Tick += (_, _) => _ = GrabAsync();
            _timer.Start();
            Log.Info($"Web page opened in the engine: {_currentUrl} ({_width}×{_height}, {(_screencastOn ? "screencast" : "screenshot poll")}).");
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

    // ---- the screencast ------------------------------------------------------------------------

    /// <summary>The browser is sending frames — or has just been asked to and is still within its grace.</summary>
    private bool ScreencastDelivering
    {
        get
        {
            if (!_screencastOn) return false;
            if (Interlocked.Read(ref _screencastFrames) > 0) return true;
            return DateTime.UtcNow.Ticks - Interlocked.Read(ref _screencastStartTicks) < ScreencastGrace.Ticks;
        }
    }

    /// <summary>
    /// Asks the browser for its screencast: the Page domain on, the frame event subscribed once,
    /// then the start call. A browser that refuses leaves the screenshot poll in charge and says
    /// so once in the log — the page still shows, at the old rate.
    /// </summary>
    private async Task StartScreencastAsync()
    {
        if (_core is null || _disposed) return;
        try
        {
            if (_screencastEvents is null)
            {
                await _core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
                if (_disposed || _core is null) return;
                _screencastEvents = _core.GetDevToolsProtocolEventReceiver(ScreencastFrame.EventName);
                _screencastEvents.DevToolsProtocolEventReceived += OnScreencastFrame;
                // The browser saying its window is hidden is the one thing that stops every capture path:
                // the flags above keep it from happening, and the status says so if it does.
                _visibilityEvents = _core.GetDevToolsProtocolEventReceiver("Page.screencastVisibilityChanged");
                _visibilityEvents.DevToolsProtocolEventReceived += (_, e) =>
                {
                    var hidden = (e.ParameterObjectAsJson ?? "").Contains("false", StringComparison.Ordinal);
                    if (hidden != _browserSaysHidden)
                    {
                        _browserSaysHidden = hidden;
                        if (hidden) Log.Warn("The browser reports the page's window as hidden — its picture stops until it is visible again (occlusion tracking should be off).");
                    }
                };
            }
            Interlocked.Exchange(ref _screencastStartTicks, DateTime.UtcNow.Ticks);
            await _core.CallDevToolsProtocolMethodAsync("Page.startScreencast", ScreencastFrame.StartParameters(_width, _height));
            _screencastOn = true;
        }
        catch (Exception ex)
        {
            _screencastOn = false;
            if (_screencastFailures++ == 0) Log.Warn("The page's screencast would not start — the screenshot poll stands in.", ex);
        }
    }

    /// <summary>
    /// A frame from the browser (UI thread). Acked at once so the next one is already on its way,
    /// then handed to the decoder: the newest frame replaces one still waiting, so a decode that
    /// runs behind drops frames rather than falling behind the room.
    /// </summary>
    private void OnScreencastFrame(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (_disposed || _core is null) return;
        var json = e.ParameterObjectAsJson;
        if (!ScreencastFrame.TryParse(json, out var sessionId, out _)) return;
        try
        {
            _ = _core.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", ScreencastFrame.AckParameters(sessionId));
        }
        catch (Exception ex)
        {
            if (_screencastFailures++ % 200 == 1) Log.Warn("Web page screencast ack failed.", ex);
        }
        Interlocked.Increment(ref _screencastFrames);
        Interlocked.Exchange(ref _pendingFrame, json);
        if (Interlocked.CompareExchange(ref _decoding, 1, 0) == 0) _ = Task.Run(DecodePending);
    }

    /// <summary>Decodes whatever is newest until nothing waits (a worker thread); the pool's bytes go back when the picture is made.</summary>
    private void DecodePending()
    {
        try
        {
            while (true)
            {
                var json = Interlocked.Exchange(ref _pendingFrame, null);
                if (json is null)
                {
                    Interlocked.Exchange(ref _decoding, 0);
                    // A frame that arrived between the last exchange and the release is nobody's: take it.
                    if (_pendingFrame is null || Interlocked.CompareExchange(ref _decoding, 1, 0) != 0) return;
                    continue;
                }
                if (_disposed) continue;
                if (!ScreencastFrame.TryParse(json, out _, out var base64)) continue;
                var bytes = ScreencastFrame.Rent(json, base64, out var length);
                if (bytes is null) continue;
                SKImage? image = null;
                try
                {
                    image = Decode(bytes.AsSpan(0, length));
                }
                catch (Exception ex)
                {
                    if (_grabFailures++ % 200 == 0) Log.Warn("Web page screencast frame did not decode.", ex);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
                }
                if (image is null) continue;
                if (_disposed)
                {
                    image.Dispose();
                    continue;
                }
                _slot.Publish(image);
                _meter.Tick(DateTime.UtcNow.Ticks);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _decoding, 0);
            Log.Warn("Web page screencast decoder stopped on an error; the next frame restarts it.", ex);
        }
    }

    // ---- the screenshot poll (the fallback) ----------------------------------------------------

    private async Task GrabAsync()
    {
        if (_capturing || _disposed || _core is null || ScreencastDelivering) return;
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
            _meter.Tick(DateTime.UtcNow.Ticks);
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

    private static SKImage? Decode(ReadOnlySpan<byte> bytes)
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

    /// <summary>
    /// The page is up and has a picture. Not "a frame arrived lately": the screencast sends
    /// nothing for a still page, and a dashboard that has not changed in a minute is still showing.
    /// </summary>
    public bool IsPlaying => _slot.HasFrame && _status == "Showing";

    public bool IsEnded => false;

    public double DurationSeconds => 0;

    /// <summary>"Showing · 30 fps" while a video plays; the rate is the room's, not the browser's promise.</summary>
    public string StatusText
    {
        get
        {
            if (!_slot.HasFrame) return _status + " (no picture yet)";
            var fps = FrameRate;
            var text = fps > 0 ? $"{_status} · {fps:0} fps" : _status;
            return _browserSaysHidden ? text + " · the browser thinks its window is hidden" : text;
        }
    }

    public double FrameRate => _meter.Rate(DateTime.UtcNow.Ticks);

    /// <summary>Whether the browser's screencast carries the picture (else the screenshot poll does) — the Media page's line.</summary>
    public bool ScreencastActive => ScreencastDelivering;

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

    private string _cleanCss = "";
    private string? _cleanScriptId;
    private string _audioDevice = "";
    private string? _sinkScriptId;
    private volatile string _audioRouteNote = "";

    /// <summary>
    /// The output the page's sound leaves by. Chromium lets a page pick an output for its media
    /// elements (setSinkId) once it may see the machine's outputs, which it may once the
    /// microphone permission is granted for its origin — granted here silently, on the page's
    /// profile, for the page's own origin only. The script finds the output by the name Windows
    /// gives it and applies it to every media element, now and as the page makes new ones; what
    /// it managed is read back into <see cref="AudioRouteNote"/>. A page that will not (no
    /// media element, a cross-origin player in a frame, an output the browser cannot see) keeps
    /// the machine's default and the note says so — routed or not is never assumed.
    /// </summary>
    public string AudioDevice
    {
        get => _audioDevice;
        set
        {
            var name = (value ?? "").Trim();
            if (name == _audioDevice) return;
            _audioDevice = name;
            OnUi(ApplyAudioDevice);
        }
    }

    public string AudioRouteNote => _audioRouteNote;

    private async void ApplyAudioDevice()
    {
        if (_core is null || _disposed) return;
        try
        {
            if (_sinkScriptId is { } old)
            {
                _sinkScriptId = null;
                _core.RemoveScriptToExecuteOnDocumentCreated(old);
            }
            if (_audioDevice.Length > 0)
            {
                try
                {
                    var origin = new Uri(_core.Source).GetLeftPart(UriPartial.Authority);
                    await _core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, origin, CoreWebView2PermissionState.Allow);
                }
                catch (Exception ex)
                {
                    Log.Warn("The page's microphone permission (what lets it see the outputs) could not be granted.", ex);
                }
                if (_disposed || _core is null) return;
                _sinkScriptId = await _core.AddScriptToExecuteOnDocumentCreatedAsync(SinkScript(_audioDevice));
            }
            if (_disposed || _core is null) return;
            await _core.ExecuteScriptAsync(SinkScript(_audioDevice));
            await Task.Delay(1500);
            if (_disposed || _core is null) return;
            var result = await _core.ExecuteScriptAsync("JSON.stringify(window.__patternsSink||null)");
            _audioRouteNote = ReadSinkNote(result, _audioDevice);
        }
        catch (Exception ex)
        {
            _audioRouteNote = "the page could not be asked: " + ex.Message;
            Log.Warn("Steering the page's sound failed.", ex);
        }
    }

    /// <summary>The page's own output picker driven by name; pure text, so the words can be tested.</summary>
    public static string SinkScript(string deviceName)
    {
        var want = System.Text.Json.JsonSerializer.Serialize(deviceName ?? "");
        return "(function(){var want=" + want + ";window.__patternsSinkWant=want;" +
               "async function apply(){try{if(!navigator.mediaDevices||!navigator.mediaDevices.enumerateDevices){window.__patternsSink={want:want,error:'this page cannot pick outputs'};return;}" +
               "var els=document.querySelectorAll('audio,video');" +
               "if(!want){for(const e of els){if(e.setSinkId){try{await e.setSinkId('');}catch(x){}}}window.__patternsSink={want:'',applied:'default',elements:els.length};return;}" +
               "var devs=await navigator.mediaDevices.enumerateDevices();var outs=devs.filter(function(d){return d.kind==='audiooutput'&&d.label;});" +
               "var m=outs.find(function(d){return d.label===want;})||outs.find(function(d){return d.label.indexOf(want)>=0||want.indexOf(d.label)>=0;});" +
               "if(!m){window.__patternsSink={want:want,error:outs.length===0?'the page cannot see any output (no permission)':'no output called '+want+' among '+outs.length};return;}" +
               "var n=0;for(const e of els){if(e.setSinkId){try{await e.setSinkId(m.deviceId);n++;}catch(err){window.__patternsSink={want:want,error:String(err&&err.message||err)};return;}}}" +
               "window.__patternsSink={want:want,applied:m.label,elements:n};}catch(err){window.__patternsSink={want:want,error:String(err&&err.message||err)};}}" +
               "apply();if(!window.__patternsSinkObs){window.__patternsSinkObs=new MutationObserver(function(){clearTimeout(window.__patternsSinkT);window.__patternsSinkT=setTimeout(apply,300);});" +
               "window.__patternsSinkObs.observe(document.documentElement,{childList:true,subtree:true});}})()";
    }

    /// <summary>The page's answer as a line: "routed to HDMI 3 (1 player)", "not routed: …", or "nothing answered yet".</summary>
    public static string ReadSinkNote(string? result, string wanted)
    {
        if (string.IsNullOrWhiteSpace(result) || result == "null") return wanted.Length == 0 ? "" : "nothing answered yet";
        try
        {
            using var outer = System.Text.Json.JsonDocument.Parse(result);
            var root = outer.RootElement;
            string? inner = root.ValueKind == System.Text.Json.JsonValueKind.String ? root.GetString() : root.GetRawText();
            if (string.IsNullOrEmpty(inner) || inner == "null") return wanted.Length == 0 ? "" : "nothing answered yet";
            using var doc = System.Text.Json.JsonDocument.Parse(inner);
            var e = doc.RootElement;
            if (e.TryGetProperty("error", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.String) return "not routed: " + error.GetString();
            if (e.TryGetProperty("applied", out var applied) && applied.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var n = e.TryGetProperty("elements", out var els) && els.ValueKind == System.Text.Json.JsonValueKind.Number ? els.GetInt32() : 0;
                var label = applied.GetString() ?? "";
                return label == "default" ? "the machine's default output" : $"routed to {label} ({n} player{(n == 1 ? "" : "s")})";
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return "the page's answer was not understood";
    }

    public string CleanCss
    {
        get => _cleanCss;
        set
        {
            var css = value ?? "";
            if (css == _cleanCss) return;
            _cleanCss = css;
            OnUi(ApplyClean);
        }
    }

    /// <summary>
    /// The style goes in twice: as a document-created script, so every page the browser loads from
    /// here on wears it before its own scripts run, and once into the document that is already
    /// open, so ticking CLEAN on a page that is on air changes the picture at once rather than at
    /// the next reload.
    /// </summary>
    private async void ApplyClean()
    {
        if (_core is null || _disposed) return;
        try
        {
            if (_cleanScriptId is { } old)
            {
                _cleanScriptId = null;
                _core.RemoveScriptToExecuteOnDocumentCreated(old);
            }
            var script = CleanScript(_cleanCss);
            if (script.Length > 0 && !_disposed && _core is not null)
            {
                _cleanScriptId = await _core.AddScriptToExecuteOnDocumentCreatedAsync(script);
            }
            if (!_disposed && _core is not null) await _core.ExecuteScriptAsync(CleanScript(_cleanCss, forNow: true));
        }
        catch (Exception ex)
        {
            // A page that will not wear the style is still a page: the show keeps its picture.
            Log.Warn("The page's CLEAN style could not be applied.", ex);
        }
    }

    /// <summary>
    /// A style element with an id of its own, put in as soon as there is a document to put it in.
    /// Selectors rather than hidden elements: the player rebuilds its controls as it plays, and a
    /// rule keeps holding where a hidden element would come back. With no style, the element goes.
    /// </summary>
    private static string CleanScript(string css, bool forNow = false)
    {
        const string id = "patterns-clean";
        if (css.Length == 0)
        {
            return forNow
                ? $"(function(){{var e=document.getElementById('{id}');if(e)e.remove();}})()"
                : "";
        }
        var literal = System.Text.Json.JsonSerializer.Serialize(css);
        return "(function(){var css=" + literal + ";" +
               "function put(){var e=document.getElementById('" + id + "');" +
               "if(!e){e=document.createElement('style');e.id='" + id + "';(document.head||document.documentElement).appendChild(e);}" +
               "if(e.textContent!==css)e.textContent=css;}" +
               "put();document.addEventListener('DOMContentLoaded',put);})()";
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

    /// <summary>The script's result as the browser's JSON text — how the page's player is read for the armed VT. "" when nothing came back.</summary>
    public async Task<string> RunScriptAsync(string script)
    {
        if (string.IsNullOrWhiteSpace(script) || _core is null || _disposed) return "";
        if (!UiThread.CheckAccess())
        {
            var tcs = new TaskCompletionSource<string>();
            UiThread.Post(async () =>
            {
                try
                {
                    tcs.TrySetResult(await RunScriptAsync(script));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            return await tcs.Task;
        }
        try
        {
            return await _core.ExecuteScriptAsync(script) ?? "";
        }
        catch (Exception ex)
        {
            Log.Warn("Web page script (with a result) failed.", ex);
            return "";
        }
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
        if (UiThread.CheckAccess()) action();
        else UiThread.Post(action);
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
            if (_screencastEvents is not null)
            {
                _screencastEvents.DevToolsProtocolEventReceived -= OnScreencastFrame;
                _screencastEvents = null;
            }
            _visibilityEvents = null;
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
