using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Show Bus 1.3 §12 on the twin, pure: the WINDOWS word a standby sends — its node id and the held forms of the windows a
/// Stagewatch granted it itself, never a path — read fail closed; the granted windows an air shows; which a standby lacks; the words
/// both desks say and the grant each asks Stagewatch for; and the Super Check's row and the dashboard's tile.
/// </summary>
public class TwinWindowsTests
{
    private const string Sw = "swdesk01";
    private const string Standby = "a1b2c3d4";
    private const string Token = "/w/abcdefghijkmnpqrstuvwxyz23";
    private static readonly string Lobby = WindowAddresses.Grant(Sw, "lobby");
    private static readonly string Foyer = WindowAddresses.Grant(Sw, "foyer");

    private static StagewatchGrant Page(string window, string name, bool opens = true)
        => new(Sw, window, name, StagewatchGrants.HowPage, opens ? "https://10.0.0.5:9924" : "", opens ? Token : "", opens ? "" : "the link was plain");

    private static ShowState Air()
    {
        var state = new ShowState();
        foreach (var id in new[] { "1", "2" }) state.Output.Placements.Add(new ScreenPlacement { ScreenId = id, Enabled = true });
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Web;
        state.Pattern.Media.WebUrl = Lobby;
        var own = state.Output.Placements.First(p => p.ScreenId == "2");
        own.UseCustomPattern = true;
        var screen = new OutputAssignment { ScreenId = "2" };
        screen.Pattern.Layer1.WebUrl = Lobby;
        screen.Pattern.Layer2.WebUrl = "https://example.com/";                                  // a page that is no grant is not the twin's
        state.Independent.Add(screen);
        var hidden = new OutputAssignment { ScreenId = "1" };                                   // a screen that follows the programme shows not its own
        hidden.Pattern.Layer1.WebUrl = Foyer;
        state.Independent.Add(hidden);
        return state;
    }

    [Fact]
    public void TheWordCarriesTheNodeAndTheHeldFormsOfTheGrantsThatOpenNeverAnAddress()
    {
        Assert.Equal(TwinWord.Windows, TwinMessage.Parse("WINDOWS {\"node\":\"\",\"held\":[]}").Word);
        Assert.Equal(TwinWord.Windows, TwinMessage.Parse("windows {}").Word);

        var word = TwinWindowsHeld.Of(Standby, new[] { Page("lobby", "Lobby"), Page("foyer", "Foyer", opens: false), Page("lobby", "Lobby again") });
        Assert.Equal(Standby, word.Node);
        Assert.Equal(new[] { Lobby }, word.Held);                                                // one that opens, once; one without its address is not held
        var json = word.ToJson();
        Assert.Equal("{\"node\":\"a1b2c3d4\",\"held\":[\"stagewatch://swdesk01/lobby\"]}", json);
        Assert.DoesNotContain(Token, json, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.5", json, StringComparison.Ordinal);
        Assert.Equal(word.Held, TwinWindowsHeld.Parse(json)!.Held);
        Assert.Equal(Standby, TwinWindowsHeld.Parse(json)!.Node);

        Assert.Equal("", TwinWindowsHeld.Of("not an id!", Array.Empty<StagewatchGrant>()).Node);   // an id that is not one is said as none
        Assert.Empty(TwinWindowsHeld.Parse("{\"node\":\"\",\"held\":[]}")!.Held);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"node\":\"a1b2c3d4\"}")]                                                         // no list
    [InlineData("{\"node\":\"NOT AN ID\",\"held\":[]}")]
    [InlineData("{\"node\":\"a1b2c3d4\",\"held\":[\"https://10.0.0.5:9924/w/abcdefghijkmnpqrstuvwxyz23\"]}")]   // an address, never a held form
    [InlineData("{\"node\":\"a1b2c3d4\",\"held\":[\"/w/abcdefghijkmnpqrstuvwxyz23\"]}")]
    [InlineData("{\"node\":\"a1b2c3d4\",\"held\":[\"kept://10.0.0.5:9924/q3x7\"]}")]
    [InlineData("{\"node\":\"a1b2c3d4\",\"held\":[\"stagewatch://swdesk01/LOBBY\"]}")]              // not in its own form
    [InlineData("{\"node\":\"a1b2c3d4\",\"held\":[\"stagewatch://swdesk01/lobby\",\"stagewatch://swdesk01/lobby\"]}")]
    [InlineData("{\"node\":\"a1b2c3d4\",\"held\":[1]}")]
    public void AWordThatDoesNotReadIsAStandbyThatHasNotSaid(string json)
    {
        Assert.Null(TwinWindowsHeld.Parse(json));
    }

