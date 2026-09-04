using System.Runtime.InteropServices;
using Patterns.Core.Media;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Ndi;

/// <summary>
/// Discovers NDI® sources on the network. One finder runs for the app's lifetime once the
/// user first looks — NDI discovery is push-based, so an old instance keeps its list fresh.
/// UI-thread use only.
/// </summary>
public sealed class NdiFinder : IDisposable
{
    private IntPtr _instance;

    /// <summary>Current source names ("MACHINE (Sender)"). Empty when NDI is unavailable.</summary>
    public IReadOnlyList<string> CurrentSources()
    {
        if (!NdiInterop.Available) return Array.Empty<string>();
        try
        {
            if (_instance == IntPtr.Zero)
            {
                var create = new NdiInterop.FindCreate { ShowLocalSources = true };
                _instance = NdiInterop.NDIlib_find_create_v2(ref create);
                if (_instance == IntPtr.Zero) return Array.Empty<string>();
            }

            var array = NdiInterop.NDIlib_find_get_current_sources(_instance, out var count);
            if (array == IntPtr.Zero || count == 0) return Array.Empty<string>();

            var size = Marshal.SizeOf<NdiInterop.Source>();
            var names = new List<string>((int)count);
            for (var i = 0; i < count; i++)
            {
                var source = Marshal.PtrToStructure<NdiInterop.Source>(array + i * size);
                var name = Marshal.PtrToStringUTF8(source.NdiName);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception ex)
        {
            Log.Warn("NDI source discovery failed.", ex);
            return Array.Empty<string>();
        }
    }

    public void Dispose()
    {
        if (_instance != IntPtr.Zero)
        {
            try { NdiInterop.NDIlib_find_destroy(_instance); }
            catch (Exception ex) { Log.Warn("NDI finder dispose issue.", ex); }
            _instance = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Receives one NDI® source as BGRA frames on a background thread; renderers composite the
/// newest frame like any video. Same immutable-image + retire-hold lifecycle as file video,
/// so GPU-deferred draws never read freed pixels.
/// </summary>
public sealed class NdiReceiver : IVideoFrameSource, IDisposable
{
    private static readonly int FourCcBgra = 'B' | ('G' << 8) | ('R' << 16) | ('A' << 24);

    private readonly object _gate = new();
    private readonly string _sourceName;
    private readonly Thread _thread;
    private volatile bool _stop;
    private IntPtr _recv;
    private SKImage? _latest;
    private volatile int _framesReceived;
    private long _lastFrameUtcTicks;
    private volatile bool _createFailed;

    private static readonly object RetiredGate = new();
    private static readonly List<(SKImage Image, DateTime RetiredUtc)> Retired = new();
    private static readonly TimeSpan RetireHold = TimeSpan.FromSeconds(2);

    private static void RetireImage(SKImage? image)
    {
        lock (RetiredGate)
        {
            if (image is not null) Retired.Add((image, DateTime.UtcNow));
            var cutoff = DateTime.UtcNow - RetireHold;
            for (var i = Retired.Count - 1; i >= 0; i--)
            {
                if (Retired[i].RetiredUtc < cutoff)
                {
                    Retired[i].Image.Dispose();
                    Retired.RemoveAt(i);
                }
            }
        }
    }

    public NdiReceiver(string sourceName)
    {
        _sourceName = sourceName;
        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "ndi-recv" };
        _thread.Start();
    }

    private void ReceiveLoop()
    {
        var namePtr = NdiInterop.Utf8(_sourceName);
        var recvNamePtr = NdiInterop.Utf8("Patterns receive");
        try
        {
            var create = new NdiInterop.RecvCreateV3
            {
                SourceToConnectTo = new NdiInterop.Source { NdiName = namePtr },
                ColorFormat = NdiInterop.RecvColorFormatBgrxBgra,
                Bandwidth = NdiInterop.RecvBandwidthHighest,
                AllowVideoFields = false,
                RecvName = recvNamePtr,
            };
            _recv = NdiInterop.NDIlib_recv_create_v3(ref create);
            if (_recv == IntPtr.Zero)
            {
                _createFailed = true;
                Log.Warn($"NDI receive create failed for '{_sourceName}'.");
                return;
            }
            Log.Info($"NDI receiving '{_sourceName}'.");

            while (!_stop)
            {
                var frame = default(NdiInterop.VideoFrameV2);
                var type = NdiInterop.NDIlib_recv_capture_v2(_recv, ref frame, IntPtr.Zero, IntPtr.Zero, 250);
                if (type != NdiInterop.FrameTypeVideo) continue;
                try
                {
                    PublishFrame(in frame);
                }
                finally
                {
                    NdiInterop.NDIlib_recv_free_video_v2(_recv, ref frame);
                }
            }
        }
        catch (Exception ex)
        {
            _createFailed = true;
            Log.Error($"NDI receive loop failed for '{_sourceName}'.", ex);
        }
        finally
        {
            if (_recv != IntPtr.Zero)
            {
                try { NdiInterop.NDIlib_recv_destroy(_recv); }
                catch (Exception ex) { Log.Warn("NDI receiver destroy issue.", ex); }
                _recv = IntPtr.Zero;
            }
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(recvNamePtr);
        }
    }

    private unsafe void PublishFrame(in NdiInterop.VideoFrameV2 frame)
    {
        if (frame.Data == IntPtr.Zero || frame.Xres <= 0 || frame.Yres <= 0) return;

        // BGRX_BGRA colour format delivers either fourCC; both are BGRA-layout bytes.
        var alpha = frame.FourCc == FourCcBgra ? SKAlphaType.Unpremul : SKAlphaType.Opaque;
        var bmp = new SKBitmap(new SKImageInfo(frame.Xres, frame.Yres, SKColorType.Bgra8888, alpha));
        var dst = (byte*)bmp.GetPixels();
        var src = (byte*)frame.Data;
        var rowBytes = frame.Xres * 4;
        var dstPitch = bmp.RowBytes;
        for (var y = 0; y < frame.Yres; y++)
        {
            Buffer.MemoryCopy(src + (long)y * frame.LineStrideInBytes, dst + (long)y * dstPitch, rowBytes, rowBytes);
        }
        bmp.SetImmutable();
        var image = SKImage.FromBitmap(bmp);
        bmp.Dispose();

        lock (_gate)
        {
            RetireImage(_latest);
            _latest = image;
        }
        _framesReceived++;
        Interlocked.Exchange(ref _lastFrameUtcTicks, DateTime.UtcNow.Ticks);
    }

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
        => DrawFrame(canvas, dest, paint, FrameCrop.None);

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop)
    {
        SKImage? image;
        lock (_gate)
        {
            image = _latest;
        }
        if (image is null) return false;
        if (crop.Any)
        {
            canvas.DrawImage(image, crop.SourceRect(new SKSizeI(image.Width, image.Height)), dest, Rendering.DrawUtil.Smooth, paint);
        }
        else
        {
            canvas.DrawImage(image, dest, Rendering.DrawUtil.Smooth, paint);
        }
        return true;
    }

    public SKSizeI? FrameSize
    {
        get
        {
            lock (_gate)
            {
                return _latest is { } img ? new SKSizeI(img.Width, img.Height) : null;
            }
        }
    }

    public bool IsPlaying
    {
        get
        {
            var last = Interlocked.Read(ref _lastFrameUtcTicks);
            return last != 0 && DateTime.UtcNow.Ticks - last < TimeSpan.TicksPerSecond * 3;
        }
    }

    public bool IsEnded => false; // live feeds have no natural end

    public double DurationSeconds => 0;

    public string StatusText => _createFailed
        ? "NDI receive failed — is the runtime installed?"
        : _framesReceived == 0
            ? $"Connecting to {_sourceName}…"
            : IsPlaying
                ? "Receiving"
                : "No frames — sender offline?";

    public void Dispose()
    {
        _stop = true;
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            Log.Warn("NDI receive thread did not stop in time.");
        }
        lock (_gate)
        {
            RetireImage(_latest);
            _latest = null;
        }
    }
}
