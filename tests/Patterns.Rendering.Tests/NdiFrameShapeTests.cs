using Patterns.Ndi;
using Xunit;

namespace Patterns.Rendering.Tests;

/// <summary>
/// Round 83 (L41): the NDI receiver copies by the runtime's own stride and width; a frame the runtime described
/// wrongly — a stride below a row, a colour format the receiver never asked for — is refused before the copy,
/// where before it was read past its buffer.
/// </summary>
public class NdiFrameShapeTests
{
    [Fact]
    public void AFrameWhoseStrideIsBelowARowOrWhoseFormatWasNotAskedForIsRefusedBeforeTheCopy()
    {
        var bgra = 'B' | ('G' << 8) | ('R' << 16) | ('A' << 24);
        var bgrx = NdiInterop.FourCcBgrx;
        var uyvy = 'U' | ('Y' << 8) | ('V' << 16) | ('Y' << 24);

        Assert.Null(NdiReceiver.FrameShapeProblem(1920, 1080, 7680, bgra));
        Assert.Null(NdiReceiver.FrameShapeProblem(1920, 1080, 8192, bgrx));           // padded rows are the runtime's to pad
        Assert.Contains("stride", NdiReceiver.FrameShapeProblem(1920, 1080, 7679, bgra));
        Assert.Contains("stride", NdiReceiver.FrameShapeProblem(1920, 1080, 0, bgra));
        Assert.Contains("stride", NdiReceiver.FrameShapeProblem(1920, 1080, -7680, bgra));
        Assert.Contains("colour format", NdiReceiver.FrameShapeProblem(1920, 1080, 3840, uyvy));
        Assert.Contains("empty", NdiReceiver.FrameShapeProblem(0, 1080, 7680, bgra));
        Assert.Contains("empty", NdiReceiver.FrameShapeProblem(1920, -1, 7680, bgra));
        Assert.Contains("stride", NdiReceiver.FrameShapeProblem(int.MaxValue, 1, int.MaxValue, bgra));   // a row past an int: refused, never overflowed into acceptance
    }
}
