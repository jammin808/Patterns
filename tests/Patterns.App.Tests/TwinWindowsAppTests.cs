using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;
using static Patterns.App.Tests.TwinAppTests;

namespace Patterns.App.Tests;

/// <summary>
/// Show Bus 1.3 §12 on the twin: a window Stagewatch granted belongs to the node it was granted to, and a standby is a node
/// of its own. The standby tells its main which windows a Stagewatch granted it itself (WINDOWS — its id and the held forms, never a
/// path); the main's Super Check, STATE, line, glance and health words name each window on its air the standby lacks and the grant
/// Stagewatch is asked for; the standby says the same of its own grants against the air it mirrors; after a takeover its own grant opens
/// the window and, without one, the window is black with words. No grant's address rides the twin either way.
/// </summary>
public class TwinWindowsAppTests
{
    private const string Key = "hunter2";
    private const string Sw = "swdesk01";
    private const string Token = "abcdefghijkmnpqrstuvwxyz23";
    private const string StandbyId = "a1b2c3d4";
    private static readonly string Lobby = WindowAddresses.Grant(Sw, "lobby");

    private static StagewatchGrant LobbyGrant(string at) => new(Sw, "lobby", "lobby", StagewatchGrants.HowPage, at, "/w/" + Token, "", StagewatchGrants.LinkTls, "");

    private static void Grant(AppServices services, params StagewatchGrant[] grants)
    {
        services.State.Control.Stagewatch.SetGrants(grants);
        services.WebPasses.SetGrants(grants);
    }

    private static void OnAir(ShowState state)
    {
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Web;
        state.Pattern.Media.WebUrl = Lobby;
    }

    private static JsonElement TwinRow(AppServices services) => JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("twin");

    private static CheckRow? WindowsCheck(AppServices services) => SuperCheck.Run(services.Metrics.GatherFacts()).Rows.SingleOrDefault(r => r.Item == TwinWindows.Item);

    private static CheckLight EyeTwinLight(AppServices services)
    {
        services.Eye.Refresh();
        return services.Eye.Graph.Nodes.Single(n => n.Kind == EyeKind.Twin).Light;
    }

    private static int LogCount(TestApp.Booted b, string words)
    {
        var log = File.ReadAllText(Path.Combine(b.Dir, "patterns.log"));
        var count = 0;
        for (var at = log.IndexOf(words, StringComparison.Ordinal); at >= 0; at = log.IndexOf(words, at + words.Length, StringComparison.Ordinal)) count++;
        return count;
    }

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

    private static string AirShow(ShowState air)
        => TwinSync.WireAirJson(new RecoverySnapshot(true, false, DateTime.UtcNow, Sandboxed: true, Air: air), sendSecrets: false);

    private static void CarriesNoAddress(IEnumerable<string> lines, string who)
    {
        foreach (var line in lines) Assert.False(line.Contains(Token, StringComparison.Ordinal), $"{who} carried the window's address: {TwinMessage.Parse(line).Word}");
    }

