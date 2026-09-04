using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NAudio.Wave;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// How a stinger sound or clip leaves the air: a fade, never a cut, and never heard again. The
/// WASAPI voice is a fake behind the player's factory; the decoder is a fake behind the video
/// engine's factory; the gain stage is exercised sample by sample.
/// </summary>
public class StingerSoundTests
{
    private static readonly DateTime T0 = new(2026, 9, 4, 20, 0, 0, DateTimeKind.Utc);

    // ---- the gain stage -----------------------------------------------------------------

    private sealed class Ones : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, 1f, offset, count);
            return count;
        }
    }

    private static float[] ReadAll(GainSampleProvider gain, int frames)
    {
        var buffer = new float[frames * 2];
        var read = gain.Read(buffer, 0, buffer.Length);
        return buffer[..read];
    }

    [Fact]
    public void ALiveGainChangeSlewsAtFullScalePerTwentyMilliseconds()
    {
        // The slew moves the gain by the whole scale in 20 ms, so a step to 0.5 lands in 10 ms —
        // fast enough that a duck is heard as a duck, slow enough that nothing clicks.
        var gain = new GainSampleProvider(new Ones());
        gain.SetTarget(0.5f);
        var samples = ReadAll(gain, 2400); // 50 ms
        Assert.True(samples[0] > 0.99f, "the first sample must start from the old gain");
        Assert.True(samples[2 * 240] is > 0.7f and < 0.8f, $"5 ms in, halfway down: {samples[2 * 240]}");
        Assert.Equal(0.5f, samples[2 * 600], 3);   // 12.5 ms in: landed
        Assert.Equal(0.5f, samples[^1], 3);
        Assert.Equal(samples[2 * 700], samples[2 * 700 + 1]); // both channels carry the same gain
    }

    [Fact]
    public void AReleaseRampsToSilenceAndThenEndsTheStream()
    {
        var gain = new GainSampleProvider(new Ones());
        gain.Release(100);
        Assert.True(gain.Releasing);
        var samples = ReadAll(gain, 4800); // exactly 100 ms
        Assert.True(samples[0] > 0.99f);
        Assert.True(samples[2 * 2400] is > 0.4f and < 0.6f, $"halfway: {samples[2 * 2400]}");
        Assert.True(samples[^1] < 0.01f, $"the tail must reach silence: {samples[^1]}");
        Assert.True(gain.Ended);
        Assert.Equal(0, gain.Read(new float[64], 0, 64)); // the output stops by itself
    }

    [Fact]
    public void AReleasedVoiceOnlyEverGetsQuieter()
    {
        var gain = new GainSampleProvider(new Ones());
        gain.Release(50);
        gain.SetTarget(1f);          // ignored
        ReadAll(gain, 4800);
        Assert.True(gain.Ended);
        Assert.Equal(0f, gain.Gain);
    }

    // ---- the voice, through the live service ------------------------------------------

    private sealed class FakeVoice : IStingerVoice
    {
        public string Path { get; }
        public double VolumePct { get; }
        public bool Playing = true;
        public int ReleasedMs = -1;
        public bool Disposed;
        public double Gain = 1;

        public FakeVoice(string path, double volumePct)
        {
            Path = path;
            VolumePct = volumePct;
        }

        public bool IsPlaying => Playing && !Disposed;
        public bool Releasing => ReleasedMs >= 0;
        public void SetGain(double gain) => Gain = gain;
        public void Release(int ms) => ReleasedMs = ms;
        public void Dispose() => Disposed = true;
    }

    private static string TempSound(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 1 });
        return path;
    }

    [AvaloniaFact]
    public void StopReleasesTheSoundAndTheNextPressIsAFreshVoice()
    {
        var b = TestApp.Boot();
        var wav = TempSound("seats.wav");
        try
        {
            b.Vm.IsSandboxActive = false;
            b.Vm.State.Stingers.StopFadeMs = 250;
            var voices = new List<FakeVoice>();
            b.Services.AudioPlayer.VoiceFactory = (path, vol) =>
            {
                var v = new FakeVoice(path, vol);
                voices.Add(v);
                return v;
            };
            b.Vm.State.Stingers.Items.Add(new StingerItemConfig { Path = wav, Name = "Take your seats", VolumePct = 90 });

            Assert.Equal(ActionStatus.Requested, b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, "1").Status);
            Assert.Single(voices);
            Assert.Equal(90, voices[0].VolumePct);
            Assert.True(b.Services.AudioPlayer.StingerPlaying);
            Assert.True(b.Services.MusicDuckActive, "the music ducks under the sound");
            Assert.Equal("Take your seats", b.Services.Stingers.VogOnAir);

            // STOP: the voice is released over the stop fade — not disposed, not cut — and the
            // duck lifts at once so the music comes back under the tail.
            b.Services.Stingers.Stop(T0);
            Assert.Equal(250, voices[0].ReleasedMs);
            Assert.False(voices[0].Disposed);
            Assert.False(b.Services.AudioPlayer.StingerPlaying);
            Assert.False(b.Services.MusicDuckActive);
            Assert.Equal("", b.Services.Stingers.VogOnAir);

            // A re-fire a moment later, while the old tail is still sounding: a second voice.
            // The first is never reused and never un-released.
            Assert.Equal(ActionStatus.Requested, b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, "1").Status);
            Assert.Equal(2, voices.Count);
            Assert.NotSame(voices[0], voices[1]);
            Assert.True(voices[0].Releasing);
            Assert.False(voices[1].Releasing);
            Assert.True(b.Services.AudioPlayer.StingerPlaying);

            // The tail reaches silence: the sweep disposes it and leaves the new voice alone.
            voices[0].Playing = false;
            b.Services.AudioPlayer.Poll();
            Assert.True(voices[0].Disposed);
            Assert.False(voices[1].Disposed);
            Assert.True(b.Services.AudioPlayer.StingerPlaying);

            // The natural end of the second voice closes the session.
            voices[1].Playing = false;
            b.Services.AudioPlayer.Poll();
            b.Services.Stingers.Poll(T0.AddSeconds(3));
            Assert.True(voices[1].Disposed);
            Assert.False(b.Services.AudioPlayer.StingerPlaying);
            Assert.Equal("", b.Services.Stingers.VogOnAir);
        }
        finally
        {
            File.Delete(wav);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ANewPressOverAPlayingSoundReleasesTheOldOne()
    {
        var b = TestApp.Boot();
        var wav = TempSound("seats.wav");
        var wav2 = TempSound("winner.wav");
        try
        {
            b.Vm.IsSandboxActive = false;
            var voices = new List<FakeVoice>();
            b.Services.AudioPlayer.VoiceFactory = (path, vol) =>
            {
                var v = new FakeVoice(path, vol);
                voices.Add(v);
                return v;
            };
            b.Vm.State.Stingers.Items.Add(new StingerItemConfig { Path = wav, Name = "Seats" });
            b.Vm.State.Stingers.Items.Add(new StingerItemConfig { Path = wav2, Name = "Winner" });

            b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, "1");
            b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, "2");
            Assert.Equal(2, voices.Count);
            Assert.Equal(b.Vm.State.Stingers.StopFadeMs, voices[0].ReleasedMs); // the default stop fade
            Assert.False(voices[1].Releasing);
            Assert.Equal("Winner", b.Services.Stingers.VogOnAir);

            // Shutdown is the one hard stop.
            b.Services.AudioPlayer.StopStinger();
            Assert.True(voices[0].Disposed);
            Assert.True(voices[1].Disposed);
            Assert.False(b.Services.AudioPlayer.StingerPlaying);
        }
        finally
        {
            File.Delete(wav);
            File.Delete(wav2);
            b.Dispose();
        }
    }

    // ---- the decoder, through the video engine --------------------------------------

    private sealed class FakeSource : IMountedSource
    {
        public MediaLocator.WantedInput Wanted { get; }
        public bool Mute;
        public double VolumePct;
        public DateTime? FadeStartUtc;
        public int FadeMs = -1;
        public int Pumps;
        public bool Disposed;

        public FakeSource(MediaLocator.WantedInput wanted)
        {
            Wanted = wanted;
            Mute = wanted.Mute;
            VolumePct = wanted.VolumePct;
        }

        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint) => false;
        public SKSizeI? FrameSize => null;
        public bool IsPlaying => !Disposed;
        public bool IsEnded => false;
        public double DurationSeconds => 10;
        public string StatusText => "fake";

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

        public void Dispose() => Disposed = true;
    }

    private static ShowSnapshot Clip(string path, int stopFadeMs, int transitionMs)
    {
        var s = new ShowState();
        s.Pattern.Kind = PatternKind.Media;
        s.Pattern.Media.Source = MediaSource.Video;
        s.Pattern.Media.VideoPath = path;
        s.Pattern.Media.Mute = false;
        s.Pattern.Media.VolumePct = 80;
        s.Stingers.StopFadeMs = stopFadeMs;
        s.Transition.Enabled = transitionMs > 0;
        s.Transition.DurationMs = Math.Max(transitionMs, 1);
        return new ShowSnapshot { State = s, Version = 1 };
    }

    private static ShowSnapshot Grid(int stopFadeMs, int transitionMs)
    {
        var s = new ShowState();
        s.Pattern.Kind = PatternKind.Grid;
        s.Stingers.StopFadeMs = stopFadeMs;
        s.Transition.Enabled = transitionMs > 0;
        s.Transition.DurationMs = Math.Max(transitionMs, 1);
        return new ShowSnapshot { State = s, Version = 2 };
    }

    [AvaloniaFact]
    public void ARetiredClipFadesItsSoundAndIsDisposedAfterTheLongestFade()
    {
        InputBus.Clear();
        var opened = new List<FakeSource>();
        using var engine = new VideoEngine
        {
            SourceFactory = w =>
            {
                var f = new FakeSource(w);
                opened.Add(f);
                return f;
            },
        };
        var key = InputKeys.Video("C:/show/whoosh.mp4");
        try
        {
            engine.Reconcile(Clip("C:/show/whoosh.mp4", stopFadeMs: 200, transitionMs: 500), null, T0);
            Assert.Single(opened);
            Assert.Same(opened[0], InputBus.For(key));
            Assert.False(opened[0].Mute);

            // The clip leaves: its sound starts fading now, the frames stay on the previous map.
            engine.Reconcile(Grid(stopFadeMs: 200, transitionMs: 500), null, T0.AddSeconds(1));
            Assert.Equal(T0.AddSeconds(1), opened[0].FadeStartUtc);
            Assert.Equal(200, opened[0].FadeMs);
            Assert.Null(InputBus.For(key));
            Assert.Same(opened[0], InputBus.PreviousFor(key));
            Assert.Equal(1, engine.RetiredCount);

            // The pump drives the fade; the hold is the longer fade (500) plus the margin (300).
            engine.Pump(T0.AddMilliseconds(1100));
            Assert.True(opened[0].Pumps > 0);
            engine.SweepRetired(T0.AddMilliseconds(1700));
            Assert.False(opened[0].Disposed);
            engine.SweepRetired(T0.AddMilliseconds(1801));
            Assert.True(opened[0].Disposed);
            Assert.Null(InputBus.PreviousFor(key));
            Assert.Equal(0, engine.RetiredCount);
        }
        finally
        {
            InputBus.Clear();
        }
    }

    [AvaloniaFact]
    public void AReFireInsideTheHoldOpensAFreshDecoderAndNeverRevivesTheOldOne()
    {
        InputBus.Clear();
        var opened = new List<FakeSource>();
        using var engine = new VideoEngine
        {
            SourceFactory = w =>
            {
                var f = new FakeSource(w);
                opened.Add(f);
                return f;
            },
        };
        var key = InputKeys.Video("C:/show/whoosh.mp4");
        try
        {
            engine.Reconcile(Clip("C:/show/whoosh.mp4", 200, 0), null, T0);
            engine.Reconcile(Grid(200, 0), null, T0.AddSeconds(1));
            Assert.True(opened[0].FadeMs >= 0);

            // The same file again, 100 ms later — well inside the 500 ms hold.
            engine.Reconcile(Clip("C:/show/whoosh.mp4", 200, 0), null, T0.AddMilliseconds(1100));
            Assert.Equal(2, opened.Count);
            Assert.Same(opened[1], InputBus.For(key));
            Assert.Same(opened[0], InputBus.PreviousFor(key));
            Assert.False(opened[1].Mute, "the new decoder plays at its own volume");
            Assert.True(opened[0].FadeMs >= 0, "the old one keeps fading — it is never brought back");
            Assert.False(opened[0].Disposed);

            // A live audio change reaches only the mounted decoder.
            engine.SweepRetired(T0.AddMilliseconds(1600));
            Assert.True(opened[0].Disposed);
            Assert.Same(opened[1], InputBus.For(key));
            Assert.Null(InputBus.PreviousFor(key));
        }
        finally
        {
            InputBus.Clear();
        }
    }
}
