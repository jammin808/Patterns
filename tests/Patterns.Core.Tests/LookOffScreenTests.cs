using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 27: "Different highlights for which Look is live, if a Look is set but one or more screens
/// have changed Patterns within that look, or not live (Also for Patterns on each screen)."
///
/// The desk could always answer the first and third — one fingerprint against another. The middle
/// one it could only answer for the whole show at once, which on a rig with eight screens is the
/// answer an operator least needs: "something has changed" sends them round the back of the set to
/// find out what. This is the per-screen reading.
/// </summary>
public class LookOffScreenTests
{
    private static ShowState Rig()
    {
        var state = new ShowState();
        foreach (var id in new[] { "a", "b", "c" })
        {
            state.Output.Placements.Add(new ScreenPlacement { ScreenId = id, Enabled = true });
        }
        state.Pattern.Kind = PatternKind.Grid;
        return state;
    }

    private static void GiveItsOwn(ShowState state, string id, PatternKind kind)
    {
        var placement = state.Output.Placements.First(p => p.ScreenId == id);
        placement.UseCustomPattern = true;
        var assignment = state.Independent.FirstOrDefault(a => a.ScreenId == id);
        if (assignment is null)
        {
            assignment = new OutputAssignment { ScreenId = id };
            state.Independent.Add(assignment);
        }
        assignment.Pattern.Kind = kind;
    }

    private static readonly string[] Targets = { "a", "b", "c" };

    [Fact]
    public void ALookThatIsStillWhatItSavedHasNoScreenOffIt()
    {
        var state = Rig();
        var look = LookService.Capture(state);
        Assert.Empty(LookService.TargetsOffLook(state, look, Targets));

        // The same picture reached another way is still the same picture.
        state.Pattern.Kind = PatternKind.ColorBars;
        state.Pattern.Kind = PatternKind.Grid;
        Assert.Empty(LookService.TargetsOffLook(state, look, Targets));
    }

    [Fact]
    public void OneScreenChangedIsOneScreenNamedAndNotTheWholeShow()
    {
        var state = Rig();
        var look = LookService.Capture(state);

        // Somebody puts screen b on its own picture after the look went up.
        GiveItsOwn(state, "b", PatternKind.Focus);
        var off = LookService.TargetsOffLook(state, look, Targets);
        Assert.Equal(new[] { "b" }, off);

        // The whole-show reading says only that SOMETHING moved — which is the reading this exists
        // to improve on, so both must be true at once rather than one replacing the other.
        Assert.NotEqual(LookService.Fingerprint(look), LookService.Fingerprint(state));

        // A second screen joins it.
        GiveItsOwn(state, "c", PatternKind.LedWall);
        Assert.Equal(new[] { "b", "c" }, LookService.TargetsOffLook(state, look, Targets));

        // Put b back and it drops off the list; c is still named.
        state.Output.Placements.First(p => p.ScreenId == "b").UseCustomPattern = false;
        Assert.Equal(new[] { "c" }, LookService.TargetsOffLook(state, look, Targets));
    }

    [Fact]
    public void AScreenTheLookItselfGaveItsOwnPictureIsNotAScreenThatHasGoneItsOwnWay()
    {
        // This is why "own" could never stand in for the reading: a look very often gives a
        // confidence screen a picture of its own ON PURPOSE, and a key that lights for that is a
        // key that cries wolf from the moment the look goes up.
        var state = Rig();
        GiveItsOwn(state, "c", PatternKind.FlatField);
        var look = LookService.Capture(state);

        Assert.True(ContentTargets.UsesOwnPattern(state, "c"));
        Assert.Empty(LookService.TargetsOffLook(state, look, Targets));

        // Change what that screen shows, though, and it is off the look.
        state.Independent.First(a => a.ScreenId == "c").Pattern.Kind = PatternKind.Motion;
        Assert.Equal(new[] { "c" }, LookService.TargetsOffLook(state, look, Targets));
    }

    [Fact]
    public void TheProgrammeMovingTakesEveryScreenThatFollowsItWithIt()
    {
        var state = Rig();
        GiveItsOwn(state, "a", PatternKind.Checkerboard);
        var look = LookService.Capture(state);

        // The show's own picture changes: every screen that follows it has changed with it, and
        // the one on its own picture has not.
        state.Pattern.Kind = PatternKind.ColorBars;
        Assert.Equal(new[] { "b", "c" }, LookService.TargetsOffLook(state, look, Targets));
    }

    [Fact]
    public void ASettingThatIsNotPartOfThePictureIsNotAScreenGoingItsOwnWay()
    {
        // Compared by the same identity the engine uses to decide a picture has changed, so the
        // things the desk deliberately does not crossfade for do not count here either — a desk
        // pointer switched on over a web page is not a screen going its own way.
        var state = Rig();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Web;
        var look = LookService.Capture(state);
        state.Pattern.Media.WebShowPointer = !state.Pattern.Media.WebShowPointer;
        state.Pattern.Media.WebZoomPct = 150;
        Assert.Empty(LookService.TargetsOffLook(state, look, Targets));

        // The page itself changing IS the picture changing.
        state.Pattern.Media.WebUrl = "https://example.com/two";
        Assert.Equal(Targets, LookService.TargetsOffLook(state, look, Targets));
    }

    [Fact]
    public void AnUnreadableLookIsNotACrashAndNamesNobody()
    {
        var state = Rig();
        Assert.Empty(LookService.TargetsOffLook(state, "", Targets));
        Assert.Empty(LookService.TargetsOffLook(state, "{ not json", Targets));
        Assert.Empty(LookService.TargetsOffLook(state, LookService.Capture(state), Array.Empty<string>()));
        Assert.Null(LookService.Read("{ not json"));
    }
}
