using NAudio.Wave;

namespace Patterns.App.Services;

/// <summary>
/// A gain stage in front of a WASAPI output: a live target the samples slew towards over 20 ms
/// (a duck never clicks), and a release — a ramp to silence over a chosen time, after which the
/// stream ends so the output stops by itself. Sample-accurate: the ramp lives in the audio
/// callback, not in a UI poll, so a busy desk cannot turn a fade into a chop.
/// </summary>
public sealed class GainSampleProvider : ISampleProvider
{
    /// <summary>How long a live gain change takes to land.</summary>
    public const int SlewMs = 20;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _slewStep;
    private float _gain;
    private float _target;
    private float _releaseStep;
    private bool _ended;

    public GainSampleProvider(ISampleProvider source, float initialGain = 1f)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _slewStep = 1f / Math.Max(1, source.WaveFormat.SampleRate * SlewMs / 1000);
        _gain = _target = Math.Clamp(initialGain, 0f, 1f);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>The gain the last sample left with.</summary>
    public float Gain => _gain;

    /// <summary>A release is running or has run; the target can no longer be moved.</summary>
    public bool Releasing => _releaseStep > 0;

    /// <summary>The stream has ended — the source ran dry or the release reached silence.</summary>
    public bool Ended => _ended;

    /// <summary>Where the gain heads next (0–1). Ignored once released: a released voice only ever gets quieter.</summary>
    public void SetTarget(float gain)
    {
        if (Releasing) return;
        _target = Math.Clamp(gain, 0f, 1f);
    }

    /// <summary>Ramps to silence over <paramref name="ms"/> from wherever the gain is now, then ends the stream.</summary>
    public void Release(int ms)
    {
        if (Releasing || _ended) return;
        _target = 0f;
        _releaseStep = 1f / Math.Max(1, WaveFormat.SampleRate * Math.Max(1, ms) / 1000);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_ended) return 0;
        var read = _source.Read(buffer, offset, count);
        if (read == 0)
        {
            _ended = true;
            return 0;
        }

        var step = Releasing ? _releaseStep : _slewStep;
        for (var i = 0; i < read; i += _channels)
        {
            if (_gain != _target)
            {
                var d = _target - _gain;
                _gain = Math.Abs(d) <= step ? _target : _gain + Math.Sign(d) * step;
            }
            var end = Math.Min(i + _channels, read);
            for (var c = i; c < end; c++)
            {
                buffer[offset + c] *= _gain;
            }
        }

        // This block carried the tail down to silence (−80 dB is silence, and float steps never
        // land on an exact zero); the next read ends the stream.
        if (Releasing && _gain <= 1e-4f)
        {
            _gain = 0f;
            _ended = true;
        }
        return read;
    }
}
