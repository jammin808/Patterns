# Field research: what goes wrong at the desk, and what Patterns does about it

Round 12 asked: *research the problems and issues show callers, techs and operators have with
event workflows with hardware and software — forums, social media, AV issues — and how Patterns
can resolve and fix them.* This is that research, written so the next round can act on it. Each
problem is stated the way the people who live with it state it, with its source; then what
Patterns already does (with the page or the verb), what this round added for it, and what still
stands.

**How it was gathered.** Web searches over the places operators talk — ControlBooth, the vMix,
Resolume and NewTek forums, the QLab mailing list, AVS Forum, the Bitfocus Companion issue
tracker, production companies' own field guides (MeyerPro, Clarity Experiences, AV Land, AVT
Productions) and the digital-signage trade. The build environment's proxy refused most forum
pages themselves, so the evidence below is the indexed excerpts of those pages, each cited by URL;
nothing here is invented, and where an excerpt was thin the claim is kept thin too.

---

## 1. The show software freezes, crashes, or the machine reboots — mid-show

**What people report.** Resolume users describe the laptop and the output freezing when a clip
starts, the screen going black, and after a forced restart *Windows configuring updates while the
show continued on a black LED background*; freezes with no error and no crash report that need
Task Manager; crash reports ending in the Nvidia driver
([Resolume forum](https://resolume.com/forum/viewtopic.php?t=26944),
[Arena freezing](https://resolume.com/forum/viewtopic.php?t=30886)). vMix users report crashes
during a live church stream, lag visible to the audience before the crash, and a build crashing
several times a day ([vMix forum](https://forums.vmix.com/posts/t24020-vMix--crash),
[vMix crashing](https://forums.vmix.com/posts/t31049-vMix-Crashing---Please-Help--Resolved)).
The standing advice is to pause Windows Update for the show and set active hours to the maximum
([Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/4023871/how-to-stop-windows-update-ignoring-settings-and-r),
[Manage device restarts](https://learn.microsoft.com/en-us/windows/deployment/update/waas-restart)).

**What Patterns does.** The watchdog runs the app as its child and restarts it within seconds
when it crashes or its UI thread stops answering, putting the show back live from the recovery
record (Machine → STABILITY). A render fault is contained to the frame that threw; the show keeps
running and the fault is counted. The advisor's *outputs frozen* rule sees the one failure a
heartbeat cannot — a render path stuck while the desk still answers — and says so. Nothing in
Patterns updates itself during a show: updates are a staged package applied only when asked
(Install page) and rolled back by the watchdog if the new build does not stay up. A driver that
takes the process down is a restart, not a black wall for the rest of the day.

**This round.** The Machine page's HEALTH AT A GLANCE puts the watchdog, the faults, the uptime
and the power on lit tiles with a verdict, so a machine that has already restarted twice is
visible before doors, not after. The beacon and a second machine remain the answer to the fault
no software survives — the machine itself.

**Still standing.** Patterns cannot stop Windows Update rebooting the machine. Candidate: a
super-check row that reads the *pending reboot* flag and the active hours and goes red before a
show — a registry read, small.

## 2. Double GO, double click, a clicker that skips

**What people report.** QLab operators with an RF presentation remote press twice and two cues
fire; a conductor's "double GOs" in a complicated show; QLab's answer is a *minimum time between
GOs* that flashes red and discards the second press
([QLab list](https://groups.google.com/g/qlab/c/4XQkzheKQJA),
[QLab settings](https://qlab.app/docs/v5/fundamentals/workspace-settings/)). Presentation
remotes skip slides because the key-down outlasts the keyboard's repeat delay
([Medium](https://tajmo.medium.com/why-does-my-presentation-remote-keep-skipping-past-slides-80085667c0f0));
one clicker driving two machines advances the one nobody is looking at
([PresentationTools](https://www.presentationtools.com/aps-clickers/)).

**What Patterns does.** Every key on the desk and on the output windows acts once per physical
press — the OS auto-repeat is ignored, so a held key or a long RF pulse cannot fire twice. GO from
a remote carries the standby id it last saw, and a GO that races a standby move answers *ERR
standby moved* instead of firing the wrong cue. A cue marked *confirm* asks for a second GO
within four seconds. HOLD is a latched inhibit; the clicker is armed on purpose. The deck's
pages turn only while the deck is on air, and the stack resumes from the deck's end only when the
deck asks for it.

**Still standing.** A QLab-style *minimum time between GOs* setting is not there. Candidate: one
number on the Cues page (default 0), the GO gate refusing a second GO inside it and the strip
flashing — an afternoon.

## 3. Dead air: the black between presenters, the transition that drags

**What people report.** *Nothing creates dead air faster than a technical glitch*; without a
show caller *transitions drag*; the advice is a *Plan B for every transition*, a *stay tuned*
slide ready on the switcher, every file pre-loaded and tested on the show computer
([Clarity Experiences](https://www.clarityexperiences.com/blog/beyond-the-av-roles-that-make-or-break-your-production),
[DJ Reese](https://www.nostresszoneent.com/eliminate-dead-air-at-conventions/),
[Jammin' DJs](https://www.jammindjs.net/atlanta-corporate-dj/10-reasons-your-corporate-event-av-isnt-working-and-how-to-fix-it/)).

**What Patterns does.** A look is one press — an F-key, a tile on the Show panel, a Companion
key, a line on the wire — and LOOK BACK puts the previous one back. FREEZE holds every output's
last frame while anything is re-patched behind it. A holding look with break music is a look like
any other; a stinger's *what happens after* takes the show to the next cue or a look by itself.
The cue stack's auto-follow removes the wait for a hand; the transition setting fades every
change so a cut is never a flash.

**This round.** The Show panel became the control surface: the cue strip, the looks, each screen
on its own and the progression on one page, so the operator never leaves it to find the next
thing. A screen's own send changes one screen without a whole-look transition on the others.

## 4. The wrong version of the deck goes on stage

**What people report.** *FINAL_final_REALfinal_v7.pptx*; a presenter walking on in Austin to
find the AV team had loaded an earlier draft; files late, mislabelled or incomplete so *the crew
has to guess — and that is where mistakes happen*; the fix is a lead operator who bumps the
version after every change and a run sheet that names the file, not "play video"
([MeyerPro file checklist](https://meyerproinc.com/av-file-checklist-live-event/),
[Feel the Boot](https://www.feeltheboot.com/blog/pitching-when-tech-is-glitching),
[Clarity Experiences](https://www.clarityexperiences.com/blog/beyond-the-av-roles-that-make-or-break-your-production)).

**What Patterns does.** A deck or a clip is mounted into the show with its path and shown on the
Media page with its page count and the tool that rendered it; a cue's summary names what it puts
on air; the journal records what actually went on and from where; the show file keeps the previous
version and twenty earlier ones (Machine → EARLIER VERSIONS). The cue validator marks a cue whose
file is missing as broken before the show, not during it.

**Still standing.** No version stamp or approval on media. Candidate: a content manifest — a hash
and a label per mounted file, listed on the super-check, so *v7 loaded at 09:12* is a fact on the
report, not a memory.

## 5. The presenter's laptop will not connect; HDMI handshakes fail mid-show

**What people report.** A speaker's laptop that would not recognise the projector in front of
200 people; EDID and HDCP handshakes that fail through splitters, switches and older cables, and
re-sync on every source change; latency in the chain causing mid-show signal loss
([Feel the Boot](https://www.feeltheboot.com/blog/pitching-when-tech-is-glitching),
[AVS Forum](https://www.avsforum.com/threads/problems-with-my-projector-losing-hdmi-signal.3242642/),
[OREI](https://www.orei.com/blogs/news/why-your-hdmi-signal-drops-and-how-to-fix-it)).

**What Patterns does.** The wall is driven by the show machine, not by the presenter's laptop:
the laptop is an input (a capture card, NDI, or the deck itself imported) and the outputs never
re-handshake when the source changes. IDENTIFY and the test patterns prove every cable before
doors; the Screens page picks the display mode; direct output bypasses the compositor on a
suitable card; a lost input shows the last frame or the holding look, not a blue *no signal*.

**Still standing.** EDID emulation is hardware; Patterns can only make the source change
invisible to the wall, which it does.

## 6. Aspect mismatches and slides that do not fit the wall

**What people report.** *Presenters bringing 16:10 slides to a 16:9 LED wall* is named as a
common mistake; the fix is a template and a brief
([AV Labor Source](https://avlaborsourceinc.com/blog/corporate-event-av-staffing-checklist-guide-2026)).

**What Patterns does.** A deck renders at the screen's size at its own aspect, letterboxed, never
stretched; the area of interest crops any input to the part that matters (a Teams window without
its furniture); canvases have known sizes and the test patterns show them; a look carries the crop
so it is right every time the cue fires.

## 7. Confidence monitors showing the wrong thing

**What people report.** *Network interruptions, resolution mismatches, accidental keystrokes and
operator handoffs* all put the wrong slide on the confidence monitor; presenters want the current
slide, the next slide and notes on separate screens
([cuetime.io](https://www.cuetime.io/blogs?c1350cf7_page=1&post=confidence-monitor-for-presenters-that-works),
[MeyerPro](https://meyerproinc.com/confidence-monitor-setup/),
[Rick Cornish](https://rickcornish.com/2013/11/01/tech-tip-using-powerpoint-on-the-big-stage/)).

**What Patterns does.** A screen has a role: a CONFIDENCE or INFO screen keeps its own picture and
is left alone by looks, cues, TAKE ALL and stingers; a lock does the same for any screen; the
multiview's tally says which screen shows what. A screen's own send (this round) puts one look's
picture on one screen alone from the panel, a cue or the wire.

**Still standing.** A *next page* view of the deck on air as a source for the confidence screen
is not there. Candidate: a `Deck (next page)` media source and a `Deck (notes)` text source, so a
confidence look is the current page on the wall and the next page on the monitor.

## 8. The running order changes at the last minute

**What people report.** *The run of show is constantly changing leading up to and often during a
show*; printed sheets are outdated by rehearsal; a presenter moved their segment up five minutes
before walking on, and the show held because the sheet was live and every file was labelled
([Rundown Studio](https://rundownstudio.app/tools/cue-sheet-generator/),
[Shoflo](https://blog.shoflo.tv/what-is-a-production-cue-sheet),
[AVT Productions](https://avtproductions.com/the-ultimate-run-of-show-checklist-for-stress-free-corporate-events/)).

**What Patterns does.** The cue sheet imports from CSV or Excel and again after a change; the
stack is edited in place with the validator reading every cue; the planned times and the clock
say what is late as the day runs; the Run page, the panel's strip and `/run` on a tablet show the
same stack the moment it changes; `CUE LIST` gives it to any other system as JSON; the standby
moves with ▲ ▼ so a swapped segment is two presses, not a re-print.

**Still standing.** No shared, multi-user rundown of its own — the CSV bridge to Rundown Studio,
Shoflo or a spreadsheet is the way. Candidate: watch the imported file and offer a one-press
re-import when it changes on disk.

## 9. NDI drops out

**What people report.** A sender with several active network interfaces announces all of them and
the receiver connects to the wrong one; random dropouts after a switch change; Wi-Fi that cannot
carry it; Dante on the same VLAN interfering
([TroikaTronix](https://community.troikatronix.com/topic/4801/ndi-drop-out),
[NDI switch guide](https://docs.ndi.video/all/using-ndi/using-ndi-with-hardware/recommended-network-switch-settings-for-ndi),
[VIDVOX](https://discourse.vidvox.net/t/ndi-network-wifi-problems-and-solutions/459),
[NewTek forums](https://forums.newtek.com/threads/ndi-drop-outs.163609/)).

**What Patterns does.** Every send has a status on the NDI page and in STATE; the super-check
lists the runtime and every send; the dashboard's NDI tile reads *n of m* and goes amber the
moment a send is not running; a send held by PREP says so. A receiver that drops shows the last
frame, not black.

**Still standing.** Patterns cannot fix the network. Candidate: a super-check row that counts the
machine's active network interfaces and names the one the sends go out on, so *two NICs* is a
warning before the receiver finds out.

## 10. Audio and video drift apart over a long playback

**What people report.** Different paths and processing add different delays; long playbacks
drift; a few tens of milliseconds is the threshold people notice
([Resi](https://resi.io/glossary/audio-video-synchronization/),
[TestDevLab](https://www.testdevlab.com/blog/how-to-test-audio-video-sync),
[Bzbgear](https://bzbgear.com/knowledge-base/how-to-eliminate-audio-delay-lip-sync-issues-in-live-broadcasts/)).

**What Patterns does.** One master clock for every sink, resampling on every audio path so a
long track never drifts against the pictures, a delay per output, the SYNC CHECK flash and click,
and the Audio tile's *worst lag* read live.

## 11. Nobody sees the machine's health until it is too late

**What people report.** Laptops throttled on battery, machines full of background apps, memory
that climbs all day, a stream that stopped by itself and nobody noticed; the field guides ask
for a technical rehearsal and files tested *on the actual system*
([AV Land](https://av.land/how-to-prepare-for-livestream-failure-at-corporate-events/),
[AV Labor Source](https://avlaborsourceinc.com/blog/corporate-event-av-staffing-checklist-guide-2026)).

**What Patterns does.** The Machine page: HEALTH AT A GLANCE (this round) — twelve lit tiles and
one verdict, the warnings and what to do about them worst first, the last three minutes beside
the day so far — the advisor's rules (a leak, a frozen render path, a stream that stopped, a
battery, a full disk), the super-check with the level of show the hardware is good for, and the
on-disk record for the morning after.

## 12. The control surface loses its state

**What people report.** Stream Deck keys grey after a reconnect until the surface is reset; a
plugin that stops working an hour into the stream; mixed button pages confusing the deck
([Companion #4190](https://github.com/bitfocus/companion/issues/4190),
[Companion #821](https://github.com/bitfocus/companion/issues/821)).

**What Patterns does.** The module reads STATE, which is pushed on every change and on demand
(`STATUS`), so a reconnected surface is right on its first render; keys label themselves from
the show's lists and rebuild when the lists change; devices of the Interactive area hear every
fact once on connect and RESEND ALL by hand; `PING` proves the line.

## 13. Signage that goes stale, freezes, or does not take an update

**What people report.** *Frozen screens in stores, outdated promotions, and black displays with no
one around to fix them*; content that does not refresh because the player was offline; *most
failures are operational — scheduling conflicts, approval bottlenecks, unmonitored devices*
([Monitors AnyWhere](https://monitorsanywhere.com/blog/top-digital-signage-mistakes/),
[Posterbooking](https://www.posterbooking.com/signage/digital-signage/troubleshoot/digital-signage-troubleshooting-25-common-problems-fixes/),
[piSignage](https://blog.pisignage.com/why-updating-a-screen-is-harder-than-it-looks-2/)).

**What Patterns does (this round).** The Install page: programmes on a rota, adverts at their
times, announcements by the clock or by hand, TODAY as the clock will run it; the watchdog for
the frozen screen; the management check-in that posts health and STATE every few minutes and
runs the reply's commands; the ADMIN page on the phone; staged updates the watchdog applies
between two starts and rolls back by itself.

**Still standing.** No proof-of-play report and no multi-site content distribution — the
check-in server is where both would live.

## 14. Rehearsal, testing on the real system, and the brief

**What people report.** The second most common mistake is skipping the technical rehearsal; the
brief must carry the running order with timings and every playback cue; assign one person to call
and one to run AV
([AV Labor Source](https://avlaborsourceinc.com/blog/corporate-event-av-staffing-checklist-guide-2026),
[FPC Events](https://fpcevents.com/blog/corporate-event-av-checklist)).

**What Patterns does.** PREP mode with planned screens so the whole show is built and rehearsed
before the rig exists; the walkthroughs by role on the Help page as live checklists; the
super-check; the cue validator; REVIEW on the multiview for the sign-off; the caller's Run page
and the operator's panel as two surfaces on one stack, which is the *one calls, one runs* the
guides ask for.

---

## What this round built in answer

| Problem above | Built in round 12 |
| --- | --- |
| 1, 11 | The Machine page as a health dashboard (commit 13); the watchdog and advisor rows on its tiles. |
| 2, 3 | The Show panel as the control surface — the cue strip, looks, per-screen sends, progression (commit 11). |
| 3, 7 | A screen's own look from the panel, a cue, the wire, OSC and Companion (commit 11); the multiview tally (commit 2). |
| 4, 6 | PDF decks at their own aspect with the click-through and the stack resuming (commit 5); PowerPoint through LibreOffice (commit 6); the area of interest (commit 3). |
| 5 | Web pages driven inside the engine — keys, clicks, presets — never a browser window on the wall (commit 4). |
| 8, 14 | The Help catalogue with the workflow context and steps per topic (commit 12); the walkthroughs stay. |
| 9 | Edge blend proved across rows, grids and corners with the audit (commit 7); NDI on the dashboard (commit 13). |
| 12 | Companion 2.x with keys that fill themselves from the show, every verb, richer STATE (commit 8); the Interactive area with facts pushed to devices (commit 9). |
| 13 | Permanent installs: the schedule, adverts, announcements, remote admin, updates (commit 10). |

## What still stands, ranked by how often it is reported against how small it is

1. **Minimum time between GOs** (problem 2) — one setting, the gate, a flash. Small.
2. **Pending Windows reboot on the super-check** (problem 1) — a registry read and a red row. Small.
3. **Two active NICs warning** (problem 9) — one row on the super-check and the NDI tile. Small.
4. **A content manifest** — hash and label per mounted file on the super-check (problem 4). Medium.
5. **Deck next-page and notes sources** for confidence screens (problem 7). Medium.
6. **Re-import when the sheet changes on disk** (problem 8). Small.
7. **Proof-of-play and multi-site distribution** through the check-in server (problem 13). Large, and a server.

## Sources

- [What is a Production Cue Sheet? — LASSO](https://www.lasso.io/articles/what-is-a-production-cue-sheet/)
- [Showcalling 101 — Rundown Studio](https://rundownstudio.app/blog/showcalling-101-basics-and-software/)
- [Free cue sheet generator — Rundown Studio](https://rundownstudio.app/tools/cue-sheet-generator/)
- [What is a production cue sheet — Shoflo](https://blog.shoflo.tv/what-is-a-production-cue-sheet)
- [Show Cue Systems feedback — ControlBooth](https://www.controlbooth.com/threads/show-cue-systems-feedback.43360/)
- [Presentation PC specs — ControlBooth](https://www.controlbooth.com/threads/presentation-pc-specs.44848/)
- [Beyond the AV: roles that make or break your production — Clarity Experiences](https://www.clarityexperiences.com/blog/beyond-the-av-roles-that-make-or-break-your-production)
- [How to prepare for livestream failure — AV Land](https://av.land/how-to-prepare-for-livestream-failure-at-corporate-events/)
- [The ultimate run of show checklist — AVT Productions](https://avtproductions.com/the-ultimate-run-of-show-checklist-for-stress-free-corporate-events/)
- [What files should you send your AV team — MeyerPro](https://meyerproinc.com/av-file-checklist-live-event/)
- [Confidence monitor setup — MeyerPro](https://meyerproinc.com/confidence-monitor-setup/)
- [Pitching when tech's glitching — Feel the Boot](https://www.feeltheboot.com/blog/pitching-when-tech-is-glitching)
- [Using PowerPoint on the big stage — Rick Cornish](https://rickcornish.com/2013/11/01/tech-tip-using-powerpoint-on-the-big-stage/)
- [Confidence monitor for presenters that works — cuetime.io](https://www.cuetime.io/blogs?c1350cf7_page=1&post=confidence-monitor-for-presenters-that-works)
- [Corporate event AV staffing checklist — AV Labor Source](https://avlaborsourceinc.com/blog/corporate-event-av-staffing-checklist-guide-2026)
- [The event manager's complete AV checklist — FPC Events](https://fpcevents.com/blog/corporate-event-av-checklist)
- [10 reasons your corporate event AV isn't working — Jammin' DJs](https://www.jammindjs.net/atlanta-corporate-dj/10-reasons-your-corporate-event-av-isnt-working-and-how-to-fix-it/)
- [Eliminate dead air at conventions — DJ Reese](https://www.nostresszoneent.com/eliminate-dead-air-at-conventions/)
- [Resolume Arena freezes then goes to black — Resolume forum](https://resolume.com/forum/viewtopic.php?t=26944)
- [Arena freezing — Resolume forum](https://resolume.com/forum/viewtopic.php?t=30886)
- [vMix 'crash'? — vMix forum](https://forums.vmix.com/posts/t24020-vMix--crash)
- [vMix crashing — vMix forum](https://forums.vmix.com/posts/t31049-vMix-Crashing---Please-Help--Resolved)
- [Preventing accidental double clicks — QLab list](https://groups.google.com/g/qlab/c/4XQkzheKQJA)
- [Workspace settings — QLab 5](https://qlab.app/docs/v5/fundamentals/workspace-settings/)
- [Why does my presentation remote keep skipping slides — Taj Moore](https://tajmo.medium.com/why-does-my-presentation-remote-keep-skipping-past-slides-80085667c0f0)
- [Solving the clicker problem — PresentationTools](https://www.presentationtools.com/aps-clickers/)
- [Problems with my projector losing HDMI signal — AVS Forum](https://www.avsforum.com/threads/problems-with-my-projector-losing-hdmi-signal.3242642/)
- [Why your HDMI signal drops — OREI](https://www.orei.com/blogs/news/why-your-hdmi-signal-drops-and-how-to-fix-it)
- [How to stop Windows Update restarting — Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/4023871/how-to-stop-windows-update-ignoring-settings-and-r)
- [Manage device restarts after updates — Microsoft Learn](https://learn.microsoft.com/en-us/windows/deployment/update/waas-restart)
- [NDI drop out — TroikaTronix forum](https://community.troikatronix.com/topic/4801/ndi-drop-out)
- [Recommended network switch settings for NDI — NDI docs](https://docs.ndi.video/all/using-ndi/using-ndi-with-hardware/recommended-network-switch-settings-for-ndi)
- [NDI network: Wi-Fi problems — VIDVOX](https://discourse.vidvox.net/t/ndi-network-wifi-problems-and-solutions/459)
- [NDI drop-outs — NewTek forums](https://forums.newtek.com/threads/ndi-drop-outs.163609/)
- [Audio video synchronization — Resi](https://resi.io/glossary/audio-video-synchronization/)
- [How to test audio-video sync — TestDevLab](https://www.testdevlab.com/blog/how-to-test-audio-video-sync)
- [Eliminate audio delay in live broadcasts — Bzbgear](https://bzbgear.com/knowledge-base/how-to-eliminate-audio-delay-lip-sync-issues-in-live-broadcasts/)
- [Stream Deck buttons grey after reconnect — Companion #4190](https://github.com/bitfocus/companion/issues/4190)
- [Mixing Companion buttons causes issues — Companion #821](https://github.com/bitfocus/companion/issues/821)
- [Common digital signage mistakes — Monitors AnyWhere](https://monitorsanywhere.com/blog/top-digital-signage-mistakes/)
- [Digital signage troubleshooting: 25 problems — Posterbooking](https://www.posterbooking.com/signage/digital-signage/troubleshoot/digital-signage-troubleshooting-25-common-problems-fixes/)
- [Why screen updates fail — piSignage](https://blog.pisignage.com/why-updating-a-screen-is-harder-than-it-looks-2/)
- [Remote digital signage management — 42Gears](https://www.42gears.com/blog/remote-digital-signage-management/)
