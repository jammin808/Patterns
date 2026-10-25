using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 89: a held page in a browser source — it resolves only at navigation, from the book handed in; its picture is
/// black until the page is up, and a Stagewatch window's until its shell has been told what to show; the shell's word is
/// taken only from the origin the held address resolved to; the words never name the real address.
/// </summary>
public class HeldPageTests
{
    private const string Token = WindowAddressesTests.Token;
    private const string Window = "http://10.0.0.5:9924/w/" + Token;
    private const string KeptWindow = "kept://10.0.0.5:9924/abcdefghijkl";
    private const string KeptPin = "kept://x/mnopqrstuvwx";
    private const string Pin = "https://x/feed?pin=4321";

    private static WebPassResolution Book(string address) => address switch
    {
        KeptWindow => new WebPassResolution(Window, ""),
        KeptPin => new WebPassResolution(Pin, ""),
        _ when WindowAddresses.IsHeld(address) => new WebPassResolution(null, WebPassBook.NotHereWhy),
        _ => new WebPassResolution(address, ""),
    };

    private static string Shell(int rev, string link = "ok") => $"{{\"type\":\"patterns-shell\",\"rev\":{rev},\"link\":\"{link}\",\"showing\":\"timer\",\"hold\":false,\"loading\":\"\",\"error\":\"\"}}";

    private static void NamesNoPass(string words)
    {
        Assert.DoesNotContain(Token, words, StringComparison.Ordinal);
        Assert.DoesNotContain("4321", words, StringComparison.Ordinal);
    }

    [Fact]
    public void APageThatIsNotHeldOpensAsItIsAndIsNeverWithheld()
    {
        var page = new HeldPage(Book);
        Assert.Equal("https://example.com/schedule", page.Open("https://example.com/schedule"));
        Assert.False(page.IsHeld);
        Assert.False(page.Withheld);
        Assert.Equal("", page.Why);
        page.Completed(false, "ConnectionAborted");
        Assert.False(page.Withheld);                                                   // a public page's failure is the browser's to show, as before
        Assert.False(page.Accept("https://example.com/", Shell(3)));                     // and no page that is not held speaks for a shell
    }

    [Fact]
    public void AHeldPageThisMachineCannotOpenGoesNowhereAndIsBlackWithTheBooksWords()
    {
        var page = new HeldPage(Book);
        Assert.Null(page.Open("kept://10.0.0.5:9924/zzzzzzzzzzzz"));
        Assert.True(page.IsHeld);
        Assert.Equal("kept://10.0.0.5:9924/zzzzzzzzzzzz", page.Address);
        Assert.True(page.Withheld);
        Assert.Equal(WebPassBook.NotHereWhy, page.Why);
        page.Completed(true);                                                          // the empty page behind the black finishing changes nothing
        Assert.True(page.Withheld);

        // An unkept address resolves nowhere either; a held prefix over a pass is rebuilt before anything reads it.
        Assert.Null(page.Open("kept://10.0.0.5:9924/w/" + Token));
        Assert.Equal("unkept://10.0.0.5:9924", page.Address);
        NamesNoPass(page.Address + " " + page.Why);
    }

