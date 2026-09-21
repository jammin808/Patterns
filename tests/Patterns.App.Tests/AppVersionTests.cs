using Patterns.App.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Round 79.5: the build says which build it is — the informational version a publish stamps, with the commit cut to seven characters, on every surface that names the desk.</summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("0.79.312+round-79.9410292a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7", "0.79.312+round-79.9410292")]   // CI's stamp with the SDK's full commit
    [InlineData("0.79.312+round-79.9410292", "0.79.312+round-79.9410292")]                                  // already short
    [InlineData("1.0.0+94102923f6f6e921506b72e66c05370b3fc319c7", "1.0.0+9410292")]                         // a plain build: the SDK's own stamp
    [InlineData("1.0.0", "1.0.0")]                                                                          // no metadata
    [InlineData("0.79.0+round-79.local", "0.79.0+round-79.local")]                                           // a script's publish with no git: not a commit, left alone
    public void TheInformationalVersionIsShortenedToItsCommit(string informational, string expected)
        => Assert.Equal(expected, AppVersion.Shorten(informational));

    [Fact]
    public void NothingReadsAsNothingAndThisBuildHasAVersion()
    {
        Assert.Null(AppVersion.Shorten(null));
        Assert.Null(AppVersion.Shorten(""));
        Assert.NotEqual("dev", AppVersion.Current);
        Assert.Equal(AppVersion.Read(typeof(AppVersion).Assembly), AppVersion.Current);
        Assert.DoesNotContain("1.0.0.0", AppVersion.Current, StringComparison.Ordinal);   // the informational version, not the four-part assembly version
    }
}
