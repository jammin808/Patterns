using NAudio.Wave;
using Patterns.Core.Audio;

namespace Patterns.App.Services;

/// <summary>
/// The sample-rate converter as a stage in a NAudio chain: the source's frames come out at the
/// ratio the sync lock asks for, and the counters say how far the source has gone against what
/// the device has been handed.
/// </summary>
public sealed class AsrcSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly SampleRateConverter _asrc;
    private readonly Func<float[], int, int, int> _reader;
    private double _target = 1;

    public AsrcSampleProvider(ISampleProvider source)
    {
        _source = source;
        _asrc = new SampleRateConverter(source.WaveFormat.Channels);
        var channels = Math.Max(1, source.WaveFormat.Channels);
        _reader = (buffer, offset, frames) =>
        {
            var read = _source.Read(buffer, offset, frames * channels);
            return read / channels;
        };
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Output frames per input frame; landed over the next reads, never a jump.</summary>
    public double Ratio
    {
        get => _target;
        set => _target = Math.Clamp(value, 0.5, 2);
    }

    public double RatioInForce => _asrc.Ratio;

    public long InputFramesConsumed => _asrc.InputFramesConsumed;

    public long OutputFramesProduced => _asrc.OutputFramesProduced;

    public int Read(float[] buffer, int offset, int count)
    {
        var channels = Math.Max(1, WaveFormat.Channels);
        // A ratio change of a few hundred ppm lands in a couple of blocks; nothing audible.
        _asrc.Ratio += (_target - _asrc.Ratio) * 0.25;
        var frames = _asrc.Read(buffer, offset, count / channels, _reader);
        return frames * channels;
    }
}

/// <summary>The lip-sync offset as a stage: what comes out now went in a set time ago.</summary>
public sealed class DelaySampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly DelayBuffer _delay;

    public DelaySampleProvider(ISampleProvider source, int delayMs)
    {
        _source = source;
        _delay = new DelayBuffer(DelayBuffer.FramesFor(delayMs, source.WaveFormat.SampleRate), source.WaveFormat.Channels);
        DelayMs = Math.Max(0, delayMs);
    }

    public int DelayMs { get; }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read > 0) _delay.Process(buffer, offset, read);
        return read;
    }
}
