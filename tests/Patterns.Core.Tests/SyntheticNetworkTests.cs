using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The NETWORK band over synthetic rooms of 0 to 160 apps, each from a fixed seed (round 84's xorshift, so every machine
/// and every run draws the same rooms): nothing overlaps, ids and slots are stable through a history of apps coming and going,
/// the band stays inside its bounds, and what it leaves out is counted exactly.
/// </summary>
public class SyntheticNetworkTests
{
    /// <summary>xorshift64 from a fixed seed: the same room on every machine and every run.</summary>
    private sealed class Noise
    {
        private ulong _s;

        public Noise(ulong seed) => _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed * 0x9E3779B97F4A7C15UL;

        public int Next(int max)
        {
            _s ^= _s << 13;
            _s ^= _s >> 7;
            _s ^= _s << 17;
            return (int)(_s % (ulong)max);
        }

        public bool Chance(int percent) => Next(100) < percent;
    }

    private static readonly string[] Kinds = { "timer", "department", "screen", "output", "projector", "surface", "link", "help", "room", "audio", "media", "odd" };
    private static readonly string[] Lights = { "green", "green", "green", "amber", "red", "grey" };
    private static readonly string[] AppKinds = { "Stagewatch", "PlayMap", "AGMessage", "Patterns node", "Slides agent" };

    /// <summary>A room of apps, every app id of the noise's own (so a later picture can keep some and lose others).</summary>
    private static List<EyeShowApp> Apps(Noise noise, int count, int from)
    {
        var apps = new List<EyeShowApp>();
        for (var i = 0; i < count; i++)
        {
            var id = $"a{from + i:0000}";
            var things = noise.Chance(80) ? noise.Next(31) : 0;
            var state = noise.Next(10) switch { 0 => ShowBusState.Gone, 1 => ShowBusState.Stale, _ => ShowBusState.Live };
            var context = things == 0 && noise.Chance(50) ? null : new ShowContext
            {
                Node = new ShowContextNode { Kind = "x", Id = id, Name = id },
                Light = Lights[noise.Next(Lights.Length)],
                Items = Enumerable.Range(0, things).Select(t => new ShowContextItem { Id = $"t{t}", Kind = Kinds[noise.Next(Kinds.Length)], Label = $"{id} thing {t}", Light = Lights[noise.Next(Lights.Length)], State = "s" }).ToList(),
            };
            apps.Add(new EyeShowApp(id, AppKinds[noise.Next(AppKinds.Length)], $"Room {id}", state, noise.Chance(30), "") { Context = context, Needed = noise.Chance(10) });
        }
        return apps;
    }

    private static EyeFacts Facts(IReadOnlyList<EyeShowApp> apps) => new()
    {
        MachineName = "FOH",
        Health = CheckLight.Green,
        Screens = new[] { new EyeScreen { Id = "s1", Number = "1", Label = "Wall" } },
        Decks = new[] { new EyeDeck("FOH deck", "3.20.0", "10.0.0.5", true) },
        ShowApps = apps,
    };

    public static IEnumerable<object[]> Rooms()
    {
        foreach (var n in new[] { 0, 1, 2, 5, 16, 17, 40, 127, 128, 129, 160 }) yield return new object[] { (ulong)(n + 1), n };
        var noise = new Noise(84);
        for (var i = 0; i < 12; i++) yield return new object[] { (ulong)(1000 + i), noise.Next(161) };
    }

    [Theory]
    [MemberData(nameof(Rooms))]
    public void ARoomOfAppsIsPlacedWithoutOverlapInsideItsBandsAndWhatIsLeftOutIsCounted(ulong seed, int count)
    {
        var apps = Apps(new Noise(seed), count, 0);
        var g = EyeGraph.Build(Facts(apps));
        var why = $"seed {seed} · {count} apps";

        // Stable ids: the same facts, the same things in the same order; one card per drawn app, at most the bound, and one for the rest.
        Assert.Equal(g.Nodes.Select(n => n.Id), EyeGraph.Build(Facts(apps)).Nodes.Select(n => n.Id));
        var cards = g.Nodes.Where(n => n.Kind == EyeKind.ShowApp && n.Id != EyeGraph.MoreId).ToList();
        Assert.Equal(Math.Min(count, EyeLayout.MaxApps), cards.Count);
        Assert.Equal(Math.Max(0, count - EyeLayout.MaxApps), g.MoreApps);
        Assert.Equal(count > EyeLayout.MaxApps, g.Find(EyeGraph.MoreId) is not null);
        Assert.All(cards, c => Assert.Equal("app:" + c.App, c.Id));
        var drawnNeeded = cards.Count(c => apps.Single(a => "app:" + a.Id == c.Id).Needed);
        Assert.Equal(Math.Min(apps.Count(a => a.Needed), EyeLayout.MaxApps), drawnNeeded);   // the needed are drawn first

        var slots = EyeLayout.Slots(g, new Dictionary<string, int>());
        Assert.Equal(cards.Select((c, i) => (c.Id, i)), cards.Select(c => (c.Id, slots[c.Id])));   // a first picture: slots in the picture's order
        var p = EyeLayout.Place(g, new EyeNetworkView { Slots = slots });
        var again = EyeLayout.Place(EyeGraph.Build(Facts(apps)), new EyeNetworkView { Slots = slots });
        foreach (var n in g.Nodes) Assert.Equal(p.Of(n.Id), again.Of(n.Id));

        // No two things overlap; each placed thing sits inside its band; every card is placed; the fit view reads or says it cannot.
        var rects = p.Rects.OrderBy(r => r.Value.Y).ThenBy(r => r.Value.X).ToList();
        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count && rects[j].Value.Y < rects[i].Value.Bottom; j++)
                Assert.False(rects[i].Value.Intersects(rects[j].Value), $"{why}: {rects[i].Key} overlaps {rects[j].Key}");
        }
        foreach (var (id, r) in p.Rects)
        {
            var band = p.Bands.Single(b => b.Plane == g.Find(id)!.Band).Rect;
            Assert.True(r.X >= band.X && r.Right <= band.Right && r.Y >= band.Y && r.Bottom <= band.Bottom, $"{why}: {id} outside its band");
            Assert.True(p.Bounds.Contains(r.X, r.Y) && r.Right <= p.Bounds.Right && r.Bottom <= p.Bounds.Bottom, $"{why}: {id} outside the bounds");
        }
        Assert.All(cards, c => Assert.False(p.Of(c.Id).IsEmpty, $"{why}: {c.Id} has no place"));
        Assert.InRange(p.Expanded.Count, 0, EyeLayout.MaxExpanded);
        Assert.True(p.MarksOnly ? p.Expanded.Count == 0 && p.Note.Contains("marks only", StringComparison.Ordinal) : p.FitScale >= EyeLayout.LabelScale, $"{why}: fit {p.FitScale:0.000}");
        Assert.Equal(count > 0, p.Bands.Any(b => b.Plane == EyePlane.Network));

        // 'left', exact: the apps past the bound, and every thing a drawn app's context names that has no place.
        Assert.Equal(Math.Max(0, count - EyeLayout.MaxApps), p.LeftApps);
        var expected = 0;
        foreach (var c in cards)
        {
            var app = apps.Single(a => "app:" + a.Id == c.Id);
            if (app.Context is null || app.State == ShowBusState.Gone) continue;
            var named = app.Context.Items.Count;
            expected += named - (p.IsExpanded(c.Id) ? Math.Min(named, EyeLayout.ClusterThings) : 0);
        }
        Assert.Equal(expected, p.LeftItems);
        Assert.Equal(expected, cards.Sum(c => g.NamedBy(c.Id)) - p.Rects.Keys.Count(id => g.Find(id)!.Kind == EyeKind.ShowAppItem));
    }

    [Theory]
    [InlineData(7UL)]
    [InlineData(84UL)]
    [InlineData(95UL)]
    public void SlotsAreStickyThroughAHistoryOfAppsComingAndGoing(ulong seed)
    {
        var noise = new Noise(seed);
        var kept = new Dictionary<string, int>(StringComparer.Ordinal);
        var room = Apps(noise, 20 + noise.Next(40), 0);
        var next = room.Count;
        EyePlacement? last = null;
        EyeGraph? lastGraph = null;
        for (var step = 0; step < 12; step++)
        {
            var g = EyeGraph.Build(Facts(room));
            var slots = EyeLayout.Slots(g, kept);
            // Every app that stayed keeps its slot; slots are distinct; a new app took the lowest slot free when it came.
            foreach (var (card, slot) in kept.Where(kv => slots.ContainsKey(kv.Key))) Assert.Equal(slot, slots[card]);
            Assert.Equal(slots.Count, slots.Values.Distinct().Count());
            var fresh = slots.Keys.Where(k => !kept.ContainsKey(k)).Select(k => slots[k]).OrderBy(s => s).ToList();
            var free = Enumerable.Range(0, slots.Count + kept.Count).Where(s => !kept.Where(kv => slots.ContainsKey(kv.Key)).Select(kv => kv.Value).Contains(s)).Take(fresh.Count).ToList();
            Assert.Equal(free, fresh);
            // The same history draws the same picture: placing it again from the same slots moves nothing.
            var p = EyeLayout.Place(g, new EyeNetworkView { Slots = slots });
            var twice = EyeLayout.Place(EyeGraph.Build(Facts(room)), new EyeNetworkView { Slots = EyeLayout.Slots(g, kept) });
            foreach (var n in g.Nodes) Assert.Equal(p.Of(n.Id), twice.Of(n.Id));
            // A picture whose apps did not change, relit only, keeps every place.
            if (last is not null && lastGraph is not null && lastGraph.Nodes.Select(n => n.Id).SequenceEqual(g.Nodes.Select(n => n.Id)))
            {
                foreach (var n in g.Nodes) Assert.Equal(last.Of(n.Id), p.Of(n.Id));
            }
            kept = slots;
            last = p;
            lastGraph = g;

            // The next picture: a few go, a few come.
            var leave = noise.Next(4);
            for (var i = 0; i < leave && room.Count > 0; i++) room.RemoveAt(noise.Next(room.Count));
            var come = noise.Next(4);
            room.AddRange(Apps(noise, come, next));
            next += come;
            if (step % 4 == 3) room = room.Select(a => a with { Context = a.Context is null ? null : a.Context with { Light = "red" } }).ToList();   // a light changes nothing's place
        }
    }
}
