using Patterns.Core.Play;
using Patterns.Arcade;
using Xunit;

namespace Patterns.Core.Tests;

public class QrCodeTests
{
    [Fact]
    public void TheReedSolomonCodewordsHaveZeroSyndromesAndTheKnownGenerator()
    {
        // The generator for two codewords is x² + α^25 x + α^1 → coefficients 1, 3, 2.
        Assert.Equal(new byte[] { 1, 3, 2 }, ReedSolomon.Generator(2));
        var rng = new Random(7);
        for (var trial = 0; trial < 20; trial++)
        {
            var data = new byte[16 + trial];
            rng.NextBytes(data);
            var degree = 10 + trial % 5;
            var ec = ReedSolomon.Encode(data, degree);
            Assert.Equal(degree, ec.Length);
            var whole = data.Concat(ec).ToArray();
            for (var power = 0; power < degree; power++) Assert.Equal(0, ReedSolomon.Syndrome(whole, power));
            whole[3] ^= 0x5A;                                                    // one byte wrong: a syndrome says so
            Assert.Contains(Enumerable.Range(0, degree), p => ReedSolomon.Syndrome(whole, p) != 0);
        }
    }

    [Fact]
    public void TheFormatAndVersionBitsAreTheStandards()
    {
        Assert.Equal(0x5412, QrCode.FormatBits(0));                             // level M, mask 0: the mask pattern itself
        Assert.Equal(0x5125, QrCode.FormatBits(1));                             // level M, mask 1
        Assert.Equal(0x07C94, QrCode.VersionBits(7));
        Assert.Equal(0x0A4D3, QrCode.VersionBits(10));
    }

    [Fact]
    public void ACodeIsBuiltAtTheSmallestVersionWithItsPatternsAndReadsBackItsCodewords()
    {
        Assert.Equal(14, QrCode.DataCapacity(1));
        Assert.Equal(1, QrCode.VersionFor(14));
        Assert.Equal(2, QrCode.VersionFor(15));
        Assert.Null(QrCode.VersionFor(300));
        Assert.Null(QrCode.Encode(new string('x', 300)));

        foreach (var text in new[] { "HELLO WORLD", "http://192.168.1.20:9696/play?room=ABCD", new string('q', 100), new string('z', 180) })
        {
            var code = QrCode.Encode(text)!;
            Assert.NotNull(code);
            Assert.Equal(17 + 4 * code.Version, code.Size);
            var s = code.Size;
            // The three finders: dark centre, light ring, dark ring — and the dark module.
            foreach (var (fx, fy) in new[] { (0, 0), (s - 7, 0), (0, s - 7) })
            {
                Assert.True(code[fx + 3, fy + 3]);
                Assert.False(code[fx + 1, fy + 3]);
                Assert.True(code[fx, fy]);
                Assert.True(code[fx + 6, fy + 6]);
            }
            Assert.True(code[8, s - 8]);
            // The timing patterns alternate between the finders.
            for (var i = 8; i < s - 8; i++)
            {
                Assert.Equal(i % 2 == 0, code[i, 6]);
                Assert.Equal(i % 2 == 0, code[6, i]);
            }
            // The codewords read back off the modules, unmasked, are the ones the text encodes to.
            Assert.Equal(QrCode.CodewordsFor(text), code.ReadBack());
            Assert.InRange(code.Mask, 0, 7);
            Assert.Contains('#', code.Ascii());
        }
        Assert.Equal(1, QrCode.Encode("HELLO WORLD")!.Version);
        Assert.Equal(3, QrCode.Encode("http://192.168.1.20:9696/play?room=ABCD")!.Version);
        Assert.True(QrCode.Encode(new string('z', 180))!.Version >= 7);       // a version with version bits
    }
}
