using System.Globalization;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 89: a show that holds a pass is scrubbed on load — every place it names a page rewritten to the kept
/// address, what was kept and what could not be counted for the one journal row — a second load finds nothing left to
/// do, and nothing that is not a page's address (another input's key, a nickname, a line of words) is touched.
/// </summary>
public class WebPassMigrationTests
{
    private static readonly DateTime T0 = new(2026, 10, 25, 9, 12, 0, DateTimeKind.Utc);
    private const string Window = WindowAddressesTests.Window;
    private const string Pin = "https://x/feed?a=1&pin=4321";

    /// <summary>A book the test owns, keeping under ids it hands out in order.</summary>
    private sealed class Book
    {
        private readonly Queue<string> _ids;

        public Book(params string[] ids) => _ids = new Queue<string>(ids);

        public WebPassBook Current { get; set; } = WebPassBook.Empty;

        /// <summary>The ids the show names: a full book drops none of them.</summary>
        public Func<string, bool>? InUse { get; set; }

        public string Keep(string url)
        {
            (Current, var address) = Current.Keep(url, () => _ids.Dequeue(), T0, InUse);
            return address;
        }
    }

    private static ShowState PrivateShow()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Web;
        state.Pattern.Media.WebUrl = Window;
        state.Pattern.Layer1.Enabled = true;
        state.Pattern.Layer1.Source = LayerSource.Web;
        state.Pattern.Layer1.WebUrl = Pin;
        state.Web.SavedUrls.Add(Window);
        state.Web.SavedUrls.Add("https://example.com/plain");

