using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 66: the God's Eye in the core — the graph built from facts alone with stable ids and
/// honest lights, the headline and the problems queue, the lenses and the de-emphasis, a layout
/// that is deterministic and overlap-free, a camera whose maths hold, the JSON, the menu.
/// </summary>
public class EyeTests
{
    private static EyeFacts Rig() => new()
    {
        MachineName = "SHOW-PC",
        Build = "1.0.0",
        OutputsLive = true,
        Health = CheckLight.Amber,
        HealthWords = "OK · 2 amber",
        MemoryWords = "3 held: 2 on air, 1 idle · GPU cache limit 128 MB · rung none",
        Attention = new[] { "Frame fence: 3 hung" },
        Displays = new[]
        {
            new EyeDisplay("d1", "\\\\.\\DISPLAY1", 3840, 2160, 50, true, false, false),
            new EyeDisplay("d2", "\\\\.\\DISPLAY2", 1920, 1080, 60, false, false, false),
            new EyeDisplay("d3", "Planned wall", 7680, 2160, 0, false, true, false),
        },
        Screens = new[]
        {
            new EyeScreen { Id = "s1", Number = "1", Label = "Main wall", DisplayId = "d1", OnAir = true, Contract = "3840x2160 50 RGB 8", Verdict = "MATCH", SignalWords = "MATCH · 3840×2160 50", Sources = new[] { "ndi:Cam 1" } },
            new EyeScreen { Id = "s2", Number = "2", Label = "Projector 1", DisplayId = "d2", OnAir = true, Contract = "1920x1080 50 RGB 8", Verdict = "MISMATCH", SignalWords = "rate: 50 asked · 60 observed", Received = "1920x1080 60", ReceivedLight = CheckLight.Red, Sources = new[] { "web:sponsor" } },
            new EyeScreen { Id = "s3", Number = "3", Label = "Confidence", DisplayId = "d3", Sources = Array.Empty<string>(), Locked = true, Armed = false, Ticked = true, Canvas = "A · Main wall" },
        },
        TakeScope = "every screen",
        TakeWords = "→ 1 · Main wall, 2 · Projector 1 · 1 held (locked)",
        Sources = new[]
        {
            new EyeSource("ndi:Cam 1", "Cam 1", "ndi", true, "receiving 50 fps", CheckLight.Green),
        },
        Devices = new[]
        {
            new EyeDevice("dev1", "Barco", "PjLink", "10.0.0.20:4352", true, true, "answering", CheckLight.Green, "power on", InputScreen: "2", Received: "1920x1080 60"),
            new EyeDevice("dev2", "Lights", "Lines", "10.0.0.30:9000", true, false, "no link", CheckLight.Red, ""),
            new EyeDevice("dev3", "Spare", "Lines", "10.0.0.31:9000", false, false, "", CheckLight.Grey, ""),
        },
        Decks = new[] { new EyeDeck("FOH deck", "3.6.0", "10.0.0.5", true), new EyeDeck("Stage deck", "3.6.0", "10.0.0.6", false) },
        Companions = new[] { new EyeCompanion("FOH-PC", "10.0.0.5", true, "5.0.3"), new EyeCompanion("Spare-PC", "10.0.0.9", true, "5.0.3") },
        WireClients = 1,
        WebClients = 2,
        Osc = new EyeOsc(8000, "mapped"),
        Twin = new EyeTwin("main", "in step", "STANDBY-PC", "in step · 3 ms", CheckLight.Green),
        Nodes = new[] { new EyeNodeHeard("i1", "caller", "CALLER-PC", true, true, "linked"), new EyeNodeHeard("i2", "timer", "STAGE-PC", false, false, "") },
        Room = new EyeRoom("APPLE", 12, "quiz", CheckLight.Green),
        Stream = new EyeStream("encoding · 6 Mb/s", CheckLight.Green),
        NdiSends = new[] { new EyeNdiSend("out1", "Patterns PGM", true, 2, "sending") },
        AudioSources = new[] { new EyeAudioSource("player", "Music player", "player"), new EyeAudioSource("tone", "Tone", "tone") },
        AudioOuts = new[] { new EyeAudioOut("device:Speakers", "Speakers", "device", false, -12, ""), new EyeAudioOut("ndi:out1", "NDI audio", "ndi", false, -60, "the sender is closed") },
        AudioRoutes = new[] { new EyeAudioRoute("player", "device:Speakers", -3, false), new EyeAudioRoute("player", "ndi:out1", 0, false) },
        Stack = new EyeStack(true, "02.010 Walk-in", "cue-2", "01.050", false, false, "", CheckLight.Green),
        Assistant = new EyeAssistant(false, ""),
    };

