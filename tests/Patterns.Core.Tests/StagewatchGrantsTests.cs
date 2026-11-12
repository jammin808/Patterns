using System.Text.Json;
using Patterns.Core.Media;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 96 (Show Bus 1.3 §12.4–§12.9): a window a Stagewatch grants this desk — its held form, the grants message read fail closed in
/// the bytes Stagewatch 2.22 sends, the revision per link, the grants kept, the answer in the shape Stagewatch reads, the readiness a
/// live take waits for, "shown" read from the air alone, the certificate a granted page opens to, and the verbs.
/// </summary>
public sealed class StagewatchGrantsTests
{
    private const string Sw = "3f9c2a1b7d0e";
    private static readonly string Token = new('k', 26);
    private static readonly string Path = "/w/" + Token;

    /// <summary>A grants message as Stagewatch's WindowGrants.GrantsJson writes it: type, from, rev, link, then the rows' five fields.</summary>
    private static string Grants(int rev, string link, params (string Window, string Name, string How, string Path, string Cannot)[] rows)
        => "{\"type\":\"grants\",\"from\":\"" + Sw + "\",\"rev\":" + rev + ",\"link\":\"" + link + "\",\"grants\":["
           + string.Join(",", rows.Select(r => $"{{\"window\":\"{r.Window}\",\"name\":\"{r.Name}\",\"how\":\"{r.How}\",\"path\":\"{r.Path}\",\"cannot\":\"{r.Cannot}\"}}"))
           + "]}";

    [Fact]
    public void AGrantIsHeldByItsNodeAndWindowAndNeverByItsAddress()
    {
        var held = WindowAddresses.Grant(Sw, "wk3m9x2");
        Assert.Equal("stagewatch://3f9c2a1b7d0e/wk3m9x2", held);
        Assert.True(WindowAddresses.IsHeld(held));
        Assert.True(WindowAddresses.IsGrant(held));
        Assert.False(WindowAddresses.LooksPrivate(held));
        Assert.Equal(held, WindowAddresses.Held(held));
        Assert.Equal(held, WindowAddresses.Held("STAGEWATCH://3f9c2a1b7d0e/WK3M9X2"));               // the window id in lower case
        Assert.Equal(held, WindowAddresses.PublicForm(held));
        Assert.Equal("web:" + held, InputKeys.Web(held));
        Assert.True(InputKeys.IsHeldWeb(InputKeys.Web(held)));
        Assert.True(WindowAddresses.TryGrant(held, out var node, out var window));
        Assert.Equal((Sw, "wk3m9x2"), (node, window));
        // A held form that names no node or window is inert: unkept, never a grant.
        foreach (var bad in new[] { "stagewatch://3f9c2a1b7d0e/", "stagewatch://ab/w1", "stagewatch://3f9c2a1b7d0e/w-1", "stagewatch://3f9c2a1b7d0e/w1/x" })
        {
            Assert.False(WindowAddresses.IsGrant(bad), bad);
            Assert.StartsWith(WindowAddresses.UnkeptPrefix, WindowAddresses.Held(bad));
        }
        // The book says why it opens nothing; no grant is a kept pass.
        Assert.Equal(new WebPassResolution(null, WebPassBook.NotGrantedWhy), WebPassBook.Empty.Resolve(held));
    }

