using Avalonia.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Independent audio track player: plays a file regardless of the visual source, to the
/// default device or any set of outputs at once (HDMI screens are audio devices too — so a
/// track can follow all screens, one screen, or a group). One reader+output per device;
/// starts together, drift over very long tracks is accepted for v1. Windows-only playback.
/// </summary>
public sealed class AudioPlayerService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly List<(IWavePlayer Output, AudioFileReader Reader, MMDevice Device)> _players = new();
    private string _activeKey = "";
    private string _status = "Stopped.";

    public AudioPlayerService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public string Status => _status;

    /// <summary>Active output device friendly names (WASAPI). Empty off-Windows.</summary>
    public static IReadOnlyList<string> OutputDevices()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var list = new List<string>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    if (!string.IsNullOrWhiteSpace(device.FriendlyName)) list.Add(device.FriendlyName);
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn("Audio device enumeration failed.", ex);
            return Array.Empty<string>();
        }
    }

    private void Tick()
    {
        var cfg = _services.State.AudioPlayer;
        try
        {
            if (!cfg.Playing || string.IsNullOrWhiteSpace(cfg.Path))
            {
                StopAll();
                _status = OperatingSystem.IsWindows()
                    ? string.IsNullOrWhiteSpace(cfg.Path) ? "Choose a track." : "Stopped."
                    : "Audio output is Windows-only.";
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                _status = "Audio output is Windows-only.";
                return;
            }

            if (!File.Exists(cfg.Path))
            {
                StopAll();
                cfg.Playing = false;
                _status = $"Track not found: {Path.GetFileName(cfg.Path)}";
                return;
            }

            var key = $"{cfg.Path}|{cfg.Loop}|{string.Join(";", cfg.Devices)}";
            if (key != _activeKey)
            {
                StopAll();
                _activeKey = key;
                StartAll(cfg.Path, cfg.Loop, cfg.Devices);
            }

            // Volume applies live (AudioFileReader.Volume is a linear gain; 1.25 ≈ +2 dB).
            var volume = (float)(cfg.VolumePct / 100.0);
            foreach (var (_, reader, _) in _players)
            {
                reader.Volume = volume;
            }

            if (_players.Count > 0)
            {
                var pos = _players[0].Reader.CurrentTime;
                var total = _players[0].Reader.TotalTime;
                var where = cfg.Devices.Count == 0 ? "default output" : $"{_players.Count} output{(_players.Count == 1 ? "" : "s")}";
                _status = $"Playing {Path.GetFileName(cfg.Path)} — {pos:mm\\:ss} / {total:mm\\:ss} on {where}{(cfg.Loop ? " · loop" : "")}";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio player failed.", ex);
            _status = $"Audio error: {ex.Message}";
            StopAll();
            cfg.Playing = false;
        }
    }

    private void StartAll(string path, bool loop, IReadOnlyList<string> deviceNames)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in ResolveDevices(enumerator, deviceNames))
        {
            try
            {
                var reader = new AudioFileReader(path);
                IWaveProvider source = loop ? new LoopingWaveStream(reader) : reader;
                var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
                output.Init(source);
                output.PlaybackStopped += (_, _) => OnPlaybackStopped();
                output.Play();
                _players.Add((output, reader, device)); // device stays alive until StopAll
            }
            catch (Exception ex)
            {
                Log.Warn($"Audio start failed on '{device.FriendlyName}'.", ex);
                device.Dispose();
            }
        }
        Log.Info($"Audio track started on {_players.Count} output(s): {Path.GetFileName(path)}");
    }

    /// <summary>Stored names → devices; empty selection (or nothing matching) = the default device.</summary>
    private static List<MMDevice> ResolveDevices(MMDeviceEnumerator enumerator, IReadOnlyList<string> names)
    {
        var result = new List<MMDevice>();
        if (names.Count > 0)
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (names.Any(n => string.Equals(n, device.FriendlyName, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(device);
                }
                else
                {
                    device.Dispose();
                }
            }
        }
        if (result.Count == 0)
        {
            result.Add(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
        }
        return result;
    }

    private void OnPlaybackStopped()
    {
        // Natural end without loop: flip the model off so the UI reflects it.
        Dispatcher.UIThread.Post(() =>
        {
            var cfg = _services.State.AudioPlayer;
            if (!cfg.Loop && cfg.Playing && _players.All(p => p.Output.PlaybackState == PlaybackState.Stopped))
            {
                cfg.Playing = false;
            }
        });
    }

    private void StopAll()
    {
        if (_players.Count == 0) return;
        foreach (var (output, reader, device) in _players)
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
                Log.Warn("Audio player stop issue.", ex);
            }
        }
        _players.Clear();
        _activeKey = "";
    }

    public void Dispose()
    {
        _timer.Stop();
        StopAll();
    }
}

/// <summary>Loops a wave stream forever by rewinding at the end (NAudio classic pattern).</summary>
public sealed class LoopingWaveStream : WaveStream
{
    private readonly WaveStream _source;

    public LoopingWaveStream(WaveStream source) => _source = source;

    public override WaveFormat WaveFormat => _source.WaveFormat;

    public override long Length => _source.Length;

    public override long Position
    {
        get => _source.Position;
        set => _source.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = _source.Read(buffer, offset + total, count - total);
            if (read == 0)
            {
                if (_source.Position == 0) break; // empty source — avoid spinning
                _source.Position = 0;
                continue;
            }
            total += read;
        }
        return total;
    }
}