    [Fact]
    public void TheGraphIsBuiltFromFactsWithStableIdsAndHonestLights()
    {
        var g = EyeGraph.Build(Rig());

        // Every thing is there once, under an id the wire can name.
        Assert.NotNull(g.Find("desk"));
        Assert.Equal(CheckLight.Amber, g.Find("desk")!.Light);
        Assert.Equal(CheckLight.Green, g.Find("screen:s1")!.Light);
        Assert.Equal(CheckLight.Red, g.Find("screen:s2")!.Light);                       // MISMATCH is red
        Assert.Equal(CheckLight.Grey, g.Find("screen:s3")!.Light);                      // a planned screen, no contract, no display: unknown, never green
        Assert.Equal(CheckLight.Amber, g.Find("display:d3")!.Light);                    // planned, not plugged in
        Assert.Equal(CheckLight.Red, g.Find("source:web:sponsor")!.Light);              // used by a picture, not mounted
        Assert.Equal("used, not mounted", g.Find("source:web:sponsor")!.Sub);
        Assert.Equal(CheckLight.Green, g.Find("source:ndi:Cam 1")!.Light);
        Assert.NotNull(g.Find("farend:dev1"));                                          // the box that carries screen 2 is the far end of its link
        Assert.Null(g.Find("device:dev1"));
        Assert.Equal(CheckLight.Red, g.Find("device:dev2")!.Light);                     // enabled, no link
        Assert.Equal(CheckLight.Grey, g.Find("device:dev3")!.Light);                    // disabled
        Assert.Equal(CheckLight.Green, g.Find("deck:FOH deck@10.0.0.5")!.Light);
        Assert.Equal(CheckLight.Amber, g.Find("deck:Stage deck@10.0.0.6")!.Light);      // connected, not paired
        Assert.Contains("not paired", g.Find("deck:Stage deck@10.0.0.6")!.Sub);
        Assert.Null(g.Find("companion:FOH-PC"));                                        // heard and connected: one thing, the deck
        Assert.Equal(CheckLight.Grey, g.Find("companion:Spare-PC")!.Light);             // heard, not connected
        Assert.Equal(CheckLight.Red, g.Find("node:i2")!.Light);                         // a node that stopped being heard
        Assert.Equal(CheckLight.Green, g.Find("node:i1")!.Light);
        Assert.Equal("Standby STANDBY-PC", g.Find("twin")!.Label);
        Assert.Equal(CheckLight.Red, g.Find("aout:ndi:out1")!.Light);                   // a lane with an error
        Assert.Equal(CheckLight.Green, g.Find("aout:device:Speakers")!.Light);
        Assert.Equal(CheckLight.Grey, g.Find("asrc:tone")!.Light);                      // not routed
        Assert.Equal("cue", g.Find("stack")!.MenuKind);                                 // the stack opens its standby cue's menu
        Assert.Equal("cue-2", g.Find("stack")!.MenuSubject);
        Assert.Equal("tile", g.Find("screen:s2")!.MenuKind);                            // a screen opens the wall tile's menu
        Assert.Equal(CheckLight.Grey, g.Find("assistant")!.Light);

        // The links, with their own lights.
        var drives = g.EdgeBetween("screen:s2", "display:d2");
        Assert.NotNull(drives);
        Assert.Equal(EyeEdgeKind.Drives, drives!.Kind);
        Assert.Equal(CheckLight.Red, drives.Light);
        var carries = g.EdgeBetween("display:d2", "farend:dev1");
        Assert.Equal(EyeEdgeKind.Carries, carries!.Kind);
        Assert.Equal(CheckLight.Red, carries.Light);                                    // the far end disagrees with the contract
        Assert.Equal(CheckLight.Green, g.EdgeBetween("screen:s1", "display:d1")!.Light);  // MATCH drives green
        Assert.Equal(CheckLight.Grey, g.EdgeBetween("desk", "screen:s3")!.Light);          // nothing to show on yet
        Assert.Equal(CheckLight.Red, g.EdgeBetween("source:web:sponsor", "screen:s2")!.Light);
        Assert.Equal(EyeEdgeKind.Controls, g.EdgeBetween("deck:FOH deck@10.0.0.5", "desk")!.Kind);
        Assert.Equal(EyeEdgeKind.Mirrors, g.EdgeBetween("desk", "twin")!.Kind);
        Assert.Equal(EyeEdgeKind.Follows, g.EdgeBetween("node:i1", "desk")!.Kind);
        Assert.Equal(EyeEdgeKind.Hears, g.EdgeBetween("node:i2", "desk")!.Kind);
        Assert.Equal(CheckLight.Red, g.EdgeBetween("asrc:player", "aout:ndi:out1")!.Light);
        Assert.Equal("-3.0 dB", g.EdgeBetween("asrc:player", "aout:device:Speakers")!.Words);

        // Deterministic: the same facts, the same picture.
        var again = EyeGraph.Build(Rig());
        Assert.Equal(g.Nodes.Select(n => n.Id), again.Nodes.Select(n => n.Id));
        Assert.Equal(g.Edges.Select(e => (e.From, e.To, e.Kind, e.Light)), again.Edges.Select(e => (e.From, e.To, e.Kind, e.Light)));
    }

