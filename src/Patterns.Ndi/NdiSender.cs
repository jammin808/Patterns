using System.Runtime.InteropServices;
using Patterns.Core.Model;
using Patterns.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Ndi;

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
    private readonly ManualResetEventSlim _stop = new(false);   // set by RequestStop: every pause in the loop ends at once
    private volatile string _status = "Off";
    private volatile int _connections;

    // The runtime's sender handle, shared with the audio side: video goes from the send loop,
    // audio from the graph's lane on a thread of its own, and the runtime takes both at once.
    private readonly object _handleGate = new();
    private IntPtr _handle;
    private IntPtr _audioBuffer;
    private int _audioCapacity;
    private long _audioFrames;

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
        _stop.Reset();
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
        RequestStop();
        AwaitStop(DateTime.UtcNow.AddSeconds(3));
    }

    /// <summary>Tells the send loop to end after its frame; <see cref="AwaitStop"/> waits for it. Split so several senders stop side by side.</summary>
    public void RequestStop()
    {
        _run = false;
        _stop.Set();
    }

    /// <summary>A wait the stop cuts short: the loop's idle, retry and error pauses never outlive a Stop().</summary>
    private void Pause(int ms)
    {
        try { _stop.Wait(ms); }
        catch (ObjectDisposedException) { _run = false; }   // disposed under the loop: it ends at its next check
    }

    /// <summary>Waits for the send loop until <paramref name="deadlineUtc"/>; a loop still running then is left to end on its own and noted.</summary>
    public void AwaitStop(DateTime deadlineUtc)
    {
        var t = _thread;
        _thread = null;
        if (t is not null && t.IsAlive)
        {
            var left = deadlineUtc - DateTime.UtcNow;
            if (left < TimeSpan.Zero || !t.Join(left))
            {
                Log.Warn($"NDI sender thread '{_senderId}' did not stop in time.");
            }
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
                    var pair = _bus.Pair;   // round 79: the programme and the sandbox of one generation
                    var snap = pair.Current;
                    var cfg = FindConfig(snap);
                    if (cfg is null || !cfg.Enabled)
                    {
                        // Config vanished or was disabled — the service will Stop() us; idle briefly.
                        Pause(100);
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
                            Pause(2000);
                            continue;
                        }
                        lock (_handleGate) _handle = sender;
                        currentName = name;
                        Log.Info($"NDI sender '{name}' created.");
                    }

                    var size = new SKSizeI(Math.Max(16, cfg.Width), Math.Max(16, cfg.Height));
                    var wantTenBit = cfg.TenBit && !tenBitUnavailable;
                    // P216 carries one Cb/Cr pair per two pixels: an odd width has no room for the
                    // last column's chroma, so a 10-bit send is always an even width.
                    if (wantTenBit && (size.Width & 1) == 1) size.Width -= 1;
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
                            Pause(2000);
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
                    NdiFrame.Render(_engine, snap, sink, surface.Canvas, size, cfg.SourceScreenId, SinkKind.Ndi, $"NDI {name}", frame++, time, pair.Sandbox);
                    surface.Canvas.Flush();

                    var (rateN, rateD) = NdiRateTable.Resolve(cfg.RateKey, snap.State.Output.MasterFps);
                    using (var pixmap = surface.PeekPixels())
                    {
                        if (pixmap is null)
                        {
                            _status = "NDI frame readback failed.";
                            Pause(500);
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
                    Pause(2000);
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

    /// <summary>Audio frames sent on this sender so far — the Audio page's sign that the embedded sound is moving.</summary>
    public long AudioFrames => Interlocked.Read(ref _audioFrames);

    /// <summary>
    /// Embedded audio: interleaved 32-bit float frames at a rate, turned planar for the runtime
    /// and sent at once (the runtime copies them). Safe from any thread; nothing happens while
    /// the sender is not up, and false says so.
    /// </summary>
    public bool SendAudio(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        if (channels <= 0 || sampleRate <= 0) return false;
        var frames = interleaved.Length / channels;
        if (frames <= 0) return false;
        lock (_handleGate)
        {
            if (_handle == IntPtr.Zero) return false;
            var needed = frames * channels * sizeof(float);
            if (_audioBuffer == IntPtr.Zero || _audioCapacity < needed)
            {
                if (_audioBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_audioBuffer);
                _audioBuffer = Marshal.AllocHGlobal(needed);
                _audioCapacity = needed;
            }
            unsafe
            {
                var dst = (float*)_audioBuffer;
                for (var ch = 0; ch < channels; ch++)
                {
                    var plane = dst + ch * frames;
                    for (var i = 0; i < frames; i++) plane[i] = interleaved[i * channels + ch];
                }
            }
            var frame = new NdiInterop.AudioFrameV3
            {
                SampleRate = sampleRate,
                NoChannels = channels,
                NoSamples = frames,
                Timecode = NdiInterop.SendTimecodeSynthesize,
                FourCc = NdiInterop.FourCcFltp,
                Data = _audioBuffer,
                ChannelStrideInBytes = frames * sizeof(float),
                Metadata = IntPtr.Zero,
                Timestamp = 0,
            };
            try
            {
                NdiInterop.NDIlib_send_send_audio_v3(_handle, ref frame);
            }
            catch (Exception ex)
            {
                Log.Warn($"NDI audio send failed on '{_senderId}'.", ex);
                return false;
            }
            Interlocked.Increment(ref _audioFrames);
            return true;
        }
    }

    private void DestroySender(ref IntPtr sender, ref IntPtr namePtr)
    {
        if (sender != IntPtr.Zero)
        {
            lock (_handleGate)
            {
                _handle = IntPtr.Zero;
                try { NdiInterop.NDIlib_send_destroy(sender); }
                catch (Exception ex) { Log.Warn("NDI sender destroy failed.", ex); }
                if (_audioBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_audioBuffer);
                    _audioBuffer = IntPtr.Zero;
                    _audioCapacity = 0;
                }
            }
            sender = IntPtr.Zero;
        }
        if (namePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(namePtr);
            namePtr = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }
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

    /// <summary>Round 78: the senders running now, by id — the desk reads each one's source to know which picture leaves the machine on it.</summary>
    public IReadOnlyCollection<string> ActiveIds => _active.Keys;

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

    /// <summary>The running sender for a config id, or null — the audio graph's lane hands it the embedded sound.</summary>
    public NdiSender? SenderFor(string id) => _active.TryGetValue(id, out var s) ? s : null;

    /// <summary>Stops every sender side by side: all are told at once, then waited for together, so the exit costs one sender's stop, not the sum.</summary>
    public void StopAll()
    {
        foreach (var s in _active.Values) s.RequestStop();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        foreach (var s in _active.Values) s.AwaitStop(deadline);
        _active.Clear();
    }

    public void Dispose() => StopAll();
}