    [Fact]
    public void AKeptPageIsBlackUntilItsNavigationLandsAndBlackAgainWhenItFails()
    {
        var page = new HeldPage(Book);
        Assert.Equal(Pin, page.Open(KeptPin));                                           // the browser alone is sent the real address
        Assert.Equal(KeptPin, page.Address);                                             // every reader keeps the held one
        Assert.False(page.IsShell);
        Assert.True(page.Withheld);
        Assert.Equal(HeldPage.OpeningWhy, page.Why);

        page.Completed(true);
        Assert.False(page.Withheld);
        Assert.Equal("", page.Why);

        page.Completed(false, "HostNameNotResolved");
        Assert.True(page.Withheld);
        Assert.Contains("did not open (HostNameNotResolved)", page.Why, StringComparison.Ordinal);
        NamesNoPass(page.Why);
        Assert.DoesNotContain("x/feed", page.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void AStagewatchWindowIsBlackUntilItsShellIsAssignedAndAgainOnceItIsWithdrawn()
    {
        var page = new HeldPage(Book);
        Assert.Equal(Window, page.Open(KeptWindow));
        Assert.True(page.IsShell);
        page.Completed(true);
        Assert.True(page.Withheld);                                                      // up, but the shell's holding page is Stagewatch's, not the air's
        Assert.Equal(HeldPage.UnassignedWhy, page.Why);

        Assert.True(page.Accept("http://10.0.0.5:9924/w/" + Token, Shell(0)));
        Assert.True(page.Withheld);                                                      // rev 0: nothing said yet

        Assert.True(page.Accept("http://10.0.0.5:9924/w/" + Token, Shell(4)));
        Assert.False(page.Withheld);
        Assert.Equal(4, page.Shell.Rev);
        Assert.True(page.Shell.Assigned);

        Assert.True(page.Accept("http://10.0.0.5:9924/w/" + Token, Shell(4, "lost")));
        Assert.False(page.Withheld);                                                     // a lost link is the shell's own loss page, the operator's choice there

        Assert.True(page.Accept("http://10.0.0.5:9924/w/" + Token, Shell(4, "gone")));
        Assert.True(page.Withheld);
        Assert.Equal(HeldPage.GoneWhy, page.Why);

        // A new navigation forgets everything the last one learned.
        page.Open(KeptWindow);
        Assert.Equal(default, page.Shell);
        Assert.Equal(HeldPage.OpeningWhy, page.Why);
    }

    [Fact]
    public void TheShellsWordIsTakenOnlyFromItsOwnOriginAndInTheObserversShape()
    {
        var page = new HeldPage(Book);
        page.Open(KeptWindow);
        page.Completed(true);

        Assert.False(page.Accept("http://10.0.0.6:9924/w/" + Token, Shell(5)));          // another host
        Assert.False(page.Accept("http://10.0.0.5:9925/w/" + Token, Shell(5)));          // another port
        Assert.False(page.Accept("https://10.0.0.5:9924/w/" + Token, Shell(5)));         // another scheme
        Assert.False(page.Accept(null, Shell(5)));
        Assert.False(page.Accept("http://10.0.0.5:9924/", "{\"type\":\"other\",\"rev\":5}"));
        Assert.False(page.Accept("http://10.0.0.5:9924/", "{\"type\":\"patterns-shell\",\"rev\":-1}"));
        Assert.False(page.Accept("http://10.0.0.5:9924/", "{\"type\":\"patterns-shell\",\"rev\":\"5\"}"));
        Assert.False(page.Accept("http://10.0.0.5:9924/", "not json"));
        Assert.True(page.Withheld);

        Assert.True(page.Accept("HTTP://10.0.0.5:9924/w/" + Token, Shell(5)));          // the origin compared as a browser compares it
        Assert.False(page.Withheld);

        // A kept page that is not a window takes no shell word at all.
        var plain = new HeldPage(Book);
        plain.Open(KeptPin);
        Assert.False(plain.Accept("https://x/feed", Shell(5)));
    }

    [Fact]
    public void TheShellStateReadsTheObserversMessageAndBelievesNoMoreThanItsBound()
    {
        Assert.True(WebShellState.TryParse(Shell(7, "lost"), out var state));
        Assert.Equal(new WebShellState(7, "lost", "timer", false, "", ""), state);
        Assert.True(state.Assigned);
        Assert.False(state.Gone);

        Assert.True(WebShellState.TryParse("{\"type\":\"patterns-shell\",\"rev\":2,\"link\":\"gone\",\"error\":\"" + new string('e', 500) + "\"}", out var gone));
        Assert.True(gone.Gone);
        Assert.False(gone.Assigned);
        Assert.Equal(WebShellState.MaxField, gone.Error.Length);

        Assert.False(WebShellState.TryParse("", out _));
        Assert.False(WebShellState.TryParse("[1]", out _));
        Assert.False(WebShellState.TryParse("{\"type\":\"patterns-shell\"}", out _));
        Assert.False(WebShellState.TryParse("{\"type\":\"patterns-shell\",\"rev\":1,\"x\":\"" + new string('x', 5000) + "\"}", out _));
    }

    [Fact]
    public void TheObserverSpeaksOnlyFromAWindowsTopDocumentInTheShapeTheHostReads()
    {
        var script = HeldPage.ObserverScript;
        Assert.Contains("window.top !== window", script, StringComparison.Ordinal);
        Assert.Contains("[a-km-np-z2-9]{26}", script, StringComparison.Ordinal);       // the window alphabet: no l, o, 0 or 1
        Assert.Contains("'" + WebShellState.MessageType + "'", script, StringComparison.Ordinal);
        foreach (var attribute in new[] { "data-rev", "data-link", "data-showing", "data-hold", "data-error" })
        {
            Assert.Contains(attribute, script, StringComparison.Ordinal);
        }
        var windowClass = new System.Text.RegularExpressions.Regex("^[a-km-np-z2-9]$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        foreach (var c in "abcdefghijklmnopqrstuvwxyz0123456789")
        {
            Assert.Equal(WindowAddresses.WindowAlphabet.Contains(c, StringComparison.Ordinal), windowClass.IsMatch(new string(c, 1)));
        }
    }

    [Fact]
    public void BackOntoAnAddressAHeldPageResolvedToIsHeldAgainAndNeverShownRaw()
    {
        var page = new HeldPage(Book);
        Assert.Null(page.HeldAgain(Window));                                             // nothing resolved yet: nothing to hold again
        Assert.Equal(Window, page.Open(KeptWindow));
        Assert.Null(page.HeldAgain(Window));                                             // the held page's own navigation goes on
        Assert.Null(page.HeldAgain(Window + "#top"));                                    // a fragment is the same document

        // A WEB OPEN sends the page to a public address: it is no longer held, and BACK onto the window holds it again.
        Assert.Equal("https://example.com/", page.Open("https://example.com/"));
        Assert.False(page.IsHeld);
        Assert.Null(page.HeldAgain("https://example.com/"));
        Assert.Equal(KeptWindow, page.HeldAgain(Window));
        Assert.Equal(KeptWindow, page.HeldAgain("HTTP://10.0.0.5:9924/w/" + Token));      // as the browser may spell it

        // From another held page, BACK onto the first is the first again, not the second's.
        Assert.Equal(Pin, page.Open(KeptPin));
        Assert.Equal(KeptWindow, page.HeldAgain(Window));
        Assert.Null(page.HeldAgain(Pin));
        Assert.Null(page.HeldAgain("https://elsewhere.example/"));                        // a page this one never resolved to goes on
        Assert.Null(page.HeldAgain("not an address"));
    }

    [Fact]
    public void AHeldPagesSoundIsGrantedToAnOriginThatNamesNoUserOrPass()
    {
        Assert.Equal("http://10.0.0.5:9924", WebAudioRoute.OriginOf(Window));
        Assert.Equal("https://hall-b.local", WebAudioRoute.OriginOf("https://crew:secret@hall-b.local/stage?view=4321"));
        Assert.Equal("https://x", WebAudioRoute.OriginOf(Pin));
    }

    [Fact]
    public void ACueThatOpensAHeldAddressThisMachineCannotOpenIsWorthALookNotARefusal()
    {
        var state = new Patterns.Core.Model.ShowState();
        var stack = new Patterns.Core.Model.CueStackConfig();
        var cue = new Patterns.Core.Model.RunCueConfig { Number = "1", Name = "Lobby" };
        cue.Actions.Add(new Patterns.Core.Model.CueActionConfig { Kind = Patterns.Core.Model.ShowActionKind.WebOpen, Value = "kept://10.0.0.9:9924/zzzzzzzzzzzz" });
        stack.Cues.Add(cue);

        var report = CueValidator.Validate(state, stack, new CueValidationContext { ResolveHeld = Book });
        Assert.False(report.IsBroken(cue.Id));
        Assert.True(report.Warnings.TryGetValue(cue.Id, out var warning));
        Assert.Contains("10.0.0.9's private address does not open on this machine", warning, StringComparison.Ordinal);
        Assert.Contains(WebPassBook.NotHereWhy, warning, StringComparison.Ordinal);

        cue.Actions[0].Value = KeptWindow;                                               // one this machine keeps: nothing to say
        Assert.False(CueValidator.Validate(state, stack, new CueValidationContext { ResolveHeld = Book }).Warnings.ContainsKey(cue.Id));
        cue.Actions[0].Value = "kept://10.0.0.9:9924/zzzzzzzzzzzz";                      // a build that cannot tell leaves it alone
        Assert.False(CueValidator.Validate(state, stack).Warnings.ContainsKey(cue.Id));
    }
}
