using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Output hot-plug on the desk: a display unplugged leaves its screen waiting, planned and off,
/// with everything kept; the same display back is adopted and turned on; a display unplugged on
/// the left re-indexes the rest without losing them; a display never met is offered as a
/// substitute — forced to the mode when it must be — or kept as its own screen.
/// </summary>
public class HotPlugAppTests
{
    private static ScreenInfo Display(int index, string label, int w, int h, int x, int y, int hz = 60, bool primary = false)
        => new($"{index}:{w}x{h}@{x},{y}", label, new PixelRect(x, y, w, h), 1.0, primary, index, Hz: hz);

    [AvaloniaFact]
    public void ADisplayUnpluggedLeavesItsScreenWaitingAndTheSameDisplayBackIsAdoptedAndTurnedOn()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var screens = services.Screens;
            var hot = services.HotPlug;
            var boot = screens.Real.ToList();                                               // the headless window's own display stays throughout
            var desk = Display(7, "Desk monitor", 1920, 1080, 7000, 0, primary: true);
            var stage = Display(8, "EPSON PJ", 1920, 1080, 8920, 0);
            IReadOnlyList<ScreenInfo> now = boot.Concat(new[] { desk, stage }).ToList();
            screens.Source = () => now;
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            var placement = vm.State.Output.Placements.Single(p => p.ScreenId == stage.Id);
            placement.CustomLabel = "Stage";
            Dispatcher.UIThread.RunJobs();
            Assert.True(placement.Enabled);
            Assert.Equal("EPSON PJ|1920x1080", placement.DisplayKey);                        // remembered, so it is known again
            Assert.Equal("8920,0", placement.DisplayOrigin);
            Assert.Equal(60, placement.DisplayHz);
            Assert.Equal("", hot.HealthWords);
            Assert.False(vm.HasHotPlug);

            // Unplugged: the screen waits, planned and off, under an id no display can carry — everything programmed for it kept.
            hot.Clock = () => new DateTime(2026, 9, 12, 19, 41, 58, DateTimeKind.Utc);
            now = boot.Concat(new[] { desk }).ToList();
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(placement, vm.State.Output.Placements);                           // never removed
            Assert.True(placement.Planned);
            Assert.False(placement.Enabled);
            Assert.True(placement.WasEnabled);
            Assert.True(placement.UserPinned);
            Assert.StartsWith(HotPlugWatch.LostIdPrefix, placement.ScreenId);
            Assert.Equal(1920, placement.PlannedWidth);
            Assert.Equal(1080, placement.PlannedHeight);
            Assert.NotNull(placement.LostAtUtc);
            Assert.Equal("Stage", placement.CustomLabel);
            Assert.StartsWith("SCREEN MISSING: 'Stage' unplugged at", hot.HealthWords);
            Assert.StartsWith("SCREEN UNPLUGGED — 'Stage' (EPSON PJ 1920×1080 @ 60 Hz) unplugged at", vm.StatusMessage);
            Assert.Contains("plug it back in and it comes back on", hot.Status);
            Assert.True(vm.HasHotPlug);
            Assert.Contains(services.Journal.Tail(5), e => e.Kind == "ScreenLost" && e.Target == "Stage");
            Assert.Contains("MISSING", screens.All.Single(s => s.Id == placement.ScreenId).Description);
            Assert.Equal(boot.Count + 2, vm.State.Output.Placements.Count);                 // the desk's reconcile added nothing

