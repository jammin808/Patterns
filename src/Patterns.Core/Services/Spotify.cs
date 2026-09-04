using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>What a Spotify link points at. Unknown first — the tolerant-enum rule.</summary>
public enum SpotifyRefKind
{
    Unknown,
    Track,
    Album,
    Playlist,
    Artist,
}

/// <summary>One Spotify thing, whatever form the operator pasted it in.</summary>
public readonly record struct SpotifyRef(SpotifyRefKind Kind, string Id)
{
    public string Uri => Kind == SpotifyRefKind.Unknown ? "" : $"spotify:{Kind.ToString().ToLowerInvariant()}:{Id}";

    /// <summary>A playlist/album/artist plays as a context; a track plays as a one-item uri list.</summary>
    public bool IsContext => Kind is SpotifyRefKind.Album or SpotifyRefKind.Playlist or SpotifyRefKind.Artist;

    /// <summary>LIST / ALBUM / SONG / ARTIST / "" — the operator's word, not the API's.</summary>
    public string KindLabel => Kind switch
    {
        SpotifyRefKind.Playlist => "LIST",
        SpotifyRefKind.Album => "ALBUM",
        SpotifyRefKind.Track => "SONG",
        SpotifyRefKind.Artist => "ARTIST",
        _ => "",
    };
}

/// <summary>Spotify links and URIs in, one canonical reference out. Pure.</summary>
public static class SpotifyUri
{
    /// <summary>
    /// Accepts "spotify:playlist:ID", "https://open.spotify.com/playlist/ID?si=…",
    /// "open.spotify.com/intl-de/track/ID" (the locale segment) and bare "open.spotify.com/…".
    /// </summary>
    public static bool TryParse(string? input, out SpotifyRef r)
    {
        r = new SpotifyRef(SpotifyRefKind.Unknown, "");
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();

        if (text.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split(':', StringSplitOptions.RemoveEmptyEntries);
            // "spotify:user:x:playlist:ID" is Spotify's own older form — the last pair still names it.
            for (var i = parts.Length - 2; i >= 1; i--)
            {
                if (!TryKind(parts[i], out var k)) continue;
                var id = Clean(parts[i + 1]);
                if (id.Length == 0) return false;
                r = new SpotifyRef(k, id);
                return true;
            }
            return false;
        }

        var url = text;
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) url = url[(scheme + 3)..];
        if (!url.StartsWith("open.spotify.com/", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("play.spotify.com/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        url = url[(url.IndexOf('/') + 1)..];
        var cut = url.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) url = url[..cut];
        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            // "intl-de" and "user/<name>" sit in front of the kind on real Spotify share links.
            if (!TryKind(segments[i], out var k)) continue;
            var id = Clean(segments[i + 1]);
            if (id.Length == 0) return false;
            r = new SpotifyRef(k, id);
            return true;
        }
        return false;
    }

    public static bool IsValid(string? input) => TryParse(input, out _);

    /// <summary>"Playlist 37i9dQZF1D…" for a nameless row; "" for junk.</summary>
    public static string Describe(string? uri)
    {
        if (!TryParse(uri, out var r)) return "";
        var id = r.Id.Length > 12 ? r.Id[..12] + "…" : r.Id;
        return $"{r.Kind} {id}";
    }

    private static bool TryKind(string word, out SpotifyRefKind kind)
    {
        kind = word.ToLowerInvariant() switch
        {
            "track" => SpotifyRefKind.Track,
            "album" => SpotifyRefKind.Album,
            "playlist" => SpotifyRefKind.Playlist,
            "artist" => SpotifyRefKind.Artist,
            _ => SpotifyRefKind.Unknown,
        };
        return kind != SpotifyRefKind.Unknown;
    }

    /// <summary>Spotify ids are base62; anything else in the segment is not part of the id.</summary>
    private static string Clean(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else break;
        }
        return sb.ToString();
    }
}

/// <summary>PKCE for a desktop app: no client secret ever exists. Pure.</summary>
public static class SpotifyPkce
{
    /// <summary>48 bytes → 64 base64url characters, inside the RFC 7636 43–128 range.</summary>
    public const int VerifierBytes = 48;

