using System.Net;
using System.Text.RegularExpressions;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>One duck rule for the file track and for break music.</summary>
public class MusicLevelTests
{
    [Fact]
    public void OneDuckRuleForBothMusicPlayers()
    {
        Assert.Equal(1.0, MusicLevel.Factor(false, 20));
        Assert.Equal(0.2, MusicLevel.Factor(true, 20), 6);
        Assert.Equal(1.0, MusicLevel.Factor(true, 200));   // clamped: a duck level above 100 is "no duck"
        Assert.Equal(0.0, MusicLevel.Factor(true, -5));
        Assert.Equal(60, MusicLevel.DevicePercent(60, false, 20));
        Assert.Equal(12, MusicLevel.DevicePercent(60, true, 20));
        Assert.Equal(0, MusicLevel.DevicePercent(60, true, 0));
        Assert.Equal(100, MusicLevel.DevicePercent(140, false, 0)); // Spotify's ceiling
        Assert.Equal(30, MusicLevel.DevicePercent(60, false, 20, 0.5)); // the fade hook
    }
}

/// <summary>The words that make a status line read as a failure — and the idle lines that never do.</summary>
public class StatusWordsTests
{
    [Theory]
    [InlineData("Not streaming.", false)]
    [InlineData("Choose a track.", false)]
    [InlineData("Stopped.", false)]
    [InlineData("Ready.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Track not found: x.mp3", true)]
    [InlineData("Stream error: connection refused", true)]
    [InlineData("Break music could not play — Spotify is unavailable — check the network.", true)]
    public void TheWatchedServicesIdleLinesAreClean(string? status, bool failure)
        => Assert.Equal(failure, StatusWords.ReadsAsFailure(status));
}

/// <summary>What a show file carries about break music — and, above all, what it never does.</summary>
public class SpotifyPersistenceTests
{
    [Fact]
    public void AShowFileNeverCarriesTheSpotifySignIn()
    {
        var state = new ShowState();
        state.Spotify.Enabled = true;
        state.Spotify.LevelPct = 45;
        state.Spotify.DeviceName = "Desk Spotify";
        state.Spotify.Items.Add(new SpotifyItemConfig { Name = "Interval bed", Uri = "spotify:playlist:X", Shuffle = true });

        var json = JsonUtil.Serialize(state);
        Assert.Contains("spotify:playlist:X", json);
        Assert.Contains("Interval bed", json);
        Assert.Contains("Desk Spotify", json);
        foreach (var secret in new[] { "clientId", "refreshToken", "accessToken", "client_id", "refresh_token", "bearer" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }

        var back = JsonUtil.Deserialize<ShowState>(json)!;
        Assert.True(back.Spotify.Enabled);
        Assert.Equal(45, back.Spotify.LevelPct);
        Assert.Equal("Desk Spotify", back.Spotify.DeviceName);
        var item = Assert.Single(back.Spotify.Items);
        Assert.Equal("spotify:playlist:X", item.Uri);
        Assert.True(item.Shuffle);
    }

    [Fact]
    public void PlayingAndPlayingIdNeverSurviveASaveAndLoad()
    {
        var state = new ShowState();
        state.Spotify.Playing = true;
        state.Spotify.PlayingId = "m1";
        var back = JsonUtil.Deserialize<ShowState>(JsonUtil.Serialize(state))!;
        Assert.False(back.Spotify.Playing);
        Assert.Equal("", back.Spotify.PlayingId);
    }

    [Fact]
    public void AnOlderShowFileGainsADisabledEmptyBreakMusicBlock()
    {
        // A file written before break music existed has no block at all.
        var old = JsonUtil.Deserialize<ShowState>("{}")!;
        Assert.False(old.Spotify.Enabled);
        Assert.Empty(old.Spotify.Items);
        Assert.Equal(60, old.Spotify.LevelPct);
        Assert.Equal("", old.Spotify.DeviceName);

        // A hand-written entry gets an id and the canonical URI; a second migration changes nothing.
        var state = new ShowState { SchemaVersion = 5 };
        state.Spotify.Items.Add(new SpotifyItemConfig { Id = "", Uri = "https://open.spotify.com/playlist/X?si=1" });
        SettingsStore.Migrate(state);
        var item = Assert.Single(state.Spotify.Items);
        Assert.False(string.IsNullOrWhiteSpace(item.Id));
        Assert.Equal("spotify:playlist:X", item.Uri);
        Assert.Equal(ShowState.CurrentSchemaVersion, state.SchemaVersion);
        var id = item.Id;
        SettingsStore.Migrate(state);
        Assert.Equal(id, state.Spotify.Items[0].Id);
        Assert.Equal("spotify:playlist:X", state.Spotify.Items[0].Uri);
    }
}

public class SpotifyUriTests
{
    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=x", SpotifyRefKind.Playlist, "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:track:4uLU6hMCjMI75M1A2tKUQC", SpotifyRefKind.Track, "4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("https://open.spotify.com/intl-de/album/1DFixLWuPkv3KT3TnV35m3", SpotifyRefKind.Album, "1DFixLWuPkv3KT3TnV35m3")]
    [InlineData("open.spotify.com/artist/4Z8W4fKeB5YxbusRsdQVPb", SpotifyRefKind.Artist, "4Z8W4fKeB5YxbusRsdQVPb")]
    [InlineData("  spotify:playlist:abc  ", SpotifyRefKind.Playlist, "abc")]
    [InlineData("spotify:user:ben:playlist:old1", SpotifyRefKind.Playlist, "old1")]
    public void LinksAndUrisBecomeOneCanonicalReference(string input, SpotifyRefKind kind, string id)
    {
        Assert.True(SpotifyUri.TryParse(input, out var r));
        Assert.Equal(kind, r.Kind);
        Assert.Equal(id, r.Id);
        Assert.Equal($"spotify:{kind.ToString().ToLowerInvariant()}:{id}", r.Uri);
        Assert.True(SpotifyUri.TryParse(r.Uri, out var again)); // the canonical form round-trips
        Assert.Equal(r, again);
    }

