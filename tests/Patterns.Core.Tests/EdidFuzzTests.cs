using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 72: the EDID parser reads bytes a display sent — bytes nobody on the desk wrote. Every truncation,
/// every flipped byte, every claimed extension the block does not carry, and every garbage block parses to
/// a report with its problems named; nothing throws, and a short block always says it is short.
/// </summary>
public class EdidFuzzTests
{
    /// <summary>A deterministic generator of its own: the same bytes on every machine, no System.Random.</summary>
    private sealed class Lcg
    {
        private uint _state;
        public Lcg(uint seed) => _state = seed;
        public uint Next() => _state = unchecked(_state * 1664525u + 1013904223u);
        public int Next(int max) => (int)(Next() % (uint)max);
    }

    private static readonly byte[][] Samples = { EdidSamples.PatternsLed(), EdidSamples.PatternsLed(withDisplayId: false) };

    [Fact]
    public void EveryTruncationParsesAndSaysItIsShort()
    {
        foreach (var sample in Samples)
        {
            for (var length = 0; length <= sample.Length; length++)
            {
                var cut = sample.Take(length).ToArray();
                var info = Edid.Parse(cut);
                Assert.NotNull(info);
                if (length < 128) Assert.NotEmpty(info.Problems);
                Assert.Equal(length, info.Length);
            }
        }
        Assert.NotEmpty(Edid.Parse(null).Problems);
        Assert.NotEmpty(Edid.Parse(Array.Empty<byte>()).Problems);
    }

    [Fact]
    public void FlippedBytesBadChecksumsAndClaimedExtensionsNeverThrow()
    {
        var rng = new Lcg(20260916);
        foreach (var sample in Samples)
        {
            // Single-byte and multi-byte flips anywhere in the block, thousands of them.
            for (var i = 0; i < 3000; i++)
            {
                var bytes = (byte[])sample.Clone();
                var flips = 1 + rng.Next(4);
                for (var f = 0; f < flips; f++) bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
                var info = Edid.Parse(bytes);
                Assert.NotNull(info);
                Assert.Equal(bytes.Length, info.Length);
            }

            // A wrong checksum is a problem, not an exception.
            var badSum = (byte[])sample.Clone();
            badSum[127] ^= 0x5A;
            var summed = Edid.Parse(badSum);
            Assert.False(summed.BlockChecksums[0]);
            Assert.NotEmpty(summed.Problems);

            // The base block claims more extensions than it carries, or fewer, or every count there is.
            for (var claimed = 0; claimed < 256; claimed++)
            {
                var bytes = (byte[])sample.Clone();
                bytes[126] = (byte)claimed;
                var info = Edid.Parse(bytes);
                Assert.NotNull(info);
                Assert.Equal(claimed, info.ExtensionCount);
            }

            // Garbage extension blocks, zero blocks, and a base block alone followed by noise of any length.
            for (var i = 0; i < 300; i++)
            {
                var extra = rng.Next(400);
                var bytes = new byte[128 + extra];
                Array.Copy(sample, bytes, 128);
                bytes[126] = (byte)rng.Next(4);
                for (var k = 128; k < bytes.Length; k++) bytes[k] = i % 3 == 0 ? (byte)0 : (byte)rng.Next(256);
                var info = Edid.Parse(bytes);
                Assert.NotNull(info);
                Assert.Equal(bytes.Length, info.Length);
            }
        }
    }

    [Fact]
    public void RandomBytesOfAnyLengthParse()
    {
        var rng = new Lcg(7);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = new byte[rng.Next(1024)];
            for (var k = 0; k < bytes.Length; k++) bytes[k] = (byte)rng.Next(256);
            var info = Edid.Parse(bytes);
            Assert.NotNull(info);
            Assert.Equal(bytes.Length, info.Length);
        }
    }
}