    [Fact]
    public void TheHeadlineNamesTheWorstAndTheProblemsWalkRedsThenAmbers()
    {
        var g = EyeGraph.Build(Rig());
        Assert.True(g.Red >= 4, $"red {g.Red}");
        Assert.Equal(CheckLight.Red, g.WorstLight);
        Assert.StartsWith($"{g.Nodes.Count} things · {g.Red} red · {g.Amber} amber — ", g.Headline);
        Assert.Equal("source:web:sponsor", g.Problems[0]);                              // the pictures first, upstream first: the unmounted source before the screen it starves
        Assert.Equal("screen:s2", g.Problems[1]);
        Assert.Contains("web:sponsor: used, not mounted", g.Headline);
        var reds = g.Problems.TakeWhile(id => g.Find(id)!.Light == CheckLight.Red).Count();
        Assert.Equal(g.Red, reds);                                                      // every red before the first amber
        Assert.All(g.Problems.Skip(reds), id => Assert.Equal(CheckLight.Amber, g.Find(id)!.Light));

        // NEXT and PREV walk the queue round.
        Assert.Equal(g.Problems[0], g.Next(null));
        Assert.Equal(g.Problems[1], g.Next(g.Problems[0]));
        Assert.Equal(g.Problems[0], g.Next(g.Problems[^1]));
        Assert.Equal(g.Problems[^1], g.Prev(g.Problems[0]));
        Assert.Equal(g.Problems[0], g.Next("screen:s1"));                               // from a green thing: the first problem

        // All green reads all green; nothing reads nothing.
        var calm = EyeGraph.Build(new EyeFacts { MachineName = "PC", OutputsLive = true, Health = CheckLight.Green, Displays = new[] { new EyeDisplay("d1", "D1", 1920, 1080, 60, true, false, false) }, Screens = new[] { new EyeScreen { Id = "s1", Number = "1", Label = "Wall", DisplayId = "d1", OnAir = true, Contract = "1920x1080 60 RGB 8", Verdict = "MATCH" } } });
        Assert.Equal("3 things linked · all green", calm.Headline);
        Assert.Null(calm.Worst);
        Assert.Null(calm.Next(null));
        Assert.Empty(EyeGraph.Empty.Nodes);
        Assert.Contains("Nothing to see yet", EyeGraph.Empty.Headline);
    }

