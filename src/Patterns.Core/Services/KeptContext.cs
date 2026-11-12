using System.Security.Cryptography;
using System.Text;

namespace Patterns.Core.Services;

/// <summary>
/// Another app's context as its receiver keeps it, with its revision (the Eye design's D9): the revision moves when the context's
/// text without its moment (<c>at</c> 0) moves, and only then — reckoned once per arrival, on the thread that received it, never
/// by a reader. The Eye's hash reads the revision and never the context, so a context sent again unchanged rebuilds nothing and a
/// changed one rebuilds once. Revisions come from one process-wide count, so two sources, or one source dialled again, never
/// share one. What is compared is the steady text's SHA-256, so the context is kept once, not twice. Immutable: a receiver
/// publishes a new one with one reference write.
/// </summary>
public sealed record KeptContext(ShowContext Context, long Rev, string Digest)
{
    private static long s_last;

    /// <summary>
    /// What the receiver keeps after <paramref name="context"/> arrives: <paramref name="was"/>'s revision when the context says
    /// what it said (its moment aside), else the next revision. The context kept is the newest either way.
    /// </summary>
    public static KeptContext Next(KeptContext? was, ShowContext context)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((context with { At = 0 }).ToJson())));
        return was is not null && string.Equals(was.Digest, digest, StringComparison.Ordinal)
            ? was with { Context = context }
            : new KeptContext(context, Interlocked.Increment(ref s_last), digest);
    }
}
