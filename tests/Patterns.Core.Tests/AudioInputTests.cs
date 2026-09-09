using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Which input the desk opens, decided from names alone. The rules exist because of two things a
/// rig does to a show file: nobody wants to name a device at all on a laptop, and Windows renames
/// a USB box when it moves socket.
/// </summary>
public class AudioInputTests
{
    private static readonly string[] Rig =
    {
        "Microphone (Realtek(R) Audio)",
        "Line In (2- Scarlett 2i2 USB)",
        "Digital Audio Interface (3- USB Capture HDMI+)",
    };

    [Fact]
    public void NothingNamedMeansTheMachinesOwnInput()
    {
        Assert.True(AudioInput.WantsDefault(""));
        Assert.True(AudioInput.WantsDefault("   "));
        Assert.True(AudioInput.WantsDefault(null));
        Assert.True(AudioInput.WantsDefault(AudioInput.DefaultDevice));
        Assert.True(AudioInput.WantsDefault(" default input "));
        Assert.False(AudioInput.WantsDefault("Line In (2- Scarlett 2i2 USB)"));

        // The default is never one of the named endpoints — it is whatever Windows says it is.
        Assert.Equal(-1, AudioInput.IndexOf(Rig, AudioInput.DefaultDevice));
        Assert.Equal(-1, AudioInput.IndexOf(Rig, ""));
    }

    [Fact]
    public void TheNameAsSavedWins()
    {
        Assert.Equal(0, AudioInput.IndexOf(Rig, "Microphone (Realtek(R) Audio)"));
        Assert.Equal(1, AudioInput.IndexOf(Rig, "  line in (2- Scarlett 2i2 USB)  "));
        Assert.Equal(2, AudioInput.IndexOf(Rig, "Digital Audio Interface (3- USB Capture HDMI+)"));
        Assert.Equal(-1, AudioInput.IndexOf(Rig, "Desk mic"));
        Assert.Equal(-1, AudioInput.IndexOf(Array.Empty<string>(), "Desk mic"));
    }

    [Fact]
    public void ACaptureCardMovedToAnotherSocketIsStillTheSameCard()
    {
        // Windows stamps the socket into the name: the card the show was built on was on 2, and it
        // is on 5 tonight because somebody moved it. Same box, same show, no re-picking at doors.
        Assert.Equal(2, AudioInput.IndexOf(Rig, "Digital Audio Interface (2- USB Capture HDMI+)"));
        Assert.Equal(1, AudioInput.IndexOf(Rig, "Line In (Scarlett 2i2 USB)"));
        Assert.Equal(1, AudioInput.IndexOf(Rig, "Line In (11- Scarlett 2i2 USB)"));

        // The socket is the only thing it forgives: a different box is still a different box, and
        // the other channel of the same interface is a different input.
        Assert.Equal(-1, AudioInput.IndexOf(Rig, "Line In (2- Scarlett 4i4 USB)"));
        Assert.Equal(-1, AudioInput.IndexOf(Rig, "Microphone (2- Scarlett 2i2 USB)"));

        // And the exact name always wins over a loose match, whatever order the rig enumerates in.
        var both = new[] { "Line In (7- Scarlett 2i2 USB)", "Line In (2- Scarlett 2i2 USB)" };
        Assert.Equal(1, AudioInput.IndexOf(both, "Line In (2- Scarlett 2i2 USB)"));
    }

    [Fact]
    public void TheKeyIsTheNameWithoutTheThingsThatMove()
    {
        Assert.Equal("line in (scarlett 2i2 usb)", AudioInput.Key("Line In (2- Scarlett 2i2 USB)"));
        Assert.Equal("line in (scarlett 2i2 usb)", AudioInput.Key("  line   in  (2- scarlett 2i2 usb) "));
        Assert.Equal("", AudioInput.Key(""));
        Assert.Equal("", AudioInput.Key(null));

        // A number that is not a socket stamp stays: "(2 channels)" is part of the device's name.
        Assert.Equal("interface (2 channels)", AudioInput.Key("Interface (2 Channels)"));
        // Windows writes the socket as "12- ", with the space; a dash that is part of the device's
        // own name is not a socket and stays.
        Assert.Equal("mic (box)", AudioInput.Key("Mic (12- Box)"));
        Assert.Equal("mic (12-box)", AudioInput.Key("Mic (12-Box)"));
    }
}
