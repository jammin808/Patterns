namespace Patterns.Core.Services;

/// <summary>The seven stages of commissioning a rig, in the order an engineer walks them.</summary>
public enum CommissionStage
{
    /// <summary>Windows shows every display the links land on; none of the show's screens has lost its display.</summary>
    Discover,
    /// <summary>Every planned screen has been adopted onto a real display; at least one screen is enabled.</summary>
    Assign,
    /// <summary>Every enabled screen has a signal contract — and none is left on the test route.</summary>
    Contract,
    /// <summary>Every display's EDID has been read and offers what its contract asks.</summary>
    Capability,
    /// <summary>The outputs have opened: the test card and IDENTIFY have been on the wall.</summary>
    OutputTest,
    /// <summary>Windows reports every contracted screen's link carrying its contract — MATCH on every one.</summary>
    Verify,
    /// <summary>The rig is saved as known good and has not moved since.</summary>
    KnownGood,
}

/// <summary>One screen as the commissioning flow sees it.</summary>
public sealed record CommissionScreen(int Number, string Label, bool HasContract, bool TestRoute, bool EdidRead, bool Advertises, SignalVerdict Verdict, string Observed = "");

/// <summary>
/// What the desk knows for the commissioning flow (round 65.10). The App gathers it — the screens
/// service, the signal reports, the outputs, the known-good rig — and <see cref="Commissioning"/>
/// judges it; nothing here reaches Windows.
/// </summary>
public sealed record CommissioningFacts
{
    /// <summary>Real displays Windows shows right now (planned and virtual screens apart).</summary>
    public int DisplaysSeen { get; init; }
    /// <summary>Placements still planned — no display adopted yet.</summary>
    public int PlannedScreens { get; init; }
    /// <summary>Enabled screens on real displays.</summary>
    public int EnabledScreens { get; init; }
    /// <summary>Enabled screens whose display has gone (hot-plug lost), with their words.</summary>
    public IReadOnlyList<string> LostScreens { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CommissionScreen> Screens { get; init; } = Array.Empty<CommissionScreen>();
    public bool OutputsLive { get; init; }
    /// <summary>The outputs have been live at some point of this run — the output test has happened.</summary>
    public bool OutputsWereLive { get; init; }
    public bool KnownGoodSaved { get; init; }
    /// <summary>The comparison against the saved rig: null before the first reading or when nothing is saved.</summary>
    public bool? KnownGoodSame { get; init; }
    public string KnownGoodWords { get; init; } = "";
}

/// <summary>One stage as a line: its light, its value, and the next thing to do when it is not green.</summary>
public sealed record CommissionLine(CommissionStage Stage, string Title, CheckLight Light, string Value, string Next = "")
{
    public string Mark => Light switch { CheckLight.Green => "✓", CheckLight.Red => "✗", CheckLight.Amber => "!", _ => "·" };

    public string Words => $"{Mark} {Title}: {Value}{(Light != CheckLight.Green && Next.Length > 0 ? $" — {Next}" : "")}";
}

/// <summary>The flow as a report: the seven lines, how many are green, and the next step in words.</summary>
public sealed record CommissioningReport(IReadOnlyList<CommissionLine> Lines)
{
    public int Done => Lines.Count(l => l.Light == CheckLight.Green);

    public int Total => Lines.Count;

    public int Percent => Total == 0 ? 0 : Done * 100 / Total;

    public bool Complete => Total > 0 && Done == Total;

    /// <summary>The first stage that is not green — where the engineer is.</summary>
    public CommissionLine? Current => Lines.FirstOrDefault(l => l.Light != CheckLight.Green);

    public string Headline => Complete
        ? "commissioned — every stage green"
        : $"{Done} of {Total} stages green · at {Current!.Title}: {Current.Value}";

    /// <summary>The next thing to do, in the desk's own words; empty when the rig is commissioned.</summary>
    public string Next => Current?.Next ?? "";

    public IReadOnlyList<string> Words => Lines.Select(l => l.Words).ToList();

