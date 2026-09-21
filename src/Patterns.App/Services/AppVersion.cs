using System.Reflection;

namespace Patterns.App.Services;

/// <summary>
/// This build's version, as the wire, the beacon, the pages, STATE and the Eye say it. Round 79: the informational
/// version — what a publish stamps (CI: 0.&lt;round&gt;.&lt;run&gt;+round-NN.&lt;sha&gt;; a local publish 0.&lt;round&gt;.0+round-NN.&lt;sha&gt;;
/// a plain build 1.0.0+&lt;sha&gt;) — with the commit cut to seven characters, so "which build is this" has one answer on every
/// surface; the assembly version alone ("1.0.0.0") when a build carries no informational version, "dev" with neither.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Read(typeof(AppVersion).Assembly);

    /// <summary>The words for an assembly's version attributes (tests hand in their own).</summary>
    public static string Read(Assembly assembly)
        => Shorten(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
           ?? assembly.GetName().Version?.ToString()
           ?? "dev";

    /// <summary>"0.79.312+round-79.9410292a1b2c3d4e5f…" → "0.79.312+round-79.9410292": the metadata's last dotted part, a commit, cut to seven; null for nothing.</summary>
    public static string? Shorten(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return null;
        var plus = informational.IndexOf('+');
        if (plus < 0) return informational;
        var head = informational[..plus];
        var meta = informational[(plus + 1)..];
        var lastDot = meta.LastIndexOf('.');
        var commit = lastDot >= 0 ? meta[(lastDot + 1)..] : meta;
        if (commit.Length > 7 && commit.All(Uri.IsHexDigit)) commit = commit[..7];
        var prefix = lastDot >= 0 ? meta[..(lastDot + 1)] : "";
        return head + "+" + prefix + commit;
    }
}
