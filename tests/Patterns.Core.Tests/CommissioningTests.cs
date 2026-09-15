using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Round 65.10: the commissioning flow judged from facts alone — each stage's light and its next step, the headline, the wire's verbs.</summary>
public class CommissioningTests
{
    private static CommissionScreen Screen(int n, string label, bool contract = true, bool route = false, bool edid = true, bool advertises = true, SignalVerdict verdict = SignalVerdict.Match, string observed = "")
        => new(n, label, contract, route, edid, advertises, verdict, observed);

    private static CommissioningFacts Ready() => new()
    {
        DisplaysSeen = 2,
        EnabledScreens = 2,
        Screens = new[] { Screen(1, "Screen 1 · Desk"), Screen(2, "Screen 2 · LED wall") },
        OutputsLive = true,
        KnownGoodSaved = true,
        KnownGoodSame = true,
        KnownGoodWords = "unchanged since 2026-09-15 09:00 (first show)",
    };

    [Fact]
    public void AnEmptyRigStartsAtDiscoverAndSaysWhatToDo()
    {
        var report = Commissioning.Build(new CommissioningFacts());
        Assert.Equal(7, report.Total);
        Assert.Equal(0, report.Done);
        Assert.False(report.Complete);
        Assert.Equal(CommissionStage.Discover, report.Current!.Stage);
        Assert.Equal(CheckLight.Grey, report.Current.Light);
        Assert.Contains("plug the links in", report.Next);
        Assert.StartsWith("0 of 7 stages green · at Discover: no display seen", report.Headline);
        Assert.Equal(CheckLight.Grey, report.Overall);
        Assert.All(report.Words, w => Assert.StartsWith("· ", w));
    }

    [Fact]
    public void ACommissionedRigIsGreenOnEveryStage()
    {
        var report = Commissioning.Build(Ready());
        Assert.True(report.Complete);
        Assert.Equal(100, report.Percent);
        Assert.Equal("commissioned — every stage green", report.Headline);
        Assert.Equal("", report.Next);
        Assert.Equal(CheckLight.Green, report.Overall);
        Assert.Equal(new[] { "Discover", "Assign", "Contract", "Capability", "Output test", "Verify", "Known good" }, report.Lines.Select(l => l.Title));
        Assert.Contains("✓ Verify: MATCH on every contracted screen (2)", report.Words);
    }

