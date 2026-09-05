using Patterns.App.Services;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Tests;

/// <summary>A stinger voice that never touches a sound card: records what the service did to it.</summary>
internal sealed class FakeVoice : IStingerVoice
{
    public string Path { get; }
    public double VolumePct { get; }
    public bool Playing = true;
    public int ReleasedMs = -1;
    public bool Disposed;
    public double Gain = 1;
    public readonly List<double> Gains = new();

    public FakeVoice(string path, double volumePct)
    {
        Path = path;
        VolumePct = volumePct;
    }

    public bool IsPlaying => Playing && !Disposed;
    public bool Releasing => ReleasedMs >= 0;

    public void SetGain(double gain)
    {
        if (Math.Abs(gain - Gain) > 0.0001) Gains.Add(gain);
        Gain = gain;
    }

    public void Release(int ms) => ReleasedMs = ms;
    public void Dispose() => Disposed = true;
}

/// <summary>A mounted video source that never decodes: records its audio settings and its retirement.</summary>
internal sealed class FakeSource : IMountedSource
{
    public MediaLocator.WantedInput Wanted { get; }
    public bool Mute;
    public double VolumePct;
    public DateTime? FadeStartUtc;
    public int FadeMs = -1;
    public int Pumps;
    public bool Disposed;
    public bool Ended;

    public FakeSource(MediaLocator.WantedInput wanted)
    {
        Wanted = wanted;
        Mute = wanted.Mute;
        VolumePct = wanted.VolumePct;
    }

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint) => false;
    public SKSizeI? FrameSize => null;
    public bool IsPlaying => !Disposed && !Ended;
    public bool IsEnded => Ended;
    public double DurationSeconds => Length;
    public string StatusText => "fake";

    /// <summary>The clip's timeline as a test sets it: its length, where it is, and where it was last told to go.</summary>
    public double Length = 10;
    public double Position;
    public double? SeekedTo;
    public bool Seekable = true;

    public double PositionSeconds => Position;
    public bool CanSeek => Seekable && !Disposed;

    public bool Seek(double seconds)
    {
        if (!CanSeek) return false;
        SeekedTo = seconds;
        Position = Math.Min(seconds, Length);
        Ended = false;
        return true;
    }

    public void SetAudio(bool mute, double volumePct)
    {
        Mute = mute;
        VolumePct = volumePct;
    }

    public void BeginFadeOut(DateTime nowUtc, int ms)
    {
        FadeStartUtc = nowUtc;
        FadeMs = ms;
    }

    public void Pump(DateTime nowUtc) => Pumps++;

    public int AudioDelayMs = int.MinValue;

    public void SetAudioDelay(int ms) => AudioDelayMs = ms;

    public void Dispose() => Disposed = true;
}

/// <summary>Wires both fakes into a booted app and keeps every voice and source it opened.</summary>
internal sealed class AudioFakes
{
    public List<FakeVoice> Voices { get; } = new();
    public List<FakeSource> Sources { get; } = new();

    public static AudioFakes Install(TestApp.Booted b)
    {
        var fakes = new AudioFakes();
        b.Services.AudioPlayer.VoiceFactory = (path, vol) =>
        {
            var v = new FakeVoice(path, vol);
            fakes.Voices.Add(v);
            return v;
        };
        b.Services.Video.SourceFactory = w =>
        {
            var s = new FakeSource(w);
            fakes.Sources.Add(s);
            return s;
        };
        return fakes;
    }

    public static string TempFile(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 1 });
        return path;
    }
}