    [Fact]
    public void LensesKeepTheDeskAndTheProblemsLensShowsWhatTheyTouch()
    {
        var g = EyeGraph.Build(Rig());
        var desk = g.Find("desk")!;
        foreach (var lens in Enum.GetValues<EyeLens>()) Assert.True(g.Visible(desk, lens), lens.ToString());
        Assert.True(g.Visible(g.Find("aout:device:Speakers")!, EyeLens.Audio));
        Assert.False(g.Visible(g.Find("aout:device:Speakers")!, EyeLens.Video));
        Assert.True(g.Visible(g.Find("screen:s2")!, EyeLens.Problems));                // red
        Assert.True(g.Visible(g.Find("display:d2")!, EyeLens.Problems));               // touches the red screen
        Assert.False(g.Visible(g.Find("asrc:tone")!, EyeLens.Problems));               // grey, touching nothing red or amber
        Assert.Equal(EyeLens.Control, EyeGraph.ParseLens("control"));
        Assert.Equal(EyeLens.All, EyeGraph.ParseLens(""));
        Assert.Null(EyeGraph.ParseLens("sideways"));

        // De-emphasis by hops: the focus and its links full, two hops dimmer, the rest on the floor — present, never hidden.
        var hops = g.Hops("screen:s2");
        Assert.Equal(0, hops["screen:s2"]);
        Assert.Equal(1, hops["display:d2"]);
        Assert.Equal(1, hops["desk"]);
        Assert.Equal(2, hops["farend:dev1"]);
        Assert.Equal(1.0, EyeGraph.Emphasis(hops["display:d2"]));
        Assert.Equal(0.55, EyeGraph.Emphasis(hops["farend:dev1"]));
        Assert.False(hops.ContainsKey("node:i2"));                                      // the walk never runs through the desk: a deck is not a screen's neighbour
        Assert.Equal(0.25, EyeGraph.EmphasisOf("node:i2", hops));                        // unreached: the floor, present and dim
        Assert.Equal(0.25, EyeGraph.EmphasisOf("aout:device:Speakers", hops));
        Assert.Equal(1.0, EyeGraph.EmphasisOf("node:i2", null));                         // no focus: everything full
        Assert.Equal(1.0, EyeGraph.Emphasis(null));
        Assert.True(g.Hops("desk").Count > 10);                                         // the desk's own focus walks everything it touches
        Assert.Empty(g.Hops("nowhere"));
    }

    [Fact]
    public void TheTakePlanAndEveryTileSwitchAreInThePicture()
    {
        // Round 67.8: what the next TAKE will do is on the desk's node; a screen's lock, arm, tick and canvas are on its own.
        var g = EyeGraph.Build(Rig());
        Assert.Contains("Next TAKE (every screen) → 1 · Main wall, 2 · Projector 1 · 1 held (locked)", g.Find("desk")!.Words);
        var held = g.Find("screen:s3")!.Words;
        Assert.Contains(held, w => w.StartsWith("LOCKED", StringComparison.Ordinal));
        Assert.Contains(held, w => w.StartsWith("held", StringComparison.Ordinal));
        Assert.Contains(held, w => w.StartsWith("ticked", StringComparison.Ordinal));
        Assert.Contains("in canvas A · Main wall", held);
        // An armed, unlocked, unticked screen of its own says none of it — the notable state is the word, the default is silence.
        var plain = g.Find("screen:s1")!.Words;
        Assert.DoesNotContain(plain, w => w.StartsWith("LOCKED", StringComparison.Ordinal) || w.StartsWith("held", StringComparison.Ordinal) || w.StartsWith("ticked", StringComparison.Ordinal) || w.StartsWith("in canvas", StringComparison.Ordinal));
        // A node with no picker says nothing about a take.
        var node = EyeGraph.Build(new EyeFacts { MachineName = "CALLER", Health = CheckLight.Green });
        Assert.DoesNotContain(node.Find("desk")!.Words, w => w.StartsWith("Next TAKE", StringComparison.Ordinal));
    }