    public static string NewVerifier(RandomNumberGenerator? rng = null) => RandomBase64Url(VerifierBytes, rng);

    /// <summary>32 hex characters — echoed back by Spotify and compared before a code is used.</summary>
    public static string NewState(RandomNumberGenerator? rng = null)
    {
        var bytes = new byte[16];
        if (rng is null) RandomNumberGenerator.Fill(bytes);
        else rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>base64url(SHA256(ascii(verifier))), unpadded.</summary>
    public static string Challenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier ?? "")));

    private static string RandomBase64Url(int bytes, RandomNumberGenerator? rng)
    {
        var buffer = new byte[bytes];
        if (rng is null) RandomNumberGenerator.Fill(buffer);
        else rng.GetBytes(buffer);
        return Base64Url(buffer);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Every Spotify URL and body this app builds, in one place. Pure string building.</summary>
public static class SpotifyEndpoints
{
    public const string Scopes = "user-read-playback-state user-modify-playback-state playlist-read-private";
    public const string Accounts = "https://accounts.spotify.com";
    public const string Api = "https://api.spotify.com/v1";

    /// <summary>"http://127.0.0.1:8724/callback" — Spotify has rejected "localhost" since 9 April 2025.</summary>
    public static string RedirectUri(int port) => $"http://127.0.0.1:{port}/callback";

    public static string AuthorizeUrl(string clientId, string redirectUri, string challenge, string state)
        => Accounts + "/authorize" +
           "?response_type=code" +
           "&client_id=" + Uri.EscapeDataString(clientId) +
           "&scope=" + Uri.EscapeDataString(Scopes) +
           "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
           "&state=" + Uri.EscapeDataString(state) +
           "&code_challenge_method=S256" +
           "&code_challenge=" + Uri.EscapeDataString(challenge);

    public static string TokenUrl => Accounts + "/api/token";

    public static string TokenForm(string clientId, string code, string verifier, string redirectUri)
        => "grant_type=authorization_code" +
           "&code=" + Uri.EscapeDataString(code) +
           "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
           "&client_id=" + Uri.EscapeDataString(clientId) +
           "&code_verifier=" + Uri.EscapeDataString(verifier);

    public static string RefreshForm(string clientId, string refreshToken)
        => "grant_type=refresh_token" +
           "&refresh_token=" + Uri.EscapeDataString(refreshToken) +
           "&client_id=" + Uri.EscapeDataString(clientId);

    public static string PlayUrl(string deviceId) => Api + "/me/player/play" + Device(deviceId, first: true);

    public static string PauseUrl(string deviceId) => Api + "/me/player/pause" + Device(deviceId, first: true);

    public static string NextUrl(string deviceId) => Api + "/me/player/next" + Device(deviceId, first: true);

    /// <summary>Clamps to Spotify's own 0–100 range: a device has no headroom above its own maximum.</summary>
    public static string VolumeUrl(int percent, string deviceId)
        => Api + "/me/player/volume?volume_percent=" +
           Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture) + Device(deviceId, first: false);

    public static string ShuffleUrl(bool on, string deviceId)
        => Api + "/me/player/shuffle?state=" + (on ? "true" : "false") + Device(deviceId, first: false);

    public static string TransferUrl => Api + "/me/player";

    public static string DevicesUrl => Api + "/me/player/devices";

    public static string PlayerUrl => Api + "/me/player";

    public static string MeUrl => Api + "/me";

    public static string PlaylistsUrl(int limit = 50)
        => Api + "/me/playlists?limit=" + Math.Clamp(limit, 1, 50).ToString(CultureInfo.InvariantCulture);

    /// <summary>Context for a playlist/album/artist, a uri list for one song, null to resume.</summary>
    public static string? PlayBody(string? uri)
    {
        if (!SpotifyUri.TryParse(uri, out var r)) return null;
        return r.IsContext
            ? "{\"context_uri\":" + JsonSerializer.Serialize(r.Uri) + "}"
            : "{\"uris\":[" + JsonSerializer.Serialize(r.Uri) + "]}";
    }

    /// <summary>Wake a sleeping Connect device without starting anything on it.</summary>
    public static string TransferBody(string deviceId)
        => "{\"device_ids\":[" + JsonSerializer.Serialize(deviceId) + "],\"play\":false}";

    private static string Device(string deviceId, bool first)
        => string.IsNullOrEmpty(deviceId) ? "" : (first ? "?" : "&") + "device_id=" + Uri.EscapeDataString(deviceId);
}

