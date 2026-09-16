using System.Buffers;

namespace Patterns.Core.Services;

/// <summary>
/// Round 70: the character sets the desk searches text for, built once (CA1870). A search for any of a
/// known set is vectorised when the set is known ahead; an array literal at the call is built and scanned
/// on every call.
/// </summary>
public static class Separators
{
    /// <summary>Both path separators: show files travel between machines, whatever the host uses.</summary>
    public static readonly SearchValues<char> Path = SearchValues.Create("/\\");

    /// <summary>Where a URL's path ends: at its query or its fragment.</summary>
    public static readonly SearchValues<char> UrlPathEnd = SearchValues.Create("?#");
}
