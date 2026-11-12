using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering.Media;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 96 (Show Bus 1.3 §12): a window Stagewatch granted this desk, on the desk. The grants message heard on the link and answered
/// with grants-seen in Stagewatch's own bytes, a revision taken only when higher, a withdrawal journalled; the granted page held by its
/// held form and resolved only through the grants; SCREEN n WINDOW only once its page is ready, the preview and TAKE otherwise; where it
/// is shown read from the air alone, by STATE, the Run strip and the readers' words; NODES ASK WINDOW a request to Stagewatch's own desk.
/// No reader carries the window's address.
/// </summary>
public class StagewatchWindowsAppTests
{
    private const string Sw = "swdesk01";
    private const string Token = "abcdefghijkmnpqrstuvwxyz23";
    private const string Held = "stagewatch://" + Sw + "/lobby";

    private static readonly List<ScreenInfo> ThreeScreens = new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    private static void CarriesNoAddress(string? text, string where)
        => Assert.False((text ?? "").Contains(Token, StringComparison.Ordinal), $"{where} carries the window's address:\n{text}");

    private static List<FakeWebSource> FakePages(AppServices services)
    {
        var made = new List<FakeWebSource>();
        services.WebIn.SourceFactory = w =>
        {
            var page = new FakeWebSource(w.Target, WebEngine.ParseSize(w.Format), SKColors.Blue);
            made.Add(page);
            return page;
        };
        return made;
    }

    private static void Grant(AppServices services, params StagewatchGrant[] grants)
    {
        services.State.Control.Stagewatch.SetGrants(grants);
        services.WebPasses.SetGrants(grants);
    }

    private static void Settle(AppServices services)
    {
        Dispatcher.UIThread.RunJobs();
        services.ReconcileInputs();
        Dispatcher.UIThread.RunJobs();
    }

    private static StagewatchWindowRow Row(AppServices services, string window = "lobby")
        => services.StagewatchWindows.Rows().Single(r => r.Window == window);

    [AvaloniaFact]
    public void AGrantedWindowGoesLiveOnlyOnceItsPageIsReadyAndEveryReaderReadsWhereItIsShownFromTheAir()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            services.Screens.All.Clear();
            foreach (var s in ThreeScreens) services.Screens.All.Add(s);
            vm.State.Output.Placements.Clear();
            vm.ReconcilePlacements(ThreeScreens);
            foreach (var p in vm.State.Output.Placements) p.Enabled = true;
            vm.RebuildSwitcherTiles(ThreeScreens);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            var made = FakePages(services);
            Grant(services,
                new StagewatchGrant(Sw, "lobby", "Lobby", StagewatchGrants.HowPage, "http://127.0.0.1:9924", "/w/" + Token, Link: StagewatchGrants.LinkLocal),
                new StagewatchGrant(Sw, "stage", "Stage", StagewatchGrants.HowDraw, "", "", StagewatchGrants.NoDraw, StagewatchGrants.LinkLocal));
            Settle(services);

            // The held form resolves through the grants alone; nothing shows it yet.
            Assert.Equal("http://127.0.0.1:9924/w/" + Token, services.WebPasses.Resolve(Held).Url);
            Assert.Equal("", Row(services).Shown);
            Assert.False(Row(services).Ready);

            // SCREEN b WINDOW before its page is open here: refused, nothing changed, steered to the preview.
            var airBefore = JsonUtil.Serialize(services.AirState);
            var early = services.Actions.Execute(ShowActionKind.ScreenWindow, ActionOrigin.Desk, "b", "Lobby");
            Assert.Equal(ActionStatus.Refused, early.Status);
            Assert.Contains("not ready on this desk", early.Message, StringComparison.Ordinal);
            Assert.Contains("SCREEN b PVW WINDOW Lobby", early.Message, StringComparison.Ordinal);
            Assert.Equal(airBefore, JsonUtil.Serialize(services.AirState));
            Assert.Empty(StagewatchGrants.Places(services.State, Held));

