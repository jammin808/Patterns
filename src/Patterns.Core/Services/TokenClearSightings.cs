using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>
/// One machine this desk's pairing token arrived from in clear: its address (IPv4 written plainly), how it came — "web"
/// (X-Patterns-Token or the pages' cookie on the web remote's plain listener) or "wire" (plain AUTH on the Companion
/// port) — when first and last, and how many times.
/// </summary>
public sealed record ClearSighting(string Address, string Via, DateTime FirstUtc, DateTime LastUtc, long Count);

/// <summary>
/// Round 89 (PeerLink profile 13.6.9): where this desk's pairing token was seen in clear from another machine — the
/// receiver's backstop for a sender that still puts it on the network unencrypted. Refusing nothing: each address is
/// marked once per token, so the Super Check, the Nodes page and STATE say "rotate it: NEW TOKEN" and the caller writes
/// one journal row per address. The marks belong to the token they were seen for: once the token changes (NEW TOKEN, or
/// one typed) none stands. Bounded to <see cref="MaxAddresses"/> addresses, the one heard longest ago giving way.
/// Thread-safe: the socket handlers note, the desk reads. The token itself is never kept — only a keyed hash (a random
/// salt of this store's own) that tells one token from the next.
///
/// Kept across a restart when given a folder (beside the settings, <see cref="FileName"/>): a token seen in clear stays
/// compromised whatever restarts, so its marks stand until the token changes. The file is written whole at each first
/// sighting from an address, and at most once a minute for the repeats' times and counts; a file that cannot be read is
/// said once and the store starts empty.
/// </summary>
public sealed class TokenClearSightings
{
    /// <summary>The addresses kept for one token.</summary>
    public const int MaxAddresses = 32;

    /// <summary>How many addresses the words name before they count the rest.</summary>
    public const int Named = 4;

    /// <summary>The marks' file beside the settings.</summary>
    public const string FileName = "patterns.clearseen.json";

    /// <summary>How often, at most, a repeat's time and count are written; a first sighting is written at once.</summary>
    public static readonly TimeSpan RepeatsWrittenEvery = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly List<ClearSighting> _seen = new();
    private readonly string? _path;
    private byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private string _tokenKey = "";
    private DateTime _writtenUtc = DateTime.MinValue;
    private bool _writeFaultSaid;

    /// <summary>A store in memory only.</summary>
    public TokenClearSightings()
    {
    }

    /// <param name="folder">Where the marks are kept across a restart (the settings' folder); they are read from there now.</param>
    public TokenClearSightings(string folder)
    {
        _path = Path.Combine(folder, FileName);
        Read();
    }

    /// <summary>
    /// The token <paramref name="token"/> (the one set now) was seen in clear from <paramref name="address"/>. True the
    /// first time for that address and token — the caller journals it then, and only then; false for a repeat, or when no
    /// token is set (an open desk has nothing to rotate).
    /// </summary>
    public bool Note(string token, string address, string via, DateTime utcNow)
    {
        var key = Key(token);
        if (key.Length == 0) return false;
        var where = (address ?? "").Trim();
        var how = via is "web" or "wire" ? via : "web";
        lock (_gate)
        {
            if (!string.Equals(key, _tokenKey, StringComparison.Ordinal))
            {
                _seen.Clear();
                _tokenKey = key;
            }
            var at = _seen.FindIndex(s => string.Equals(s.Address, where, StringComparison.Ordinal));
            if (at >= 0)
            {
                var was = _seen[at];
                _seen[at] = was with { Via = Join(was.Via, how), LastUtc = utcNow, Count = was.Count + 1 };
                if (utcNow - _writtenUtc >= RepeatsWrittenEvery) Write(utcNow);
                return false;
            }
            if (_seen.Count >= MaxAddresses)
            {
                var oldest = 0;
                for (var i = 1; i < _seen.Count; i++)
                    if (_seen[i].LastUtc < _seen[oldest].LastUtc) oldest = i;
                _seen.RemoveAt(oldest);
            }
            _seen.Add(new ClearSighting(where, how, utcNow, utcNow, 1));
            Write(utcNow);
            return true;
        }
    }

