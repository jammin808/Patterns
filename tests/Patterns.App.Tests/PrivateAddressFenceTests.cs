using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;
using static Patterns.App.Tests.TwinAppTests;

namespace Patterns.App.Tests;

/// <summary>
/// Round 89.4, the App's half of the fence: a desk whose show names a Stagewatch window's address and a page with a PIN
/// publishes neither pass anywhere a reader can reach — STATE (a held page by its kept address and its host, a page
/// that went to a private address by itself by its host), CONTEXT, the Eye now and replayed, the journal and the log
/// whoever writes to them, the show file, the recovery record, the Super Check and a support bundle — while this
/// machine's book alone holds both. The twin carries the book to a standby only while it carries the show's secrets,
/// the standby opens a kept page with it after a takeover and hands its own back with the show, and a standby without
/// it draws the page black and says so.
/// </summary>
public class PrivateAddressFenceTests
{
    private const string Token = "abcdefghijkmnpqrstuvwxyz23";
    private const string Window = "http://10.0.0.5:9924/w/" + Token;

    /// <summary>The PIN's value: one no timestamp, duration, port or measured number in a file the desk writes can hold by chance.</summary>
    private const string PinValue = "pz4321qk";
    private const string Pin = "https://x/feed?pin=" + PinValue;

    private static void CarriesNoPass(string? text, string where)
    {
        var t = text ?? "";
        Assert.False(t.Contains(Token, StringComparison.Ordinal), $"{where} carries the window's token:\n{t}");
        Assert.False(t.Contains(PinValue, StringComparison.Ordinal), $"{where} carries the PIN:\n{t}");
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

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

    private static void Settings(string dir, Action<ShowState> edit)
    {
        var state = SettingsStore.Fresh();
        edit(state);
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(state));
    }

    private static JsonElement Web(CommandRouter router)
    {
        using var doc = JsonDocument.Parse(router.StateJson());
        return doc.RootElement.GetProperty("web").Clone();
    }