    [Theory]
    [InlineData(@"C:\music\a.mp3")]
    [InlineData("https://youtube.com/x")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("spotify:")]
    [InlineData("https://open.spotify.com/")]
    [InlineData("https://open.spotify.com/playlist/")]
    public void JunkIsNotASpotifyReference(string? input)
    {
        Assert.False(SpotifyUri.TryParse(input, out var r));
        Assert.Equal(SpotifyRefKind.Unknown, r.Kind);
        Assert.Equal("", r.Uri);
        Assert.False(SpotifyUri.IsValid(input));
    }

    [Fact]
    public void DescribeAndKindLabelNameTheThing()
    {
        Assert.StartsWith("Playlist ", SpotifyUri.Describe("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M"));
        Assert.EndsWith("…", SpotifyUri.Describe("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M"));   // a long id is shortened
        Assert.Equal("Track abc", SpotifyUri.Describe("spotify:track:abc"));
        Assert.Equal("", SpotifyUri.Describe("junk"));
        SpotifyUri.TryParse("spotify:playlist:x", out var list);
        SpotifyUri.TryParse("spotify:album:x", out var album);
        SpotifyUri.TryParse("spotify:track:x", out var song);
        SpotifyUri.TryParse("spotify:artist:x", out var artist);
        Assert.Equal("LIST", list.KindLabel);
        Assert.Equal("ALBUM", album.KindLabel);
        Assert.Equal("SONG", song.KindLabel);
        Assert.Equal("ARTIST", artist.KindLabel);
        Assert.True(list.IsContext);
        Assert.False(song.IsContext);
        SpotifyUri.TryParse("junk", out var junk);
        Assert.Equal("", junk.KindLabel);

        var item = new SpotifyItemConfig { Uri = "spotify:playlist:X" };
        Assert.Equal("LIST", item.KindLabel);
        Assert.Equal("Playlist X", item.DisplayName);
        item.Name = "Interval bed";
        Assert.Equal("Interval bed", item.DisplayName);
    }
}

public class SpotifyPkceTests
{
    [Fact]
    public void TheChallengeMatchesTheRfcVector()
        => Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                        SpotifyPkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")); // RFC 7636 appendix B

