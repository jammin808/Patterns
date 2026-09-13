using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The hand-back: a standby that ran the show says so as it rejoins, marks it on disk for a main on
/// the same machine, and the main's and the standby's lines say where the show is.
/// </summary>
public class TwinHandoverTests
{
    [Fact]
    public void AJoinSaysWhenTheStandbyHasTheShowAndAnOlderJoinDoesNot()
    {
        var join = TwinJoin.Parse(new TwinJoin("Backup desk", "PC", "abcd", "k", TookOver: true).ToJson());
        Assert.NotNull(join);
        Assert.True(join!.TookOver);
        var older = TwinJoin.Parse("{\"Name\":\"Backup desk\",\"Machine\":\"PC\",\"Instance\":\"abcd\",\"Key\":\"k\",\"Proto\":1}");
        Assert.NotNull(older);
        Assert.False(older!.TookOver);                                              // a standby of the previous build never claims the show
        Assert.Equal(TwinWord.HandBack, TwinMessage.Parse("HANDBACK").Word);
        Assert.Equal("HANDBACK", TwinMessage.Format(TwinWord.HandBack));
    }

    [Fact]
    public void TheMarkerIsWrittenReadAndHoldsOnlyWhileItsProcessLives()
    {
        var home = Path.Combine(Path.GetTempPath(), "patterns-handover-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(TwinHandover.Read(home));                                   // no folder yet: nothing
            Assert.False(TwinHandover.Holds(null, _ => 1));
            var at = new DateTime(2026, 9, 12, 19, 41, 58, DateTimeKind.Utc);
            TwinHandover.Write(home, new TwinTookOverMarker("Backup desk", "PC", 4242, 77, "C:\\Patterns\\Patterns.exe", at, "MAIN-DESK"));
            var read = TwinHandover.Read(home);
            Assert.NotNull(read);
            Assert.Equal("Backup desk", read!.Standby);
            Assert.Equal(4242, read.Pid);
            Assert.Equal(77, read.StartedAtUtcTicks);
            Assert.Equal(at, read.AtUtc);
            Assert.True(TwinHandover.Holds(read, pid => pid == 4242 ? 77 : null));        // alive, the same process
            Assert.False(TwinHandover.Holds(read, pid => pid == 4242 ? 78 : null));       // the id reused by another program
            Assert.False(TwinHandover.Holds(read, _ => (long?)null));                        // gone
            Assert.False(TwinHandover.Holds(read with { Pid = 0 }, _ => 77));            // never a process
            // The three-answer look: a process that is up but cannot be read from here still holds — a
            // fence, not an absence — and the other answers mean what they meant.
            Assert.True(TwinHandover.Holds(read, _ => ProcessSight.Unreadable()));
            Assert.True(TwinHandover.Holds(read, _ => ProcessSight.Alive(77)));
            Assert.False(TwinHandover.Holds(read, _ => ProcessSight.Alive(78)));
            Assert.False(TwinHandover.Holds(read, _ => ProcessSight.Gone));
            Assert.False(TwinHandover.Holds(read with { Pid = 0 }, _ => ProcessSight.Unreadable()));
            TwinHandover.Clear(home);
            Assert.Null(TwinHandover.Read(home));
            TwinHandover.Clear(home);                                                   // twice is fine
            File.WriteAllText(TwinHandover.PathFor(home), "{ not json");
            Assert.Null(TwinHandover.Read(home));                                       // a marker this build cannot read holds nothing
            Assert.Equal(Path.Combine("C:\\show", TwinHandover.StandbyFolder), TwinHandover.StandbyHome("C:\\show"));
        }
        finally
        {
            try { Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TheLinesSayWhoHasTheShowAndWhatTheLauncherIsDoing()
    {
        var now = new DateTime(2026, 9, 12, 20, 0, 0, DateTimeKind.Utc);
        var linked = new[] { ("Backup desk", now) };
        var held = TwinWatch.DescribeMain(9699, linked, 3, now, holder: "Backup desk");
        Assert.StartsWith("MAIN — the standby Backup desk HAS THE SHOW; this desk's outputs are held closed. TAKE BACK puts the show back here.", held);
        var unlinked = TwinWatch.DescribeMain(9699, Array.Empty<(string, DateTime)>(), 3, now, holder: "Backup desk");
        Assert.Contains("It is not on the link yet — TAKE BACK once it is, or OUTPUTS ON if it is gone.", unlinked);
        var launched = TwinWatch.DescribeMain(9699, Array.Empty<(string, DateTime)>(), 0, now, launcher: "Standby process running (pid 4321).");
        Assert.Equal("MAIN — listening for a standby on port 9699; none connected. Standby process running (pid 4321).", launched);
        Assert.Equal("MAIN — listening for a standby on port 9699; none connected.", TwinWatch.DescribeMain(9699, Array.Empty<(string, DateTime)>(), 0, now));

        var alone = TwinWatch.DescribeStandby(TwinPhase.TookOver, "MAIN-DESK", null, 0, true, now, "at 19:41:58");
        Assert.Equal("TOOK OVER from MAIN-DESK at 19:41:58 — this desk runs the show now. STAND BY AGAIN once MAIN-DESK is back.", alone);
        var back = TwinWatch.DescribeStandby(TwinPhase.TookOver, "MAIN-DESK", now, 0, true, now, "at 19:41:58", linked: true);
        Assert.Contains("MAIN-DESK is back on the link and its TAKE BACK puts the show there again", back);

        var words = TwinHandover.HoldWords("Backup desk", new DateTime(2026, 9, 12, 19, 41, 58, DateTimeKind.Utc));
        Assert.StartsWith("the standby twin Backup desk has the show (took over at ", words);
        Assert.EndsWith(") — TAKE BACK on the Machine page", words);
        Assert.Equal("the standby twin has the show — TAKE BACK on the Machine page", TwinHandover.HoldWords("", null));
    }

    [Fact]
    public void TheWireKnowsTakeBack()
    {
        var cmd = ControlProtocol.Parse("TWIN TAKEBACK");
        Assert.Equal(RemoteCommandKind.Action, cmd.Kind);
        Assert.Equal(ShowActionKind.TwinTakeBack, cmd.Action.Kind);
        Assert.Equal("Twin — take the show back", ActionSpec.Label(ShowActionKind.TwinTakeBack));
    }
}
