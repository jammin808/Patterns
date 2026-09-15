using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 65.9: the machine's facts as words, the commissioned rig as JSON and back, the drift as
/// lines with their marks, the RIG rows of Super Check, and the wire's RIG verbs.
/// </summary>
public class RigSnapshotTests
{
    private static readonly DateTime Taken = new(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc);

    internal static MachineFacts Facts(string driver = "32.0.15.6094", string plan = "High performance", bool ledWall = true, string edidHash = "AB12CD34EF5600112233445566778899AABBCCDDEEFF00112233445566778899")
    {
        var displays = new List<MachineDisplayFact> { new("Desk monitor", 0, 0, 2560, 1440, "60", "DisplayPort", "DEL A0B1 · DELL U2723", "0011223344556677", "RGB 4:4:4", 8, "SDR") };
        if (ledWall) displays.Add(new MachineDisplayFact("LED wall", 2560, 0, 3840, 2160, "50", "HDMI", "PTN 0001 · PATTERNS LED", edidHash, "RGB 4:4:4", 8, "SDR"));
        return new MachineFacts(Taken, "1.65.9", "SHOW-PC-1", "Windows 11 Pro 24H2 (build 26100.4351)", "10.0.0", "Intel Core i9-13900K", 32, 63.7,
            new[]
            {
                new GpuFact("NVIDIA RTX A4000", 0x10DE, 0x24B0, 16376, driver, "2024-08-20", "NVIDIA", false),
                new GpuFact("Microsoft Basic Render Driver", 0x1414, 0x8C, 0, "", "", "", true),
            },
            displays,
            new[]
            {
                new AudioEndpointFact("Speakers (Realtek)", "{out-1}", "out", true, 48000, 32, 2),
                new AudioEndpointFact("NDI Audio", "{out-2}", "out", false, 48000, 32, 8),
                new AudioEndpointFact("Mic (USB)", "{in-1}", "in", true, 48000, 16, 1),
            },
            plan, false, "on", "off", "as Windows decides", Array.Empty<string>());
    }

    private static RigSnapshot Snapshot(MachineFacts facts, string note = "first show", string contract = "3840x2160 50 RGB 8 SDR", string bindings = "every interface · HTTP 9696 · TCP 9697 · paired", string clock = "60.0 Hz")
        => RigSnapshot.From(facts, new[] { new RigContract("Screen 2 · LED wall", contract), new RigContract("Screen 1 · Desk monitor", "") }, new[] { "Programme", "Clean" }, bindings, clock, note, Taken);

    [Fact]
    public void TheFriendlyDriverIsNvidiasOwnNumber()
    {
        Assert.Equal("560.94", new GpuFact("RTX", 0x10DE, 1, 0, "32.0.15.6094", "", "", false).FriendlyDriver);
        Assert.Equal("552.22", new GpuFact("RTX", 0x10DE, 1, 0, "31.0.15.5222", "", "", false).FriendlyDriver);
        Assert.Equal("31.0.21001.45002", new GpuFact("Radeon", 0x1002, 1, 0, "31.0.21001.45002", "", "", false).FriendlyDriver);   // AMD's number as it is
        Assert.Equal("", new GpuFact("RTX", 0x10DE, 1, 0, "", "", "", false).FriendlyDriver);
        Assert.Equal("NVIDIA", new GpuFact("RTX", 0x10DE, 1, 0, "", "", "", false).Vendor);
        Assert.Equal("vendor 1AB3", new GpuFact("Odd", 0x1AB3, 1, 0, "", "", "", false).Vendor);
    }

