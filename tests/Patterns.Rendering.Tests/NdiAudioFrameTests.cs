using System.Runtime.InteropServices;
using Patterns.Ndi;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The NDI audio frame the sender fills has the header's layout, field by field.</summary>
public class NdiAudioFrameTests
{
    [Fact]
    public void TheNdiAudioFrameMatchesTheHeadersLayout()
    {
        if (!Environment.Is64BitProcess) return;
        Assert.Equal(64, Marshal.SizeOf<NdiInterop.AudioFrameV3>());
        Assert.Equal(0, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.SampleRate)));
        Assert.Equal(16, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.Timecode)));
        Assert.Equal(24, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.FourCc)));
        Assert.Equal(32, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.Data)));
        Assert.Equal(40, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.ChannelStrideInBytes)));
        Assert.Equal(48, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.Metadata)));
        Assert.Equal(56, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.Timestamp)));
        Assert.Equal('F' | ('L' << 8) | ('T' << 16) | ('P' << 24), NdiInterop.FourCcFltp);
    }
}
