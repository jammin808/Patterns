using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.Views.Sections;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The lower-thirds library on the desk: the page, USE and SHOW, the remote and the panel, a list imported, a cue naming a person.</summary>
public class LowerThirdLibraryAppTests
{
    [AvaloniaFact]
    public void ThePageKeepsPeopleAndPutsThemIntoADesign()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            vm.IsSandboxActive = false;
            var headshot = vm.NewLowerThird("Headshot");
            var photo = LowerThirdsConfig.PhotoElement(headshot)!;

            // Entries by hand: named so they never collide, selected, edited in place, deleted with the selection moving on.
            var jane = vm.NewEntry("Jane Doe");
            Assert.Same(jane, vm.SelectedEntry);
            Assert.True(vm.HasEntry);
            jane.Role = "Chief Executive";
            jane.Company = "Acme Ltd";
            jane.Photo = Path.Combine(b.Dir, "jane.jpg");
            var sam = vm.NewEntry("Sam Patel");
            sam.Role = "Head of Product";
            Assert.Equal("New person", vm.NewEntry().Name);
            Assert.Equal("New person 2", vm.NewEntry().Name);
            vm.DeleteEntryCommand.Execute(vm.State.LowerThirds.Entries[3]);
            Assert.Same(vm.State.LowerThirds.Entries[2], vm.SelectedEntry);
            vm.DeleteEntryCommand.Execute(vm.State.LowerThirds.Entries[2]);
            Assert.Equal(2, vm.State.LowerThirds.Entries.Count);
            Assert.Same(sam, vm.SelectedEntry);

            // USE: the fields and the photo, nothing on air. SHOW: the same, and on air, the snapshot carrying the person.
            vm.SelectedLowerThird = headshot;
            Assert.Same(headshot, vm.UseEntry(jane));
            Assert.Equal(("Jane Doe", "Chief Executive", "Acme Ltd", jane.Photo), (headshot.PersonName, headshot.PersonRole, headshot.Company, photo.Path));
            Assert.False(vm.State.LowerThirds.IsShowing);
            Assert.True(vm.ShowEntry(sam, headshot).Ok);
            Assert.True(vm.State.LowerThirds.IsShowing);
            Assert.Equal(("Sam Patel", "Head of Product", ""), (headshot.PersonName, headshot.PersonRole, headshot.Company));
            Assert.Equal(jane.Photo, photo.Path); // Sam has no photo: Jane's picture stays
            Assert.Equal("Sam Patel", services.Bus.Current.State.LowerThirds.Active!.PersonName);