    [AvaloniaFact]
    public void NoPassReachesStateContextTheEyeTheJournalTheLogTheFilesTheSuperCheckOrTheBundle()
    {
        // A show file from before the round: the pattern's page carries a PIN, its first layer a Stagewatch window.
        var b = TestApp.Boot(prepare: dir => Settings(dir, s =>
        {
            s.Pattern.Kind = PatternKind.Media;
            s.Pattern.Media.Source = MediaSource.Web;
            s.Pattern.Media.WebUrl = Pin;
            s.Pattern.Layer1.Enabled = true;
            s.Pattern.Layer1.Source = LayerSource.Web;
            s.Pattern.Layer1.WebUrl = Window;
        }));
        var zip = Path.Combine(Path.GetTempPath(), "patterns-fence-bundle-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            Assert.Equal(1, services.WebPassesKeptRow());                                       // kept on boot, one journal row
            var keptPin = services.State.Pattern.Media.WebUrl;
            var keptWindow = services.State.Pattern.Layer1.WebUrl;
            Assert.True(WindowAddresses.IsKept(keptPin));
            Assert.True(WindowAddresses.IsKept(keptWindow));
            var made = FakePages(services);
            Dispatcher.UIThread.RunJobs();
            services.ReconcileInputs();
            Dispatcher.UIThread.RunJobs();
            var router = new CommandRouter(services);

            // STATE: the page on air by its kept address and its host — what this machine can do with it said beside it.
            var web = Web(router);
            Assert.Equal(keptPin, web.GetProperty("url").GetString());
            Assert.Equal("x (private address, kept)", web.GetProperty("page").GetString());
            CarriesNoPass(router.StateJson(), "STATE");

            // The wire: WEB OPEN with a window's address — the reply and the journal's row name its host only; the page is sent its kept address.
            var reply = Send(router, "WEB OPEN " + Window);
            Assert.StartsWith("OK", reply, StringComparison.Ordinal);
            CarriesNoPass(reply, "WEB OPEN's reply");
            var opened = services.Journal.Tail(1)[0];
            Assert.Equal("WebOpen", opened.Kind);
            Assert.Contains("10.0.0.5 (private address, kept on this machine)", opened.Message, StringComparison.Ordinal);
            CarriesNoPass(opened.Target + " " + opened.Message, "WEB OPEN's journal row");
            var onAir = made.Single(p => p.Navigations.Count > 0);
            Assert.Equal(keptWindow, onAir.Navigations.Last());                                  // the same address kept again is its own entry
            web = Web(router);
            Assert.Equal(keptWindow, web.GetProperty("url").GetString());
            Assert.Equal("10.0.0.5 (private address, kept)", web.GetProperty("page").GetString());

            // An approved line another app proposed runs through the same path, and says no more.
            var (approvedOk, approvedWords) = TestApp.Pump(services.Kernel.Stagewatch.RunApprovedLine!("WEB OPEN " + Pin));
            Assert.True(approvedOk, approvedWords);
            CarriesNoPass(approvedWords, "an approved line's words");

            // A page that went to a private address by itself — a link, a redirect — is named by its host.
            onAir.Navigate("https://x/elsewhere?pin=" + PinValue);
            web = Web(router);
            Assert.Equal("https://x", web.GetProperty("url").GetString());
            Assert.Equal("x", web.GetProperty("page").GetString());
            CarriesNoPass(router.StateJson(), "STATE after the page moved by itself");

            // A page's title: a browser names a document without one by its address, pass and all. STATE's web.title (the
            // deck's web_title is that field) and the Media page's status line say it in public, whatever the source says.
            foreach (var title in new[] { Window, "10.0.0.5:9924/w/" + Token, "x/feed?pin=" + PinValue })
            {
                foreach (var p in made) p.Title = title;
                web = Web(router);
                Assert.NotEqual("", web.GetProperty("title").GetString());
                CarriesNoPass(web.GetProperty("title").GetString(), "STATE's web.title");
                CarriesNoPass(router.StateJson(), "STATE with a title that names an address");
                vm.Media.RefreshWebControls();
                Assert.StartsWith("Showing", vm.Media.WebPageStatus, StringComparison.Ordinal);
                CarriesNoPass(vm.Media.WebPageStatus, "the Media page's status line");
            }
            foreach (var p in made) p.Title = "A page";

            // CONTEXT and the Eye.
            CarriesNoPass(router.ContextJson(), "CONTEXT");
            services.Eye.Refresh();
            var eye = Send(router, "EYE");
            Assert.StartsWith("OK {", eye, StringComparison.Ordinal);
            CarriesNoPass(eye, "EYE");
            Assert.Contains("kept://10.0.0.5:9924/", eye, StringComparison.Ordinal);

            // Writers that forgot: a log line, an exception's text and a journal row carrying the raw address are said in public.
            Log.Warn($"Web page navigate failed: {Window}", new InvalidOperationException("could not open " + Pin));
            Log.Info($"Approval desk (FOH): approved \"WEB OPEN {Window}\" — done");
            services.Journal.Record("wire", "WebOpen", Window, "Done", "lobby → " + Pin);
            var log = File.ReadAllText(Path.Combine(b.Dir, "patterns.log"));
            Assert.Contains("Web page navigate failed: http://10.0.0.5:9924 (private address)", log, StringComparison.Ordinal);
            Assert.Contains("could not open https://x (private address)", log, StringComparison.Ordinal);
            Assert.Contains("Approval desk (FOH): approved \"WEB OPEN http://10.0.0.5:9924 (private address)\"", log, StringComparison.Ordinal);

            // The Super Check: both pages kept on this machine, green, and its report names neither pass.
            var report = services.Metrics.RunSuperCheck();
            var row = report.Rows.Single(r => r.Item == "Private pages");
            Assert.Equal(CheckLight.Green, row.Light);
            Assert.Equal("2 kept on this machine", row.Value);
            CarriesNoPass(File.ReadAllText(services.Metrics.LastReportPath), "the Super Check's report");

            // The files the desk writes beside the settings: none carries a pass but the book, which holds both. The show
            // file's .bak is the version before the last write — the file as the older build left it, until the next save
            // rotates it out — and, like backups/, an older copy that stays on this machine: never in a bundle.
            services.WriteRunPlace();
            TestApp.FlushFiles(services);
            var previous = Path.GetFileName(services.Store.SettingsPath) + ".bak";
            Assert.DoesNotContain(previous, SupportBundle.Files);
            var named = new List<string>();
            foreach (var file in Directory.GetFiles(b.Dir))
            {
                var name = Path.GetFileName(file);
                named.Add(name);
                var text = File.ReadAllText(file);
                if (name == WebPassBook.FileName)
                {
                    Assert.Contains(Token, text, StringComparison.Ordinal);
                    Assert.Contains(PinValue, text, StringComparison.Ordinal);
                    continue;
                }
                if (name == previous) continue;
                CarriesNoPass(text, name);
            }
            Assert.Contains("patterns.settings.json", named);
            Assert.Contains(ShowLog.FileName, named);
            Assert.Contains("patterns.log", named);
            Assert.Contains(SuperCheck.FileName, named);
            Assert.Contains("patterns.recovery.json", named);                                     // the recovery record was among the files read

            // The twin's mirror of the show carries kept addresses only, the show's secrets sent or not.
            CarriesNoPass(TwinSync.WireJson(services.State, sendSecrets: false), "the twin's show without the secrets");
            CarriesNoPass(TwinSync.WireJson(services.State, sendSecrets: true), "the twin's show with the secrets");

            // A support bundle, built as the desk builds it: no entry carries a pass, and the book is not in it.
            SupportBundle.Build(b.Dir, zip, "Site: Lobby", services.Kernel.SecretValues());
            using (var bundle = ZipFile.OpenRead(zip))
            {
                Assert.Null(bundle.GetEntry(WebPassBook.FileName));
                foreach (var entry in bundle.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    CarriesNoPass(reader.ReadToEnd(), "the bundle's " + entry.FullName);
                }
            }

            // A row an older build wrote raw stays in its file, and reaches every reader of the journal said in public.
            var old = new ShowLogEntry(DateTime.UtcNow.AddSeconds(-5), "wire", "WebOpen", Window, "Done", "lobby → " + Pin);
            File.AppendAllText(services.Journal.Path, JsonUtil.SerializeCompact(old) + Environment.NewLine);
            CarriesNoPass(string.Join("\n", services.Journal.Tail(50).Select(r => $"{r.Target} {r.Message}")), "the journal read back");
            CarriesNoPass(router.ContextJson(), "CONTEXT with an older row");
            CarriesNoPass(Send(router, "EYE AT " + ReplayTime.Stamp(DateTime.UtcNow.AddSeconds(1))), "the Eye replayed over an older row");

            // A raw address written in by hand after the load's scrub: inert, and the Super Check names where it sits.
            services.State.Pattern.Layer2.WebUrl = Window;
            report = services.Metrics.RunSuperCheck();
            row = report.Rows.Single(r => r.Item == "Private pages");
            Assert.Equal(CheckLight.Amber, row.Light);
            Assert.Equal("1 of 3 open nothing here · written raw", row.Value);
            Assert.Contains("the pattern's layer 2 holds a private address written raw into the show (unkept://10.0.0.5:9924)", row.Note, StringComparison.Ordinal);
            CarriesNoPass(SuperCheck.ToText(report), "the Super Check with a raw address");
            CarriesNoPass(router.StateJson(), "STATE with a raw address in the show");
            services.State.Pattern.Layer2.WebUrl = "";
        }
        finally
        {
            b.Dispose();
            if (File.Exists(zip)) File.Delete(zip);
        }
    }