        var other = new ShowState();
        other.Pattern.Kind = PatternKind.Media;
        other.Pattern.Media.Source = MediaSource.Web;
        other.Pattern.Media.WebUrl = "https://hall-b/stage?view=k2m4";
        state.LooksAndCues.Looks.Add(new LookConfig { Name = "Hall B", Json = LookService.Capture(other) });
        return state;
    }

    [Fact]
    public void ThePatternALayerTheSavedPagesAndALookAreRewrittenAndCounted()
    {
        var state = PrivateShow();
        var book = new Book("aaaaaaaaaaaa", "bbbbbbbbbbbb", "cccccccccccc");

        // Four places, three addresses: the window is named by the picture and a saved page.
        Assert.Equal(new WebPassScrub(4, 3, 0), WebPassMigration.Rewrite(state, book.Keep));

        Assert.Equal("kept://10.0.0.5:9924/aaaaaaaaaaaa", state.Pattern.Media.WebUrl);
        Assert.Equal("kept://x/bbbbbbbbbbbb", state.Pattern.Layer1.WebUrl);
        Assert.Equal(new[] { "kept://10.0.0.5:9924/aaaaaaaaaaaa", "https://example.com/plain" }, state.Web.SavedUrls);
        var look = JsonUtil.Deserialize<LookData>(state.LooksAndCues.Looks[0].Json)!;
        Assert.Equal("kept://hall-b/cccccccccccc", look.Pattern.Media.WebUrl);
        Assert.Equal(3, book.Current.Count);
        Assert.Equal(Window, book.Current.Resolve(state.Pattern.Media.WebUrl).Url);

        var file = JsonUtil.Serialize(state);
        Assert.DoesNotContain(WindowAddressesTests.Token, file, StringComparison.Ordinal);
        Assert.DoesNotContain("4321", file, StringComparison.Ordinal);
        Assert.DoesNotContain("k2m4", file, StringComparison.Ordinal);
        Assert.Equal(new HashSet<string> { "aaaaaaaaaaaa", "bbbbbbbbbbbb", "cccccccccccc" }, WebPassMigration.KeptIdsIn(state));

        // The second load: nothing left to keep, nothing changed.
        var before = JsonUtil.Serialize(state);
        var again = WebPassMigration.Rewrite(state, book.Keep);
        Assert.Equal(new WebPassScrub(0, 0, 0), again);
        Assert.False(again.Any);
        Assert.Equal(before, JsonUtil.Serialize(state));
    }

    [Fact]
    public void EveryOtherPlaceAShowNamesAPageIsScrubbedToo()
    {
        var state = new ShowState();
        state.Web.Url = "10.0.0.5:9924/w/" + WindowAddressesTests.Token;           // a bare host, typed
        state.Independent.Add(new OutputAssignment { ScreenId = "s2" });
        state.Independent[0].Pattern.Layer2.WebUrl = Pin;
        state.InputLabels.Add(new InputLabelConfig { Key = "web:" + Pin, Label = "Feed" });
        var stack = new CueStackConfig();
        var cue = new RunCueConfig();
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.WebOpen, Target = "web:" + Pin, Value = Window });
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.StageMessage, Value = "Wi-Fi page: " + Pin + " — ask at the desk" });
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.Note, Value = "open 10.0.0.5:9924/w/" + WindowAddressesTests.Token });   // typed bare in words
        stack.Cues.Add(cue);
        state.Stacks.Add(stack);
        var device = new DeviceConfig();
        device.Triggers.Add(new DeviceTriggerConfig { Match = "BTN1", Command = "WEB OPEN " + Pin + " ON lobby" });
        device.Triggers.Add(new DeviceTriggerConfig { Match = "BTN2", Command = "WEB OPEN 10.0.0.5:9924/w/" + WindowAddressesTests.Token + " ON lobby" });
        state.Interactive.Devices.Add(device);
        const string unrelated = "https://example.com/schedule?day=2";
        state.Web.SavedUrls.Add(Pin);
        state.Web.SavedUrls.Add(unrelated);
        state.Web.SavedUrls.Add(Pin);

        // The pictures first (the feed takes the first id), then the Remote & web page (typed bare, so read as https), the
        // saved pages (two pastes of one address keep one entry), the nicknames, the cues, the devices. Eleven places,
        // three addresses: the bare window in a cue's words and a device's line is the Remote & web page's entry.
        var book = new Book("aaaaaaaaaaaa", "bbbbbbbbbbbb", "cccccccccccc");
        Assert.Equal(new WebPassScrub(11, 3, 0), WebPassMigration.Rewrite(state, book.Keep));

        Assert.Equal("kept://x/aaaaaaaaaaaa", state.Independent[0].Pattern.Layer2.WebUrl);
        Assert.Equal("kept://10.0.0.5:9924/bbbbbbbbbbbb", state.Web.Url);
        Assert.Equal(new[] { unrelated, "kept://x/aaaaaaaaaaaa" }, state.Web.SavedUrls);
        Assert.Equal("web:kept://x/aaaaaaaaaaaa", state.InputLabels[0].Key);
        Assert.Equal("web:kept://x/aaaaaaaaaaaa", cue.Actions[0].Target);
        Assert.Equal("kept://10.0.0.5:9924/cccccccccccc", cue.Actions[0].Value);
        Assert.Equal("Wi-Fi page: kept://x/aaaaaaaaaaaa — ask at the desk", cue.Actions[1].Value);
        Assert.Equal("open kept://10.0.0.5:9924/bbbbbbbbbbbb", cue.Actions[2].Value);
        Assert.Equal("WEB OPEN kept://x/aaaaaaaaaaaa ON lobby", device.Triggers[0].Command);
        Assert.Equal("WEB OPEN kept://10.0.0.5:9924/bbbbbbbbbbbb ON lobby", device.Triggers[1].Command);
        Assert.Equal(3, book.Current.Count);

        var file = JsonUtil.Serialize(state);
        Assert.DoesNotContain(WindowAddressesTests.Token, file, StringComparison.Ordinal);
        Assert.DoesNotContain("4321", file, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatIsNotAPagesAddressIsLeftAsItIs()
    {
        // Another input's key, a nickname a page step names its page by, and words with an '@' or a colon in them: none is
        // a page's address, whatever an address's grammar would make of the '@' — each stays byte for byte, the book empty.
        var state = new ShowState();
        state.InputLabels.Add(new InputLabelConfig { Key = "ndi:MEDIA-PC (OBS@Main)", Label = "OBS" });
        state.InputLabels.Add(new InputLabelConfig { Key = "cap:Cam@1", Label = "Cam 1" });
        state.InputLabels.Add(new InputLabelConfig { Key = "web:https://example.com/schedule?day=2", Label = "Schedule" });
        var stack = new CueStackConfig();
        var cue = new RunCueConfig();
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.WebReload, Target = "Timer @ FOH" });
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.WebKey, Target = "Lobby@FOH", Value = "space" });
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.StageMessage, Value = "Doors 19:30@foyer — mail ops@venue.example" });
        stack.Cues.Add(cue);
        state.Stacks.Add(stack);
        var device = new DeviceConfig();
        device.Triggers.Add(new DeviceTriggerConfig { Match = "BTN1", Command = "WEB RELOAD Timer @ FOH" });
        state.Interactive.Devices.Add(device);
        var before = JsonUtil.Serialize(state);

        var book = new Book("aaaaaaaaaaaa");
        var scrub = WebPassMigration.Rewrite(state, book.Keep);

        Assert.Equal(new WebPassScrub(0, 0, 0), scrub);
        Assert.Equal(before, JsonUtil.Serialize(state));
        Assert.Equal("ndi:MEDIA-PC (OBS@Main)", state.InputLabels[0].Key);
        Assert.Equal("cap:Cam@1", state.InputLabels[1].Key);
        Assert.Equal("Timer @ FOH", cue.Actions[0].Target);
        Assert.Equal("Lobby@FOH", cue.Actions[1].Target);
        Assert.Equal(0, book.Current.Count);
    }

    [Fact]
    public void APageStepNamedByAPrivateAddressTypedBareIsKept()
    {
        // A page step's one-word target that can be nothing but a page: a window typed bare, or a user with a password.
        var state = new ShowState();
        var stack = new CueStackConfig();
        var cue = new RunCueConfig();
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.WebReload, Target = "10.0.0.5:9924/w/" + WindowAddressesTests.Token });
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.WebReload, Target = "admin:pw@10.0.0.6" });
        stack.Cues.Add(cue);
        state.Stacks.Add(stack);

        var book = new Book("aaaaaaaaaaaa", "bbbbbbbbbbbb");
        Assert.Equal(new WebPassScrub(2, 2, 0), WebPassMigration.Rewrite(state, book.Keep));
        Assert.Equal("kept://10.0.0.5:9924/aaaaaaaaaaaa", cue.Actions[0].Target);
        Assert.Equal("kept://10.0.0.6/bbbbbbbbbbbb", cue.Actions[1].Target);
    }

    [Fact]
    public void AHeldPrefixOverAPassIsMadeInert()
    {
        // A held scheme written by hand over a pass is no held address: its unkept form, counted as one that shows black.
        var state = new ShowState();
        state.Pattern.Media.WebUrl = "kept://10.0.0.5:9924/w/" + WindowAddressesTests.Token;
        state.Pattern.Layer1.WebUrl = "unkept://x/feed?pin=4321";
        state.Pattern.Layer2.WebUrl = "KEPT://X/abcdefghijkl";                  // its own form, but for its case: rewritten, not counted
        state.InputLabels.Add(new InputLabelConfig { Key = "web:kept://10.0.0.5:9924/w/" + WindowAddressesTests.Token, Label = "Hall A" });

        var scrub = WebPassMigration.Rewrite(state, new Book().Keep);

        Assert.Equal(new WebPassScrub(4, 0, 2), scrub);
        Assert.Equal("unkept://10.0.0.5:9924", state.Pattern.Media.WebUrl);
        Assert.Equal("unkept://x", state.Pattern.Layer1.WebUrl);
        Assert.Equal("kept://x/abcdefghijkl", state.Pattern.Layer2.WebUrl);
        Assert.Equal("web:unkept://10.0.0.5:9924", state.InputLabels[0].Key);
        var file = JsonUtil.Serialize(state);
        Assert.DoesNotContain(WindowAddressesTests.Token, file, StringComparison.Ordinal);
        Assert.DoesNotContain("4321", file, StringComparison.Ordinal);
    }

    [Fact]
    public void ABookThatCannotKeepLeavesTheUnkeptFormNeverThePass()
    {
        var state = PrivateShow();
        var scrub = WebPassMigration.Rewrite(state, _ => "");
        Assert.Equal(new WebPassScrub(4, 0, 3), scrub);                              // three addresses, none kept
        Assert.True(scrub.Any);
        Assert.Equal("unkept://10.0.0.5:9924", state.Pattern.Media.WebUrl);
        Assert.Equal("unkept://x", state.Pattern.Layer1.WebUrl);
        var file = JsonUtil.Serialize(state);
        Assert.DoesNotContain("4321", file, StringComparison.Ordinal);
        Assert.DoesNotContain("k2m4", file, StringComparison.Ordinal);
        Assert.Empty(WebPassMigration.KeptIdsIn(state));
    }

    [Fact]
    public void AFullBookKeepsWhatItAlreadyHasAndSaysWhatCouldNotBeKept()
    {
        // Sixty-four passes the show still names, one of them the window: the window is kept again under its own entry,
        // the two new addresses cannot be — they are held unkept and the row says so, never 'kept 3'.
        var book = new Book();
        var full = WebPassBook.Empty;
        (full, _) = full.Keep(Window, () => "aaaaaaaaaaaa", T0);
        var ids = WebPassBookTests.Ids(1);
        for (var i = 1; i < WebPassBook.Max; i++) (full, _) = full.Keep(string.Create(CultureInfo.InvariantCulture, $"https://h{i}/feed?pin={i}"), ids, T0);
        Assert.Equal(WebPassBook.Max, full.Count);
        book.Current = full;
        book.InUse = _ => true;

        var state = PrivateShow();
        var scrub = WebPassMigration.Rewrite(state, book.Keep);

        Assert.Equal(new WebPassScrub(4, 1, 2), scrub);
        Assert.Equal("kept://10.0.0.5:9924/aaaaaaaaaaaa", state.Pattern.Media.WebUrl);
        Assert.Equal("unkept://x", state.Pattern.Layer1.WebUrl);
        Assert.Same(full, book.Current);                                            // nothing dropped, nothing added

        var row = WebPassMigration.Row(scrub, T0);
        Assert.Equal("DoneWithWarnings", row.Outcome);
        Assert.Equal("kept 1 private page address on this machine (Stagewatch windows and keyed links); 2 could not be kept and show black — paste them again; "
                     + "older copies in backups/ and the log keep them on this machine — the support bundle hides them", row.Message);
    }

    [Fact]
    public void TheJournalRowSaysHowManyAndWhereOlderCopiesStay()
    {
        var row = WebPassMigration.Row(new WebPassScrub(5, 2, 0), T0);
        Assert.Equal(WebPassMigration.RowKind, row.Kind);
        Assert.Equal("Done", row.Outcome);
        Assert.Equal(T0, row.AtUtc);
        Assert.Equal("kept 2 private page addresses on this machine (Stagewatch windows and keyed links); older copies in backups/ and the log keep them on this machine — the support bundle hides them", row.Message);
        Assert.StartsWith("kept 1 private page address on this machine", WebPassMigration.Words(new WebPassScrub(1, 1, 0)), StringComparison.Ordinal);
        Assert.StartsWith("kept 0 private page addresses on this machine (Stagewatch windows and keyed links); 1 could not be kept and shows black — paste it again;",
            WebPassMigration.Words(new WebPassScrub(1, 0, 1)), StringComparison.Ordinal);
    }
}
