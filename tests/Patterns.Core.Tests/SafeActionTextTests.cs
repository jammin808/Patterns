using System.IO.Compression;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 76: a secret may enter the authority path and never leaves through a diagnostic, a recording
/// or a persistence. One formatter renders an action everywhere it is rendered diagnostically, and it
/// redacts the target of the kinds that carry the admin passcode; a control row an older show carried
/// the passcode in is removed when the show loads, with a note that never repeats it; journal rows an
/// older build wrote with the passcode are blanked once at boot; the support bundle masks the show's
/// own secret values wherever they appear in its text entries.
/// </summary>
public class SafeActionTextTests
{
    private const string Passcode = "CorrectHorseBatteryStaple";

    [Fact]
    public void AnActionThatCarriesTheAdminPasscodeRendersRedactedAndEveryOtherActionRendersWhole()
    {
        Assert.Equal("Restart [redacted]", new ShowAction(ShowActionKind.Restart, Passcode).ToString());
        Assert.Equal("UpdateApply [redacted]", new ShowAction(ShowActionKind.UpdateApply, Passcode).ToString());
        Assert.Equal("UpdateApply [redacted] now", new ShowAction(ShowActionKind.UpdateApply, Passcode, "now").ToString());
        Assert.Equal("Restart", new ShowAction(ShowActionKind.Restart).ToString());                       // nothing typed, nothing to hide
        Assert.Equal("ApplyLook Walk-in fade", new ShowAction(ShowActionKind.ApplyLook, "Walk-in", "fade").ToString());
        Assert.Equal("CueGo", new ShowAction(ShowActionKind.CueGo).ToString());
        // The log line the executor writes on an exception is an interpolation of the action: the same formatter.
        var line = $"Action {new ShowAction(ShowActionKind.Restart, Passcode)} failed.";
        Assert.DoesNotContain(Passcode, line, StringComparison.Ordinal);
        Assert.Equal("Action Restart [redacted] failed.", line);
    }

    [Fact]
    public void AControlRowCarryingThePasscodeIsRemovedOnLoadWithANoteThatNeverRepeatsIt()
    {
        var state = new ShowState();
        var apc = new DeviceConfig { Name = "APC40", Link = DeviceLink.Midi };
        apc.Triggers.Add(new DeviceTriggerConfig { Match = "NOTE 1 53 *", Command = "CUE GO" });
        apc.Triggers.Add(new DeviceTriggerConfig { Match = "NOTE 1 54 *", Command = "RESTART " + Passcode });
        apc.Triggers.Add(new DeviceTriggerConfig { Match = "NOTE 1 55 *", Command = "UPDATE APPLY " + Passcode });
        apc.Triggers.Add(new DeviceTriggerConfig { Match = "RESTART *", Command = "RESTART *" });         // the passcode comes from the device's line: no secret in the file
        apc.Triggers.Add(new DeviceTriggerConfig { Match = "CUE STANDBY *", Command = "NOTE 1 60 127" });  // a lamp row: a fact lighting a control
        var arduino = new DeviceConfig { Name = "Lectern", Link = DeviceLink.Serial };
        arduino.Triggers.Add(new DeviceTriggerConfig { Match = "BTN1", Command = "CUE GO" });
        state.Interactive.Devices.Add(apc);
        state.Interactive.Devices.Add(arduino);

        Assert.True(SecretRows.Carries("RESTART " + Passcode));
        Assert.True(SecretRows.Carries("update apply " + Passcode));
        Assert.False(SecretRows.Carries("RESTART *"));
        Assert.False(SecretRows.Carries("RESTART"));
        Assert.False(SecretRows.Carries("CUE GO"));
        Assert.False(SecretRows.Carries(""));

        var notes = SettingsStore.Migrate(state);
        var note = Assert.Single(notes);
        Assert.Contains("'APC40'", note, StringComparison.Ordinal);
        Assert.Contains("credential", note, StringComparison.Ordinal);
        Assert.DoesNotContain(Passcode, note, StringComparison.Ordinal);
        Assert.Equal(new[] { "CUE GO", "RESTART *", "NOTE 1 60 127" }, apc.Triggers.Select(t => t.Command));
        Assert.Equal(new[] { "CUE GO" }, arduino.Triggers.Select(t => t.Command));
        Assert.DoesNotContain(Passcode, JsonUtil.Serialize(state), StringComparison.Ordinal);

        // Idempotent: a clean show yields no note.
        Assert.Empty(SettingsStore.Migrate(state));
    }