    [Fact]
    public void VerifiersAreUniqueUrlSafeAndLongEnough()
    {
        var unreserved = new Regex("^[A-Za-z0-9\\-._~]+$");
        var verifiers = Enumerable.Range(0, 100).Select(_ => SpotifyPkce.NewVerifier()).ToList();
        Assert.All(verifiers, v =>
        {
            Assert.InRange(v.Length, 43, 128);
            Assert.Matches(unreserved, v);
        });
        Assert.Equal(100, verifiers.Distinct().Count());
        var states = Enumerable.Range(0, 100).Select(_ => SpotifyPkce.NewState()).ToList();
        Assert.All(states, s => Assert.Matches("^[0-9a-f]{32}$", s));
        Assert.Equal(100, states.Distinct().Count());
    }
}

public class SpotifyEndpointTests
{
    [Fact]
    public void TheAuthorizeUrlCarriesPkceAndALoopbackRedirect()
    {
        var url = SpotifyEndpoints.AuthorizeUrl("cid", SpotifyEndpoints.RedirectUri(8724), "CHAL", "st4te");
        Assert.StartsWith("https://accounts.spotify.com/authorize?", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("code_challenge=CHAL", url);
        Assert.Contains("client_id=cid", url);
        Assert.Contains("state=st4te", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A8724%2Fcallback", url);
        Assert.Contains("user-read-playback-state", url);
        Assert.Contains("user-modify-playback-state", url);
        Assert.Contains("playlist-read-private", url);
        Assert.DoesNotContain("localhost", url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("http://127.0.0.1:8724/callback", SpotifyEndpoints.RedirectUri(8724));
        Assert.Contains("grant_type=authorization_code", SpotifyEndpoints.TokenForm("cid", "code", "ver", SpotifyEndpoints.RedirectUri(8725)));
        Assert.Contains("code_verifier=ver", SpotifyEndpoints.TokenForm("cid", "code", "ver", SpotifyEndpoints.RedirectUri(8725)));
        Assert.Contains("grant_type=refresh_token&refresh_token=rt&client_id=cid", SpotifyEndpoints.RefreshForm("cid", "rt"));
    }

    [Fact]
    public void PlaybackUrlsCarryTheDeviceAndTheLevel()
    {
        Assert.Equal("https://api.spotify.com/v1/me/player/play?device_id=abc", SpotifyEndpoints.PlayUrl("abc"));
        Assert.Equal("https://api.spotify.com/v1/me/player/pause", SpotifyEndpoints.PauseUrl(""));
        Assert.Equal("https://api.spotify.com/v1/me/player/volume?volume_percent=40&device_id=abc", SpotifyEndpoints.VolumeUrl(40, "abc"));
        Assert.EndsWith("volume_percent=100", SpotifyEndpoints.VolumeUrl(140, ""));
        Assert.EndsWith("volume_percent=0", SpotifyEndpoints.VolumeUrl(-5, ""));
        Assert.Equal("https://api.spotify.com/v1/me/player/shuffle?state=true", SpotifyEndpoints.ShuffleUrl(true, ""));
        Assert.Equal("https://api.spotify.com/v1/me/player/next?device_id=abc", SpotifyEndpoints.NextUrl("abc"));
        Assert.Equal("https://api.spotify.com/v1/me/player/devices", SpotifyEndpoints.DevicesUrl);
        Assert.Equal("https://api.spotify.com/v1/me/playlists?limit=50", SpotifyEndpoints.PlaylistsUrl(500));
        Assert.Equal("{\"device_ids\":[\"abc\"],\"play\":false}", SpotifyEndpoints.TransferBody("abc"));
    }

    [Fact]
    public void ThePlayBodyIsAContextForAListAndUrisForASong()
    {
        Assert.Equal("{\"context_uri\":\"spotify:playlist:X\"}", SpotifyEndpoints.PlayBody("spotify:playlist:X"));
        Assert.Equal("{\"context_uri\":\"spotify:album:X\"}", SpotifyEndpoints.PlayBody("https://open.spotify.com/album/X?si=1"));
        Assert.Equal("{\"uris\":[\"spotify:track:Y\"]}", SpotifyEndpoints.PlayBody("spotify:track:Y"));
        Assert.Null(SpotifyEndpoints.PlayBody(null));   // resume
        Assert.Null(SpotifyEndpoints.PlayBody(""));
        Assert.Null(SpotifyEndpoints.PlayBody("junk"));
    }
}

public class SpotifyJsonTests
{
    private static readonly DateTime T = new(2026, 9, 4, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ReadsATokenAndItsExpiry()
    {
        Assert.True(SpotifyJson.TryReadToken("{\"access_token\":\"A\",\"refresh_token\":\"R\",\"expires_in\":3600}", T, "", out var token));
        Assert.Equal("A", token.AccessToken);
        Assert.Equal("R", token.RefreshToken);
        Assert.Equal(T.AddSeconds(3600), token.ExpiresUtc);
        Assert.False(token.NeedsRefresh(T.AddSeconds(3500)));
        Assert.True(token.NeedsRefresh(T.AddSeconds(3541)));   // a minute of margin
        Assert.False(SpotifyJson.TryReadToken("{\"error\":\"invalid_grant\"}", T, "", out _));
        Assert.False(SpotifyJson.TryReadToken("not json", T, "", out _));
        Assert.False(SpotifyJson.TryReadToken("[]", T, "", out _));
    }

    [Fact]
    public void ARefreshWithoutANewRefreshTokenKeepsTheOldOne()
    {
        Assert.True(SpotifyJson.TryReadToken("{\"access_token\":\"A\",\"expires_in\":3600}", T, "OLD", out var token));
        Assert.Equal("OLD", token.RefreshToken);
        Assert.True(SpotifyJson.TryReadToken("{\"access_token\":\"A\",\"refresh_token\":\"NEW\"}", T, "OLD", out var rotated));
        Assert.Equal("NEW", rotated.RefreshToken);
        Assert.Equal(T.AddSeconds(3600), rotated.ExpiresUtc); // no expires_in → an hour
    }

    [Fact]
    public void ReadsDevicesNowPlayingAndPlaylists()
    {
        var devices = SpotifyJson.ReadDevices("""
            {"devices":[{"id":"d1","name":"Desk","is_active":true,"is_restricted":false,"volume_percent":55},
                        {"id":"d2","name":"Lobby speaker","is_active":false,"is_restricted":true,"volume_percent":80},
                        {"name":"no id"}]}
            """);
        Assert.Equal(2, devices.Count);
        Assert.Equal(("d1", "Desk", true, false, 55), (devices[0].Id, devices[0].Name, devices[0].IsActive, devices[0].IsRestricted, devices[0].VolumePercent));
        Assert.Equal(("d2", "Lobby speaker", false, true, 80), (devices[1].Id, devices[1].Name, devices[1].IsActive, devices[1].IsRestricted, devices[1].VolumePercent));
        Assert.Empty(SpotifyJson.ReadDevices(""));
        Assert.Empty(SpotifyJson.ReadDevices("{}"));
        Assert.Empty(SpotifyJson.ReadDevices("{\"devices\":[{\"id\":"));

        var now = SpotifyJson.ReadNowPlaying("""
            {"is_playing":true,"item":{"name":"Kerala","artists":[{"name":"Bonobo"},{"name":"Someone"}]},
             "device":{"id":"d1","name":"Desk","volume_percent":55}}
            """)!;
        Assert.True(now.IsPlaying);
        Assert.Equal("Bonobo · Kerala", now.Line);
        Assert.Equal("d1", now.DeviceId);
        Assert.Equal("Desk", now.DeviceName);
        Assert.Equal(55, now.VolumePercent);
        Assert.Null(SpotifyJson.ReadNowPlaying(""));
        Assert.Null(SpotifyJson.ReadNowPlaying("{}"));
        Assert.Null(SpotifyJson.ReadNowPlaying("{\"item\":null}"));
        Assert.Null(SpotifyJson.ReadNowPlaying("{\"is_playing\":tr"));
        Assert.Null(SpotifyJson.ReadNowPlaying("[1,2]"));
        var paused = SpotifyJson.ReadNowPlaying("{\"is_playing\":false,\"device\":{\"name\":\"Desk\"}}")!;
        Assert.False(paused.IsPlaying);
        Assert.Equal("", paused.Line);
        Assert.Equal("Kerala", SpotifyJson.ReadNowPlaying("{\"is_playing\":true,\"item\":{\"name\":\"Kerala\"}}")!.Line);

        var lists = SpotifyJson.ReadPlaylists("""
            {"items":[{"uri":"spotify:playlist:A","name":"Walk-in","tracks":{"total":40}},
                      {"id":"B"},
                      {"name":"no uri or id"},
                      7]}
            """);
        Assert.Equal(2, lists.Count);
        Assert.Equal(("spotify:playlist:A", "Walk-in", 40), (lists[0].Uri, lists[0].Name, lists[0].Tracks));
        Assert.Equal(("spotify:playlist:B", "", 0), (lists[1].Uri, lists[1].Name, lists[1].Tracks));
        Assert.Equal("Walk-in (40)", lists[0].ToString());
        Assert.Empty(SpotifyJson.ReadPlaylists("garbage"));

        Assert.Equal(("Ben", true), SpotifyJson.ReadMe("{\"display_name\":\"Ben\",\"product\":\"premium\"}"));
        Assert.Equal(("ben42", false), SpotifyJson.ReadMe("{\"id\":\"ben42\",\"product\":\"free\"}"));
        Assert.Equal(("", false), SpotifyJson.ReadMe("nope"));
        Assert.Equal(7, SpotifyJson.RetryAfterSeconds("7"));
        Assert.Equal(5, SpotifyJson.RetryAfterSeconds(null));
        Assert.Equal(5, SpotifyJson.RetryAfterSeconds("soon"));
        Assert.Equal(5, SpotifyJson.RetryAfterSeconds("-3"));
    }

    [Theory]
    [InlineData(204, "", 0, null)]
    [InlineData(200, "{}", 0, null)]
    [InlineData(0, "", 0, "Spotify is unavailable — check the network.")]
    [InlineData(401, "", 0, "Spotify sign-in expired — press CONNECT on the Audio page.")]
    [InlineData(403, "{\"error\":{\"reason\":\"PREMIUM_REQUIRED\"}}", 0, "Spotify Premium is required to control playback.")]
    [InlineData(403, "{\"error\":{\"message\":\"User not registered in the Developer Dashboard\"}}", 0, "Spotify refused that — check the account is listed on your developer app.")]
    [InlineData(404, "{\"error\":{\"reason\":\"NO_ACTIVE_DEVICE\"}}", 0, "No Spotify device — open Spotify on the desk machine and press play once.")]
    [InlineData(404, "{\"error\":{\"message\":\"Not found\"}}", 0, "Spotify could not find that playlist or track.")]
    [InlineData(429, "", 7, "Spotify is busy (rate limited) — retrying in 7s.")]
    [InlineData(429, "", 0, "Spotify is busy (rate limited) — retrying in 1s.")]
    [InlineData(503, "", 0, "Spotify service error (503) — retrying.")]
    [InlineData(418, "", 0, "Spotify could not do that (418).")]
    public void EveryFailureBecomesAnOperatorSentence(int status, string body, int retryAfter, string? expected)
        => Assert.Equal(expected, SpotifyJson.Failure(new SpotifyReply(status, body, retryAfter)));
}

/// <summary>
/// The test that stops a machine with Spotify merely installed from red-flagging every asynchronous
/// cue in the show: the cue rows read <c>CommandFailure</c> ("Break music could not play — …"), never
/// the status line, and a rate limit must never reach them at all.
/// </summary>
public class SpotifyStatusTests
{
    public static readonly string[] IdleLines =
    {
        "Off.",
        "Break music is run by the first Patterns window.",
        "Add your Spotify Client ID on the Audio page.",
        "Not connected — press CONNECT on the Audio page.",
        "Ready — Spotify will use whichever device is active.",
        "Ready — Spotify on Desk.",
        "Paused.",
        "Starting…",
        "Playing — Bonobo · Kerala — on Desk · 60%",
        "Waiting for Spotify sign-in in your browser…",
        "Spotify sign-in timed out — press CONNECT again.",
        "Spotify sign-in was cancelled.",
        "'Desk' is not on Spotify right now — playing on the active device.",
        "Spotify reports nothing playing — open Spotify on the desk machine and press play once.",
        "Spotify is busy (rate limited) — retrying in 7s.",
    };

    [Fact]
    public void NoIdleBreakMusicStatusReadsAsAFailure()
    {
        foreach (var line in IdleLines)
        {
            Assert.False(StatusWords.ReadsAsFailure(line), line);
        }
        foreach (var status in new[] { 0, 401, 403, 404, 500, 418 })
        {
            var problem = SpotifyJson.Failure(new SpotifyReply(status, ""))!;
            Assert.True(StatusWords.ReadsAsFailure($"Break music could not play — {problem}"), problem);
        }
        // The one that never becomes a command failure at all.
        Assert.False(StatusWords.ReadsAsFailure(SpotifyJson.Failure(new SpotifyReply(429, "", 3))));
    }
}

public class SpotifyBackoffTests
{
    [Fact]
    public void RepeatedFailuresBackOffAndCapAtAMinute()
    {
        Assert.Equal(new[] { 2, 4, 8, 15, 30, 60, 60, 60 },
                     Enumerable.Range(1, 8).Select(n => (int)SpotifyBackoff.Delay(n).TotalSeconds).ToArray());
        Assert.Equal(2, (int)SpotifyBackoff.Delay(0).TotalSeconds);
        Assert.Equal(60, (int)SpotifyBackoff.Delay(1000).TotalSeconds);
    }
}

/// <summary>One break-music resolver for the desk, a cue and the remote, and the references a delete must respect.</summary>
public class SpotifyLibraryTests
{
    [Fact]
    public void OneResolverForTheDeskACueAndTheRemote()
    {
        var state = new ShowState();
        var a = new SpotifyItemConfig { Id = "m-a", Name = "Interval bed", Uri = "spotify:playlist:A" };
        var b = new SpotifyItemConfig { Id = "m-b", Uri = "spotify:album:B" }; // named by its link
        state.Spotify.Items.Add(a);
        state.Spotify.Items.Add(b);

        Assert.Same(a, SpotifyLibrary.Find(state, "1"));
        Assert.Same(b, SpotifyLibrary.Find(state, " 2 "));
        Assert.Null(SpotifyLibrary.Find(state, "0"));
        Assert.Null(SpotifyLibrary.Find(state, "9"));
        Assert.Same(a, SpotifyLibrary.Find(state, "m-a"));
        Assert.Same(a, SpotifyLibrary.Find(state, "interval BED"));
        Assert.Same(b, SpotifyLibrary.Find(state, b.DisplayName.ToUpperInvariant()));
        Assert.Null(SpotifyLibrary.Find(state, ""));
        Assert.Null(SpotifyLibrary.Find(state, "   "));
        Assert.Null(SpotifyLibrary.Find(state, null));
    }

    [Fact]
    public void ReferencesNameTheCuesThatPlayAnEntry()
    {
        var state = new ShowState();
        var a = new SpotifyItemConfig { Id = "m-a", Name = "Interval bed", Uri = "spotify:playlist:A" };
        state.Spotify.Items.Add(a);
        var stack = CueStacks.Caller(state);
        stack.Cues.Add(new RunCueConfig { Number = "03.020", Name = "Interval", Actions = { new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "m-a" } } });
        stack.Cues.Add(new RunCueConfig { Number = "03.030", Name = "By name", Actions = { new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "interval bed" } } });
        stack.Cues.Add(new RunCueConfig { Number = "03.040", Name = "Pause", Actions = { new CueActionConfig { Kind = CueActionKind.SpotifyPause } } });
        stack.Cues.Add(new RunCueConfig { Number = "03.050", Name = "Resume", Actions = { new CueActionConfig { Kind = CueActionKind.SpotifyPlay } } });

        var refs = SpotifyLibrary.References(state, a);
        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.Contains("03.020 Interval"));
        Assert.Contains(refs, r => r.Contains("03.030 By name"));
        Assert.Empty(SpotifyLibrary.References(state, new SpotifyItemConfig { Id = "other", Uri = "spotify:track:x" }));
    }
}

public class SpotifyCredentialStoreTests
{
    [Fact]
    public void TheSignInLivesBesideTheSettingsAndClears()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-spotify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SpotifyCredentialStore(dir);
            Assert.EndsWith("patterns.spotify.json", store.Path);
            Assert.Equal(SpotifyCredentials.None, store.Read());
            Assert.False(SpotifyCredentials.None.IsConnected);
            Assert.False(SpotifyCredentials.None.HasClientId);

