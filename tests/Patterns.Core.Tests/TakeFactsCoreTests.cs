using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 79.1: attempts are not facts, so the facts are fields. A journal row carries Visibility and Effect as
/// the executor stamped them and leaves them out when nothing did; the picture the audience sees has an identity
/// of its own (<see cref="LookService.Seen"/>) apart from who owns it (<see cref="LookService.Fingerprint"/>);
/// and the show's own automation is told apart from a hand on a key by one rule.
/// </summary>
public class TakeFactsCoreTests
{
    [Fact]
    public void AJournalRowCarriesTheStampsAsFieldsAndLeavesThemOutWhenNothingStamped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-facts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new ShowLog(dir);
            log.Record("desk", "Take", "", "Done", "TAKE — sandbox faded up on every armed screen.", "OutputsOff", "Changed");
            log.Record("desk", "ApplyLook", "Walk-in", "Done", "");
            // A row a build before round 79 wrote: no fields, the shape the scrub test pins.
            File.AppendAllText(log.Path, "{\"AtUtc\":\"2026-09-01T10:00:00Z\",\"Origin\":\"desk\",\"Kind\":\"Cut\",\"Target\":\"\",\"Outcome\":\"Done\",\"Message\":\"CUT — sandbox is now the program.\"}\n");

            var lines = File.ReadAllLines(log.Path);
            Assert.Equal(3, lines.Length);
            Assert.Contains("\"Visibility\":\"OutputsOff\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"Effect\":\"Changed\"", lines[0], StringComparison.Ordinal);
            Assert.DoesNotContain("Visibility", lines[1], StringComparison.Ordinal);                 // nothing stamped: no field, not a null
            Assert.DoesNotContain("Effect", lines[1], StringComparison.Ordinal);

            var rows = log.Tail(10);
            Assert.Equal(3, rows.Count);
            var take = rows.Single(r => r.Kind == "Take");
            Assert.Equal("OutputsOff", take.Visibility);
            Assert.Equal("Changed", take.Effect);
            var look = rows.Single(r => r.Kind == "ApplyLook");
            Assert.Null(look.Visibility);
            Assert.Null(look.Effect);
            var old = rows.Single(r => r.Kind == "Cut");                                             // the old row reads, its fields empty
            Assert.Null(old.Visibility);
            Assert.Null(old.Effect);
            Assert.Equal("CUT — sandbox is now the program.", old.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SeenIsThePicturesOnTheScreensAndTheFingerprintIsWhoOwnsThem()
    {
        var air = Rig();
        var same = Rig();
        var targets = new[] { "a", "b", "c" };
        Assert.Equal(LookService.Seen(air, targets), LookService.Seen(same, targets));
        Assert.Equal(LookService.Fingerprint(air), LookService.Fingerprint(same));

        // Screen c made its own with the programme's picture: the audience sees the same; the ownership moved.
        ContentTargets.EnsureAssignment(same, "c").Pattern.Kind = PatternKind.Grid;
        ContentTargets.SetOwnPattern(same, "c", true);
        Assert.Equal(LookService.Seen(air, targets), LookService.Seen(same, targets));
        Assert.NotEqual(LookService.Fingerprint(air), LookService.Fingerprint(same));

        // Its own picture changed: the audience sees it.
        same.Independent.Single(x => x.ScreenId == "c").Pattern.Kind = PatternKind.Focus;
        Assert.NotEqual(LookService.Seen(air, targets), LookService.Seen(same, targets));

        // A rig-wide layer a take carries counts too, on a picture that did not move.
        var overlay = Rig();
        overlay.Overlays.Clock.Enabled = !air.Overlays.Clock.Enabled;
        Assert.NotEqual(LookService.Seen(air, targets), LookService.Seen(overlay, targets));
        var black = Rig();
        black.Blackout = true;
        Assert.NotEqual(LookService.Seen(air, targets), LookService.Seen(black, targets));

        // A target's picture on one screen only: the other screens read the same.
        var one = Rig();
        ContentTargets.EnsureAssignment(one, "b").Pattern.Kind = PatternKind.Ramp;
        ContentTargets.SetOwnPattern(one, "b", true);
        Assert.NotEqual(LookService.Seen(air, targets), LookService.Seen(one, targets));
        Assert.Equal(LookService.Seen(air, new[] { "a", "c" }), LookService.Seen(one, new[] { "a", "c" }));
    }

    [Fact]
    public void TheShowsOwnAutomationIsTheOneRuleAndAHandIsEverythingElse()
    {
        var automation = new[] { OriginKind.Cue, OriginKind.Follow, OriginKind.Schedule, OriginKind.Playlist, OriginKind.Stinger, OriginKind.Recovery };
        foreach (var kind in Enum.GetValues<OriginKind>())
        {
            Assert.Equal(automation.Contains(kind), new ActionOrigin(kind).IsAutomation);
        }
        Assert.False(ActionOrigin.Desk.IsAutomation);
        Assert.False(new ActionOrigin(OriginKind.Companion, "FOH deck").IsAutomation);
        Assert.False(new ActionOrigin(OriginKind.Tcp, "", "10.0.0.12:51234").IsAutomation);
        Assert.True(ActionOrigin.Stinger.IsAutomation);
        Assert.True(new ActionOrigin(OriginKind.Cue, "Q12").IsAutomation);
    }

    [Fact]
    public void AResultCarriesTheStampsAndDefaultsToUnstamped()
    {
        var plain = ActionResult.Done("TAKE — sandbox faded up on every armed screen.");
        Assert.Equal(ActionVisibility.Unknown, plain.Visibility);
        Assert.Equal(ActionEffect.NotMeasured, plain.Effect);
        var stamped = plain with { Visibility = ActionVisibility.Blackout, Effect = ActionEffect.OwnOnly };
        Assert.Equal(ActionVisibility.Blackout, stamped.Visibility);
        Assert.Equal(ActionEffect.OwnOnly, stamped.Effect);
        Assert.Equal(plain.Message, stamped.Message);
        Assert.True(stamped.Ok);
    }

    private static ShowState Rig()
    {
        var s = new ShowState();
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "a" });
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "b" });
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "c" });
        s.Pattern.Kind = PatternKind.Grid;
        return s;
    }
}
