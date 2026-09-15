using System.Security.Cryptography;
using System.Text;

namespace Patterns.Core.Services;

/// <summary>
/// The show's pairing token: what a remote presents before the desk runs its mutating verbs —
/// AUTH on the wire, X-Patterns-Token on the web (round 65). Twelve symbols in three groups from
/// an alphabet a person can read out over a radio and type on a phone (no 0/O, no 1/I/L), compared
/// in constant time with dashes, spaces and case ignored. Empty is open: the network as it was,
/// with Super Check saying so. The token is the show's — saved with it, mirrored to the twin so a
/// standby that takes over answers the same remotes — and never in a URL.
/// </summary>
public static class PairingToken
{
    /// <summary>The alphabet: upper-case letters and digits with the look-alikes taken out.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>Symbols in a token: 31^12 ≈ 8 × 10^17, far past what a wire could try.</summary>
    public const int Length = 12;

    /// <summary>A fresh token, grouped in fours ("K7QM-3XWD-P9RA") from the system's random source.</summary>
    public static string New()
    {
        var symbols = RandomNumberGenerator.GetString(Alphabet, Length);
        return string.Join('-', Enumerable.Range(0, Length / 4).Select(i => symbols.Substring(i * 4, 4)));
    }

    /// <summary>Whether remotes must present a token: one is set.</summary>
    public static bool Needed(string? configured) => Normal(configured).Length > 0;

    /// <summary>
    /// True when <paramref name="offered"/> is the token — dashes, spaces and case ignored, the
    /// compare in constant time. False when no token is set: an open desk has nothing to match,
    /// and the callers ask <see cref="Needed"/> first.
    /// </summary>
    public static bool Matches(string? configured, string? offered)
    {
        var a = Encoding.UTF8.GetBytes(Normal(configured));
        var b = Encoding.UTF8.GetBytes(Normal(offered));
        return a.Length > 0 && a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>A token as compared: no dashes or spaces, upper case.</summary>
    public static string Normal(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        var sb = new StringBuilder(token.Length);
        foreach (var c in token)
        {
            if (c is '-' or ' ' or '\t' or '\r' or '\n') continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }
}
