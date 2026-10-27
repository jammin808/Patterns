using System.IO.Compression;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 89, the Core's half of the fence: a show that still holds a page's pass raw (an older file, a hand edit, an
/// older main's twin) publishes none of it. The pass is fenced at the key — what the engines mount, what the Eye and the
/// context are drawn from, what a cue's page list and a pre-roll name — and the support bundle says every private
/// address in public, masks the kept ones and never carries the book. Round 89.4 adds the sinks every writer shares —
/// the journal said in public as it is written and as it is read — the places the Super Check names, its row, and the
/// twin's KEPT word on one line. The App's half (STATE, the log, the twin's wire, the Super Check on a desk) is
/// <c>Patterns.App.Tests.PrivateAddressFenceTests</c>.
/// </summary>
public class PrivateAddressFenceTests
{
    private const string Window = WindowAddressesTests.Window;
    private const string Token = WindowAddressesTests.Token;
    private const string Pin = "https://x/feed?a=1&pin=4321";

    private static readonly string[] Passes = { Token, "4321", "k2m4" };

    private static void CarriesNoPass(string text, string where)
    {
        foreach (var pass in Passes) Assert.False(text.Contains(pass, StringComparison.OrdinalIgnoreCase), $"{where} carries '{pass}':\n{text}");
    }

    private static ShowState RawShow()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Web;
        state.Pattern.Media.WebUrl = Window;
        state.Pattern.Media.WebAutoPlay = true;
        state.Pattern.Layer1.Enabled = true;
        state.Pattern.Layer1.Source = LayerSource.Web;
        state.Pattern.Layer1.WebUrl = Pin;
        state.Pattern.Layer1.WebAutoPlay = true;
        var screen = new OutputAssignment { ScreenId = "s2" };
        screen.Pattern.Kind = PatternKind.Media;
        screen.Pattern.Media.Source = MediaSource.Web;
        screen.Pattern.Media.WebUrl = "hall-b.local/stage?view=k2m4";
        state.Independent.Add(screen);
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "s2", Enabled = true, UseCustomPattern = true });
        return state;
    }

    [Fact]
    public void TheKeyNeverCarriesThePass()
    {
        Assert.Equal("web:unkept://10.0.0.5:9924", InputKeys.Web(Window));
        Assert.Equal("web:unkept://x", InputKeys.Web(Pin));
        Assert.Equal("web:kept://10.0.0.5:9924/abcdefghijkl", InputKeys.Web("kept://10.0.0.5:9924/abcdefghijkl"));
        Assert.Equal("web:https://example.com", InputKeys.Web("example.com"));

        // A held prefix is no pass through the fence: every held form is rebuilt from what it names.
        Assert.Equal("web:unkept://10.0.0.5:9924", InputKeys.Web("kept://10.0.0.5:9924/w/" + Token));
        Assert.Equal("web:unkept://x", InputKeys.Web("unkept://x/feed?pin=4321"));
        Assert.Equal("web:unkept://x", InputKeys.Web("unkept://user:4321@x"));
        Assert.Equal("web:unkept://10.0.0.5:9924", InputKeys.Web("kept://admin:4321@10.0.0.5:9924/abcdefghijkl"));
        Assert.Equal("web:kept://x/abcdefghijkl", InputKeys.Web("KEPT://X/abcdefghijkl"));
        Assert.Equal("unkept://x", WindowAddresses.Held("unkept://x/feed?pin=4321"));
        Assert.True(InputKeys.IsHeldWeb(InputKeys.Web("kept://10.0.0.5:9924/w/" + Token)));

        var state = RawShow();
        var wanted = MediaLocator.FindWantedInputs(new ShowSnapshot { State = state, Version = 1 })
            .Where(w => w.Kind == MediaLocator.WantedKind.Web).ToList();
        Assert.Equal(new[] { "web:unkept://10.0.0.5:9924", "web:unkept://x", "web:unkept://hall-b.local" }, wanted.Select(w => w.Key));
        foreach (var w in wanted)
        {
            Assert.Equal(w.Key, InputKeys.Web(w.Target));                                         // the target is what the key says: nothing to open
            CarriesNoPass(w.Target, w.Key);
        }

        var pages = WebPresets.PagesIn(state);
        Assert.Equal(3, pages.Count);
        Assert.All(pages, p => Assert.Equal(p.Key, InputKeys.Web(p.Url)));
        foreach (var (key, url) in pages) CarriesNoPass(key + " " + url, "PagesIn");
        Assert.True(WebPresets.Matches(pages[0].Key, pages[0].Url, "", Window));                     // the address typed raw still names its page
        Assert.False(WebPresets.Matches(pages[0].Key, pages[0].Url, "", "https://example.com/w"));

        var preRoll = WebVt.AutoPlayPagesIn(LookService.Capture(state));
        Assert.Equal(2, preRoll.Count);
        foreach (var want in preRoll) CarriesNoPass(want.Key + " " + want.Url, "a pre-roll");
    }

    [Fact]
    public void TheEyeAndTheContextDrawnFromTheKeysCarryNoPass()
    {
        var state = RawShow();
        var keys = MediaLocator.FindWantedInputs(new ShowSnapshot { State = state, Version = 1 }).Select(w => w.Key).ToList();
        // The sources as the desk names them: a nickname, or the key's address.
        var sources = keys.Select(k => new EyeSource(k, state.InputLabel(k, k[(k.IndexOf(':', StringComparison.Ordinal) + 1)..]), "web page", false, "", CheckLight.Grey, "Media")).ToList();
        var graph = EyeGraph.Build(new EyeFacts
        {
            MachineName = "FOH",
            Displays = new[] { new EyeDisplay("d1", "D1", 1920, 1080, 60, true, false, false) },
            Screens = new[] { new EyeScreen { Id = "s1", Number = "1", Label = "Wall", DisplayId = "d1", OnAir = true, Sources = keys } },
            Sources = sources,
        });
        var eye = EyeJson.Write(graph, EyeLayout.Place(graph), null, EyeLens.Video);
        CarriesNoPass(eye, "the Eye");
        Assert.Contains("source:web:unkept://10.0.0.5:9924", eye, StringComparison.Ordinal);

        // A journal row written before the round still names the pass: the context says it in public.
        var old = new ShowLogEntry(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), "wire", "WebOpen", "lobby", "Done", $"lobby → {Pin}");
        var context = ShowContextBuilder.FromEye(graph, "a1b2c3d4", "FOH", new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc), new[] { old }, Array.Empty<ShowContextPeer>()).ToJson();
        CarriesNoPass(context, "the context");
        Assert.Contains("https://x (private address)", context, StringComparison.Ordinal);
    }

    /// <summary>An address the book keeps that the rule does not read as private — kept by hand, or by a later rule — so only the book can mask it.</summary>
    private const string KeptByHand = "https://10.0.0.5:9924/stage/room-7";

    private static string Temp([System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerMemberName] string member = "")
        => TempRun.Folder("patterns-fence-", file, member).Path;

    private static string Entry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheSupportBundleSaysEveryPrivateAddressInPublicAndNeverCarriesTheBook()
    {
        Assert.True(SupportBundle.NeverBundled.ContainsKey(WebPassBook.FileName));
        Assert.DoesNotContain(WebPassBook.FileName, SupportBundle.Files);
        Assert.False(WindowAddresses.LooksPrivate(KeptByHand));
        Assert.Equal(KeptByHand, WindowAddresses.RedactText(KeptByHand));                          // the rule leaves it: only the book masks it

        var dir = Temp();
        try
        {
            var state = RawShow();
            state.LooksAndCues.Looks.Add(new LookConfig { Name = "Raw", Json = LookService.Capture(state) });   // a look's JSON, escaped again inside the settings
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(state));
            File.WriteAllText(Path.Combine(dir, "patterns.recovery.json"), JsonUtil.Serialize(state));
            File.WriteAllText(Path.Combine(dir, "patterns.log"), $"10:00:00.000 [WARN] Web page failed: {Window}: net::ERR_CONNECTION_REFUSED\n10:00:01.000 [INFO] OSC /patterns/web/open → WEB OPEN {Pin} → OK\n"
                                                                  + $"10:00:02.000 [INFO] Web page loaded: {KeptByHand}\n");
            new ShowLog(dir).Record("wire", "WebOpen", "lobby", "Done", $"lobby → {Pin} and {Window} and {KeptByHand}");
            var (book, _) = WebPassBook.Empty.Keep(Pin, () => "abcdefghijkl", DateTime.UtcNow);
            (book, _) = book.Keep(KeptByHand, () => "abcdefghijkm", DateTime.UtcNow);
            File.WriteAllText(Path.Combine(dir, WebPassBook.FileName), book.ToJson());

            var zipPath = Path.Combine(dir, "bundle.zip");
            var entries = SupportBundle.Build(dir, zipPath, $"Site: Lobby — last page {Window}");
            Assert.DoesNotContain(WebPassBook.FileName, entries);
            using var zip = ZipFile.OpenRead(zipPath);
            Assert.Null(zip.GetEntry(WebPassBook.FileName));
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                CarriesNoPass(text, entry.FullName);
                Assert.False(text.Contains("room-7", StringComparison.Ordinal), $"{entry.FullName} carries the address the book keeps:\n{text}");
            }
            Assert.Equal("10:00:00.000 [WARN] Web page failed: http://10.0.0.5:9924 (private address): net::ERR_CONNECTION_REFUSED\n"
                         + "10:00:01.000 [INFO] OSC /patterns/web/open → WEB OPEN https://x (private address) → OK\n"
                         + $"10:00:02.000 [INFO] Web page loaded: {Secrets.Mask}\n", Entry(zip, "patterns.log"));
            Assert.DoesNotContain(WebPassBook.FileName, Entry(zip, "bundle-info.txt"), StringComparison.Ordinal);   // a book read whole says nothing
        }
        finally
        {
            TempRun.Remove(dir);
        }
    }

    [Fact]
    public void ABookTheBundleCannotReadIsNotedAndThePrivateAddressesAreStillSaidInPublic()
    {
        var dir = Temp();
        try
        {
            File.WriteAllText(Path.Combine(dir, "patterns.log"), $"10:00:00.000 [INFO] Web page loaded: {Window}\n");

            // A book of another version: not read, and said so.
            var bookPath = Path.Combine(dir, WebPassBook.FileName);
            File.WriteAllText(bookPath, "{\"v\":2,\"passes\":[]}");
            var zipPath = Path.Combine(dir, "bundle.zip");
            Assert.Contains("patterns.log", SupportBundle.Build(dir, zipPath, "Site: Lobby"));
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                Assert.Contains($"{WebPassBook.FileName}: not read — its kept addresses are masked only where they read as private.", Entry(zip, "bundle-info.txt"), StringComparison.Ordinal);
                Assert.Equal("10:00:00.000 [INFO] Web page loaded: http://10.0.0.5:9924 (private address)\n", Entry(zip, "patterns.log"));
            }

            // A book another holds shut: could not be read, and said so; the log still goes, said in public.
            File.WriteAllText(bookPath, WebPassBook.Empty.ToJson());
            using (new FileStream(bookPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Contains("patterns.log", SupportBundle.Build(dir, zipPath, "Site: Lobby"));
            }
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                Assert.Contains($"{WebPassBook.FileName}: could not be read — ", Entry(zip, "bundle-info.txt"), StringComparison.Ordinal);
                Assert.Equal("10:00:00.000 [INFO] Web page loaded: http://10.0.0.5:9924 (private address)\n", Entry(zip, "patterns.log"));
            }
        }
        finally
        {
            TempRun.Remove(dir);
        }
    }

    [Fact]
    public void AMultiMegabyteLogGoesIntoTheBundleSaidInPublic()
    {
        // A desk left running for days: a log far past the size one pattern's scan could read in its time. It is read a
        // line at a time, so it goes into the bundle whole, every private address said in public.
        var dir = Temp();
        try
        {
            var logPath = Path.Combine(dir, "patterns.log");
            var lines = 0;
            using (var writer = new StreamWriter(logPath, append: false, new System.Text.UTF8Encoding(false)))
            {
                for (var i = 0; i < 320_000; i++, lines++)
                {
                    writer.Write(i % 1000 == 0
                        ? $"10:00:00.000 [INFO] OSC /patterns/web/open → WEB OPEN {Pin} → OK and {Window} and 10.0.0.5:9924/w/{Token}\n"
                        : "10:00:00.000 [INFO] frame drew in 4.2 ms on output 1 — https://example.com/schedule?day=2&lang=en\n");
                }
            }
            Assert.True(new FileInfo(logPath).Length > 24L * 1024 * 1024);

            var zipPath = Path.Combine(dir, "bundle.zip");
            var entries = SupportBundle.Build(dir, zipPath, "Site: Lobby");

            Assert.Contains("patterns.log", entries);
            using var zip = ZipFile.OpenRead(zipPath);
            var log = Entry(zip, "patterns.log");
            CarriesNoPass(log, "patterns.log");
            Assert.Equal(lines, log.Count(c => c == '\n'));
            Assert.DoesNotContain(WindowAddresses.WithheldLine, log, StringComparison.Ordinal);
            Assert.DoesNotContain("patterns.log: could not be read", Entry(zip, "bundle-info.txt"), StringComparison.Ordinal);
        }
        finally
        {
            TempRun.Remove(dir);
        }
    }

    [Fact]
    public void TheTwinHasAWordForTheBookOnOneLine()
    {
        var (book, kept) = WebPassBook.Empty.Keep(Window, () => "abcdefghijkl", new DateTime(2026, 10, 25, 9, 12, 0, DateTimeKind.Utc));
        (book, _) = book.Keep(Pin, () => "abcdefghijkm", new DateTime(2026, 10, 25, 9, 13, 0, DateTimeKind.Utc));
        var json = book.ToJson(indented: false);
        Assert.DoesNotContain('\n', json);
        Assert.Contains('\n', book.ToJson());                                                     // the file stays readable by hand
        var line = TwinMessage.Format(TwinWord.Kept, json);
        Assert.StartsWith("KEPT {\"v\":1,", line, StringComparison.Ordinal);
        var back = TwinMessage.Parse(line);
        Assert.Equal(TwinWord.Kept, back.Word);
        Assert.True(WebPassBook.TryParse(back.Payload, out var read));
        Assert.True(read.SameAs(book));
        Assert.Equal(Window, read.Resolve(kept).Url);
        Assert.False(read.SameAs(WebPassBook.Empty));
        Assert.False(read.SameAs(null));
    }

    [Fact]
    public void TheJournalSaysEveryRowInPublicAsItIsWrittenAndAsItIsRead()
    {
        var dir = Temp();
        try
        {
            var journal = new ShowLog(dir);
            // A writer that forgot: the row goes to the file said in public all the same.
            journal.Record("wire", "WebOpen", Window, "Done", $"lobby → {Pin} (and 10.0.0.5:9924/w/{Token} typed bare)");
            var file = File.ReadAllText(journal.Path);
            CarriesNoPass(file, "the journal file");
            Assert.Contains("http://10.0.0.5:9924 (private address)", file, StringComparison.Ordinal);
            Assert.Contains("https://x (private address)", file, StringComparison.Ordinal);

            // A row an older build wrote raw: every reader of the journal gets it said in public.
            var old = new ShowLogEntry(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), "wire", "WebOpen", Window, "Done", $"lobby → {Pin}");
            File.AppendAllText(journal.Path, JsonUtil.SerializeCompact(old) + Environment.NewLine);
            Assert.Contains(Token, File.ReadAllText(journal.Path), StringComparison.Ordinal);       // the file keeps what the older build wrote (the papers say so)
            var rows = journal.Tail(10);
            Assert.Equal(2, rows.Count);
            foreach (var r in rows) CarriesNoPass($"{r.Origin} {r.Kind} {r.Target} {r.Outcome} {r.Message}", "a row read back");
            Assert.Equal("http://10.0.0.5:9924 (private address)", rows[1].Target);

            // A row with nothing private is the very row: no copy, byte for byte.
            var plain = new ShowLogEntry(DateTime.UtcNow, "desk", "ApplyLook", "Walk-in", "Done", "https://example.com/schedule?day=2");
            Assert.Same(plain, ShowLog.InPublic(plain));
        }
        finally
        {
            TempRun.Remove(dir);
        }
    }

    [Fact]
    public void ThePlacesAShowNamesAPrivatePageAreFoundWithTheAddressHeldNeverRaw()
    {
        var state = new ShowState();
        state.Pattern.Media.WebUrl = "kept://10.0.0.5:9924/abcdefghijkl";
        state.Pattern.Layer1.WebUrl = Window;                                                        // raw: a hand edit after the load's scrub
        state.Pattern.Layer2.WebUrl = "https://example.com/schedule";                                // public: not a place
        var screen = new OutputAssignment { ScreenId = "s2" };
        screen.Pattern.Media.WebUrl = "unkept://hall-b.local";
        state.Independent.Add(screen);
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "s2", Enabled = true, UseCustomPattern = true, CustomLabel = "Wall" });
        state.Web.SavedUrls.Add("kept://x/abcdefghijkm");
        var stack = new CueStackConfig { Name = "Main" };
        var cue = new RunCueConfig { Number = "3", Name = "Doors" };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.WebOpen, Target = "lobby", Value = "kept://10.0.0.5:9924/abcdefghijkl" });
        stack.Cues.Add(cue);
        state.Stacks.Add(stack);
        state.Interactive.Devices.Add(new DeviceConfig { Id = "d1", Name = "Button box", Triggers = { new DeviceTriggerConfig { Command = $"WEB OPEN 10.0.0.5:9924/w/{Token} ON lobby" } } });
        state.LooksAndCues.Looks.Add(new LookConfig { Name = "Lobby", Json = "{\"Pattern\":{\"Media\":{\"WebUrl\":\"kept://x/abcdefghijkm\"}}}" });

        var places = WebPassMigration.Places(state);
        foreach (var p in places) CarriesNoPass(p.Where + " " + p.Address, "a place");
        Assert.Equal(new[]
        {
            ("the pattern's page", "kept://10.0.0.5:9924/abcdefghijkl", false),
            ("the pattern's layer 1", "unkept://10.0.0.5:9924", true),
            ("screen 'Wall''s page", "unkept://hall-b.local", false),
            ("the saved pages", "kept://x/abcdefghijkm", false),
            ("cue 3 'Doors' in Main", "kept://10.0.0.5:9924/abcdefghijkl", false),
            ("device 'Button box'", "unkept://10.0.0.5:9924", true),
            ("look 'Lobby'", "kept://x/abcdefghijkm", false),
        }, places.Select(p => (p.Where, p.Address, p.Raw)));

        Assert.Empty(WebPassMigration.Places(new ShowState()));
    }

    private static PrivatePageFact Fact(string where, string address, bool opens, string why = "", bool raw = false) => new(where, address, raw, opens, why);

    [Fact]
    public void TheSuperCheckNamesWhereAPrivatePageOpensNothingAndNeverItsPass()
    {
        static CheckRow? Row(CheckFacts f) => SuperCheck.Run(f).Rows.SingleOrDefault(r => r.Item == "Private pages");

        Assert.Null(Row(new CheckFacts()));                                                          // no private page, no row

        var green = Row(new CheckFacts { PrivatePages = new[] { Fact("the pattern's page", "kept://10.0.0.5:9924/abcdefghijkl", true), Fact("cue 3", "kept://10.0.0.5:9924/abcdefghijkl", true) } })!;
        Assert.Equal(CheckLight.Green, green.Light);
        Assert.Equal("SHOW", green.Section);
        Assert.Equal("1 kept on this machine", green.Value);

        var facts = new CheckFacts
        {
            PrivatePages = new[]
            {
                Fact("the pattern's page", "kept://10.0.0.5:9924/abcdefghijkl", true),
                Fact("the pattern's layer 1", "unkept://10.0.0.5:9924", false, WebPassBook.UnkeptWhy, raw: true),
                Fact("screen 'Wall''s page", "kept://10.0.0.9:9924/abcdefghijkm", false, WebPassBook.NotHereWhy),
            },
        };
        var amber = Row(facts)!;
        Assert.Equal(CheckLight.Amber, amber.Light);
        Assert.Equal("2 of 3 open nothing here · written raw", amber.Value);
        Assert.StartsWith("FIX: the pattern's layer 1 holds a private address written raw into the show (unkept://10.0.0.5:9924)", amber.Note, StringComparison.Ordinal);
        Assert.Contains("screen 'Wall''s page: kept://10.0.0.9:9924/abcdefghijkm — " + WebPassBook.NotHereWhy, amber.Note, StringComparison.Ordinal);
        CarriesNoPass(SuperCheck.ToText(SuperCheck.Run(facts)), "the Super Check's text");

        // More than the row names: the rest are counted. A book that keeps nothing is said even with nothing named.
        var many = Enumerable.Range(1, SuperCheck.PrivatePagesNamed + 2).Select(i => Fact($"cue {i}", "unkept://x", false, WebPassBook.UnkeptWhy)).ToArray();
        Assert.EndsWith("; and 2 more", Row(new CheckFacts { PrivatePages = many })!.Note, StringComparison.Ordinal);
        var frozen = Row(new CheckFacts { PassBookProblem = "patterns.webpasses.json beside the settings is not a book this build reads" })!;
        Assert.Equal(CheckLight.Amber, frozen.Light);
        Assert.Equal("nothing can be kept on this machine", frozen.Value);
    }
}
