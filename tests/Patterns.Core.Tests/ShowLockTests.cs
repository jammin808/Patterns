using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The show lock, pure: the report's words and lights, the allowed-audio list, the config as the machine's own section, and the wire's words.</summary>
public class ShowLockTests
{
    private static LockItem Done(string key, string words) => new(key, ShowLockWords.TitleOf(key), LockItemState.Done, words);

    [Fact]
    public void TheReportSaysWhatIsHeldWhatFailedAndWhatNeedsAnAdministrator()
    {
        Assert.StartsWith("Not locked — LOCK THE MACHINE holds off notifications", LockReport.Unlocked.Summary);
        Assert.Equal("", LockReport.Unlocked.HealthWords(outputsLive: false));
        Assert.StartsWith("SHOW LOCK OFF — the outputs are live", LockReport.Unlocked.HealthWords(outputsLive: true));
        Assert.Equal(CheckLight.Grey, LockReport.Unlocked.Light(false));
        Assert.Equal(CheckLight.Amber, LockReport.Unlocked.Light(true));
        Assert.Equal("off", LockReport.Unlocked.Value);

        var at = new DateTime(2026, 9, 12, 19, 30, 2, DateTimeKind.Utc);
        var held = new LockReport(true, at, new[]
        {
            Done(ShowLockWords.Notifications, "notifications off"),
            Done(ShowLockWords.Sounds, "system sounds off"),
            Done(ShowLockWords.Audio, "3 other apps' audio muted (Spotify let through)"),
            Done(ShowLockWords.Shortcuts, "shortcut keys off"),
            Done(ShowLockWords.Awake, "awake, display on"),
            Done(ShowLockWords.WindowsKey, "Windows key off"),
            new LockItem(ShowLockWords.Updates, ShowLockWords.TitleOf(ShowLockWords.Updates), LockItemState.Done, "no restart pending"),
        }, UpdateRestartPending: false, Auto: true);
        Assert.StartsWith("LOCKED since ", held.Summary);
        Assert.Contains("(with the outputs) — notifications off · system sounds off · 3 other apps' audio muted (Spotify let through) · shortcut keys off · awake, display on · Windows key off · no restart pending", held.Summary);
        Assert.Equal("", held.HealthWords(true));
        Assert.Equal(CheckLight.Green, held.Light(true));
        Assert.Equal("held", held.Value);

        var partly = new LockReport(true, at, new[]
        {
            Done(ShowLockWords.Notifications, "notifications off"),
            new LockItem(ShowLockWords.Sounds, ShowLockWords.TitleOf(ShowLockWords.Sounds), LockItemState.Failed, "could not be set — patterns.log says why"),
            new LockItem(ShowLockWords.Audio, ShowLockWords.TitleOf(ShowLockWords.Audio), LockItemState.Off, ""),
            new LockItem(ShowLockWords.Updates, ShowLockWords.TitleOf(ShowLockWords.Updates), LockItemState.NeedsAdmin, "a restart is pending — pause updates before doors (docs/SHOW-MACHINE.md)"),
        }, UpdateRestartPending: true);
        Assert.Contains("✗ System sounds — could not be set", partly.Summary);
        Assert.Contains("⚠ Windows Update — a restart is pending", partly.Summary);
        Assert.Contains("Windows Update: a restart is pending — pause updates before doors", partly.Summary);
        Assert.Equal("SHOW LOCK: system sounds could not be held", partly.HealthWords(true));
        Assert.Equal(CheckLight.Red, partly.Light(true));
        Assert.Equal("1 item(s) not held", partly.Value);
        Assert.Equal(1, partly.Failed);
        Assert.Equal(1, partly.NeedAdmin);

        var elsewhere = new LockReport(true, at, new[] { new LockItem(ShowLockWords.Notifications, "Notifications", LockItemState.NotHere, "only Windows has this") }, false);
        Assert.Contains("– Notifications — only Windows has this", new LockItem(ShowLockWords.Notifications, "Notifications", LockItemState.NotHere, "only Windows has this").Line);
        Assert.Equal(CheckLight.Green, elsewhere.Light(true));
        Assert.Equal("· Other apps' audio — left as it is", new LockItem(ShowLockWords.Audio, ShowLockWords.TitleOf(ShowLockWords.Audio), LockItemState.Off, "").Line);
    }

