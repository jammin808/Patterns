namespace Patterns.Core.Services;

/// <summary>What this machine is signed in to Spotify as. Never inside a show file.</summary>
public sealed record SpotifyCredentials(string ClientId, string RefreshToken, string Account, DateTime SavedUtc)
{
    public static readonly SpotifyCredentials None = new("", "", "", DateTime.MinValue);

    public bool HasClientId => ClientId.Trim().Length > 0;

    public bool IsConnected => HasClientId && RefreshToken.Length > 0;
}

/// <summary>
/// The Spotify sign-in, kept beside the settings file and never inside a show: SaveTo writes the
/// whole ShowState into *.patshow.json and those files travel between machines. The Client ID lives
/// here too — it is minted in the operator's own developer dashboard paired with the refresh token
/// issued for it, so carrying one desk's Client ID to another produces the most confusing failure
/// class there is. The access token is never written to disk: it lives in a service field for an
/// hour. Atomic like everything else the store writes; an unreadable file is "not connected",
/// never a startup failure.
/// </summary>
public sealed class SpotifyCredentialStore
{
    public const string FileName = "patterns.spotify.json";

    public SpotifyCredentialStore(string directory) => Path = System.IO.Path.Combine(directory, FileName);

    public string Path { get; }

    public SpotifyCredentials Read()
    {
        try
        {
            if (!File.Exists(Path)) return SpotifyCredentials.None;
            return JsonUtil.Deserialize<SpotifyCredentials>(File.ReadAllText(Path)) ?? SpotifyCredentials.None;
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify sign-in file unreadable — treating this machine as not connected.", ex);
            return SpotifyCredentials.None;
        }
    }

    public void Write(SpotifyCredentials creds)
    {
        try
        {
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonUtil.Serialize(creds));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify sign-in file write failed.", ex);
        }
    }

    /// <summary>DISCONNECT: the sign-in leaves this machine.</summary>
    public void Clear()
    {
        try
        {
            File.Delete(Path);
        }
        catch
        {
            // Nothing to clear (or locked) — harmless either way.
        }
    }
}