/// <summary>An access token and how long it is good for.</summary>
public readonly record struct SpotifyToken(string AccessToken, string RefreshToken, DateTime ExpiresUtc)
{
    /// <summary>No token — the value to hold instead of <c>default</c>, whose strings are null.</summary>
    public static readonly SpotifyToken None = new("", "", DateTime.MinValue);

    public bool IsEmpty => string.IsNullOrEmpty(AccessToken);

    /// <summary>A minute of margin, so a command never goes out on a token that expires mid-flight.</summary>
    public bool NeedsRefresh(DateTime nowUtc) => nowUtc >= ExpiresUtc - TimeSpan.FromSeconds(60);
}

/// <summary>One Spotify Connect device as /me/player/devices reports it.</summary>
public sealed record SpotifyDevice(string Id, string Name, bool IsActive, bool IsRestricted, int VolumePercent);

/// <summary>One of the operator's own playlists, for the "add from my playlists" picker.</summary>
public sealed record SpotifyPlaylistRef(string Uri, string Name, int Tracks)
{
    public override string ToString() => Tracks > 0 ? $"{Name} ({Tracks})" : Name;
}

/// <summary>What Spotify says is actually happening — the read-back the desk and the remote show.</summary>
public sealed record SpotifyNowPlaying(bool IsPlaying, string Track, string Artist, string DeviceId,
                                       string DeviceName, int VolumePercent)
{
    public string Line => Track.Length == 0 ? "" : Artist.Length == 0 ? Track : $"{Artist} · {Track}";
}

/// <summary>One request the transport must make; a plain record so a test can be the transport.</summary>
public readonly record struct SpotifyRequest(string Method, string Url, string? Body = null,
                                             string? Bearer = null, string ContentType = "application/json");

/// <summary>A reply, or StatusCode 0 for "never reached Spotify" (DNS, timeout, offline).</summary>
public readonly record struct SpotifyReply(int StatusCode, string Body, int RetryAfterSeconds = 0)
{
    public bool Ok => StatusCode is >= 200 and < 300;
}

/// <summary>2, 4, 8, 15, 30, 60 s, capped — the <see cref="SupervisorPolicy"/> shape.</summary>
public static class SpotifyBackoff
{
    private static readonly int[] DelaySeconds = { 2, 4, 8, 15, 30, 60 };

    public static TimeSpan Delay(int consecutiveFailures)
    {
        var i = Math.Clamp(consecutiveFailures, 1, DelaySeconds.Length) - 1;
        return TimeSpan.FromSeconds(DelaySeconds[i]);
    }
}

