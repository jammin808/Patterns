using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Round 89: this machine's book of kept page addresses — keep, reuse, resolve, bound, merge, and its file.</summary>
public class WebPassBookTests
{
    private static readonly DateTime T0 = new(2026, 10, 25, 9, 12, 0, DateTimeKind.Utc);

    /// <summary>Ids from a counter: deterministic and distinct.</summary>
    internal static Func<string> Ids(int from = 0)
    {
        var n = from;
        return () =>
        {
            var v = n++;
            var chars = new char[WindowAddresses.IdLength];
            for (var i = chars.Length - 1; i >= 0; i--)
            {
                chars[i] = WindowAddresses.IdAlphabet[v & 31];
                v >>= 5;
            }
            return new string(chars);
        };
    }

    private static Func<string> Sequence(params string[] ids)
    {
        var queue = new Queue<string>(ids);
        return () => queue.Dequeue();
    }

    [Fact]
    public void TheSameAddressKeptTwiceIsOneEntry()
    {
        var (book, first) = WebPassBook.Empty.Keep(WindowAddressesTests.Window, Sequence("abcdefghijkl"), T0);
        Assert.Equal("kept://10.0.0.5:9924/abcdefghijkl", first);
        var (again, second) = book.Keep(WindowAddressesTests.Window, Sequence("mnopqrstuvwx"), T0.AddMinutes(1));
        Assert.Same(book, again);
        Assert.Equal(first, second);
        Assert.Equal(1, again.Count);

        var resolved = again.Resolve(first);
        Assert.True(resolved.Resolved);
        Assert.Equal(WindowAddressesTests.Window, resolved.Url);
        Assert.Equal(new[] { WindowAddressesTests.Window }, again.SecretValues);
    }

    [Fact]
    public void AnIdTheBookAlreadyHasIsDrawnAgain()
    {
        var (book, _) = WebPassBook.Empty.Keep("https://x/feed?pin=1", Sequence("abcdefghijkl"), T0);
        var (two, address) = book.Keep("https://x/feed?pin=2", Sequence("abcdefghijkl", "abcdefghijkm"), T0);
        Assert.Equal("kept://x/abcdefghijkm", address);
        Assert.Equal(2, two.Count);
        Assert.Throws<InvalidOperationException>(() => two.Keep("https://x/feed?pin=3", () => "abcdefghijkl", T0));
    }

    [Fact]
    public void WhatAHeldAddressOpensAndHowItIsSaid()
    {
        var (book, kept) = WebPassBook.Empty.Keep(WindowAddressesTests.Window, Sequence("abcdefghijkl"), T0);
        Assert.Equal(new WebPassResolution(WindowAddressesTests.Window, ""), book.Resolve(kept));
        Assert.Equal(new WebPassResolution("https://example.com", ""), book.Resolve("https://example.com"));
        Assert.Equal(new WebPassResolution(null, WebPassBook.NotHereWhy), book.Resolve("kept://10.0.0.5:9924/zzzzzzzzzzzz"));
        Assert.Equal(new WebPassResolution(null, WebPassBook.NotHereWhy), WebPassBook.Empty.Resolve(kept));
        Assert.Equal(new WebPassResolution(null, WebPassBook.UnkeptWhy), book.Resolve("unkept://10.0.0.5:9924"));
        Assert.Equal(new WebPassResolution(null, WebPassBook.MismatchWhy), book.Resolve("kept://10.0.0.9:9924/abcdefghijkl"));

        Assert.Equal("10.0.0.5 (private address, kept)", book.Label(kept));
        Assert.Equal("10.0.0.5 (private address, kept on another machine)", WebPassBook.Empty.Label(kept));
        Assert.Equal("10.0.0.5 (private address, not kept — paste it again)", book.Label("unkept://10.0.0.5:9924"));
        Assert.Equal("example.com", book.Label("https://example.com/a"));
    }

