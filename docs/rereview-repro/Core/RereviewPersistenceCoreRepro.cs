using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Re-review reproducers (kept under docs/rereview-repro, outside the test projects). A PASS confirms the finding: PB-5 (a lost move reads as no crash), PB-9 (a failed move leaves the temp with the secret), PB-10 (the recovery record's twin key is not masked by place).</summary>
public class RereviewPersistenceCoreRepro
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-rereview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void PB5_ARecordWhoseReplacingMoveWasLostReadsAsNoCrash()
    {
        var dir = NewDir();
        try
        {
            var store = new RecoveryStore(dir);
            var main = Path.Combine(dir, "patterns.recovery.json");
            store.Write(live: true, audioPlaying: false, airLook: "One");
            store.Write(live: true, audioPlaying: false, airLook: "Two");
            Assert.True(File.Exists(main + ".bak"));
            File.Move(main, AtomicFile.TempPath(main));                  // the lost move: main gone, .bak = One, .tmp = Two
            Assert.Null(store.Read());                                    // FINDING: "no crash"
            Assert.Equal("", store.Problem);                              // FINDING: and no Super Check row
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PB9_AFailedMoveLeavesTheTempFileWithTheSecretBehind()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "patterns.spotify.json");
        Directory.CreateDirectory(path);                                  // the target cannot be replaced: the move throws
        try
        {
            Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "{\"RefreshToken\":\"rt-secret\"}"));
            Assert.True(File.Exists(AtomicFile.TempPath(path)));          // FINDING: the temp stays
            Assert.Contains("rt-secret", File.ReadAllText(AtomicFile.TempPath(path)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PB10_TheRecoveryRecordsTwinKeyIsNotMaskedByPlaceWhileItsTokenIsMaskedByName()
    {
        var air = new ShowState();
        air.Twin.Key = "twin-key-before-rotation";
        air.Control.Token = "token-in-the-air";
        var json = RecoveryStore.Serialize(new RecoverySnapshot(true, false, DateTime.UtcNow, Air: air));
        var redacted = SupportBundle.Redact(json);
        Assert.Contains("twin-key-before-rotation", redacted);            // FINDING: the placed secret under Air is left in clear
        Assert.DoesNotContain("token-in-the-air", redacted);              // a named secret is masked anywhere
    }
}
