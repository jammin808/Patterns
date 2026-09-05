namespace Patterns.Core.Services;

/// <summary>What an operator types as a page address, made into something a browser opens. Pure.</summary>
public static class WebAddress
{
    /// <summary>Normalises operator input: a bare "example.com" becomes https; a scheme or a file on disk stays as typed.</summary>
    public static string Normalize(string input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return s;
        if (s.Contains("://") || File.Exists(s)) return s;
        return "https://" + s;
    }

    /// <summary>A short name for a page — its host, or a local file's name — for labels and status lines.</summary>
    public static string ShortName(string url)
    {
        var s = (url ?? "").Trim();
        if (s.Length == 0) return "";
        if (Uri.TryCreate(Normalize(s), UriKind.Absolute, out var uri))
        {
            if (uri.IsFile) return LastSegment(Uri.UnescapeDataString(uri.LocalPath));
            if (uri.Host.Length > 0) return uri.Host;
        }
        return LastSegment(s);
    }

    /// <summary>The part after the last slash of either kind (a Windows path reads the same on every machine).</summary>
    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.Length > 0 ? name : path;
    }
}
