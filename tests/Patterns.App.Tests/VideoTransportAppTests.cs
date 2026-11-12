using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 86: the clip's transport on a live desk, as another app drives it over the wire — PAUSE,
/// PLAY, STOP and SEEK moving the decoder, the refusals that would change nothing, and STATE's video
/// row with the precise clock (positionMs, lengthMs, remainingMs, paused, rate, seq) and videos[].
/// And the hold by role: a playlist item paused and stopped with the playlist's clock, a stinger's
/// clip paused with the stinger's ending and never stopped here, a scheduled entry held past its
/// time skipped and journalled, and what PAUSE and STOP do to the clip said after seq
/// (pausable, stoppable) and read by the panel's keys, the menus and the assistant.
/// </summary>
public class VideoTransportAppTests
{
    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static FakeSource PutClipOnAir(TestApp.Booted b, AudioFakes fakes, string name, bool loop = false, [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerMemberName] string member = "")
    {
        var (services, vm, _) = b;
        vm.ActivePattern.Kind = PatternKind.Media;
        vm.ActivePattern.Media.Source = MediaSource.Video;
        vm.ActivePattern.Media.Loop = loop;
        vm.ActivePattern.Media.VideoPath = AudioFakes.TempFile(b, name, file, member);
        Dispatcher.UIThread.RunJobs();
        services.ReconcileInputs();
        var fake = fakes.Sources.Single();
        fake.Length = 210;
        fake.Position = 62;
        return fake;
    }

    private static long Seq(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("video").GetProperty("seq").GetInt64();
    }

    [AvaloniaFact]
    public void AnotherAppPausesSeeksAndStopsTheClipAndReadsItBack()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var fake = PutClipOnAir(b, fakes, "sponsor.mp4");

            // The precise clock: milliseconds, the rate, a seq — at the end of the row; videos[] lists the mounted clip.
            var json = router.StateJson();
            Assert.Contains("\"positionMs\":62000", json);
            Assert.Contains("\"lengthMs\":210000", json);
            Assert.Contains("\"remainingMs\":148000", json);
            Assert.Contains("\"paused\":false", json);
            Assert.Contains("\"rate\":1", json);
            Assert.Contains("\"seekable\":true", json);
            Assert.Contains("\"videos\":[{\"key\":", json);
            Assert.Contains("\"onAir\":true", json);
            var seq0 = Seq(json);
            Assert.Equal(seq0, Seq(router.StateJson()));   // nothing moved, nothing to resync

            // PAUSE holds it; a second PAUSE changes nothing and is refused; STATE says paused and the seq moved.
            Assert.StartsWith("OK", Send(router, "VIDEO PAUSE"));
            Assert.True(fake.Paused);
            Assert.Contains("already paused", Send(router, "VIDEO PAUSE"));
            var paused = router.StateJson();
            Assert.Contains("\"paused\":true", paused);
            Assert.Contains("\"chip\":\"VT PAUSED 2:28\"", paused);
            var seq1 = Seq(paused);
            Assert.True(seq1 > seq0);

            // PLAY plays on; a PLAY while playing is refused.
            Assert.StartsWith("OK", Send(router, "VIDEO PLAY"));
            Assert.False(fake.Paused);
            Assert.Contains("already playing", Send(router, "VIDEO PLAY"));

            // SEEK: from the top, a clock time, from the end; each moves the decoder.
            Assert.StartsWith("OK", Send(router, "VIDEO SEEK 1:23"));
            Assert.Equal(83, fake.SeekedTo);
            Assert.StartsWith("OK", Send(router, "VIDEO SEEK END 5"));
            Assert.Equal(205, fake.SeekedTo);
            Assert.StartsWith("OK", Send(router, "VT SEEK 30"));
            Assert.Equal(30, fake.SeekedTo);
            Assert.True(Seq(router.StateJson()) > seq1);
            Assert.StartsWith("ERR", Send(router, "VIDEO SEEK soon"));

