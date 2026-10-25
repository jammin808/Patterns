using System.Security.Cryptography;
using System.Text;
using Patterns.Core.Media;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 89: the private rule read off an address alone, the held forms (kept, unkept), the random id and the words
/// that say a private address in public.
/// </summary>
public class WindowAddressesTests
{
    /// <summary>26 of Stagewatch's window alphabet — a window's pass.</summary>
    internal const string Token = "q3x7m2k9p4abcdefghjk5n6r8t";

    internal const string Window = "http://10.0.0.5:9924/w/" + Token;

    [Theory]
    [InlineData(Window, true)]
    [InlineData("http://10.0.0.5:9924/w/" + Token + "/", true)]
    [InlineData("https://hall-a.local/w/" + Token + "?embed=1", true)]
    [InlineData("10.0.0.5:9924/w/" + Token, true)]                                  // a bare host, as the desk opens it
    [InlineData("http://10.0.0.5:9924/W/Q3X7M2K9P4ABCDEFGHJK5N6R8T", true)]          // case-blind, failing closed
    [InlineData("http://10.0.0.5:9924/w/q3x7m2k9p4abcdefghjk5n6r8", false)]         // 25 symbols
    [InlineData("http://10.0.0.5:9924/w/q3x7m2k9p4abcdefghjk5n6rl", false)]         // an l is not in the alphabet
    [InlineData("http://10.0.0.5:9924/w/q3x7m2k9p4abcdefghjk5n6r8tx", false)]       // 27: another path
    [InlineData("https://hall-a.local/stage?view=k2m4", true)]
    [InlineData("https://x/feed?pin=4321", true)]
    [InlineData("https://x/feed?a=1&PIN=4321", true)]
    [InlineData("https://x/feed?%70in=4321", true)]                                  // a percent-encoded name
    [InlineData("https://x/app#token=abc", true)]
    [InlineData("https://x/app#/route?access_token=abc", true)]
    [InlineData("https://user:pass@x/page", true)]
    [InlineData("https://x/feed?api_key=abc", true)]
    [InlineData("https://www.example.com/schedule?day=2", false)]
    [InlineData("https://x/feed?viewport=1&keyboard=on", false)]                     // whole words only
    [InlineData("https://x/feed?pin", false)]                                        // a word with no value carries nothing
    [InlineData("https://x/@speaker", false)]                                        // an @ in the path is not a user
    [InlineData("example.com", false)]
    [InlineData("", false)]
    [InlineData("file:///C:/show/w/" + Token, false)]
    [InlineData("C:\\show\\schedule.html?pin=1", false)]
    [InlineData("kept://10.0.0.5:9924/abcdefghijkl", false)]
    [InlineData("unkept://10.0.0.5:9924", false)]
    [InlineData("https://h:99999/w/" + Token, true)]                                 // a port no browser opens still fails closed
    [InlineData("ndi:MEDIA-PC (OBS@Main)", false)]                                   // a source's name: "Main)" is no host
    [InlineData("cap:Cam (USB@2)", false)]
    [InlineData("Timer @ FOH", false)]                                               // words: white space in the authority
    [InlineData("https://Timer @ FOH/w/" + Token, false)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42", false)]
    [InlineData("https://bbc.co.uk/news/w/live", false)]
    public void ThePrivateRuleReadsTheAddressAlone(string url, bool expected)
    {
        Assert.Equal(expected, WindowAddresses.LooksPrivate(url));
    }

    [Theory]
    [InlineData("10.0.0.5:9924/w/" + Token, false, true)]
    [InlineData("hall-b.local/stage?view=k2m4&lang=en", false, true)]
    [InlineData("[fe80::1]:9924/w/" + Token, false, true)]
    [InlineData("admin:pw@10.0.0.5", false, false)]                                // a user is not enough in words…
    [InlineData("admin:pw@10.0.0.5", true, true)]                                  // …where the token can be nothing but a page, a password is
    [InlineData("Lobby@FOH", true, false)]                                         // a nickname: a user with no password
    [InlineData("19:30@foyer", false, false)]
    [InlineData("ops@venue.example", true, false)]
    [InlineData("Timer @ FOH", true, false)]
    [InlineData("https://x/feed?pin=1", true, false)]                              // a scheme: not bare (RewriteText's)
    [InlineData("C:\\show\\schedule.html?pin=1", true, false)]                     // a file
    [InlineData("/w/" + Token, true, false)]                                       // no host
    [InlineData("web:10.0.0.5:9924/w/" + Token, true, false)]                      // a key: its address after "web:" is the token
    [InlineData("example.com/schedule?day=2", true, false)]
    public void ABareTokenIsPrivateOnlyBeyondDoubt(string token, bool userWithPassword, bool expected)
    {
        Assert.Equal(expected, WindowAddresses.LooksPrivateBare(token, userWithPassword));
    }

    [Fact]
    public void AnAddressTypedBareInWordsIsFoundAndNothingElse()
    {
        static string Mark(string url) => "<" + url + ">";

        Assert.Equal("WEB OPEN <10.0.0.5:9924/w/" + Token + "> ON lobby", WindowAddresses.RewriteBareText("WEB OPEN 10.0.0.5:9924/w/" + Token + " ON lobby", Mark));
        Assert.Equal("open (<hall-b.local/stage?view=k2m4>).", WindowAddresses.RewriteBareText("open (hall-b.local/stage?view=k2m4).", Mark));
        Assert.Equal("web:<10.0.0.5:9924/w/" + Token + ">", WindowAddresses.RewriteBareText("web:10.0.0.5:9924/w/" + Token, Mark));
        // JSON-escaped: the token is handed over with its escapes undone, the quote that ends it stays.
        Assert.Equal("{\"c\":\"<x/feed?a=1&pin=4321>\"}", WindowAddresses.RewriteBareText("{\"c\":\"x/feed?a=1\\u0026pin=4321\"}", Mark));

        // Words, nicknames, a time, a mail address, an address with its scheme: the very same text.
        const string words = "Doors 19:30@foyer, mail ops@venue.example, Timer @ FOH, Lobby@FOH and https://x/feed?pin=1 = 2";
        Assert.Same(words, WindowAddresses.RewriteBareText(words, Mark));
        Assert.Equal("", WindowAddresses.RewriteBareText(null, Mark));
    }

    [Fact]
    public void AWindowPathIsAStagewatchWindow()
    {
        Assert.True(WindowAddresses.IsStagewatchWindowUrl(Window));
        Assert.False(WindowAddresses.IsStagewatchWindowUrl("https://x/feed?pin=4321"));
        Assert.False(WindowAddresses.IsStagewatchWindowUrl("kept://10.0.0.5:9924/abcdefghijkl"));
    }

    [Fact]
    public void HeldFormsCarryTheHostAndNeverThePass()
    {
        Assert.Equal("unkept://10.0.0.5:9924", WindowAddresses.Unkept(Window));
        Assert.Equal("unkept://x", WindowAddresses.Unkept("https://user:secret@X/feed?pin=4321"));
        Assert.Equal("unkept://10.0.0.5:9924", WindowAddresses.Held(Window));
        Assert.Equal("https://example.com/a?b=1", WindowAddresses.Held("https://example.com/a?b=1"));
        Assert.Equal("kept://10.0.0.5:9924/abcdefghijkl", WindowAddresses.Held("kept://10.0.0.5:9924/abcdefghijkl"));

        Assert.Equal("kept://10.0.0.5:9924/abcdefghijkl", WindowAddresses.KeptFor(Window, "abcdefghijkl"));
        Assert.Equal("kept://x/abcdefghijkl", WindowAddresses.KeptFor("https://user:secret@x/feed?pin=4321", "abcdefghijkl"));
        Assert.Equal("kept://10.0.0.5:9924/abcdefghijkl", WindowAddresses.Kept("10.0.0.5", 9924, "abcdefghijkl"));
        Assert.Equal("kept://[::1]:9924/abcdefghijkl", WindowAddresses.Kept("::1", 9924, "abcdefghijkl"));
        Assert.Equal("kept://hall-a/abcdefghijkl", WindowAddresses.Kept("Hall-A", 0, "abcdefghijkl"));
        Assert.Throws<ArgumentException>(() => WindowAddresses.Kept("x", 0, "ABC"));

        Assert.True(WindowAddresses.TryKept("kept://[::1]:9924/abcdefghijkl", out var host, out var port, out var id));
        Assert.Equal(("[::1]", 9924, "abcdefghijkl"), (host, port, id));
        Assert.True(WindowAddresses.TryKept("kept://hall-a/abcdefghij27", out host, out port, out id));
        Assert.Equal(("hall-a", 0, "abcdefghij27"), (host, port, id));
        Assert.False(WindowAddresses.TryKept("kept://10.0.0.5:9924/abcdefghijk", out _, out _, out _));      // eleven
        Assert.False(WindowAddresses.TryKept("kept://10.0.0.5:9924/abcdefghijk1", out _, out _, out _));     // a 1 is not base32
        Assert.False(WindowAddresses.TryKept("kept:///abcdefghijkl", out _, out _, out _));
        Assert.False(WindowAddresses.TryKept("unkept://10.0.0.5:9924", out _, out _, out _));

        // The fence rebuilds every held form from what it names: a pass written after a held prefix never passes it.
        Assert.Equal("unkept://10.0.0.5:9924", WindowAddresses.Held("kept://10.0.0.5:9924/w/" + Token));
        Assert.Equal("unkept://x", WindowAddresses.Held("unkept://x/feed?pin=4321"));
        Assert.Equal("unkept://10.0.0.5:9924", WindowAddresses.Held("kept://admin:pw@10.0.0.5:9924/abcdefghijkl"));
        Assert.Equal("unkept://x", WindowAddresses.Held("unkept://user:secret@x"));
        Assert.Equal("kept://x:80/abcdefghijkl", WindowAddresses.Held("KEPT://X:080/abcdefghijkl"));
        Assert.Equal("kept://x/abcdefghijkl", WindowAddresses.Held("kept://x:/abcdefghijkl"));
        Assert.Equal("unkept://", WindowAddresses.Held("unkept://Main)"));
        Assert.Equal("unkept://10.0.0.5:9924", WindowAddresses.Secure("kept://10.0.0.5:9924/w/" + Token, _ => throw new InvalidOperationException("a held address is not kept again")));
        Assert.Equal("unkept://10.0.0.5:9924", WindowAddresses.PublicForm("kept://10.0.0.5:9924/w/" + Token));
        const string canonical = "kept://[::1]:9924/abcdefghijkl";
        Assert.Same(canonical, WindowAddresses.Held(canonical));

        Assert.True(WindowAddresses.IsHeld("KEPT://x/abcdefghijkl"));
        Assert.True(WindowAddresses.IsHeld("unkept://x"));
        Assert.False(WindowAddresses.IsHeld(Window));
        Assert.True(InputKeys.IsHeldWeb("web:kept://x/abcdefghijkl"));
        Assert.True(InputKeys.IsHeldWeb("web:unkept://x"));
        Assert.False(InputKeys.IsHeldWeb("web:https://example.com"));
        Assert.False(InputKeys.IsHeldWeb("vid:kept://x/abcdefghijkl"));
    }

    [Fact]
    public void ThePublicFormIsTheSchemeAndTheAuthorityWithoutItsUser()
    {
        Assert.Equal("http://10.0.0.5:9924", WindowAddresses.PublicForm(Window));
        Assert.Equal("https://x", WindowAddresses.PublicForm("https://user:pass@X/page?pin=1"));
        Assert.Equal("https://example.com/a?b=1", WindowAddresses.PublicForm("https://example.com/a?b=1"));
        Assert.Equal("kept://x/abcdefghijkl", WindowAddresses.PublicForm("kept://x/abcdefghijkl"));

        // A held address's short name is its host — a label, a status line, the Library's tile.
        Assert.Equal("10.0.0.5", WebAddress.ShortName("kept://10.0.0.5:9924/abcdefghijkl"));
        Assert.Equal("10.0.0.5", WebAddress.ShortName("unkept://10.0.0.5:9924"));
        Assert.Equal("[::1]", WebAddress.ShortName("kept://[::1]:9924/abcdefghijkl"));
        Assert.Equal("10.0.0.5", WebAddress.ShortName(Window));
    }

    [Fact]
    public void AnIdIsTwelveBase32SymbolsFromTheBytesHandedIn()
    {
        Assert.Equal("abcdefghijkl", WindowAddresses.NewId(n => Enumerable.Range(0, n).Select(i => (byte)i).ToArray()));
        Assert.Equal("777777777777", WindowAddresses.NewId(n => Enumerable.Repeat((byte)31, n).ToArray()));
        Assert.Equal("aaaaaaaaaaaa", WindowAddresses.NewId(n => Enumerable.Repeat((byte)32, n).ToArray()));   // five bits of each byte
        Assert.Throws<ArgumentException>(() => WindowAddresses.NewId(_ => new byte[3]));

        var fresh = WindowAddresses.NewId();
        Assert.True(WindowAddresses.IsId(fresh));
        Assert.False(WindowAddresses.IsId("abcdefghijk"));
        Assert.False(WindowAddresses.IsId("abcdefghijk0"));
    }

    [Fact]
    public void TheIdIsNotAFunctionOfTheAddress()
    {
        // Two fresh books keep the same address under different ids: nothing published can be worked back to a short PIN.
        const string url = "https://x/feed?pin=4321";
        var one = WebPassBook.Empty.Keep(url, WindowAddresses.NewId, DateTime.UtcNow).Address;
        var two = WebPassBook.Empty.Keep(url, WindowAddresses.NewId, DateTime.UtcNow).Address;
        Assert.NotEqual(one, two);
        Assert.True(WindowAddresses.TryKept(one, out _, out _, out var id1));
        Assert.True(WindowAddresses.TryKept(two, out _, out _, out var id2));

        // And no id is a window of the address's hash.
        var hash = Base32(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        for (var i = 0; i + WindowAddresses.IdLength <= hash.Length; i++)
        {
            Assert.NotEqual(hash.Substring(i, WindowAddresses.IdLength), id1);
            Assert.NotEqual(hash.Substring(i, WindowAddresses.IdLength), id2);
        }
        Assert.DoesNotContain("4321", one, StringComparison.Ordinal);
    }

    [Fact]
    public void SecureKeepsAPrivateAddressAndLeavesEveryOtherAsTyped()
    {
        var kept = new List<string>();
        string Keep(string url)
        {
            kept.Add(url);
            return "kept://10.0.0.5:9924/abcdefghijkl";
        }

        Assert.Equal("kept://10.0.0.5:9924/abcdefghijkl", WindowAddresses.Secure(Window, Keep));
        Assert.Equal("kept://10.0.0.5:9924/abcdefghijkl", WindowAddresses.Secure("10.0.0.5:9924/w/" + Token, Keep));
        Assert.Equal(new[] { Window, "https://10.0.0.5:9924/w/" + Token }, kept);
        Assert.Equal("example.com", WindowAddresses.Secure("example.com", Keep));
        Assert.Equal("kept://x/abcdefghijkl", WindowAddresses.Secure("kept://x/abcdefghijkl", Keep));
        Assert.Equal(2, kept.Count);

        // A book that cannot keep it, or answers anything but a kept address: the unkept form — never the pass.
        Assert.Equal("unkept://x", WindowAddresses.Secure("https://x/feed?pin=4321", _ => ""));
        Assert.Equal("unkept://x", WindowAddresses.Secure("https://x/feed?pin=4321", u => u));
    }

    [Fact]
    public void RedactedTextSaysEachPrivateAddressInPublic()
    {
        Assert.Equal("Web page failed: http://10.0.0.5:9924 (private address): net::ERR_CONNECTION_REFUSED",
            WindowAddresses.RedactText($"Web page failed: {Window}: net::ERR_CONNECTION_REFUSED"));
        Assert.Equal("open http://10.0.0.5:9924 (private address).", WindowAddresses.RedactText($"open {Window}."));
        Assert.Equal("two: https://x (private address) and http://10.0.0.5:9924 (private address); one plain https://example.com/a?b=1",
            WindowAddresses.RedactText($"two: https://x/feed?pin=4321 and {Window}; one plain https://example.com/a?b=1"));

        // JSON-escaped once (a journal row) and twice (a look inside the settings): the '&' written \u0026 is still read.
        var once = "{\"Message\":\"WEB OPEN https://x/feed?a=1\\u0026pin=4321 ON lobby\"}";
        Assert.Equal("{\"Message\":\"WEB OPEN https://x (private address) ON lobby\"}", WindowAddresses.RedactText(once));
        var twice = "\"Json\": \"{\\u0022WebUrl\\u0022: \\u0022https://x/feed?a=1\\\\u0026pin=4321\\u0022}\"";
        var redacted = WindowAddresses.RedactText(twice);
        Assert.DoesNotContain("4321", redacted, StringComparison.Ordinal);
        Assert.Contains("https://x (private address)\\u0022}", redacted, StringComparison.Ordinal);

        // Nothing private: the very same text.
        const string plain = "nothing here but https://example.com/schedule?day=2 and web:kept://x/abcdefghijkl";
        Assert.Equal(plain, WindowAddresses.RedactText(plain));
        Assert.Equal("", WindowAddresses.RedactText(null));

        // An address typed bare has no scheme to say it by: its pass alone is masked.
        Assert.Equal($"hall-b.local/stage?view={Secrets.Mask}&lang=en and 10.0.0.5:9924/w/{Secrets.Mask}",
            WindowAddresses.RedactText("hall-b.local/stage?view=k2m4&lang=en and 10.0.0.5:9924/w/" + Token));
        Assert.Equal($"\"WebUrl\": \"x/feed?a=1\\u0026pin={Secrets.Mask}\"", WindowAddresses.RedactText("\"WebUrl\": \"x/feed?a=1\\u0026pin=4321\""));
    }

    [Fact]
    public void RedactionReadsALineAtATimeAndNeverSendsALineRaw()
    {
        // Many lines: each is read alone, the line breaks stay where they were.
        var text = string.Join("\r\n", Enumerable.Range(0, 2000).Select(i => i % 100 == 0 ? $"open {Window} now" : "frame drew in 4.2 ms — https://example.com/schedule?day=2"));
        var said = WindowAddresses.RedactText(text);
        Assert.DoesNotContain(Token, said, StringComparison.Ordinal);
        Assert.Equal(text.Split('\n').Length, said.Split('\n').Length);
        Assert.Equal(20, said.Split('\n').Count(l => l == "open http://10.0.0.5:9924 (private address) now\r"));

        // One hostile line far longer than any log writes, full of bare window paths: masked or withheld, never raw, never a throw.
        var hostile = string.Concat(Enumerable.Repeat("/w/" + Token + " ", 200_000));
        var redacted = WindowAddresses.RedactText("before\n" + hostile + "\nafter");
        Assert.DoesNotContain(Token, redacted, StringComparison.Ordinal);
        Assert.StartsWith("before\n", redacted, StringComparison.Ordinal);
        Assert.EndsWith("\nafter", redacted, StringComparison.Ordinal);

        // A held address out of its own form is said in that form; one in its own form is left alone.
        Assert.Equal("web:unkept://10.0.0.5:9924 and web:kept://x/abcdefghijkl", WindowAddresses.RedactText("web:kept://10.0.0.5:9924/w/" + Token + " and web:kept://x/abcdefghijkl"));
    }

    [Fact]
    public void TheFenceAllocatesNothingForAnAddressWithNoPassOrOneAlreadyHeld()
    {
        // The renderer asks every frame: an address with no pass, and a held one in its own form, cost no allocation.
        string[] addresses =
        {
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42",
            "https://bbc.co.uk/news/live",
            "https://example.com/schedule?day=2&lang=en",
            "kept://10.0.0.5:9924/abcdefghijkl",
            "kept://[::1]:9924/abcdefghijkl",
            "unkept://10.0.0.5:9924",
        };
        var held = 0;
        for (var i = 0; i < 50; i++)
        {
            foreach (var a in addresses) held += WindowAddresses.LooksPrivate(a) || !ReferenceEquals(WindowAddresses.Held(a), a) ? 1 : 0;
        }
        Assert.Equal(0, held);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++)
        {
            foreach (var a in addresses) held += WindowAddresses.LooksPrivate(a) || !ReferenceEquals(WindowAddresses.Held(a), a) ? 1 : 0;
        }
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
        Assert.Equal(0, held);
    }

    private static string Base32(byte[] bytes)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = ((buffer << 8) | b) & 0xFFFF;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(WindowAddresses.IdAlphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        return sb.ToString();
    }
}
