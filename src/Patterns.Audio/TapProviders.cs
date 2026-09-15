using NAudio.Wave;

namespace Patterns.Audio;

/// <summary>A tap read into a mixer lane: the ring's samples at a gain that lands over a block, never a step.</summary>
public sealed class TapSampleProvider : ISampleProvider
{
    private readonly AudioRing _ring;
    private readonly AudioRing.Reader _reader;
    private float _gain;
    private volatile float _target;

    public TapSampleProvider(AudioRing ring, int latencyFrames, float gain = 0f)
    {
        _ring = ring;
        _reader = ring.OpenReader(latencyFrames);
        _gain = _target = gain;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(AudioMix.Rate, ring.Channels);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Where the gain heads: landed over the next block.</summary>
    public float Target
    {
        get => _target;
        set => _target = Math.Clamp(value, 0f, 4f);
    }

    public long Underruns => _reader.Underruns;
    public long Snaps => _reader.Snaps;

    public int Read(float[] buffer, int offset, int count)
    {
        _ring.Read(_reader, buffer.AsSpan(offset, count));
        var target = _target;
        var gain = _gain;
        if (Math.Abs(target - gain) < 1e-5f)
        {
            if (gain != 1f)
            {
                var span = buffer.AsSpan(offset, count);
                for (var i = 0; i < span.Length; i++) span[i] *= gain;
            }
            return count;
        }
        var channels = Math.Max(1, WaveFormat.Channels);
        var frames = count / channels;
        var step = (target - gain) / Math.Max(1, frames);
        for (var f = 0; f < frames; f++)
        {
            gain += step;
            for (var c = 0; c < channels; c++) buffer[offset + f * channels + c] *= gain;
        }
        _gain = target;
        return count;
    }
}

/// <summary>Copies what passes through into a ring — the show's own sound tapped for the NDI lanes — and remembers its peak for a meter.</summary>
public sealed class TeeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float _peak;

    public TeeSampleProvider(ISampleProvider source, AudioRing? tap)
    {
        _source = source;
        Tap = tap;
    }

    public AudioRing? Tap { get; }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>The loudest sample since the meter was last read (0–1+), then it starts again.</summary>
    public float TakePeak() => Interlocked.Exchange(ref _peak, 0f);

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;
        var span = buffer.AsSpan(offset, read);
        var peak = 0f;
        for (var i = 0; i < span.Length; i++)
        {
            var a = Math.Abs(span[i]);
            if (a > peak) peak = a;
        }
        if (peak > _peak) _peak = peak;
        Tap?.Write(span);
        return read;
    }
}
