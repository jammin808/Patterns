using System.Globalization;
using Avalonia.Headless.XUnit;
using Xunit;
using Xunit.Abstractions;

namespace Patterns.App.Tests;

/// <summary>
/// The suite's last test (TempFenceLast orders its collection after every other): every folder and file a test made
/// under the temp path was removed by its test (tests/Shared/TempRun.cs). A desk the last test left open is closed
/// first, as the next test's boot would have closed it (<see cref="TestApp.CloseAll"/>), and its folder goes with it.
/// A desk closed late took its folder with it, so the fence does not judge it; it names each test that forgot to
/// close its desk (<see cref="TestApp.ClosedLate"/>), as the memlog's <c>late_close</c> lines do.
/// </summary>
[Collection(TempRun.FenceCollection)]
public sealed class TempFenceTests
{
    private readonly ITestOutputHelper _output;

    public TempFenceTests(ITestOutputHelper output) => _output = output;

    [AvaloniaFact]
    public void TheRunLeavesNoTemporaryFolderOrFileOfItsOwnMakingBehind()
    {
        TestApp.CloseAll();
        var late = TestApp.ClosedLate;
        _output.WriteLine(late.Count == 0
            ? "No desk was closed late: every test closed its own."
            : string.Create(CultureInfo.InvariantCulture,
                $"{late.Count} desk(s) closed late, their folders removed with them, by the next test's boot or this fence — the tests that forgot to close them (said, not judged): ")
              + string.Join(", ", late.GroupBy(t => t, StringComparer.Ordinal)
                                      .Select(g => g.Count() > 1 ? string.Create(CultureInfo.InvariantCulture, $"{g.Key} ×{g.Count()}") : g.Key)) + ".");
        var left = TempRun.Judge(_output);
        Assert.True(left.Count == 0, "Left in the run's temporary folder:\n" + string.Join("\n", left));
    }
}
