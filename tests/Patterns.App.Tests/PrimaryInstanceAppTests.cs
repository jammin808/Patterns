using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 78: the show folder's lease is asked for again. A desk that started as the second on its folder —
/// a handover's replacement beside the desk it replaces, or a second window — owns the folder the moment
/// the first has gone: saving comes on and the show is written at once, the break music is its to run,
/// STATE and the Eye say so; and while it is the second desk its exit never clears the recovery record
/// that belongs to the first.
/// </summary>
public class PrimaryInstanceAppTests
{
    private sealed class FakeLease : IInstanceLease
    {
        /// <summary>The first desk has gone: the next ask is answered.</summary>
        public bool Free { get; set; }
        public bool Owned { get; private set; }
        public bool Disposed { get; private set; }
        public int Asks { get; private set; }

        public bool TryAcquire()
        {
            Asks++;
            if (!Owned && Free) Owned = true;
            return Owned;
        }

        public void Dispose() => Disposed = true;
    }

    private static TestApp.Booted BootSecond(FakeLease lease)
    {
        AppServices.LeaseFactory = _ => lease;
        try
        {
            return TestApp.Boot();
        }
        finally
        {
            AppServices.LeaseFactory = null;
        }
    }

    [AvaloniaFact]
    public void ASecondDeskTakesTheFolderOverTheMomentTheFirstHasGone()
    {
        var lease = new FakeLease();
        var b = BootSecond(lease);
        try
        {
            var (services, vm, _) = b;
            Assert.False(services.IsPrimaryInstance);
            Assert.False(services.Persistence.Autosave);
            Assert.Contains("\"primary\":false", new CommandRouter(services).StateJson());
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Find(Patterns.Core.Services.EyeGraph.DeskId)!.Words, w => w.Contains("second desk", StringComparison.Ordinal));
            vm.State.Spotify.Enabled = true;
            services.Spotify.PokeNow();
            Assert.Equal("Break music is run by the first Patterns window.", services.Spotify.Status);

            // An edit while second reaches no file: the first desk saves.
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            services.SaveNow();
            TestApp.FlushFiles(services);
            Assert.False(File.Exists(services.Store.SettingsPath));

            // The poll asks while the first desk is still there: nothing changes.
            services.PollPrimary();
            Assert.False(services.IsPrimaryInstance);
            Assert.True(lease.Asks > 0);

            // The first desk has gone: the next poll owns the folder — saving on, the show written with the edit, the music unlocked, STATE and the Eye say so.
            lease.Free = true;
            services.PollPrimary();
            Assert.True(services.IsPrimaryInstance);
            Assert.True(services.Persistence.Autosave);
            TestApp.FlushFiles(services);
            Assert.True(File.Exists(services.Store.SettingsPath));
            Assert.Contains("LedWall", File.ReadAllText(services.Store.SettingsPath));
            Assert.Contains("owns the show folder", vm.StatusMessage);
            Assert.Contains("\"primary\":true", new CommandRouter(services).StateJson());
            services.Spotify.PokeNow();
            Assert.NotEqual("Break music is run by the first Patterns window.", services.Spotify.Status);
            services.Eye.Refresh();
            Assert.DoesNotContain(services.Eye.Graph.Find(Patterns.Core.Services.EyeGraph.DeskId)!.Words, w => w.Contains("second desk", StringComparison.Ordinal));

            // Owned: the poll asks no more.
            var asks = lease.Asks;
            services.PollPrimary();
            Assert.Equal(asks, lease.Asks);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// Round 85 (P1-04): the boot's newer-schema guard holds through the handover. A second desk on a folder whose show
    /// file a newer build wrote owns the folder when the first has gone — the record, the music — but saving stays off
    /// and the file is never written over, and the words say which.
    /// </summary>
    [AvaloniaFact]
    public void ASecondDeskThatComesToOwnAFileFromANewerBuildKeepsSavingOff()
    {
        var lease = new FakeLease();
        AppServices.LeaseFactory = _ => lease;
        TestApp.Booted b;
        try
        {
            b = TestApp.Boot(prepare: dir =>
            {
                var newer = new ShowState { Name = "From tomorrow", SchemaVersion = ShowState.CurrentSchemaVersion + 2 };
                newer.Pattern.Kind = PatternKind.ColorBars;
                File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), Patterns.Core.Services.JsonUtil.Serialize(newer));
            });
        }
        finally
        {
            AppServices.LeaseFactory = null;
        }
        try
        {
            var (services, vm, _) = b;
            Assert.True(services.Kernel.NewerSchema > 0);
            Assert.False(services.IsPrimaryInstance);
            Assert.False(services.Persistence.Autosave);
            var asWritten = File.ReadAllText(services.Store.SettingsPath);

            lease.Free = true;
            services.PollPrimary();
            Assert.True(services.IsPrimaryInstance);                                          // the folder is this desk's: the record, the music
            Assert.False(services.Persistence.Autosave);                                      // the newer file is still never written over
            Assert.Contains("\"primary\":true", new CommandRouter(services).StateJson());
            Assert.Contains("newer Patterns", vm.StatusMessage);
            Assert.Contains("saving stays off", vm.StatusMessage);

            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            services.SaveNow();
            TestApp.FlushFiles(services);
            Assert.Equal(asWritten, File.ReadAllText(services.Store.SettingsPath));          // byte for byte as the newer build left it
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ASecondDesksExitLeavesTheFirstDesksRecoveryRecordAlone()
    {
        var lease = new FakeLease();
        var b = BootSecond(lease);
        try
        {
            var services = b.Services;
            services.Recovery.Write(live: true, audioPlaying: false);           // the first desk's record, as it stands on the shared folder
            Assert.NotNull(services.Recovery.Read());
            services.Shutdown();
            Assert.NotNull(services.Recovery.Read());                            // kept: a second desk's clean exit is not the show's end
            Assert.True(lease.Disposed);
        }
        finally
        {
            b.Dispose();
        }
    }
}