            // The same display back — under whatever id the index gives it — is recognised, adopted and turned on.
            var stageAgain = Display(9, "EPSON PJ", 1920, 1080, 8920, 0);
            now = boot.Concat(new[] { desk, stageAgain }).ToList();
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            Assert.False(placement.Planned);
            Assert.True(placement.Enabled);
            Assert.Null(placement.LostAtUtc);
            Assert.Equal(stageAgain.Id, placement.ScreenId);
            Assert.Equal("Stage", placement.CustomLabel);
            Assert.Equal("", hot.HealthWords);
            Assert.StartsWith("SCREEN BACK — 'Stage' is back: EPSON PJ 1920×1080 @ 60 Hz adopted and turned on.", vm.StatusMessage);
            Assert.Equal(boot.Count + 2, vm.State.Output.Placements.Count);
            Assert.False(vm.HasHotPlug);
            Assert.Contains(services.Journal.Tail(5), e => e.Kind == "ScreenBack" && e.Target == "Stage");
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ADisplayUnpluggedOnTheLeftReIndexesTheRestAndAStrangerIsOfferedAsASubstituteOrItsOwnScreen()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var screens = services.Screens;
            var hot = services.HotPlug;
            hot.ModesOf = info => info.Label == "BENQ" ? new[] { (1280, 720, 60), (1920, 1080, 60) } : new[] { (3840, 2160, 60) };
            var forced = new List<(string Label, (int Width, int Height, int Hz) Mode)>();
            hot.ApplyMode = (info, mode) =>
            {
                forced.Add((info.Label, mode));
                return "";
            };
            var boot = screens.Real.ToList();
            var desk = Display(7, "Desk monitor", 1920, 1080, 7000, 0, primary: true);
            var pjL = Display(8, "EPSON PJ", 1920, 1080, 8920, 0);
            var pjR = Display(9, "EPSON PJ", 1920, 1080, 10840, 0);       // two of one make: told apart as far as the facts allow
            IReadOnlyList<ScreenInfo> now = boot.Concat(new[] { desk, pjL, pjR }).ToList();
            screens.Source = () => now;
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            var placements = vm.State.Output.Placements;
            var deskPlacement = placements.Single(p => p.ScreenId == desk.Id);
            var left = placements.Single(p => p.ScreenId == pjL.Id);
            var right = placements.Single(p => p.ScreenId == pjR.Id);
            deskPlacement.CustomLabel = "Desk";
            deskPlacement.Enabled = true;
            deskPlacement.UserPinned = true;
            left.CustomLabel = "PJ left";
            right.CustomLabel = "PJ right";
            Dispatcher.UIThread.RunJobs();

            // The desk monitor goes; the desktop re-anchors: the projectors come back re-indexed and shifted — both keep their screens.
            var pjL2 = Display(7, "EPSON PJ", 1920, 1080, 7000, 0);
            var pjR2 = Display(8, "EPSON PJ", 1920, 1080, 8920, 0);
            now = boot.Concat(new[] { pjL2, pjR2 }).ToList();
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            Assert.False(left.Planned);
            Assert.False(right.Planned);
            Assert.Equal(new[] { pjL2.Id, pjR2.Id }.OrderBy(x => x), new[] { left.ScreenId, right.ScreenId }.OrderBy(x => x));
            Assert.True(deskPlacement.Planned);
            Assert.True(placements.Count == boot.Count + 3, string.Join(" | ", placements.Select(p => $"{p.ScreenId}:{p.CustomLabel}:{(p.Planned ? "planned" : "live")}:{p.DisplayKey}")));
            Assert.Equal("Desk", HotPlugWatch.LostName(Assert.Single(hot.LostScreens)));

            // A display the rig has never met, while 'Desk' waits: off, its own placement, and the offer — this one can be forced to the size.
            var benq = Display(9, "BENQ", 1280, 720, 10840, 0);
            now = boot.Concat(new[] { pjL2, pjR2, benq }).ToList();
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            var stranger = placements.Single(p => p.ScreenId == benq.Id);
            Assert.False(stranger.Enabled);
            Assert.True(stranger.UserPinned);
            Assert.Equal(boot.Count + 4, placements.Count);
            var offer = Assert.Single(hot.Offers);
            Assert.Same(deskPlacement, offer.Lost);
            Assert.True(offer.CanSubstitute);
            Assert.Equal("FORCE 1920×1080 @ 60 Hz AND SUBSTITUTE FOR 'Desk'", offer.SubstituteLabel);
            Assert.StartsWith("NEW DISPLAY — BENQ 1280×720 @ 60 Hz connected while 'Desk' is missing.", vm.StatusMessage);
            Assert.Contains("can be forced to 1920×1080 @ 60 Hz", vm.StatusMessage);
            Assert.True(vm.HasHotPlug);
            Assert.Same(offer, Assert.Single(vm.HotPlugOffers));

