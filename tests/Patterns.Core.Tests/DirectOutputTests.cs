using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Direct output: the decision, the card rule, the status lines, the super-check row, the flag in the show.</summary>
public class DirectOutputTests
{
    private static readonly GpuAdapterInfo Discrete = new("NVIDIA GeForce RTX 4070", GpuAdapterInfo.VendorNvidia, 1, 12288, 7, false);
    private static readonly GpuAdapterInfo Integrated = new("Intel Iris Xe", GpuAdapterInfo.VendorIntel, 2, 128, 8, false);
    private static readonly GpuAdapterInfo Software = new("Microsoft Basic Render Driver", GpuAdapterInfo.VendorMicrosoft, 3, 0, 9, true);

    private static DirectOutputFacts Facts(bool asks = true, IReadOnlyList<GpuAdapterInfo>? adapters = null, string active = "",
        bool windows = true, int build = 22631, bool fuse = false)
        => new(windows, build, asks, adapters ?? new[] { Discrete }, active, fuse);

    [Fact]
    public void TheDecisionFollowsTheFacts()
    {
        var plan = DirectOutput.Decide(Facts());
        Assert.Equal(DirectOutputMode.LowLatencySwapChain, plan.Mode);
        Assert.True(plan.CardSuitable);
        Assert.Contains("discrete card", plan.Reason);

        var elsewhere = DirectOutput.Decide(Facts(windows: false));
        Assert.Equal(DirectOutputMode.Composed, elsewhere.Mode);
        Assert.Contains("Windows-only", elsewhere.Reason);
        Assert.Contains("No output asks", DirectOutput.Decide(Facts(asks: false)).Reason);
        var held = DirectOutput.Decide(Facts(fuse: true));
        Assert.Equal(DirectOutputMode.Composed, held.Mode);
        Assert.Contains("Held off", held.Reason);
        Assert.True(held.CardSuitable); // the card is fine; the fuse is what holds it
        Assert.Contains("Windows 10", DirectOutput.Decide(Facts(build: 9600)).Reason);
        Assert.Equal(DirectOutputMode.LowLatencySwapChain, DirectOutput.Decide(Facts(build: 0)).Mode); // an unknown build never refuses
    }

    [Fact]
    public void TheCardMustBeHardware()
    {
        // The software renderer active: refused, whatever else is in the machine.
        var plan = DirectOutput.Decide(Facts(adapters: new[] { Discrete, Software }, active: Software.Name));
        Assert.Equal(DirectOutputMode.Composed, plan.Mode);
        Assert.False(plan.CardSuitable);
        Assert.Contains("software renderer", plan.Reason);

        // An integrated card is a hardware card: good enough for the flip.
        var igpu = DirectOutput.Decide(Facts(adapters: new[] { Integrated }, active: Integrated.Name));
        Assert.Equal(DirectOutputMode.LowLatencySwapChain, igpu.Mode);
        Assert.Contains("hardware card", igpu.Reason);

        // No active name yet (before the renderer answered): the best card decides.
        Assert.True(DirectOutput.CardSuitable(new[] { Software, Discrete }, "", out var note));
        Assert.Contains(Discrete.Name, note);
        Assert.False(DirectOutput.CardSuitable(Array.Empty<GpuAdapterInfo>(), "", out note));
        Assert.Contains("no graphics card", note);
        Assert.Contains("no graphics card", DirectOutput.Decide(Facts(adapters: Array.Empty<GpuAdapterInfo>())).Reason);
    }

    [Fact]
    public void TheStatusLinesSayWhatIsInForceAndWhatIsNot()
    {
        var wanted = DirectOutput.Decide(Facts());
        Assert.StartsWith("Composed by the desktop:", DirectOutput.Status(false, DirectOutputMode.Composed, wanted));
        Assert.Contains("tick to prepare", DirectOutput.Status(false, DirectOutputMode.LowLatencySwapChain, wanted));
        Assert.StartsWith("DIRECT", DirectOutput.Status(true, DirectOutputMode.LowLatencySwapChain, wanted));
        Assert.Contains("another app", DirectOutput.Status(true, DirectOutputMode.LowLatencySwapChain, wanted));
        Assert.StartsWith("Restart Patterns", DirectOutput.Status(true, DirectOutputMode.Composed, wanted));
        var refused = DirectOutput.Decide(Facts(adapters: new[] { Software }, active: Software.Name));
        Assert.Equal(refused.Reason, DirectOutput.Status(true, DirectOutputMode.Composed, refused));

        Assert.StartsWith("Off", DirectOutput.Summary(0, DirectOutputMode.Composed, DirectOutput.Decide(Facts(asks: false))));
        Assert.Equal("2 outputs ask · low-latency swap chain in force from this start.", DirectOutput.Summary(2, DirectOutputMode.LowLatencySwapChain, wanted));
        Assert.Equal("1 output asks · restart Patterns to take effect.", DirectOutput.Summary(1, DirectOutputMode.Composed, wanted));
        Assert.Contains("no output asks any more", DirectOutput.Summary(0, DirectOutputMode.LowLatencySwapChain, DirectOutput.Decide(Facts(asks: false))));
        Assert.Contains(refused.Reason, DirectOutput.Summary(1, DirectOutputMode.Composed, refused));
    }

    [Fact]
    public void TheSuperCheckLightsTheRowAndTheFlagTravelsInTheShow()
    {
        var waiting = new CheckFacts
        {
            Gpus = new[] { Discrete },
            ActiveGpu = Discrete.Name,
            DirectOutputsAsking = 1,
            DirectOutputInForce = false,
            DirectOutputSummary = "1 output asks · restart Patterns to take effect.",
        };
        var row = Assert.Single(SuperCheck.Run(waiting).Rows, r => r.Item == "Direct output");
        Assert.Equal(CheckLight.Amber, row.Light);
        Assert.Contains("restart", row.Value);
        Assert.Contains("not in force", row.Note);

        var inForce = new CheckFacts
        {
            Gpus = new[] { Discrete },
            ActiveGpu = Discrete.Name,
            DirectOutputsAsking = 1,
            DirectOutputInForce = true,
            DirectOutputSummary = "1 output asks · low-latency swap chain in force from this start.",
        };
        row = Assert.Single(SuperCheck.Run(inForce).Rows, r => r.Item == "Direct output");
        Assert.Equal(CheckLight.Green, row.Light);
        Assert.Equal("", row.Note);

        row = Assert.Single(SuperCheck.Run(new CheckFacts { DirectOutputSummary = "Off — every output is composed by the desktop." }).Rows, r => r.Item == "Direct output");
        Assert.Equal(CheckLight.Grey, row.Light);
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Item == "Direct output");

        var state = new ShowState();
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", DirectOutput = true });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "b" });
        var back = JsonUtil.Deserialize<ShowState>(JsonUtil.Serialize(state))!;
        Assert.True(back.Output.Placements[0].DirectOutput);
        Assert.False(back.Output.Placements[1].DirectOutput);
    }
}