            var creds = new SpotifyCredentials("cid", "refresh", "ben@example.com", new DateTime(2026, 9, 4, 19, 0, 0, DateTimeKind.Utc));
            store.Write(creds);
            var back = store.Read();
            Assert.Equal(("cid", "refresh", "ben@example.com"), (back.ClientId, back.RefreshToken, back.Account));
            Assert.True(back.IsConnected);
            Assert.False(new SpotifyCredentials("cid", "", "", DateTime.MinValue).IsConnected); // an id alone is not a sign-in
            Assert.True(new SpotifyCredentials("cid", "", "", DateTime.MinValue).HasClientId);

            File.WriteAllText(store.Path, "{ this is not json");
            Assert.Equal(SpotifyCredentials.None, store.Read()); // unreadable = not connected, never a throw

            store.Write(creds);
            store.Clear();
            Assert.False(File.Exists(store.Path));
            Assert.Equal(SpotifyCredentials.None, store.Read());
            store.Clear(); // twice is harmless
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>The cue side of break music: the spec table, the summary, the validator.</summary>
public class SpotifyCueTests
{
    private static RunCueConfig Cue(params CueActionConfig[] actions)
    {
        var cue = new RunCueConfig { Number = "01.010", Name = "Doors" };
        foreach (var a in actions) cue.Actions.Add(a);
        return cue;
    }