    [Fact]
    public void EachStageHoldsTheFlowWithItsOwnWords()
    {
        // A lost display is red at DISCOVER and names the screen.
        var lost = Commissioning.Build(Ready() with { LostScreens = new[] { "Screen 2 · LED wall — its display is missing (lost at 09:12)" } });
        Assert.Equal((CommissionStage.Discover, CheckLight.Red), (lost.Current!.Stage, lost.Current.Light));
        Assert.Contains("lost at 09:12", lost.Next);
        Assert.Equal(CheckLight.Red, lost.Overall);

        // A planned screen waits at ASSIGN.
        var planned = Commissioning.Build(Ready() with { PlannedScreens = 1 });
        Assert.Equal((CommissionStage.Assign, CheckLight.Amber), (planned.Current!.Stage, planned.Current.Light));
        Assert.Contains("Adopt", planned.Next);

        // A screen without a contract holds at CONTRACT with the verb spelt out.
        var open = Commissioning.Build(Ready() with { Screens = new[] { Screen(1, "Screen 1 · Desk"), Screen(2, "Screen 2 · LED wall", contract: false) } });
        Assert.Equal(CommissionStage.Contract, open.Current!.Stage);
        Assert.Equal("Screen 2 · LED wall without a contract", open.Current.Value);
        Assert.Contains("SCREEN 2 SIGNAL 3840x2160 50 RGB 8 SDR", open.Next);

        // The test route is a proven path, not a commissioned design: CONTRACT stays amber until it is off.
        var route = Commissioning.Build(Ready() with { Screens = new[] { Screen(1, "Screen 1 · Desk"), Screen(2, "Screen 2 · LED wall", route: true) } });
        Assert.Equal((CommissionStage.Contract, CheckLight.Amber), (route.Current!.Stage, route.Current.Light));
        Assert.Equal("Screen 2 · LED wall on TEST ROUTE", route.Current.Value);
        Assert.Contains("SCREEN 2 TESTROUTE OFF", route.Next);
        Assert.Contains(route.Lines, l => l.Stage == CommissionStage.Capability && l.Value == "every display advertises its contract (1)");   // the routed screen is out of the count

        // An EDID that does not offer the contract is amber at CAPABILITY; none read is grey and says why the contract stands anyway.
        var refusing = Commissioning.Build(Ready() with { Screens = new[] { Screen(1, "Screen 1 · Desk"), Screen(2, "Screen 2 · LED wall", advertises: false) } });
        Assert.Equal((CommissionStage.Capability, CheckLight.Amber), (refusing.Current!.Stage, refusing.Current.Light));
        Assert.Contains("ADVERTISED", refusing.Next);
        var unread = Commissioning.Build(Ready() with { Screens = new[] { Screen(1, "Screen 1 · Desk", edid: false), Screen(2, "Screen 2 · LED wall") } });
        Assert.Equal((CommissionStage.Capability, CheckLight.Grey), (unread.Current!.Stage, unread.Current.Light));
        Assert.Contains("engineer's word", unread.Next);

        // The outputs never opened: OUTPUT TEST grey; opened once this run: green.
        var closed = Commissioning.Build(Ready() with { OutputsLive = false });
        Assert.Equal(CommissionStage.OutputTest, closed.Current!.Stage);
        Assert.Contains("IDENTIFY", closed.Next);
        Assert.True(Commissioning.Build(Ready() with { OutputsLive = false, OutputsWereLive = true }).Lines.Single(l => l.Stage == CommissionStage.OutputTest).Light == CheckLight.Green);

        // A MISMATCH is red at VERIFY with the observation and the test route offered; unverified is grey.
        var mismatch = Commissioning.Build(Ready() with { Screens = new[] { Screen(1, "Screen 1 · Desk"), Screen(2, "Screen 2 · LED wall", verdict: SignalVerdict.Mismatch, observed: "3840×2160 · 60 Hz · YCbCr 4:2:0 · 8-bit · SDR") } });
        Assert.Equal((CommissionStage.Verify, CheckLight.Red), (mismatch.Current!.Stage, mismatch.Current.Light));
        Assert.Equal("MISMATCH on Screen 2 · LED wall", mismatch.Current.Value);
        Assert.Contains("YCbCr 4:2:0", mismatch.Next);
        Assert.Contains("SCREEN 2 TESTROUTE ON", mismatch.Next);
        var unverified = Commissioning.Build(Ready() with { Screens = new[] { Screen(1, "Screen 1 · Desk", verdict: SignalVerdict.Unverified), Screen(2, "Screen 2 · LED wall") } });
        Assert.Equal((CommissionStage.Verify, CheckLight.Grey), (unverified.Current!.Stage, unverified.Current.Light));

        // KNOWN GOOD: not saved is grey with the verb; saved and moved is amber; saved and not yet compared is grey.
        var unsaved = Commissioning.Build(Ready() with { KnownGoodSaved = false });
        Assert.Equal((CommissionStage.KnownGood, CheckLight.Grey), (unsaved.Current!.Stage, unsaved.Current.Light));
        Assert.Contains("RIG SAVE first show", unsaved.Next);
        Assert.Equal(6, unsaved.Done);
        var moved = Commissioning.Build(Ready() with { KnownGoodSame = false, KnownGoodWords = "2 changes since 2026-09-15 09:00 (first show)" });
        Assert.Equal(CheckLight.Amber, moved.Current!.Light);
        Assert.Equal("2 changes since 2026-09-15 09:00 (first show)", moved.Current.Value);
        Assert.Equal(CheckLight.Grey, Commissioning.Build(Ready() with { KnownGoodSame = null }).Current!.Light);
    }

    [Fact]
    public void ManyScreensAreCountedNotListed()
    {
        var screens = Enumerable.Range(1, 5).Select(n => Screen(n, $"Screen {n}", contract: false)).ToList();
        var report = Commissioning.Build(Ready() with { Screens = screens, EnabledScreens = 5, DisplaysSeen = 5 });
        Assert.Equal("5 screens without a contract", report.Current!.Value);
        Assert.Contains("SCREEN 1 SIGNAL", report.Next);
    }

    [Fact]
    public void TheWireSpeaksTheFlow()
    {
        Assert.Equal(RemoteCommandKind.CommissionStatus, ControlProtocol.Parse("COMMISSION").Kind);
        Assert.Equal(RemoteCommandKind.CommissionStatus, ControlProtocol.Parse("COMMISSION STATUS").Kind);
        Assert.Equal(RemoteCommandKind.CommissionStatus, ControlProtocol.Parse("commissioning").Kind);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenTestRoute, "2", "ON"), ControlProtocol.Parse("SCREEN 2 TESTROUTE ON").Action);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenTestRoute, "2", "OFF"), ControlProtocol.Parse("SCREEN 2 TEST ROUTE OFF").Action);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenTestRoute, "2", ""), ControlProtocol.Parse("SCREEN 2 TESTROUTE").Action);
    }
}
