using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Web pages driven from the workflow on a live desk: the WEB verbs reaching the page on air or
/// a named page, YouTube through its own player, a cue's web action, the state naming the page
/// and its actions, KEYS → PAGE taking the desk's keyboard, FULL FRAME and the streamlined
/// address, and the pages carrying the new blocks and none of the old browser-window ones.
/// </summary>
public class WebWorkflowAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
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

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    [AvaloniaFact]
    public void TheWireDrivesThePageOnAirOrANamedOneAndYouTubeThroughItsPlayer()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var made = FakePages(services);
            var router = new CommandRouter(services);
            vm.IsSandboxActive = false;

            // Nothing on air yet: the verbs say so instead of doing nothing.
            Assert.StartsWith("ERR", Send(router, "WEB NEXT"));
            Assert.Contains("No web page is on air", Send(router, "WEB NEXT"));

            // A Slides deck on the pattern, a YouTube player as a layer: two pages on the desk.
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Web;
            vm.State.Pattern.Media.WebUrl = "https://docs.google.com/presentation/d/e/2PACX/embed?rm=minimal";
            vm.State.Pattern.Layer1.Enabled = true;
            vm.State.Pattern.Layer1.Source = LayerSource.Web;
            vm.State.Pattern.Layer1.WebUrl = "https://www.youtube-nocookie.com/embed/abc?autoplay=1&controls=0";
            Settle(window);
            var deck = made.Single(p => p.CurrentUrl.Contains("docs.google"));
            var tube = made.Single(p => p.CurrentUrl.Contains("youtube"));

            // The page on air is the pattern's: an action word becomes its key; a chord goes as written.
            Assert.Equal("OK", Send(router, "WEB NEXT"));
            Assert.Equal("ArrowRight", deck.Keys.Last());
            Assert.Equal("OK", Send(router, "WEB KEY present"));
            Assert.Equal("Ctrl+Shift+F5", deck.Keys.Last());
            Assert.Equal("OK", Send(router, "WEB KEY Shift+n"));
            Assert.Equal("Shift+N", deck.Keys.Last());
            Assert.Equal("OK", Send(router, "WEB BLACK"));
            Assert.Equal("b", deck.Keys.Last());
            Assert.Empty(tube.Keys);

            // A named page — by a word of its address — and YouTube's own player for play, mute and restart.
            Assert.Equal("OK", Send(router, "WEB PLAY ON youtube"));
            Assert.Contains("playVideo", tube.Scripts.Last());
            Assert.Equal("OK", Send(router, "WEB KEY restart ON youtube"));
            Assert.Contains("seekTo(0", tube.Scripts.Last());
            Assert.Equal("OK", Send(router, "WEB KEY captions ON youtube"));
            Assert.Equal("c", tube.Keys.Last());
            Assert.StartsWith("ERR", Send(router, "WEB NEXT ON vimeo"));

            // A click in percent, typing, a reload, another address, and a key nobody knows.
            Assert.Equal("OK", Send(router, "WEB CLICK 25 75"));
            Assert.Equal(("up", 0.25f, 0.75f), deck.Events.Last());
            Assert.Equal("down", deck.Events[^2].Kind);
            Assert.Equal("OK", Send(router, "WEB TYPE hello there"));
            Assert.Equal("hello there", deck.Typed.Last());
            Assert.Equal("OK", Send(router, "WEB RELOAD"));
            Assert.Equal(1, deck.Reloads);
            Assert.Contains("neither a key", Send(router, "WEB KEY dance"));
            Assert.StartsWith("ERR", Send(router, "WEB CLICK middle"));

            // A cue's web action runs through the same executor; the state names the page on air and its actions.
            var cue = new CueActionConfig { Kind = CueActionKind.WebKey, Value = "prev" };
            Assert.True(services.Actions.Execute(ShowActions.ToShowAction(cue), new ActionOrigin(OriginKind.Cue, "01.010")).Ok);
            Assert.Equal("ArrowLeft", deck.Keys.Last());
            var state = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("web");
            Assert.Equal("Google Slides", state.GetProperty("service").GetString());
            Assert.Contains(state.GetProperty("actions").EnumerateArray(), a => a.GetProperty("id").GetString() == "present");

            // Another address in the same browser: the page follows, the state reads the page as it is now, the pattern keeps its own.
            Assert.Equal("OK", Send(router, "WEB OPEN example.org/next-deck"));
            Assert.Equal("https://example.org/next-deck", deck.CurrentUrl);
            Assert.Equal("", System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("web").GetProperty("service").GetString());
            Assert.Contains("docs.google", vm.State.Pattern.Media.WebUrl);

            // Nothing web on air any more: the state says so and the verbs refuse again.
            vm.State.Pattern.Media.Source = MediaSource.Image;
            vm.State.Pattern.Layer1.Enabled = false;
            Settle(window);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("web").ValueKind);
            Assert.StartsWith("ERR", Send(router, "WEB NEXT"));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void KeysToPageTakesTheKeyboardFullFrameRewritesTheAddressAndThePagesCarryTheBlocks()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var made = FakePages(services);
            vm.IsSandboxActive = false;
            var look = new LookConfig { Name = "Deck", Hotkey = 5 };
            vm.State.LooksAndCues.Looks.Add(look);

            // No page: the chip springs back and says why.
            vm.KeysToPage = true;
            Assert.False(vm.KeysToPage);
            Assert.Contains("No web page", vm.StatusMessage);

            // A YouTube watch link typed on the Remote & web page goes on as the player alone.
            vm.State.Web.Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
            vm.PutWebPageOnPatternCommand.Execute(null);
            Settle(window);
            Assert.StartsWith("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?", vm.State.Pattern.Media.WebUrl);
            Assert.Contains("https://www.youtube.com/watch?v=dQw4w9WgXcQ", vm.State.Web.SavedUrls);
            var page = made.Single();
            vm.PollNow();
            Assert.Contains("YouTube", vm.WebPresetNote);
            Assert.False(vm.WebCanFullFrame);
            Assert.Contains(vm.WebPageActions, a => a.Id == "play");

            // KEYS → PAGE: F5 (a look's key) and Space (blackout) go to the page, the desk's keys wait; Ctrl+Alt+K gives them back.
            vm.KeysToPage = true;
            Assert.True(vm.KeysToPage);
            window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
            window.KeyRelease(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
            Assert.Equal("F5", page.Keys.Last());
            Assert.Equal(PatternKind.Media, vm.State.Pattern.Kind);          // the look did not fire
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            Assert.Equal("Space", page.Keys.Last());
            Assert.False(vm.State.Blackout);
            window.KeyPress(Key.F5, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F5, null);
            window.KeyRelease(Key.F5, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F5, null);
            Assert.Equal("Ctrl+Shift+F5", page.Keys.Last());
            window.KeyPress(Key.K, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.K, null);
            window.KeyRelease(Key.K, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.K, null);
            Assert.False(vm.KeysToPage);
            var keysBefore = page.Keys.Count;
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            Assert.Equal(keysBefore, page.Keys.Count);
            Assert.True(vm.State.Blackout);                                   // the desk's Space is back
            vm.State.Blackout = false;

            // The action chips drive the page the controls drive; a watch link on the Media page offers FULL FRAME.
            vm.RunWebAction("mute");
            Assert.Contains("isMuted", page.Scripts.Last());
            vm.State.Pattern.Media.WebUrl = "https://www.youtube.com/watch?v=other";
            vm.PollNow();
            Assert.True(vm.WebCanFullFrame);
            vm.WebFullFrameCommand.Execute(null);
            Assert.StartsWith("https://www.youtube-nocookie.com/embed/other?", vm.State.Pattern.Media.WebUrl);

            // The Media page carries the chip and the actions; the Remote & web page has no browser window to open any more.
            vm.SelectPage(Shell.IndexOf("Media"));
            Settle(window);
            Assert.Contains(window.GetVisualDescendants().OfType<ToggleButton>(), t => t.Content as string == "⌨ KEYS → PAGE");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "PLAY / PAUSE");
            vm.SelectPage(Shell.IndexOf("Remote"));
            Settle(window);
            var buttons = window.GetVisualDescendants().OfType<Button>().Select(x => x.Content as string).ToList();
            Assert.Contains("SHOW THIS PAGE ON THE PATTERN", buttons);
            Assert.DoesNotContain("OPEN FULL SCREEN", buttons);
            Assert.DoesNotContain("Windowed", buttons);
        }
        finally
        {
            b.Dispose();
        }
    }
}
