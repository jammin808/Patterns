using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// Optional video decode via libVLC with callback rendering: frames land in a BGRA buffer
/// that the engine composites like any layer — so video reaches outputs, spans and NDI.
/// Also opens DirectShow capture devices (HDMI/SDI cards, webcams) through the same path.
/// When libVLC is absent the Media pattern explains how to enable it; nothing crashes.
/// </summary>
public sealed class VideoEngine : IDisposable
{
    private LibVLC? _vlc;
    private bool _vlcInitFailed;
    private VlcFrameSource? _source;
    private string _activeKey = "";
    private (VlcFrameSource Source, DateTime RetiredUtc)? _retired;

    /// <summary>Reconciles the running decoder with the current snapshot (UI thread).</summary>
    public void Reconcile(ShowSnapshot snap)
    {
        SweepRetired();

        var media = MediaLocator.FindActiveVideo(snap);

        // Mute/volume apply live to the running player — they never restart the media.
        var key = media is null ? "" : $"{media.Value.Target}|{media.Value.Loop}|{media.Value.IsCapture}";
        if (key == _activeKey)
        {
            if (media is not null) _source?.SetAudio(media.Value.Mute, media.Value.VolumePct);
            return;
        }
        _activeKey = key;

        VideoService.Current = null;
        RetireSource();

        if (media is null) return;

        if (!EnsureVlc()) return;

        try
        {
            _source = new VlcFrameSource(_vlc!, media.Value.Target, media.Value.Loop, media.Value.IsCapture,
                media.Value.Mute, media.Value.VolumePct);
            VideoService.Current = _source;
        }
        catch (Exception ex)
        {
            Log.Error($"Video open failed for '{media.Value.Target}'.", ex);
            VideoService.AvailabilityNote = $"Could not open video: {ex.Message}";
            // Forget the key so the next state change retries (the file may just not be ready yet).
            _activeKey = "";
        }
    }

    /// <summary>
    /// The old source keeps decoding briefly as <see cref="VideoService.Previous"/> so a
    /// crossfade fades out real frames instead of a placeholder; muted so two soundtracks
    /// never overlap; swept a couple of seconds later.
    /// </summary>
    private void RetireSource()
    {
        if (_retired is { } r)
        {
            r.Source.Dispose();
            _retired = null;
        }
        VideoService.Previous = null;
        if (_source is not null)
        {
            _source.SetAudio(mute: true, volumePct: 0);
            VideoService.Previous = _source;
            _retired = (_source, DateTime.UtcNow);
            _source = null;
        }
    }

    /// <summary>Also called from the app's 1 s poll so a retired decoder never lingers.</summary>
    public void SweepRetired()
    {
        if (_retired is { } r && DateTime.UtcNow - r.RetiredUtc > TimeSpan.FromSeconds(4))
        {
            VideoService.Previous = null;
            r.Source.Dispose();
            _retired = null;
        }
    }

    /// <summary>Whether video decode is available (initialises libVLC on first ask).</summary>
    public bool EnsureAvailable() => EnsureVlc();

    /// <summary>The shared libVLC instance for secondary players (PiP); null when unavailable.</summary>
    public LibVLC? SharedVlc => EnsureVlc() ? _vlc : null;

    private bool EnsureVlc()
    {
        if (_vlc is not null) return true;
        if (_vlcInitFailed) return false;

        foreach (var dir in CandidateDirs())
        {
            try
            {
                if (dir is not null && !Directory.Exists(dir)) continue;
                LibVLCSharp.Shared.Core.Initialize(dir);
                _vlc = new LibVLC("--no-video-title-show", "--quiet");
                Log.Info($"libVLC initialised ({dir ?? "default probe"}).");
                VideoService.AvailabilityNote = "";
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"libVLC init failed for '{dir ?? "default"}': {ex.Message}");
            }
        }

        _vlcInitFailed = true;
        VideoService.AvailabilityNote =
            "Video needs libVLC: install 64-bit VLC, or put a 'libvlc' folder (libvlc.dll + plugins) beside Patterns.exe.";
        Log.Warn(VideoService.AvailabilityNote);
        return false;
    }

    private static IEnumerable<string?> CandidateDirs()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, "libvlc", "win-x64");
        yield return Path.Combine(exeDir, "libvlc");
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(pf, "VideoLAN", "VLC");
        }
        yield return null; // library default probing
    }

    public void Dispose()
    {
        VideoService.Current = null;
        VideoService.Previous = null;
        _source?.Dispose();
        _source = null;
        if (_retired is { } r)
        {
            r.Source.Dispose();
            _retired = null;
        }
        _vlc?.Dispose();
        _vlc = null;
    }
}

/// <summary>One playing video: libVLC decodes into our BGRA buffer; renderers draw the newest frame.</summary>
public sealed class VlcFrameSource : IVideoFrameSource, IDisposable
{
    private readonly object _gate = new();
    private readonly Media _media;
    private readonly MediaPlayer _player;

    // Keep delegate instances alive for the lifetime of the callbacks.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    private IntPtr _native;
    private int _nativePitch;
    private SKImage? _latest;
    private int _width;
    private int _height;
    private bool _disposed;
    private volatile bool _mute;
    private volatile float _volumePct;