    [Fact]
    public void TheLinesSayEverythingWindowsSaidAndTheBriefOmitsTheName()
    {
        var facts = Facts();
        var lines = facts.Lines;
        Assert.Contains("Build: 1.65.9 · .NET 10.0.0", lines);
        Assert.Contains("Windows: Windows 11 Pro 24H2 (build 26100.4351) · SHOW-PC-1", lines);
        Assert.Contains("CPU: Intel Core i9-13900K · 32 logical cores · RAM 63.7 GB", lines);
        Assert.Contains("GPU: NVIDIA RTX A4000 · NVIDIA · 16376 MB · driver 560.94 (2024-08-20)", lines);
        Assert.Contains("GPU: Microsoft Basic Render Driver · Microsoft · driver unknown · software", lines);
        Assert.Contains("Display: LED wall · 3840×2160 @ 50 Hz · HDMI · RGB 4:4:4 · 8-bit · SDR · EDID PTN 0001 · PATTERNS LED AB12CD34", lines);
        Assert.Contains("Audio out: Speakers (Realtek) (out) · default · 48 kHz · 32-bit · 2 ch", lines);
        Assert.Contains("Audio in: Mic (USB) (in) · default · 48 kHz · 16-bit · 1 ch", lines);
        Assert.Contains("Power: High performance · mains", lines);
        Assert.Contains("GPU scheduling: on · Game DVR: off · overlay planes: as Windows decides", lines);
        Assert.Equal("NVIDIA RTX A4000 · driver 560.94 · 2 displays · 2 audio outputs · High performance", facts.Summary);

        // The assistant's brief names nothing that identifies the machine.
        var brief = facts.LinesFor(withMachineName: false);
        Assert.DoesNotContain(brief, l => l.Contains("SHOW-PC-1"));
        Assert.Contains("Windows: Windows 11 Pro 24H2 (build 26100.4351)", brief);
        Assert.Equal(lines.Count, brief.Count);

        Assert.True(MachineFacts.Empty.IsEmpty);
        Assert.Empty(MachineFacts.Empty.Lines);
        Assert.Equal("display at 100,200", new MachineDisplayFact("", 100, 200, 1920, 1080, "", "", "", "", "", 0, "").Key);
        Assert.EndsWith("no EDID read", new MachineDisplayFact("", 100, 200, 1920, 1080, "", "", "", "", "", 0, "").Words);
    }

    [Fact]
    public void TheSnapshotRoundTripsAsJsonWithItsListsInOrder()
    {
        var snapshot = Snapshot(Facts());
        Assert.Equal(new[] { "Screen 1 · Desk monitor", "Screen 2 · LED wall" }, snapshot.Contracts.Select(c => c.Screen));   // ordinal by screen
        Assert.Equal(new[] { "NDI Audio", "Speakers (Realtek)" }, snapshot.AudioOut);
        Assert.Equal(new[] { "Mic (USB)" }, snapshot.AudioIn);
        Assert.Equal(new[] { "Clean", "Programme" }, snapshot.NdiSenders);
        Assert.Equal(2, snapshot.Gpus.Count);
        Assert.Equal(2, snapshot.Displays.Count);
        Assert.Equal("first show", snapshot.Note);

        var back = RigSnapshot.FromJson(snapshot.ToJson());
        Assert.NotNull(back);
        Assert.Equal(snapshot.TakenUtc, back!.TakenUtc);
        Assert.Equal(snapshot.Note, back.Note);
        Assert.Equal(snapshot.Windows, back.Windows);
        Assert.Equal(snapshot.Gpus, back.Gpus);
        Assert.Equal(snapshot.Displays, back.Displays);
        Assert.Equal(snapshot.Contracts, back.Contracts);
        Assert.Equal(snapshot.AudioOut, back.AudioOut);
        Assert.Equal(snapshot.NdiSenders, back.NdiSenders);
        Assert.Equal(snapshot.Bindings, back.Bindings);
        Assert.Equal(snapshot.RenderClock, back.RenderClock);
        Assert.True(RigDrift.Compare(snapshot, back).Same);

        Assert.Null(RigSnapshot.FromJson("not json"));
    }

