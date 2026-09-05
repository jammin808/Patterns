using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The lower-thirds library: an entry into a design, a speaker list in and out, the cue action with a person, the remote verbs.</summary>
public class LowerThirdLibraryTests
{
    [Fact]
    public void AnEntryFillsADesignsFieldsAndItsPicture()
    {
        var headshot = LowerThirdPresets.Create("Headshot");
        var photo = Assert.Single(headshot.Elements, e => e.Kind == LowerThirdElementKind.Image);
        Assert.Same(photo, LowerThirdsConfig.PhotoElement(headshot));
        var jane = new LowerThirdEntry { Name = "Jane Doe", Role = "Chief Executive", Company = "Acme Ltd", Photo = @"C:\show\jane.jpg" };
        Assert.Same(photo, LowerThirdsConfig.Fill(headshot, jane));
        Assert.Equal(("Jane Doe", "Chief Executive", "Acme Ltd", @"C:\show\jane.jpg"), (headshot.PersonName, headshot.PersonRole, headshot.Company, photo.Path));

        // No photo: the picture stays; an empty company lets the brand kit's through.
        var sam = new LowerThirdEntry { Name = "Sam Patel", Role = "Head of Product" };
        Assert.Null(LowerThirdsConfig.Fill(headshot, sam));
        Assert.Equal(@"C:\show\jane.jpg", photo.Path);
        Assert.Equal(("Sam Patel", "Head of Product", ""), (headshot.PersonName, headshot.PersonRole, headshot.Company));
        Assert.Equal("Head of Product", sam.Summary);
        Assert.Equal("Chief Executive · Acme Ltd", jane.Summary);

        // A design without a picture element takes the words and reports no home for the photo; a named picture wins over the first.
        var clean = LowerThirdPresets.Create("Clean");
        Assert.Null(LowerThirdsConfig.PhotoElement(clean));
        Assert.Null(LowerThirdsConfig.Fill(clean, jane));
        Assert.Equal("Jane Doe", clean.PersonName);
        var badge = new LowerThirdElement { Kind = LowerThirdElementKind.Image, Name = "Badge" };
        var portrait = new LowerThirdElement { Kind = LowerThirdElementKind.Image, Name = "Speaker portrait" };
        clean.Elements.Add(badge);
        clean.Elements.Add(portrait);
        Assert.Same(portrait, LowerThirdsConfig.PhotoElement(clean));
        clean.Elements.Remove(portrait);
        Assert.Same(badge, LowerThirdsConfig.PhotoElement(clean));

        // The library finds by id, name and number; a clone can take a new id.
        var lib = new LowerThirdsConfig();
        lib.Entries.Add(jane);
        lib.Entries.Add(sam);
        Assert.Same(jane, lib.FindEntry(jane.Id));
        Assert.Same(sam, lib.FindEntry("sam patel"));
        Assert.Same(sam, lib.FindEntry(" 2 "));
        Assert.Null(lib.FindEntry("3"));
        Assert.Null(lib.FindEntry(""));
        Assert.Null(lib.FindEntry("Nobody"));
        var copy = jane.Clone(newId: true);
        Assert.NotEqual(jane.Id, copy.Id);
        Assert.Equal(jane.Photo, copy.Photo);
        Assert.Contains("\"Entries\"", JsonUtil.Serialize(lib));
    }

    [Fact]
    public void ASpeakerListBecomesEntriesAndBack()
    {
        var csv = "Speaker;Job title;Organisation;Headshot;Notes\n" +
                  "Jane Doe;Chief Executive;Acme Ltd;C:\\show\\jane.jpg;Pronounced DOH\n" +
                  ";;;;\n" +
                  "Sam Patel;Head of Product;;;\n" +
                  " ;Nobody;Acme Ltd;;\n";
        var report = LowerThirdLibrary.Import(CsvTable.Parse(csv));
        Assert.Equal(2, report.Entries.Count);
        Assert.Equal(3, report.Rows); // the empty line is not a row; the nameless one is
        var jane = report.Entries[0];
        Assert.Equal(("Jane Doe", "Chief Executive", "Acme Ltd", @"C:\show\jane.jpg", "Pronounced DOH"), (jane.Name, jane.Role, jane.Company, jane.Photo, jane.Note));
        Assert.Equal("Sam Patel", report.Entries[1].Name);
        var note = Assert.Single(report.Notes);
        Assert.Contains("no name", note);
        Assert.Equal("2 entries from 3 rows — 1 note", report.Summary);

        // First and last names are joined when there is no Name column.
        var split = LowerThirdLibrary.Import(CsvTable.Parse("First name,Last name,Role\nJane,Doe,CEO\nCher,,Singer\n"));
        Assert.Equal(new[] { "Jane Doe", "Cher" }, split.Entries.Select(e => e.Name));
        Assert.Empty(split.Notes);

        // No header, or no name column: nothing but the note.
        Assert.Contains("no header", LowerThirdLibrary.Import(TableData.Empty).Notes[0]);
        Assert.Contains("No Name column", LowerThirdLibrary.Import(CsvTable.Parse("Role,Company\nCEO,Acme\n")).Notes[0]);

        // The template reads back; a merge updates a name that is there (keeping its id and what the list leaves empty) and adds the rest.
        var template = LowerThirdLibrary.Import(CsvTable.Parse(LowerThirdLibrary.Template()));
        Assert.Equal(LowerThirdLibrary.Headers, CsvTable.Parse(LowerThirdLibrary.Template()).Headers);
        Assert.Equal(3, template.Entries.Count);
        Assert.Empty(template.Notes);
        var lib = new LowerThirdsConfig();
        Assert.Equal((3, 0), LowerThirdLibrary.Merge(lib.Entries, template.Entries));
        var janeId = lib.Entries[0].Id;
        var again = LowerThirdLibrary.Import(CsvTable.Parse("Name,Role\njane doe,Chair\nNew Person,Guest\n"));
        Assert.Equal((1, 1), LowerThirdLibrary.Merge(lib.Entries, again.Entries));
        Assert.Equal(4, lib.Entries.Count);
        Assert.Equal(janeId, lib.Entries[0].Id);
        Assert.Equal(("jane doe", "Chair", "Acme Ltd", @"C:\show\headshots\jane.jpg"), (lib.Entries[0].Name, lib.Entries[0].Role, lib.Entries[0].Company, lib.Entries[0].Photo));
        Assert.Equal("New Person", lib.Entries[3].Name);

        // An export round-trips.
        var back = LowerThirdLibrary.Import(CsvTable.Parse(LowerThirdLibrary.Export(lib.Entries)));
        Assert.Equal(
            lib.Entries.Select(e => (e.Name, e.Role, e.Company, e.Photo, e.Note)),
            back.Entries.Select(e => (e.Name, e.Role, e.Company, e.Photo, e.Note)));
    }

