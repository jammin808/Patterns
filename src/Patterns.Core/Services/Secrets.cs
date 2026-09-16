using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The show's secrets in one list (round 65.12): the property names that hold a credential — the
/// admin passcode, the management token, the remote's pairing token, a box's password, the
/// weather key, Spotify's tokens — and the one that hides under a plain name, the twin's link key
/// under Twin. Everything that leaves the machine reads this list: the support bundle's settings
/// and recovery files, and the twin's wire. A name is matched wherever it appears, so a new
/// section with a "Password" is covered the day it is written; a bare "Key" is never a secret
/// (an input label's key, an audio destination's key are identities) — only Twin.Key is.
/// </summary>
public static class Secrets
{
    public const string Mask = "•••";

    /// <summary>The property names that hold a credential, wherever they appear.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "AdminPasscode", "ManagementToken", "ClientSecret", "ClientId", "RefreshToken", "AccessToken", "ApiKey", "Password", "Passcode", "Token", "Secret",
    };

    /// <summary>The properties that are secrets by their place, not their name: the twin's link key.</summary>
    public static readonly IReadOnlyList<(string Section, string Property)> Placed = new[] { (nameof(ShowState.Twin), nameof(TwinConfig.Key)) };

    private static readonly Regex Fallback = new(
        "(\"(AdminPasscode|ManagementToken|ClientSecret|ClientId|RefreshToken|AccessToken|ApiKey|Password|Passcode|Token|Secret)\"\\s*:\\s*\")([^\"]*)(\")",
        RegexOptions.Compiled);

    /// <summary>
    /// The JSON with every secret masked (an empty one stays empty): parsed and walked when it is
    /// JSON, so the mask lands on the property and never on a value that merely looks like one;
    /// the regex over the named properties when it is not.
    /// </summary>
    public static string Redact(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            root = null;
        }
        if (root is not JsonObject obj) return Fallback.Replace(json, m => m.Groups[1].Value + (m.Groups[3].Value.Length == 0 ? "" : Mask) + m.Groups[4].Value);
        Blank(obj, Mask);
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>
    /// Every secret in the tree replaced in place — by name anywhere, by place for the twin's key —
    /// with <paramref name="replacement"/>; an empty value stays empty. The bundle masks; the twin's
    /// wire blanks, so the standby keeps its own.
    /// </summary>
    public static int Blank(JsonObject root, string replacement)
    {
        var count = 0;
        Walk(root, ref count);
        foreach (var (section, property) in Placed)
        {
            if (root[section] is JsonObject s && s[property] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0)
            {
                s[property] = replacement;
                count++;
            }
        }
        return count;

        void Walk(JsonNode? node, ref int n)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToList())
                    {
                        if (Names.Contains(key) && o[key] is JsonValue v && v.TryGetValue<string>(out var text))
                        {
                            if (text.Length > 0)
                            {
                                o[key] = replacement;
                                n++;
                            }
                            continue;
                        }
                        Walk(o[key], ref n);
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a) Walk(item, ref n);
                    break;
            }
        }
    }

    /// <summary>Whether the text still carries a secret's value — the check a bundle test runs over what it wrote.</summary>
    public static bool Carries(string json, string secretValue) => secretValue.Length > 0 && json.Contains(secretValue, StringComparison.Ordinal);
}
