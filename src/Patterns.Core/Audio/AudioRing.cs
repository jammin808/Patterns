namespace Patterns.Core.Audio;

/// <summary>
/// A ring of interleaved float samples with one writer and any number of readers, each keeping
/// its own place: the fan-out behind the routing matrix, where one decoded soundtrack has to reach
/// a sound card, an HDMI screen and an NDI send, each pulling at the cadence of its own clock.
///
/// The writer never waits and never blocks the decoder. A reader that asks for more than has been
/// written gets silence for the rest (an underrun: the source paused, or the device's clock runs
/// a little fast); a reader that has fallen further behind than the ring holds is snapped forward
/// to a set distance behind the writer (the device's clock runs slow, or the reader was away) —
/// a skip of a few hundred milliseconds once in a long while rather than a growing delay. Both
/// are counted, so the Audio page can say. Pure; tested.
/// </summary>
public sealed class AudioRing
{
    private readonly float[] _ring;
    private long _written;   // samples (not frames) written in all

    /// <summary>One reader's place in the ring.</summary>
    public sealed class Reader
    {
        internal long Position;
        internal bool Started;
        public long Underruns { get; internal set; }
        public long Snaps { get; internal set; }
        internal readonly int LatencySamples;

        internal Reader(int latencySamples)
        {
            LatencySamples = latencySamples;
        }
    }

    public AudioRing(int channels = 2, int capacityFrames = 48000)
    {
        Channels = Math.Max(1, channels);
        CapacitySamples = Math.Max(Channels, capacityFrames) * Channels;
        _ring = new float[CapacitySamples];
    }

    public int Channels { get; }

    /// <summary>The ring's size in samples (frames × channels).</summary>
    public int CapacitySamples { get; }

    /// <summary>Samples written in all — a reader's distance behind the writer is its latency.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>
    /// A reader that starts (and, after a snap, resumes) this many frames behind the writer:
    /// enough for the writer's bursts to arrive before they are due, not so much that the sound
    /// runs late against the picture. Must fit the ring with room to spare.
    /// </summary>
    public Reader OpenReader(int latencyFrames = 4800)
    {
        var latency = Math.Clamp(latencyFrames, 0, CapacitySamples / Channels / 2) * Channels;
        return new Reader(latency);
    }

    /// <summary>The writer's samples, interleaved; more than the ring holds keeps the newest.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.Length == 0) return;
        var src = interleaved;
        if (src.Length > CapacitySamples) src = src[^CapacitySamples..];
        var written = Interlocked.Read(ref _written);
        var at = (int)(written % CapacitySamples);
        var first = Math.Min(src.Length, CapacitySamples - at);
        src[..first].CopyTo(_ring.AsSpan(at, first));
        if (first < src.Length) src[first..].CopyTo(_ring.AsSpan(0, src.Length - first));
        Interlocked.Exchange(ref _written, written + interleaved.Length);
    }

    /// <summary>
    /// Fills <paramref name="destination"/> (interleaved) from the reader's place: silence for
    /// what has not been written yet, a snap forward when it has fallen out of the ring. Returns
    /// the samples that were real (the rest is silence).
    /// </summary>
    public int Read(Reader reader, Span<float> destination)
    {
        var written = Interlocked.Read(ref _written);
        if (!reader.Started)
        {
            // Nothing to hear yet: silence, and the reader starts the moment there is something.
            if (written <= 0)
            {
                destination.Clear();
                return 0;
            }
            reader.Started = true;
            reader.Position = Math.Max(0, written - reader.LatencySamples);
        }
        var behind = written - reader.Position;
        if (behind > CapacitySamples)
        {
            reader.Position = written - reader.LatencySamples;
            reader.Snaps++;
            behind = reader.LatencySamples;
        }
        var wanted = destination.Length;
        var real = (int)Math.Max(0, Math.Min(wanted, behind));
        if (real > 0)
        {
            var at = (int)(reader.Position % CapacitySamples);
            var first = Math.Min(real, CapacitySamples - at);
            _ring.AsSpan(at, first).CopyTo(destination[..first]);
            if (first < real) _ring.AsSpan(0, real - first).CopyTo(destination[first..real]);
            reader.Position += real;
        }
        if (real < wanted)
        {
            destination[real..].Clear();
            if (real == 0 || behind < wanted) reader.Underruns++;
        }
        return real;
    }

    /// <summary>How far a reader is behind the writer, in frames.</summary>
    public long LagFrames(Reader reader) => Math.Max(0, Written - reader.Position) / Channels;
}