    // Frames handed to render sinks may be recorded into GPU-deferred canvases that read the
    // pixels at flush time — so each decoded frame becomes its own immutable SKImage, and
    // superseded frames are retired for a grace period instead of disposed immediately.
    // Static so a successor source still sweeps a disposed predecessor's leftovers.
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

    /// <summary>Media options for a DirectShow capture device. Pure — unit tested.</summary>
    public static string[] CaptureOptions(string deviceName) => new[]
    {
        $":dshow-vdev={deviceName}",
        ":dshow-adev=none",     // programme audio routing stays with the desk, not the display PC
        ":dshow-aspect-ratio=", // native
        ":live-caching=80",     // low-latency for confidence monitoring
    };

    public VlcFrameSource(LibVLC vlc, string target, bool loop, bool isCapture, bool mute, double volumePct)
    {
        if (isCapture)
        {
            _media = new Media(vlc, "dshow://", FromType.FromLocation);
            foreach (var opt in CaptureOptions(target))
            {
                _media.AddOption(opt);
            }
        }
        else
        {
            _media = new Media(vlc, new Uri(Path.GetFullPath(target)));
            if (loop) _media.AddOption("input-repeat=65535");
        }

        _mute = mute;
        _volumePct = (float)volumePct;

        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _displayCb = OnDisplay;

        _player = new MediaPlayer(_media)
        {
            EnableHardwareDecoding = true,
        };
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, null, _displayCb);

        // Audio state set before the audio output exists can be lost — (re)apply once
        // playback has actually started, and again on every later change.
        _player.Playing += (_, _) => ApplyAudio();
        _player.Play();
        ApplyAudio();
    }

    /// <summary>Live mute/volume — never restarts the media.</summary>
    public void SetAudio(bool mute, double volumePct)
    {
        if (_mute == mute && Math.Abs(_volumePct - volumePct) < 0.5) return;
        _mute = mute;
        _volumePct = (float)volumePct;
        ApplyAudio();
    }

    private void ApplyAudio()
    {
        if (_disposed) return;
        try
        {
            _player.Mute = _mute;
            _player.Volume = (int)Math.Clamp(_volumePct, 0, 125);
        }
        catch (Exception ex)
        {
            Log.Warn("Applying audio state failed.", ex);
        }
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

    public bool IsPlaying => _player.IsPlaying;

    public bool IsEnded => _player.State == VLCState.Ended;

    public double DurationSeconds
    {
        get
        {
            var ms = _player.Length;
            return ms > 0 ? ms / 1000.0 : 0;
        }
    }

    public string StatusText => _player.State switch
    {
        VLCState.Opening => "Opening…",
        VLCState.Buffering => "Buffering…",
        VLCState.Error => "Playback error — check the file or device.",
        VLCState.Ended => "Ended.",
        VLCState.Stopped => "Stopped.",
        VLCState.Playing => "Playing (no picture yet)…",
        _ => "Waiting for first frame…",
    };

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
    {
        SKImage? image;
        lock (_gate)
        {
            image = _latest;
        }
        if (image is null) return false;
        // The image is immutable and outlives any deferred flush via the retire hold.
        canvas.DrawImage(image, dest, Patterns.Core.Rendering.DrawUtil.Smooth, paint);
        return true;
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // Ask VLC for BGRA at the native size.
        Marshal.Copy(new[] { (byte)'B', (byte)'G', (byte)'R', (byte)'A' }, 0, chroma, 4);
        var pitch = ((width * 4) + 31) & ~31u;
        pitches = pitch;
        lines = height;

        lock (_gate)
        {
            FreeBuffers();
            _width = (int)width;
            _height = (int)height;
            _nativePitch = (int)pitch;
            _native = Marshal.AllocHGlobal(_nativePitch * _height);
        }
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_gate)
        {
            FreeBuffers();
        }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _native);
        return IntPtr.Zero;
    }

    private unsafe void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_gate)
        {
            if (_native == IntPtr.Zero || _width <= 0) return;

            // Every displayed frame becomes its own immutable image (native-heap copy, no GC
            // pressure); renderers can hold/record it safely while VLC decodes the next one.
            var bmp = new SKBitmap(new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            var dst = (byte*)bmp.GetPixels();
            var src = (byte*)_native;
            var rowBytes = _width * 4;
            var dstPitch = bmp.RowBytes;
            for (var y = 0; y < _height; y++)
            {
                Buffer.MemoryCopy(src + (long)y * _nativePitch, dst + (long)y * dstPitch, rowBytes, rowBytes);
            }
            bmp.SetImmutable();
            var image = SKImage.FromBitmap(bmp);
            bmp.Dispose(); // the image keeps the (immutable) pixel ref alive

            RetireImage(_latest);
            _latest = image;
        }
    }

    private void FreeBuffers()
    {
        if (_native != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_native);
            _native = IntPtr.Zero;
        }
        RetireImage(_latest);
        _latest = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _player.Stop();
            _player.Dispose();
            _media.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Video source dispose issue.", ex);
        }
        lock (_gate)
        {
            FreeBuffers();
        }
    }
}
