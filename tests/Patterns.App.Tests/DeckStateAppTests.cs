using System.Net;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// What a deck's keys read of the room around the desk: STATE carries every node heard (by place,
/// with its kind and whether it is heard now), the callers linked, the twin's role and phase, and
/// the stage — the timer in its own colour, the segment, the messages waiting — in the shape the
/// module's own fixture has, so the keys light from real facts; a node appearing or a message to
/// the stage is a push of its own; and the Nodes page's card wears the kind's colour the deck does.
/// </summary>
public class DeckStateAppTests
{
    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static JsonElement State(CommandRouter router) => JsonDocument.Parse(router.StateJson()).RootElement;

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the condition never held");
        }
    }

    private static JsonElement Fixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "integrations", "companion-module-patterns"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "integrations", "companion-module-patterns", "test", "fixtures", "state.json"))).RootElement;
    }

    private static void SameKeys(JsonElement fixture, JsonElement actual, string where)
    {
        foreach (var p in fixture.EnumerateObject())
        {
            Assert.True(actual.TryGetProperty(p.Name, out var got), $"{where}.{p.Name} is in the module's fixture but not in STATE");
            if (p.Value.ValueKind == JsonValueKind.Object && got.ValueKind == JsonValueKind.Object) SameKeys(p.Value, got, $"{where}.{p.Name}");
        }
    }

    [AvaloniaFact]
    public void StateCarriesTheNodesTheTwinAndTheStageAsTheModuleReadsThem()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var router = new CommandRouter(services);
            var fixture = Fixture();

            // Nothing heard yet: the blocks are there and empty, in the fixture's shape.
            var quiet = State(router);
            Assert.Equal(0, quiet.GetProperty("linked").GetInt32());
            Assert.Empty(quiet.GetProperty("nodes").EnumerateArray());
            SameKeys(fixture.GetProperty("twin"), quiet.GetProperty("twin"), "twin");
            SameKeys(fixture.GetProperty("stage"), quiet.GetProperty("stage"), "stage");
            Assert.Equal("off", quiet.GetProperty("twin").GetProperty("role").GetString());
            Assert.Equal("idle", quiet.GetProperty("stage").GetProperty("timer").GetProperty("phase").GetString());
            Assert.Equal("--:--", quiet.GetProperty("stage").GetProperty("timer").GetProperty("text").GetString());
            Assert.Equal("", quiet.GetProperty("stage").GetProperty("pendingSpeaker").GetString());

            // A caller node heard on the beacon is the first node row, in the module's shape, with its kind's hue on the desk's card.
            var signatureBefore = services.DeckSignature();
            services.Beacon.Hear(new Beacon { Instance = "a1b2", Kind = "caller", Machine = "FOH-CALL", Show = "Gala", Health = "Caller FOH-CALL", Wire = 9697, Http = 9696, Link = 9700 }, new IPEndPoint(IPAddress.Parse("10.0.0.12"), 9700));
            services.Nodes.Poll();
            var card = Assert.Single(services.Nodes.Nodes);
            Assert.Equal("#8250DC", card.Hue);
            var state = State(router);
            var node = Assert.Single(state.GetProperty("nodes").EnumerateArray());
            SameKeys(fixture.GetProperty("nodes")[0], node, "nodes[0]");
            Assert.Equal(1, node.GetProperty("n").GetInt32());
            Assert.Equal("caller", node.GetProperty("kind").GetString());
            Assert.Equal("FOH-CALL", node.GetProperty("name").GetString());
            Assert.Equal("Gala", node.GetProperty("show").GetString());
            Assert.True(node.GetProperty("fresh").GetBoolean());
            Assert.Equal("Caller FOH-CALL", node.GetProperty("words").GetString());
            Assert.Equal("10.0.0.12", node.GetProperty("address").GetString());
            Assert.Equal(9700, node.GetProperty("link").GetInt32());
            // The node appearing is a push of its own: the deck signature the wire's clock tick watches has moved.
            Assert.NotEqual(signatureBefore, services.DeckSignature());
            var signatureNode = services.DeckSignature();

            // The stage: a countdown running is the timer in its own colour; a message to the speaker waits for its ACK.
            Assert.StartsWith("OK", Send(router, "COUNTDOWN START 5"));
            var running = State(router).GetProperty("stage");
            Assert.Equal("running", running.GetProperty("timer").GetProperty("phase").GetString());
            Assert.Equal("green", running.GetProperty("timer").GetProperty("colour").GetString());
            Assert.Matches("^[45]:[0-5][0-9]$", running.GetProperty("timer").GetProperty("text").GetString());
            Assert.InRange(running.GetProperty("timer").GetProperty("remaining").GetDouble(), 290, 300);
            Assert.False(running.GetProperty("timer").GetProperty("paused").GetBoolean());
            Assert.Equal("#1E9E5A", CompanionPalette.StageHue(running.GetProperty("timer").GetProperty("colour").GetString()!));
            Assert.StartsWith("OK", Send(router, "STAGE MESSAGE Wrap up"));
            Assert.NotEqual(signatureNode, services.DeckSignature());                 // a message waiting is a push of its own too
            var pending = State(router).GetProperty("stage");
            Assert.Equal("Wrap up", pending.GetProperty("pendingSpeaker").GetString());
            Assert.Equal("", pending.GetProperty("pendingCrew").GetString());
            Assert.StartsWith("OK", Send(router, "TIMER PAUSE"));
            Assert.Equal("paused", State(router).GetProperty("stage").GetProperty("timer").GetProperty("phase").GetString());
            Assert.StartsWith("OK", Send(router, "STAGE CLEAR"));
            Assert.Equal("", State(router).GetProperty("stage").GetProperty("pendingSpeaker").GetString());

            // The whole of the module's fixture is answered at the top level too: every key a variable reads is a key the desk sends.
            var full = State(router);
            foreach (var key in new[] { "show", "version", "decks", "linked", "nodes", "twin", "stage", "cuestack", "looks", "screens", "overlays", "stream", "machine" })
            {
                Assert.True(full.TryGetProperty(key, out _), key);
            }
            // The machine row carries the sinks' render faults, so a deck key can go red while the room is on the last good picture.
            var machine = full.GetProperty("machine");
            Assert.Equal(0, machine.GetProperty("renderFaults").GetInt32());
            Assert.False(machine.GetProperty("faulting").GetBoolean());
        }
        finally
        {
            b.Dispose();
        }
    }
}