    [Fact]
    public void TheWordsForTheMutedAndTheAllowed()
    {
        Assert.Equal("no other app is playing; any that starts is muted", ShowLockWords.MutedWords(0));
        Assert.Equal("1 other app's audio muted", ShowLockWords.MutedWords(1));
        Assert.Equal("4 other apps' audio muted", ShowLockWords.MutedWords(4));
        Assert.Equal("could not be read", ShowLockWords.MutedWords(-1));
        Assert.Equal(new[] { "spotify", "vlc" }, ShowLockWords.Allowed("Spotify, VLC.exe; spotify"));
        Assert.Empty(ShowLockWords.Allowed("  "));
        var allowed = ShowLockWords.Allowed("Spotify");
        Assert.True(ShowLockWords.IsAllowed("Spotify", allowed));
        Assert.True(ShowLockWords.IsAllowed("spotify.exe", allowed));
        Assert.True(ShowLockWords.IsAllowed("SpotifyWebHelper", allowed));
        Assert.False(ShowLockWords.IsAllowed("ms-teams", allowed));
        Assert.False(ShowLockWords.IsAllowed("Outlook", allowed));
    }

    [Fact]
    public void TheLockIsTheMachinesOwnSectionWithTheShowsDefaultsAndTheWireKnowsItsWords()
    {
        var state = new ShowState();
        Assert.True(state.Lock.AutoWithOutputs);
        Assert.True(state.Lock.Notifications);
        Assert.True(state.Lock.Sounds);
        Assert.True(state.Lock.OtherAudio);
        Assert.True(state.Lock.Shortcuts);
        Assert.True(state.Lock.KeepAwake);
        Assert.True(state.Lock.WindowsKey);
        Assert.Equal("Spotify", state.Lock.AllowedAudio);
        Assert.Contains(nameof(ShowState.Lock), TwinSync.LocalSections);          // a standby never copies the main's hold
        Assert.False(TwinSync.IsMirrored(nameof(ShowState.Lock)));

        Assert.Equal(new ShowAction(ShowActionKind.ShowLockOn), ControlProtocol.Parse("SHOWLOCK ON").Action);
        Assert.Equal(new ShowAction(ShowActionKind.ShowLockOff), ControlProtocol.Parse("showlock off").Action);
        Assert.Equal(new ShowAction(ShowActionKind.ShowLockOff), ControlProtocol.Parse("SHOW-LOCK RELEASE").Action);
        Assert.Equal(RemoteCommandKind.ShowLockStatus, ControlProtocol.Parse("SHOWLOCK STATUS").Kind);
        Assert.Equal(RemoteCommandKind.ShowLockStatus, ControlProtocol.Parse("SHOWLOCK").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SHOWLOCK DANCE").Kind);
        Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.ShowLockOn));
        Assert.Equal("Show lock — hold the machine", ActionSpec.Label(ShowActionKind.ShowLockOn));
        Assert.DoesNotContain(ShowActionKind.ShowLockOn, ActionSpec.CueKinds);
    }

    // ---- round 77: the desk's own browser is never muted --------------------------------------

    [Fact]
    public void AProcessUnderTheDeskIsTheDesksOwnAndAnythingElseIsNot()
    {
        // 100 is the desk; 200 is WebView2's browser under it; 300 a renderer under that; 900 Teams under explorer (1).
        var parents = new Dictionary<int, int?> { [200] = 100, [300] = 200, [310] = 300, [900] = 1, [1] = 0, [100] = 1 };
        int? ParentOf(int pid) => parents.TryGetValue(pid, out var p) ? p : null;

        Assert.True(ProcessTree.IsDescendant(200, 100, ParentOf));
        Assert.True(ProcessTree.IsDescendant(300, 100, ParentOf));
        Assert.True(ProcessTree.IsDescendant(310, 100, ParentOf));
        Assert.False(ProcessTree.IsDescendant(900, 100, ParentOf), "another app under the shell is not ours");
        Assert.False(ProcessTree.IsDescendant(100, 100, ParentOf), "the desk is not its own descendant");
        Assert.False(ProcessTree.IsDescendant(0, 100, ParentOf));
        Assert.False(ProcessTree.IsDescendant(4242, 100, ParentOf), "a process the reader cannot place is nobody's");

        // The walk is bounded: a chain deeper than the hop limit is not ours, and a cycle from a reused pid never spins.
        var deep = new Dictionary<int, int?>();
        for (var pid = 2; pid <= 20; pid++) deep[pid] = pid - 1;
        Assert.True(ProcessTree.IsDescendant(5, 1, pid => deep.TryGetValue(pid, out var p) ? p : null));
        Assert.False(ProcessTree.IsDescendant(20, 1, pid => deep.TryGetValue(pid, out var p) ? p : null));
        Assert.True(ProcessTree.IsDescendant(20, 1, pid => deep.TryGetValue(pid, out var p) ? p : null, maxHops: 40));
        var loop = new Dictionary<int, int?> { [7] = 8, [8] = 7 };
        Assert.False(ProcessTree.IsDescendant(7, 100, pid => loop.TryGetValue(pid, out var p) ? p : null));
        Assert.False(ProcessTree.IsDescendant(7, 100, pid => pid));                                   // a reader answering "itself"
        Assert.False(ProcessTree.IsDescendant(7, 100, _ => throw new InvalidOperationException("gone")));   // a process that went while it was read
    }
}
