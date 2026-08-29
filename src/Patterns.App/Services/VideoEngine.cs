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
/// When libVLC is absent the Media pattern explains how to enable it; nothing crashes.
/// </summary>
public sealed class VideoEngine : IDisposable
{
    private LibVLC? _vlc;
    private bool _vlcInitFailed;
    private VlcFrameSource? _source;
    private string _activeKey = "";

    /// <summary>Reconciles the running decoder with the current snapshot (UI thread).</summary>
    public void Reconcile(ShowSnapshot snap)
    {
        var media = FindActiveVideo(snap.State);
        var key = media is null ? "" : $"{media.VideoPath}|{media.Loop}|{media.Mute}";
        if (key == _activeKey) return;
        _activeKey = key;

        VideoService.Current = null;
        _source?.Dispose();
        _source = null;

        if (media is null) return;

        if (!EnsureVlc()) return;

        try
        {
            _source = new VlcFrameSource(_vlc!, media.VideoPath, media.Loop, media.Mute);
            VideoService.Current = _source;
        }
        catch (Exception ex)
        {
            Log.Error($"Video open failed for '{media.VideoPath}'.", ex);
            VideoService.AvailabilityNote = $"Could not open video: {ex.Message}";
        }
    }

    /// <summary>The media options that should be playing (program first, then independent screens).</summary>
    private static MediaOptions? FindActiveVideo(ShowState state)
    {
        static bool Wants(PatternConfig p) =>
            p.Kind == PatternKind.Media && p.Media.Source == MediaSource.Video &&
            !string.IsNullOrWhiteSpace(p.Media.VideoPath);

        if (Wants(state.Pattern)) return state.Pattern.Media;
        if (state.Output.Mode == OutputMode.Independent)
        {
            foreach (var a in state.Independent)
            {
                if (Wants(a.Pattern)) return a.Pattern.Media;
            }
        }
        return null;
    }

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
        _source?.Dispose();
        _source = null;
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
    private SKBitmap? _front;
    private bool _hasFrame;
    private int _width;
    private int _height;
    private bool _disposed;

    public VlcFrameSource(LibVLC vlc, string path, bool loop, bool mute)
    {
        _media = new Media(vlc, new Uri(Path.GetFullPath(path)));
        if (loop) _media.AddOption("input-repeat=65535");

        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _displayCb = OnDisplay;

        _player = new MediaPlayer(_media)
        {
            Mute = mute,
            EnableHardwareDecoding = true,
        };
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, null, _displayCb);
        _player.Play();
    }

    public SKSizeI? FrameSize
    {
        get
        {
            lock (_gate)
            {
                return _hasFrame ? new SKSizeI(_width, _height) : null;
            }
        }
    }

    public bool IsPlaying => _player.IsPlaying;

    public string StatusText => _player.State switch
    {
        VLCState.Opening => "Opening video…",
        VLCState.Buffering => "Buffering…",
        VLCState.Error => "Video error — check the file.",
        VLCState.Ended => "Video ended.",
        VLCState.Stopped => "Video stopped.",
        _ => "Waiting for first frame…",
    };

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
    {
        lock (_gate)
        {
            if (!_hasFrame || _front is null) return false;
            // Zero-copy wrap of the bitmap pixels; the lock keeps the decoder from writing meanwhile.
            using var pixmap = _front.PeekPixels();
            if (pixmap is null) return false;
            using var image = SKImage.FromPixels(pixmap);
            if (image is null) return false;
            canvas.DrawImage(image, dest, Patterns.Core.Rendering.DrawUtil.Smooth, paint);
            return true;
        }
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
            _front = new SKBitmap(new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            _hasFrame = false;
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
            if (_front is null || _native == IntPtr.Zero) return;
            var dst = (byte*)_front.GetPixels();
            var src = (byte*)_native;
            var rowBytes = _width * 4;
            var dstPitch = _front.RowBytes;
            for (var y = 0; y < _height; y++)
            {
                Buffer.MemoryCopy(src + (long)y * _nativePitch, dst + (long)y * dstPitch, rowBytes, rowBytes);
            }
            _hasFrame = true;
        }
    }

    private void FreeBuffers()
    {
        if (_native != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_native);
            _native = IntPtr.Zero;
        }
        _front?.Dispose();
        _front = null;
        _hasFrame = false;
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