    [Fact]
    public void TheGrantsMessageIsReadInTheBytesStagewatchSends()
    {
        var json = Grants(3, "tls", ("wk3m9x2", "lobby", "page", Path, ""), ("wq8r4t1", "foyer", "page", "", "this link is not encrypted — link over wss://"), ("wz5n6p7", "stage", "draw", "", ""));
        Assert.True(StagewatchGrants.TryParse(json, Sw, out var heard, out var why), why);
        Assert.Equal((Sw, 3, "tls", 3), (heard.From, heard.Rev, heard.Link, heard.Rows.Count));
        Assert.Equal(new GrantRowHeard("wk3m9x2", "lobby", "page", Path, ""), heard.Rows[0]);
        Assert.Equal("this link is not encrypted — link over wss://", heard.Rows[1].Cannot);
        // An empty list is a list.
        Assert.True(StagewatchGrants.TryParse(Grants(1, "local"), Sw, out var none, out _));
        Assert.Empty(none.Rows);
        // 12.5: a link that has not said which node it is (no verified id, no key bound by id) takes no grants — not even its own.
        Assert.False(StagewatchGrants.TryParse(Grants(1, "local", ("wk3m9x2", "lobby", "page", Path, "")), null, out var unheard, out var unknown));
        Assert.Empty(unheard.Rows);
        Assert.Contains("has not said which node it is", unknown, StringComparison.Ordinal);
        Assert.False(StagewatchGrants.TryParse(Grants(1, "local"), "", out _, out _));
    }