    [Fact]
    public void AFullBookDropsTheOldestPassTheShowNoLongerNamesAndNeverOneItDoes()
    {
        var ids = Ids();
        var book = WebPassBook.Empty;
        var addresses = new List<string>();
        for (var i = 0; i < WebPassBook.Max; i++)
        {
            (book, var a) = book.Keep($"https://x/feed?pin={i}", ids, T0.AddMinutes(i));
            addresses.Add(a);
        }
        Assert.Equal(WebPassBook.Max, book.Count);
        Assert.Equal(WebPassBook.Max, addresses.Distinct().Count());

        // The show names the first two: the third-oldest goes.
        Assert.True(WindowAddresses.TryKept(addresses[0], out _, out _, out var id0));
        Assert.True(WindowAddresses.TryKept(addresses[1], out _, out _, out var id1));
        Assert.True(WindowAddresses.TryKept(addresses[2], out _, out _, out var id2));
        var named = new HashSet<string> { id0, id1 };
        var (fuller, added) = book.Keep("https://x/feed?pin=new", ids, T0.AddHours(2), named.Contains);
        Assert.NotEqual("", added);
        Assert.Equal(WebPassBook.Max, fuller.Count);
        Assert.NotNull(fuller.Find(id0));
        Assert.NotNull(fuller.Find(id1));
        Assert.Null(fuller.Find(id2));

        // Every pass named: nothing kept, nothing dropped, and no id drawn for it.
        var (same, none) = fuller.Keep("https://x/feed?pin=another", () => throw new InvalidOperationException("a full book draws no id"), T0.AddHours(3), _ => true);
        Assert.Same(fuller, same);
        Assert.Equal("", none);
    }

    [Fact]
    public void AMergeTakesEveryIncomingPassAndMakesRoomFromPassesNoShowNamesFirst()
    {
        var (main, onMain) = WebPassBook.Empty.Keep(WindowAddressesTests.Window, Sequence("abcdefghijkl"), T0);
        var (standby, own) = WebPassBook.Empty.Keep("https://y/feed?pin=9", Sequence("mnopqrstuvwx"), T0);
        (standby, _) = standby.Keep("https://z/feed?pin=8", Sequence("abcdefghijkl"), T0.AddMinutes(-5));   // the same id, another address

        var merged = standby.Merge(main);
        Assert.Equal(2, merged.Count);
        Assert.Equal(WindowAddressesTests.Window, merged.Resolve(onMain).Url);   // the incoming address wins its id
        Assert.Equal("https://y/feed?pin=9", merged.Resolve(own).Url);

        // Bounded: this book's oldest unnamed pass goes first, then the incoming's oldest unnamed — a pass a show names
        // stands over one no show names, whichever book it came from.
        var ids = Ids(1000);
        var big = WebPassBook.Empty;
        for (var i = 0; i < WebPassBook.Max; i++) (big, _) = big.Keep($"https://m/feed?pin={i}", ids, T0.AddMinutes(i));
        var mine = WebPassBook.Empty;
        (mine, var older) = mine.Keep("https://s/feed?pin=old", Sequence("ssssssssssss"), T0.AddDays(-1));
        (mine, var named) = mine.Keep("https://s/feed?pin=named", Sequence("tttttttttttt"), T0.AddDays(-2));
        var bounded = mine.Merge(big, id => id == "tttttttttttt");
        Assert.Equal(WebPassBook.Max, bounded.Count);
        Assert.False(bounded.Resolve(older).Resolved);
        Assert.True(bounded.Resolve(named).Resolved);                          // named here: it stands
        var oldestIncoming = big.Passes.OrderBy(p => p.KeptUtc).First();
        Assert.Null(bounded.Find(oldestIncoming.Id));                          // the incoming's oldest unnamed pass made the room
        Assert.All(big.Passes.Where(p => p.Id != oldestIncoming.Id), p => Assert.NotNull(bounded.Find(p.Id)));

        // Every pass named on both sides: the incoming stand, and this book's oldest named one gives way as the last resort.
        var full = mine.Merge(big, _ => true);
        Assert.Equal(WebPassBook.Max, full.Count);
        Assert.All(big.Passes, p => Assert.NotNull(full.Find(p.Id)));
        Assert.False(full.Resolve(named).Resolved);
        Assert.False(full.Resolve(older).Resolved);
    }

