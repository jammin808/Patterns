using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The native-player path for a page's video (round 68.6): what applies, what the tool is asked, how its answer is read, where it is looked for, and the locator's rewrite.</summary>
public class WebVideoResolverTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OnlyAVideoPageWhoseLookAsksForItGoesToTheNativePlayer()
    {
        Assert.True(WebVideoResolver.Applies(PageService.YouTube, WebPlayVia.NativePlayer));
        Assert.True(WebVideoResolver.Applies(PageService.Vimeo, WebPlayVia.NativePlayer));
        Assert.False(WebVideoResolver.Applies(PageService.YouTube, WebPlayVia.Browser));
        Assert.False(WebVideoResolver.Applies(PageService.Page, WebPlayVia.NativePlayer));
        Assert.False(WebVideoResolver.Applies(PageService.GoogleSlides, WebPlayVia.NativePlayer));
    }

    [Fact]
    public void TheToolIsAskedForAddressesOnlyOneArgumentAtATime()
    {
        var args = WebVideoResolver.ArgumentList("https://www.youtube.com/watch?v=abc&list=x");
        Assert.Equal("-g", args[0]);
        Assert.Contains("-f", args);
        Assert.Contains(WebVideoResolver.Format, args);
        Assert.Contains("--no-playlist", args);
        Assert.Equal("--", args[^2]);
        Assert.Equal("https://www.youtube.com/watch?v=abc&list=x", args[^1]);                             // after the -- so an address can never be read as an option
        Assert.StartsWith("best[height<=1080][ext=mp4][acodec!=none]", WebVideoResolver.Format);          // one file with sound first
    }

    [Fact]
    public void TheAnswerIsThePictureThenTheSoundWithTheAddressesOwnExpiry()
    {
        var expire = new DateTimeOffset(Now.AddHours(5)).ToUnixTimeSeconds();
        var stdout = $"https://rr3.googlevideo.com/videoplayback?expire={expire}&itag=22\n";
        Assert.True(WebVideoResolver.TryParse(stdout, Now, out var one));
        Assert.Equal(stdout.Trim(), one.VideoUrl);
        Assert.False(one.HasSeparateAudio);
        Assert.Equal(Now.AddHours(5), one.ExpiresUtc);
        Assert.False(one.Expired(Now));
        Assert.True(one.Expired(Now.AddHours(6)));

        Assert.True(WebVideoResolver.TryParse("https://cdn.example/v.mp4\r\nhttps://cdn.example/a.m4a\r\n", Now, out var two));
        Assert.Equal("https://cdn.example/v.mp4", two.VideoUrl);
        Assert.Equal("https://cdn.example/a.m4a", two.AudioUrl);
        Assert.True(two.HasSeparateAudio);
        Assert.Equal(Now + WebVideoResolver.DefaultTtl, two.ExpiresUtc);                                    // no stamp: assumed

        Assert.False(WebVideoResolver.TryParse("", Now, out _));
        Assert.False(WebVideoResolver.TryParse("WARNING: nothing\n", Now, out _));
        Assert.False(WebVideoResolver.TryParse(null, Now, out _));
        Assert.Null(WebVideoResolver.ExpiryOf("https://x/y?expire=1", Now));                               // a stamp already passed
        Assert.Null(WebVideoResolver.ExpiryOf("https://x/y", Now));
    }

    [Fact]
    public void TheToolsComplaintComesBackInOneLine()
    {
        Assert.Equal("ERROR: [youtube] abc: Video unavailable", WebVideoResolver.FailureWords("WARNING: something\nERROR: [youtube] abc: Video unavailable\n"));
        Assert.Equal("WARNING: only this", WebVideoResolver.FailureWords("\nWARNING: only this\n"));
        Assert.Equal("yt-dlp gave nothing back", WebVideoResolver.FailureWords(""));
        Assert.EndsWith("…", WebVideoResolver.FailureWords("ERROR: " + new string('x', 300)));
    }

    [Fact]
    public void TheToolIsLookedForWhereTheOperatorSaidThenBesideTheDeskThenOnPath()
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        bool Exists(string p) => files.Contains(p);
        var sep = Path.DirectorySeparatorChar;
        Assert.Null(WebVideoResolver.Find("", $"{sep}app", $"{sep}usr{sep}bin{Path.PathSeparator}{sep}opt{sep}tools", Exists));
        files.Add($"{sep}opt{sep}tools{sep}yt-dlp.exe");
        Assert.Equal($"{sep}opt{sep}tools{sep}yt-dlp.exe", WebVideoResolver.Find("", $"{sep}app", $"{sep}usr{sep}bin{Path.PathSeparator}{sep}opt{sep}tools", Exists));
        files.Add($"{sep}app{sep}yt-dlp.exe");
        Assert.Equal($"{sep}app{sep}yt-dlp.exe", WebVideoResolver.Find("", $"{sep}app", $"{sep}opt{sep}tools", Exists));       // beside the desk beats PATH
        files.Add($"{sep}mine{sep}yt-dlp.exe");
        Assert.Equal($"{sep}mine{sep}yt-dlp.exe", WebVideoResolver.Find($"{sep}mine", $"{sep}app", "", Exists));             // the operator's folder
        Assert.Equal($"{sep}mine{sep}yt-dlp.exe", WebVideoResolver.Find($"{sep}mine{sep}yt-dlp.exe", $"{sep}app", "", Exists));  // or the file itself
        Assert.Equal($"{sep}app{sep}yt-dlp.exe", WebVideoResolver.Find($"{sep}nowhere", $"{sep}app", "", Exists));           // a wrong path falls through
        files.Add($"{sep}usr{sep}bin{sep}yt-dlp");
        Assert.Equal($"{sep}usr{sep}bin{sep}yt-dlp", WebVideoResolver.Find("", $"{sep}none", $"{sep}usr{sep}bin", Exists));    // the bare name on a PATH folder
        Assert.Contains("yt-dlp.exe beside Patterns.exe", WebVideoResolver.ToolMissingNote);
        Assert.Contains("site's terms", WebVideoResolver.TermsNote);
    }

    [Fact]
    public void TheLocatorHandsAResolvedPageToTheClipPlayerUnderThePagesOwnKeyAndLeavesTheRest()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Web;
        state.Pattern.Media.WebUrl = "https://www.youtube.com/watch?v=abc";
        state.Pattern.Media.WebPlayVia = WebPlayVia.NativePlayer;
        state.Pattern.Layer1.Enabled = true;
        state.Pattern.Layer1.Source = LayerSource.Web;
        state.Pattern.Layer1.WebUrl = "https://intranet/scoreboard";                                        // the browser's, whatever the hook says
        var snap = new ShowSnapshot { State = state, Version = 1 };
        var asked = new List<string>();
        var was = MediaLocator.WebResolver;
        try
        {
            MediaLocator.WebResolver = w =>
            {
                asked.Add(w.Target);
                return w with { Kind = MediaLocator.WantedKind.VideoFile, Target = "https://cdn.example/v.mp4", Slave = "https://cdn.example/a.m4a", Origin = w.Target };
            };
            var wanted = MediaLocator.FindWantedInputs(snap);
            var tube = wanted.Single(w => w.Key.StartsWith("web:https://www.youtube", StringComparison.Ordinal));
            Assert.Equal(MediaLocator.WantedKind.VideoFile, tube.Kind);
            Assert.Equal("https://cdn.example/v.mp4", tube.Target);
            Assert.Equal("https://cdn.example/a.m4a", tube.Slave);
            Assert.Equal("https://www.youtube.com/watch?v=abc", tube.Origin);
            Assert.Equal(WebPlayVia.NativePlayer, tube.PlayVia);
            Assert.False(tube.Loop);
            var board = wanted.Single(w => w.Target.Contains("scoreboard"));
            Assert.Equal(MediaLocator.WantedKind.Web, board.Kind);                                            // the look never asked
            Assert.Equal(new[] { "https://www.youtube.com/watch?v=abc" }, asked);

            // A hook that answers with another key is ignored: the page stays the page.
            MediaLocator.WebResolver = w => w with { Key = "vid:elsewhere", Kind = MediaLocator.WantedKind.VideoFile };
            Assert.Equal(MediaLocator.WantedKind.Web, MediaLocator.FindWantedInputs(snap).Single(w => w.Target.Contains("youtube")).Kind);

            // No hook: every page is the browser's.
            MediaLocator.WebResolver = null;
            Assert.All(MediaLocator.FindWantedInputs(snap).Where(w => w.Key.StartsWith("web:", StringComparison.Ordinal)), w => Assert.Equal(MediaLocator.WantedKind.Web, w.Kind));
        }
        finally
        {
            MediaLocator.WebResolver = was;
        }
    }
}
