using System.Runtime.InteropServices;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Ndi;

/// <summary>
/// Renders the program to an offscreen raster surface on a dedicated thread and hands
/// frames to the NDI runtime. Clocked sends pace the loop to the configured frame rate.
/// Double-buffered: the SDK may reference the previous frame until the next send.
/// </summary>
public sealed class NdiSender : IDisposable
{
    private readonly SnapshotBus _bus;
    private readonly PatternEngine _engine = new();

    private Thread? _thread;
    private volatile bool _run;
    private volatile string _status = "Off";
    private volatile int _connections;

    public NdiSender(SnapshotBus bus)
    {
        _bus = bus;
    }

    /// <summary>Status line for the UI (polled — no cross-thread events to worry about).</summary>
    public string Status => _status;
    public int Connections => _connections;
    public bool IsRunning => _run;

    public static bool RuntimeAvailable => NdiInterop.Available;

    public static string RuntimeHelp =>
        "NDI runtime not found. Install the free NDI Tools/Runtime from ndi.video, " +
        "or drop Processing.NDI.Lib.x64.dll next to Patterns.exe, then re-enable NDI.";

    public void Start()
    {
        if (_run) return;
        if (!NdiInterop.Available)
        {
            _status = RuntimeHelp;
            return;
        }
        _run = true;
        _thread = new Thread(SendLoop)
        {
            Name = "ndi-sender",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        var t = _thread;
        _thread = null;
        if (t is not null && t.IsAlive && !t.Join(TimeSpan.FromSeconds(3)))
        {
            Log.Warn("NDI sender thread did not stop in time.");
        }
        _status = "Off";
        _connections = 0;
    }

    private void SendLoop()
    {
        var sink = new SinkState();
        IntPtr sender = IntPtr.Zero;
        IntPtr namePtr = IntPtr.Zero;
        SKSurface? surfaceA = null, surfaceB = null;
        var useA = true;
        long frame = 0;
        string currentName = "";
        var currentSize = SKSizeI.Empty;

        try
        {
            while (_run)
            {
                try
                {
                    var snap = _bus.Current;
                    var cfg = snap.State.Ndi;

                    // (Re)create the sender when the advertised name changes.
                    var name = string.IsNullOrWhiteSpace(cfg.SenderName) ? "Patterns" : cfg.SenderName.Trim();
                    if (sender == IntPtr.Zero || name != currentName)
                    {
                        DestroySender(ref sender, ref namePtr);
                        namePtr = NdiInterop.Utf8(name);
                        var create = new NdiInterop.SendCreate
                        {
                            NdiName = namePtr,
                            Groups = IntPtr.Zero,
                            ClockVideo = true,
                            ClockAudio = false,
                        };
                        sender = NdiInterop.NDIlib_send_create(ref create);
                        if (sender == IntPtr.Zero)
                        {
                            _status = "Could not create NDI sender (name in use?) — retrying…";
                            Thread.Sleep(2000);
                            continue;
                        }
                        currentName = name;
                        Log.Info($"NDI sender '{name}' created.");
                    }

                    // (Re)create surfaces when the frame size changes.
                    var size = new SKSizeI(Math.Max(16, cfg.Width), Math.Max(16, cfg.Height));
                    if (surfaceA is null || size != currentSize)
                    {
                        surfaceA?.Dispose();
                        surfaceB?.Dispose();
                        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                        surfaceA = SKSurface.Create(info);
                        surfaceB = SKSurface.Create(info);
                        if (surfaceA is null || surfaceB is null)
                        {
                            _status = $"Could not allocate {size.Width}×{size.Height} NDI frame buffers.";
                            Thread.Sleep(2000);
                            continue;
                        }
                        currentSize = size;
                    }

                    var surface = useA ? surfaceA : surfaceB!;
                    useA = !useA;

                    var ctx = new RenderContext
                    {
                        ViewportSize = size,
                        ReferenceSize = size,
                        ViewportOrigin = default,
                        Time = ShowClock.Seconds,
                        Now = DateTime.Now,
                        UtcNow = DateTime.UtcNow,
                        Frame = frame++,
                        Sink = SinkKind.Ndi,
                        SinkIndex = 0,
                        SinkLabel = $"NDI {name}",
                        MeasuredFps = sink.Fps.Fps,
                    };
                    sink.Fps.Tick(ctx.Time);

                    _engine.Render(surface.Canvas, snap, in ctx, sink);
                    surface.Canvas.Flush();

                    using (var pixmap = surface.PeekPixels())
                    {
                        if (pixmap is null)
                        {
                            _status = "NDI frame readback failed.";
                            Thread.Sleep(500);
                            continue;
                        }
                        var videoFrame = new NdiInterop.VideoFrameV2
                        {
                            Xres = size.Width,
                            Yres = size.Height,
                            FourCc = NdiInterop.FourCcBgrx,
                            FrameRateN = Math.Max(1, cfg.FrameRateN),
                            FrameRateD = Math.Max(1, cfg.FrameRateD),
                            PictureAspectRatio = 0,
                            FrameFormatType = NdiInterop.FrameFormatProgressive,
                            Timecode = NdiInterop.SendTimecodeSynthesize,
                            Data = pixmap.GetPixels(),
                            LineStrideInBytes = pixmap.RowBytes,
                            Metadata = IntPtr.Zero,
                            Timestamp = 0,
                        };
                        // Clocked send — blocks to hold the configured frame rate.
                        NdiInterop.NDIlib_send_send_video_v2(sender, ref videoFrame);
                    }

                    if ((frame & 0x1F) == 0)
                    {
                        _connections = NdiInterop.NDIlib_send_get_no_connections(sender, 0);
                    }
                    var fps = (double)Math.Max(1, cfg.FrameRateN) / Math.Max(1, cfg.FrameRateD);
                    _status = $"Sending '{currentName}' · {size.Width}×{size.Height} @ {fps:0.##} · {_connections} receiver{(_connections == 1 ? "" : "s")}";
                }
                catch (Exception ex)
                {
                    Log.Error("NDI send loop error — retrying in 2 s.", ex);
                    _status = $"NDI error: {ex.Message} — retrying…";
                    Thread.Sleep(2000);
                }
            }
        }
        finally
        {
            DestroySender(ref sender, ref namePtr);
            surfaceA?.Dispose();
            surfaceB?.Dispose();
            sink.Dispose();
        }
    }

    private static void DestroySender(ref IntPtr sender, ref IntPtr namePtr)
    {
        if (sender != IntPtr.Zero)
        {
            try { NdiInterop.NDIlib_send_destroy(sender); }
            catch (Exception ex) { Log.Warn("NDI sender destroy failed.", ex); }
            sender = IntPtr.Zero;
        }
        if (namePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(namePtr);
            namePtr = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();
}