    [Fact]
    public void TheSpecTableSummaryAndPickerAgree()
    {
        Assert.Equal((TargetKind.Music, ValueKind.None), CueActionSpec.For(CueActionKind.SpotifyPlay));
        Assert.Equal((TargetKind.None, ValueKind.None), CueActionSpec.For(CueActionKind.SpotifyPause));
        Assert.Equal((TargetKind.None, ValueKind.None), CueActionSpec.For(CueActionKind.SpotifyNext));
        Assert.Equal((TargetKind.None, ValueKind.Level), CueActionSpec.For(CueActionKind.SpotifyVolume));
        foreach (var kind in new[] { CueActionKind.SpotifyPlay, CueActionKind.SpotifyPause, CueActionKind.SpotifyNext, CueActionKind.SpotifyVolume })
        {
            Assert.Contains(kind, CueActionSpec.Editable);
            Assert.False(string.IsNullOrWhiteSpace(CueActionSpec.Label(kind)));
            Assert.StartsWith("Break music", CueActionSpec.Label(kind));
            Assert.False(CueActionSpec.ChangesContent(kind)); // sound only: a video stinger may share the cue
        }
        Assert.True(CueActionSpec.TryParseLevel(" 40 ", out var level));
        Assert.Equal(40, level);
        Assert.True(CueActionSpec.TryParseLevel("0", out _));
        Assert.True(CueActionSpec.TryParseLevel("100", out _));
        foreach (var bad in new[] { "101", "-1", "loud", "", null })
        {
            Assert.False(CueActionSpec.TryParseLevel(bad, out _), bad ?? "null");
        }

        var state = new ShowState();
        state.Spotify.Items.Add(new SpotifyItemConfig { Id = "m1", Name = "Interval bed", Uri = "spotify:playlist:X" });
        Assert.Equal("Break music 'Interval bed'", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "m1" }));
        Assert.Equal("Break music 'ghost'", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "ghost" }));
        Assert.Equal("Break music play", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.SpotifyPlay }));
        Assert.Equal("Break music pause", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.SpotifyPause }));
        Assert.Equal("Break music skip", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.SpotifyNext }));
        Assert.Equal("Break music 40%", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.SpotifyVolume, Value = "40" }));
    }

    [Fact]
    public void ABrokenBreakMusicCueSaysWhyAndAnUnsetOneOnlyWarns()
    {
        var state = new ShowState();
        state.Spotify.Enabled = true;
        state.Spotify.Items.Add(new SpotifyItemConfig { Id = "m1", Name = "Interval bed", Uri = "spotify:playlist:X" });
        state.Spotify.Items.Add(new SpotifyItemConfig { Id = "m2", Name = "Bad", Uri = "junk" });
        var ready = new CueValidationContext { FileExists = _ => true, VideoDecoderAvailable = true, MusicReady = true };

        var unknown = CueValidator.ValidateOne(state, Cue(new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "nope" }), ready);
        Assert.Equal(1, unknown.BrokenCount);
        Assert.Contains("not found", unknown.Broken.Values.Single());

        var junk = CueValidator.ValidateOne(state, Cue(new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "m2" }), ready);
        Assert.Equal(1, junk.BrokenCount);
        Assert.Contains("no valid Spotify link", junk.Broken.Values.Single());

        var level = CueValidator.ValidateOne(state, Cue(new CueActionConfig { Kind = CueActionKind.SpotifyVolume, Value = "120" }), ready);
        Assert.Equal(1, level.BrokenCount);
        Assert.Contains("0 to 100", level.Broken.Values.Single());

        // Off is a warning, never Broken: a Hard issue would make GO skip the look this cue applies.
        state.Spotify.Enabled = false;
        var off = CueValidator.ValidateOne(state, Cue(new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "m1" }), ready);
        Assert.Equal(0, off.BrokenCount);
        Assert.Contains("break music is off", off.Warnings.Values.Single());
        state.Spotify.Enabled = true;

        var notConnected = CueValidator.ValidateOne(state, Cue(new CueActionConfig { Kind = CueActionKind.SpotifyPause }),
            new CueValidationContext { FileExists = _ => true, MusicReady = false });
        Assert.Equal(0, notConnected.BrokenCount);
        Assert.Contains("not connected", notConnected.Warnings.Values.Single());

        var noDevice = CueValidator.ValidateOne(state, Cue(new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "m1" }), ready);
        Assert.Equal(0, noDevice.BrokenCount);
        Assert.Contains("no Spotify device chosen", noDevice.Warnings.Values.Single());

        state.Spotify.DeviceName = "Desk";
        var clean = CueValidator.ValidateOne(state, Cue(
            new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "m1" },
            new CueActionConfig { Kind = CueActionKind.SpotifyVolume, Value = "40" },
            new CueActionConfig { Kind = CueActionKind.SpotifyNext },
            new CueActionConfig { Kind = CueActionKind.SpotifyPause }), ready);
        Assert.Equal(0, clean.BrokenCount);
        Assert.Empty(clean.Warnings);

        // A video stinger may share a cue with break music (sound is not content)…
        state.Stingers.Items.Add(new StingerItemConfig { Id = "s1", Name = "Opening", Path = "C:/show/opening.mp4" });
        var shared = CueValidator.ValidateOne(state, Cue(
            new CueActionConfig { Kind = CueActionKind.StingerFire, Target = "s1" },
            new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "m1" }), ready);
        Assert.Equal(0, shared.BrokenCount);
    }
}

