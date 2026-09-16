using System.Globalization;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 70: the desk runs on the invariant culture from its first line, so a German or Swedish machine
/// formats and parses the wire, the files and the words exactly as an English one does.
/// </summary>
public class CultureGuardTests
{
    [Fact]
    public async Task TheGuardPutsTheProcessOnTheInvariantCultureWhateverTheMachineHad()
    {
        var was = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture, CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");                                       // a Hamburg desk: 0,5 and 31.01.2026
            Assert.Equal("0,5", 0.5.ToString(CultureInfo.CurrentCulture));
            CultureGuard.Apply();
            Assert.True(CultureGuard.Applied);
            Assert.Equal(CultureInfo.InvariantCulture, CultureInfo.CurrentCulture);
            Assert.Equal(CultureInfo.InvariantCulture, CultureInfo.DefaultThreadCurrentCulture);
            Assert.Equal("0.5", 0.5.ToString(CultureInfo.CurrentCulture));
            Assert.Equal(0.5, double.Parse("0.5", CultureInfo.CurrentCulture));
            Assert.Equal("-1", (-1).ToString(CultureInfo.CurrentCulture));
            Assert.StartsWith("culture invariant · 0.5 · 01/31/2026", CultureGuard.Words);
            var onAWorker = await Task.Run(() => 0.5.ToString(CultureInfo.CurrentCulture));                       // a thread made after the guard
            Assert.Equal("0.5", onAWorker);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = was.Item3;
            CultureInfo.DefaultThreadCurrentUICulture = was.Item4;
            CultureInfo.CurrentCulture = was.Item1;
            CultureInfo.CurrentUICulture = was.Item2;
        }
    }
}
