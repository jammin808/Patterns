using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The walk-in loop must keep running on air while the operator programs the next look.</summary>
public class PlaylistOnAirTests
{
    private static void Tick(AppServices services)
    {
        // The 250 ms timer body, driven directly (no waiting on the clock).
        typeof(PlaylistService)
            .GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(services.Playlist, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheOnAirPlaylistKeepsPlayingWhileThePreviewShowsSomethingElse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-playlist-air-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var slideA = Path.Combine(dir, "a.png");
        var slideB = Path.Combine(dir, "b.png");
        File.WriteAllBytes(slideA, new byte[] { 1 });
        File.WriteAllBytes(slideB, new byte[] { 1 });

        var services = new AppServices(new SettingsStore(dir));
        AppServices.Instance = services;
        var vm = new MainViewModel(services);
        var window = new MainWindow { DataContext = vm };
        services.AttachMainWindow(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var media = services.State.Pattern.Media;
            services.State.Pattern.Kind = PatternKind.Media;
            media.Source = MediaSource.Playlist;
            PlaylistSequencer.Normalize(media.Playlist);
            media.Playlist.Sections[0].Items.Add(new PlaylistItemConfig { Path = slideA, DurationSeconds = 30 });
            media.Playlist.Sections[0].Items.Add(new PlaylistItemConfig { Path = slideB, DurationSeconds = 30 });
            Dispatcher.UIThread.RunJobs();

            Tick(services);
            Assert.Equal(slideA, services.Bus.Current.PlaylistNow?.Path);

            services.StartDefaultSandbox();
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Sandbox.Active);

            // The operator builds a completely different look in the sandboxed preview —
            // one that is not a playlist at all.
            services.State.Pattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();
            Tick(services);

            // The audience keeps their slideshow: the sequencer follows air, not the preview.
            Assert.Equal(PatternKind.Media, services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(slideA, services.Bus.Current.PlaylistNow?.Path);
            Assert.Contains("Playing", services.Playlist.Status);

            // …and the preview is showing the operator's work.
            Assert.Equal(PatternKind.Focus, services.Bus.Sandbox!.State.Pattern.Kind);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }
}