            // A draw grant is never a page here; an ungranted name says what is granted.
            Assert.Contains(StagewatchGrants.NoDraw, services.Actions.Execute(ShowActionKind.ScreenStageWindow, ActionOrigin.Desk, "b", "stage").Message, StringComparison.Ordinal);
            Assert.Contains("Granted: Lobby, Stage", services.Actions.Execute(ShowActionKind.ScreenWindow, ActionOrigin.Desk, "b", "foyer").Message, StringComparison.Ordinal);

            // Staged on b's PVW: EDIT SAFE opens, the page is mounted under its held form and is black until its shell says what it shows.
            var staged = services.Actions.Execute(ShowActionKind.ScreenStageWindow, ActionOrigin.Desk, "b", "lobby");
            Assert.True(staged.Ok, staged.Message);
            Assert.True(services.Sandbox.Active);
            Settle(services);
            var page = Assert.Single(made, p => p.Navigations.Contains(Held) || p.CurrentUrl == Held);
            CarriesNoAddress(string.Join("\n", page.Navigations), "the page's navigations");             // the page is sent the held form; it resolves it itself
            var row = Row(services);
            Assert.Equal(StagewatchGrants.InPreview, row.Shown);
            Assert.Equal(new[] { "b" }, row.Staged);
            Assert.Empty(row.Screens);
            Assert.False(row.Ready);
            Assert.Equal(HeldPage.UnassignedWhy, row.ReadyWhy);

            // TAKE while the PVW holds a window that is not ready: refused, the air as it was.
            airBefore = JsonUtil.Serialize(services.AirState);
            var notYet = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.Equal(ActionStatus.Refused, notYet.Status);
            Assert.Contains("Stagewatch window 'Lobby', not ready", notYet.Message, StringComparison.Ordinal);
            Assert.Equal(airBefore, JsonUtil.Serialize(services.AirState));

            // The shell says what the window shows, its link up: ready, and TAKE puts it on b alone — the fields say what the room could see.
            page.Shell = new WebShellState(3, "ok", "clock", false, "", "");
            Assert.True(Row(services).Ready);
            var take = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.True(take.Ok, take.Message);
            Assert.Equal(ActionEffect.Changed, take.Effect);
            Assert.Equal(services.OutputsLive ? ActionVisibility.OutputsLive : ActionVisibility.OutputsOff, take.Visibility);
            Settle(services);
            row = Row(services);
            Assert.Equal(new[] { "b" }, row.Screens);
            Assert.Empty(row.Staged);
            Assert.Equal(services.OutputsLive ? StagewatchGrants.OnAir : StagewatchGrants.OutputsClosed, row.Shown);

            // Ready, SCREEN c WINDOW goes live on c alone, measured; again changes nothing and is refused with the reason.
            var live = services.Actions.Execute(ShowActionKind.ScreenWindow, ActionOrigin.Desk, "c", "lobby");
            Assert.True(live.Ok, live.Message);
            Assert.Equal(ActionEffect.Changed, live.Effect);
            Assert.NotEqual(ActionVisibility.Unknown, live.Visibility);
            Assert.Equal(new[] { "b", "c" }, Row(services).Screens);
            var again = services.Actions.Execute(ShowActionKind.ScreenWindow, ActionOrigin.Desk, "c", "lobby");
            Assert.Equal(ActionStatus.Refused, again.Status);
            Assert.Contains("already shows Stagewatch window 'Lobby'", again.Message, StringComparison.Ordinal);

            // STATE's bus.stagewatch.windows: the row's fields, its screens from the air, never the address.
            var router = new CommandRouter(services);
            var state = router.StateJson();
            CarriesNoAddress(state, "STATE");
            using (var doc = JsonDocument.Parse(state))
            {
                var windows = doc.RootElement.GetProperty("bus").GetProperty("stagewatch").GetProperty("windows");
                var lobby = windows.EnumerateArray().Single(w => w.GetProperty("window").GetString() == "lobby");
                Assert.Equal("Lobby", lobby.GetProperty("name").GetString());
                Assert.Equal(Held, lobby.GetProperty("held").GetString());
                Assert.True(lobby.GetProperty("granted").GetBoolean());
                Assert.True(lobby.GetProperty("ready").GetBoolean());
                Assert.Equal(new[] { "b", "c" }, lobby.GetProperty("screens").EnumerateArray().Select(e => e.GetString()).ToArray());
                var stage = windows.EnumerateArray().Single(w => w.GetProperty("window").GetString() == "stage");
                Assert.False(stage.GetProperty("granted").GetBoolean());
                Assert.Equal(StagewatchGrants.NoDraw, stage.GetProperty("cannot").GetString());
            }

