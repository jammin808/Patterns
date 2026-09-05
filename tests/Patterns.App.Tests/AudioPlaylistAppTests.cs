using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The audio playlist on a live desk: rows and a folder read into one order, PLAY landing on the
/// first row, NEXT / PREV wrapping, a track by number, name and id from the wire and a cue, a
/// natural end moving on and the list stopping at its end without loop, a missing file skipped,
/// the ▶ NOW marker, STATE's block, the refusals, and the pages.
/// </summary>
public class AudioPlaylistAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static string Track(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 1 });
        return path;
    }

    [AvaloniaFact]
    public void TheListPlaysInOrderStepsWrapsAndTakesATrackByNumberNameAndId()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var router = new CommandRouter(services);
            var list = vm.State.AudioPlayer;
            var walkIn = Track(b.Dir, "walk-in.mp3");
            var intro = Track(b.Dir, "intro.wav");
            var folder = Path.Combine(b.Dir, "bed");
            Directory.CreateDirectory(folder);
            var closing = Track(folder, "closing.mp3");
            var opener = Track(folder, "01 opener.mp3");
            list.Items.Add(new AudioTrackConfig { Path = walkIn, Name = "Walk-in" });
            list.Items.Add(new AudioTrackConfig { Path = intro });
            list.Folders.Add(folder);
            list.Loop = false;

            // Nothing on until PLAY; the order is the rows, then the folder in name order.
            services.AudioPlayer.Poll();
            Assert.Equal(4, services.AudioPlayer.Count);
            Assert.Equal(new[] { walkIn, intro, opener, closing }, services.AudioPlayer.Order);
            Assert.Equal(-1, services.AudioPlayer.NowIndex);
            Assert.Equal("Walk-in", services.AudioPlayer.CurrentName);              // what PLAY would start
            Assert.Contains("\"audio\":{\"playing\":false,\"track\":\"Walk-in\",\"n\":0,\"count\":4", router.StateJson());

            // PLAY: the first row, marked ▶ NOW, said on the panel and in STATE.
            Assert.StartsWith("OK", Send(router, "AUDIO PLAY"));
            Assert.True(list.Playing);
            Assert.Equal(0, services.AudioPlayer.NowIndex);
            Assert.Equal(walkIn, services.AudioPlayer.NowPath);
            Assert.True(list.Items[0].IsNowPlaying);
            Assert.False(list.Items[1].IsNowPlaying);
            Assert.Equal("intro", services.AudioPlayer.NextName);
            vm.PollNow();
            Assert.Contains("1/4: Walk-in", vm.AudioPlayerStatus);
            Assert.Contains("next: intro", vm.AudioPlayerStatus);
            Assert.Contains("\"playing\":true,\"track\":\"Walk-in\",\"n\":1,\"count\":4,\"next\":\"intro\"", router.StateJson());
            Assert.Contains("\"items\":[{\"n\":1,\"name\":\"Walk-in\"},{\"n\":2,\"name\":\"intro\"},{\"n\":3,\"name\":\"01 opener\"}", router.StateJson());

            // NEXT / PREV step and wrap; a track by number, by name, by a row's id.
            Assert.StartsWith("OK", Send(router, "AUDIO NEXT"));
            Assert.Equal(1, services.AudioPlayer.NowIndex);
            Assert.True(list.Items[1].IsNowPlaying);
            Assert.False(list.Items[0].IsNowPlaying);
            Assert.StartsWith("OK", Send(router, "AUDIO PREV"));
            Assert.Equal(0, services.AudioPlayer.NowIndex);
            Assert.StartsWith("OK", Send(router, "AUDIO PREV"));
            Assert.Equal(3, services.AudioPlayer.NowIndex);                            // wrapped to the last
            Assert.Equal(closing, services.AudioPlayer.NowPath);
            Assert.StartsWith("OK", Send(router, "AUDIO PLAY 3"));
            Assert.Equal(opener, services.AudioPlayer.NowPath);
            Assert.StartsWith("OK", Send(router, "AUDIO PLAY walk-in"));
            Assert.Equal(walkIn, services.AudioPlayer.NowPath);
            Assert.StartsWith("OK", Send(router, "AUDIO PLAY closing"));
            Assert.Equal(closing, services.AudioPlayer.NowPath);
            Assert.Contains("No audio track 'Nobody'", Send(router, "AUDIO PLAY Nobody"));
            Assert.StartsWith("ERR", Send(router, "AUDIO PLAY 9"));
            var cue = new CueActionConfig { Kind = CueActionKind.AudioPlay, Target = list.Items[1].Id };
            Assert.True(services.Actions.Execute(ShowActions.ToShowAction(cue), new ActionOrigin(OriginKind.Cue, "01.010")).Ok);
            Assert.Equal(intro, services.AudioPlayer.NowPath);
            Assert.True(services.Actions.Execute(ShowActions.ToShowAction(new CueActionConfig { Kind = CueActionKind.AudioNext }), new ActionOrigin(OriginKind.Cue, "01.020")).Ok);
            Assert.Equal(opener, services.AudioPlayer.NowPath);
            Assert.Contains(services.Journal.Tail(12), e => e.Kind == nameof(ShowActionKind.AudioNext));
            Assert.StartsWith("OK", Send(router, "AUDIO VOL 40"));
            Assert.Equal(40, list.VolumePct);

            // The panel's own keys.
            vm.AudioNextCommand.Execute(null);
            Assert.Equal(closing, services.AudioPlayer.NowPath);
            Assert.Contains("4/4", vm.StatusMessage);
            vm.PlayAudioItemCommand.Execute(list.Items[0]);
            Assert.Equal(walkIn, services.AudioPlayer.NowPath);

            // A natural end moves on; at the end without loop the list stops and says so; with loop it starts over.
            services.AudioPlayer.PlayAt(2);
            services.AudioPlayer.TrackEnded();
            Assert.Equal(closing, services.AudioPlayer.NowPath);
            Assert.True(list.Playing);
            services.AudioPlayer.TrackEnded();
            Assert.False(list.Playing);
            Assert.Equal(-1, services.AudioPlayer.NowIndex);
            Assert.Contains("ended", services.AudioPlayer.Status);
            Assert.All(list.Items, i => Assert.False(i.IsNowPlaying));
            list.Loop = true;
            services.AudioPlayer.PlayAt(3);
            services.AudioPlayer.TrackEnded();
            Assert.Equal(walkIn, services.AudioPlayer.NowPath);
            Assert.True(list.Playing);

            // A file that vanished is skipped in the direction of travel; the list carries on.
            File.Delete(intro);
            services.AudioPlayer.PlayAt(1);
            Assert.Equal(opener, services.AudioPlayer.NowPath);

            // STOP clears the marker; an empty list is refused.
            Assert.StartsWith("OK", Send(router, "AUDIO STOP"));
            Assert.False(list.Playing);
            services.AudioPlayer.Poll();
            Assert.All(list.Items, i => Assert.False(i.IsNowPlaying));
            list.Items.Clear();
            list.Folders.Clear();
            services.AudioPlayer.Poll();
            Assert.Contains("empty", Send(router, "AUDIO PLAY"));
            Assert.Contains("empty", Send(router, "AUDIO NEXT"));
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void TheOldSingleTrackStillPlaysAndThePagesShowTheList()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var list = vm.State.AudioPlayer;

            // A show that only ever named one file (set by hand, as an older test does): it is the whole list.
            var old = Track(b.Dir, "bed.mp3");
            list.Path = old;
            services.AudioPlayer.Poll();
            Assert.Equal(new[] { old }, services.AudioPlayer.Order);
            list.Playing = true;
            services.AudioPlayer.Poll();
            Assert.Equal(old, services.AudioPlayer.NowPath);
            list.Playing = false;
            list.Path = "";

            // The pages: the Audio page's block with its keys and rows, the Show panel's block.
            list.Items.Add(new AudioTrackConfig { Path = Track(b.Dir, "walk-in.mp3"), Name = "Walk-in" });
            vm.SelectPage(Shell.IndexOf("Audio"));
            Settle(window);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("AUDIO PLAYLIST", texts);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "+ Add tracks…");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "+ Add folder…");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "⏭ NEXT");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "RESHUFFLE");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), x => x.Text == "Walk-in");

            vm.SelectPage(Shell.PanelPage);
            Settle(window);
            Assert.Contains("AUDIO PLAYLIST", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "⏭");

            // The cue editor offers the rows by id for Play audio.
            var stack = services.CueStack.Stack;
            var cue = new RunCueConfig { Number = "1", Name = "Music" };
            stack.Cues.Add(cue);
            vm.Cues.SelectedStack = stack;
            vm.Cues.SelectedCue = cue;
            vm.Cues.AddActionCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var row = vm.Cues.ActionRows.Last();
            row.SelectedKind = row.KindChoices.First(k => k.Id == nameof(CueActionKind.AudioPlay));
            Assert.Contains(row.TargetChoices, t => t.Id == list.Items[0].Id && t.Label.Contains("Walk-in"));
            Assert.Contains("blank", row.TargetHint);

            // Help carries the list on the pages it lives on.
            Assert.Contains(HelpTopics.ForPage("Audio"), t => t.Id == "audio-playlist");
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }
}