    /// <summary>The sightings of the token set now, first seen first; none once the token has changed, or with none set.</summary>
    public IReadOnlyList<ClearSighting> For(string token)
    {
        var key = Key(token);
        if (key.Length == 0) return Array.Empty<ClearSighting>();
        lock (_gate)
        {
            return string.Equals(key, _tokenKey, StringComparison.Ordinal) ? _seen.OrderBy(s => s.FirstUtc).ToList() : Array.Empty<ClearSighting>();
        }
    }

    /// <summary>"seen in clear from 10.0.0.5 — rotate it: NEW TOKEN" — the first <see cref="Named"/> addresses, then a count; "" for none.</summary>
    public static string Words(IReadOnlyList<ClearSighting> seen)
    {
        if (seen.Count == 0) return "";
        var named = string.Join(", ", seen.Take(Named).Select(s => s.Address));
        var more = seen.Count > Named ? $" and {(seen.Count - Named).ToString(System.Globalization.CultureInfo.InvariantCulture)} more" : "";
        return $"seen in clear from {named}{more} — rotate it: NEW TOKEN";
    }

    /// <summary>How the token came, in words: "over plain http" for the web, "as plain AUTH on the wire" for the wire, both joined.</summary>
    public static string HowWords(string via) => string.Join(" and ", via.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(v => v == "wire" ? "as plain AUTH on the wire" : "over plain http"));

    private static string Join(string was, string how) => was.Split(',', StringSplitOptions.TrimEntries).Contains(how, StringComparer.Ordinal) ? was : was + "," + how;

    private string Key(string? token)
    {
        var normal = PairingToken.Normal(token);
        if (normal.Length == 0) return "";
        lock (_gate) return Convert.ToHexString(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(normal)));
    }

    /// <summary>The marks as kept: the salt, the keyed hash of the token they were seen for, each address. Under the gate.</summary>
    private void Write(DateTime utcNow)
    {
        if (_path is null) return;
        _writtenUtc = utcNow;
        var json = JsonSerializer.Serialize(new
        {
            v = 1,
            salt = Convert.ToBase64String(_salt),
            token = _tokenKey,
            seen = _seen.Select(s => new
            {
                address = s.Address,
                via = s.Via,
                first = s.FirstUtc.ToString("o", CultureInfo.InvariantCulture),
                last = s.LastUtc.ToString("o", CultureInfo.InvariantCulture),
                count = s.Count,
            }),
        });
        try
        {
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The marks stand in memory; the next first sighting tries again. Said once.
            if (_writeFaultSaid) return;
            _writeFaultSaid = true;
            Log.Warn("Remote: the token-seen-in-clear marks could not be kept for a restart.", ex);
        }
    }

    /// <summary>The kept marks, when the file is there and whole; otherwise none, said once.</summary>
    private void Read()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var r = doc.RootElement;
            var salt = Convert.FromBase64String(r.GetProperty("salt").GetString() ?? "");
            var token = r.GetProperty("token").GetString() ?? "";
            if (salt.Length < 16 || token.Length == 0) return;
            var seen = new List<ClearSighting>();
            foreach (var e in r.GetProperty("seen").EnumerateArray().Take(MaxAddresses))
            {
                seen.Add(new ClearSighting(
                    e.GetProperty("address").GetString() ?? "",
                    e.GetProperty("via").GetString() ?? "web",
                    DateTime.Parse(e.GetProperty("first").GetString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    DateTime.Parse(e.GetProperty("last").GetString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    e.GetProperty("count").GetInt64()));
            }
            lock (_gate)
            {
                _salt = salt;
                _tokenKey = token;
                _seen.AddRange(seen);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn("Remote: the kept token-seen-in-clear marks could not be read — none stand from before this start.", ex);
        }
    }
}