            // USE AS SUBSTITUTE: the mode is forced first; the display comes back under a new id and size and then stands in.
            vm.SubstituteScreenCommand.Execute(offer);
            Assert.Equal(("BENQ", (1920, 1080, 60)), Assert.Single(forced));
            Assert.StartsWith("Switching BENQ 1280×720 @ 60 Hz to 1920×1080 @ 60 Hz", vm.StatusMessage);
            Assert.Empty(hot.Offers);
            var benqHd = Display(9, "BENQ", 1920, 1080, 10840, 0);
            now = boot.Concat(new[] { pjL2, pjR2, benqHd }).ToList();
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(benqHd.Id, deskPlacement.ScreenId);
            Assert.False(deskPlacement.Planned);
            Assert.True(deskPlacement.Enabled);
            Assert.Equal("BENQ|1920x1080", deskPlacement.DisplayKey);
            Assert.Equal("Desk", deskPlacement.CustomLabel);
            Assert.Equal(boot.Count + 3, placements.Count);                                   // the stranger's own placement gave way
            Assert.Empty(hot.LostScreens);
            Assert.StartsWith("SCREEN SUBSTITUTED — 'Desk' has a substitute: BENQ 1920×1080 @ 60 Hz adopted and turned on.", vm.StatusMessage);
            Assert.False(vm.HasHotPlug);

            // ITS OWN SCREEN: a display that cannot stand in (no mode of the size) is a screen of its own, on; the lost one keeps waiting.
            var rightId = right.ScreenId;
            now = boot.Concat(new[] { pjL2, pjR2, benqHd }).Where(s => s.Id != rightId).ToList();
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            Assert.True(right.Planned);
            var tv = Display(8, "LG TV", 3840, 2160, 8920, 0);
            now = boot.Concat(new[] { pjL2, pjR2, benqHd }).Where(s => s.Id != rightId).Concat(new[] { tv }).ToList();
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            var tvOffer = Assert.Single(hot.Offers);
            Assert.False(tvOffer.CanSubstitute);
            Assert.Contains("another resolution (3840×2160) and no 1920×1080 mode offered", tvOffer.Words);
            Assert.Contains("cannot stand in", hot.Substitute(tvOffer));                       // refused in words, nothing done
            Assert.True(right.Planned);
            vm.OwnScreenCommand.Execute(tvOffer);
            var tvPlacement = placements.Single(p => p.ScreenId == tv.Id);
            Assert.True(tvPlacement.Enabled);
            Assert.Empty(hot.Offers);
            Assert.True(right.Planned);
            Assert.StartsWith("LG TV 3840×2160 @ 60 Hz is its own screen, on", vm.StatusMessage);
            Assert.Contains("'PJ right' still waits", vm.StatusMessage);
            Assert.True(vm.HasHotPlug);                                                        // a screen still waits