    [Fact]
    public void WordsResolveToAThing()
    {
        var g = EyeGraph.Build(Rig());
        Assert.Equal("screen:s2", g.Resolve("screen 2"));
        Assert.Equal("screen:s2", g.Resolve("2"));
        Assert.Equal("screen:s2", g.Resolve("SCREEN:S2"));                              // the id, whatever the case
        Assert.Equal("screen:s2", g.Resolve("Projector 1"));
        Assert.Equal("farend:dev1", g.Resolve("barco"));                                // a label containing the words
        Assert.Equal("desk", g.Resolve("this desk"));
        Assert.Null(g.Resolve("the moon"));
        Assert.Null(g.Resolve(""));
    }

    [Fact]
    public void TheLayoutIsDeterministicOverlapFreeAndBanded()
    {
        var g = EyeGraph.Build(Rig());
        var p = EyeLayout.Place(g);
        var again = EyeLayout.Place(EyeGraph.Build(Rig()));
        Assert.Equal(g.Nodes.Count, p.Rects.Count);
        foreach (var n in g.Nodes) Assert.Equal(p.Of(n.Id), again.Of(n.Id));
        var rects = p.Rects.ToList();
        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++) Assert.False(rects[i].Value.Intersects(rects[j].Value), $"{rects[i].Key} overlaps {rects[j].Key}");
        }
        // Bands top to bottom in the picture's order, each holding its own things.
        Assert.Equal(new[] { EyePlane.Control, EyePlane.Video, EyePlane.Audio, EyePlane.Room }, p.Bands.Select(b => b.Plane));
        for (var i = 1; i < p.Bands.Count; i++) Assert.True(p.Bands[i].Rect.Y > p.Bands[i - 1].Rect.Bottom);
        foreach (var n in g.Nodes)
        {
            var band = p.Bands.Single(b => b.Plane == n.Plane).Rect;
            var r = p.Of(n.Id);
            Assert.True(r.Y >= band.Y && r.Bottom <= band.Bottom, n.Id);
            Assert.Equal(EyeLayout.BandPad + n.Tier * EyeLayout.ColW, r.X);              // the tier is the column
        }
        // The signal travels left to right: a source left of the desk, the desk left of the screen, the screen left of its display, the display left of the far end.
        Assert.True(p.Of("source:ndi:Cam 1").X < p.Of("desk").X);
        Assert.True(p.Of("desk").X < p.Of("screen:s2").X);
        Assert.True(p.Of("screen:s2").X < p.Of("display:d2").X);
        Assert.True(p.Of("display:d2").X < p.Of("farend:dev1").X);
        // Hit-testing by rectangle maths; the focus area takes the neighbours in.
        var r2 = p.Of("screen:s2");
        Assert.Equal("screen:s2", p.At(r2.CenterX, r2.CenterY));
        Assert.Null(p.At(-100, -100));
        var around = p.Around("screen:s2", g);
        Assert.True(around.W > r2.W && around.Contains(p.Of("display:d2").CenterX, p.Of("display:d2").CenterY));
        Assert.True(p.Bounds.W >= EyeLayout.Tiers * EyeLayout.ColW);
    }

    [Fact]
    public void TheCameraFitsZoomsAboutAPointAndGlidesWithoutOvershoot()
    {
        var cam = new EyeCamera();
        var world = new EyeRect(0, 0, 1400, 700);
        cam.FitTo(world, 700, 350, pad: 0, animate: false);
        Assert.Equal(0.5, cam.Scale, 6);
        Assert.Equal((350.0, 175.0), cam.WorldToView(world.CenterX, world.CenterY));   // centred

        // Zoom about a view point keeps the world point under it.
        var (wx, wy) = cam.ViewToWorld(100, 80);
        cam.ZoomAt(100, 80, 2);
        Assert.Equal(1.0, cam.Scale, 6);
        var (vx, vy) = cam.WorldToView(wx, wy);
        Assert.Equal(100, vx, 6);
        Assert.Equal(80, vy, 6);
        cam.ZoomAt(100, 80, 1000);
        Assert.Equal(EyeCamera.MaxScale, cam.Scale, 6);                                 // clamped
        cam.Pan(10, -5);
        Assert.Equal(cam.TargetX, cam.X);                                              // a pan is immediate

        // A focus glides: the spring never overshoots and settles within two seconds at 60 Hz.
        cam.FitTo(world, 700, 350, pad: 0, animate: false);
        var start = cam.View;
        cam.FocusOn(new EyeRect(200, 200, 200, 52), 700, 350);
        Assert.True(cam.TargetScale > start.Scale);
        var frames = 0;
        var maxScale = cam.Scale;
        while (cam.Step(1.0 / 60) && frames < 600)
        {
            frames++;
            Assert.True(cam.Scale <= cam.TargetScale + 1e-6, "overshoot on the way in");
            maxScale = Math.Max(maxScale, cam.Scale);
        }
        Assert.True(frames > 3 && frames < 120, $"settled in {frames} frames");
        Assert.True(cam.IsSettled);
        Assert.Equal(cam.TargetScale, cam.Scale, 6);
        Assert.Equal(cam.TargetX, cam.X, 6);

        // A long frame is sub-stepped, not leapt: one 250 ms step lands the same side of the target as sixty small ones would.
        cam.Restore(start, animate: false);
        cam.FocusOn(new EyeRect(200, 200, 200, 52), 700, 350);
        cam.Step(0.25);
        Assert.True(cam.Scale <= cam.TargetScale + 1e-6);
        Assert.True(cam.Scale > start.Scale);
        // Back to the kept view.
        cam.Restore(start, animate: false);
        Assert.Equal(start, cam.View);
    }

    [Fact]
    public void TheJsonCarriesTheWholePicture()
    {
        var g = EyeGraph.Build(Rig());
        var json = EyeJson.Write(g, EyeLayout.Place(g), "screen:s2", EyeLens.Video);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(g.Headline, root.GetProperty("headline").GetString());
        Assert.Equal("source:web:sponsor", root.GetProperty("worst").GetString());
        Assert.Equal("red", root.GetProperty("worstLight").GetString());
        Assert.Equal("screen:s2", root.GetProperty("focus").GetString());
        Assert.Equal("video", root.GetProperty("lens").GetString());
        Assert.Equal(g.Nodes.Count, root.GetProperty("nodes").GetArrayLength());
        Assert.Equal(g.Edges.Count, root.GetProperty("edges").GetArrayLength());
        Assert.Equal(g.Red, root.GetProperty("counts").GetProperty("red").GetInt32());
        var node = root.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("id").GetString() == "screen:s2");
        Assert.Equal("EYE FOCUS screen:s2", node.GetProperty("wire").GetString());
        Assert.Equal("Screens", node.GetProperty("page").GetString());
        Assert.Equal("s2", node.GetProperty("item").GetString());
        Assert.True(node.GetProperty("x").GetDouble() > 0);
        Assert.Equal(g.Problems, root.GetProperty("problems").EnumerateArray().Select(p => p.GetString()!).ToList());

        // The words the assistant reads: one line per thing and per link, the headline first.
        var lines = g.Lines();
        Assert.Equal(g.Headline, lines[0]);
        Assert.Contains(lines, l => l.StartsWith("VIDEO · Screen: Projector 1 — red", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Projector 1 → \\\\.\\DISPLAY2 (drives, red", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEyeMenuHasFocusTheLinksThePageAndAsk()
    {
        var g = EyeGraph.Build(Rig());
        var facts = new DeskFacts { AssistantReady = false };
        var menu = EyeMenus.For(facts, g, g.Find("farend:dev1")!);
        Assert.Equal("eye", menu.Kind);
        Assert.Equal("farend:dev1", menu.Subject);
        Assert.Equal(MenuTone.Tile, menu.Tone);
        var entries = menu.Flatten().ToList();
        var focus = entries.Single(e => e.Id == "eye.focus");
        Assert.Equal("EYE FOCUS farend:dev1", focus.Wire);
        Assert.Equal(ShowActionKind.EyeFocus, focus.Action!.Value.Kind);
        Assert.Equal("farend:dev1", focus.Action.Value.Value);
        Assert.Contains(entries, e => e.Id == "eye.next");                               // there are problems
        Assert.Contains(entries, e => e.Id == "eye.contact:display:d2" && e.Detail.Contains("carries to this"));
        var open = entries.Single(e => e.Id == "eye.open");
        Assert.Equal(new MenuRoute("Interactive", "dev1"), open.Route);
        var ask = entries.Single(e => e.Id == "eye.ask");
        Assert.False(ask.IsEnabled);                                                    // no key: says why
        Assert.Contains("Barco", ask.Question);
        Assert.DoesNotContain(entries, e => e.Id == "eye.why");                          // green: nothing to explain

        var red = EyeMenus.For(facts with { AssistantReady = true }, g, g.Find("screen:s2")!);
        Assert.Equal(MenuTone.Warn, red.Tone);
        var why = red.Flatten().Single(e => e.Id == "eye.why");
        Assert.True(why.IsEnabled);
        Assert.Contains("say unknown where they are silent", why.Question);
        Assert.All(red.Flatten().Where(e => e.Scope == MenuScope.Live), e => Assert.True(e.HasWire));   // every verb shows its wire line
    }

    [Fact]
    public void TheEyeVerbsAreTheDesksAlone()
    {
        foreach (var kind in new[] { ShowActionKind.EyeFocus, ShowActionKind.EyeNext, ShowActionKind.EyePrev, ShowActionKind.EyeLens, ShowActionKind.EyeReset })
        {
            Assert.Contains("never moves what the desk is looking at", ActionSpec.DeskOnly(kind));
            Assert.StartsWith("God's Eye", ActionSpec.Label(kind));
        }
        Assert.Equal((TargetKind.None, ValueKind.Text), ActionSpec.For(ShowActionKind.EyeFocus));
        Assert.Equal((TargetKind.None, ValueKind.None), ActionSpec.For(ShowActionKind.EyeReset));
    }

    [Fact]
    public void TheDeskNodeCarriesTheMemoryLineAndItsHelpersStayStable()
    {
        var g = EyeGraph.Build(Rig());
        var desk = g.Nodes.Single(n => n.Id == EyeGraph.DeskId);
        Assert.Contains("3 held: 2 on air, 1 idle · GPU cache limit 128 MB · rung none", desk.Words);
        Assert.DoesNotContain(EyeGraph.Build(new EyeFacts { MachineName = "bare" }).Nodes.Single(n => n.Id == EyeGraph.DeskId).Words, w => w.Contains("held", StringComparison.Ordinal));   // no line without the facts

        // The helpers behind the line: counts by reason, no bytes and no countdown; the cache's bound and rung, not its fill.
        var holds = new[]
        {
            new Hold("clip:a", "clip", "A", HoldReason.OnAir, 100),
            new Hold("clip:b", "clip", "B", HoldReason.OnAir, 100),
            new Hold("pic:c", "picture", "C", HoldReason.Idle, 50, IdleSeconds: 17),
        };
        Assert.Equal("3 held: 2 on air, 1 idle", Residency.CountWords(holds));
        Assert.Equal("nothing held", Residency.CountWords(Array.Empty<Hold>()));
        Assert.Equal("GPU cache limit 128 MB · rung none", GpuGovernor.EyeWords(true, 128L * 1024 * 1024, MemoryPressure.None));
        Assert.Equal("GPU cache limit 64 MB · rung high", GpuGovernor.EyeWords(true, 64L * 1024 * 1024, MemoryPressure.High));
        Assert.Equal("GPU cache: no GPU context (software rendering)", GpuGovernor.EyeWords(false, 128L * 1024 * 1024, MemoryPressure.None));
    }
}