    [Fact]
    public void OldJournalRowsCarryingThePasscodeAreBlankedOnceAndTheRestOfTheJournalIsUntouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-scrub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new ShowLog(dir);
            log.Record("desk", "ApplyLook", "Walk-in", "Done", "");
            log.Record("http 10.0.0.5", "Restart", Passcode, "Done", "restarting");                 // what a build before round 75 wrote
            log.Record("tcp 10.0.0.9", "UpdateApply", Passcode, "Refused", "wrong passcode");
            log.Record("desk", "CueGo", "cue-1", "Done", "");
            File.AppendAllText(log.Path, "{torn line");
            // The rotated half carries one too.
            File.WriteAllText(log.Path + ".1", "{\"AtUtc\":\"2026-09-01T10:00:00Z\",\"Origin\":\"desk\",\"Kind\":\"Restart\",\"Target\":\"" + Passcode + "\",\"Outcome\":\"Done\",\"Message\":\"\"}\n");

            Assert.True(ShowLog.KindCarriesSecret("Restart"));
            Assert.True(ShowLog.KindCarriesSecret("UpdateApply"));
            Assert.False(ShowLog.KindCarriesSecret("ApplyLook"));
            Assert.False(ShowLog.KindCarriesSecret("Migration"));

            Assert.Equal(3, log.ScrubSecrets());
            var text = File.ReadAllText(log.Path);
            Assert.DoesNotContain(Passcode, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Passcode, File.ReadAllText(log.Path + ".1"), StringComparison.Ordinal);
            Assert.EndsWith("{torn line", text);                                                          // a torn line stays as it is
            var tail = log.Tail(10);
            Assert.Equal(new[] { "ApplyLook", "Restart", "UpdateApply", "CueGo" }, tail.Select(e => e.Kind));
            Assert.Equal(new[] { "Walk-in", "", "", "cue-1" }, tail.Select(e => e.Target));
            Assert.Equal("restarting", tail[1].Message);
            Assert.Equal("http 10.0.0.5", tail[1].Origin);
            Assert.Equal(0, log.ScrubSecrets());                                                          // once
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheSupportBundleMasksTheShowsOwnSecretValuesInEveryTextEntry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string admin = "ADMIN_PASSCODE_TEST";
            const string management = "MANAGEMENT_TOKEN_TEST";
            const string twin = "TWIN_KEY_TEST";
            var state = new ShowState();
            state.Install.AdminPasscode = admin;
            state.Install.ManagementToken = management;
            state.Twin.Key = twin;
            state.Install.SiteName = "Lobby";
            var values = Secrets.ValuesOf(state);
            Assert.Contains(admin, values);
            Assert.Contains(management, values);
            Assert.Contains(twin, values);
            Assert.DoesNotContain("Lobby", values);
            Assert.All(values, v => Assert.True(v.Length >= Secrets.MinScrubLength));

            File.WriteAllText(Path.Combine(dir, "patterns.log"), $"10:00:00.000 [ERROR] Action Restart {admin} failed.\n10:00:01.000 [INFO] token {management} accepted\n");
            File.WriteAllText(Path.Combine(dir, "patterns.watchdog.log"), $"child asked to restart with {admin}\n");
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(state));
            new ShowLog(dir).Record("http 10.0.0.5", "Restart", admin, "Done", $"key {twin}");
            Directory.CreateDirectory(Path.Combine(dir, "updates"));
            File.WriteAllText(Path.Combine(dir, "updates", UpdateApply.NoteName), $"Updated to 1.2.0 by {admin}");

            var zipPath = Path.Combine(dir, SupportBundle.FileNameFor(new DateTime(2026, 9, 17, 11, 30, 0)));
            var entries = SupportBundle.Build(dir, zipPath, "Site: Lobby", values);
            Assert.Contains("patterns.log", entries);
            Assert.Contains("patterns.watchdog.log", entries);
            Assert.Contains(ShowLog.FileName, entries);
            Assert.Contains("patterns.settings.json", entries);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                Assert.False(Secrets.Carries(text, admin), entry.FullName);
                Assert.False(Secrets.Carries(text, management), entry.FullName);
                Assert.False(Secrets.Carries(text, twin), entry.FullName);
            }
            using var logReader = new StreamReader(zip.GetEntry("patterns.log")!.Open());
            Assert.Equal($"10:00:00.000 [ERROR] Action Restart {Secrets.Mask} failed.\n10:00:01.000 [INFO] token {Secrets.Mask} accepted\n", logReader.ReadToEnd());
            using var settingsReader = new StreamReader(zip.GetEntry("patterns.settings.json")!.Open());
            Assert.Contains("\"SiteName\": \"Lobby\"", settingsReader.ReadToEnd());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ShortSecretsAreNotScrubbedFromFreeTextAndAnEmptyListChangesNothing()
    {
        Assert.Equal("the 12 rows", Secrets.Scrub("the 12 rows", new[] { "12" }));
        Assert.Equal("the same text", Secrets.Scrub("the same text", null));
        Assert.Equal("the same text", Secrets.Scrub("the same text", Array.Empty<string>()));
        Assert.Equal($"pass {Secrets.Mask} and {Secrets.Mask}", Secrets.Scrub("pass abcd and abcdefgh", new[] { "abcdefgh", "abcd" }));
        var state = new ShowState();
        state.Install.AdminPasscode = "123";
        Assert.Empty(Secrets.ValuesOf(state));
    }
}
