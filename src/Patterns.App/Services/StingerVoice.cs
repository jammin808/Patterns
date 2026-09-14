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

    /// <summary>The same per output, by the device's name (the matrix's gain for this destination); a voice with one gain takes the first.</summary>
    void SetGainPer(Func<string, double> gainFor) => SetGain(gainFor(""));

    /// <summary>Fade to silence over <paramref name="ms"/>, then end.</summary>
    void Release(int ms);

    /// <summary>The voice's sound as the NDI lanes read it (its first output's), or null.</summary>
    Patterns.Core.Audio.AudioRing? Tap => null;
}

/// <summary>The WASAPI voice: one output per selected device, each behind a <see cref="GainSampleProvider"/>.</summary>
public sealed class WasapiStingerVoice : IStingerVoice
{
    private readonly List<(IWavePlayer Output, AudioFileReader Reader, GainSampleProvider Gain, MMDevice Device, string Key, double Pick)> _outputs = new();
    private bool _releasing;
    private Patterns.Core.Audio.AudioRing? _tap;

    private WasapiStingerVoice()
    {
    }

    /// <summary>
    /// Opens the file on every resolved device, each behind its lip-sync delay (<paramref name="delayFor"/>
    /// answers a device key with milliseconds). Null when nothing opened (no device, unreadable file).
    /// </summary>
    public static WasapiStingerVoice? Open(string path, double volumePct, IReadOnlyList<string> deviceNames, Func<string, int>? delayFor = null)
    {
        var names = deviceNames.Count == 0 ? new[] { AudioPlayerService.DefaultDeviceKey } : deviceNames;
        return Open(path, volumePct, names.Select(n => new AudioOutputPick(n, 1.0, delayFor?.Invoke(n) ?? 0)).ToList(), null);
    }

    /// <summary>
    /// Opens the file on every pick — a device by name at a gain behind a delay — the way the
    /// routing matrix hands them over; the first output is tapped for the NDI lanes when a graph
    /// is given. Null when nothing opened (no device, unreadable file).
    /// </summary>
    public static WasapiStingerVoice? Open(string path, double volumePct, IReadOnlyList<AudioOutputPick> picks, AudioGraphService? graph)
    {
        var voice = new WasapiStingerVoice();
        if (picks.Count == 0) return null;   // routed nowhere: nothing to open, and that is the matrix's word
        var deviceNames = picks.Select(p => p.Device).ToList();
        using var enumerator = new MMDeviceEnumerator();
        List<MMDevice> devices;
        try
        {
            devices = AudioPlayerService.ResolveDevices(enumerator, deviceNames);
        }
        catch (Exception ex)
        {
            // No default endpoint at all (an HDMI screen that was the only output is off, a driver
            // mid-restart): the press fails with a reason, never with an exception up the desk.
            Log.Warn("No audio output could be resolved for a VOG or stinger.", ex);
            return null;
        }
        var first = true;
        foreach (var device in devices)
        {
            AudioFileReader? reader = null;
            WasapiOut? output = null;
            try
            {
                reader = new AudioFileReader(path) { Volume = (float)Math.Clamp(volumePct / 100.0, 0, 1.25) };
                var key = AudioPlayerService.DelayKeyFor(device, deviceNames);
                var pick = picks.FirstOrDefault(p => string.Equals(p.Device, key, StringComparison.OrdinalIgnoreCase));
                var pickGain = pick.Device is null ? 1.0 : pick.Gain;
                var gain = new GainSampleProvider(reader, (float)Math.Clamp(pickGain, 0, 1));
                ISampleProvider tail = gain;
                if (first && graph is not null)
                {
                    // The voice owns its tap; the graph reads it while the voice plays and forgets it after.
                    voice._tap = new Patterns.Core.Audio.AudioRing(AudioGraphService.Channels, AudioGraphService.Rate);
                    tail = new TeeSampleProvider(gain, voice._tap);
                }
                first = false;
                var delayMs = pick.Device is null ? 0 : pick.DelayMs;
                if (delayMs > 0) tail = new DelaySampleProvider(tail, delayMs);
                output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
                output.Init(new SampleToWaveProvider(tail));
                output.Play();
                voice._outputs.Add((output, reader, gain, device, key, pickGain)); // the device stays alive until Dispose
            }
            catch (Exception ex)
            {
                Log.Warn($"Stinger start failed on '{device.FriendlyName}'.", ex);
                try { output?.Dispose(); } catch { /* never opened */ }
                try { reader?.Dispose(); } catch { /* already gone */ }
                device.Dispose();
            }
        }
        if (voice._outputs.Count > 0) return voice;
        voice.Dispose();
        return null;
    }

    public bool IsPlaying => _outputs.Any(o => o.Output.PlaybackState != PlaybackState.Stopped);

    public bool Releasing => _releasing;

    public Patterns.Core.Audio.AudioRing? Tap => _tap;

    public void SetGain(double gain)
    {
        foreach (var o in _outputs) o.Gain.SetTarget((float)(gain * o.Pick));
    }

    public void SetGainPer(Func<string, double> gainFor)
    {
        foreach (var o in _outputs) o.Gain.SetTarget((float)gainFor(o.Key));
    }

    public void Release(int ms)
    {
        _releasing = true;
        foreach (var o in _outputs) o.Gain.Release(ms);
    }

    public void Dispose()
    {
        foreach (var (output, reader, _, device, _, _) in _outputs)
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