    [Fact]
    public void AWordListingMoreGrantsThanAnyDeskKeepsIsRefusedWhole()
    {
        var held = Enumerable.Range(0, TwinWindowsHeld.Max + 1).Select(i => WindowAddresses.Grant(Sw, "w" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        Assert.Null(TwinWindowsHeld.Parse(new TwinWindowsHeld(Standby, held).ToJson()));
        Assert.NotNull(TwinWindowsHeld.Parse(new TwinWindowsHeld(Standby, held.Take(TwinWindowsHeld.Max).ToList()).ToJson()));
    }

    [Fact]
    public void TheWindowsOnAirAreTheGrantsTheProgrammeAndTheScreensOwnPicturesShowNamedAsAGrantHereKnowsThem()
    {
        var air = Air();
        Assert.Equal(new[] { Lobby }, TwinWindows.HeldIn(air));                                 // foyer sits on a screen that follows the programme

        var onAir = TwinWindows.OnAir(air, new[] { Page("lobby", "Lobby") });
        var lobby = Assert.Single(onAir);
        Assert.Equal("Lobby", lobby.Name);
        Assert.Equal("lobby", lobby.Window);
        Assert.Equal(new[] { "PGM", "2" }, lobby.Places);
        Assert.Equal("the programme and Screen 2", TwinWindows.PlaceWords(lobby.Places));
        Assert.Equal("lobby", Assert.Single(TwinWindows.OnAir(air, Array.Empty<StagewatchGrant>())).Name);   // no grant here: its id

        Assert.Empty(TwinWindows.OnAir(new ShowState(), new[] { Page("lobby", "Lobby") }));     // granted but not on air: nothing to say
    }

    [Fact]
    public void TheMainNamesEachWindowOnAirAStandbyLacksAndTheGrantStagewatchIsAskedFor()
    {
        var onAir = TwinWindows.OnAir(Air(), new[] { Page("lobby", "Lobby") });

        var lacks = new TwinWindowsHeld(Standby, Array.Empty<string>());
        var words = TwinWindows.OnMain("Backup", lacks, onAir);
        Assert.Equal("Stagewatch Lobby is on air (the programme and Screen 2) and granted to this desk, not to its standby Backup — a takeover shows black there: Stagewatch → window grant Lobby a1b2c3d4", words);

        Assert.Equal("", TwinWindows.OnMain("Backup", new TwinWindowsHeld(Standby, new[] { Lobby }), onAir));     // it holds it
        Assert.Contains("window grant Lobby <its Show Bus id", TwinWindows.OnMain("Backup", new TwinWindowsHeld("", Array.Empty<string>()), onAir), StringComparison.Ordinal);
        Assert.Equal("Backup has not said which Stagewatch windows it holds — Lobby on air here may show black after a takeover", TwinWindows.OnMain("Backup", null, onAir));

        var report = TwinWindows.ForMain(new[] { ("Backup", (TwinWindowsHeld?)lacks) }, onAir);
        Assert.True(report.Lacking);
        Assert.Equal("Lobby not held by Backup", report.Value);
        var notSaid = TwinWindows.ForMain(new[] { ("Backup", (TwinWindowsHeld?)null) }, onAir);
        Assert.True(notSaid.Lacking);
        Assert.Equal("Backup has not said", notSaid.Value);
        var two = TwinWindows.ForMain(new[] { ("Backup", (TwinWindowsHeld?)lacks), ("Hall B", (TwinWindowsHeld?)null) }, onAir);
        Assert.Equal("Lobby not held by Backup · and 1 more standby", two.Value);
        Assert.Contains("Hall B has not said", two.Words, StringComparison.Ordinal);

        var held = TwinWindows.ForMain(new[] { ("Backup", (TwinWindowsHeld?)new TwinWindowsHeld(Standby, new[] { Lobby })) }, onAir);
        Assert.False(held.Lacking);
        Assert.Equal("1 on air · held by Backup", held.Value);

        Assert.Same(TwinWindowsReport.None, TwinWindows.ForMain(Array.Empty<(string, TwinWindowsHeld?)>(), onAir));   // no standby: the twin row says so

        // This desk's own grant withdrawn: the window is black here too, and the words never say it is granted to this desk.
        var ungranted = TwinWindows.OnAir(Air(), Array.Empty<StagewatchGrant>());
        Assert.False(Assert.Single(ungranted).Granted);
        Assert.True(Assert.Single(onAir).Granted);
        var bothBlack = TwinWindows.OnMain("Backup", lacks, ungranted);
        Assert.Equal("Stagewatch lobby is on air (the programme and Screen 2) and black here too — no grant opens it on this desk or on its standby Backup: Stagewatch → window grant lobby a1b2c3d4", bothBlack);
        Assert.DoesNotContain("granted to this desk", bothBlack, StringComparison.Ordinal);
        var withoutAddress = TwinWindows.OnAir(Air(), new[] { Page("lobby", "Lobby", opens: false) });   // granted, its address never reached this desk
        Assert.Contains("black here too", TwinWindows.OnMain("Backup", lacks, withoutAddress), StringComparison.Ordinal);
        Assert.Same(TwinWindowsReport.None, TwinWindows.ForMain(new[] { ("Backup", (TwinWindowsHeld?)lacks) }, Array.Empty<TwinWindowOnAir>()));
    }

    private static readonly WindowReadiness NotYet = new(false, "its page is still loading");

    private static TwinStandbySide Side(bool tookOver = false, bool canAsk = false, Func<StagewatchGrant, WindowReadiness>? ready = null)
        => new(Standby, "MAIN-DESK", tookOver, canAsk, ready ?? (_ => NotYet));

    [Fact]
    public void AStandbyReadsItsOwnGrantsAgainstTheAirItMirrorsAndAsksByItsOwnId()
    {
        var air = Air();
        var none = Array.Empty<StagewatchGrant>();
        var report = TwinWindows.ForStandby(TwinWindows.OnAir(air, none), none, Side());
        Assert.True(report.Lacking);
        Assert.Equal("lobby not held by this desk", report.Value);
        Assert.Equal("Stagewatch lobby is on air at MAIN-DESK (the programme and Screen 2) and not granted to this desk — a takeover shows black there: Stagewatch → window grant lobby a1b2c3d4 (NODES ASK WINDOW lobby asks it from this desk once its Stagewatch link is up)", report.Words);
        Assert.EndsWith(", or ask from this desk: NODES ASK WINDOW lobby", TwinWindows.ForStandby(TwinWindows.OnAir(air, none), none, Side(canAsk: true)).Words, StringComparison.Ordinal);

        var own = new[] { Page("lobby", "Lobby") };
        var held = TwinWindows.ForStandby(TwinWindows.OnAir(air, own), own, Side());          // before a takeover: the grant is what it holds; nothing is on its air yet
        Assert.False(held.Lacking);
        Assert.Equal("1 on air · held here", held.Value);

        var noAddress = new[] { Page("lobby", "Lobby", opens: false) };                        // granted, but its address never reached this desk
        Assert.True(TwinWindows.ForStandby(TwinWindows.OnAir(air, noAddress), noAddress, Side()).Lacking);
        Assert.Same(TwinWindowsReport.None, TwinWindows.ForStandby(Array.Empty<TwinWindowOnAir>(), none, Side()));
    }

    [Fact]
    public void AfterATakeoverTheStandbySaysWhatIsBlackNowAndIsGreenOnlyWhenItsOwnPageIsReady()
    {
        var air = Air();
        var none = Array.Empty<StagewatchGrant>();

        // No grant of its own: black here now, in words of the present, and the ask.
        var black = TwinWindows.ForStandby(TwinWindows.OnAir(air, none), none, Side(tookOver: true, canAsk: true));
        Assert.True(black.Lacking);
        Assert.Equal("Stagewatch lobby is on air here (the programme and Screen 2) and black — no grant of this desk's own opens it: Stagewatch → window grant lobby a1b2c3d4, or ask from this desk: NODES ASK WINDOW lobby", black.Words);
        Assert.DoesNotContain("a takeover", black.Words, StringComparison.Ordinal);

        // Its own grant, its page not ready: amber with the browser's why — the grant alone is not what the room sees.
        var own = new[] { Page("lobby", "Lobby") };
        var asked = new List<StagewatchGrant>();
        var loading = TwinWindows.ForStandby(TwinWindows.OnAir(air, own), own, Side(tookOver: true, ready: g => { asked.Add(g); return NotYet; }));
        Assert.True(loading.Lacking);
        Assert.Equal("Lobby not ready here", loading.Value);
        Assert.Equal("Stagewatch Lobby is on air here (the programme and Screen 2) from this desk's own grant and black until its page is ready: its page is still loading", loading.Words);
        Assert.Equal(own[0], Assert.Single(asked));                                           // the readiness asked of the grant that opens it

        // Ready: green, and only then.
        var open = TwinWindows.ForStandby(TwinWindows.OnAir(air, own), own, Side(tookOver: true, ready: _ => new WindowReadiness(true, "")));
        Assert.False(open.Lacking);
        Assert.Equal("1 on air · open here", open.Value);
        Assert.Contains("its page is ready", open.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSuperCheckRowAndTheDashboardTileSayAWindowATakeoverWouldShowBlack()
    {
        static CheckRow? Row(CheckFacts f) => SuperCheck.Run(f).Rows.SingleOrDefault(r => r.Item == TwinWindows.Item);

        var lacking = new TwinWindowsReport("Lobby not held by Backup", "Stagewatch Lobby is on air … window grant Lobby a1b2c3d4", true);
        var f = new CheckFacts { TwinRole = TwinRole.Main, TwinPhase = TwinPhase.Listening, TwinWords = "MAIN", TwinWindowsCheck = lacking };
        var row = Row(f)!;
        Assert.Equal(CheckLight.Amber, row.Light);
        Assert.Equal("Lobby not held by Backup", row.Value);
        Assert.Equal("FIX: Stagewatch Lobby is on air … window grant Lobby a1b2c3d4", row.Note);
        Assert.Equal(CheckLight.Green, SuperCheck.Run(f).Rows.Single(r => r.Item == "Twin").Light);   // the link itself is in step

        var tile = HealthDashboard.Tiles(f).Single(t => t.Id == "watchdog");
        Assert.Equal(CheckLight.Amber, tile.Light);
        Assert.Equal("Lobby not held by Backup", tile.Value);

        Assert.Equal(CheckLight.Green, Row(new CheckFacts { TwinRole = TwinRole.Main, TwinPhase = TwinPhase.Listening, TwinWindowsCheck = new TwinWindowsReport("1 on air · held by Backup", "…", false) })!.Light);
        Assert.Null(Row(new CheckFacts { TwinRole = TwinRole.Main, TwinPhase = TwinPhase.Listening }));          // nothing granted on air
        Assert.Null(Row(new CheckFacts { TwinRole = TwinRole.Off, TwinWindowsCheck = lacking }));                // the twin off: not the twin's to say
    }

    [Fact]
    public void ASilentMainMachineOutranksAWindowATakeoverWouldShowBlackOnTheTile()
    {
        var lacking = new TwinWindowsReport("Lobby not held by Backup", "Stagewatch Lobby is on air … window grant Lobby a1b2c3d4", true);
        var f = new CheckFacts
        {
            TwinRole = TwinRole.Main, TwinPhase = TwinPhase.Listening, TwinWords = "MAIN", TwinWindowsCheck = lacking,
            BeaconListening = true, BeaconWatch = "MAIN MACHINE silent for 12 s",
        };
        var tile = HealthDashboard.Tiles(f).Single(t => t.Id == "watchdog");
        Assert.Equal(CheckLight.Red, tile.Light);
        Assert.Equal("MAIN SILENT", tile.Value);
        Assert.Equal(CheckLight.Amber, SuperCheck.Run(f).Rows.Single(r => r.Item == TwinWindows.Item).Light);   // the row still says it
    }
}
