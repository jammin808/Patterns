using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Show Bus 1.3 §13.16.5: the acceptor's list — each app link's light and mark (✓ verified, △ plain from another machine or seen in
/// clear, ○ ended, never red), its words with no secret, ended links kept grey for peer.forget.ms and no longer, refusals counted
/// this minute and bounded, the shared words every reader takes, and the row's menu (close it, revoke its key) beside the buttons.
/// </summary>
public class AcceptorListTests
{
    private static readonly DateTime T0 = new(2026, 11, 15, 20, 14, 3, DateTimeKind.Utc);

    private static AppLinkRow Row(string lid = "c1bb791379aa366c", bool local = false, bool tls = true, string verified = "key", string scope = "drive")
        => new(lid, "main:3f9c2a1b7d0e", "Stagewatch Hall A", "10.0.0.7", local, tls, verified, verified == "code" ? "code" : "e63c317763b1454f", scope, T0);

    [Fact]
    public void AVerifiedLinkIsGreenPlainFromAnotherMachineOrSeenInClearIsAmberAndAnEndedOneGreyNeverRed()
    {
        var tls = Row();
        Assert.Equal((CheckLight.Green, "✓", 1, "main", "3f9c2a1b7d0e"), (tls.Light, tls.Mark, tls.Level, tls.T, tls.Id));
        Assert.Empty(tls.Flags);
        Assert.Equal("e63c317763b1454f", tls.KeyKid);

        var plainLocal = Row(local: true, tls: false);
        Assert.Equal(CheckLight.Green, plainLocal.Light);                         // i on a local link, never amber
        Assert.Equal(new[] { "local" }, plainLocal.Flags);

        var plainRemote = Row(tls: false);
        Assert.Equal((CheckLight.Amber, "△"), (plainRemote.Light, plainRemote.Mark));
        Assert.Equal(new[] { "plain" }, plainRemote.Flags);
        Assert.Contains("proved, not private", plainRemote.Words, StringComparison.Ordinal);

        var clear = Row() with { ClearSeen = true };
        Assert.Equal(CheckLight.Amber, clear.Light);
        Assert.Contains("its key was seen in clear", clear.Words, StringComparison.Ordinal);

        var ended = plainRemote with { EndedUtc = T0.AddSeconds(5), EndWords = "closed by this desk's operator" };
        Assert.Equal((CheckLight.Grey, "○"), (ended.Light, ended.Mark));
        Assert.EndsWith("ended 20:14:08 UTC — closed by this desk's operator", ended.Words, StringComparison.Ordinal);

        // A code's link names the token, and has no key a revoke could reach.
        var code = Row(verified: "code", scope: "operate");
        Assert.Equal("", code.KeyKid);
        Assert.Contains("the pairing token", code.Words, StringComparison.Ordinal);
        Assert.Equal("Stagewatch Hall A (Stagewatch 3f9c2a1b7d0e, 10.0.0.7) · key e63c317763b1454f · scope drive · line over TLS · since 20:14:03 UTC", tls.Words);
    }

    [Fact]
    public void AnEndedLinkStaysGreyForPeerForgetMsAndThenLeavesTheList()
    {
        var list = new AcceptorList();
        Assert.Equal(60000, (int)AcceptorList.ForgetAfter.TotalMilliseconds);    // Show Bus §14 peer.forget.ms
        list.Ended(Row(), T0, "the connection ended");
        var kept = Assert.Single(list.EndedRows(T0.AddMilliseconds(60000)));    // within the window includes its end
        Assert.Equal(("the connection ended", true), (kept.EndWords, kept.Ended));
        Assert.Empty(list.EndedRows(T0.AddMilliseconds(60001)));

        // Bounded: the oldest ended link gives way past the bound, and one lid is one row.
        for (var i = 0; i < AcceptorList.MaxEnded + 5; i++) list.Ended(Row(lid: i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)), T0.AddMilliseconds(70000 + i), "gone");
        list.Ended(Row(lid: "0000000000000024"), T0.AddMilliseconds(80000), "again");
        var rows = list.EndedRows(T0.AddMilliseconds(80000));
        Assert.Equal(AcceptorList.MaxEnded, rows.Count);
        Assert.Single(rows, r => r.Lid == "0000000000000024");
    }

    [Fact]
    public void RefusalsAreCountedThisMinutePerAddressConnectorKidAndClassAndBounded()
    {
        var list = new AcceptorList();
        for (var i = 0; i < 3; i++) list.Refused("10.0.0.12", "playmap:0a1b2c3d", "e63c317763b1454f", "refused", "this key was revoked", T0.AddSeconds(i));
        list.Refused("10.0.0.12", "playmap:0a1b2c3d", "code", "binding", "this key speaks for Stagewatch a1b2", T0.AddSeconds(4));
        var rows = list.Refusals(T0.AddSeconds(5));
        Assert.Equal(2, rows.Count);
        var revoked = rows.Single(r => r.Class == "refused");
        Assert.Equal(3, revoked.Count);
        Assert.Equal("PlayMap 0a1b2c3d (10.0.0.12): key e63c317763b1454f refused — this key was revoked ×3 this minute", revoked.Line);
        Assert.Equal("PlayMap 0a1b2c3d (10.0.0.12): the pairing token refused (binding) — this key speaks for Stagewatch a1b2 ×1 this minute", rows.Single(r => r.Class == "binding").Line);
        // A refusal a minute old is swept; past the bound a refusal is counted, never a row.
        Assert.Empty(list.Refusals(T0.AddSeconds(66)));
        for (var i = 0; i < AcceptorList.MaxRefusals + 3; i++) list.Refused("10.0.0." + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "main:x", "code", "refused", "w", T0.AddSeconds(70));
        Assert.Equal(AcceptorList.MaxRefusals, list.Refusals(T0.AddSeconds(70)).Count);
        Assert.Equal(3, list.RefusalsUncounted);
        // The bound's leftovers are said beside the rows, never dropped silently.
        Assert.Equal("", AcceptorWords.Uncounted(0));
        Assert.Equal("+3 more LINK refusals this run were counted and not listed (the list keeps 32 a minute)", AcceptorWords.Uncounted(list.RefusalsUncounted));
        Assert.StartsWith("+1 more LINK refusal this run was counted", AcceptorWords.Uncounted(1), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryReaderTakesTheListsWordsFromOnePlace()
    {
        Assert.Equal("", AcceptorWords.Line(Array.Empty<AppLinkRow>()));
        Assert.Equal("", AcceptorWords.Glance(Array.Empty<AppLinkRow>()));
        Assert.Equal(CheckLight.Grey, AcceptorWords.Worst(Array.Empty<AppLinkRow>()));

        var one = new[] { Row() };
        Assert.Equal("1 app link by LINK: Stagewatch Hall A ✓ key e63c317763b1454f drive TLS", AcceptorWords.Line(one));
        Assert.Equal("APP LINK ✓", AcceptorWords.Glance(one));
        Assert.Equal(CheckLight.Green, AcceptorWords.Worst(one));

        var plain = new[] { Row(tls: false) };
        Assert.Equal("APP LINK △ PLAIN", AcceptorWords.Glance(plain));
        Assert.Equal(CheckLight.Amber, AcceptorWords.Worst(plain));

        var mixed = new[] { Row(), Row(lid: "a", tls: false), Row(lid: "b") with { ClearSeen = true }, Row(lid: "c") with { EndedUtc = T0 } };
        Assert.Equal("APP LINKS 3 △ (1 plain, 1 in clear)", AcceptorWords.Glance(mixed));
        Assert.StartsWith("3 app links by LINK (1 ended in the last minute): ", AcceptorWords.Line(mixed), StringComparison.Ordinal);
        // An ended link says nothing on the glance and nothing amber: the Nodes page lists it.
        Assert.Equal("", AcceptorWords.Glance(new[] { Row(tls: false) with { EndedUtc = T0 } }));
        Assert.Equal(CheckLight.Grey, AcceptorWords.Worst(new[] { Row(tls: false) with { EndedUtc = T0 } }));
        // The signature moves when a row is rescoped, marked or ended.
        Assert.NotEqual(AcceptorWords.Signature(one), AcceptorWords.Signature(new[] { Row(scope: "follow") }));
        Assert.NotEqual(AcceptorWords.Signature(one), AcceptorWords.Signature(new[] { Row() with { EndedUtc = T0 } }));
    }

    [Fact]
    public void AnAppLinksMenuClosesItAndRevokesItsKeyAsTheRowsButtonsDoTheOperatorsAlone()
    {
        var menu = DeskMenus.AppLink(new DeskFacts(), MenuAppLink.Of(Row()));
        Assert.Equal(("applink", "c1bb791379aa366c"), (menu.Kind, menu.Subject));
        var entries = menu.Flatten().ToList();
        var close = entries.Single(e => e.Id == "link.close");
        Assert.Equal(new ShowAction(ShowActionKind.AppLinkClose, "c1bb791379aa366c"), close.Action);
        Assert.True(close.IsEnabled);
        Assert.Equal(new ShowAction(ShowActionKind.AppKeyRevoke, "e63c317763b1454f"), entries.Single(e => e.Id == "link.revoke").Action);
        Assert.All(entries, e => Assert.Equal("", e.Wire));                         // no wire line: the operator's on the desk

        // A code's link has no key of its own: the entry says why and runs nothing; an ended link has nothing to close.
        var code = DeskMenus.AppLink(new DeskFacts(), MenuAppLink.Of(Row(verified: "code") with { EndedUtc = T0 })).Flatten().ToList();
        Assert.Null(code.Single(e => e.Id == "link.revoke").Action);
        Assert.False(code.Single(e => e.Id == "link.revoke").IsEnabled);
        Assert.False(code.Single(e => e.Id == "link.close").IsEnabled);
        // On a node, closing and revoking are the desk's.
        Assert.All(DeskMenus.AppLink(new DeskFacts { IsNode = true }, MenuAppLink.Of(Row())).Flatten(), e => Assert.False(e.IsEnabled));

        // The kind is the desk's alone: no cue carries it, the recorder writes no line for it.
        Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.AppLinkClose));
        Assert.Contains(ShowActionKind.AppLinkClose, WireWriter.Unsayable);
        Assert.Equal("", WireWriter.Line(new ShowAction(ShowActionKind.AppLinkClose, "c1bb791379aa366c")));
    }

    [Fact]
    public void TheEyeDrawsEachAppLinkAsTheOneEdgeFromItsAppsCardCountedOnceNeverAThingOrADeck()
    {
        var stagewatch = Row();                                                                // TLS, key, drive: green
        var playmap = Row(lid: "f0f0f0f0f0f0f0f0", tls: false, scope: "follow") with { Client = "playmap:0a1b2c3d", Name = "Stage left" };
        var none = EyeGraph.Build(new EyeFacts());
        var facts = new EyeFacts { AppLinks = new[] { stagewatch with { Drove = true }, playmap } };
        var g = EyeGraph.Build(facts);

        // No node of its own, no deck: each link is an edge from its app's card, which LINK's proved id makes when no hello has.
        Assert.DoesNotContain(g.Nodes, n => n.Kind == EyeKind.Deck || n.Id.StartsWith("applink:", StringComparison.Ordinal));
        var card = g.Nodes.Single(n => n.Id == "app:3f9c2a1b7d0e");
        Assert.Equal((EyeKind.ShowApp, "Stagewatch Stagewatch Hall A", false, "3f9c2a1b7d0e"), (card.Kind, card.Label, card.Counted, card.App));
        var drives = Assert.Single(g.Edges, e => e.From == card.Id && e.To == EyeGraph.DeskId);
        Assert.Equal((EyeEdgeKind.Controls, CheckLight.Green, true), (drives.Kind, drives.Light, drives.Counted));   // it ran a verb: controls
        Assert.StartsWith("✓ linked to this desk by LINK · key e63c317763b1454f · scope drive", drives.Words, StringComparison.Ordinal);
        var follows = Assert.Single(g.Edges, e => e.From == "app:0a1b2c3d" && e.To == EyeGraph.DeskId);
        Assert.Equal((EyeEdgeKind.Follows, CheckLight.Amber), (follows.Kind, follows.Light));                        // it only read: follows
        Assert.Contains("plain", follows.Words, StringComparison.Ordinal);
        // Each app and its link one counted thing, lit by the link: two things more than a desk with none, one of them amber.
        Assert.Equal((none.Things + 2, none.Amber + 1), (g.Things, g.Amber));

        // An app heard on the show network and linked by LINK is one card, its link folded into it — never a second thing.
        var heard = new EyeShowApp("3f9c2a1b7d0e", "Stagewatch", "Hall A", ShowBusState.Live, false, "");
        var both = EyeGraph.Build(new EyeFacts { ShowApps = new[] { heard }, AppLinks = new[] { stagewatch } });
        Assert.Single(both.Nodes, n => n.App == "3f9c2a1b7d0e" && n.Kind == EyeKind.ShowApp);
        Assert.Equal(none.Things + 1, both.Things);
        Assert.Contains(both.Edges, e => e.From == "app:3f9c2a1b7d0e" && e.Kind == EyeEdgeKind.Follows && e.Counted);

        // Ended: grey on the card that stands, counted by nothing; with no card of its own, nothing is drawn (the Nodes page lists it).
        var ended = EyeGraph.Build(new EyeFacts { ShowApps = new[] { heard }, AppLinks = new[] { stagewatch with { EndedUtc = T0, EndWords = "closed by this desk's operator" }, playmap with { EndedUtc = T0 } } });
        var grey = Assert.Single(ended.Edges, e => e.From == "app:3f9c2a1b7d0e" && e.To == EyeGraph.DeskId && e.Kind != EyeEdgeKind.Hears);
        Assert.Equal(CheckLight.Grey, grey.Light);
        Assert.False(grey.Counted);
        Assert.Equal("○ its link to this desk ended — closed by this desk's operator", grey.Words);
        Assert.DoesNotContain(ended.Nodes, n => n.Id == "app:0a1b2c3d");

        // The card's menu carries the row's own entries, the same actions as the Nodes page's CLOSE and REVOKE KEY.
        var d = new DeskFacts { AppLinks = facts.AppLinks.Select(MenuAppLink.Of).ToList() };
        var menu = EyeMenus.For(d, g, card);
        var group = Assert.Single(menu.Groups, m => m.Heading == "ITS LINK TO THIS DESK");
        Assert.Contains(group.Entries, e => e.Id == "link.close" && e.Action == new ShowAction(ShowActionKind.AppLinkClose, "c1bb791379aa366c"));
        Assert.Contains(group.Entries, e => e.Id == "link.revoke" && e.Action == new ShowAction(ShowActionKind.AppKeyRevoke, "e63c317763b1454f"));
    }

    private static CheckFacts RemoteFacts(params AppLinkRow[] links) => new()
    {
        RemoteEnabled = true, WirePort = 9697, AppLinks = links,
    };

    [Fact]
    public void TheSuperChecksAppLinksRowAndTheRemoteTileReadTheListsLights()
    {
        CheckRow Check(CheckFacts f) => SuperCheck.Run(f).Rows.Single(r => r.Section == "REMOTE" && r.Item == "App links");

        // No app link: no row.
        Assert.DoesNotContain(SuperCheck.Run(RemoteFacts()).Rows, r => r.Item == "App links");

        // A green link: the row green, its value the list's line; the tile is not amber for it.
        var green = Check(RemoteFacts(Row()));
        Assert.Equal((CheckLight.Green, "1 app link by LINK: Stagewatch Hall A ✓ key e63c317763b1454f drive TLS"), (green.Light, green.Value));
        Assert.DoesNotContain("FIX", green.Note, StringComparison.Ordinal);
        Assert.NotEqual("app link amber", HealthDashboard.Tiles(RemoteFacts(Row())).Single(t => t.Id == "remote").Value);

        // Plain from another machine, and a key seen in clear: amber, each with its FIX.
        var plain = Check(RemoteFacts(Row(tls: false)));
        Assert.Equal(CheckLight.Amber, plain.Light);
        Assert.StartsWith("FIX: Stagewatch Hall A: plain from another machine — link it over TLS", plain.Note, StringComparison.Ordinal);
        var clear = Check(RemoteFacts(Row() with { ClearSeen = true }));
        Assert.Equal(CheckLight.Amber, clear.Light);
        Assert.Contains("its key was seen in clear — REVOKE KEY, then KEY MAKE", clear.Note, StringComparison.Ordinal);
        var tile = HealthDashboard.Tiles(RemoteFacts(Row(tls: false))).Single(t => t.Id == "remote");
        Assert.Equal((CheckLight.Amber, "app link amber"), (tile.Light, tile.Value));
        Assert.Contains("(Nodes page, APP LINKS)", tile.Detail, StringComparison.Ordinal);

        // Only ended links: the row green, saying none is open; an ended plain link lights nothing amber.
        var ended = Check(RemoteFacts(Row(tls: false) with { EndedUtc = T0 }));
        Assert.Equal((CheckLight.Green, "0 app links by LINK (1 ended in the last minute) — none open"), (ended.Light, ended.Value));
    }
}