    // ---- the twin ---------------------------------------------------------------------------------------------------

    private static (TcpClient Client, NetworkStream Stream, StreamReader Reader) JoinMain(int port, TwinJoin join)
    {
        var client = new TcpClient();
        TestApp.Pump(client.ConnectAsync(IPAddress.Loopback, port).ContinueWith(_ => true));
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        Assert.True(Join(stream, reader, join, "hunter2"), "the main proved the key first");
        return (client, stream, reader);
    }

    /// <summary>The words a joiner hears from the WELCOME up to and including the SHOW.</summary>
    private static List<TwinMessage> UpToTheShow(StreamReader reader)
    {
        var heard = new List<TwinMessage>();
        while (true)
        {
            var msg = TwinMessage.Parse(ReadLine(reader));
            heard.Add(msg);
            if (msg.Word is TwinWord.Show or TwinWord.Refused) return heard;
        }
    }

    [AvaloniaFact]
    public void AMainSendsTheKeptPassesItsShowNamesToAStandbyOnlyWhileTheTwinCarriesTheShowsSecrets()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var keptWindow = services.WebPasses.Secure(Window);
            Assert.True(WindowAddresses.IsKept(keptWindow));
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Web;
            vm.State.Pattern.Media.WebUrl = keptWindow;
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.SendSecrets = true;
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();