    [Fact]
    public void TheBookCutToTheIdsAShowNamesCarriesNoOtherPass()
    {
        var (book, a) = WebPassBook.Empty.Keep(WindowAddressesTests.Window, Sequence("abcdefghijkl"), T0);
        (book, var b) = book.Keep("https://y/feed?pin=9", Sequence("mnopqrstuvwx"), T0.AddMinutes(1));
        var all = new HashSet<string>(StringComparer.Ordinal) { "abcdefghijkl", "mnopqrstuvwx", "zzzzzzzzzzzz" };
        Assert.Same(book, book.Only(all));                                     // every entry named: the book itself
        var cut = book.Only(new HashSet<string>(StringComparer.Ordinal) { "mnopqrstuvwx" });
        Assert.Equal(1, cut.Count);
        Assert.False(cut.Resolve(a).Resolved);
        Assert.Equal("https://y/feed?pin=9", cut.Resolve(b).Url);
        Assert.DoesNotContain(WindowAddressesTests.Token, cut.ToJson(indented: false), StringComparison.Ordinal);
        Assert.Equal(0, book.Only(new HashSet<string>(StringComparer.Ordinal)).Count);

        // The ids a text names — a show's JSON as the twin carries it — read as a show's are.
        Assert.Equal(new[] { "abcdefghijkl" }, WebPassMigration.KeptIdsInText("{\"WebUrl\":\"kept://10.0.0.5:9924/ABCDEFGHIJKL\"}"));
        Assert.Empty(WebPassMigration.KeptIdsInText(null));
        Assert.Empty(WebPassMigration.KeptIdsInText("unkept://10.0.0.5:9924"));
    }

    [Fact]
    public void TheFileReadsBackAndAnotherVersionIsNotRead()
    {
        var (book, kept) = WebPassBook.Empty.Keep("https://x/feed?a=1&pin=4321", Sequence("abcdefghijkl"), T0);
        var json = book.ToJson();
        Assert.Contains("\"v\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"keptUtc\": \"2026-10-25T09:12:00Z\"", json, StringComparison.Ordinal);
        Assert.True(WebPassBook.TryParse(json, out var back));
        Assert.Equal("https://x/feed?a=1&pin=4321", back.Resolve(kept).Url);
        Assert.Equal(T0, back.Passes[0].KeptUtc);

        Assert.False(WebPassBook.TryParse("{\"v\":2,\"passes\":[]}", out _));
        Assert.False(WebPassBook.TryParse("not json", out _));
        Assert.False(WebPassBook.TryParse("", out _));
        Assert.True(WebPassBook.TryParse("""
            {"v":1,"passes":[
              {"id":"abcdefghijkl","url":"https://x/feed?pin=1","keptUtc":"2026-10-25T09:12:00Z"},
              {"id":"abcdefghijkl","url":"https://x/feed?pin=2","keptUtc":"2026-10-25T09:13:00Z"},
              {"id":"BAD","url":"https://x/feed?pin=3"},
              {"id":"mnopqrstuvwx","url":"no host"},
              {"id":"mnopqrstuvwy","url":"kept://x/abcdefghijkl"},
              {"id":"mnopqrstuvwz","url":"https://x/feed?pin=4","keptUtc":"whenever"}
            ]}
            """, out var tolerant));
        Assert.Equal(new[] { "mnopqrstuvwz", "abcdefghijkl" }, tolerant.Passes.Select(p => p.Id));   // the first of a repeated id; oldest first
        Assert.Equal("https://x/feed?pin=1", tolerant.Find("abcdefghijkl")!.Url);
    }
}