            // The Run strip's chip and the readers' words say the same, from the air.
            vm.Run.Tick();
            Assert.StartsWith(services.OutputsLive ? "▣ SW Lobby · ON AIR b c" : "△ SW Lobby · OUTPUTS CLOSED", vm.Run.WindowChip, StringComparison.Ordinal);
            Assert.Contains("Lobby — " + Row(services).Shown + " on b, c (ready)", services.StagewatchWindows.Words(), StringComparison.Ordinal);
            CarriesNoAddress(vm.Run.WindowTip, "the Run strip's tip");
            CarriesNoAddress(services.StagewatchWindows.Words(), "the readers' words");
            CarriesNoAddress(string.Join("\n", services.Journal.Tail(20).Select(r => $"{r.Target} {r.Message}")), "the journal");

            // The page's link to Stagewatch drops: not ready, said with why — the take gate reads it, the air keeps what it shows.
            page.Shell = new WebShellState(3, "lost", "clock", false, "", "");
            Assert.False(Row(services).Ready);
            Assert.Contains("link to Stagewatch is lost", Row(services).ReadyWhy, StringComparison.Ordinal);
            Assert.Equal(ActionStatus.Refused, services.Actions.Execute(ShowActionKind.ScreenWindow, ActionOrigin.Desk, "a", "lobby").Status);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AskingForAWindowIsARequestToStagewatchsOwnDeskAndNeverRunsHere()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;

            // With the link off the ask is refused with the way to turn it on; a granted window is staged, not asked for.
            var off = services.Actions.Execute(ShowActionKind.NodesAskWindow, ActionOrigin.Desk, "", "Foyer");
            Assert.Equal(ActionStatus.Refused, off.Status);
            Assert.Contains("STAGEWATCH LINK ON", off.Message, StringComparison.Ordinal);
            Grant(services, new StagewatchGrant(Sw, "lobby", "Lobby", StagewatchGrants.HowPage, "http://127.0.0.1:9924", "/w/" + Token, Link: StagewatchGrants.LinkLocal));
            Assert.Contains("PVW WINDOW Lobby", services.Actions.Execute(ShowActionKind.NodesAskWindow, ActionOrigin.Desk, "", "lobby").Message, StringComparison.Ordinal);