            // EDIT SAFE open: the air record holds the programme whole, the page on it as its kept address.
            RecoverySnapshot? moved = null;
            services.RecoveryMoved += r => moved = r;
            vm.IsSandboxActive = true;
            services.WriteRunPlace();
            PumpUntil(() => moved?.Air is not null);

            // Carried: the book rides the welcome, before the show that names it; the show and the air hold the kept address only.
            var (client, stream, reader) = JoinMain(vm.State.Twin.Port, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", ""));
            using (client)
            using (reader)
            {
                var heard = UpToTheShow(reader);
                Assert.Equal(new[] { TwinWord.Welcome, TwinWord.Kept, TwinWord.Secrets, TwinWord.Show }, heard.Select(m => m.Word));
                Assert.True(WebPassBook.TryParse(heard[1].Payload, out var book));
                Assert.Equal(Window, book.Resolve(keptWindow).Url);
                Assert.True(TwinSecretsOffer.Parse(heard[2].Payload)!.Rides, "a standby on this machine, SEND SECRETS on: the secrets ride");
                CarriesNoPass(heard[3].Payload, "the SHOW line");
                Assert.Contains(keptWindow, heard[3].Payload, StringComparison.Ordinal);
                var air = ReadWord(reader, TwinWord.Air).Payload;
                CarriesNoPass(air, "the AIR line");
                Assert.Contains(keptWindow, air, StringComparison.Ordinal);
                PumpUntil(() => services.Twin.StandbyNames.Count == 1);

                // A page kept on the main goes to the standby once the show names it — within the beat — and a pass the
                // show does not name never travels.
                var keptPin = services.WebPasses.Secure(Pin);
                var unnamed = services.WebPasses.Secure("https://w/feed?token=qq8888zz");
                Assert.True(WindowAddresses.IsKept(unnamed));
                services.BulkEdit(() => vm.State.Web.SavedUrls.Add(keptPin));
                var again = ReadWord(reader, TwinWord.Kept);
                Assert.True(WebPassBook.TryParse(again.Payload, out var grown));
                Assert.Equal(2, grown.Count);
                Assert.Equal(Pin, grown.Resolve(keptPin).Url);
                Assert.Equal(Window, grown.Resolve(keptWindow).Url);
                Assert.DoesNotContain("qq8888zz", again.Payload, StringComparison.Ordinal);

                // The show's secrets off: nothing more of the book travels, however it moves.
                services.BulkEdit(() => vm.State.Twin.SendSecrets = false);
                services.WebPasses.Secure("https://y/feed?key=7777");
                services.Twin.Tick();
                services.Twin.Tick();
                Assert.False(Arrives(client, reader, TwinWord.Kept, 1500), "the book travelled with the show's secrets off");
            }

            // A standby joining with the secrets off hears the show and no book.
            var (client2, _, reader2) = JoinMain(vm.State.Twin.Port, new TwinJoin("Backup desk", "BACKUP-PC", "abcd5678", ""));
            using (client2)
            using (reader2)
            {
                var heard = UpToTheShow(reader2);
                Assert.DoesNotContain(heard, m => m.Word == TwinWord.Kept);
                Assert.Equal(TwinWord.Show, heard[^1].Word);
            }

            // A caller never hears the book, the secrets carried or not: it never takes the show.
            services.BulkEdit(() => vm.State.Twin.SendSecrets = true);
            var (client3, _, reader3) = JoinMain(vm.State.Twin.Port, new TwinJoin("Caller", "CALL-PC", "abcd9999", "", Kind: "caller"));
            using (client3)
            using (reader3)
            {
                var heard = UpToTheShow(reader3);
                Assert.DoesNotContain(heard, m => m.Word == TwinWord.Kept);
                services.WebPasses.Secure("https://z/feed?token=5555");
                services.Twin.Tick();
                Assert.False(Arrives(client3, reader3, TwinWord.Kept, 1500), "a caller heard the book");
            }
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AMainTakesTheHoldingStandbysPassesInAgainstTheShowItSentAndSendsItNone()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            // A full book on the main; its show names one of the passes — one a standby standing by would be sent.
            for (var i = 0; i < WebPassBook.Max; i++) services.WebPasses.Secure($"https://h{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}.local/feed?pin=1");
            Assert.Equal(WebPassBook.Max, services.WebPasses.Book.Count);
            var named = WebPassBook.AddressOf(services.WebPasses.Book.Passes[^1]);
            vm.State.Web.SavedUrls.Add(named);
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.SendSecrets = true;
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();

            // A standby that ran the show while this desk was away: welcomed with no show and no pass.
            var (client, stream, reader) = JoinMain(vm.State.Twin.Port, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", "", TookOver: true, Handover: "takeover-1"));
            using (client)
            using (reader)
            {
                var heard = new List<TwinWord>();
                while (heard.LastOrDefault() != TwinWord.Beat) heard.Add(TwinMessage.Parse(ReadLine(reader)).Word);
                Assert.Equal(new[] { TwinWord.Welcome, TwinWord.Beat }, heard);

                // It sends its show — which names a page pasted there — its air, then that page's pass.
                var pasted = WindowAddresses.Kept("10.0.0.7", 9924, "q3x7m2k6p4ab");
                const string real = "http://10.0.0.7:9924/w/" + Token;
                var (theirBook, _) = WebPassBook.Empty.Keep(real, () => "q3x7m2k6p4ab", DateTime.UtcNow);
                var theirs = new ShowState { Name = "Run on the standby" };
                theirs.Web.SavedUrls.Add(pasted);
                Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.ShowJson(theirs)));
                Write(stream, TwinMessage.Format(TwinWord.Air, "null"));
                Write(stream, TwinMessage.Format(TwinWord.Kept, theirBook.ToJson(indented: false)));
                PumpUntil(() => services.WebPasses.Resolve(pasted).Resolved);
                Assert.Equal(real, services.WebPasses.Resolve(pasted).Url);                       // it opens here after TAKE BACK
                Assert.Equal(WebPassBook.Max, services.WebPasses.Book.Count);                     // a pass no show names made the room
                Assert.True(services.WebPasses.Resolve(named).Resolved);                           // the one this desk's show names stands
                Assert.Contains(Token, File.ReadAllText(services.WebPasses.Path), StringComparison.Ordinal);

                // The book moved and the beats go on: a standby that holds the show is sent none of it.
                services.Twin.Tick();
                Assert.False(Arrives(client, reader, TwinWord.Kept, 1500), "a standby that holds the show was sent the main's passes");
            }
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbyOpensAKeptPageWithTheMainsBookHandsItsOwnBackAndWithoutItDrawsBlackWithWords()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var (services, vm, _) = b;
            var twin = services.Twin;
            vm.IsSandboxActive = false;
            var made = FakePages(services);
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.SendSecrets = true;                                                    // this desk's own setting: it decides what it hands back
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();

