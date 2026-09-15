using Patterns.Core.LowerThirds;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The people tally behind the Show panel's chips: which library entry a design carries, and
/// the chips' second line — the tally while live, else what the chip is.
/// </summary>
public class LowerThirdTallyTests
{
    [Fact]
    public void ADesignCarriesTheEntryWhoseNameItReads()
    {
        var jane = new LowerThirdEntry { Name = "Jane Doe", Role = "Chief Executive" };
        var sam = new LowerThirdEntry { Name = "Sam Patel" };
        var blank = new LowerThirdEntry { Name = "  " };
        var entries = new[] { jane, sam, blank };
        var design = new LowerThirdDesign { Name = "Neon", PersonName = " jane doe " };

        Assert.True(LowerThirdsConfig.Carries(design, jane));         // any case, spaces trimmed
        Assert.False(LowerThirdsConfig.Carries(design, sam));
        Assert.False(LowerThirdsConfig.Carries(null, jane));
        Assert.Same(jane, LowerThirdsConfig.PersonOf(design, entries));

        design.PersonName = "Somebody Typed";                            // a hand edit is nobody's
        Assert.Null(LowerThirdsConfig.PersonOf(design, entries));
        design.PersonName = "";
        Assert.Null(LowerThirdsConfig.PersonOf(design, entries));
        Assert.False(LowerThirdsConfig.Carries(design, blank));         // an empty name never matches an empty field
        Assert.Null(LowerThirdsConfig.PersonOf(null, entries));
        design.PersonName = "SAM PATEL";
        Assert.Same(sam, LowerThirdsConfig.PersonOf(design, entries));
        Assert.Null(LowerThirdsConfig.PersonOf(design, Array.Empty<LowerThirdEntry>()));
    }

    [Fact]
    public void TheChipLineReadsTheTallyFirstThenWhatTheChipIs()
    {
        var jane = new LowerThirdEntry { Name = "Jane Doe", Role = "Chief Executive", Company = "Acme Ltd" };
        var raised = new List<string>();
        jane.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        Assert.Equal("Chief Executive", jane.ChipText);
        Assert.Equal("Chief Executive · Acme Ltd", jane.Summary);

        jane.IsInPreview = true;
        jane.PreviewText = "IN PREVIEW · Neon";
        Assert.Equal("IN PREVIEW · Neon", jane.ChipText);
        jane.IsOnAir = true;
        jane.OnAirText = "ON AIR · Neon";
        Assert.Equal("ON AIR · Neon", jane.ChipText);                    // on air outranks the preview
        jane.IsOnAir = false;
        jane.OnAirText = "";
        Assert.Equal("IN PREVIEW · Neon", jane.ChipText);
        jane.IsInPreview = false;
        jane.PreviewText = "";
        Assert.Equal("Chief Executive", jane.ChipText);
        jane.Role = "Chair";
        Assert.Equal("Chair", jane.ChipText);
        jane.Company = "";
        Assert.Equal("Chair", jane.Summary);
        Assert.Contains(nameof(LowerThirdEntry.ChipText), raised);
        Assert.Contains(nameof(LowerThirdEntry.Summary), raised);

        // Never saved: a copy carries the fields, not the lights.
        jane.IsOnAir = true;
        jane.OnAirText = "ON AIR · Neon";
        var copy = jane.Clone();
        Assert.False(copy.IsOnAir);
        Assert.Equal("", copy.OnAirText);
        Assert.Equal(("Jane Doe", "Chair"), (copy.Name, copy.Role));

        // The design's line: its tally, else the name it carries.
        var design = new LowerThirdDesign { Name = "Neon", PersonName = "Jane Doe" };
        var designRaised = new List<string>();
        design.PropertyChanged += (_, e) => designRaised.Add(e.PropertyName ?? "");
        Assert.Equal("Jane Doe", design.ChipText);
        design.IsInPreview = true;
        design.PreviewText = "IN PREVIEW";
        Assert.Equal("IN PREVIEW", design.ChipText);
        design.IsOnAir = true;
        design.OnAirText = "ARRIVING";
        Assert.Equal("ARRIVING", design.ChipText);
        design.IsOnAir = false;
        design.OnAirText = "";
        design.IsInPreview = false;
        design.PreviewText = "";
        Assert.Equal("Jane Doe", design.ChipText);
        design.PersonName = "";
        Assert.Equal("", design.ChipText);
        Assert.Contains(nameof(LowerThirdDesign.ChipText), designRaised);
        design.IsOnAir = true;
        design.OnAirText = "ON AIR";
        var saved = design.Clone(newId: false);
        Assert.False(saved.IsOnAir);
        Assert.Equal("", saved.ChipText);
    }
}