            // STOP: back to the top and held — the outputs untouched.
            var outputsBefore = services.Bus.OutputsLive;
            Assert.StartsWith("OK", Send(router, "VIDEO STOP"));
            Assert.Equal(0, fake.SeekedTo);
            Assert.True(fake.Paused);
            Assert.Equal(outputsBefore, services.Bus.OutputsLive);
            Assert.Contains("already stopped", Send(router, "VIDEO STOP"));
            Assert.StartsWith("OK", Send(router, "VIDEO PLAY"));
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == nameof(ShowActionKind.VideoStop));
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == nameof(ShowActionKind.VideoSeek));

            // A length not known yet: from the top still works, from the end is refused rather than guessed; no lengthMs.
            fake.Length = 0;
            Assert.Contains("not said how long", Send(router, "VIDEO SEEK END 5"));
            Assert.StartsWith("OK", Send(router, "VIDEO SEEK 12"));
            var unknown = router.StateJson();
            Assert.Contains("\"lengthMs\":null", unknown);
            Assert.Contains("\"remainingMs\":null", unknown);

            // A clip shorter than the gap starts again from its top.
            fake.Length = 6;
            Assert.StartsWith("OK", Send(router, "VIDEO SEEK END 10"));
            Assert.Equal(0, fake.SeekedTo);

            // An ended clip: PLAY and PAUSE refuse with the way back; RESTART is the verb.
            fake.Length = 210;
            fake.Ended = true;
            Assert.Contains("VIDEO RESTART", Send(router, "VIDEO PLAY"));
            Assert.Contains("VIDEO RESTART", Send(router, "VIDEO PAUSE"));
            fake.Ended = false;

            // The panel's keys: one play/pause key and a stop.
            vm.VideoPlayPauseCommand.Execute(null);
            Assert.True(fake.Paused);
            vm.VideoPlayPauseCommand.Execute(null);
            Assert.False(fake.Paused);
            vm.VideoStopCommand.Execute(null);
            Assert.True(fake.Paused);
            Assert.Equal(0, fake.SeekedTo);

            // No clip on air: every transport verb says so.
            vm.ActivePattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            services.ReconcileInputs();
            foreach (var line in new[] { "VIDEO PLAY", "VIDEO PAUSE", "VIDEO STOP", "VIDEO SEEK 10" })
            {
                Assert.Contains("No video is on air", Send(router, line));
            }
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }

    private static readonly DateTime T0 = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>STATE's video row: its role, the three facts, and the names of its last three fields in order.</summary>
    private static (string Role, bool Paused, bool Pausable, bool Stoppable, string Tail) VideoRow(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var video = doc.RootElement.GetProperty("video");
        var names = video.EnumerateObject().Select(p => p.Name).ToList();
        return (video.GetProperty("role").GetString()!, video.GetProperty("paused").GetBoolean(), video.GetProperty("pausable").GetBoolean(),
            video.GetProperty("stoppable").GetBoolean(), string.Join(",", names.TakeLast(3)));
    }

    /// <summary>A playlist on air whose first item is a clip the fakes open, on the test's clock: the item's path and its fake.</summary>
    private static (string Opener, FakeSource Fake) PutPlaylistOnAir(TestApp.Booted b, AudioFakes fakes, Func<DateTime> clock, Action<PlaylistOptions, string>? more = null,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerMemberName] string member = "")
    {
        var (services, vm, _) = b;
        services.Playlist.NowUtc = clock;
        services.Playlist.VideoAvailableOverride = () => true;   // headless: no libVLC — the clips are the fakes'
        var opener = AudioFakes.TempFile(b, "opener.mp4", file, member);
        var slide = AudioFakes.TempFile(b, "slide.png", file, member);
        vm.ActivePattern.Kind = PatternKind.Media;
        vm.ActivePattern.Media.Source = MediaSource.Playlist;
        var list = vm.ActivePattern.Media.Playlist;
        PlaylistSequencer.Normalize(list);
        list.Sections[0].Items.Add(new PlaylistItemConfig { Path = opener, DurationSeconds = 30 });
        list.Sections[0].Items.Add(new PlaylistItemConfig { Path = slide, DurationSeconds = 30 });
        more?.Invoke(list, slide);
        Dispatcher.UIThread.RunJobs();
        Tick(services);
        Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);
        var fake = fakes.Sources.Last();
        fake.Length = 210;
        fake.Position = 62;
        return (opener, fake);
    }

    private static void Tick(AppServices services)
    {
        services.Playlist.Poll();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void APlaylistItemIsHeldWithThePlaylistsClockAndRunsOnAfterPlay()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var clock = T0;
            var (opener, fake) = PutPlaylistOnAir(b, fakes, () => clock);
            var name = Path.GetFileName(opener);

            // Before: a playlist item says it can be paused and stopped — after seq, and the row's old fields where they were.
            var json = router.StateJson();
            Assert.Equal(("playlist", false, true, true, "seq,pausable,stoppable"), VideoRow(json));
            Assert.Contains("\"role\":\"playlist\",\"tag\":\"PLAYLIST\",\"position\":62", json);
            Assert.Contains("\"positionMs\":62000,\"lengthMs\":210000,\"remainingMs\":148000,\"paused\":false,\"rate\":1,\"seekable\":true,\"seq\":", json);
            Assert.Contains($"Playing 1/2: {name} · 30s left", services.Playlist.Status);

            // PAUSE ten seconds in: Done, in the wire's words; the clip and the playlist's clock both hold.
            clock = T0.AddSeconds(10);
            var reply = Send(router, "VIDEO PAUSE");
            Assert.StartsWith("OK", reply);
            Assert.Equal($"OK '{name}' paused at 1:02 — the playlist holds with it; VIDEO PLAY runs both on.", reply);
            Assert.True(fake.Paused);
            Assert.True(services.Playlist.Held);
            Assert.Contains("already paused", Send(router, "VIDEO PAUSE"));
            Assert.Equal(("playlist", true, true, true, "seq,pausable,stoppable"), VideoRow(router.StateJson()));
            Assert.Contains(services.Journal.Tail(5), e => e.Kind == nameof(ShowActionKind.VideoPause) && e.Outcome == "Done" && e.Message.Contains("the playlist holds with it"));

            // Five minutes pass — ten times its length: the item is not advanced past its end, and its "left" does not count down.
            foreach (var later in new[] { 31, 120, 310 })
            {
                clock = T0.AddSeconds(later);
                Tick(services);
                Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);
                Assert.Contains($"Held 1/2: {name} · 20s left — VIDEO PLAY runs it on", services.Playlist.Status);
            }
            using (var state = System.Text.Json.JsonDocument.Parse(router.StateJson()))
            {
                Assert.StartsWith($"Held 1/2: {name} · 20s left", state.RootElement.GetProperty("playlist").GetString());   // the pages' "left" is this string
            }

            // Every reader: the panel's keys, the right-click entries, the Run strip's tip, the assistant's facts.
            vm.Show.RefreshVideoClock();
            Assert.True(vm.Show.VideoPausable);
            Assert.True(vm.Show.VideoStoppable);
            var menu = Patterns.Core.Menus.DeskMenus.Page(DeskMenuFacts.Desk(services), DeskMenuFacts.Page(services, "Media")).Groups.SelectMany(g => g.Entries).ToList();
            Assert.Contains("already paused", menu.Single(e => e.Id == "video.pause").Because);
            Assert.Equal("", menu.Single(e => e.Id == "video.play").Because);
            Assert.Equal("", menu.Single(e => e.Id == "video.stop").Because);
            Assert.Contains("a playlist item · 1:02 / 3:30 · 2:28 left · paused — held, and the playlist holds with it; VIDEO PLAY runs both on", services.GatherFacts().ClipNow);
            Assert.DoesNotContain("opener", services.GatherFacts().ClipNow);

            // PLAY releases both: twenty seconds were left, and are; then the playlist moves on.
            var play = Send(router, "VIDEO PLAY");
            Assert.StartsWith("OK", play);
            Assert.Contains("the playlist runs on with it", play);
            Assert.False(fake.Paused);
            Assert.False(services.Playlist.Held);
            Assert.Contains("already playing", Send(router, "VIDEO PLAY"));
            Assert.Contains($"Playing 1/2: {name} · 20s left", services.Playlist.Status);
            clock = T0.AddSeconds(310 + 19);
            Tick(services);
            Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);
            clock = T0.AddSeconds(310 + 21);
            Tick(services);
            Assert.NotEqual(opener, services.Bus.Current.PlaylistNow?.Path);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void StopPutsAPlaylistItemAndItsClockBackToTheTopHeldAndAHoldEndsWithItsClip()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var clock = T0;
            var (opener, fake) = PutPlaylistOnAir(b, fakes, () => clock);

            // STOP twenty-five seconds in: the clip at its top, held, and the item's clock back to its whole thirty seconds, held.
            clock = T0.AddSeconds(25);
            var reply = Send(router, "VIDEO STOP");
            Assert.StartsWith("OK", reply);
            Assert.Contains("stopped — back to its top, held, and the playlist holds with it. VIDEO PLAY runs both on.", reply);
            Assert.Equal(0, fake.SeekedTo);
            Assert.True(fake.Paused);
            Assert.True(services.Playlist.Held);
            Assert.Contains("already stopped", Send(router, "VIDEO STOP"));
            clock = T0.AddSeconds(200);
            Tick(services);
            Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);
            Assert.Contains($"Held 1/2: {Path.GetFileName(opener)} · 30s left", services.Playlist.Status);

            // A seek under the hold moves the clock with the picture and stays held; PLAY then runs what the seek left.
            Assert.Contains("still paused", Send(router, "VIDEO SEEK END 5"));
            Assert.True(services.Playlist.Held);
            clock = T0.AddSeconds(400);
            Assert.StartsWith("OK", Send(router, "VIDEO PLAY"));
            clock = T0.AddSeconds(404);
            Tick(services);
            Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);
            clock = T0.AddSeconds(406);
            Tick(services);
            Assert.NotEqual(opener, services.Bus.Current.PlaylistNow?.Path);

            // Round again to the clip; held, and then the operator takes the playlist off the air: the hold ends with its
            // clip, said once in the journal, and the playlist does not come back parked.
            clock = T0.AddSeconds(440);
            Tick(services);
            Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);
            var again = fakes.Sources.Last();
            Assert.NotSame(fake, again);
            again.Length = 210;
            again.Position = 5;
            Assert.StartsWith("OK", Send(router, "VIDEO PAUSE"));
            Assert.True(services.Playlist.Held);
            vm.ActivePattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            var stale = 0;
            void CountStale()
            {
                if (services.Bus.Current.PlaylistNow is not null) stale++;
            }
            services.Bus.Changed += CountStale;
            Tick(services);
            services.Bus.Changed -= CountStale;
            Assert.Equal(0, stale);   // nothing of a playlist the air no longer holds is published on its way out
            Assert.Null(services.Bus.Current.PlaylistNow);
            Assert.False(services.Playlist.Held);
            Assert.Single(services.Journal.Tail(20), e => e.Kind == PlaylistService.PlaylistHoldKind && e.Outcome == PlaylistService.HoldEndedStatus && e.Message.Contains("left the air") && e.Message.Contains("does not come back held"));
            Tick(services);
            Assert.Single(services.Journal.Tail(20), e => e.Kind == PlaylistService.PlaylistHoldKind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AHoldWhoseItemLeavesThePlaylistEndsWithOneRow()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var clock = T0;
            var (opener, _) = PutPlaylistOnAir(b, fakes, () => clock);

            clock = T0.AddSeconds(10);
            Assert.StartsWith("OK", Send(router, "VIDEO PAUSE"));
            Assert.True(services.Playlist.Held);

            // The operator takes the held item out of the part: the order has no such item, the one after it runs, and
            // the hold that went with it is said once — a row and a line, like every hold that ends without VIDEO PLAY.
            var items = vm.ActivePattern.Media.Playlist.Sections[0].Items;
            items.Remove(items.Single(i => i.Path == opener));
            Dispatcher.UIThread.RunJobs();
            clock = T0.AddSeconds(11);
            Tick(services);
            Assert.False(services.Playlist.Held);
            Assert.NotEqual(opener, services.Bus.Current.PlaylistNow?.Path);
            var row = Assert.Single(services.Journal.Tail(20), e => e.Kind == PlaylistService.PlaylistHoldKind);
            Assert.Equal(PlaylistService.HoldEndedStatus, row.Outcome);
            Assert.Contains("its item left the playlist; the playlist runs on", row.Message);
            Assert.StartsWith("Playing 1/1:", services.Playlist.Status);
            clock = T0.AddSeconds(12);
            Tick(services);
            Assert.Single(services.Journal.Tail(20), e => e.Kind == PlaylistService.PlaylistHoldKind);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// A part's start and a scheduled entry's minute come under a hold, and the playlist then leaves the air — held
    /// still, or released by VIDEO PLAY a moment before, with no tick between. Back on air later, neither is the
    /// playlist's any more: no part takes over late, no entry fires late or is reported skipped.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhatWaitedUnderAHoldIsNotThePlaylistsThatComesBackOnAir(bool playedFirst)
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var clock = T0;
            var minute = T0.AddMinutes(1).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var (opener, _) = PutPlaylistOnAir(b, fakes, () => clock, (list, slide) =>
            {
                list.Sections[0].Items[0].DurationSeconds = 100000;
                list.Sections[0].Items[1].ScheduledTime = minute;
                list.Sections[0].Items[1].ScheduledDurationSeconds = 600;   // its window is open long after the playlist is back
                var part = new PlaylistSectionConfig { Name = "Break", StartTime = minute };
                part.Items.Add(new PlaylistItemConfig { Path = slide, DurationSeconds = 30 });
                list.Sections.Add(part);
            });
            var options = vm.ActivePattern.Media.Playlist;
            // The playlist ticks only where this test ticks it: its own 250 ms timer, firing inside the pump VIDEO PLAY
            // waits on, would tick it on air between the play and the leaving that "no tick between" rules out.
            services.Playlist.Dispose();

            // Held before the minute; the minute comes under the hold, and both wait.
            clock = T0.AddSeconds(30);
            Assert.StartsWith("OK", Send(router, "VIDEO PAUSE"));
            clock = T0.AddSeconds(70);
            Tick(services);
            Assert.Equal(0, options.ActiveSection);
            Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);

            // Off the air — held, or played on first with no tick between.
            clock = T0.AddSeconds(80);
            if (playedFirst) Assert.StartsWith("OK", Send(router, "VIDEO PLAY"));
            vm.ActivePattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            clock = T0.AddSeconds(85);
            Tick(services);
            Assert.Null(services.Bus.Current.PlaylistNow);
            Assert.False(services.Playlist.Held);

            // Back on air past the minute, inside the entry's window: the playlist plays its own running order.
            clock = T0.AddSeconds(200);
            vm.ActivePattern.Kind = PatternKind.Media;
            Dispatcher.UIThread.RunJobs();
            Tick(services);
            Tick(services);
            Assert.Equal(0, options.ActiveSection);
            Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);
            Assert.True(services.Bus.Current.PlaylistNow!.Index >= 0);   // the order's own item, not a scheduled interruption
            Assert.DoesNotContain(services.Journal.Tail(30), e => e.Kind == nameof(ShowActionKind.PlaylistPart));
            Assert.DoesNotContain(services.Journal.Tail(30), e => e.Kind == PlaylistService.PlaylistMissedKind);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// The other order (L105): the same part's start and scheduled entry wait under a hold, VIDEO PLAY releases it, and a
    /// tick comes before the playlist leaves the air. That tick is the playlist's on air: what waited is due, the part
    /// takes over once — one PlaylistPart row — and its slide runs as a scheduled interruption, with no entry reported
    /// skipped and no hold said ended (VIDEO PLAY ended it). Off the air after it, nothing more of the playlist's is said.
    /// </summary>
    [AvaloniaFact]
    public void WhatWaitedUnderAHoldRunsWhenATickComesBetweenThePlayAndTheLeaving()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var clock = T0;
            var minute = T0.AddMinutes(1).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var (opener, _) = PutPlaylistOnAir(b, fakes, () => clock, (list, slide) =>
            {
                list.Sections[0].Items[0].DurationSeconds = 100000;
                list.Sections[0].Items[1].ScheduledTime = minute;
                list.Sections[0].Items[1].ScheduledDurationSeconds = 600;
                var part = new PlaylistSectionConfig { Name = "Break", StartTime = minute };
                part.Items.Add(new PlaylistItemConfig { Path = slide, DurationSeconds = 30 });
                list.Sections.Add(part);
            });
            var options = vm.ActivePattern.Media.Playlist;
            // The playlist ticks only where this test ticks it: the one tick between the play and the leaving is this test's.
            services.Playlist.Dispose();

            clock = T0.AddSeconds(30);
            Assert.StartsWith("OK", Send(router, "VIDEO PAUSE"));
            clock = T0.AddSeconds(70);
            Tick(services);
            Assert.Equal(0, options.ActiveSection);
            Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);

            // Played on at 80 s, and ticked on air before it leaves: what waited is the playlist's again.
            clock = T0.AddSeconds(80);
            Assert.StartsWith("OK", Send(router, "VIDEO PLAY"));
            Tick(services);
            Assert.False(services.Playlist.Held);
            Assert.Equal(1, options.ActiveSection);
            Assert.Single(services.Journal.Tail(30), e => e.Kind == nameof(ShowActionKind.PlaylistPart));
            Assert.Equal(-1, services.Bus.Current.PlaylistNow?.Index);                                 // the slide, as a scheduled interruption
            Assert.DoesNotContain(services.Journal.Tail(30), e => e.Kind == PlaylistService.PlaylistMissedKind);
            Assert.DoesNotContain(services.Journal.Tail(30), e => e.Kind == PlaylistService.PlaylistHoldKind);
            var rows = services.Journal.Tail(30).Count;

            // Off the air: nothing of the playlist's is on, and nothing more is said of it.
            vm.ActivePattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            clock = T0.AddSeconds(85);
            Tick(services);
            Assert.Null(services.Bus.Current.PlaylistNow);
            Assert.Equal(rows, services.Journal.Tail(30).Count);
            Assert.Single(services.Journal.Tail(30), e => e.Kind == nameof(ShowActionKind.PlaylistPart));
            Assert.DoesNotContain(services.Journal.Tail(30), e => e.Kind is PlaylistService.PlaylistMissedKind or PlaylistService.PlaylistHoldKind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void APausedClipWhosePlaylistRunsOnIsNeverSaidToHoldIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var clock = T0;
            var (opener, fake) = PutPlaylistOnAir(b, fakes, () => clock);
            var name = Path.GetFileName(opener);

            clock = T0.AddSeconds(5);
            Assert.StartsWith("OK", Send(router, "VIDEO PAUSE"));
            Assert.True(services.VideoOnAir()!.ClockHeld);
            Assert.Contains("the playlist holds with it", services.GatherFacts().ClipNow);

            // The playlist's clock is let go under a decoder still paused: the words follow what is held, not the role —
            // the fact for the assistant, and the reply to the PLAY that then has only the clip to run on.
            Assert.True(services.Playlist.ReleaseCurrent());
            Assert.True(fake.Paused);
            Assert.False(services.VideoOnAir()!.ClockHeld);
            Assert.Contains("paused — held, and the playlist runs on without it; VIDEO PLAY runs it on", services.GatherFacts().ClipNow);
            Assert.DoesNotContain("holds with it", services.GatherFacts().ClipNow);
            Assert.Equal($"OK '{name}' plays on from 1:02.", Send(router, "VIDEO PLAY"));
            Assert.False(fake.Paused);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>Two screens in the rig, both on: a sting can cover one of them alone.</summary>
    private static void TwoScreens(TestApp.Booted b)
    {
        var screens = new List<ScreenInfo>
        {
            new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
            new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        };
        b.Services.Screens.All.Clear();
        foreach (var s in screens) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(screens);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles(screens);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void UnderAStingOnOneScreenTheProgrammesPlaylistItemIsStillThePlaylists()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var clock = T0;
            services.Stingers.NowUtc = () => clock;
            TwoScreens(b);
            var (opener, fake) = PutPlaylistOnAir(b, fakes, () => clock);
            var name = Path.GetFileName(opener);

            // A sting as one screen's take: its clip is that screen's own picture, and the programme is untouched.
            var sting = new StingerItemConfig { Kind = StingerKind.Sting, Path = AudioFakes.TempFile(b, "whoosh.mp4"), Name = "Whoosh" };
            vm.State.Stingers.Items.Add(sting);
            var ticket = new TakeTicket { Scope = "ID a", Taken = new[] { "a" }, Labels = new[] { "1 · Left" }, Where = "on 1 · Left alone", Cover = "Whoosh", PressedUtc = clock };
            Assert.True(services.Stingers.Fire(sting, clock, StingerAfter.Take, ticket), services.Stingers.Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Stingers.ClipActive);
            Assert.Equal(MediaSource.Playlist, services.AirState.Pattern.Media.Source);
            var whoosh = fakes.Sources.Last();
            Assert.NotSame(fake, whoosh);

            // The clip the transport reads is the programme's item, by its own role: stoppable, and the playlist's to hold.
            Assert.Equal(("playlist", false, true, true, "seq,pausable,stoppable"), VideoRow(router.StateJson()));
            clock = T0.AddSeconds(10);
            Assert.Equal($"OK '{name}' paused at 1:02 — the playlist holds with it; VIDEO PLAY runs both on.", Send(router, "VIDEO PAUSE"));
            Assert.True(fake.Paused);
            Assert.False(whoosh.Paused);
            Assert.True(services.Playlist.Held);
            Assert.False(services.Stingers.ClipHeld);                       // the sting's guards and its ending run on: its clip does
            Assert.NotEqual(StingerService.ClipHeldStatus, services.Stingers.Status);
            clock = T0.AddSeconds(100);
            Tick(services);
            Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);   // held: not moved on under the frozen picture

            // PLAY runs both on; STOP is the playlist item's, never refused toward STINGER STOP.
            Assert.Contains("the playlist runs on with it", Send(router, "VIDEO PLAY"));
            var stop = Send(router, "VIDEO STOP");
            Assert.StartsWith("OK", stop);
            Assert.DoesNotContain("STINGER STOP", stop);
            Assert.Contains("the playlist holds with it", stop);
            Assert.Equal(0, fake.SeekedTo);
            Assert.Null(whoosh.SeekedTo);
            Assert.True(services.Playlist.Held);
            Assert.False(services.Stingers.ClipHeld);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AScheduledEntryHeldPastItsTimeIsSkippedAndJournalledNotPlayedLate()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var clock = T0;
            var due = T0.AddMinutes(1).ToLocalTime();
            var (opener, _) = PutPlaylistOnAir(b, fakes, () => clock, (list, slide) =>
            {
                list.Sections[0].Items[0].DurationSeconds = 100000;
                list.Sections[0].Items[1].ScheduledTime = due.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                list.Sections[0].Items[1].ScheduledDurationSeconds = 20;   // its window is its own minute
            });

            // Held before the entry's minute; the minute comes and goes under the hold and the picture stays.
            clock = T0.AddSeconds(30);
            Assert.StartsWith("OK", Send(router, "VIDEO PAUSE"));
            clock = T0.AddSeconds(70);
            Tick(services);
            Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);
            Assert.DoesNotContain(services.Journal.Tail(20), e => e.Kind == PlaylistService.PlaylistMissedKind);

            // Released two minutes past it: the entry is not played late — one journal row says it was skipped, and why.
            clock = T0.AddSeconds(190);
            Assert.StartsWith("OK", Send(router, "VIDEO PLAY"));
            Tick(services);
            Tick(services);
            Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);
            var row = Assert.Single(services.Journal.Tail(20), e => e.Kind == PlaylistService.PlaylistMissedKind);
            Assert.Equal(PlaylistService.SkippedStatus, row.Outcome);
            Assert.EndsWith("slide.png", row.Target);
            Assert.Contains("held past its time", row.Message);
            Assert.Contains("not played late", row.Message);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// A stinger's clip held past the stall guard's fifteen seconds, then played on and read by a tick before its
    /// position has moved: the show is not put back — whether the source says it plays under a pause (the guard would
    /// trip inside the hold) or says it does not (the guard would trip at the release, its instant stale by the hold).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStingersClipIsHeldWithItsEndingAndTheStallGuardDoesNotCountTheHold(bool playsUnderPause)
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var clock = T0;
            services.Stingers.NowUtc = () => clock;
            vm.State.Pattern.Kind = PatternKind.Grid;
            var item = new StingerItemConfig { Path = AudioFakes.TempFile(b, "sting.mp4"), Name = "Sting" };
            vm.State.Stingers.Items.Add(item);
            Assert.True(services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            Dispatcher.UIThread.RunJobs();
            var source = fakes.Sources[0];
            source.Length = 120;
            source.PlaysUnderPause = playsUnderPause;
            foreach (var s in new[] { 1, 3 })
            {
                clock = T0.AddSeconds(s);
                source.Position = s;
                services.Stingers.Poll();
            }
            Assert.True(services.Stingers.ClipActive);

            // A stinger's clip can be paused and is never stopped here: STATE says so, the wire refuses STOP toward STINGER STOP.
            Assert.Equal(("stinger", false, true, false, "seq,pausable,stoppable"), VideoRow(router.StateJson()));
            var stop = Send(router, "VIDEO STOP");
            Assert.StartsWith("ERR", stop);
            Assert.Contains("is a stinger's clip — STINGER STOP puts the show back.", stop);
            Assert.False(source.Paused);
            Assert.Null(source.SeekedTo);
            vm.Show.RefreshVideoClock();
            Assert.True(vm.Show.VideoPausable);
            Assert.False(vm.Show.VideoStoppable);
            var entry = Patterns.Core.Menus.DeskMenus.Page(DeskMenuFacts.Desk(services), DeskMenuFacts.Page(services, "Media")).Groups.SelectMany(g => g.Entries).Single(e => e.Id == "video.stop");
            Assert.Contains("STINGER STOP", entry.Because);

            // PAUSE holds the clip and the stinger's ending with it.
            var pause = Send(router, "VIDEO PAUSE");
            Assert.StartsWith("OK", pause);
            Assert.Contains("paused at 0:03 — the stinger holds with it; VIDEO PLAY runs both on.", pause);
            Assert.True(source.Paused);
            Assert.True(services.Stingers.ClipHeld);
            Assert.Equal(("stinger", true, true, false, "seq,pausable,stoppable"), VideoRow(router.StateJson()));
            Assert.Equal(StingerService.ClipHeldStatus, services.Stingers.Status);   // the stinger's own readers say held, and the two ways on
            Assert.False(StatusWords.ReadsAsFailure(services.Stingers.Status));

            // Held far past the guard's fifteen seconds: the show is not put back.
            foreach (var s in new[] { 8, 14, 3 + StingerService.StallSeconds + 1, 43 })
            {
                clock = T0.AddSeconds(s);
                services.Stingers.Poll();
                Assert.True(services.Stingers.ClipActive, $"held at +{s} s");
            }
            Assert.NotEqual(PatternKind.Grid, services.AirState.Pattern.Kind);

            // PLAY, and a tick before the position has moved: still on — the hold was not a stall.
            var play = Send(router, "VIDEO PLAY");
            Assert.StartsWith("OK", play);
            Assert.Contains("the stinger runs on with it", play);
            Assert.False(source.Paused);
            Assert.False(services.Stingers.ClipHeld);
            Assert.Equal("Clip on screens: Sting", services.Stingers.Status);
            clock = T0.AddSeconds(43.2);
            services.Stingers.Poll();
            Assert.True(services.Stingers.ClipActive);

            // The guard is still there for a clip that really stops moving: fifteen seconds after it last moved, the show comes back.
            clock = T0.AddSeconds(44);
            source.Position = 4;
            services.Stingers.Poll();
            clock = T0.AddSeconds(44 + StingerService.StallSeconds + 1);
            services.Stingers.Poll();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal("Clip stalled — previous content back.", services.Stingers.Status);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void StingerStopPutsTheShowBackFromUnderAHeldClipAndTheNextFiringRuns()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var clock = T0;
            services.Stingers.NowUtc = () => clock;
            vm.State.Pattern.Kind = PatternKind.Grid;
            var item = new StingerItemConfig { Path = AudioFakes.TempFile(b, "sting.mp4"), Name = "Sting" };
            vm.State.Stingers.Items.Add(item);
            Assert.True(services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            Dispatcher.UIThread.RunJobs();
            fakes.Sources[0].Length = 120;
            fakes.Sources[0].Position = 2;
            Assert.StartsWith("OK", Send(router, "VIDEO PAUSE"));
            Assert.True(services.Stingers.ClipHeld);

            Assert.StartsWith("OK", Send(router, "STINGER STOP"));
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Stingers.ClipActive);
            Assert.False(services.Stingers.ClipHeld);
            Assert.Equal(PatternKind.Grid, services.AirState.Pattern.Kind);

            // Fired again: a new firing is never held.
            clock = T0.AddSeconds(30);
            Assert.True(services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Stingers.ClipActive);
            Assert.False(services.Stingers.ClipHeld);
            Assert.NotEqual(StingerService.ClipHeldStatus, services.Stingers.Status);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStingerFiredOverADecoderAHoldLeftPausedTellsItToPlay()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var clock = T0;
            services.Stingers.NowUtc = () => clock;

            // The show's own clip is the stinger's file: one decoder, which the show keeps open when the stinger is stopped.
            var clip = PutClipOnAir(b, fakes, "sting.mp4");
            var item = new StingerItemConfig { Path = vm.ActivePattern.Media.VideoPath, Name = "Sting" };
            vm.State.Stingers.Items.Add(item);
            Assert.True(services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(clip, fakes.Sources.Last());
            Assert.Contains("the stinger holds with it", Send(router, "VIDEO PAUSE"));
            Assert.True(clip.Paused);

            Assert.StartsWith("OK", Send(router, "STINGER STOP"));
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Stingers.ClipActive);
            Assert.Same(clip, fakes.Sources.Last());
            Assert.True(clip.Paused);                                       // the hold's pause outlived the stinger, on the show's decoder

            // The next firing finds that decoder open and paused: it is told to play, from the top, and is not held.
            clock = T0.AddSeconds(30);
            clip.SeekedTo = null;
            Assert.True(services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Stingers.ClipActive);
            Assert.Same(clip, fakes.Sources.Last());
            Assert.False(clip.Paused);
            Assert.Equal(0, clip.SeekedTo);
            Assert.False(services.Stingers.ClipHeld);
        }
        finally
        {
            b.Dispose();
        }
    }

    // ---- VIDEO … ON <key>: a clip the programme holds, named by its key from STATE videos[] ----

    /// <summary>A clip on the programme's first layer, over whatever the pattern shows: its key and the fake the pool opened for it.</summary>
    private static (string Key, FakeSource Fake) PutLayerClip(TestApp.Booted b, AudioFakes fakes, string name, [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerMemberName] string member = "")
    {
        var (services, vm, _) = b;
        var path = AudioFakes.TempFile(b, name, file, member);
        vm.ActivePattern.Layer1.Source = LayerSource.Video;
        vm.ActivePattern.Layer1.VideoPath = path;
        vm.ActivePattern.Layer1.Enabled = true;
        Dispatcher.UIThread.RunJobs();
        services.ReconcileInputs();
        var key = Patterns.Core.Media.InputKeys.Video(path);
        return (key, fakes.Sources.Last(f => f.Wanted.Key == key && !f.Disposed));
    }

    /// <summary>STATE videos[]: the row of one key — on air, pre-rolled, paused, held by the air — or null when the list does not hold it.</summary>
    private static (bool OnAir, bool PreRoll, bool Paused, bool Air)? VideosRow(string json, string key)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var row in doc.RootElement.GetProperty("videos").EnumerateArray())
        {
            if (row.GetProperty("key").GetString() != key) continue;
            return (row.GetProperty("onAir").GetBoolean(), row.GetProperty("preRoll").GetBoolean(), row.GetProperty("paused").GetBoolean(), row.GetProperty("air").GetBoolean());
        }
        return null;
    }

    [AvaloniaFact]
    public void ALayersClipIsDrivenByItsKeyWhileTheClipOnAirPlaysOn()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var onAir = PutClipOnAir(b, fakes, "opener.mp4");
            var onAirKey = onAir.Wanted.Key;
            var (key, layer) = PutLayerClip(b, fakes, "b roll.mp4");                    // a path with a space: the key is the rest of the line
            layer.Length = 60;
            layer.Position = 12;
            Assert.Contains(' ', key);
            var side = VideoClock.NameOfKey(key);                                        // the file's name, as the replies say it
            var opener = VideoClock.NameOfKey(onAirKey);

            // STATE lists both clips; the programme's own is the one on air, and the transport's with no key.
            var before = router.StateJson();
            Assert.Equal((true, false, false, true), VideosRow(before, onAirKey));
            Assert.Equal((false, false, false, true), VideosRow(before, key));
            Assert.Equal(new[] { onAirKey, key }, services.VideosOnAir().Select(r => r.Key));
            Assert.Equal(VideoRole.Layer, services.VideosOnAir()[1].Role);

            // PAUSE by the key: the layer's clip holds, in the wire's words; the clip on air plays on and STATE says which is which.
            var reply = Send(router, $"VIDEO PAUSE ON {key}");
            Assert.Equal($"OK '{side}' paused at 0:12.", reply);
            Assert.True(layer.Paused);
            Assert.False(onAir.Paused);
            var held = router.StateJson();
            Assert.Equal((true, false, false, true), VideosRow(held, onAirKey));
            Assert.Equal((false, false, true, true), VideosRow(held, key));
            Assert.Equal(("program", false, true, true, "seq,pausable,stoppable"), VideoRow(held));
            var row = Assert.Single(services.Journal.Tail(5), e => e.Kind == nameof(ShowActionKind.VideoPause));
            Assert.Equal((key, "Done"), (row.Target, row.Outcome));

            // The rule follows the clip named: a second PAUSE by the key changes nothing and is refused; bare PLAY is the clip on air's.
            Assert.Contains($"'{side}' is already paused at 0:12", Send(router, $"VIDEO PAUSE ON {key}"));
            Assert.Contains($"'{opener}' is already playing", Send(router, "VIDEO PLAY"));
            Assert.True(layer.Paused);

            // SEEK, END and RESTART by the key move the layer's clip and nothing else; STOP at its top, held, would change nothing.
            Assert.Equal($"OK '{side}' to 0:30 of 1:00 — still paused.", Send(router, $"VIDEO SEEK 30 ON {key}"));
            Assert.Equal(30, layer.SeekedTo);
            Assert.StartsWith($"OK '{side}' to its last 5 s", Send(router, $"VT END 5 ON {key}"));
            Assert.Equal(55, layer.SeekedTo);
            Assert.StartsWith($"OK '{side}' from the top", Send(router, $"CLIP RESTART ON {key}"));
            Assert.Equal(0, layer.SeekedTo);
            Assert.Contains("already stopped at its top", Send(router, $"VIDEO STOP ON {key}"));
            Assert.Null(onAir.SeekedTo);
            Assert.False(onAir.Paused);

            // PLAY by the key runs it on; then STOP puts it back to its top, held — the clip on air and the outputs untouched.
            Assert.Equal($"OK '{side}' plays on from 0:00.", Send(router, $"VIDEO PLAY ON {key}"));
            Assert.False(layer.Paused);
            layer.Position = 20;
            var outputs = services.Bus.OutputsLive;
            Assert.Equal($"OK '{side}' stopped — back to its top, held. VIDEO PLAY runs it.", Send(router, $"VIDEO STOP ON {key}"));
            Assert.True(layer.Paused);
            Assert.Equal(outputs, services.Bus.OutputsLive);
            Assert.Null(onAir.SeekedTo);

            // A key that is not mounted is refused with the reason and never read as the clip on air — a word after the key,
            // another case, a path the show never opened, a live source's key.
            foreach (var wrong in new[] { key + " NOW", key.ToUpperInvariant(), "vid:/nowhere/else.mp4", "cap:Camera 1", "opener.mp4" })
            {
                foreach (var verb in new[] { "PAUSE", "PLAY", "STOP", "RESTART", "END 5", "SEEK 10" })
                {
                    Assert.Equal("ERR " + VideoClock.NotMountedWords, Send(router, $"VIDEO {verb} ON {wrong}"));
                }
            }
            Assert.False(onAir.Paused);
            Assert.Null(onAir.SeekedTo);
            Assert.Equal(0, layer.SeekedTo);
            Assert.Contains(services.Journal.Tail(40), e => e.Kind == nameof(ShowActionKind.VideoPause) && e.Outcome == "Refused" && e.Target == "vid:/nowhere/else.mp4");

            // The clip on air by its own key is the clip on air: the same clip, the same rule.
            Assert.Equal($"OK '{opener}' paused at 1:02.", Send(router, $"VIDEO PAUSE ON {onAirKey}"));
            Assert.True(onAir.Paused);
            Assert.Contains("already paused", Send(router, "VIDEO PAUSE"));
            Assert.StartsWith("OK", Send(router, "VIDEO PLAY"));

            // The desk's right-click: OTHER CLIPS on the Media page lists the layer's clip with the wire's own lines, off by the wire's rule.
            var others = Patterns.Core.Menus.DeskMenus.Page(DeskMenuFacts.Desk(services), DeskMenuFacts.Page(services, "Media")).Groups.Single(g => g.Heading == "OTHER CLIPS").Entries;
            Assert.Equal(new[] { $"VIDEO PLAY ON {key}", $"VIDEO RESTART ON {key}", $"VIDEO STOP ON {key}" }, others.Select(e => e.Wire));
            Assert.Contains("already stopped", others[2].Because);
            Assert.Equal("", others[0].Because);
            Assert.True(services.Actions.Execute(others[0].Action!.Value, ActionOrigin.Desk).Ok);
            Assert.False(layer.Paused);

            // A cue's step carries the key as its target, and the step's words name the clip.
            var step = new CueActionConfig { Kind = ShowActionKind.VideoPause, Target = key };
            Assert.Equal($"Video: pause → {side}", CueSummary.DescribeAction(services.State, step));
            var cue = new RunCueConfig { Number = "01.010", Name = "Hold the side clip" };
            cue.Actions.Add(step);
            services.CueStack.Stack.Cues.Add(cue);
            Assert.True(((ICueHost)services).RunCue(services.CueStack.Stack, cue, ActionOrigin.Desk).Ok);
            Assert.True(layer.Paused);
            Assert.False(onAir.Paused);

            // The assistant's facts: the other clip by its role and clock, never its file or its key, with the form that drives it.
            var facts = services.GatherFacts();
            Assert.StartsWith("a layer's or a screen's own clip · ", facts.ClipsByKey);
            Assert.Contains("VIDEO PAUSE|PLAY|STOP|RESTART ON <key>", facts.ClipsByKey);
            Assert.DoesNotContain("b roll", facts.ClipsByKey);
            Assert.StartsWith("the programme's clip · ", facts.ClipNow);

            // With the layer off, its key names no mounted clip: refused, and OTHER CLIPS is gone.
            vm.ActivePattern.Layer1.Enabled = false;
            Dispatcher.UIThread.RunJobs();
            services.ReconcileInputs();
            Assert.Equal("ERR " + VideoClock.NotMountedWords, Send(router, $"VIDEO PLAY ON {key}"));
            Assert.Empty(DeskMenuFacts.Desk(services).OtherClips);
            Assert.Equal("", services.GatherFacts().ClipsByKey);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AClipOnlyThePreviewHoldsIsRefusedByItsKeyUntilATakePutsItOnTheAir()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var onAir = PutClipOnAir(b, fakes, "opener.mp4");

            // EDIT SAFE: a layer's clip staged in the preview is mounted for the preview alone. STATE lists it; the air does not hold it.
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            var (key, staged) = PutLayerClip(b, fakes, "staged.mp4");
            var stagedName = VideoClock.NameOfKey(key);
            staged.Length = 60;
            staged.Position = 7;
            Assert.False(services.AirState.Pattern.Layer1.Enabled);
            Assert.Equal((false, false, false, false), VideosRow(router.StateJson(), key));
            Assert.DoesNotContain(services.VideosOnAir(), r => r.Key == key);
            Assert.Empty(DeskMenuFacts.Desk(services).OtherClips);

            // Every clip verb by its key is refused with the reason; the staged clip and the clip on air are untouched.
            foreach (var verb in new[] { "PAUSE", "PLAY", "STOP", "RESTART", "END 5", "SEEK 10" })
            {
                Assert.Equal($"ERR '{stagedName}' is the preview's clip, not the air's — a take puts it on the air.", Send(router, $"VIDEO {verb} ON {key}"));
            }
            Assert.False(staged.Paused);
            Assert.Null(staged.SeekedTo);
            Assert.Equal(7, staged.Position);
            Assert.False(onAir.Paused);
            Assert.Null(onAir.SeekedTo);
            Assert.Contains(services.Journal.Tail(10), e => e.Kind == nameof(ShowActionKind.VideoPause) && e.Outcome == "Refused" && e.Target == key);

            // The clip the programme and the preview both hold is the programme's, and is driven.
            Assert.StartsWith($"OK '{VideoClock.NameOfKey(onAir.Wanted.Key)}' paused", Send(router, $"VIDEO PAUSE ON {onAir.Wanted.Key}"));
            Assert.StartsWith("OK", Send(router, $"VIDEO PLAY ON {onAir.Wanted.Key}"));

            // A take puts it on the air: now the key names a clip the programme holds.
            Assert.True(services.Actions.Execute(ShowActionKind.Cut, ActionOrigin.Desk, "").Ok);
            Dispatcher.UIThread.RunJobs();
            services.ReconcileInputs();
            Assert.True(services.AirState.Pattern.Layer1.Enabled);
            var live = fakes.Sources.Last(f => f.Wanted.Key == key && !f.Disposed);
            Assert.StartsWith($"OK '{stagedName}' paused", Send(router, $"VIDEO PAUSE ON {key}"));
            Assert.True(live.Paused);
            Assert.False(onAir.Paused);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheStandbysPreRolledClipIsRefusedByItsKeyAndStaysHeld()
    {
        var b = TestApp.Boot();
        var fakes = AudioFakes.Install(b);
        try
        {
            var (services, _, _) = b;
            var router = new CommandRouter(services);
            var state = services.State;
            var clip = AudioFakes.TempFile(b, "vt.mp4");
            var key = Patterns.Core.Media.InputKeys.Video(clip);

            // Two looks — one with the clip, one plain — and the clip's cue on standby: the pool opens the clip and holds it on its first frame.
            services.BulkEdit(() =>
            {
                state.Pattern.Kind = PatternKind.Media;
                state.Pattern.Media.Source = MediaSource.Video;
                state.Pattern.Media.VideoPath = clip;
                state.Pattern.Media.Loop = false;
            });
            state.LooksAndCues.Looks.Add(new LookConfig { Name = "VT in", Json = LookService.Capture(state) });
            services.BulkEdit(() => state.Pattern.Kind = PatternKind.Grid);
            state.LooksAndCues.Looks.Add(new LookConfig { Name = "Plain", Json = LookService.Capture(state) });
            Dispatcher.UIThread.RunJobs();
            var vtCue = new RunCueConfig { Number = "01.010", Name = "VT" };
            vtCue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ApplyLook, Target = "VT in" });
            services.CueStack.Stack.Cues.Add(vtCue);
            services.CueStack.Standby(vtCue.Id);
            var held = Assert.Single(fakes.Sources, f => f.Wanted.Key == key && !f.Disposed && f.FadeMs < 0);
            Assert.True(held.IsHeld);
            Assert.Equal(PreRoll.State.Ready, services.Video.PreRollStateOf(key));

            // STATE lists it, pre-rolled; the air does not hold it, and no clip is on air.
            Assert.Equal((false, true, false, false), VideosRow(router.StateJson(), key));
            Assert.Empty(services.VideosOnAir());
            Assert.Null(services.VideoOnAir());

            // Every clip verb by its key is refused with the reason: the pre-roll is not released, moved or paused.
            foreach (var verb in new[] { "PAUSE", "PLAY", "STOP", "RESTART", "END 5", "SEEK 10" })
            {
                Assert.Equal($"ERR '{Path.GetFileName(clip)}' is held for the standby — it plays when that goes.", Send(router, $"VIDEO {verb} ON {key}"));
            }
            Assert.True(held.IsHeld);
            Assert.Equal(0, held.Releases);
            Assert.Null(held.SeekedTo);
            Assert.False(held.Paused);
            Assert.Equal(PreRoll.State.Ready, services.Video.PreRollStateOf(key));
            Assert.Equal((false, true, false, false), VideosRow(router.StateJson(), key));
            Assert.Contains(services.Journal.Tail(10), e => e.Kind == nameof(ShowActionKind.VideoPlay) && e.Outcome == "Refused" && e.Target == key);

            // With no key the answer is still the air's: nothing is on air.
            Assert.Equal("ERR " + VideoClock.NoClipWords, Send(router, "VIDEO PLAY"));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AKeyThatIsThePlaylistsItemIsHeldWithThePlaylistsClock()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var clock = T0;
            var (opener, fake) = PutPlaylistOnAir(b, fakes, () => clock);
            var name = Path.GetFileName(opener);
            var key = Patterns.Core.Media.InputKeys.Video(opener);
            Assert.Equal(VideoRole.Playlist, Assert.Single(services.VideosOnAir()).Role);

            // PAUSE by the key: the role's rule follows the clip named — the playlist's clock holds with its item, as with no key.
            clock = T0.AddSeconds(10);
            Assert.Equal($"OK '{name}' paused at 1:02 — the playlist holds with it; VIDEO PLAY runs both on.", Send(router, $"VIDEO PAUSE ON {key}"));
            Assert.True(fake.Paused);
            Assert.True(services.Playlist.Held);
            Assert.Equal(("playlist", true, true, true, "seq,pausable,stoppable"), VideoRow(router.StateJson()));
            clock = T0.AddSeconds(310);
            Tick(services);
            Assert.Equal(opener, services.Bus.Current.PlaylistNow?.Path);                   // held: not advanced past its end

            // PLAY by the key releases both; STOP by the key puts the item and its clock back to the top, held.
            Assert.Equal($"OK '{name}' plays on from 1:02 — the playlist runs on with it.", Send(router, $"VIDEO PLAY ON {key}"));
            Assert.False(fake.Paused);
            Assert.False(services.Playlist.Held);
            Assert.Equal($"OK '{name}' stopped — back to its top, held, and the playlist holds with it. VIDEO PLAY runs both on.", Send(router, $"VIDEO STOP ON {key}"));
            Assert.True(services.Playlist.Held);
            Assert.Equal(0, fake.SeekedTo);
            Assert.StartsWith("OK", Send(router, $"VIDEO PLAY ON {key}"));
            Assert.False(services.Playlist.Held);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ALoopHasNoEndToCountDownTo()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            PutClipOnAir(b, fakes, "walk-in.mp4", loop: true);
            var json = router.StateJson();
            Assert.Contains("\"loops\":true", json);
            Assert.Contains("\"lengthMs\":210000", json);
            Assert.Contains("\"remainingMs\":null", json);
            Assert.Contains("\"remainingMs\":null,\"playing\"", json);   // videos[] too
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void TheHelloNamesTheWireAndNothingOfTheShow()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, _, _) = b;
            services.State.Name = "Secret AGM";
            var hello = services.Beacon.BuildHello();
            var json = hello.ToJson();
            Assert.Equal(services.State.Control.TcpPort, hello.Wire);
            Assert.Equal(services.State.Control.HttpPort, hello.Web);
            Assert.Equal(services.Kernel.ShowBusId, hello.Id);                       // round 87: the install's id; the process rides as inst
            Assert.Equal(services.Beacon.Instance, hello.Instance);
            Assert.DoesNotContain("Secret AGM", json);
            if (services.State.Control.Token.Length > 0) Assert.DoesNotContain(services.State.Control.Token, json);
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }
}