    [Theory]
    [InlineData("wrong from")]
    [InlineData("65th grant")]
    [InlineData("bad path")]
    [InlineData("path on a plain link")]
    [InlineData("path on a draw grant")]
    [InlineData("window named twice")]
    [InlineData("no revision")]
    [InlineData("unknown carrier")]
    [InlineData("unknown way to hold")]
    [InlineData("not json")]
    public void AGrantsMessageThatBreaksARuleIsRefusedWhole(string what)
    {
        var json = what switch
        {
            "wrong from" => Grants(1, "tls", ("wk3m9x2", "lobby", "page", Path, "")).Replace(Sw, "aaaaaaaaaaaa", StringComparison.Ordinal),
            "65th grant" => Grants(1, "tls", Enumerable.Range(0, 65).Select(i => ("w" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "n", "page", "", "x")).ToArray()),
            "bad path" => Grants(1, "tls", ("wk3m9x2", "lobby", "page", "/api/windows", "")),
            "path on a plain link" => Grants(1, "plain", ("wk3m9x2", "lobby", "page", Path, "")),
            "path on a draw grant" => Grants(1, "tls", ("wk3m9x2", "lobby", "draw", Path, "")),
            "window named twice" => Grants(1, "tls", ("wk3m9x2", "lobby", "page", Path, ""), ("wk3m9x2", "lobby", "page", "", "")),
            "no revision" => Grants(1, "tls").Replace("\"rev\":1", "\"rev\":0", StringComparison.Ordinal),
            "unknown carrier" => Grants(1, "wifi"),
            "unknown way to hold" => Grants(1, "tls", ("wk3m9x2", "lobby", "frame", "", "")),
            _ => "{\"type\":\"grants\"",
        };
        Assert.False(StagewatchGrants.TryParse(json, Sw, out var heard, out var why), what);
        Assert.Empty(heard.Rows);
        Assert.NotEmpty(why);
        Assert.DoesNotContain(Token, why, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkTakesItsFirstGrantsWhateverTheirRevisionAndThenOnlyAHigherOne()
    {
        Assert.True(StagewatchGrants.Takes(0, 7));                       // a new link: Stagewatch's rev restarts with it
        Assert.True(StagewatchGrants.Takes(0, 1));
        Assert.True(StagewatchGrants.Takes(3, 4));
        Assert.False(StagewatchGrants.Takes(3, 3));
        Assert.False(StagewatchGrants.Takes(3, 2));
    }

    [Fact]
    public void GrantsSeenIsTheShapeStagewatchReads()
    {
        Assert.Equal("{\"type\":\"grants-seen\",\"rev\":3,\"cannot\":\"\"}", StagewatchGrants.SeenJson(3, ""));
        var long1 = StagewatchGrants.SeenJson(1, new string('x', 500));
        using var doc = JsonDocument.Parse(long1);
        Assert.Equal(StagewatchGrants.MaxCannot, doc.RootElement.GetProperty("cannot").GetString()!.Length);
    }

    [Fact]
    public void GrantsAreKeptUntilAListLeavesThemOutAndAPathlessRowKeepsTheAddressGivenBefore()
    {
        var other = new StagewatchGrant("bbbbbbbbbbbb", "w9", "elsewhere", "page", "https://10.0.0.8:9925", Path);
        Assert.True(StagewatchGrants.TryParse(Grants(1, "tls", ("wk3m9x2", "lobby", "page", Path, ""), ("wz5n6p7", "stage", "draw", "", "")), Sw, out var first, out _));
        var (kept, cannot) = StagewatchGrants.Apply(new[] { other }, first, "https://sw.local:9925", "sha256:" + new string('a', 64));
        Assert.Equal("", cannot);
        Assert.Equal(3, kept.Count);
        Assert.Equal(other, kept[0]);                                      // another Stagewatch's grants stay as they were
        var lobby = kept.Single(g => g.Window == "wk3m9x2");
        Assert.True(lobby.Opens);
        Assert.Equal("https://sw.local:9925" + Path, lobby.Url);
        Assert.Equal("stagewatch://3f9c2a1b7d0e/wk3m9x2", lobby.Held);
        var stage = kept.Single(g => g.Window == "wz5n6p7");
        Assert.False(stage.Opens);
        Assert.Equal(StagewatchGrants.NoDraw, stage.Cannot);

        // A later list whose row carries no path (a plain link now): the address given before still opens, with the row's reason.
        Assert.True(StagewatchGrants.TryParse(Grants(2, "plain", ("wk3m9x2", "lobby", "page", "", "this link is not encrypted — link over wss://")), Sw, out var second, out _));
        var (again, _) = StagewatchGrants.Apply(kept, second, "http://10.0.0.9:9924");
        var still = again.Single(g => g.Sw == Sw);
        Assert.Equal(lobby.Url, still.Url);
        Assert.Equal(lobby.Pin, still.Pin);
        Assert.Equal("this link is not encrypted — link over wss://", still.Cannot);
        Assert.DoesNotContain(again, g => g.Window == "wz5n6p7");          // left out: dropped

        // The list that names none ends them all; the other Stagewatch's stays.
        Assert.True(StagewatchGrants.TryParse(Grants(3, "tls"), Sw, out var empty, out _));
        var (gone, _) = StagewatchGrants.Apply(again, empty, "https://sw.local:9925");
        Assert.Equal(new[] { other }, gone);

        // No page grant that opens: grants-seen says why this desk shows no page.
        Assert.True(StagewatchGrants.TryParse(Grants(4, "plain", ("wq8r4t1", "foyer", "page", "", "this link is not encrypted — link over wss://")), Sw, out var plain, out _));
        var (_, why) = StagewatchGrants.Apply(Array.Empty<StagewatchGrant>(), plain, "");
        Assert.Contains("this link is not encrypted", why, StringComparison.Ordinal);
    }

    [Fact]
    public void RowsPastTheCapAreSaidInTheAnswerNeverDroppedInSilence()
    {
        var others = Enumerable.Range(0, StagewatchGrants.Max - 1)
            .Select(i => new StagewatchGrant("bbbbbbbbbbbb", "o" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "n", "page", "https://10.0.0.8:9925", Path)).ToList();
        Assert.True(StagewatchGrants.TryParse(Grants(1, "tls", ("wk3m9x2", "lobby", "page", Path, ""), ("wq8r4t1", "foyer", "page", Path, ""), ("wz5n6p7", "stage", "draw", "", "")), Sw, out var heard, out _));
        var (kept, cannot) = StagewatchGrants.Apply(others, heard, "https://10.0.0.9:9925", "sha256:" + new string('a', 64));
        Assert.Equal(StagewatchGrants.Max, kept.Count);
        Assert.Equal("wk3m9x2", Assert.Single(kept, g => g.Sw == Sw).Window);
        Assert.StartsWith("this desk keeps at most 64 granted windows — 2 of these were not kept", cannot, StringComparison.Ordinal);   // a page still opens: only the cut is said
        Assert.DoesNotContain(Token, cannot, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(StagewatchGrants.SeenJson(1, cannot));
        Assert.Equal(cannot, doc.RootElement.GetProperty("cannot").GetString());
        // Every row kept: nothing to say.
        Assert.Equal("", StagewatchGrants.Apply(others.Take(10).ToList(), heard, "https://10.0.0.9:9925").Cannot);
    }

    [Fact]
    public void ANameTwoStagewatchesGrantFindsNeitherAndTheNodeFormFindsOne()
    {
        const string sw2 = "bbbbbbbbbbbb";
        var grants = new[]
        {
            new StagewatchGrant(Sw, "lobby", "Lobby", "page", "https://10.0.0.8:9925", Path),
            new StagewatchGrant(sw2, "wq8r4t1", "Lobby", "page", "https://10.0.0.9:9925", Path),
            new StagewatchGrant(sw2, "wz5n6p7", "Stage", "page", "https://10.0.0.9:9925", Path),
        };
        Assert.Equal(grants[0], Assert.Single(StagewatchGrants.Matches(grants, "LOBBY")));  // an id on one and a name on the other: the id wins…
        Assert.Null(StagewatchGrants.Find(new[] { grants[1], grants[0] with { Window = "w1" } }, "Lobby"));   // …two names alone: neither
        var words = StagewatchGrants.AmbiguousWords("Lobby", new[] { grants[1], grants[0] with { Window = "w1" } });
        Assert.Contains($"{sw2}/wq8r4t1", words, StringComparison.Ordinal);
        Assert.Contains($"{Sw}/w1", words, StringComparison.Ordinal);
        Assert.Equal(grants[1], StagewatchGrants.Find(grants, $"{sw2}/wq8r4t1"));
        Assert.Equal(grants[1], StagewatchGrants.Find(grants, WindowAddresses.Grant(sw2, "wq8r4t1")));
        Assert.Equal(grants[2], StagewatchGrants.Find(grants, "stage"));
        Assert.Null(StagewatchGrants.Find(grants, $"{sw2}/nope"));
        // The menus name a window by the form that finds exactly one.
        Assert.Equal("Lobby", new MenuWindow("Lobby", true, "", "").Line);
        Assert.Equal($"{sw2}/wq8r4t1", new MenuWindow("Lobby", true, "", "", $"{sw2}/wq8r4t1").Line);
    }

    [Fact]
    public void ASettingsFileIsReadAsThisDeskKeepsGrantsBoundedAndEachAddressWholeOrNone()
    {
        var pin = "sha256:" + new string('a', 64);
        var rows = new List<StagewatchGrant?>
        {
            new StagewatchGrant(Sw, "lobby", "Lobby", "page", "https://10.0.0.8:9925/", Path, "", "tls", pin.ToUpperInvariant()),
            new StagewatchGrant(Sw, "lobby", "Again", "page", "https://10.0.0.8:9925", Path),                 // the same window twice: the first stands
            new StagewatchGrant(Sw, "foyer", "Foyer", "page", "https://10.0.0.8:9925", "/api/windows"),        // a secret that is no window address
            new StagewatchGrant(Sw, "hall", "Hall", "page", "http://10.0.0.8:9924", Path),                    // plain to another machine
            new StagewatchGrant(Sw, "local", "Local", "page", "http://127.0.0.1:9924", Path, "", "local", "nonsense"),
            new StagewatchGrant(Sw, "ftp", "Ftp", "page", "ftp://10.0.0.8", Path),
            new StagewatchGrant(Sw, "pathy", "Pathy", "page", "https://10.0.0.8:9925/w/x", Path),
            new StagewatchGrant(Sw, "stage", "Stage", "draw", "https://10.0.0.8:9925", Path),                 // a draw grant keeps no address
            new StagewatchGrant("x", "bad", "Bad", "page", "", ""),                                            // no node id
            new StagewatchGrant(Sw, "bad id!", "Bad", "page", "", ""),
            new StagewatchGrant(Sw, "frame", "Frame", "frame", "", ""),
            null,
            new StagewatchGrant(Sw, "noname", "", "page", "", "", new string('c', 500), "wifi"),
        };
        var kept = StagewatchGrants.Sanitize(rows);
        Assert.Equal(new[] { "lobby", "foyer", "hall", "local", "ftp", "pathy", "stage", "noname" }, kept.Select(g => g.Window));
        var lobby = kept[0];
        Assert.True(lobby.Opens);
        Assert.Equal(("https://10.0.0.8:9925", "Lobby", pin), (lobby.Base, lobby.Name, lobby.Pin));
        Assert.All(kept.Where(g => g.Window is "foyer" or "hall" or "ftp" or "pathy" or "stage"), g => Assert.Equal(("", "", false), (g.Base, g.Secret, g.Opens)));
        var local = kept.Single(g => g.Window == "local");
        Assert.True(local.Opens);
        Assert.Equal("", local.Pin);
        var noname = kept.Single(g => g.Window == "noname");
        Assert.Equal(("noname", "", StagewatchGrants.MaxCannot), (noname.Name, noname.Link, noname.Cannot.Length));
        var many = Enumerable.Range(0, 80).Select(i => (StagewatchGrant?)new StagewatchGrant(Sw, "w" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "n", "page", "", ""));
        Assert.Equal(StagewatchGrants.Max, StagewatchGrants.Sanitize(many).Count);
        Assert.Empty(StagewatchGrants.Sanitize(null));
        Assert.Equal(kept, StagewatchGrants.Sanitize(kept));                                               // what it keeps it keeps again unchanged
    }

    [Fact]
    public void AGrantsSecretIsMaskedByItsNameAndTheConfigKeepsThemBounded()
    {
        var state = new ShowState();
        Assert.True(state.Control.Stagewatch.SetGrants(new[] { new StagewatchGrant(Sw, "wk3m9x2", "lobby", "page", "https://sw:9925", Path) }));
        Assert.False(state.Control.Stagewatch.SetGrants(state.Control.Stagewatch.Grants.ToList()));   // nothing changed, nothing written
        var json = JsonUtil.SerializeCompact(state);
        Assert.Contains(Token, json, StringComparison.Ordinal);                                         // the machine's own settings hold it
        Assert.DoesNotContain(Token, Secrets.Redact(json), StringComparison.Ordinal);                   // a bundle never does
        var many = Enumerable.Range(0, 80).Select(i => new StagewatchGrant(Sw, "w" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "n", "page", "", "")).ToList();
        state.Control.Stagewatch.SetGrants(many);
        Assert.Equal(StagewatchGrants.Max, state.Control.Stagewatch.Grants.Count);
        Assert.Contains(nameof(ShowState.Control), TwinSync.LocalSections);                            // never mirrored to a twin
    }

    [Fact]
    public void ReadinessIsTheBrowsersWordAndEachNoSaysWhy()
    {
        var grant = new StagewatchGrant(Sw, "wk3m9x2", "lobby", "page", "https://sw:9925", Path);
        var told = new WebShellState(4, "ok", "/clock", false, "", "");
        Assert.True(StagewatchGrants.Readiness(grant, true, false, "", told).Ready);
        Assert.Contains("no such window", StagewatchGrants.Readiness(null, true, false, "", told).Why, StringComparison.Ordinal);
        Assert.Equal(StagewatchGrants.NoDraw, StagewatchGrants.Readiness(grant with { How = "draw" }, true, false, "", told).Why);
        Assert.Contains("has not reached", StagewatchGrants.Readiness(grant with { Secret = "", Cannot = "plain" }, true, false, "", told).Why, StringComparison.Ordinal);
        Assert.Contains("not open", StagewatchGrants.Readiness(grant, false, false, "", told).Why, StringComparison.Ordinal);
        Assert.Equal(HeldPage.OpeningWhy, StagewatchGrants.Readiness(grant, true, true, HeldPage.OpeningWhy, told).Why);
        Assert.Equal(HeldPage.UnassignedWhy, StagewatchGrants.Readiness(grant, true, false, "", told with { Rev = 0 }).Why);
        Assert.Equal(HeldPage.UnassignedWhy, StagewatchGrants.Readiness(grant, true, false, "", told with { Link = "gone" }).Why);
        Assert.Equal(HeldPage.UnassignedWhy, StagewatchGrants.Readiness(grant, true, false, "", null).Why);
        Assert.Contains("loading", StagewatchGrants.Readiness(grant, true, false, "", told with { Loading = "/clock" }).Why, StringComparison.Ordinal);
        Assert.Contains("lost", StagewatchGrants.Readiness(grant, true, false, "", told with { Link = "lost" }).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ShownIsReadFromTheAirAlone()
    {
        Assert.Equal("on air", StagewatchGrants.Shown(onAir: true, inPreview: true, outputsLive: true));
        Assert.Equal("outputs closed", StagewatchGrants.Shown(onAir: true, inPreview: false, outputsLive: false));
        Assert.Equal("in the preview — not on air", StagewatchGrants.Shown(onAir: false, inPreview: true, outputsLive: true));
        Assert.Equal("", StagewatchGrants.Shown(onAir: false, inPreview: false, outputsLive: true));

        var held = WindowAddresses.Grant(Sw, "wk3m9x2");
        var state = new ShowState();
        Assert.Empty(StagewatchGrants.Places(state, held));
        state.Pattern.Layer1.WebUrl = held;
        Assert.Equal(new[] { "PGM" }, StagewatchGrants.Places(state, held));
        state.Pattern.Layer1.WebUrl = "";
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "2" });
        var own = ContentTargets.EnsureAssignment(state, "2");
        own.Pattern.Kind = PatternKind.Media;
        own.Pattern.Media.Source = MediaSource.Web;
        own.Pattern.Media.WebUrl = held;
        ContentTargets.SetOwnPattern(state, "2", true);
        Assert.Equal(new[] { "2" }, StagewatchGrants.Places(state, held));
        ContentTargets.SetOwnPattern(state, "2", false);                                           // the screen follows the programme again: not its picture
        Assert.Empty(StagewatchGrants.Places(state, held));
        ContentTargets.SetOwnPattern(state, "2", true);
        Assert.Empty(StagewatchGrants.Places(state, WindowAddresses.Grant(Sw, "other1")));
    }

    [Fact]
    public void AGrantedPageOpensOnlyToThePinItsLinkWasProvedOver()
    {
        var held = WindowAddresses.Grant(Sw, "wk3m9x2");
        var pin = "sha256:" + new string('a', 64);
        Assert.Equal(WindowCertificateVerdict.Allow, WindowCertificate.Decide(held, pin, pin));
        Assert.Equal(WindowCertificateVerdict.Allow, WindowCertificate.Decide(held, pin.ToUpperInvariant().Replace("SHA256", "sha256", StringComparison.Ordinal), pin));
        Assert.Equal(WindowCertificateVerdict.Deny, WindowCertificate.Decide(held, "sha256:" + new string('b', 64), pin));
        Assert.Equal(WindowCertificateVerdict.Deny, WindowCertificate.Decide(held, pin, ""));          // a grant with no pin opens to no certificate
        Assert.Equal(WindowCertificateVerdict.Deny, WindowCertificate.Decide(held, "", pin));
        Assert.Equal(WindowCertificateVerdict.Default, WindowCertificate.Decide("kept://10.0.0.5:9924/abcdefghijkl", pin, pin));
        Assert.Equal(WindowCertificateVerdict.Default, WindowCertificate.Decide("https://example.com", pin, pin));
    }

    [Fact]
    public void TheWindowVerbsParseWriteBackAndABareWindowIsUnknown()
    {
        var live = ControlProtocol.Parse("SCREEN 2 WINDOW lobby");
        Assert.True(live.IsAction);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenWindow, "2", "lobby"), live.Action);
        var staged = ControlProtocol.Parse("SCREEN 2 PVW WINDOW lobby");
        Assert.Equal(new ShowAction(ShowActionKind.ScreenStageWindow, "2", "lobby"), staged.Action);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenStageWindow, "", "Main hall"), ControlProtocol.Parse("PVW WINDOW Main hall").Action);
        Assert.Equal(new ShowAction(ShowActionKind.NodesAskWindow, "", "lobby"), ControlProtocol.Parse("NODES ASK WINDOW lobby").Action);
        foreach (var bare in new[] { "SCREEN 2 WINDOW", "SCREEN 2 PVW WINDOW", "NODES ASK WINDOW", "NODES ASK lobby", "NODES ASK" })
            Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse(bare).Kind);
        foreach (var line in new[] { "SCREEN 2 WINDOW lobby", "SCREEN 2 PVW WINDOW lobby", "PVW WINDOW lobby", "NODES ASK WINDOW lobby" })
            Assert.Equal(line, WireWriter.Line(ControlProtocol.Parse(line).Action));
        Assert.True(ActionSpec.IsStaged(ShowActionKind.ScreenStageWindow));
        Assert.True(ActionSpec.ChangesContent(ShowActionKind.ScreenWindow));
        Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.NodesAskWindow));
    }

    [Fact]
    public void AnAskIsForThisDeskAloneOnTheStagewatchsOwnDesk()
    {
        const string self = "a1b2c3d4";
        Assert.Equal("window grant lobby a1b2c3d4", DeskProposals.AskWindowLine(" lobby ", self));
        Assert.Equal("window grant \"Main hall\" a1b2c3d4", DeskProposals.AskWindowLine("Main hall", self));
        Assert.Null(DeskProposals.AskWindowLine("say \"hi\"", self));
        Assert.Null(DeskProposals.AskWindowLine("", self));
        Assert.Null(DeskProposals.AskWindowLine("lobby", "x"));                                       // no node id: nothing names this desk
        using var doc = JsonDocument.Parse(DeskProposals.Ask(self, "FOH", "window grant lobby a1b2c3d4", "FOH asks to show this window"));
        var root = doc.RootElement;
        Assert.Equal("main", root.GetProperty("target").GetProperty("t").GetString());
        Assert.Equal("", root.GetProperty("target").GetProperty("id").GetString());                  // §11: kind main, no id — the desk it is posted to
        Assert.Equal(self, root.GetProperty("from").GetProperty("id").GetString());
        Assert.Equal("high", root.GetProperty("risk").GetString());
    }

    [Fact]
    public void TheTileMenuStagesAGrantedWindowAndPutsAReadyOneLiveAndSaysWhyNot()
    {
        var d = new DeskFacts { SandboxOpen = true, Windows = new[] { new MenuWindow("lobby", true, "", ""), new MenuWindow("foyer", false, "its page is still loading", "") } };
        var menu = DeskMenus.Screen(d, new ScreenFacts { TargetId = "b", Number = "2", Title = "2 · Stage left", OnAir = true, Enabled = true, Armed = true });
        var staged = menu.Flatten().Where(e => e.Id.StartsWith("stage.window:", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "SCREEN 2 PVW WINDOW lobby", "SCREEN 2 PVW WINDOW foyer" }, staged.Select(e => e.Wire));
        Assert.All(staged, e => Assert.True(e.IsEnabled));
        var live = menu.Flatten().Where(e => e.Id.StartsWith("screen.window:", StringComparison.Ordinal)).ToList();
        Assert.True(live.Single(e => e.Text == "lobby").IsEnabled);
        Assert.Equal("SCREEN 2 WINDOW lobby", live.Single(e => e.Text == "lobby").Wire);
        Assert.Contains("still loading", live.Single(e => e.Text == "foyer").Because, StringComparison.Ordinal);
        // None granted: neither drawer is offered.
        var none = DeskMenus.Screen(new DeskFacts { SandboxOpen = true }, new ScreenFacts { TargetId = "b", Number = "2", Title = "2", OnAir = true, Enabled = true });
        Assert.DoesNotContain(none.Flatten(), e => e.Id.Contains("window", StringComparison.Ordinal));
    }
}