    /// <summary>Every line that comes within the time, pumping the desk meanwhile; the read still in flight at the end is the link's last.</summary>
    private static List<string> LinesFor(StreamReader reader, int ms)
    {
        var heard = new List<string>();
        var deadline = Environment.TickCount64 + ms;
        var next = reader.ReadLineAsync();
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (!next.IsCompleted)
            {
                Thread.Sleep(5);
                continue;
            }
            if (!next.IsCompletedSuccessfully || next.Result is not { } line) break;
            heard.Add(line);
            next = reader.ReadLineAsync();
        }
        return heard;
    }

    [AvaloniaFact]
    public void TheMainNamesEachGrantedWindowOnItsAirTheStandbyLacksAndSendsNoGrantsAddress()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = Key;
            Grant(services, LobbyGrant("https://10.0.0.5:9924"));
            services.BulkEdit(() => OnAir(vm.State));
            Assert.Equal(new[] { "PGM" }, StagewatchGrants.Places(services.AirState, Lobby));
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(WindowsCheck(services));                                                    // no standby yet: the twin row says so

            using var client = new TcpClient();
            TestApp.Pump(client.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
            Assert.True(Join(stream, reader, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", ""), Key), "the main proved the key first");
            var heard = new List<string>();
            while (true)
            {
                var line = ReadLine(reader);
                heard.Add(line);
                if (TwinMessage.Parse(line).Word is TwinWord.Show or TwinWord.Refused) break;
            }
            PumpUntil(() => services.Twin.StandbyNames.Count == 1);

            // A standby that has not said (an older one, or a link not yet told): a takeover may show the window black.
            var row = WindowsCheck(services)!;
            Assert.Equal(CheckLight.Amber, row.Light);
            Assert.Equal("Backup desk has not said", row.Value);
            Assert.Contains("lobby on air here may show black after a takeover", row.Note, StringComparison.Ordinal);

            // It says it holds none: every reader names the window, where it is on air and the grant Stagewatch is asked for.
            Write(stream, TwinMessage.Format(TwinWord.Windows, new TwinWindowsHeld(StandbyId, Array.Empty<string>()).ToJson()));
            PumpUntil(() => WindowsCheck(services)?.Value == "lobby not held by Backup desk");
            const string Ask = "Stagewatch lobby is on air (the programme) and granted to this desk, not to its standby Backup desk — a takeover shows black there: Stagewatch → window grant lobby a1b2c3d4";
            row = WindowsCheck(services)!;
            Assert.Equal(CheckLight.Amber, row.Light);
            Assert.Equal("FIX: " + Ask, row.Note);
            Assert.Equal(Ask, TwinRow(services).GetProperty("windows").GetString());
            Assert.Contains("\"windows\":", services.Twin.StatusJson(), StringComparison.Ordinal);
            Assert.Contains("WINDOWS: " + Ask, services.Twin.Status, StringComparison.Ordinal);
            Assert.Contains("WINDOW BLACK ON TAKEOVER", services.Twin.GlanceWords, StringComparison.Ordinal);
            Assert.Contains("window grant lobby a1b2c3d4", services.Twin.HealthWords, StringComparison.Ordinal);
            Assert.Equal(CheckLight.Amber, EyeTwinLight(services));                                // the Eye's twin light reads the same

            // One air: EDIT SAFE open and the window edited off the preview — the air still shows it, so the row still names it.
            vm.IsSandboxActive = true;
            services.BulkEdit(() => vm.State.Pattern.Kind = PatternKind.Grid);
            Assert.Empty(StagewatchGrants.Places(vm.State, Lobby));
            Assert.Equal(new[] { "PGM" }, StagewatchGrants.Places(services.AirState, Lobby));
            Assert.Equal("lobby not held by Backup desk", WindowsCheck(services)?.Value);
            vm.IsSandboxActive = false;                                                             // discarded: the preview is the air again

            // It is granted the window itself: green, and nothing is said on the line.
            Write(stream, TwinMessage.Format(TwinWord.Windows, new TwinWindowsHeld(StandbyId, new[] { Lobby }).ToJson()));
            PumpUntil(() => WindowsCheck(services)?.Light == CheckLight.Green);
            Assert.Equal("1 on air · held by Backup desk", WindowsCheck(services)!.Value);
            Assert.Equal("", TwinRow(services).GetProperty("windows").GetString());
            Assert.DoesNotContain("WINDOWS:", services.Twin.Status, StringComparison.Ordinal);
            Assert.Equal(CheckLight.Green, EyeTwinLight(services));

            // A word that does not read — an address where a held form belongs — is a standby that has not said, never one that holds all;
            // the log says so once while its words stay unreadable, and again only after one read.
            const string Unread = "WINDOWS word could not be read";
            var bad = TwinMessage.Format(TwinWord.Windows, "{\"node\":\"a1b2c3d4\",\"held\":[\"https://10.0.0.5:9924/w/" + Token + "\"]}");
            Write(stream, bad);
            PumpUntil(() => WindowsCheck(services)?.Value == "Backup desk has not said");
            Write(stream, bad);
            Write(stream, bad);
            Write(stream, TwinMessage.Format(TwinWord.Windows, new TwinWindowsHeld(StandbyId, new[] { Lobby }).ToJson()));
            PumpUntil(() => WindowsCheck(services)?.Light == CheckLight.Green);                    // the bad words before it were read in order
            Assert.Equal(1, LogCount(b, Unread));
            Write(stream, bad);
            PumpUntil(() => WindowsCheck(services)?.Value == "Backup desk has not said");
            PumpUntil(() => LogCount(b, Unread) == 2);

            // The window off the air: nothing granted is on air, nothing to say.
            services.BulkEdit(() => vm.State.Pattern.Kind = PatternKind.Grid);
            PumpUntil(() => WindowsCheck(services) is null);

            // One air the other way: the window in the preview alone is on no air a takeover would take.
            vm.IsSandboxActive = true;
            services.BulkEdit(() => OnAir(vm.State));
            Assert.Equal(new[] { "PGM" }, StagewatchGrants.Places(vm.State, Lobby));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(WindowsCheck(services));
            vm.IsSandboxActive = false;

            // The grant's address rode no line the main sent: the show, the air, the secrets word, the beats.
            services.Twin.Tick();
            heard.AddRange(LinesFor(reader, 600));
            CarriesNoAddress(heard, "the main");
            Assert.True(heard.Count > 3);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbySaysWhichWindowsItHoldsReadsItsOwnGrantsAgainstTheMirroredAirAndAfterATakeoverOpensOnlyItsOwn()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var (services, vm, _) = b;
            var twin = services.Twin;
            var made = FakePages(services);
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = Key;
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();

            var (peer, reader, _) = AcceptJoin(main);
            using var _peer = peer;
            using var _reader = reader;
            var stream = peer.GetStream();

            // The main's show puts its granted window on the programme; this standby holds no grant of it.
            var theirs = new ShowState { Name = "From the main" };
            OnAir(theirs);
            Write(stream, TwinMessage.Format(TwinWord.Welcome, new TwinWelcome("MAIN-DESK", "MAIN-PC", "ef01", 1, 0, "", "From the main").ToJson()));
            Write(stream, TwinMessage.Format(TwinWord.Secrets, TwinSecretsOffer.For(theirs, TwinSecrets.Plain).ToJson()));
            Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.WireJson(theirs, sendSecrets: false)));
            Write(stream, TwinMessage.Format(TwinWord.Air, "null"));
            PumpUntil(() => twin.Phase == TwinPhase.InStep);
            var self = services.Kernel.ShowBusId;

            // Once on the link: its id and no window — never an address.
            twin.Tick();
            var word = ReadWord(reader, TwinWord.Windows);
            var said = TwinWindowsHeld.Parse(word.Payload)!;
            Assert.Equal(self, said.Node);
            Assert.Empty(said.Held);
            var row = WindowsCheck(services)!;
            Assert.Equal(CheckLight.Amber, row.Light);
            Assert.Equal("lobby not held by this desk", row.Value);
            Assert.Equal($"FIX: Stagewatch lobby is on air at MAIN-DESK (the programme) and not granted to this desk — a takeover shows black there: Stagewatch → window grant lobby {self} (NODES ASK WINDOW lobby asks it from this desk once its Stagewatch link is up)", row.Note);
            Assert.Contains("window grant lobby " + self, TwinRow(services).GetProperty("windows").GetString(), StringComparison.Ordinal);
            Assert.Contains("WINDOW BLACK ON TAKEOVER", twin.GlanceWords, StringComparison.Ordinal);
            Assert.Contains("window grant lobby " + self, twin.Status, StringComparison.Ordinal);
            Assert.Equal(CheckLight.Amber, EyeTwinLight(services));

            // One air: the main's EDIT SAFE open, its frozen air (the AIR record) without the window — not on the air a takeover would take.
            var edited = new ShowState { Name = "From the main" };
            Write(stream, TwinMessage.Format(TwinWord.Air, AirShow(edited)));
            PumpUntil(() => WindowsCheck(services) is null);
            // And the other way: the window on the frozen air alone, edited off the show this desk mirrors.
            Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.WireJson(edited, sendSecrets: false)));
            Write(stream, TwinMessage.Format(TwinWord.Air, AirShow(theirs)));
            PumpUntil(() => WindowsCheck(services)?.Value == "lobby not held by this desk");
            Assert.Empty(StagewatchGrants.Places(services.Kernel.State, Lobby));
            // The main's EDIT SAFE closed: its show is its air again.
            Write(stream, TwinMessage.Format(TwinWord.Air, "null"));
            PumpUntil(() => WindowsCheck(services) is null);
            Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.WireJson(theirs, sendSecrets: false)));
            PumpUntil(() => WindowsCheck(services)?.Value == "lobby not held by this desk");

            // Stagewatch grants it the window itself: the main is told the held form (and only that), and the row turns green.
            Grant(services, LobbyGrant("https://10.0.0.5:9924"));
            twin.Tick();
            word = ReadWord(reader, TwinWord.Windows);
            Assert.Equal(new[] { Lobby }, TwinWindowsHeld.Parse(word.Payload)!.Held);
            Assert.DoesNotContain(Token, word.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("10.0.0.5", word.Payload, StringComparison.Ordinal);
            Assert.Equal(CheckLight.Green, WindowsCheck(services)!.Light);
            Assert.Equal("1 on air · held here", WindowsCheck(services)!.Value);

            // A tick with nothing changed says nothing again.
            twin.Tick();
            Assert.DoesNotContain(LinesFor(reader, 500), l => TwinMessage.Parse(l).Word == TwinWord.Windows);

            // The main goes and this standby takes the show: the window opens through its own grant, at its own address.
            peer.Close();
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            var took = services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk);
            Assert.True(took.Ok, took.Message);
            Assert.Equal(new[] { "PGM" }, StagewatchGrants.Places(services.AirState, Lobby));
            Assert.Equal("https://10.0.0.5:9924/w/" + Token, services.WebPasses.Resolve(Lobby).Url);

            // On the air now, green only once its page is ready here — observed, never claimed from the grant alone.
            Dispatcher.UIThread.RunJobs();
            services.ReconcileInputs();
            Dispatcher.UIThread.RunJobs();
            row = WindowsCheck(services)!;
            Assert.Equal(CheckLight.Amber, row.Light);
            Assert.Equal("lobby not ready here", row.Value);
            Assert.Contains("is on air here (the programme) from this desk's own grant and black until its page is ready: ", row.Note, StringComparison.Ordinal);
            var page = made.Last(p => !p.Disposed && (p.CurrentUrl == Lobby || p.Navigations.Contains(Lobby)));
            page.Shell = new WebShellState(3, "ok", "clock", false, "", "");
            Assert.True(services.StagewatchWindows.Readiness(services.StagewatchWindows.Find("lobby")).Ready);
            row = WindowsCheck(services)!;
            Assert.Equal(CheckLight.Green, row.Light);
            Assert.Equal("1 on air · open here", row.Value);

            // One air on this desk too: its own EDIT SAFE open, the window edited off the preview — still on its air, still read.
            vm.IsSandboxActive = true;
            services.BulkEdit(() => vm.State.Pattern.Kind = PatternKind.Grid);
            Assert.Empty(StagewatchGrants.Places(vm.State, Lobby));
            Assert.Equal("1 on air · open here", WindowsCheck(services)?.Value);
            vm.IsSandboxActive = false;

            // Without a grant of its own the window is black, and every reader says so as it is now, and what to ask Stagewatch for.
            Grant(services);
            var black = services.WebPasses.Resolve(Lobby);
            Assert.Null(black.Url);
            Assert.Equal(WebPassBook.NotGrantedWhy, black.Why);
            Assert.False(services.StagewatchWindows.Readiness(services.StagewatchWindows.Find("lobby")).Ready);
            row = WindowsCheck(services)!;
            Assert.Equal(CheckLight.Amber, row.Light);
            Assert.Equal($"FIX: Stagewatch lobby is on air here (the programme) and black — no grant of this desk's own opens it: Stagewatch → window grant lobby {self} (NODES ASK WINDOW lobby asks it from this desk once its Stagewatch link is up)", row.Note);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }
}