            // The TV goes and PJ right's own display is back: PJ right is adopted; the TV — a screen of its own now — waits in its turn.
            now = boot.Concat(new[] { pjL2, pjR2, benqHd }).ToList();
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            Assert.False(right.Planned);
            Assert.Equal(rightId, right.ScreenId);
            Assert.Same(tvPlacement, Assert.Single(hot.LostScreens));
            Assert.True(tvPlacement.Planned);
            Assert.Equal(3840, tvPlacement.PlannedWidth);
            // …and is back: adopted and on, as it was.
            now = boot.Concat(new[] { pjL2, pjR2, benqHd, tv }).ToList();
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            Assert.False(tvPlacement.Planned);
            Assert.True(tvPlacement.Enabled);
            Assert.Equal(tv.Id, tvPlacement.ScreenId);
            Assert.Empty(hot.LostScreens);
            // A new display with no screen waiting is simply a screen of its own, as ever.
            var extra = Display(11, "Extra", 1920, 1080, 12760, 0);
            now = boot.Concat(new[] { pjL2, pjR2, benqHd, tv, extra }).ToList();
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(hot.Offers);
            Assert.True(placements.Single(p => p.ScreenId == extra.Id).Enabled);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// Round 76: a display unplugged re-indexes the others, and their output windows used to be closed and
    /// opened again under the new ids — black on every output the unplugged display had nothing to do with.
    /// Now the hot-plug pass's own renames carry each window over: the same window, the same pipeline, the
    /// same last frame, moved to the display's new place; only the unplugged display's window closes.
    /// </summary>
    [AvaloniaFact]
    public void TheOutputWindowsOfTheOtherDisplaysAreCarriedOverAHotPlugNotClosedAndOpenedAgain()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var screens = services.Screens;
            var boot = screens.Real.ToList();
            var desk = Display(7, "Desk monitor", 1920, 1080, 7000, 0, primary: true);
            var pjL = Display(8, "EPSON PJ", 1920, 1080, 8920, 0);
            var pjR = Display(9, "EPSON PJ", 1920, 1080, 10840, 0);
            IReadOnlyList<ScreenInfo> now = boot.Concat(new[] { desk, pjL, pjR }).ToList();
            screens.Source = () => now;
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            foreach (var p in vm.State.Output.Placements) p.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            var on = services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Assert.True(on.Ok, on.Message);
            Dispatcher.UIThread.RunJobs();
            var placements = vm.State.Output.Placements;
            var deskPlacement = placements.Single(p => p.ScreenId == desk.Id);
            var left = placements.Single(p => p.ScreenId == pjL.Id);
            var right = placements.Single(p => p.ScreenId == pjR.Id);
            var before = services.Outputs.Windows.ToDictionary(w => w.TargetScreenId);
            Assert.True(before.ContainsKey(desk.Id) && before.ContainsKey(pjL.Id) && before.ContainsKey(pjR.Id), string.Join(", ", before.Keys));
            var leftWindow = before[pjL.Id];
            var rightWindow = before[pjR.Id];
            Assert.Equal(0, services.Outputs.CarriedOver);

            // The desk monitor goes; the projectors come back re-indexed and shifted left, and Windows — which always
            // names one display primary — promotes a projector to primary, as it does when the primary is unplugged.
            // Two identical projectors: the model keeps each screen by its display's name and size where it can and
            // renames the other onto the display left over — whatever it decided, every live screen keeps the very
            // window it had, moved to where its display is, and the promotion turns no live output off.
            var pjL2 = Display(7, "EPSON PJ", 1920, 1080, 7000, 0, primary: true);
            var pjR2 = Display(8, "EPSON PJ", 1920, 1080, 8920, 0);
            now = boot.Concat(new[] { pjL2, pjR2 }).ToList();
            screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            Assert.True(deskPlacement.Planned);                                                 // the unplugged display's screen waits
            Assert.Contains(desk.Id, services.HotPlug.RecentLost);
            Assert.Equal(new[] { pjL2.Id, pjR2.Id }.OrderBy(x => x), new[] { left.ScreenId, right.ScreenId }.OrderBy(x => x));
            var renamed = new[] { (left, pjL.Id), (right, pjR.Id) }.Count(x => x.Item1.ScreenId != x.Item2);
            Assert.True(renamed >= 1, "at least one projector was re-identified under a new id");
            foreach (var (p, was) in new[] { (left, pjL.Id), (right, pjR.Id) })
            {
                if (p.ScreenId != was) Assert.Equal(p.ScreenId, services.HotPlug.RecentRenames[was]);
            }

