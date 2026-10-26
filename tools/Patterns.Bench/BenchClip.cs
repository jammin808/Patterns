using System.Globalization;
using Patterns.App.Services;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Bench;

/// <summary>
/// A clip with no decoder: a wall-clock playhead on the show clock (<see cref="ShowClock"/>) over a length its file
/// states, drawn as a solid colour with its position, and silent. It plays, pauses, seeks, stops (a seek to the top and a
/// pause), holds at its start for the standby and ends at its length as a decoded clip does, so the desk's transport,
/// STATE's video row and the clock another app counts down read it as they read a real one. A file the bench did not
/// write has no length it can state: the clip plays with none, and STATE says so (<c>lengthMs</c> null).
/// </summary>
internal sealed class BenchClip : IMountedSource
{
    /// <summary>The first word of a bench clip's file; the second is its length in seconds.</summary>
    public const string Marker = "patterns-bench-clip";

    private static readonly SKSizeI Size = new(1280, 720);

    private readonly object _gate = new();
    private readonly double _length;
    private readonly bool _loop;
    private readonly string _name;
    private double _anchorPosition;
    private double _anchorClock;
    private bool _paused;
    private bool _held;
    private bool _disposed;

    public BenchClip(string name, double lengthSeconds, bool loop)
    {
        _name = name;
        _length = Math.Max(0, lengthSeconds);
        _loop = loop;
        _anchorClock = ShowClock.Seconds;
    }

    /// <summary>The words a bench clip's file holds for a length.</summary>
    public static string FileText(double seconds) => string.Create(CultureInfo.InvariantCulture, $"{Marker} {seconds:0.###}\n");

    /// <summary>The length a file states, or 0 when it is not a bench clip's (or cannot be read).</summary>
    public static double LengthOf(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var first = reader.ReadLine() ?? "";
            var words = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 2 && words[0] == Marker
                   && double.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s) && s > 0
                ? s : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Where the playhead is now (under the gate).</summary>
    private double PositionLocked()
    {
        if (_paused || _held || _disposed) return _anchorPosition;
        var at = _anchorPosition + (ShowClock.Seconds - _anchorClock);
        if (_length <= 0) return at;
        return _loop ? at % _length : Math.Min(at, _length);
    }

    private bool EndedLocked() => !_loop && _length > 0 && PositionLocked() >= _length;

    /// <summary>The playhead re-anchored at <paramref name="position"/> now (under the gate).</summary>
    private void AnchorLocked(double position)
    {
        _anchorPosition = position;
        _anchorClock = ShowClock.Seconds;
    }

    public double PositionSeconds
    {
        get
        {
            lock (_gate) return PositionLocked();
        }
    }

    public double DurationSeconds => _length;

    public bool IsEnded
    {
        get
        {
            lock (_gate) return EndedLocked();
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_gate) return !_disposed && !_paused && !_held && !EndedLocked();
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate) return _paused && !_disposed && !EndedLocked();
        }
    }

    public bool CanSeek
    {
        get
        {
            lock (_gate) return !_disposed;
        }
    }

    public double Rate => 1;

    public string StatusText
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return "closed";
                if (_held) return "synthetic clip, held at its start";
                if (EndedLocked()) return "synthetic clip, ended";
                return _paused ? "synthetic clip, paused" : "synthetic clip";
            }
        }
    }

    public SKSizeI? FrameSize => Size;

    public bool Seek(double seconds)
    {
        lock (_gate)
        {
            if (_disposed || !double.IsFinite(seconds)) return false;
            AnchorLocked(Math.Clamp(seconds, 0, _length > 0 ? _length : double.MaxValue));
            return true;
        }
    }

    /// <summary>An operator's pause or play; refused for a clip held for the standby and for an ended one, as the decoder refuses them.</summary>
    public bool SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_disposed || _held || EndedLocked()) return false;
            if (_paused == paused) return true;
            AnchorLocked(PositionLocked());
            _paused = paused;
            return true;
        }
    }

    public void HoldAtStart()
    {
        lock (_gate)
        {
            _held = true;
            _paused = false;
            AnchorLocked(0);
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (!_held) return;
            _held = false;
            AnchorLocked(0);
        }
    }

    public bool IsHeld
    {
        get
        {
            lock (_gate) return _held && !_disposed;
        }
    }

    /// <summary>The clip is silent: there is no sound to set, fade or route.</summary>
    public void SetAudio(bool mute, double volumePct)
    {
        // a synthetic clip has no soundtrack
    }

    public void BeginFadeOut(DateTime nowUtc, int ms)
    {
        // a synthetic clip has no soundtrack to fade
    }

    public void Pump(DateTime nowUtc)
    {
        // nothing ramps: the clip is silent
    }

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
    {
        double position;
        double fraction;
        lock (_gate)
        {
            if (_disposed) return false;
            position = PositionLocked();
            fraction = _length > 0 ? Math.Clamp(position / _length, 0, 1) : 0;
        }
        using var fill = new SKPaint { Color = new SKColor(0x20, 0x30, 0x48), IsAntialias = false };
        canvas.DrawRect(dest, fill);
        // The position as a bar along the foot of the frame, and in words above it.
        var bar = new SKRect(dest.Left, dest.Bottom - dest.Height * 0.06f, dest.Left + dest.Width * (float)fraction, dest.Bottom);
        using var ink = new SKPaint { Color = new SKColor(0xF0, 0xF0, 0xF0), IsAntialias = true };
        canvas.DrawRect(bar, ink);
        using var font = new SKFont { Size = Math.Max(12f, dest.Height * 0.08f) };
        var words = string.Create(CultureInfo.InvariantCulture, $"{_name}  {VideoClock.Format(position)}{(_length > 0 ? " / " + VideoClock.Format(_length) : "")}");
        canvas.DrawText(words, dest.Left + dest.Width * 0.04f, dest.Bottom - dest.Height * 0.12f, font, ink);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            AnchorLocked(PositionLocked());
            _disposed = true;
        }
    }
}

/// <summary>
/// The bench's clips: where their files are written, and the desk's source factory (<see cref="VideoEngine.SourceFactory"/>).
/// With synthetic clips every file the desk wants opens as a <see cref="BenchClip"/>; without them nothing opens. A camera,
/// a feed or a page is never opened here — the bench has none, and libVLC is off in its process.
/// </summary>
internal sealed class BenchClips
{
    public BenchClips(string home, bool synthetic)
    {
        Folder = Path.Combine(home, "bench-clips");
        Synthetic = synthetic;
    }

    /// <summary>The folder the bench writes its clips' files to.</summary>
    public string Folder { get; }

    /// <summary>Whether this bench opens clips at all.</summary>
    public bool Synthetic { get; }

    /// <summary>The ready line's word for the clips: "synthetic", or "none".</summary>
    public string Word => Synthetic ? "synthetic" : "none";

    /// <summary>Writes a clip's file of <paramref name="seconds"/> and returns its path.</summary>
    public string Write(string name, double seconds)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name);
        AtomicFile.WriteAllText(path, BenchClip.FileText(seconds));
        return path;
    }

    /// <summary>The source for one wanted input: a synthetic clip for a file, nothing for anything else or with clips off.</summary>
    public IMountedSource? Open(MediaLocator.WantedInput wanted)
    {
        if (!Synthetic || wanted.Kind != MediaLocator.WantedKind.VideoFile) return null;
        return new BenchClip(Path.GetFileName(wanted.Target), BenchClip.LengthOf(wanted.Target), wanted.Loop);
    }
}
