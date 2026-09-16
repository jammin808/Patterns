using System.Globalization;

namespace Patterns.Core.Services;

/// <summary>
/// Round 70: one number format and one language on every machine. The desk speaks English, and its
/// numbers, its dates, its files and its wire read the same in Hamburg and in Leeds: the process runs on
/// the invariant culture from its first line, so a 0.5 is never a 0,5 and a minus is never a Unicode minus.
/// The analyzers fence the explicit providers as well (CA1305, CA1310, S6580) — belt and braces: the
/// guard for the machine, the provider for the reader of the code.
/// </summary>
public static class CultureGuard
{
    /// <summary>Whether <see cref="Apply"/> has run in this process.</summary>
    public static bool Applied { get; private set; }

    /// <summary>The invariant culture for this thread, every thread made after it, and the UI culture. Idempotent.</summary>
    public static void Apply()
    {
        var invariant = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = invariant;
        CultureInfo.DefaultThreadCurrentUICulture = invariant;
        CultureInfo.CurrentCulture = invariant;
        CultureInfo.CurrentUICulture = invariant;
        Applied = true;
    }

    /// <summary>"culture invariant · 0.5 · 2026-01-31" — the support info's line, from the culture this thread is really on.</summary>
    public static string Words
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            var name = culture.Name.Length == 0 ? "invariant" : culture.Name;
            var sample = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);
            return $"culture {name} · {0.5.ToString(culture)} · {sample.ToString("d", culture)}{(Applied ? "" : " (the guard has not run)")}";
        }
    }
}