    [Fact]
    public void TheCueActionCarriesAPersonAndTheChecksRefuseAStranger()
    {
        var state = SettingsStore.Fresh();
        var neon = LowerThirdPresets.Create("Neon");
        state.LowerThirds.Designs.Add(neon);
        var jane = new LowerThirdEntry { Name = "Jane Doe", Role = "CEO", Photo = @"C:\show\jane.jpg" };
        state.LowerThirds.Entries.Add(jane);
        var stack = CueStacks.Caller(state);
        var byId = new RunCueConfig { Name = "Jane on" };
        byId.Actions.Add(new CueActionConfig { Kind = CueActionKind.LowerThirdShow, Target = neon.Id, Value = jane.Id });
        var byName = new RunCueConfig { Name = "Jane again" };
        byName.Actions.Add(new CueActionConfig { Kind = CueActionKind.LowerThirdShow, Target = "", Value = "jane doe" });
        var stranger = new RunCueConfig { Name = "Who?" };
        stranger.Actions.Add(new CueActionConfig { Kind = CueActionKind.LowerThirdShow, Target = neon.Id, Value = "Nobody" });
        stack.Cues.Add(byId);
        stack.Cues.Add(byName);
        stack.Cues.Add(stranger);

        Assert.Equal("Lower third 'Neon' — Jane Doe", CueSummary.DescribeAction(state, byId.Actions[0]));
        Assert.Equal("Lower third on air — Jane Doe", CueSummary.DescribeAction(state, byName.Actions[0]));
        Assert.Equal("Lower third 'Neon' — 'Nobody' (not in the library)", CueSummary.DescribeAction(state, stranger.Actions[0]));

        var report = CueValidator.Validate(state, stack, new CueValidationContext { FileExists = _ => true });
        Assert.DoesNotContain(report.Issues, p => p.CueId == byId.Id);
        Assert.DoesNotContain(report.Issues, p => p.CueId == byName.Id);
        Assert.Contains(report.Issues, p => p.CueId == stranger.Id && p.Severity == IssueSeverity.Hard && p.Text.Contains("not in the lower-thirds library"));
        var noPhoto = CueValidator.Validate(state, stack, new CueValidationContext { FileExists = _ => false });
        Assert.Contains(noPhoto.Issues, p => p.CueId == byId.Id && p.Severity != IssueSeverity.Hard && p.Text.Contains("photo"));

        // The sheet: "Speaker" is the action and the Value is the person as written; the export writes an id back as the name.
        var sheet = CueSheet.Import(CsvTable.Parse("Name,Action,Target,Value\nIntro,Speaker,Neon,Jane Doe\n"), state);
        var action = Assert.Single(sheet.Cues[0].Actions);
        Assert.Equal((CueActionKind.LowerThirdShow, neon.Id, "Jane Doe"), (action.Kind, action.Target, action.Value));
        var export = CueSheet.Export(state, stack);
        Assert.Contains("Lower third on,Neon,Jane Doe", export);
        Assert.DoesNotContain(jane.Id, export);
    }

    [Fact]
    public void TheRemoteVerbsNameAPerson()
    {
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdPerson, 0, "Neon", "Jane Doe"), ControlProtocol.Parse("LT Neon WITH Jane Doe"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdPerson, 0, "2", "3"), ControlProtocol.Parse("lowerthird 2 with 3"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdPerson, 0, "", "3"), ControlProtocol.Parse("PERSON 3"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdPerson, 0, "", "Jane Doe"), ControlProtocol.Parse("person Jane Doe"));
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("LT Neon WITH").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("PERSON").Kind);
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdShow, 0, "Withers"), ControlProtocol.Parse("LT Withers")); // a name with "with" in it is a name
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdShow, 2, ""), ControlProtocol.Parse("LT 2"));
    }
}