            // The remote: PERSON n into the design on air, LT … WITH …, a stranger refused, STATE carrying the library and the name on screen.
            var router = new CommandRouter(services);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("PERSON 1"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Jane Doe", headshot.PersonName);
            var tag = vm.NewLowerThird("Tag");
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT Tag WITH sam patel"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(tag.Id, vm.State.LowerThirds.ActiveId);
            Assert.Equal("Sam Patel", tag.PersonName);
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("PERSON nobody"))));
            Assert.Equal("Sam Patel", tag.PersonName);
            var state = router.StateJson();
            Assert.Contains("\"people\":[{\"n\":1,\"name\":\"Jane Doe\",\"role\":\"Chief Executive\"},{\"n\":2,\"name\":\"Sam Patel\",\"role\":\"Head of Product\"}]", state);
            Assert.Contains("\"lowerThirdPerson\":\"Sam Patel\"", state);

            // The page hosts the library with the selection; the Show panel has a chip per person that fills the design on air.
            var host = new Window { DataContext = vm, Width = 900, Height = 3200, Content = new ScrollViewer { Content = new LowerThirdsSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(host.GetVisualDescendants().OfType<ListBox>(), l => ReferenceEquals(l.SelectedItem, vm.SelectedEntry));
            host.Close();
            var panel = new Window { DataContext = vm, Width = 900, Height = 2400, Content = new ScrollViewer { Content = new ShowSection() } };
            panel.Show();
            Dispatcher.UIThread.RunJobs();
            var chips = panel.GetVisualDescendants().OfType<Button>().Where(x => x.DataContext is LowerThirdEntry).ToList();
            Assert.Equal(2, chips.Count);
            chips[0].Command!.Execute(chips[0].CommandParameter);
            Assert.Equal("Jane Doe", tag.PersonName); // the design on air took her
            Assert.Equal(tag.Id, vm.State.LowerThirds.ActiveId);
            panel.Close();
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AListComesInAndACueNamesAPerson()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            vm.IsSandboxActive = false;
            var neon = vm.NewLowerThird("Neon");

            // A list replaces the library; an appended list updates a name that is there; a file that is not there changes nothing; the export round-trips.
            var list = Path.Combine(b.Dir, "speakers.csv");
            File.WriteAllText(list, "Name,Role,Company\nJane Doe,CEO,Acme Ltd\nSam Patel,Head of Product,\n");
            Assert.StartsWith("2 entries from 2 rows: 2 added, 0 updated", vm.ImportPeopleFrom(list, append: false));
            Assert.Equal(new[] { "Jane Doe", "Sam Patel" }, vm.State.LowerThirds.Entries.Select(e => e.Name));
            Assert.Same(vm.State.LowerThirds.Entries[0], vm.SelectedEntry);
            var janeId = vm.State.LowerThirds.Entries[0].Id;
            var more = Path.Combine(b.Dir, "more.csv");
            File.WriteAllText(more, "Speaker,Job title\nJane Doe,Chair\nAlex Kim,Guest\n");
            Assert.StartsWith("2 entries from 2 rows: 1 added, 1 updated", vm.ImportPeopleFrom(more, append: true));
            Assert.Equal(3, vm.State.LowerThirds.Entries.Count);
            var jane = vm.State.LowerThirds.Entries[0];
            Assert.Equal((janeId, "Chair", "Acme Ltd"), (jane.Id, jane.Role, jane.Company));
            Assert.StartsWith("Could not read", vm.ImportPeopleFrom(Path.Combine(b.Dir, "missing.csv"), append: false));
            Assert.Equal(3, vm.State.LowerThirds.Entries.Count);
            var csv = vm.ExportPeopleCsv();
            Assert.Contains("Name,Role,Company,Photo,Note", csv);
            Assert.Contains("Alex Kim,Guest,,,", csv);

            // A cue: the person picker offers the library, the action carries the id, GO shows the design with the person.
            var stack = CueStacks.Caller(vm.State);
            vm.Cues.SelectedStack = stack;
            var cue = vm.Cues.AddCue();
            vm.Cues.SelectedCue = cue;
            vm.Cues.QuickActionCommand.Execute("LowerThirdShow");
            var row = Assert.Single(vm.Cues.ActionRows);
            Assert.True(row.HasPersonValue);
            Assert.False(row.HasTextValue);
            Assert.Equal(4, row.PersonChoices.Count); // as designed, then three people
            Assert.Same(row.PersonChoices[0], row.SelectedPerson);
            row.SelectedTarget = row.TargetChoices.First(t => t.Id == neon.Id);
            row.SelectedPerson = row.PersonChoices.First(p => p.Label.StartsWith("Alex Kim"));
            var alex = vm.State.LowerThirds.Entries[2];
            Assert.Equal(alex.Id, cue.Actions[0].Value);
            Assert.Equal("Lower third 'Neon' — Alex Kim", CueSummary.DescribeAction(vm.State, cue.Actions[0]));
            var fired = services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, cue.Id), ActionOrigin.Desk);
            Assert.True(fired.Ok, fired.Message);
            Assert.True(vm.State.LowerThirds.IsShowing);
            Assert.Equal(("Alex Kim", "Guest"), (neon.PersonName, neon.PersonRole));

            // A person from a cue while the sandbox is open reaches the frozen program's copy, and the edited design too.
            vm.IsSandboxActive = true;
            row.SelectedPerson = row.PersonChoices.First(p => p.Label.StartsWith("Jane Doe"));
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, cue.Id), ActionOrigin.Desk).Ok);
            Assert.Equal("Jane Doe", services.AirState.LowerThirds.Find(neon.Id)!.PersonName);
            Assert.Equal("Jane Doe", neon.PersonName);
            vm.IsSandboxActive = false;

            // A stranger: the checks say so before the show, and the cue is refused rather than a wrong name shown.
            cue.Actions[0].Value = "Nobody";
            var report = CueValidator.Validate(vm.State, stack, services.ValidationContext);
            Assert.Contains(report.Issues, p => p.CueId == cue.Id && p.Severity == IssueSeverity.Hard);
            Assert.False(services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, cue.Id), ActionOrigin.Desk).Ok);
            Assert.Equal("Jane Doe", neon.PersonName);
            row.RefreshChoices();
            Assert.Equal("Nobody (not in the library)", row.SelectedPerson!.Label);
        }
        finally
        {
            b.Dispose();
        }
    }
}
