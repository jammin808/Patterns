using Patterns.App.Services;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The standby process a main runs on this machine: the launch's words, and the launcher's starts, restarts and end — against a fake process.</summary>
internal sealed class FakeTwinChild : IChildHandle
{
    public int Pid { get; init; }
    public bool HasExited { get; set; }
    public int ExitCode { get; set; }
    public bool Killed { get; private set; }
    public bool WriteLine(string line) => false;
    public string? ReadLine() => null;
    public void Kill() { Killed = true; HasExited = true; }
    public void Dispose() { }
}

public class TwinLauncherTests
{

    [Fact]
    public void TheLaunchNamesTheFolderTheMainAndTheKeyAndReadsTheAddressBack()
    {
        Assert.Equal(new[] { "--home", "C:\\show\\twin-standby", "--standby-of", "127.0.0.1:9699", "--key", "hunter2", "--no-watchdog" }, TwinLaunch.Arguments("C:\\show\\twin-standby", 9699, "hunter2"));
        Assert.Equal(new[] { "--home", "/show/twin-standby", "--standby-of", "127.0.0.1:9699", "--no-watchdog" }, TwinLaunch.Arguments("/show/twin-standby", 9699, ""));
        Assert.Equal(("127.0.0.1", 9699), TwinLaunch.ParseStandbyOf("127.0.0.1:9699"));
        Assert.Equal(("::1", 9700), TwinLaunch.ParseStandbyOf("[::1]:9700"));
        Assert.Null(TwinLaunch.ParseStandbyOf("127.0.0.1"));
        Assert.Null(TwinLaunch.ParseStandbyOf("127.0.0.1:80"));
        Assert.Null(TwinLaunch.ParseStandbyOf(":9699"));
        Assert.Null(TwinLaunch.ParseStandbyOf(""));

        var state = new ShowState();
        state.Twin.Role = TwinRole.Main;
        state.Twin.LocalStandby = true;
        state.Watchdog.BeaconEnabled = true;
        state.Control.Enabled = true;
        TwinLaunch.ConfigureStandby(state, "127.0.0.1", 9699, "hunter2");
        Assert.Equal(TwinRole.Standby, state.Twin.Role);
        Assert.Equal("127.0.0.1", state.Twin.MainHost);
        Assert.Equal(9699, state.Twin.Port);
        Assert.Equal("hunter2", state.Twin.Key);
        Assert.True(state.Twin.AutoTakeOver);
        Assert.False(state.Twin.LocalStandby);                        // a standby never launches a standby of its own
        Assert.False(state.Watchdog.Enabled);                         // the main is its watchdog
        Assert.False(state.Watchdog.BeaconEnabled);
        Assert.False(state.Control.Enabled);                          // the main holds the machine's remote ports
    }

    [Fact]
    public void TheLauncherStartsTheProcessStartsItAgainAfterItExitsWithAGrowingPauseAndEndsItUnlessItHasTheShow()
    {
        var now = new DateTime(2026, 9, 12, 20, 0, 0, DateTimeKind.Utc);
        var launcher = new TwinLauncher(() => now);
        var spawned = new List<(string File, IReadOnlyList<string> Args)>();
        var children = new List<FakeTwinChild>();
        launcher.Spawn = (file, args) =>
        {
            spawned.Add((file, args));
            var child = new FakeTwinChild { Pid = 4000 + children.Count };
            children.Add(child);
            return child;
        };
        launcher.FolderOwned = _ => false;

        Assert.Equal("", launcher.Words);
        launcher.Tick(standbyHoldsShow: false);
        Assert.Empty(spawned);                                                        // nothing wanted, nothing started

        launcher.Want("/show/twin-standby", 9699, "k", standbyHoldsShow: false);
        Assert.Equal("Standby process starting…", launcher.Words);
        launcher.Tick(standbyHoldsShow: false);
        var (_, args) = Assert.Single(spawned);
        Assert.Contains("--standby-of", args);
        Assert.Equal("127.0.0.1:9699", args[args.ToList().IndexOf("--standby-of") + 1]);
        Assert.Contains("--no-watchdog", args);
        Assert.Equal(4000, launcher.Pid);
        Assert.Equal("Standby process running (pid 4000).", launcher.Words);
        launcher.Tick(standbyHoldsShow: false);
        Assert.Single(spawned);                                                       // running: left alone

        // It exits: said, and started again after the pause — two seconds, then four, never past thirty.
        children[0].HasExited = true;
        children[0].ExitCode = 3;
        launcher.Tick(standbyHoldsShow: false);
        Assert.Null(launcher.Pid);
        Assert.Equal("Standby process exited (code 3); starting again in 2 s.", launcher.Words);
        now += TimeSpan.FromSeconds(1);
        launcher.Tick(standbyHoldsShow: false);
        Assert.Single(spawned);                                                       // too soon
        now += TimeSpan.FromSeconds(1.5);
        launcher.Tick(standbyHoldsShow: false);
        Assert.Equal(2, spawned.Count);
        Assert.Equal(4001, launcher.Pid);
        children[1].HasExited = true;
        launcher.Tick(standbyHoldsShow: false);
        Assert.Contains("starting again in 4 s", launcher.Words);

        // A standby that has the show is never doubled while it runs it.
        now += TimeSpan.FromSeconds(10);
        launcher.Tick(standbyHoldsShow: true);
        Assert.Equal(2, spawned.Count);
        // One the previous main started still owns the folder: adopted, not started again.
        launcher.FolderOwned = _ => true;
        launcher.Tick(standbyHoldsShow: false);
        Assert.Equal(2, spawned.Count);
        Assert.Equal("Standby process running (started by the previous main).", launcher.Words);
        launcher.FolderOwned = _ => false;
        now += TimeSpan.FromSeconds(10);
        launcher.Tick(standbyHoldsShow: false);
        Assert.Equal(3, spawned.Count);

        // Switched off, or the desk closing: the process ends with it — unless it has the show.
        launcher.Want(null, 9699, "k", standbyHoldsShow: true);
        Assert.False(children[2].Killed);
        Assert.False(launcher.Wanted);
        launcher.Want("/show/twin-standby", 9699, "k", standbyHoldsShow: false);
        launcher.Tick(standbyHoldsShow: false);
        Assert.Equal(4, spawned.Count);
        launcher.Dispose();
        Assert.True(children[3].Killed);
        Assert.Equal("", launcher.Words);
    }
}