    [Fact]
    public void TheSameRigReadsUnchangedOnEveryLine()
    {
        var known = Snapshot(Facts());
        var drift = RigDrift.Compare(known, Snapshot(Facts(), note: "a later reading"));
        Assert.True(drift.Same);
        Assert.Equal(0, drift.Changed);
        Assert.StartsWith("unchanged since ", drift.Headline);
        Assert.EndsWith("(first show)", drift.Headline);
        Assert.All(drift.Words, w => Assert.StartsWith("✓ ", w));
        Assert.Contains(drift.Lines, l => l.Item == "GPU" && l.Words == "GPU unchanged: NVIDIA RTX A4000 · driver 32.0.15.6094");
        Assert.Contains(drift.Lines, l => l.Item == "display" && l.Words.StartsWith("LED wall unchanged: 3840×2160 @ 50 Hz · EDID AB12CD34"));
        Assert.Contains(drift.Lines, l => l.Item == "contract" && l.Words == "contract unchanged: Screen 2 · LED wall · 3840x2160 50 RGB 8 SDR");
        Assert.Contains(drift.Lines, l => l.Item == "audio outputs" && l.Words == "audio outputs unchanged (2)");
        Assert.Contains(drift.Lines, l => l.Item == "render clock" && l.Words == "render clock unchanged: 60.0 Hz");
        Assert.Empty(drift.Changes);
    }

    [Fact]
    public void ADriverChangeIsAmberAndADisplayGoneIsSevere()
    {
        var known = Snapshot(Facts());
        var now = Snapshot(Facts(driver: "32.0.15.6200", ledWall: false));
        var drift = RigDrift.Compare(known, now);
        Assert.False(drift.Same);
        Assert.Equal(2, drift.Changed);
        Assert.StartsWith("2 changes since ", drift.Headline);
        var driver = drift.Lines.Single(l => l.Item == "GPU driver");
        Assert.Equal("GPU driver changed on NVIDIA RTX A4000: 32.0.15.6094 (2024-08-20) → 32.0.15.6200 (2024-08-20)", driver.Words);
        Assert.False(driver.Severe);
        var gone = drift.Lines.Single(l => l.Item == "display" && !l.Same);
        Assert.Equal("display missing: LED wall (3840×2160 @ 50 Hz)", gone.Words);
        Assert.True(gone.Severe);
        Assert.Equal(new[] { driver.Words, gone.Words }, drift.Changes);
        Assert.Contains("! " + gone.Words, drift.Words);
    }

    [Fact]
    public void EdidsContractsAudioBindingsAndTheClockAreCompared()
    {
        var known = Snapshot(Facts());
        var moved = Facts(edidHash: "FFEEDDCCBBAA99887766554433221100FFEEDDCCBBAA99887766554433221100", plan: "Balanced") with
        {
            Audio = new[] { new AudioEndpointFact("NDI Audio", "{out-2}", "out", false, 48000, 32, 8), new AudioEndpointFact("Mic (USB)", "{in-1}", "in", true, 48000, 16, 1), new AudioEndpointFact("Dante Virtual Soundcard", "{out-3}", "out", true, 48000, 32, 16) },
        };
        var drift = RigDrift.Compare(known, Snapshot(moved, contract: "3840x2160 60 RGB 8 SDR", bindings: "10.0.0.5 · HTTP 9696 · TCP 9697 · paired", clock: "50.0 Hz"));
        Assert.Contains(drift.Lines, l => l.Item == "display" && l.Words == "LED wall changed: EDID changed AB12CD34 → FFEEDDCC");
        Assert.Contains(drift.Lines, l => l.Item == "contract" && l.Words == "contract changed on Screen 2 · LED wall: 3840x2160 50 RGB 8 SDR → 3840x2160 60 RGB 8 SDR");
        var audio = drift.Lines.Single(l => l.Item == "audio outputs");
        Assert.Equal("audio outputs changed — gone: Speakers (Realtek); added: Dante Virtual Soundcard", audio.Words);
        Assert.True(audio.Severe);                                                        // an output gone is a show that may play to nowhere
        Assert.Contains(drift.Lines, l => l.Item == "network bindings" && l.Words == "network bindings changed: every interface · HTTP 9696 · TCP 9697 · paired → 10.0.0.5 · HTTP 9696 · TCP 9697 · paired");
        Assert.Contains(drift.Lines, l => l.Item == "power plan" && l.Words == "power plan changed: High performance → Balanced");
        Assert.Contains(drift.Lines, l => l.Item == "render clock" && l.Words == "render clock changed: 60.0 → 50.0 Hz");
        Assert.Equal(6, drift.Changed);

        // A clock within half a hertz is the same clock; a display or a GPU that is new is a change, not a fault.
        var nearly = RigDrift.Compare(known, Snapshot(Facts(), clock: "59.9 Hz"));
        Assert.Contains(nearly.Lines, l => l.Item == "render clock" && l.Same);
        var extra = Facts() with { Gpus = Facts().Gpus.Append(new GpuFact("Intel UHD 770", 0x8086, 0xA780, 128, "31.0.101.5186", "2024-01-10", "Intel", false)).ToList() };
        var added = RigDrift.Compare(known, Snapshot(extra));
        var line = added.Lines.Single(l => !l.Same);
        Assert.Equal("GPU added: Intel UHD 770 · driver 31.0.101.5186", line.Words);
        Assert.False(line.Severe);
    }