/// <summary>Spotify's JSON in, plain records out. Pure — the <see cref="FeedParser"/> of this feature.</summary>
public static class SpotifyJson
{
    /// <summary>
    /// A token reply. Spotify usually omits <c>refresh_token</c> on a refresh, so the one already
    /// stored is kept unless a new one arrives.
    /// </summary>
    public static bool TryReadToken(string body, DateTime nowUtc, string keepRefreshToken, out SpotifyToken token)
    {
        token = default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var access = Str(root, "access_token");
            if (access.Length == 0) return false;
            var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n) ? n : 3600;
            var refresh = Str(root, "refresh_token");
            token = new SpotifyToken(access, refresh.Length > 0 ? refresh : keepRefreshToken ?? "",
                                     nowUtc.AddSeconds(seconds));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify token reply unreadable.", ex);
            return false;
        }
    }

    public static IReadOnlyList<SpotifyDevice> ReadDevices(string body)
    {
        var list = new List<SpotifyDevice>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            {
                return list;
            }
            foreach (var d in devices.EnumerateArray())
            {
                var id = Str(d, "id");
                if (id.Length == 0) continue;
                list.Add(new SpotifyDevice(id, Str(d, "name"), Bool(d, "is_active"), Bool(d, "is_restricted"),
                                           Int(d, "volume_percent")));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify device list unreadable.", ex);
        }
        return list;
    }

    /// <summary>204 / "" / "{}" / junk → null, never a throw: a read-back must not be able to crash a poll.</summary>
    public static SpotifyNowPlaying? ReadNowPlaying(string body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var track = "";
            var artist = "";
            if (root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
            {
                track = Str(item, "name");
                if (item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in artists.EnumerateArray())
                    {
                        artist = Str(a, "name");
                        if (artist.Length > 0) break;
                    }
                }
            }
            var deviceId = "";
            var deviceName = "";
            var volume = 0;
            if (root.TryGetProperty("device", out var device) && device.ValueKind == JsonValueKind.Object)
            {
                deviceId = Str(device, "id");
                deviceName = Str(device, "name");
                volume = Int(device, "volume_percent");
            }
            var playing = Bool(root, "is_playing");
            if (!playing && track.Length == 0 && deviceName.Length == 0) return null;
            return new SpotifyNowPlaying(playing, track, artist, deviceId, deviceName, volume);
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify player state unreadable.", ex);
            return null;
        }
    }

    public static IReadOnlyList<SpotifyPlaylistRef> ReadPlaylists(string body)
    {
        var list = new List<SpotifyPlaylistRef>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return list;
            }
            foreach (var p in items.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var uri = Str(p, "uri");
                if (uri.Length == 0 && Str(p, "id") is { Length: > 0 } id) uri = "spotify:playlist:" + id;
                if (uri.Length == 0) continue;
                var tracks = p.TryGetProperty("tracks", out var t) && t.ValueKind == JsonValueKind.Object ? Int(t, "total") : 0;
                list.Add(new SpotifyPlaylistRef(uri, Str(p, "name"), tracks));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify playlist list unreadable.", ex);
        }
        return list;
    }

    /// <summary>The account line the Audio page shows: the display name and whether it is Premium.</summary>
    public static (string Name, bool Premium) ReadMe(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("", false);
            var name = Str(root, "display_name");
            if (name.Length == 0) name = Str(root, "id");
            return (name, string.Equals(Str(root, "product"), "premium", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify account reply unreadable.", ex);
            return ("", false);
        }
    }

    /// <summary>Null for a 2xx. Otherwise the sentence the operator reads and can act on.</summary>
    public static string? Failure(SpotifyReply reply)
    {
        if (reply.Ok) return null;
        return reply.StatusCode switch
        {
            0 => "Spotify is unavailable — check the network.",
            401 => "Spotify sign-in expired — press CONNECT on the Audio page.",
            403 => reply.Body.Contains("PREMIUM_REQUIRED", StringComparison.OrdinalIgnoreCase)
                ? "Spotify Premium is required to control playback."
                : "Spotify refused that — check the account is listed on your developer app.",
            404 => reply.Body.Contains("NO_ACTIVE_DEVICE", StringComparison.OrdinalIgnoreCase)
                ? "No Spotify device — open Spotify on the desk machine and press play once."
                : "Spotify could not find that playlist or track.",
            // Deliberately free of every failure word: a rate limit is transient and self-healing,
            // and must never flip a settling cue row to FailedLate.
            429 => $"Spotify is busy (rate limited) — retrying in {Math.Max(1, reply.RetryAfterSeconds)}s.",
            >= 500 and < 600 => $"Spotify service error ({reply.StatusCode}) — retrying.",
            _ => $"Spotify could not do that ({reply.StatusCode}).",
        };
    }

    /// <summary>The Retry-After header in seconds; 5 when it is absent or nonsense.</summary>
    public static int RetryAfterSeconds(string? header)
        => int.TryParse(header?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 5;

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static bool Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : 0;
}
