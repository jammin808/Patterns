using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Converters;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering.Media;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 89.3, held addresses on the desk: this machine's book of kept page addresses and its one writer, and every
/// entry that hands the show an address — the wire's WEB OPEN, OSC, the Media page's boxes and buttons, a cue step, a
/// cue sheet, the Library's saved pages and presets, a device's line, a show read at boot or from a file — keeping a
/// private one here and holding <c>kept://…</c> in its place; the browser engine resolving a held page through the book
/// alone (and again when the book moves); the native player asking its tool for the real address under the held one.
/// STATE, the logs, the twin and the full fence are 89.4's.
/// </summary>
public class WebPassesAppTests
{
    private const string Token = "abcdefghijkmnpqrstuvwxyz23";
    private const string Window = "http://10.0.0.5:9924/w/" + Token;
    private const string Pin = "https://x/feed?pin=4321";

    private static void CarriesNoPass(string? text, string where)
    {
        var t = text ?? "";
        Assert.False(t.Contains(Token, StringComparison.Ordinal), $"{where} carries the window's token:\n{t}");
        Assert.False(t.Contains("4321", StringComparison.Ordinal), $"{where} carries the PIN:\n{t}");
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-webpasses-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Remove(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder the system cleans up later.
        }
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

    private static void ShowPage(AppServices services, string url)
    {
        services.State.Pattern.Kind = PatternKind.Media;
        services.State.Pattern.Media.Source = MediaSource.Web;
        services.State.Pattern.Media.WebUrl = url;
        Dispatcher.UIThread.RunJobs();
        services.ReconcileInputs();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settings(string dir, Action<ShowState> edit)
    {
        var state = SettingsStore.Fresh();
        edit(state);
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(state));
    }

    // ---- the book's one writer ----------------------------------------------------------------------------------

    [Fact]
    public void TheBookKeepsAnAddressOnceOnTheDiskBeforeAnyReaderSeesIt()
    {
        var dir = TempDir();
        try
        {
            var passes = new WebPasses(dir);
            var changed = 0;
            var said = new List<string>();
            passes.Changed += () => changed++;
            passes.Noticed += said.Add;

            var kept = passes.Secure(Window);
            Assert.True(WindowAddresses.IsKept(kept));
            Assert.StartsWith("kept://10.0.0.5:9924/", kept, StringComparison.Ordinal);
            CarriesNoPass(kept, "the kept address");
            Assert.Equal(kept, passes.Secure(Window));                                          // the same address kept again is its own entry
            Assert.Equal(kept, passes.Secure(kept));                                            // a held address is never kept again
            Assert.Equal(1, passes.Book.Count);
            Assert.Equal(1, changed);
            Assert.Single(said);
            Assert.Contains("Kept 10.0.0.5's private address on this machine", said[0], StringComparison.Ordinal);
            CarriesNoPass(said[0], "the status words");

            // On the disk before it was answered: another reader of the folder opens it.
            Assert.Contains(Token, File.ReadAllText(passes.Path), StringComparison.Ordinal);
            Assert.Equal(Window, new WebPasses(dir).Resolve(kept).Url);

            // A public address is not kept; a line of words has its private addresses held, written bare or with a scheme.
            Assert.Equal("https://example.com/schedule", passes.Secure("https://example.com/schedule"));
            Assert.Equal($"WEB OPEN {kept}", passes.SecureText("WEB OPEN " + Window));
            var bare = passes.SecureText("WEB OPEN 10.0.0.5:9924/w/" + Token + " ON lobby");   // typed bare, read as the desk opens it: https
            var bareKept = passes.Secure("https://10.0.0.5:9924/w/" + Token);
            Assert.Equal($"WEB OPEN {bareKept} ON lobby", bare);
            Assert.NotEqual(kept, bareKept);
            Assert.Equal("CUE GO", passes.SecureText("CUE GO"));
            Assert.Equal(2, passes.Book.Count);
        }
        finally
        {
            Remove(dir);
        }
    }

    [Fact]
    public void ABookThatCannotBeReadIsNeverWrittenOverAndNothingIsKept()
    {
        foreach (var unreadable in new[] { "{\"v\":2,\"passes\":[]}", "not a book" })
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, WebPassBook.FileName);
                File.WriteAllText(path, unreadable);
                var passes = new WebPasses(dir);
                Assert.NotEqual("", passes.Frozen);

                var held = passes.Secure(Window, out var why);
                Assert.Equal("unkept://10.0.0.5:9924", held);                                    // black, with words — never the pass
                Assert.Equal(passes.Frozen, why);
                Assert.Equal(unreadable, File.ReadAllText(path));                                  // another version's book stays as it was
                Assert.Equal(0, passes.Book.Count);
            }
            finally
            {
                Remove(dir);
            }
        }
    }

    [Fact]
    public void ABookThatCannotBeWrittenKeepsNothingAndTheAddressIsHeldUnkept()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, WebPassBook.FileName));                  // the book's name taken by a folder: no write lands
            var passes = new WebPasses(dir);
            var said = new List<string>();
            passes.Noticed += said.Add;

            var held = passes.Secure(Pin, out var why);
            Assert.Equal("unkept://x", held);
            Assert.Contains("could not be written", why, StringComparison.Ordinal);
            Assert.Equal(0, passes.Book.Count);                                                  // never kept for this run alone
            Assert.Single(said);
            Assert.Contains("could not be kept on this machine", said[0], StringComparison.Ordinal);
            CarriesNoPass(said[0] + why, "the refusal");
        }
        finally
        {
            Remove(dir);
        }
    }

    [Fact]
    public void AFullBookMakesRoomOnlyFromAPassNoShowNames()
    {
        var dir = TempDir();
        try
        {
            var show = new ShowState();
            var passes = new WebPasses(dir) { Shows = () => new[] { show } };
            for (var i = 0; i < WebPassBook.Max; i++)
            {
                show.Web.SavedUrls.Add(passes.Secure($"https://h{i.ToString(CultureInfo.InvariantCulture)}.local/feed?pin=4321"));
            }
            Assert.Equal(WebPassBook.Max, passes.Book.Count);

            var refused = passes.Secure(Pin, out var why);
            Assert.Equal("unkept://x", refused);
            Assert.Equal(WebPassBook.FullWhy, why);

            var dropped = show.Web.SavedUrls[10];
            show.Web.SavedUrls.RemoveAt(10);                                                     // the show no longer names one
            var kept = passes.Secure(Pin);
            Assert.True(WindowAddresses.IsKept(kept));
            Assert.False(passes.Resolve(dropped).Resolved);
            Assert.All(show.Web.SavedUrls, address => Assert.True(passes.Resolve(address).Resolved));   // every pass the show names stands
        }
        finally
        {
            Remove(dir);
        }
    }

    [Fact]
    public void ATwinsPassesAreTakenInOnTheDiskFirstAndAFullBookGivesUpOnlyPassesNoShowNames()
    {
        var dir = TempDir();
        try
        {
            var show = new ShowState();
            var passes = new WebPasses(dir) { Shows = () => new[] { show } };
            var changed = 0;
            passes.Changed += () => changed++;
            var mine = passes.Secure(Pin);
            show.Web.SavedUrls.Add(mine);
            changed = 0;

            // A standby taking the main's passes: on the disk before the swap, raised once; the same passes again change nothing.
            var (theirs, kept) = WebPassBook.Empty.Keep(Window, () => "abcdefghijkl", DateTime.UtcNow);
            Assert.True(passes.MergeFromTwin(theirs, theirsStand: true, "MAIN-DESK"));
            Assert.Equal(1, changed);
            Assert.Equal(Window, new WebPasses(dir).Resolve(kept).Url);
            Assert.Equal(Pin, passes.Resolve(mine).Url);
            Assert.False(passes.MergeFromTwin(theirs, theirsStand: true, "MAIN-DESK"));
            Assert.Equal(1, changed);

            // A main taking back a standby's passes into a full book: one its own show names stands, the standby's pass the
            // show it holds for TAKE BACK names is taken, and the room is made from a pass no show names.
            for (var i = 0; passes.Book.Count < WebPassBook.Max; i++) passes.Secure($"https://h{i.ToString(CultureInfo.InvariantCulture)}.local/feed?pin=1");
            var (handed, pasted) = WebPassBook.Empty.Keep("https://y/feed?key=7", () => "q3x7m2k6p4ab", DateTime.UtcNow);
            var held = WebPassMigration.KeptIdsInText($"{{\"WebUrl\":\"{pasted}\"}}");
            Assert.True(passes.MergeFromTwin(handed, theirsStand: false, "Backup desk", held));
            Assert.Equal(WebPassBook.Max, passes.Book.Count);
            Assert.Equal("https://y/feed?key=7", passes.Resolve(pasted).Url);
            Assert.Equal(Pin, passes.Resolve(mine).Url);
        }
        finally
        {
            Remove(dir);
        }
    }

    [Fact]
    public void AFrozenBookOrOneThatCannotBeWrittenTakesNoTwinsPassesAndRaisesNothing()
    {
        var (theirs, kept) = WebPassBook.Empty.Keep(Window, () => "abcdefghijkl", DateTime.UtcNow);

        // Frozen: a file that is not a book this build reads stays as it was.
        var frozenDir = TempDir();
        try
        {
            var path = Path.Combine(frozenDir, WebPassBook.FileName);
            File.WriteAllText(path, "not a book");
            var passes = new WebPasses(frozenDir);
            var changed = 0;
            passes.Changed += () => changed++;
            Assert.NotEqual("", passes.Frozen);
            Assert.False(passes.MergeFromTwin(theirs, theirsStand: true, "MAIN-DESK"));
            Assert.Equal("not a book", File.ReadAllText(path));
            Assert.Equal(0, passes.Book.Count);
            Assert.False(passes.Resolve(kept).Resolved);
            Assert.Equal(0, changed);
        }
        finally
        {
            Remove(frozenDir);
        }

        // Cannot be written: the book's name taken by a folder, so nothing is kept for this run alone.
        var lockedDir = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(lockedDir, WebPassBook.FileName));
            var passes = new WebPasses(lockedDir);
            var changed = 0;
            passes.Changed += () => changed++;
            Assert.False(passes.MergeFromTwin(theirs, theirsStand: false, "Backup desk"));
            Assert.Equal(0, passes.Book.Count);
            Assert.False(passes.Resolve(kept).Resolved);
            Assert.Equal(0, changed);
        }
        finally
        {
            Remove(lockedDir);
        }
    }

    [Fact]
    public void SecuringAWholeShowSaysItOnceAndALoadWritesOneJournalRow()
    {
        var dir = TempDir();
        try
        {
            var passes = new WebPasses(dir);
            var changed = 0;
            var said = new List<string>();
            passes.Changed += () => changed++;
            passes.Noticed += said.Add;

            var show = new ShowState();
            show.Pattern.Media.WebUrl = Window;
            show.Pattern.Layer1.WebUrl = Pin;
            show.Web.SavedUrls.Add(Window);
            var scrub = passes.SecureShow(show);
            Assert.Equal(2, scrub.Kept);
            Assert.Equal(1, changed);                                                            // one change for the whole show
            Assert.Empty(said);
            CarriesNoPass(JsonUtil.Serialize(show), "the secured show");
            Assert.Equal(show.Pattern.Media.WebUrl, show.Web.SavedUrls[0]);

            var journal = new ShowLog(dir);
            var loaded = new ShowState();
            loaded.Pattern.Media.WebUrl = Window;
            loaded.Web.Url = "hall-b.local/stage?view=4321";
            var migrated = passes.MigrateShow(loaded, journal);
            Assert.Equal(2, migrated.Kept);
            var rows = journal.Tail(10);
            var row = Assert.Single(rows);
            Assert.Equal(WebPassMigration.RowKind, row.Kind);
            Assert.StartsWith("kept 2 private page addresses on this machine", row.Message, StringComparison.Ordinal);
            Assert.Single(said);                                                                 // the load says it once, on the status strip
            Assert.Equal(show.Pattern.Media.WebUrl, loaded.Pattern.Media.WebUrl);                // the same address is the same kept entry

            Assert.False(passes.MigrateShow(loaded, journal).Any);                               // a show already held writes no row
            Assert.Single(journal.Tail(10));
        }
        finally
        {
            Remove(dir);
        }
    }

    // ---- a show read at boot, or from a file --------------------------------------------------------------------

    [AvaloniaFact]
    public void ABootThatFindsAPassInTheShowKeepsItWritesTheShowBackAndJournalsOnce()
    {
        var b = TestApp.Boot(prepare: dir => Settings(dir, s =>
        {
            s.Pattern.Kind = PatternKind.Media;
            s.Pattern.Media.Source = MediaSource.Web;
            s.Pattern.Media.WebUrl = Window;
            s.Web.SavedUrls.Add(Pin);
        }));
        try
        {
            var services = b.Services;
            var page = services.State.Pattern.Media.WebUrl;
            Assert.True(WindowAddresses.IsKept(page));
            Assert.True(WindowAddresses.IsKept(services.State.Web.SavedUrls[0]));
            Assert.Equal(Window, services.WebPasses.Resolve(page).Url);
            Assert.Equal(Pin, services.WebPasses.Resolve(services.State.Web.SavedUrls[0]).Url);
            Assert.Equal(1, services.WebPassesKeptRow());

            TestApp.FlushFiles(services);
            CarriesNoPass(File.ReadAllText(services.Store.SettingsPath), "the show file written back");
            Assert.Contains(Token, File.ReadAllText(services.WebPasses.Path), StringComparison.Ordinal);   // the book holds it, beside the settings
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AShowLoadedFromAFileIsHeldBeforeItBecomesTheShow()
    {
        var b = TestApp.Boot();
        var file = Path.Combine(b.Dir, "other-show.json");
        try
        {
            var (services, vm, _) = b;
            var other = SettingsStore.Fresh();
            other.Pattern.Kind = PatternKind.Media;
            other.Pattern.Media.Source = MediaSource.Web;
            other.Pattern.Layer2.WebUrl = Pin;
            other.Pattern.Media.WebUrl = Window;
            File.WriteAllText(file, JsonUtil.Serialize(other));

            TestApp.Pump(vm.LoadShowFromAsync(file));
            Assert.True(WindowAddresses.IsKept(services.State.Pattern.Media.WebUrl));
            Assert.True(WindowAddresses.IsKept(services.State.Pattern.Layer2.WebUrl));
            CarriesNoPass(JsonUtil.Serialize(services.State), "the loaded show");
            Assert.Equal(1, services.WebPassesKeptRow());
        }
        finally
        {
            b.Dispose();
        }
    }

    // ---- the wire, OSC and the engine ---------------------------------------------------------------------------

    [AvaloniaFact]
    public void WebOpenKeepsAPrivateAddressAndSendsThePageItsKeptAddress()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var made = FakePages(services);
            ShowPage(services, "example.com");
            var page = Assert.Single(made);

            var origin = new ActionOrigin(OriginKind.Tcp, "", "127.0.0.1:50000");
            var result = services.Actions.Execute(ShowActionKind.WebOpen, origin, "", Window);
            Assert.True(result.Ok, result.Message);
            Assert.Contains("10.0.0.5 (private address, kept on this machine)", result.Message, StringComparison.Ordinal);
            Assert.Contains("black until Stagewatch says what the window shows", result.Message, StringComparison.Ordinal);   // a window's picture is Stagewatch's word, not this reply's
            CarriesNoPass(result.Message, "WEB OPEN's words");
            var kept = page.Navigations.Last();
            Assert.True(WindowAddresses.IsKept(kept));                                          // the page is sent the held address; it resolves it itself
            Assert.Equal(Window, services.WebPasses.Resolve(kept).Url);
            Assert.Equal(Window, services.WebIn.ResolveOf(kept).Url);
            Assert.Equal("example.com", services.State.Pattern.Media.WebUrl);                  // the pattern keeps its own address
            CarriesNoPass(string.Join("\n", services.Journal.Tail(5).Select(r => $"{r.Target} {r.Message}")), "the journal");

            // A page named by its private address on the wire: the row names it in public.
            services.Actions.Execute(ShowActionKind.WebReload, origin, Pin);
            CarriesNoPass(services.Journal.Tail(1)[0].Target, "the journal's target");

            // The line on the wire, as a controller sends it.
            var router = new CommandRouter(services);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("WEB OPEN " + Pin))));
            Assert.True(WindowAddresses.IsKept(page.Navigations.Last()));

            // A kept address from another machine opens nothing here: refused, the page as it was.
            var before = page.Navigations.Count;
            var refused = services.Actions.Execute(ShowActionKind.WebOpen, origin, "", "kept://10.0.0.9:9924/zzzzzzzzzzzz");
            Assert.Equal(ActionStatus.Refused, refused.Status);
            Assert.Contains(WebPassBook.NotHereWhy, refused.Message, StringComparison.Ordinal);
            Assert.Equal(before, page.Navigations.Count);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheEngineResolvesAHeldPageThroughTheBookAndSendsItBackWhenItsResolutionMoves()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var made = FakePages(services);
            var kept = services.WebPasses.Secure(Window);
            ShowPage(services, kept);

            var page = Assert.Single(made);
            Assert.Equal(kept, page.CurrentUrl);                                                // the source is handed the held address, never the real one
            Assert.Equal("web:" + kept, services.WebIn.MountStatuses.Single().Key);
            Assert.Equal(Window, services.WebIn.ResolveOf(kept).Url);
            var nothing = services.WebIn.ResolveOf("kept://10.0.0.5:9924/zzzzzzzzzzzz");
            Assert.False(nothing.Resolved);
            Assert.Equal(WebPassBook.NotHereWhy, nothing.Why);
            Assert.Equal("https://example.com", services.WebIn.ResolveOf("https://example.com").Url);

            // The book moves under a mounted held page: the page is sent its held address again, once.
            services.WebIn.Reresolve();
            Assert.Empty(page.Navigations);                                                     // its resolution stands: nothing to do
            var real = services.WebIn.Resolve;
            services.WebIn.Resolve = _ => new WebPassResolution(null, WebPassBook.NotHereWhy);
            services.WebIn.Reresolve();
            Assert.Equal(new[] { kept }, page.Navigations);
            services.WebIn.Reresolve();
            Assert.Single(page.Navigations);
            services.WebIn.Resolve = real;
            services.WebIn.Reresolve();
            Assert.Equal(new[] { kept, kept }, page.Navigations);

            // A keep anywhere raises the book's change, and the desk re-reads its held pages on the UI thread.
            services.WebIn.Resolve = _ => new WebPassResolution(null, "held off for the test");
            services.WebPasses.Secure(Pin);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, page.Navigations.Count);
            services.WebIn.Resolve = real;

            // An operator's WEB OPEN stands: a book that moves never takes the page back to its own address.
            var origin = new ActionOrigin(OriginKind.Tcp, "", "127.0.0.1:50000");
            Assert.True(services.Actions.Execute(ShowActionKind.WebOpen, origin, "", "https://example.com/schedule").Ok);
            var opened = page.Navigations.Count;
            services.WebIn.Resolve = _ => new WebPassResolution(null, "held off for the test");
            services.WebIn.Reresolve();
            Assert.Equal(opened, page.Navigations.Count);
            Assert.Equal("https://example.com/schedule", page.CurrentUrl);

            // A WEB OPEN onto another held address is followed there: that address is the one sent again when it moves.
            services.WebIn.Resolve = real;
            var other = services.WebPasses.Secure("https://x/other?pin=4321");
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Actions.Execute(ShowActionKind.WebOpen, origin, "", other).Ok);
            services.WebIn.Reresolve();                                                         // it went there itself, resolving as it went
            Assert.Equal(other, page.Navigations.Last());
            opened = page.Navigations.Count;
            services.WebIn.Resolve = _ => new WebPassResolution(null, "held off for the test");
            services.WebIn.Reresolve();
            Assert.Equal(opened + 1, page.Navigations.Count);
            Assert.Equal(other, page.Navigations.Last());
            services.WebIn.Resolve = real;
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void OscKeepsThePrivateAddressAtTheActionAndItsStatusLineSaysItInPublic()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var made = FakePages(services);
            ShowPage(services, "example.com");
            var page = Assert.Single(made);

            var cfg = services.State.Control;
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) cfg.OscPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            cfg.OscEnabled = true;
            Dispatcher.UIThread.RunJobs();
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            sender.Send(OscCodec.Encode(OscMessage.Of("/patterns/web/open", Window)), new IPEndPoint(IPAddress.Loopback, cfg.OscPort));

            var deadline = Environment.TickCount64 + 8000;
            while (!services.Osc.LastLine.Contains("/patterns/web/open", StringComparison.Ordinal) && Environment.TickCount64 < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
            Assert.Contains("/patterns/web/open", services.Osc.LastLine, StringComparison.Ordinal);
            Assert.Contains("10.0.0.5:9924 (private address)", services.Osc.LastLine, StringComparison.Ordinal);
            CarriesNoPass(services.Osc.LastLine, "OSC's last line");
            CarriesNoPass(services.Osc.StatusLine, "OSC's status line");
            Assert.True(WindowAddresses.IsKept(page.Navigations.Last()));
        }
        finally
        {
            b.Dispose();
        }
    }

    [Fact]
    public void TheNativePlayerAsksItsToolForTheRealAddressAndKeepsTheAnswerUnderTheHeldOne()
    {
        var tool = Path.Combine(Path.GetTempPath(), "patterns-yt-dlp-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(tool, "stand-in");
        const string Kept = "kept://10.0.0.5:9924/abcdefghijkl";
        var service = new WebVideoService
        {
            ConfiguredPath = () => tool,
            PathEnvironment = () => "",
            Resolve = a => a == Kept ? new WebPassResolution(Window, "") : new WebPassResolution(null, WebPassBook.NotHereWhy),
        };
        try
        {
            var asked = new List<string[]>();
            service.Runner = (_, args, _) =>
            {
                asked.Add(args);
                return Task.FromResult((1, "", "ERROR: [generic] Unable to download webpage: " + Window + " (caused by a refusal)"));
            };
            MediaLocator.WantedInput Want(string target) => new("web:" + target, MediaLocator.WantedKind.Web, target, false, false, 0)
                { Service = PageService.YouTube, PlayVia = WebPlayVia.NativePlayer };

            Assert.Equal(MediaLocator.WantedKind.Web, service.Rewrite(Want(Kept)).Kind);       // still the browser's while the tool is asked
            var deadline = Environment.TickCount64 + 5000;
            while (service.EntryFor(Kept) is { Phase: WebVideoService.Phase.Resolving } && Environment.TickCount64 < deadline) Thread.Sleep(5);

            Assert.Equal(Window, Assert.Single(asked)[^1]);                                     // the tool alone is handed the real address
            var entry = service.EntryFor(Kept)!;
            Assert.Equal(WebVideoService.Phase.Failed, entry.Phase);
            CarriesNoPass(entry.Words, "the native player's words");                             // the tool's own error echoed the address
            Assert.Null(service.EntryFor(Window));
            Assert.All(service.Rows(), r => CarriesNoPass(r.Url + " " + r.Words, "STATE's native rows"));

            // A held page this machine cannot open is never asked about.
            service.Rewrite(Want("kept://10.0.0.9:9924/zzzzzzzzzzzz"));
            Assert.Single(asked);
            var other = service.EntryFor("kept://10.0.0.9:9924/zzzzzzzzzzzz")!;
            Assert.Equal(WebVideoService.Phase.Failed, other.Phase);
            Assert.Contains("private address kept on another machine", other.Words, StringComparison.Ordinal);
        }
        finally
        {
            service.Dispose();
            File.Delete(tool);
        }
    }

    // ---- the desk's own entries ---------------------------------------------------------------------------------

    [AvaloniaFact]
    public void TheDesksBoxesButtonsCuesLibraryAndLinesHoldTheKeptAddress()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var culture = CultureInfo.InvariantCulture;

            // The address boxes (the pattern's page, a layer's, the Remote & web page's) and a device's line, as they are left.
            var box = new KeptAddressConverter();
            var kept = (string)box.ConvertBack(Window, typeof(string), null, culture)!;
            Assert.True(WindowAddresses.IsKept(kept));
            Assert.Equal(Window, services.WebPasses.Resolve(kept).Url);
            Assert.Equal("https://example.com", box.ConvertBack("https://example.com", typeof(string), null, culture));
            Assert.Equal($"WEB OPEN {kept} ON lobby", new KeptLineConverter().ConvertBack("WEB OPEN " + Window + " ON lobby", typeof(string), null, culture));

            // SHOW THIS PAGE ON THE PATTERN with an address the box did not secure: kept, on the pattern and in the saved pages.
            vm.State.Web.Url = Pin;
            vm.Media.PutWebPageOnPatternCommand.Execute(null);
            var pinKept = vm.ActivePattern.Media.WebUrl;
            Assert.True(WindowAddresses.IsKept(pinKept));
            Assert.Equal(pinKept, vm.State.Web.Url);
            Assert.Contains(pinKept, vm.State.Web.SavedUrls);
            CarriesNoPass(vm.StatusMessage, "the status strip");
            vm.Media.SavedWebPick = Window;                                                       // a pick from the saved pages
            Assert.Equal(kept, vm.ActivePattern.Media.WebUrl);

            // A cue step's address box.
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "01.010", Name = "Lobby" };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.WebOpen });
            stack.Cues.Add(cue);
            vm.Cues.SelectedStack = stack;
            vm.Cues.SelectedCue = cue;
            Dispatcher.UIThread.RunJobs();
            var row = Assert.Single(vm.Cues.ActionRows);
            Assert.True(row.HasAddressValue);
            Assert.False(row.HasTextValue);
            row.AddressValue = Window;
            Assert.Equal(kept, cue.Actions[0].Value);
            Assert.Equal(kept, row.AddressValue);

            // A cue sheet's page step.
            vm.Cues.ImportRows(CsvTable.Parse("Name,Action,Target,Value\nDoors,open,," + Pin + "\n"), append: true);
            var imported = stack.Cues.Last();
            Assert.Equal(pinKept, imported.Actions.Single(a => a.Kind == ShowActionKind.WebOpen).Value);

            // The Library: a saved page that slipped in raw, and a preset saved before the round.
            vm.State.Web.SavedUrls.Add(Window);
            var preset = new PatternConfig { Kind = PatternKind.Media };
            preset.Media.Source = MediaSource.Web;
            preset.Media.WebUrl = Pin;
            preset.Layer1.WebUrl = Window;
            services.Store.SavePreset("Private page", preset);
            var tiles = LibraryItems.Build(vm.State, services.Store, swatches: false, passes: services.WebPasses);
            var target = new PatternConfig();
            tiles.First(t => t.Id == "web:" + Window).Picture!(target);
            Assert.Equal(kept, target.Media.WebUrl);
            Assert.Equal("10.0.0.5 (private address)", LibraryItems.WebTitle(Window));
            Assert.True(WindowAddresses.TryKept(kept, out _, out _, out var keptId));
            Assert.Equal($"10.0.0.5 · {keptId} (private address)", LibraryItems.WebTitle(kept));   // two windows of one Stagewatch, two names
            var fromPreset = new PatternConfig();
            tiles.First(t => t.Name == "Private page").Picture!(fromPreset);
            Assert.Equal(pinKept, fromPreset.Media.WebUrl);
            Assert.Equal(kept, fromPreset.Layer1.WebUrl);
            var unsecured = new PatternConfig();
            LibraryItems.ApplyWeb(unsecured, Window);                                            // without the book: the fence's own form
            Assert.Equal("unkept://10.0.0.5:9924", unsecured.Media.WebUrl);

            // A layer's words in the menus.
            var layered = new PatternConfig();
            layered.Layer1.Source = LayerSource.Web;
            layered.Layer1.WebUrl = Pin;
            Assert.Equal("https://x", DeskMenuFacts.Layer(layered, 1).Words);
        }
        finally
        {
            b.Dispose();
        }
    }

    // ---- the review's entries: presets by name, the recovery record, MIDI learn, a node, a book that would not read --

    private static void Rig(TestApp.Booted b)
    {
        var screens = new List<ScreenInfo>
        {
            new("a", "Left", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
            new("b", "Right", new Avalonia.PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        };
        b.Services.Screens.All.Clear();
        foreach (var screen in screens) b.Services.Screens.All.Add(screen);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(screens);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles(screens);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void APresetByNameFromTheWireACueOrTheDeckIsHeldLikeTheLibrarysTile()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();
            var preset = new PatternConfig { Kind = PatternKind.Media };                          // a preset saved before the round: the pass in its file
            preset.Media.Source = MediaSource.Web;
            preset.Media.WebUrl = Pin;
            preset.Layer1.WebUrl = Window;
            services.Store.SavePreset("Private page", preset);
            var router = new CommandRouter(services);
            string Send(string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

            Assert.StartsWith("OK", Send("PRESET Private page"), StringComparison.Ordinal);
            Dispatcher.UIThread.RunJobs();
            var pinKept = services.State.Pattern.Media.WebUrl;
            var kept = services.State.Pattern.Layer1.WebUrl;
            Assert.True(WindowAddresses.IsKept(pinKept));
            Assert.True(WindowAddresses.IsKept(kept));
            Assert.Equal(Pin, services.WebPasses.Resolve(pinKept).Url);
            Assert.Equal(Window, services.WebPasses.Resolve(kept).Url);

            // Onto one screen, live: the assignment holds the kept addresses, on the edited state and the air alike.
            Assert.StartsWith("OK", Send("SCREEN 1 PRESET Private page"), StringComparison.Ordinal);
            Dispatcher.UIThread.RunJobs();
            var own = services.State.Independent.Single(x => ContentTargets.UsesOwnPattern(services.State, x.ScreenId));
            Assert.Equal(pinKept, own.Pattern.Media.WebUrl);
            Assert.Equal(kept, own.Pattern.Layer1.WebUrl);

            // Staged on a screen's PVW: EDIT SAFE opens and the preview holds the kept addresses too.
            Assert.StartsWith("OK", Send("SCREEN 2 PVW PRESET Private page"), StringComparison.Ordinal);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Sandbox.Active);
            Assert.Equal(2, services.State.Independent.Count(x => x.Pattern.Media.WebUrl == pinKept && x.Pattern.Layer1.WebUrl == kept));

            CarriesNoPass(JsonUtil.Serialize(services.State), "the show after a preset by name");
            CarriesNoPass(JsonUtil.Serialize(services.AirState), "the air after a preset by name");
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ARecoveryRecordsAirIsHeldBeforeItGoesBackOnAir()
    {
        var b = TestApp.Boot(prepare: dir =>
        {
            var air = new ShowState();                                                            // the air an older build wrote, its page raw
            air.Pattern.Kind = PatternKind.Media;
            air.Pattern.Media.Source = MediaSource.Web;
            air.Pattern.Media.WebUrl = Window;
            new RecoveryStore(dir).Write(new RecoverySnapshot(true, false, DateTime.UtcNow, Air: air, Sandboxed: true));
        });
        try
        {
            var (services, vm, _) = b;
            services.TryRecover(vm);
            Dispatcher.UIThread.RunJobs();

            Assert.True(services.Sandbox.Active);
            var onAir = services.Sandbox.ProgramState!.Pattern.Media.WebUrl;
            Assert.True(WindowAddresses.IsKept(onAir));
            Assert.Equal(Window, services.WebPasses.Resolve(onAir).Url);
            Assert.Equal(onAir, services.Bus.Current.State.Pattern.Media.WebUrl);
            CarriesNoPass(JsonUtil.Serialize(services.AirState), "the air put back");

            // The next record carries the held air, never the pass.
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            TestApp.FlushFiles(services);
            Assert.Equal(onAir, services.Recovery.Read()?.Air?.Pattern.Media.WebUrl);
            CarriesNoPass(File.ReadAllText(Path.Combine(b.Dir, "patterns.recovery.json")), "the recovery record");
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void MidiLearnBindsAPrivateLineInItsHeldFormAndForgetFindsItByTheRealOne()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var fake = new FakeDeviceLink();
            services.Devices.LinkFactory = _ => fake;
            vm.AddMidiDeviceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var surface = vm.State.Interactive.Devices.Single();
            surface.Name = "APC40";
            surface.Triggers.Clear();
            vm.State.Interactive.Enabled = true;
            services.Devices.Reconcile();
            Dispatcher.UIThread.RunJobs();
            var router = new CommandRouter(services);
            string Send(string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

            var armed = Send("MIDI LEARN WEB OPEN " + Window + " ON lobby");
            Assert.StartsWith("OK", armed, StringComparison.Ordinal);
            CarriesNoPass(armed, "MIDI LEARN's reply");
            var kept = services.WebPasses.Secure(Window);
            Assert.Equal($"WEB OPEN {kept} ON lobby", services.MidiLearn.Wire);
            CarriesNoPass(services.MidiLearn.Words, "the learn banner");

            fake.Say("NOTE 1 53 127");
            Dispatcher.UIThread.RunJobs();
            var row = Assert.Single(surface.Triggers);
            Assert.Equal($"WEB OPEN {kept} ON lobby", row.Command);
            Assert.Equal($"⌁ NOTE 1 53 on APC40 → WEB OPEN {kept} ON lobby", services.MidiLearn.LastLearned);   // the log line's words
            CarriesNoPass(JsonUtil.Serialize(services.State), "the show after a MIDI learn");

            // Forgetting by the line as typed, with the real address: the held form finds the row, keeping nothing new.
            var count = services.WebPasses.Book.Count;
            var forgot = Send("MIDI FORGET WEB OPEN " + Window + " ON lobby");
            Assert.StartsWith("OK", forgot, StringComparison.Ordinal);
            Assert.Empty(surface.Triggers);
            var row2 = services.Journal.Tail(1)[0];
            Assert.Contains($"bound to WEB OPEN {kept} ON lobby forgotten", row2.Message, StringComparison.Ordinal);   // the words name the held form
            CarriesNoPass(row2.Target + " " + row2.Message, "MIDI FORGET's journal row");
            Assert.Equal("WEB OPEN unkept://10.0.0.9:9924", services.WebPasses.HeldText("WEB OPEN http://10.0.0.9:9924/w/" + Token));   // one the book never kept
            Assert.Equal(count, services.WebPasses.Book.Count);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ANodeWritesItsShowBackOnceItsBootKeptAPass()
    {
        var dir = TempDir();
        NodeHost? host = null;
        try
        {
            var show = SettingsStore.Fresh();
            show.Control.Enabled = false;
            show.Web.SavedUrls.Add(Window);
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(show));

            host = NodeHost.Build(NodeKind.Timer, new SettingsStore(dir));
            var held = host.State.Web.SavedUrls.Single();
            Assert.True(WindowAddresses.IsKept(held));
            Assert.Equal(Window, host.Kernel.WebPasses.Resolve(held).Url);
            CarriesNoPass(File.ReadAllText(Path.Combine(dir, "patterns.settings.json")), "the node's show file");
            Assert.Contains(held, File.ReadAllText(Path.Combine(dir, "patterns.settings.json")), StringComparison.Ordinal);
        }
        finally
        {
            host?.Dispose();
            Remove(dir);
        }
    }

    [Fact]
    public void ABookThatCouldNotBeReadIsReadAgainAndItsFreezeClearsOnceItReads()
    {
        var dir = TempDir();
        try
        {
            var kept = new WebPasses(dir).Secure(Window);                                         // a book on the disk, kept by an earlier run
            var now = new DateTime(2026, 10, 25, 20, 0, 0, DateTimeKind.Utc);
            WebPasses passes;
            using (new FileStream(Path.Combine(dir, WebPassBook.FileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                passes = new WebPasses(dir, clock: () => now);                                    // a scan holds the file at boot
                Assert.Contains("could not be read", passes.Frozen, StringComparison.Ordinal);
                Assert.Contains("tries it again", passes.Frozen, StringComparison.Ordinal);
                Assert.False(passes.Resolve(kept).Resolved);
                now += WebPasses.RereadEvery;
                Assert.False(passes.Resolve(kept).Resolved);                                      // still held: still frozen
                Assert.Equal("unkept://x", passes.Secure(Pin));                                   // nothing kept meanwhile, nothing written over
            }
            var changed = 0;
            passes.Changed += () => changed++;
            Assert.False(passes.Resolve(kept).Resolved);                                          // read again at most once a second
            now += WebPasses.RereadEvery;
            Assert.Equal(Window, passes.Resolve(kept).Url);                                       // the file reads: the freeze clears
            Assert.Equal("", passes.Frozen);
            Assert.Equal(1, changed);                                                             // and the held pages are told
            Assert.True(WindowAddresses.IsKept(passes.Secure(Pin)));
            Assert.Equal(2, new WebPasses(dir).Book.Count);                                       // the earlier entry kept, the new one beside it
        }
        finally
        {
            Remove(dir);
        }
    }
}

/// <summary>The journal rows a boot or a load wrote for its kept addresses.</summary>
internal static class WebPassesTestFacts
{
    public static int WebPassesKeptRow(this AppServices services)
        => services.Journal.Tail(200).Count(r => r.Kind == WebPassMigration.RowKind);
}