            // With the link on (no Stagewatch reached): one entry for Stagewatch's desk, high risk, requested, never applied here.
            vm.State.Control.Stagewatch.Enabled = true;
            vm.State.Control.Stagewatch.Host = "127.0.0.1:1";
            Dispatcher.UIThread.RunJobs();
            var asked = services.Actions.Execute(ShowActionKind.NodesAskWindow, ActionOrigin.Desk, "", "Foyer 2");
            if (PeerLink.IsId(services.Kernel.Stagewatch.SelfId))
            {
                Assert.Equal(ActionStatus.Requested, asked.Status);
                Assert.Contains("a person there decides", asked.Message, StringComparison.Ordinal);
                var entry = Assert.Single(services.Kernel.Stagewatch.Approvals.All, e => e.ToDesk);
                Assert.Equal("high", entry.Risk);
                Assert.StartsWith("window grant \"Foyer 2\" ", entry.Line, StringComparison.Ordinal);
                Assert.False(entry.Apply().Item1);
            }
            else
            {
                Assert.Equal(ActionStatus.Refused, asked.Status);
                Assert.Contains("show network id", asked.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            b.Services.State.Control.Stagewatch.Enabled = false;
            b.Services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    /// <summary>
    /// One air (Show Bus 1.3 §12.9): the wall's TAKE and CUT and every scoped TAKE land each taken target's PVW — a granted window still
    /// opening there is refused with nothing changed, in the same words a tile's take says; once its page is ready the take lands, and
    /// the fields say what the room could see. PVW WINDOW stages it on the programme's own preview, which only the wall's take lands.
    /// </summary>
    [AvaloniaFact]
    public void TheWallsTakeAndEveryScopedTakeWaitForAGrantedWindowsPageAsATilesDoes()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            services.Screens.All.Clear();
            foreach (var sc in ThreeScreens) services.Screens.All.Add(sc);
            vm.State.Output.Placements.Clear();
            vm.ReconcilePlacements(ThreeScreens);
            foreach (var p in vm.State.Output.Placements) p.Enabled = true;
            vm.RebuildSwitcherTiles(ThreeScreens);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            var made = FakePages(services);
            Grant(services, new StagewatchGrant(Sw, "lobby", "Lobby", StagewatchGrants.HowPage, "http://127.0.0.1:9924", "/w/" + Token, Link: StagewatchGrants.LinkLocal));
            Settle(services);

            // PVW WINDOW: staged on the programme's own preview; EDIT SAFE opens, the air untouched.
            var staged = services.Actions.Execute(ShowActionKind.ScreenStageWindow, ActionOrigin.Desk, "", "Lobby");
            Assert.True(staged.Ok, staged.Message);
            Assert.True(services.Sandbox.Active);
            Settle(services);
            var page = Assert.Single(made, p => p.Navigations.Contains(Held) || p.CurrentUrl == Held);
            Assert.False(Row(services).Ready);

            // Not ready: the wall's TAKE and CUT, and a scoped TAKE, are each refused whole — the air exactly as it was.
            var airBefore = JsonUtil.Serialize(services.AirState);
            foreach (var (kind, target) in new[] { (ShowActionKind.Take, ""), (ShowActionKind.Cut, ""), (ShowActionKind.Take, "SCREEN 2"), (ShowActionKind.Cut, "SCREEN 3") })
            {
                var refused = services.Actions.Execute(kind, ActionOrigin.Desk, target);
                Assert.Equal(ActionStatus.Refused, refused.Status);
                Assert.Contains("Stagewatch window 'Lobby', not ready", refused.Message, StringComparison.Ordinal);
                Assert.Contains("nothing taken", refused.Message, StringComparison.Ordinal);
                Assert.Equal(airBefore, JsonUtil.Serialize(services.AirState));
                CarriesNoAddress(refused.Message, "a refused take's words");
            }
            Assert.Empty(Row(services).Screens);
            Assert.Equal(StagewatchGrants.InPreview, Row(services).Shown);

            // Ready: a scoped take lands on its screen alone; then the wall's take lands the rest — Done, the picture changed.
            page.Shell = new WebShellState(2, "ok", "clock", false, "", "");
            var one = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "SCREEN 2");
            Assert.True(one.Ok, one.Message);
            Assert.Equal(ActionEffect.Changed, one.Effect);
            Assert.Equal(new[] { "b" }, Row(services).Screens);
            var wall = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "");
            Assert.True(wall.Ok, wall.Message);
            Assert.Equal(ActionEffect.Changed, wall.Effect);
            Assert.NotEqual(ActionVisibility.Unknown, wall.Visibility);
            Assert.Contains("PGM", Row(services).Screens);

            // On air, its page's link lost: a take that puts up nothing new of it is not the gate's to refuse — the air already shows it there.
            page.Shell = new WebShellState(2, "lost", "clock", false, "", "");
            var again = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.DoesNotContain("not ready", again.Message, StringComparison.Ordinal);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// Show Bus 1.3 §12.5: a granted page over TLS is opened only once its server has shown the certificate its Stagewatch's link was
    /// proved over — checked before the browser is sent, so a certificate an authority vouches for never stands in for the pin. Another
    /// certificate, none pinned, or an address no certificate guards: never opened, with words that name no address.
    /// </summary>
    [Fact]
    public async Task AGrantedPageIsOpenedOnlyToTheCertificateItsLinkWasProvedOver()
    {
        using var mine = ShowBus12AppTests.Cert("Hall A");
        using var other = ShowBus12AppTests.Cert("Hall A (someone else)");
        using var sw = new ShowBus12AppTests.FakeStagewatch(ShowBus12AppTests.Frame, mine);
        var url = "https://127.0.0.1:" + sw.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/w/" + Token;
        Task<string> Check(string pin, double seconds = 10) => PinnedPageProbe.CheckAsync(url, pin, TimeSpan.FromSeconds(seconds));
        Assert.Equal("", await Check(TlsFingerprint.Of(mine)));
        Assert.Equal("", await Check(TlsFingerprint.Of(mine).ToUpperInvariant()));
        Assert.Equal(PinnedPageProbe.OtherCertificate, await Check(TlsFingerprint.Of(other)));
        Assert.Contains("no certificate is pinned", await Check(""), StringComparison.Ordinal);
        Assert.Contains("not one a certificate guards", await PinnedPageProbe.CheckAsync("http://127.0.0.1:1/w/" + Token, TlsFingerprint.Of(mine), TimeSpan.FromSeconds(1)), StringComparison.Ordinal);
        sw.Certificate = null;                                                                          // it answers plain now: no certificate comes
        Assert.NotEqual("", await Check(TlsFingerprint.Of(mine), 2));
        Assert.Empty(sw.Authorizations);                                                                // a check asks for no page and carries no key
    }

