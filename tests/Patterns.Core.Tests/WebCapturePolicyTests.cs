using Patterns.Core.Media;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>What the browser is asked to hand over for a page on this machine (round 68).</summary>
public class WebCapturePolicyTests
{
    [Fact]
    public void ThePageIsCapturedAtItsOwnSizeWhenNothingSmallerDrawsIt()
    {
        var plan = WebCapturePolicy.Plan(1920, 1080, 0, 0, MachineClass.Standard, 0, 0);
        Assert.Equal((1920, 1080, 70, 1), (plan.MaxWidth, plan.MaxHeight, plan.JpegQuality, plan.EveryNthFrame));
        Assert.Equal(FrameSmoother.Bounds.For(MachineClass.Standard), plan.Smoothing);
        Assert.Equal("captured at 1920×1080 · q70", plan.Words);
    }

    [Fact]
    public void TheCaptureFitsTheDrawnSizeAndNeverExceedsTheViewport()
    {
        var fitted = WebCapturePolicy.Plan(1920, 1080, 1280, 720, MachineClass.Standard, 0, 30);
        Assert.Equal((1280, 720), (fitted.MaxWidth, fitted.MaxHeight));
        var portrait = WebCapturePolicy.Plan(1920, 1080, 1080, 1920, MachineClass.Standard, 0, 30);
        Assert.Equal((1080, 608), (portrait.MaxWidth, portrait.MaxHeight));                             // the page's aspect, inside the screen
        var wall = WebCapturePolicy.Plan(1920, 1080, 3840, 2160, MachineClass.Big, 0, 60);
        Assert.Equal((1920, 1080), (wall.MaxWidth, wall.MaxHeight));                                     // the browser is never asked to upscale
        Assert.Equal(80, wall.JpegQuality);
    }

    [Fact]
    public void ASmallMachineCapsAt720pOnceTheLadderHasStepped()
    {
        Assert.Equal((1920, 1080), Size(WebCapturePolicy.Plan(1920, 1080, 0, 0, MachineClass.Small, 0, 30)));
        Assert.Equal((1280, 720), Size(WebCapturePolicy.Plan(1920, 1080, 0, 0, MachineClass.Small, 1, 30)));
        Assert.Equal((1280, 720), Size(WebCapturePolicy.Plan(1920, 1080, 3840, 2160, MachineClass.Small, 3, 30)));
        Assert.Equal((1024, 576), Size(WebCapturePolicy.Plan(1920, 1080, 1024, 576, MachineClass.Small, 1, 30)));   // already smaller: left alone
        Assert.Equal(60, WebCapturePolicy.QualityFor(MachineClass.Small));
        Assert.Equal(ScreencastFrame.Quality, WebCapturePolicy.QualityFor(MachineClass.Standard));
    }

    [Fact]
    public void OnlyAFastPageOnAnEconomyLadderIsCapturedEverySecondFrame()
    {
        Assert.Equal(2, WebCapturePolicy.Plan(1920, 1080, 0, 0, MachineClass.Standard, 2, 60).EveryNthFrame);
        Assert.Equal(1, WebCapturePolicy.Plan(1920, 1080, 0, 0, MachineClass.Standard, 2, 30).EveryNthFrame);
        Assert.Equal(1, WebCapturePolicy.Plan(1920, 1080, 0, 0, MachineClass.Standard, 2, 44).EveryNthFrame);      // a 30 fps video's wobble never trips it
        Assert.Equal(1, WebCapturePolicy.Plan(1920, 1080, 0, 0, MachineClass.Standard, 1, 60).EveryNthFrame);
        Assert.Equal(2, WebCapturePolicy.Plan(1920, 1080, 0, 0, MachineClass.Small, 3, 60).EveryNthFrame);
        Assert.Equal("captured at 1280×720 · q60 · every 2nd frame", WebCapturePolicy.Plan(1920, 1080, 0, 0, MachineClass.Small, 3, 60).Words);
    }

    private static (int, int) Size(WebCapturePlan plan) => (plan.MaxWidth, plan.MaxHeight);
}