/// <summary>The MUSIC verbs on the wire, with the arguments the router reads.</summary>
public class SpotifyProtocolTests
{
    [Theory]
    [InlineData("MUSIC PLAY", RemoteCommandKind.MusicPlay, 0, "")]
    [InlineData("music play 3", RemoteCommandKind.MusicPlay, 3, "")]
    [InlineData("MUSIC PLAY Interval bed", RemoteCommandKind.MusicPlay, 0, "Interval bed")]
    [InlineData("MUSIC RESUME", RemoteCommandKind.MusicPlay, 0, "")]
    [InlineData("MUSIC 3", RemoteCommandKind.MusicPlay, 3, "")]
    [InlineData("MUSIC Interval bed", RemoteCommandKind.MusicPlay, 0, "Interval bed")]
    [InlineData("SPOTIFY PLAY 2", RemoteCommandKind.MusicPlay, 2, "")]
    [InlineData("spotify 4", RemoteCommandKind.MusicPlay, 4, "")]
    [InlineData("MUSIC PAUSE", RemoteCommandKind.MusicPause, 0, "")]
    [InlineData("MUSIC STOP", RemoteCommandKind.MusicPause, 0, "")]
    [InlineData("MUSIC NEXT", RemoteCommandKind.MusicNext, 0, "")]
    [InlineData("MUSIC SKIP", RemoteCommandKind.MusicNext, 0, "")]
    [InlineData("MUSIC VOL 40", RemoteCommandKind.MusicVolume, 0, "40")]
    [InlineData("MUSIC VOLUME 0", RemoteCommandKind.MusicVolume, 0, "0")]      // 0 rides TextArg: IntArg 0 is "no number"
    [InlineData("MUSIC VOL 120", RemoteCommandKind.MusicVolume, 0, "120")]    // parses; the executor refuses it in words
    public void ParsesBreakMusicCommands(string line, RemoteCommandKind kind, int intArg, string textArg)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.Equal(kind, cmd.Kind);
        Assert.Equal(intArg, cmd.IntArg);
        Assert.Equal(textArg, cmd.TextArg);
    }

    [Theory]
    [InlineData("MUSIC")]
    [InlineData("MUSIC VOL")]
    [InlineData("SPOTIFY")]
    public void ABareMusicVerbIsUnknownNotAGuess(string line)
        => Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse(line).Kind);
}
