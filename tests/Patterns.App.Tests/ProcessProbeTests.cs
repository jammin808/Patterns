using System.Diagnostics;
using Patterns.App.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The real probe: a process ended is a process gone, and "ended" is only said once it is.</summary>
public class ProcessProbeTests
{
    [Fact]
    public void TheProbeEndsAProcessAndOnlySaysSoOnceItIsGone()
    {
        if (OperatingSystem.IsWindows()) return;                           // `sleep` is the Unix runner's; the probe's code is the same on both
        using var child = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        var probe = new SystemProcessProbe();
        try
        {
            Assert.NotNull(probe.StartTicks(child.Id));
            Assert.True(probe.Kill(child.Id));
            Assert.True(child.HasExited);
            Assert.Null(probe.StartTicks(child.Id));
            Assert.False(probe.Kill(child.Id));                            // gone already: not ended by us
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }
}