    /// <summary>
    /// Show Bus 1.3 §12.4: NODES ASK WINDOW posts §2.10's body to the linked Stagewatch's own desk — a proposal to its main, for this desk,
    /// high risk — and follows its state as Stagewatch says it; a second press while it waits asks nothing new.
    /// </summary>
    [AvaloniaFact]
    public void AnAskPostsItsBodyToStagewatchsOwnDeskAndFollowsItsState()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var sw = new ShowBus12AppTests.FakeStagewatch(ShowBus12AppTests.Frame, null);
        try
        {
            var link = vm.State.Control.Stagewatch;
            link.Host = "127.0.0.1:" + sw.Port;
            link.ApiKey = "swk_nodekey";
            ShowBus12AppTests.Hear(services, sw.Port, "", "timer", "approvals");
            link.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            var self = services.Kernel.Stagewatch.SelfId;
            Assert.True(PeerLink.IsId(self));

            var asked = services.Actions.Execute(ShowActionKind.NodesAskWindow, ActionOrigin.Desk, "", "Foyer 2");
            Assert.Equal(ActionStatus.Requested, asked.Status);
            ShowBus12AppTests.PumpUntil(() => { ShowBus12AppTests.Hear(services, sw.Port, "", "timer", "approvals"); return !sw.Proposals.IsEmpty; }, 60000,
                () => services.Kernel.Stagewatch.Status);
            var (id, posted) = sw.Proposals.Single();
            Assert.Equal(("window grant \"Foyer 2\" " + self, "", self), (posted.Line, posted.Target, posted.From));
            using (var doc = JsonDocument.Parse(posted.Json))
            {
                var root = doc.RootElement;
                Assert.Equal("main", root.GetProperty("target").GetProperty("t").GetString());
                Assert.Equal("", root.GetProperty("target").GetProperty("id").GetString());
                Assert.Equal("high", root.GetProperty("risk").GetString());
                Assert.Equal(self, root.GetProperty("from").GetProperty("id").GetString());
            }
            var entry = Assert.Single(services.Kernel.Stagewatch.Approvals.All, e => e.ToDesk);

            // Asked again while it waits: nothing new is asked or posted.
            var twice = services.Actions.Execute(ShowActionKind.NodesAskWindow, ActionOrigin.Desk, "", "Foyer 2");
            Assert.Equal(ActionStatus.Refused, twice.Status);
            Assert.Contains("asked for it already", twice.Message, StringComparison.Ordinal);
            Assert.Single(services.Kernel.Stagewatch.Approvals.All, e => e.ToDesk);

            // Stagewatch's desk decides: the ask's state follows it, and nothing is collected to run here.
            sw.Approve(id);
            ShowBus12AppTests.PumpUntil(() => { ShowBus12AppTests.Hear(services, sw.Port, "", "timer", "approvals"); return entry.State == "approved"; }, 60000,
                () => services.Kernel.Stagewatch.Status);
            Assert.False(entry.Ran);
            Assert.Equal(1, sw.ProposalPosts);
            Assert.Empty(sw.ResultPosts);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    /// <summary>
    /// Show Bus 1.3 §12.5: a link whose Stagewatch has said no node — an address typed, no hello heard — takes no grants: nothing kept, none
    /// answered, the window resolves nothing. Grants are another node's to make for itself alone.
    /// </summary>
    [AvaloniaFact]
    public void ALinkWhoseStagewatchSaidNoNodeTakesNoGrants()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var sw = new ShowBus12AppTests.FakeStagewatch(ShowBus12AppTests.Frame, null);
        try
        {
            sw.Grants = "{\"type\":\"grants\",\"from\":\"" + Sw + "\",\"rev\":1,\"link\":\"local\",\"grants\":["
                        + "{\"window\":\"lobby\",\"name\":\"Lobby\",\"how\":\"page\",\"path\":\"/w/" + Token + "\",\"cannot\":\"\"}]}";
            var link = vm.State.Control.Stagewatch;
            link.Host = "127.0.0.1:" + sw.Port;
            link.ApiKey = "swk_nodekey";
            link.Enabled = true;                                                                          // typed, and no hello heard
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            ShowBus12AppTests.PumpUntil(() => sw.Upgrades > 0 && services.Stage.Following.Length > 0, 60000, () => services.Kernel.Stagewatch.Status);
            ShowBus12AppTests.PumpFor(2000);
            Assert.DoesNotContain(sw.LinkMessages, m => m.Contains("grants-seen", StringComparison.Ordinal));
            Assert.Empty(link.Grants);
            Assert.True(string.IsNullOrEmpty(services.WebPasses.Resolve(Held).Url));
            Assert.DoesNotContain(services.Journal.Tail(30), r => r.Kind == "Grants");
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    /// <summary>
    /// Show Bus 1.3 §12.4, §12.6: grants outlive the link and a restart. The link dropped, the grant and its address stay; a new link's
    /// first grants are taken whatever their revision (Stagewatch's restarts at 1) and answered; and a desk started again over the same
    /// settings resolves the window from them before any link is up.
    /// </summary>
    [AvaloniaFact]
    public void GrantsOutliveTheLinkAndARestartAndANewLinksFirstRevisionIsTaken()
    {
        using var home = TempRun.Folder("patterns-tests-");
        var first = TestApp.Boot(dir: home.Path);
        var (services, vm) = (first.Services, first.Vm);
        var sw = new ShowBus12AppTests.FakeStagewatch(ShowBus12AppTests.Frame, null);
        var port = sw.Port;
        ShowBus12AppTests.FakeStagewatch? again = null;
        var gone = false;
        string Granted(int rev, string name) => "{\"type\":\"grants\",\"from\":\"" + Sw + "\",\"rev\":" + rev.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"link\":\"local\",\"grants\":[{\"window\":\"lobby\",\"name\":\"" + name + "\",\"how\":\"page\",\"path\":\"/w/" + Token + "\",\"cannot\":\"\"}]}";
        try
        {
            var link = vm.State.Control.Stagewatch;
            sw.Grants = Granted(4, "Lobby");
            link.Host = "127.0.0.1:" + port;
            link.ApiKey = "swk_nodekey";
            ShowBus12AppTests.Hear(services, port, "", "timer", "approvals");
            link.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            ShowBus12AppTests.PumpUntil(() => { ShowBus12AppTests.Hear(services, port, "", "timer", "approvals"); return sw.LinkMessages.Any(m => m.Contains("\"rev\":4", StringComparison.Ordinal)); }, 60000,
                () => services.Kernel.Stagewatch.Status);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Lobby", Assert.Single(link.Grants).Name);

            // The Stagewatch goes away: the link drops, the grant and its address stay.
            sw.Dispose();
            gone = true;
            ShowBus12AppTests.PumpUntil(() => services.Stage.Following.Length == 0 || !services.Kernel.Stagewatch.Status.Contains("linked", StringComparison.OrdinalIgnoreCase), 30000);
            ShowBus12AppTests.PumpFor(1500);
            Assert.Equal("Lobby", Assert.Single(link.Grants).Name);
            Assert.Equal("http://127.0.0.1:" + port + "/w/" + Token, services.WebPasses.Resolve(Held).Url);

            // It comes back on the same address, its grants' revision begun again at 1: the new link's first is taken and answered.
            again = new ShowBus12AppTests.FakeStagewatch(ShowBus12AppTests.Frame, null, port) { Grants = Granted(1, "Lobby again") };
            ShowBus12AppTests.PumpUntil(() => { ShowBus12AppTests.Hear(services, port, "", "timer", "approvals"); return again.LinkMessages.Any(m => m.Contains("grants-seen", StringComparison.Ordinal)); }, 90000,
                () => services.Kernel.Stagewatch.Status);
            Assert.Equal("{\"type\":\"grants-seen\",\"rev\":1,\"cannot\":\"\"}", again.LinkMessages.Single(m => m.Contains("grants-seen", StringComparison.Ordinal)));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Lobby again", Assert.Single(link.Grants).Name);

            link.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            services.SaveNow();
            TestApp.FlushFiles(services);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            first.Dispose();
            if (!gone) sw.Dispose();
            again?.Dispose();
        }

        // Started again over the same settings, no link on: the window resolves from the grants kept with the machine.
        var second = TestApp.Boot(dir: home.Path);
        try
        {
            Assert.False(second.Vm.State.Control.Stagewatch.Enabled);
            var kept = Assert.Single(second.Services.State.Control.Stagewatch.Grants);
            Assert.Equal(("lobby", "Lobby again", StagewatchGrants.LinkLocal), (kept.Window, kept.Name, kept.Link));
            Assert.Equal("http://127.0.0.1:" + port + "/w/" + Token, second.Services.WebPasses.Resolve(Held).Url);
            Assert.Equal("Lobby again", Assert.Single(second.Services.StagewatchWindows.Rows()).Name);
        }
        finally
        {
            second.Dispose();
        }
    }

    [AvaloniaFact]
    public void GrantsHeardOnTheLinkAreAnsweredInStagewatchsBytesTakenOnlyWhenNewerAndAWithdrawalIsJournalled()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var sw = new ShowBus12AppTests.FakeStagewatch(ShowBus12AppTests.Frame, null);
        try
        {
            var granted = "{\"type\":\"grants\",\"from\":\"" + Sw + "\",\"rev\":4,\"link\":\"local\",\"grants\":["
                          + "{\"window\":\"lobby\",\"name\":\"Lobby\",\"how\":\"page\",\"path\":\"/w/" + Token + "\",\"cannot\":\"\"},"
                          + "{\"window\":\"stage\",\"name\":\"Stage\",\"how\":\"draw\",\"cannot\":\"\"}]}";
            sw.Grants = granted;
            var link = vm.State.Control.Stagewatch;
            link.Host = "127.0.0.1:" + sw.Port;
            link.ApiKey = "swk_nodekey";
            ShowBus12AppTests.Hear(services, sw.Port, "", "timer", "approvals");
            link.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();

            // Heard: kept with the machine, the page's address under its held form, answered with grants-seen byte for byte.
            ShowBus12AppTests.PumpUntil(() => { ShowBus12AppTests.Hear(services, sw.Port, "", "timer", "approvals"); return sw.LinkMessages.Any(m => m.Contains("grants-seen", StringComparison.Ordinal)); }, 60000,
                () => services.Kernel.Stagewatch.Status);
            Assert.Equal("{\"type\":\"grants-seen\",\"rev\":4,\"cannot\":\"\"}", sw.LinkMessages.Single(m => m.Contains("grants-seen", StringComparison.Ordinal)));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "lobby", "stage" }, link.Grants.Select(g => g.Window).ToArray());
            Assert.Equal("http://127.0.0.1:" + sw.Port + "/w/" + Token, services.WebPasses.Resolve(Held).Url);
            var row = services.Journal.Tail(20).Last(r => r.Kind == "Grants");
            Assert.Contains("Lobby", row.Message, StringComparison.Ordinal);
            Assert.Contains("Stage (no address)", row.Message, StringComparison.Ordinal);

            // The settings carry the grant with its path masked; no reader carries the address.
            CarriesNoAddress(Secrets.Redact(JsonUtil.Serialize(services.State)), "the settings as a bundle carries them");
            CarriesNoAddress(new CommandRouter(services).StateJson(), "STATE");
            CarriesNoAddress(string.Join("\n", services.Journal.Tail(30).Select(r => $"{r.Target} {r.Message}")), "the journal");
            CarriesNoAddress(services.Kernel.Stagewatch.GrantsWords, "the link's words");

            // An older revision on the same link is not taken and not answered.
            sw.Grants = granted.Replace("\"rev\":4", "\"rev\":3", StringComparison.Ordinal).Replace("\"name\":\"Lobby\"", "\"name\":\"Old\"", StringComparison.Ordinal);
            ShowBus12AppTests.PumpFor(3000);
            Assert.Single(sw.LinkMessages, m => m.Contains("grants-seen", StringComparison.Ordinal));
            Assert.Equal("Lobby", link.Grants[0].Name);

            // Withdrawn (a newer revision with none): nothing kept, the held form resolves to nothing, one journal row, answered.
            sw.Grants = "{\"type\":\"grants\",\"from\":\"" + Sw + "\",\"rev\":5,\"link\":\"local\",\"grants\":[]}";
            ShowBus12AppTests.PumpUntil(() => { ShowBus12AppTests.Hear(services, sw.Port, "", "timer", "approvals"); return sw.LinkMessages.Any(m => m.Contains("\"rev\":5", StringComparison.Ordinal)); }, 60000);
            Assert.Equal("{\"type\":\"grants-seen\",\"rev\":5,\"cannot\":\"\"}", sw.LinkMessages.Last());
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(link.Grants);
            Assert.True(string.IsNullOrEmpty(services.WebPasses.Resolve(Held).Url));
            Assert.Equal("no Stagewatch window is granted to this desk now", services.Journal.Tail(20).Last(r => r.Kind == "Grants").Message);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }
    /// <summary>
    /// A Stagewatch heard at one of this machine's own network addresses is dialled at loopback: the key goes plain only there, and a
    /// grant's address rides that local carrier, opened at loopback — never at the machine's network address, which reads as another's.
    /// </summary>
    [AvaloniaFact]
    public void AStagewatchHeardAtThisMachinesOwnAddressIsDialledAtLoopbackAndItsGrantOpensThere()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var sw = new ShowBus12AppTests.FakeStagewatch(ShowBus12AppTests.Frame, null);
        var own = IPAddress.Parse("192.0.2.10");                                                        // this machine's, as the test plays it
        void HearAtOwn()
        {
            var hello = JsonSerializer.Serialize(new { t = "main", v = 1, proto = 1, id = Sw, name = "Hall A", ports = new { http = sw.Port, ws = sw.Port, https = 0 }, caps = new[] { "timer" } });
            services.Kernel.Beacon.ShowBus.Hear(System.Text.Encoding.UTF8.GetBytes(hello), own.ToString(), DateTime.UtcNow, "not-this-process");
        }
        try
        {
            sw.Grants = "{\"type\":\"grants\",\"from\":\"" + Sw + "\",\"rev\":1,\"link\":\"local\",\"grants\":["
                        + "{\"window\":\"lobby\",\"name\":\"Lobby\",\"how\":\"page\",\"path\":\"/w/" + Token + "\",\"cannot\":\"\"}]}";
            services.Kernel.Stagewatch.IsThisMachine = a => a.Equals(own);
            var link = vm.State.Control.Stagewatch;
            link.Host = "";                                                                               // the first Stagewatch heard
            link.ApiKey = "swk_nodekey";
            HearAtOwn();
            link.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();

            ShowBus12AppTests.PumpUntil(() => { HearAtOwn(); return sw.LinkMessages.Any(m => m.Contains("grants-seen", StringComparison.Ordinal)); }, 60000,
                () => services.Kernel.Stagewatch.Status);
            Assert.Contains(sw.Authorizations, a => a.EndsWith("swk_nodekey", StringComparison.Ordinal));  // keyed: it was dialled at loopback
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("http://127.0.0.1:" + sw.Port + "/w/" + Token, services.WebPasses.Resolve(Held).Url);
            Assert.Equal(StagewatchGrants.LinkLocal, Assert.Single(link.Grants).Link);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }
}
