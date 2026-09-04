using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A VOG sound is an announcement over whatever is on: a playing stinger — sound, clip or held
/// frame — ducks under it and carries on, and comes back up when it ends. Fake voices and fake
/// decoders behind the two factories; the clock is injected.
/// </summary>
public class VogDuckTests
{
    private static readonly DateTime T0 = new(2026, 9, 4, 21, 0, 0, DateTimeKind.Utc);

    private static void Ground(TestApp.Booted b)
    {
        b.Vm.IsSandboxActive = false;
        b.Vm.ActivePattern.Kind = PatternKind.Grid;
        b.Vm.State.Stingers.FadeMs = 400;
        b.Vm.State.Stingers.DuckPct = 20;
        b.Services.AirLabel = "Doors";
        Dispatcher.UIThread.RunJobs();
    }

    private static StingerItemConfig Add(TestApp.Booted b, string path, string name, StingerKind kind, StingerAfter after = StingerAfter.Return)
    {
        var item = new StingerItemConfig { Path = path, Name = name, Kind = kind, After = after };
        b.Vm.State.Stingers.Items.Add(item);
        return item;
    }

    private static ActionResult Fire(TestApp.Booted b, int n)
    {
        var r = b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, n.ToString());
        Dispatcher.UIThread.RunJobs();
        return r;
    }

    [AvaloniaFact]
    public void AVogSoundOverAStingSoundDucksItAndBothKeepPlaying()
    {
        var b = TestApp.Boot();
        var whoosh = AudioFakes.TempFile("whoosh.wav");
        var seats = AudioFakes.TempFile("seats.wav");
        try
        {
            Ground(b);
            var fakes = AudioFakes.Install(b);
            Add(b, whoosh, "Whoosh", StingerKind.Sting);
            Add(b, seats, "Take your seats", StingerKind.Vog);

            Assert.Equal(ActionStatus.Requested, Fire(b, 1).Status);
            Assert.Equal("STING: Whoosh", b.Services.AirLabel);
            // The press ran on the real clock: read the fade two seconds after it, never a fixed instant.
            Assert.Equal(0.0, b.Services.Stingers.MusicGainAt(DateTime.UtcNow.AddSeconds(2)));   // the sting fades the music

            Assert.Equal(ActionStatus.Requested, Fire(b, 2).Status);
            Assert.Equal(2, fakes.Voices.Count);
            var sting = fakes.Voices[0];
            var vog = fakes.Voices[1];
            Assert.False(sting.Releasing);                       // the stinger was not stopped
            Assert.Equal(0.2, sting.Gain, 6);                    // it ducks to the show's level
            Assert.Equal(1.0, vog.Gain);                         // nothing ducks a VOG
            Assert.True(b.Services.MusicDuckActive);
            Assert.Equal("Whoosh", b.Services.Stingers.StingOnAir);
            Assert.Equal("Take your seats", b.Services.Stingers.VogOnAir);
            Assert.Equal("Take your seats", b.Services.Stingers.VogSoundOnAir);
            Assert.Equal("Whoosh", b.Vm.State.Stingers.PlayingName);   // the session still owns the show
            Assert.Equal("STING: Whoosh", b.Services.AirLabel);       // and its label

            var json = new CommandRouter(b.Services).StateJson();
            Assert.Contains("\"vogSound\":\"Take your seats\"", json);
            Assert.Contains("\"stingerKind\":\"sting\"", json);

            // The announcement ends: the stinger comes back up and keeps its session.
            vog.Playing = false;
            b.Services.AudioPlayer.Poll();
            b.Services.Stingers.Poll(T0.AddSeconds(5));
            Assert.Equal(1.0, sting.Gain);
            Assert.False(sting.Releasing);
            Assert.Equal("", b.Services.Stingers.VogSoundOnAir);
            Assert.Equal("", b.Services.Stingers.VogOnAir);
            Assert.Equal("Whoosh", b.Services.Stingers.StingOnAir);
            Assert.Equal("STING: Whoosh", b.Services.AirLabel);

            // The stinger's natural end runs its after-policy as ever.
            sting.Playing = false;
            b.Services.AudioPlayer.Poll();
            b.Services.Stingers.Poll(T0.AddSeconds(8));
            Assert.Equal("", b.Services.Stingers.StingOnAir);
            Assert.Equal("Doors", b.Services.AirLabel);
            Assert.Equal(1.0, b.Services.Stingers.MusicGainAt(T0.AddSeconds(9)));
        }
        finally
        {
            File.Delete(whoosh);
            File.Delete(seats);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AVogSoundOverAStingClipDucksTheClipAndLeavesTheScreensAlone()
    {
        var b = TestApp.Boot();
        var clip = AudioFakes.TempFile("whoosh.mp4");
        var seats = AudioFakes.TempFile("seats.wav");
        try
        {
            Ground(b);
            var fakes = AudioFakes.Install(b);
            var item = Add(b, clip, "Whoosh", StingerKind.Sting);
            item.VolumePct = 80;
            Add(b, seats, "Take your seats", StingerKind.Vog);

            Assert.True(b.Services.Stingers.Fire(item, T0));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(fakes.Sources);
            var decoder = fakes.Sources[0];
            Assert.Equal(80, decoder.VolumePct);
            Assert.True(b.Services.Stingers.ClipActive);

            Assert.Equal(ActionStatus.Requested, Fire(b, 2).Status);
            Assert.True(b.Services.Stingers.ClipActive, "the clip keeps the screens");
            Assert.Equal(clip, b.Vm.State.Pattern.Media.VideoPath);
            Assert.Equal(16, decoder.VolumePct, 3);              // 80 × the 20 % duck
            Assert.False(decoder.FadeMs >= 0, "the clip was not retired");
            Assert.Equal("STING: Whoosh", b.Services.AirLabel);
            Assert.Equal("Whoosh", b.Vm.State.Stingers.PlayingName);
            Assert.Equal("Take your seats", b.Services.Stingers.VogSoundOnAir);
            Assert.Equal(0.2, b.Services.Video.ClipGain, 6);

            // The announcement ends: the clip's soundtrack comes back up.
            fakes.Voices[0].Playing = false;
            b.Services.AudioPlayer.Poll();
            b.Services.Stingers.Poll(T0.AddSeconds(4));
            Assert.Equal(80, decoder.VolumePct, 3);
            Assert.True(b.Services.Stingers.ClipActive);
            Assert.Equal("STING: Whoosh", b.Services.AirLabel);

            // And the clip's own end still puts the show back.
            decoder.Ended = true;
            b.Services.Stingers.Poll(T0.AddSeconds(6));
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Services.Stingers.ClipActive);
            Assert.Equal(PatternKind.Grid, b.Vm.State.Pattern.Kind);
            Assert.Equal("Doors", b.Services.AirLabel);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            File.Delete(seats);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AVogSoundOverAHeldStingLeavesTheHold()
    {
        var b = TestApp.Boot();
        var clip = AudioFakes.TempFile("title.mp4");
        var seats = AudioFakes.TempFile("seats.wav");
        try
        {
            Ground(b);
            var fakes = AudioFakes.Install(b);
            var item = Add(b, clip, "Title card", StingerKind.Sting, StingerAfter.Manual);
            Add(b, seats, "Take your seats", StingerKind.Vog);
            b.Vm.State.Stingers.HoldSeconds = 0;

            Assert.True(b.Services.Stingers.Fire(item, T0));
            Dispatcher.UIThread.RunJobs();
            fakes.Sources[0].Ended = true;
            b.Services.Stingers.Poll(T0.AddSeconds(3));
            Assert.True(b.Services.Stingers.Holding);
            Assert.Equal("STING HOLD: Title card", b.Services.AirLabel);

            Assert.Equal(ActionStatus.Requested, Fire(b, 2).Status);
            Assert.True(b.Services.Stingers.Holding);
            Assert.Equal("Title card", b.Services.Stingers.HoldName);
            Assert.Equal("STING HOLD: Title card", b.Services.AirLabel);
            Assert.Equal("Take your seats", b.Services.Stingers.VogSoundOnAir);
            Assert.Equal(clip, b.Vm.State.Pattern.Media.VideoPath);

            fakes.Voices[0].Playing = false;
            b.Services.AudioPlayer.Poll();
            b.Services.Stingers.Poll(T0.AddSeconds(6));
            Assert.True(b.Services.Stingers.Holding);
            Assert.Equal("STING HOLD: Title card", b.Services.AirLabel);

            b.Services.Stingers.Stop(T0.AddSeconds(7));
            Assert.False(b.Services.Stingers.Holding);
            Assert.Equal("Doors", b.Services.AirLabel);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            File.Delete(seats);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStingReleasesAPlayingVogSoundAndANewVogReplacesTheOld()
    {
        var b = TestApp.Boot();
        var seats = AudioFakes.TempFile("seats.wav");
        var welcome = AudioFakes.TempFile("welcome.wav");
        var clip = AudioFakes.TempFile("whoosh.mp4");
        try
        {
            Ground(b);
            var fakes = AudioFakes.Install(b);
            Add(b, seats, "Take your seats", StingerKind.Vog);
            Add(b, welcome, "Welcome", StingerKind.Vog);
            var sting = Add(b, clip, "Whoosh", StingerKind.Sting);

            // Alone, a VOG sound names the air as it always did.
            Assert.Equal(ActionStatus.Requested, Fire(b, 1).Status);
            Assert.Equal("VOG: Take your seats", b.Services.AirLabel);
            Assert.Equal("Take your seats", b.Vm.State.Stingers.PlayingName);

            // A second VOG replaces the first: one announcement at a time.
            Assert.Equal(ActionStatus.Requested, Fire(b, 2).Status);
            Assert.True(fakes.Voices[0].Releasing);
            Assert.False(fakes.Voices[1].Releasing);
            Assert.Equal("VOG: Welcome", b.Services.AirLabel);
            Assert.Equal("Welcome", b.Services.Stingers.VogOnAir);

            // A transition hit ends the announcement and takes the air; the original label
            // comes back after the sting, not the VOG's.
            Assert.True(b.Services.Stingers.Fire(sting, T0));
            Dispatcher.UIThread.RunJobs();
            Assert.True(fakes.Voices[1].Releasing);
            Assert.Equal("", b.Services.Stingers.VogSoundOnAir);
            Assert.Equal("STING: Whoosh", b.Services.AirLabel);
            Assert.Equal("Whoosh", b.Vm.State.Stingers.PlayingName);

            fakes.Sources[0].Ended = true;
            b.Services.Stingers.Poll(T0.AddSeconds(4));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Doors", b.Services.AirLabel);
            Assert.Equal(PatternKind.Grid, b.Vm.State.Pattern.Kind);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(seats);
            File.Delete(welcome);
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AVogSoundOverAVogClipDucksItAndLetsItFinish()
    {
        var b = TestApp.Boot();
        var clip = AudioFakes.TempFile("walk-in.mp4");
        var seats = AudioFakes.TempFile("seats.wav");
        try
        {
            Ground(b);
            var fakes = AudioFakes.Install(b);
            var vogClip = Add(b, clip, "Walk-in", StingerKind.Vog);
            Add(b, seats, "Take your seats", StingerKind.Vog);

            Assert.True(b.Services.Stingers.Fire(vogClip, T0));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ActionStatus.Requested, Fire(b, 2).Status);
            Assert.True(b.Services.Stingers.ClipActive);
            Assert.Equal(20, fakes.Sources[0].VolumePct, 3);
            Assert.Equal("VOG: Walk-in", b.Services.AirLabel);
            Assert.Equal("Take your seats", b.Services.Stingers.VogSoundOnAir);
            Assert.Equal("Take your seats", b.Services.Stingers.VogOnAir);   // the announcement is the VOG on air
            Assert.Equal("Walk-in", b.Vm.State.Stingers.PlayingName);        // the clip owns the show

            fakes.Sources[0].Ended = true;
            b.Services.Stingers.Poll(T0.AddSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Services.Stingers.ClipActive);
            Assert.Equal("Doors", b.Services.AirLabel);
            Assert.Equal("Take your seats", b.Vm.State.Stingers.PlayingName); // the announcement is still on
            Assert.Equal("Take your seats", b.Services.Stingers.VogOnAir);

            fakes.Voices[0].Playing = false;
            b.Services.AudioPlayer.Poll();
            b.Services.Stingers.Poll(T0.AddSeconds(8));
            Assert.Equal("", b.Vm.State.Stingers.PlayingName);
            Assert.Equal("Doors", b.Services.AirLabel);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            File.Delete(seats);
            b.Dispose();
        }
    }
}
