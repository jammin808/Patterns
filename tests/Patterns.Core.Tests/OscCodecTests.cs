using System.Buffers.Binary;
using System.Text;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The OSC wire format: every type both ways, padding, bundles, and what a bad packet does.</summary>
public class OscCodecTests
{
    [Fact]
    public void EveryTypeRoundTripsAndPadsToFourBytes()
    {
        var m = OscMessage.Of("/patterns/look", 2, 0.5f, "Walk-in", new byte[] { 1, 2, 3 }, true, false, null, OscImpulse.Instance, 1.25, 7L);
        var bytes = OscCodec.Encode(m);
        Assert.Equal(0, bytes.Length % 4);
        var back = Assert.Single(OscCodec.Decode(bytes));
        Assert.Equal("/patterns/look", back.Address);
        Assert.Equal(10, back.Args.Count);
        Assert.Equal(2, back.Args[0]);
        Assert.Equal(0.5f, back.Args[1]);
        Assert.Equal("Walk-in", back.Args[2]);
        Assert.Equal(new byte[] { 1, 2, 3 }, back.Args[3]);
        Assert.Equal(true, back.Args[4]);
        Assert.Equal(false, back.Args[5]);
        Assert.Null(back.Args[6]);
        Assert.Same(OscImpulse.Instance, back.Args[7]);
        Assert.Equal(1.25, back.Args[8]);
        Assert.Equal(7L, back.Args[9]);

        // The address is null-terminated and padded; the type tag string follows at a four-byte boundary.
        Assert.Equal("/patterns/look\0\0", Encoding.ASCII.GetString(bytes, 0, 16));
        Assert.Equal((byte)',', bytes[16]);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(28, 4))); // ",ifsbTFNIdh" + pad = 12 bytes → the int at 28
    }

    [Fact]
    public void NumberAndTextReadTheFirstArgumentEitherWay()
    {
        Assert.Equal(1, OscMessage.Of("/x", 1).Number());
        Assert.Equal(1, OscMessage.Of("/x", true).Number());
        Assert.Equal(0.5, OscMessage.Of("/x", 0.5f).Number()!.Value, 6);
        Assert.Null(OscMessage.Of("/x", "two").Number());
        Assert.Null(OscMessage.Of("/x").Number());
        Assert.Equal("two", OscMessage.Of("/x", "two").Text());
        Assert.Equal("3", OscMessage.Of("/x", 3).Text());
        Assert.Equal("1", OscMessage.Of("/x", true).Text());
        Assert.Null(OscMessage.Of("/x", new byte[1]).Text());
        Assert.Equal("/x 3 two", OscMessage.Of("/x", 3, "two").ToString());
    }

    [Fact]
    public void ABundleFlattensAndABadPacketYieldsNothing()
    {
        var a = OscCodec.Encode(OscMessage.Of("/a", 1));
        var b = OscCodec.Encode(OscMessage.Of("/b", "x"));
        var bundle = new List<byte>();
        bundle.AddRange(Encoding.ASCII.GetBytes("#bundle\0"));
        bundle.AddRange(new byte[8]); // time tag: immediately
        foreach (var element in new[] { a, b })
        {
            var size = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(size, element.Length);
            bundle.AddRange(size);
            bundle.AddRange(element);
        }
        var messages = OscCodec.Decode(bundle.ToArray());
        Assert.Equal(2, messages.Count);
        Assert.Equal("/a", messages[0].Address);
        Assert.Equal("/b", messages[1].Address);
        Assert.Equal("x", messages[1].Args[0]);

        Assert.Empty(OscCodec.Decode(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n")));
        Assert.Empty(OscCodec.Decode(Array.Empty<byte>()));
        Assert.Empty(OscCodec.Decode(new byte[] { (byte)'/', (byte)'a' })); // no terminator
        // A message with no type tags is still a message.
        var bare = Assert.Single(OscCodec.Decode(Encoding.ASCII.GetBytes("/go\0")));
        Assert.Equal("/go", bare.Address);
        Assert.Empty(bare.Args);
        // A truncated blob drops the packet's arguments but nothing crashes.
        var truncated = OscCodec.Encode(OscMessage.Of("/blob", new byte[16]))[..20];
        Assert.Empty(OscCodec.Decode(truncated));
    }
}
