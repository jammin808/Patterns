using System.Runtime.InteropServices;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Ndi;

/// <summary>
/// One advertised NDI source: renders its configured view of the show (program or a
/// specific screen's pattern) to an offscreen surface on a dedicated thread and hands
/// frames to the NDI runtime. Clocked sends pace the loop to the configured frame rate.
/// 8-bit sends BGRX; 10-bit renders RGBA-1010102 and sends P216.
/// </summary>
public sealed class NdiSender : IDisposable
{
    private const int FourCcP216 = 'P' | ('2' << 8) | ('1' << 16) | ('6' << 24);

    private readonly SnapshotBus _bus;
    private readonly string _senderId;
    private readonly PatternEngine _engine = new();

    private Thread? _thread;
    private volatile bool _run;
    private volatile string _status = "Off";
    private volatile int _connections;

    public NdiSender(SnapshotBus bus, string senderId)
    {
        _bus = bus;
        _senderId = senderId;
    }

    public string SenderId => _senderId;
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
        NdiInterop.ReprobeIfUnavailable();
        if (!NdiInterop.Available)
        {
            _status = RuntimeHelp;
            return;
        }
        _run = true;
        _thread = new Thread(SendLoop)
        {
            Name = $"ndi-{_senderId[..Math.Min(8, _senderId.Length)]}",
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
            Log.Warn($"NDI sender thread '{_senderId}' did not stop in time.");
        }
        _status = "Off";
        _connections = 0;
    }

    private NdiSenderConfig? FindConfig(ShowSnapshot snap)
        => snap.State.Ndi.Senders.FirstOrDefault(s => s.Id == _senderId);

    private void SendLoop()
    {
        var sink = new SinkState();
        IntPtr sender = IntPtr.Zero;
        IntPtr namePtr = IntPtr.Zero;
        IntPtr p216Buffer = IntPtr.Zero;
        var p216Capacity = 0;
        SKSurface? surfaceA = null, surfaceB = null;
        var useA = true;
        long frame = 0;
        string currentName = "";
        var currentSize = SKSizeI.Empty;
        var currentTenBit = false;
        var tenBitUnavailable = false;

        try
        {
            while (_run)
            {
                try
                {
                    var snap = _bus.Current;
                    var cfg = FindConfig(snap);
                    if (cfg is null || !cfg.Enabled)
                    {
                        // Config vanished or was disabled — the service will Stop() us; idle briefly.
                        Thread.Sleep(100);
                        continue;
                    }

                    var name = string.IsNullOrWhiteSpace(cfg.Name) ? "Patterns" : cfg.Name.Trim();
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
                            _status = $"Could not create NDI sender '{name}' (name in use?) — retrying…";
                            Thread.Sleep(2000);
                            continue;
                        }
                        currentName = name;
                        Log.Info($"NDI sender '{name}' created.");
                    }

                    var size = new SKSizeI(Math.Max(16, cfg.Width), Math.Max(16, cfg.Height));
                    var wantTenBit = cfg.TenBit && !tenBitUnavailable;
                    if (surfaceA is null || size != currentSize || wantTenBit != currentTenBit)
                    {
                        surfaceA?.Dispose();
                        surfaceB?.Dispose();
                        surfaceA = surfaceB = null;

                        if (wantTenBit)
                        {
                            var info10 = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba1010102, SKAlphaType.Opaque);
                            surfaceA = SKSurface.Create(info10);
                            surfaceB = SKSurface.Create(info10);
                            if (surfaceA is null || surfaceB is null)
                            {
                                Log.Warn("10-bit surfaces unavailable — falling back to 8-bit for this sender.");
                                tenBitUnavailable = true;
                                wantTenBit = false;
                                surfaceA?.Dispose();
                                surfaceB?.Dispose();
                                surfaceA = surfaceB = null;
                            }
                        }

                        if (surfaceA is null)
                        {
                            var info8 = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                            surfaceA = SKSurface.Create(info8);
                            surfaceB = SKSurface.Create(info8);
                        }
                        if (surfaceA is null || surfaceB is null)
                        {
                            _status = $"Could not allocate {size.Width}×{size.Height} NDI frame buffers.";
                            Thread.Sleep(2000);
                            continue;
                        }

                        if (wantTenBit)
                        {
                            var needed = size.Width * size.Height * 2 * 2; // Y + CbCr planes, 2 bytes each
                            if (p216Buffer == IntPtr.Zero || p216Capacity < needed)
                            {
                                if (p216Buffer != IntPtr.Zero) Marshal.FreeHGlobal(p216Buffer);
                                p216Buffer = Marshal.AllocHGlobal(needed);
                                p216Capacity = needed;
                            }
                        }

                        currentSize = size;
                        currentTenBit = wantTenBit;
                    }

                    var surface = useA ? surfaceA : surfaceB!;
                    useA = !useA;

                    var time = ShowClock.Seconds;
                    sink.Fps.Tick(time);
                    // The program fills the frame; a mirrored target keeps its shape; the sender's own screen fills it.
                    NdiFrame.Render(_engine, snap, sink, surface.Canvas, size, cfg.SourceScreenId, SinkKind.Ndi, $"NDI {name}", frame++, time);
                    surface.Canvas.Flush();

                    var (rateN, rateD) = NdiRateTable.Resolve(cfg.RateKey, snap.State.Output.MasterFps);
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
                            FrameRateN = rateN,
                            FrameRateD = rateD,
                            PictureAspectRatio = 0,
                            FrameFormatType = NdiInterop.FrameFormatProgressive,
                            Timecode = NdiInterop.SendTimecodeSynthesize,
                            Metadata = IntPtr.Zero,
                            Timestamp = 0,
                        };

                        if (currentTenBit)
                        {
                            P216Converter.ConvertFrame(pixmap.GetPixels(), pixmap.RowBytes, size.Width, size.Height, p216Buffer);
                            videoFrame.FourCc = FourCcP216;
                            videoFrame.Data = p216Buffer;
                            videoFrame.LineStrideInBytes = size.Width * 2;
                        }
                        else
                        {
                            videoFrame.FourCc = NdiInterop.FourCcBgrx;
                            videoFrame.Data = pixmap.GetPixels();
                            videoFrame.LineStrideInBytes = pixmap.RowBytes;
                        }

                        // Clocked send — blocks to hold the configured frame rate.
                        NdiInterop.NDIlib_send_send_video_v2(sender, ref videoFrame);
                    }

                    if ((frame & 0x1F) == 0)
                    {
                        _connections = NdiInterop.NDIlib_send_get_no_connections(sender, 0);
                    }
                    var fps = (double)rateN / rateD;
                    var depth = currentTenBit ? "10-bit" : "8-bit";
                    _status = $"'{currentName}' · {size.Width}×{size.Height} @ {fps:0.##} {depth} · {_connections} receiver{(_connections == 1 ? "" : "s")}";
                }
                catch (Exception ex)
                {
                    Log.Error($"NDI send loop '{_senderId}' error — retrying in 2 s.", ex);
                    _status = $"NDI error: {ex.Message} — retrying…";
                    Thread.Sleep(2000);
                }
            }
        }
        finally
        {
            DestroySender(ref sender, ref namePtr);
            if (p216Buffer != IntPtr.Zero) Marshal.FreeHGlobal(p216Buffer);
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

/// <summary>Keeps the set of running sender threads in sync with the configured senders.</summary>
public sealed class NdiService : IDisposable
{
    private readonly SnapshotBus _bus;
    private readonly Dictionary<string, NdiSender> _active = new();

    public NdiService(SnapshotBus bus)
    {
        _bus = bus;
    }

    public int ActiveCount => _active.Count;

    public void Reconcile(ShowSnapshot snap)
    {
        // Prep is pre-programming: nothing leaves the machine, on a cable or on the network.
        var desired = snap.State.Mode == ShowMode.Prep
            ? new HashSet<string>()
            : snap.State.Ndi.Senders.Where(s => s.Enabled).Select(s => s.Id).ToHashSet();

        foreach (var id in _active.Keys.Where(id => !desired.Contains(id)).ToList())
        {
            _active[id].Stop();
            _active.Remove(id);
        }

        foreach (var id in desired)
        {
            if (!_active.ContainsKey(id))
            {
                var sender = new NdiSender(_bus, id);
                sender.Start();
                _active[id] = sender;
            }
        }
    }

    public string StatusFor(string id)
        => _active.TryGetValue(id, out var s) ? s.Status : NdiSender.RuntimeAvailable ? "Off" : NdiSender.RuntimeHelp;

    public void StopAll()
    {
        foreach (var s in _active.Values)
        {
            s.Stop();
        }
        _active.Clear();
    }

    public void Dispose() => StopAll();
}
