using Xunit;
using Xunit.Abstractions;

namespace Patterns.Core.Tests;

/// <summary>The suite's last test (TempFenceLast orders its collection after every other): every folder and file a test made under the temp path was removed by its test (tests/Shared/TempRun.cs).</summary>
[Collection(TempRun.FenceCollection)]
public sealed class TempFenceTests
{
    private readonly ITestOutputHelper _output;

    public TempFenceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TheRunLeavesNoTemporaryFolderOrFileOfItsOwnMakingBehind()
    {
        var left = TempRun.Judge(_output);
        Assert.True(left.Count == 0, "Left in the run's temporary folder:\n" + string.Join("\n", left));
    }
}