            var after = services.Outputs.Windows.ToDictionary(w => w.TargetScreenId);
            var story = $"before: {string.Join(", ", before.Keys)} | after: {string.Join(", ", after.Keys)} | renames: {string.Join(", ", services.HotPlug.RecentRenames.Select(kv => kv.Key + "→" + kv.Value))} | lost: {string.Join(", ", services.HotPlug.RecentLost)} | left now {left.ScreenId} right now {right.ScreenId} | carried {services.Outputs.CarriedOver}";
            Assert.True(ReferenceEquals(leftWindow, after[left.ScreenId]), "left: " + story);   // the very window, carried or kept
            Assert.True(ReferenceEquals(rightWindow, after[right.ScreenId]), "right: " + story);
            var byId = now.ToDictionary(d => d.Id);
            Assert.Equal(byId[left.ScreenId].Bounds, after[left.ScreenId].ScreenBounds);         // at its display's place now
            Assert.Equal(byId[right.ScreenId].Bounds, after[right.ScreenId].ScreenBounds);
            Assert.True(after.Count == before.Count - 1, "count: " + story);                     // the unplugged display's window alone closed
            Assert.True(left.Enabled && right.Enabled, "the promotion to primary turned no live output off");
            Assert.Equal(renamed, services.Outputs.CarriedOver);
            Assert.True(services.Outputs.IsLive);

            // The pass is acted on once: a later re-apply carries nothing again.
            services.Outputs.OnScreensChanged();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(renamed, services.Outputs.CarriedOver);
            Assert.Same(leftWindow, services.Outputs.Windows.Single(w => w.TargetScreenId == left.ScreenId));
        }
        finally
        {
            b.Dispose();
        }
    }

    private static void Settings(string dir, Action<ShowState> edit)
    {
        var s = SettingsStore.Fresh();
        edit(s);
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
    }

    /// <summary>
    /// Round 77.4: the boot's first refresh runs before the window is attached, with no display in the
    /// list — and the replacement's boot (every boot) marked every screen unplugged and then back a
    /// moment later, journaled and notified. Before a display has been seen an empty list decides
    /// nothing; once one has, every display gone is every screen unplugged, as before.
    /// </summary>
    [AvaloniaFact]
    public void TheBootsFirstLookAtAnEmptyDisplayListUnplugsNothing()
    {
        ScreenInfo display;
        var first = TestApp.Boot("patterns-hotplug-boot-a-");
        try
        {
            display = first.Services.Screens.Real[0];
        }
        finally
        {
            first.Dispose();
        }

        var b = TestApp.Boot("patterns-hotplug-boot-b-", dir => Settings(dir, s =>
        {
            s.Output.Placements.Clear();
            s.Output.Placements.Add(new ScreenPlacement
            {
                ScreenId = display.Id,
                Enabled = true,
                CustomLabel = "Main",
                DisplayKey = $"{display.Label}|{display.Bounds.Width}x{display.Bounds.Height}",
                DisplayOrigin = $"{display.Bounds.X},{display.Bounds.Y}",
                DisplayHz = display.Hz,
            });
        }));
        try
        {
            var services = b.Services;
            var placement = Assert.Single(b.Vm.State.Output.Placements, p => p.CustomLabel == "Main");
            Assert.Equal(display.Id, placement.ScreenId);
            Assert.True(placement.Enabled);
            Assert.Null(placement.LostAtUtc);
            var journal = services.Journal.Tail(50);
            Assert.DoesNotContain(journal, e => e.Kind == "ScreenLost");
            Assert.DoesNotContain(journal, e => e.Kind == "ScreenBack");
            Assert.Equal("", services.HotPlug.HealthWords);

            // A display has been seen: every display gone is every screen unplugged — the guard is the boot's alone.
            services.HotPlug.Clock = () => new DateTime(2026, 9, 17, 21, 0, 0, DateTimeKind.Utc);
            services.Screens.Source = () => Array.Empty<ScreenInfo>();
            services.Screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(services.Journal.Tail(50), e => e.Kind == "ScreenLost");
            Assert.StartsWith(HotPlugWatch.LostIdPrefix, placement.ScreenId);
        }
        finally
        {
            b.Dispose();
        }
    }
}
