using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// One sound on air: a file playing on the audio-track outputs with a gain of its own. A voice is
/// never reused — a stop releases it (a fade to silence, then it ends by itself) and a new press
/// always opens a fresh one, so nothing that was told to stop can ever be heard again.
/// </summary>
public interface IStingerVoice : IDisposable
{
    /// <summary>Still producing sound — false once the file ended or the release reached silence.</summary>
    bool IsPlaying { get; }

    /// <summary>Told to leave; only ever gets quieter from here.</summary>
    bool Releasing { get; }

    /// <summary>Live gain on top of the item's own volume (0–1), landing over a short slew.</summary>
    void SetGain(double gain);

    /// <summary>Fade to silence over <paramref name="ms"/>, then end.</summary>
    void Release(int ms);
}

/// <summary>The WASAPI voice: one output per selected device, each behind a <see cref="GainSampleProvider"/>.</summary>
public sealed class WasapiStingerVoice : IStingerVoice
{
    private readonly List<(IWavePlayer Output, AudioFileReader Reader, GainSampleProvider Gain, MMDevice Device)> _outputs = new();
    private bool _releasing;

    private WasapiStingerVoice()
    {
    }

    /// <summary>
    /// Opens the file on every resolved device, each behind its lip-sync delay (<paramref name="delayFor"/>
    /// answers a device key with milliseconds). Null when nothing opened (no device, unreadable file).
    /// </summary>
    public static WasapiStingerVoice? Open(string path, double volumePct, IReadOnlyList<string> deviceNames, Func<string, int>? delayFor = null)
    {
        var voice = new WasapiStingerVoice();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in AudioPlayerService.ResolveDevices(enumerator, deviceNames))
        {
            try
            {
                var reader = new AudioFileReader(path) { Volume = (float)Math.Clamp(volumePct / 100.0, 0, 1.25) };
                var gain = new GainSampleProvider(reader);
                var delayMs = delayFor?.Invoke(AudioPlayerService.DelayKeyFor(device, deviceNames)) ?? 0;
                ISampleProvider tail = delayMs > 0 ? new DelaySampleProvider(gain, delayMs) : gain;
                var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
                output.Init(new SampleToWaveProvider(tail));
                output.Play();
                voice._outputs.Add((output, reader, gain, device)); // the device stays alive until Dispose
            }
            catch (Exception ex)
            {
                Log.Warn($"Stinger start failed on '{device.FriendlyName}'.", ex);
                device.Dispose();
            }
        }
        if (voice._outputs.Count > 0) return voice;
        voice.Dispose();
        return null;
    }

    public bool IsPlaying => _outputs.Any(o => o.Output.PlaybackState != PlaybackState.Stopped);

    public bool Releasing => _releasing;

    public void SetGain(double gain)
    {
        foreach (var o in _outputs) o.Gain.SetTarget((float)gain);
    }

    public void Release(int ms)
    {
        _releasing = true;
        foreach (var o in _outputs) o.Gain.Release(ms);
    }

    public void Dispose()
    {
        foreach (var (output, reader, _, device) in _outputs)
        {
            try
            {
                output.Stop();
                output.Dispose();
                reader.Dispose();
                device.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Stinger voice dispose issue.", ex);
            }
        }
        _outputs.Clear();
    }
}