            // The main's show names two kept pages; its book, carried under its SendSecrets, holds only the window's.
            var keptWindow = WindowAddresses.Kept("10.0.0.5", 9924, "abcdefghijkl");
            const string elsewhere = "kept://x/abcdefghijkm";
            var (mainBook, _) = WebPassBook.Empty.Keep(Window, () => "abcdefghijkl", DateTime.UtcNow);
            var theirs = new ShowState { Name = "From the main" };
            theirs.Pattern.Kind = PatternKind.Media;
            theirs.Pattern.Media.Source = MediaSource.Web;
            theirs.Pattern.Media.WebUrl = keptWindow;
            theirs.Pattern.Layer1.Enabled = true;
            theirs.Pattern.Layer1.Source = LayerSource.Web;
            theirs.Pattern.Layer1.WebUrl = elsewhere;

            var (peer, reader, _) = AcceptJoin(main);
            using (peer)
            using (reader)
            {
                var stream = peer.GetStream();
                Write(stream, TwinMessage.Format(TwinWord.Welcome, new TwinWelcome("MAIN-DESK", "SOME-OTHER-PC", "ef01", 1, 0, "", "From the main").ToJson()));
                Write(stream, TwinMessage.Format(TwinWord.Kept, mainBook.ToJson(indented: false)));
                Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.ShowJson(theirs)));
                Write(stream, TwinMessage.Format(TwinWord.Air, "null"));
                PumpUntil(() => twin.Phase == TwinPhase.InStep && services.WebPasses.Resolve(keptWindow).Resolved);
                Assert.Equal(Window, services.WebPasses.Resolve(keptWindow).Url);
                Assert.Contains(Token, File.ReadAllText(services.WebPasses.Path), StringComparison.Ordinal);   // kept on this machine's disk too
                CarriesNoPass(File.ReadAllText(Path.Combine(b.Dir, "patterns.log")), "the standby's log");

                // The page the main kept opens here; the one it did not is black, said so wherever it is read.
                Assert.Equal("10.0.0.5 (private address, kept)", services.WebPasses.Label(keptWindow));
                Assert.Equal("x (private address, kept on another machine)", services.WebPasses.Label(elsewhere));
                var row = services.Metrics.RunSuperCheck().Rows.Single(r => r.Item == "Private pages");
                Assert.Equal(CheckLight.Amber, row.Light);
                Assert.Contains($"the pattern's layer 1: {elsewhere} — {WebPassBook.NotHereWhy}", row.Note, StringComparison.Ordinal);

                // The main goes; this desk takes the show, and the kept page it opens resolves through its own book.
                peer.Close();
                PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            }
            var took = services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk);
            Assert.True(took.Ok, took.Message);
            services.ReconcileInputs();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(made, p => p.CurrentUrl == keptWindow);                                  // the page is handed the held address and resolves it itself
            Assert.Equal(Window, services.WebIn.ResolveOf(keptWindow).Url);
            Assert.False(services.WebIn.ResolveOf(elsewhere).Resolved);
            using (var doc = JsonDocument.Parse(new CommandRouter(services).StateJson()))
            {
                var web = doc.RootElement.GetProperty("web");
                Assert.Equal(keptWindow, web.GetProperty("url").GetString());
                Assert.Equal("10.0.0.5 (private address, kept)", web.GetProperty("page").GetString());
            }

            // A page pasted here while this desk has the show: kept here, and handed back after the show when the main
            // returns — the passes the show names, never one it does not.
            var keptPin = services.WebPasses.Secure(Pin);
            vm.State.Web.SavedUrls.Add(keptPin);
            services.WebPasses.Secure("https://w/feed?token=qq8888zz");
            var (peer2, reader2, join2) = AcceptJoin(main);
            using (peer2)
            using (reader2)
            {
                Assert.True(join2.TookOver);
                Write(peer2.GetStream(), TwinMessage.Format(TwinWord.Welcome, new TwinWelcome("MAIN-DESK", "SOME-OTHER-PC", "ef01", 1, 0, "", "From the main").ToJson()));
                CarriesNoPass(ReadWord(reader2, TwinWord.Show).Payload, "the show handed back");
                var kept = ReadWord(reader2, TwinWord.Kept);
                Assert.True(WebPassBook.TryParse(kept.Payload, out var handed));
                Assert.Equal(2, handed.Count);
                Assert.Equal(Pin, handed.Resolve(keptPin).Url);
                Assert.Equal(Window, handed.Resolve(keptWindow).Url);
                Assert.DoesNotContain("qq8888zz", kept.Payload, StringComparison.Ordinal);
            }

            // This desk's show secrets off: the show goes back, and no pass with it.
            vm.State.Twin.SendSecrets = false;
            var (peer3, reader3, join3) = AcceptJoin(main);
            using (peer3)
            using (reader3)
            {
                Assert.True(join3.TookOver);
                Write(peer3.GetStream(), TwinMessage.Format(TwinWord.Welcome, new TwinWelcome("MAIN-DESK", "SOME-OTHER-PC", "ef01", 1, 0, "", "From the main").ToJson()));
                ReadWord(reader3, TwinWord.Show);
                Assert.False(Arrives(peer3, reader3, TwinWord.Kept, 1500), "the standby handed back its passes with the show's secrets off");
            }
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }
}