    [Fact]
    public void SuperCheckReadsTheRigInItsOwnRows()
    {
        var none = SuperCheck.Run(new CheckFacts { RigChecked = false });
        Assert.DoesNotContain(none.Rows, r => r.Section == "RIG");

        var unsaved = SuperCheck.Run(new CheckFacts { RigChecked = true, RigDrift = null });
        var grey = unsaved.Rows.Single(r => r.Section == "RIG");
        Assert.Equal(("Known good", CheckLight.Grey, "not saved"), (grey.Item, grey.Light, grey.Value));
        Assert.Contains("SAVE KNOWN GOOD", grey.Note);

        var known = Snapshot(Facts());
        var same = SuperCheck.Run(new CheckFacts { RigChecked = true, RigDrift = RigDrift.Compare(known, Snapshot(Facts())) });
        var green = same.Rows.Single(r => r.Section == "RIG");
        Assert.Equal(CheckLight.Green, green.Light);
        Assert.StartsWith("unchanged since", green.Value);

        var moved = SuperCheck.Run(new CheckFacts { RigChecked = true, RigDrift = RigDrift.Compare(known, Snapshot(Facts(driver: "32.0.15.6200", ledWall: false))) });
        var rows = moved.Rows.Where(r => r.Section == "RIG").ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(CheckLight.Amber, rows[0].Light);
        Assert.StartsWith("2 changes since", rows[0].Value);
        Assert.Contains(rows, r => r.Item == "GPU driver" && r.Light == CheckLight.Amber);
        Assert.Contains(rows, r => r.Item == "display" && r.Light == CheckLight.Red && r.Value.StartsWith("display missing: LED wall"));
    }

    [Fact]
    public void TheWireSpeaksRig()
    {
        Assert.Equal(RemoteCommandKind.RigStatus, ControlProtocol.Parse("RIG").Kind);
        Assert.Equal(RemoteCommandKind.RigStatus, ControlProtocol.Parse("rig status").Kind);
        var save = ControlProtocol.Parse("RIG SAVE first show");
        Assert.Equal(RemoteCommandKind.Action, save.Kind);
        Assert.Equal(new ShowAction(ShowActionKind.RigSaveKnownGood, "", "first show"), save.Action);
        Assert.Equal(ShowActionKind.RigSaveKnownGood, ControlProtocol.Parse("RIG KNOWNGOOD").Action.Kind);
        Assert.Equal(ShowActionKind.RigSaveKnownGood, ControlProtocol.Parse("RIG COMMISSION").Action.Kind);
        Assert.Equal("", ControlProtocol.Parse("RIG COMMISSION").Action.Value);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("RIG DANCE").Kind);
    }
}