    public CheckLight Overall => Complete ? CheckLight.Green : Lines.Any(l => l.Light == CheckLight.Red) ? CheckLight.Red : Lines.Any(l => l.Light == CheckLight.Amber) ? CheckLight.Amber : CheckLight.Grey;
}

/// <summary>
/// The commissioning flow (round 65.10): DISCOVER → ASSIGN → CONTRACT → CAPABILITY → OUTPUT TEST →
/// VERIFY → KNOWN GOOD, each stage judged from the desk's evidence alone, each line saying what to do
/// next in the words the wire and the pages use. A screen on the test route (the diagnostic profile
/// standing in for its contract) holds the flow at CONTRACT: the route may be proven, the design is
/// not. Pure: the same facts give the same report on any machine.
/// </summary>
public static class Commissioning
{
    public static CommissioningReport Build(CommissioningFacts f)
    {
        var lines = new List<CommissionLine>();
        var contracted = f.Screens.Where(s => s.HasContract && !s.TestRoute).ToList();
        var onRoute = f.Screens.Where(s => s.TestRoute).ToList();

        // DISCOVER
        if (f.DisplaysSeen == 0)
            lines.Add(new(CommissionStage.Discover, "Discover", CheckLight.Grey, "no display seen", "plug the links in and let Windows show the displays; Detect on the Screens page rescans"));
        else if (f.LostScreens.Count > 0)
            lines.Add(new(CommissionStage.Discover, "Discover", CheckLight.Red, $"{f.DisplaysSeen} display{Plural(f.DisplaysSeen)} seen · {f.LostScreens.Count} lost", string.Join("; ", f.LostScreens)));
        else
            lines.Add(new(CommissionStage.Discover, "Discover", CheckLight.Green, $"{f.DisplaysSeen} display{Plural(f.DisplaysSeen)} seen"));

        // ASSIGN
        if (f.EnabledScreens == 0 && f.PlannedScreens == 0)
            lines.Add(new(CommissionStage.Assign, "Assign", CheckLight.Grey, "no screen enabled", "enable the screens the show uses on the Screens page (SCREEN n ON)"));
        else if (f.PlannedScreens > 0)
            lines.Add(new(CommissionStage.Assign, "Assign", CheckLight.Amber, $"{f.PlannedScreens} planned screen{Plural(f.PlannedScreens)} without a display", "pick the display each turned out to be and press Adopt on the Screens page"));
        else
            lines.Add(new(CommissionStage.Assign, "Assign", CheckLight.Green, $"{f.EnabledScreens} screen{Plural(f.EnabledScreens)} on real displays"));

        // CONTRACT
        var without = f.Screens.Where(s => !s.HasContract && !s.TestRoute).ToList();
        if (f.Screens.Count == 0)
            lines.Add(new(CommissionStage.Contract, "Contract", CheckLight.Grey, "no screen to contract", "assign the screens first"));
        else if (onRoute.Count > 0)
            lines.Add(new(CommissionStage.Contract, "Contract", CheckLight.Amber, $"{Names(onRoute)} on TEST ROUTE", $"the diagnostic profile stands in for the design — once the route is proven, SCREEN {onRoute[0].Number} TESTROUTE OFF and the contract holds again"));
        else if (without.Count > 0)
            lines.Add(new(CommissionStage.Contract, "Contract", CheckLight.Amber, $"{Names(without)} without a contract", $"say what the link is meant to carry — SCREEN {without[0].Number} SIGNAL 3840x2160 50 RGB 8 SDR, or the words on the Screens page"));
        else
            lines.Add(new(CommissionStage.Contract, "Contract", CheckLight.Green, $"{contracted.Count} contract{Plural(contracted.Count)} set"));

        // CAPABILITY
        if (contracted.Count == 0)
            lines.Add(new(CommissionStage.Capability, "Capability", CheckLight.Grey, "no contract to hold an EDID against", "set the contracts first"));
        else
        {
            var unread = contracted.Where(s => !s.EdidRead).ToList();
            var refusing = contracted.Where(s => s.EdidRead && !s.Advertises).ToList();
            if (refusing.Count > 0)
                lines.Add(new(CommissionStage.Capability, "Capability", CheckLight.Amber, $"{Names(refusing)}: the display does not advertise the contract", "ADVERTISED on the Screens page names what the EDID offers — change the contract, the display's EDID (the planned EDID export) or the processor's input"));
            else if (unread.Count > 0)
                lines.Add(new(CommissionStage.Capability, "Capability", CheckLight.Grey, $"{Names(unread)}: no EDID read", "the desk cannot say what the display offers — an emulator or a processor that presents no EDID is the usual reason; the contract stands on the engineer's word"));
            else
                lines.Add(new(CommissionStage.Capability, "Capability", CheckLight.Green, $"every display advertises its contract ({contracted.Count})"));
        }

        // OUTPUT TEST
        if (f.OutputsLive)
            lines.Add(new(CommissionStage.OutputTest, "Output test", CheckLight.Green, "outputs live"));
        else if (f.OutputsWereLive)
            lines.Add(new(CommissionStage.OutputTest, "Output test", CheckLight.Green, "outputs have been live this run"));
        else
            lines.Add(new(CommissionStage.OutputTest, "Output test", CheckLight.Grey, "outputs not yet opened", "OUTPUTS ON, then IDENTIFY and a test card on every screen — the wall says which link is which"));

        // VERIFY
        if (contracted.Count == 0)
            lines.Add(new(CommissionStage.Verify, "Verify", CheckLight.Grey, "nothing to verify", "set the contracts first"));
        else
        {
            var mismatched = contracted.Where(s => s.Verdict == SignalVerdict.Mismatch).ToList();
            var unverified = contracted.Where(s => s.Verdict == SignalVerdict.Unverified).ToList();
            if (mismatched.Count > 0)
                lines.Add(new(CommissionStage.Verify, "Verify", CheckLight.Red, $"MISMATCH on {Names(mismatched)}", string.Join("; ", mismatched.Select(s => $"{s.Label}: {s.Observed}")) + $" — SCREEN {mismatched[0].Number} SIGNAL reads the lines; SCREEN {mismatched[0].Number} TESTROUTE ON tells a capability problem from a path problem"));
            else if (unverified.Count > 0)
                lines.Add(new(CommissionStage.Verify, "Verify", CheckLight.Grey, $"{Names(unverified)} unverified", "Windows has not stated the path — the outputs on, the display awake; a property it never states stays unknown"));
            else
                lines.Add(new(CommissionStage.Verify, "Verify", CheckLight.Green, $"MATCH on every contracted screen ({contracted.Count})"));
        }

        // KNOWN GOOD
        if (!f.KnownGoodSaved)
            lines.Add(new(CommissionStage.KnownGood, "Known good", CheckLight.Grey, "not saved", "SAVE KNOWN GOOD on the Machine page (RIG SAVE first show) once every stage above is green"));
        else if (f.KnownGoodSame is null)
            lines.Add(new(CommissionStage.KnownGood, "Known good", CheckLight.Grey, "saved — not compared yet", "the machine is still being read"));
        else if (f.KnownGoodSame == false)
            lines.Add(new(CommissionStage.KnownGood, "Known good", CheckLight.Amber, f.KnownGoodWords, "the rig moved since it was saved — the RIG rows say what; SAVE KNOWN GOOD again when the change is meant"));
        else
            lines.Add(new(CommissionStage.KnownGood, "Known good", CheckLight.Green, f.KnownGoodWords.Length > 0 ? f.KnownGoodWords : "saved and unchanged"));

        return new CommissioningReport(lines);
    }

    private static string Plural(int n) => n == 1 ? "" : "s";

    private static string Names(IReadOnlyList<CommissionScreen> screens)
        => screens.Count <= 3 ? string.Join(", ", screens.Select(s => s.Label)) : $"{screens.Count} screens";
}
