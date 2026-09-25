# Patterns re-reviewed after rounds 79 to 85

Repository `jammin808/Patterns` (public, MIT), branch `claude/nice-hopper-vf1paa`, read at `2746612` (round 85.7, 2026-09-25 00:24 UTC). This follows `docs/RETROSPECTIVE.md` on this branch, which read the tree at `08dcaaa` (round 78.5) and was handed in to open round 79. Since then the maintainer has run seven rounds: 50 commits from 2026-09-21 to 09-25, touching 167 files (+10,633 / -662 lines: source +2,827, tests +4,794, Markdown +2,772).

How it was checked, in short: a full clone (414 commits reachable from the head, no shallow boundary); eight read-only reviewers by area on round 84's head; round 85 read when it landed during the review; the build and all seven suites run at both heads; 42 reproducer tests written for the findings and run at `2746612`; repository, CI, tag and Release state read from the GitHub API. A finding marked **Confirmed** has a reproducer test that passes at `2746612` (each test asserts the defective behaviour, so a pass means the defect is there) or was read in the code at that head. **Read** means traced in the code but not run. **Reported** means a reviewer's finding I did not re-check. The full method is at the end.

## The short answer

The code half of the retrospective was taken, quickly and mostly well. Round 79 adopted ten of its sixteen entries in under three hours: results that carry whether the room could see them and what they changed, one build identity from git, one atomic-write helper, paced monitors, the newer-schema rule, a standing agent brief (`CLAUDE.md`) and a cumulative ledger of open items (`docs/OPEN.md`, now 46 rows). The build is clean, all seven suites pass at the head (2,592 tests, the number the papers give), and CI is green (run 268).

The evidence half was not taken. The retrospective's first and largest recommendation was to close the loop with the show laptop's own files and a short scripted walk. In seven rounds no file has reached `docs/field/`, no row has been filled in any of the 25 qualification sections, and the one report from the rig (round 81) was diagnosed from the maintainer's words. The other six rounds were opened by a handed-in critique (79, 82, 85), by "continue" (80, 83) or by web research (84), and five of them end by saying the walk is next.

That gap is where the new defects are. The take rules rewritten in rounds 78, 79 and 81 now refuse presses an operator expects to land and let through presses LOCK should stop. Running tests confirm, at the head:

- **A group take works once.** After one MAIN SCREENS take, the next picture is refused on the whole group until each tile is sent or put back on the programme. Once every screen is OWN, ALL ARMED refuses with "the air is already the preview" when it is not.
- **A wall TAKE is refused when it carries only a new brand kit, a lower third, or a change of ownership.** The desk's own refusal then advises the SEND the operator has just done.
- **LOCK is not absolute.** SEND TO TICKED and a cue's per-screen step change a locked screen on air, and SEND TO TICKED writes no journal row at all.
- **The new "could the room see it" field is rig-wide.** It says the outputs were live for a take made under FREEZE, on a screen faded to black on its own, and on a screen with no window.

Security moved both ways. Round 85 fixed the phone remote that round 83 had broken on a paired desk, and made a mistyped bind address fail closed. But round 83's cross-origin gate trusts the request's own `Host` header and trusts loopback, so a DNS-rebinding page can still run BLACKOUT on the default desk, and on a paired desk through the desk machine's own browser. On the TCP wire, an unpaired client can read the operator's journal through `EYE AT`. And the field instructions ask the maintainer to commit the raw recovery record to this public repository; that file holds the pairing token in clear. That last one traces back to my own retrospective, which asked for the recovery record to be committed and never said it held secrets.

Three papers claims are false at the head. The ledger's "checked and left" row says the history begins at `b746a8d`; it was computed in a shallow clone. QUALIFICATION §24 and the new §25 promise that a power cut's air is put back, and no start path does that. The one command meant to produce tags and Releases cannot produce a Release.

If I rebuilt the project now, I would keep every code recommendation and change how the evidence ones are delivered. A rule that costs the maintainer effort on the rig was written down and not practised; the same rule as a mechanism that costs less than skipping it might be. Concretely: one press on the desk that writes a redacted field pack, walk scripts whose expected readings come from the operator rather than the code, and a round that states which walk row it read before it builds anything new.

## What the seven rounds did

| Round | When (UTC) | Opened by | What it did | Commits |
|---|---|---|---|---|
| 79 | 09-21 00:01 to 02:46 | the retrospective, handed in | results carry `Visibility` and `Effect`, and a hand's wall TAKE that would change nothing is refused; the wire's fail-open words refused; the recorder's nine missing arms; the monitors paced; build identity, `createdump` target, newer-schema rule, bundle fence; `CLAUDE.md`, `docs/OPEN.md`, `docs/field/`, QUALIFICATION §22 | 10 |
| 80 | 09-21 18:43 to 19:29 | "continue": ledger rows that need no rig | the master rate follows the slowest display; the plan says which tile keeps its own picture; the module reads the descriptor version; one atomic-write helper | 6 |
| 81 | 09-23 15:14 to 16:23 | the maintainer's list from the rig, spoken | groups are screen kinds; a scoped take lands on its targets alone as their own pictures; an OWN tile's preview is its own until SEND or PROGRAM; focus and selection made one | 6 |
| 82 | 09-24 09:31 to 09:50 | an outside review with its patch set | the review's patches: finite time spans, finite numbers, the OSC reference fenced, per-message faults, the audience word list, a fuzz fence; ledger rows L31 to L41 | 9 |
| 83 | 09-24 11:29 to 12:05 | "continue": the review's rows | faults answered on the wire; a cross-origin gate and pairing on the state and the pictures; flush before move and a `.bak` for the recovery record; whole-word matching; feed caps; NDI frame shapes | 8 |
| 84 | 09-24 13:12 to 13:50 | web research (a connectomics post) | the Eye's replay (`EYE REPLAY`, `EYE AT`), synthetic shows as a fence, `docs/CONNECTOMICS.md` | 4 |
| 85 | 09-24 23:22 to 09-25 00:24 | a handed-in plan, "Show Brain Development Plan", against round 84's head | seven of its nine reported defects fixed, two designed as ledger rows L45 and L46 | 7 |

The cadence changed. Rounds 29 to 78 ran at about seven a day; these seven took four days, and most of the work was hardening rather than breadth. Round 85's method was good: every claim in the handed-in plan was read against the head and confirmed before anything was written, which is the retrospective's rule 9 working. Round 84 was the exception, a new operator feature (the replay, two wire verbs, STATE fields, menus) built from web research in the round it was proposed.

The three handed-in documents that opened rounds 79, 82 and 85 are cited by the papers but none is in the tree; round 72's `ROADMAP-ASSESSMENT.md` was committed, and that practice lapsed.

### What round 85 fixed on its own

The handed-in plan was prepared against the same head this review started from, independently. At least five of its nine items are findings this review had also made, which raises confidence in both.

| Plan item | This review's finding | Round 85 | Checked here |
|---|---|---|---|
| P1-01 browser pairing incomplete | SEC-1: the phone page and the admin page poll the state with no credential on a paired desk, so they show "Connection lost" for ever | fixed (85.6) | my reproducer now fails at the bare-poll assertion; the round's own tests cover the fix |
| P1-02 one state-access policy | part of EY-1 | fixed on the web; the wire's queries left open "on purpose" | `EYE AT` over `/api/cmd` is refused (403) at the head |
| P1-03 recovery ladder, null and torn | a different case from PB-5 below | fixed (85.3) | read |
| P1-04 newer-schema guard lost at promotion | PB-2, first of its four paths | fixed (85.4) | read; the other three paths remain (below) |
| P1-05 a display change restarts the encoder | RT-9 | designed, L45 | read |
| P1-06 a malformed bind falls back to every interface | WR-1 and PR-4 | fixed for the control ports, OSC and the audience socket (85.5) | read: `BindAddress` refuses anything that is not an address |
| P1-07 the replay reads rolling counts as cumulative | EY-4 | fixed (85.1) | my reproducer now fails as expected |
| P1-08 the replay reads its files on the UI thread | the cost half of EY-1 | fixed (85.2): a bounded, cancellable worker read | read |
| P1-09 output facts are not one set | TF-3, RT-1 and RT-5 are instances | designed, L46 | read |

## The retrospective, taken

### Its sixteen entries

| Entry | Taken? (PLAN §97.9) | How it went |
|---|---|---|
| 1. The show laptop's files and a scripted walk | taken as documents | 0 files, 0 walk rows in seven rounds (see "The process") |
| 2. Freeze the twin and nodes; games out of the tree | declined; status written (L21) | the audience room was hardened twice (82.5, 83.4) while L21 says it has never run on two machines |
| 3. One air: a sealed programme type | deferred, "viable, not now" (L28) | round 81 added no new reader of the wrong copy; STATE's `screens[]` still reads the edited state as "what this screen is drawing" |
| 4. Results carry the desk's state | taken (79.1) | well built, and then taken further into a refusal; see TF-1 to TF-4, TF-6 and "Where the retrospective was wrong" |
| 5. Measure before governing | taken for the monitors (79.4) | the ladder left alone, as advised |
| 6. Fences early | "done at round 70" | `CLAUDE.md` says `DateTime.Now` is banned in `src/`; it is not, and 57 lines use it |
| 7. Frame leases, lifetime census | "done" | fine; `TestApp.Boot` still is not `IDisposable` |
| 8. A probe per Windows native | WebView2 probe left open (L26) | unchanged |
| 9. A tested portable footprint | taken: target, check, release gate | the full bundle and both Release artefacts still lack `createdump.exe` (PB-6) |
| 10. Fixtures, newer-schema rule | rule taken, fixtures deferred | the rule's words never reach the desk at boot (PB-1); three writers still bypass it (PB-2) |
| 11. Build identity, commits, tags | identity and short subjects taken | still zero tags and zero Releases; the script cannot make a Release (PR-3) |
| 12. Agent memory apart from papers; a ledger | brief, ledger, requester taken; PLAN freeze declined | the papers' share of insertions rose from about 11% to 26% (41% in round 85) |
| 13. Trust on the control network | bind rule and lights taken | the cross-origin gate came from the round-82 review; DNS rebinding and the first-run token remain (SEC-2, L42) |
| 14. Integrations: fail closed, the module as oracle | taken | the oracle cannot see a free name spliced after a verb (WR-2) |
| 15. The assistant as unobserved | L22 | unchanged |
| 16. Smaller items | taken | done |

### Its 22 open rows

All 22 were carried into the ledger. Eighteen are closed and four remain open (L09 the policy key, L10 the WebView2 screencast, L21 two-machine features never run, L22 the assistant's model id). The reviewers verified each closure in the code: seven hold without qualification (L02, L04, L05, L12, L14, L15, L16) and eleven hold with a caveat found here, one of which (L03's) round 85 has since fixed.

| Row | Caveat at the head |
|---|---|
| L01 the recorder's missing kinds | a look named "Update Walk-in" or "2" is recorded as a line that replays as a different action (WR-2, Confirmed) |
| L03 OSC's bind | a mistyped bind still opened every interface until 85.5 fixed it |
| L06 the newer-schema rule | its notice reaches only `patterns.log` (PB-1, Confirmed); a node, Load show and SAVE AS write around it (PB-2, Read) |
| L07 the build identity | right on STATE, the Eye and CI; the log banner, the Super Check, the bundle and the check-in still read the assembly version (PB-7, Reported) |
| L08 `createdump` and the release gate | missing from the full bundle and the Releases (PB-6, Read) |
| L11 and L20 the 50 Hz display and the monitors | the follow's four defects (RT-1 to RT-4, Confirmed) |
| L13 the programme and sandbox pair | the stream renderer and the remote pictures still read the two separately (RT-5, Reported) |
| L17 the descriptor version | still 1 after round 81 changed what GROUPS means (SW-4, Read) |
| L18 one atomic write | a failed move leaves the temp file, credentials included (PB-9, Confirmed) |
| L19 take tests with an output open | every test opens every window, so the rig-wide stamp cannot fail a test (TF-3) |
| L27 stale prose | README still says every round has a tag and a Release |

### Its twenty rules for directing the agent

| Verdict | Rules |
|---|---|
| Adopted and practised | 3 ("not run here" becomes a walk row, in practice), 6 (Done is a claim), 9 (critiques handed in and checked at a line), 10 (no governor without a number), 17 (fail closed; the module's lines as oracle), 20 (the cumulative ledger, with gaps) |
| Partly | 8 (the "no measurement this round" line, but no pricing in lines and hours), 11 (outputs open and second presses, but the round-81 tests never drive the pointer), 14 (bind and lights; not the first-run token), 15 (the brief and the requester; comments still carry round numbers: 187 added in rounds 79 to 84, 114 of them after the brief banned it), 16 (the prefix and short subjects; CI never makes the tag) |
| Written, not practised | 1 (the four files), 2 (the walk), 19 (the closing sentence names the rig, but round 84 put two verbs on the wire in its own round, and no Companion import of a module version is recorded) |
| Declined | 4's written gate, 5 (freeze what cannot be verified) |
| Not addressed | 7 (external claims written as unverified; see PR-1 and PR-3), 18 (a recorded run of the simple path) |
| Unchanged or earlier | 12 (the WebView2 probe, L26), 13 (analyzers, from round 70) |

## Verified findings at the head

Ranked by what an operator or the maintainer would meet first. Severity is the reviewers' after my checks.

| ID | Severity | Finding | Status |
|---|---|---|---|
| SEC-2 | medium-high | The cross-origin gate is bypassed by DNS rebinding; loopback is trusted even with a token | Confirmed |
| PR-2 | medium, urgent | The field instructions would commit the pairing token to this public repository | Confirmed |
| SW-1 | medium | A group, ticked or tile take works once; the next picture is refused until SEND or PROGRAM on each tile | Confirmed |
| TF-4 / SW-3 | medium | LOCK is bypassed by SEND TO TICKED and by per-screen verbs; SEND TO TICKED writes no journal row | Confirmed |
| TF-1 | medium | A wall TAKE carrying only a brand kit or a lower third is refused for a hand, and measured as Nothing for automation | Confirmed |
| TF-2 | medium | A wall TAKE whose only effect is ownership is refused, advising the SEND just done | Confirmed |
| SW-2 | medium | After a tile's TAKE (and by reading SEND, PROGRAM or → PVW), the highlighted tile is not the one the editors change | Confirmed |
| PB-4 | medium | No start after a power cut restores the recovery record; QUALIFICATION §24 and §25 cannot pass | Confirmed |
| PB-3 | medium | A desk that stood down clears the live desk's recovery record, and its backup, at its own exit | Confirmed |
| TF-3 | medium | Visibility is rig-wide: FREEZE, a screen faded black, a screen with no window all read "live" | Confirmed |
| RT-2 | medium | The display follow trusts Windows' integer refresh: 29.97 Hz drops the show to 29 fps, and a reported 1 Hz to 1 fps | Confirmed |
| RT-3 | medium | A screen on its own frame rate still drags the master rate down | Confirmed |
| RT-1 | medium | Under EDIT SAFE the NDI "master" rate is computed from the frozen programme, not the live rig | Confirmed |
| RT-4 | medium (design) | The follow, on by default, makes every 60 Hz output judder on a mixed 50/60 Hz rig | Confirmed (cadence) |
| PB-1 | medium | A show file from a newer build runs unsaved with no word on the desk | Confirmed |
| PB-2 | medium | A node, Load show and SAVE AS still write around the newer-schema rule | Read |
| PB-6 | medium | `createdump.exe` is in neither the full bundle nor any Release | Read |
| PR-3 | medium | `tag-rounds.sh` pushes about seventy tags in one push; GitHub creates no event for more than three, so no Release follows | Read, with documented limit |
| SW-4 | medium | GROUPS silently changed meaning in round 81; old cues, sheets and deck keys now reach every main screen | Read |
| EY-5 | medium | After the journal rotates, the replay says "no rows" for a period the journal recorded | Confirmed |
| EY-2 | medium | The replay paints the journal's own alarm words (SCREEN UNPLUGGED, MISMATCH) grey | Confirmed |
| EY-3 | medium | The replay lights the screen numbered like a look, lower third or track | Confirmed |
| WR-2 | medium | Recorded lines splice a free name after a verb: "Update Walk-in" replays as LOOK UPDATE | Confirmed |
| PR-1 | medium | The ledger's "checked and left" row states a false history, computed in a shallow clone | Confirmed |
| PR-5 | medium | The walk scripts encode the code's behaviour as the pass; §20 passes what the operator rejected | Read |
| EY-1 | low-medium | An unpaired wire client reads the operator's journal rows through `EYE AT`, while RECORD is gated for that reason | Confirmed |
| TF-6, TF-7, TF-8, EY-6, PB-5, PB-9, PB-10 | low | see Appendix B | Confirmed |

### The switcher and the take rules

This is where the operator works and where the recent rounds changed most. The reproducer rig is the suites' own: three fake screens, headless windows, the view-model commands the desk binds to.

**SW-1, a group take works once.** A scoped take (FOCUSED, TICKED, a group, a canvas, a tile's own key) lands the preview on its targets as their own pictures (81.2). Round 81.3 then made an OWN tile's PVW its own picture until SEND or PROGRAM. Together: MAIN SCREENS lands Bars; the operator builds Focus in the programme's preview and presses MAIN SCREENS again; the press is refused with "Nothing to take on the main screens - they already show their own pictures". Once every screen is OWN, ALL ARMED refuses with "the air is already the preview", although the preview is Focus. The round's own tests step around this by pressing PROGRAM, a live cut to the programme, on each tile before the next group take (`ScopedTakeAppTests.cs:178-181`, `TakeScopeAppTests.cs:143`). By the switcher reviewer's reading of its steps, QUALIFICATION §23 as written fails at MAIN SCREENS and at ALL ARMED. This reproduces round 81's "stopped working" complaint at group scale.

**TF-4 and SW-3, LOCK is not absolute.** With tile 2 locked and ticked, SEND TO TICKED puts the preview on its air. The button calls `SandboxService.SendToTargets` straight from the view model (`MainViewModel.cs:330`), so no lock check, no journal row, no Visibility or Effect, no STATE `take.last` and no recorder line. A cue step "SCREEN 3 PATTERN Focus" on a locked screen is Done and the screen shows Focus. PLAN §99.3 says "LOCK means lock on every path", and the LOCK tooltip says the lock holds "through every look, cue, clicker step, TAKE ALL and stinger". Whether a per-screen send should pass a lock is the maintainer's call; the papers should then say so.

**TF-1 and TF-2, the wall's refusal predicts less than a take carries.** Round 79.1 added `SandboxService.WouldChange` (`SandboxService.cs:122`), which compares each taken screen's picture, the overlays and the countdown, and refuses a hand's wall TAKE when nothing differs (`ShowActions.Switcher.cs:194-197`). It does not compare the brand kit, a lower third put in the preview, the programme's own picture when every screen is OWN, or ownership. So:
- A new brand kit under EDIT SAFE: a hand's TAKE is refused; an automation's TAKE lands the kit and is measured as Effect Nothing.
- A lower third put in the preview, whose own words say "TAKE puts it on air": refused.
- SEND on a tile whose picture equals the air: the PVW badge lights, the take is refused with "SEND a picture to a tile first", and the tile keeps following the programme. The tile's own CUT of the same state is allowed.

**SW-2, the highlight and the editors part.** After a tile's TAKE (and, by reading, after SEND, PROGRAM or → PVW), the tile is highlighted, FOCUSED means it and the big PREVIEW pane shows it, but the editors stay on PROGRAM. Edits then change the programme's preview while the pane shows the tile's untouched picture. `TakeTruthAppTests.cs:88-94` asserts exactly this, so round 81's item 6 ("the highlighted tile is the focus of any editing") and REVIEW 81's "selection was already one" do not hold.

**TF-3, "could the room see it" is rig-wide.** `ShowActions.VisibilityNow` (`ShowActions.cs:77`) reads whether any output window is open. A take under FREEZE, on a screen faded to black on its own, or on a screen with OUT off and no window, is stamped OutputsLive. The desk already has the right per-target rule in the tile menu (`DeskMenuFacts.cs:142`). NDI senders keep running with the outputs off, so a take they carry is stamped OutputsOff (read, not run). This is round 85's L46 in miniature.

**SW-4, GROUPS changed meaning silently.** Before round 81, `GROUPS` and `CANVASES` were one case: the ticked canvases (`FadeScope.cs` at `08dcaaa`, lines 61-62). Now `GROUPS` means every screen of the ticked tiles' kinds (lines 76-78 at the head). No schema bump, no migration, no warning; a pre-81 cue "Fade to black: GROUPS", a CSV row, a deck key or `/patterns/fade/groups` now reaches every main screen. L30 names only the other direction, and its first closing path relies on a descriptor version that was never bumped.

### Security and the network

**SEC-2, DNS rebinding.** `HttpHead.IsCrossSite` (`HttpHead.cs:50-63`) decides "another origin" by comparing the request's `Origin` with its own `Host` header and believes `Sec-Fetch-Site: same-origin`. Under DNS rebinding the browser sends the attacker's name as both, so the request passes as the desk's own page. The reproducer sends exactly that shape: on the default desk (no token) `BLACKOUT ON` returns 200 and the blackout goes up; with a token set and loopback trusted (the default, `ControlService.cs:500`, `Paired` at :522), the same shape from loopback also runs. A plain cross-origin request is still refused, as 83.2 intended. Whether a given browser lets a public page reach a private or loopback address depends on its private-network protections, so real reachability varies by browser. The standard server-side fix works whatever the browser: accept only a `Host` that is one of the desk's own names or addresses, and stop trusting loopback once a token is set. On a default desk any machine on the network can already send verbs; what rebinding adds is reach from any web page a browser on that network opens.

**PR-2, the field folder would publish the token.** `docs/field/README.md` lists `patterns.recovery.json` among the four files to commit under `docs/field/round-NN/`, and warns only about `patterns.settings.json`. With EDIT SAFE open (the shipped default) the recovery record carries the programme as a whole show state (`AppServices.cs:1337`, `:1353`). The reproducer sets a token, opens the outputs and finds the token in clear in the file; the support bundle's redacted copy masks it. The repository is public, and `CLAUDE.md` forbids rewriting pushed history, so a committed secret could not be removed under the project's own rules. No report has been committed yet; the fix is one paragraph: commit the support bundle's redacted copies, never the raw files. My retrospective's rule 1 is where this instruction came from.

**EY-1, the journal on the wire.** `EYE AT <time>` is classed as a query (`ControlProtocol.cs:1426` is a deny-list), so an unpaired TCP client gets the replayed Eye, whose node words include the journal rows: the reproducer's reply carries "CueGo Keynote - Failed" with the row's message. Round 85 closed the same line on the web and wrote that the wire's queries stay open because "a TCP line is not a browser". But round 75 gated RECORD on the wire precisely so that "an unpaired connection hears what the operator does no more than it moves the show" (its comment at `ControlService.cs:429-430`), and polling `EYE AT <now>` gives the same feed with a delay. `STATUS` already exposes the show state on the wire, so the extra exposure is the history. Either gate `EYE AT` with RECORD or write the exception into the Trust paragraph.

**Still open from the ledger.** L33 (an update fetched over `http://` with an echoed token and no signature, applied by policy without the passcode) remains the most serious no-credential risk on a shared network; the security reviewer rates it correctly at medium. L36 (the twin link sends secrets over plain TCP after the key is proved) is weighted low and should be medium. L42 (control on, every interface, no token, announced by default) is the root that makes SEC-2 matter on the default desk.

### Recovery, persistence and release

**PB-4, a power cut is never restored.** After a battery pull the supervisor starts fresh with no restarts, so its child gets no `--recover` (`Supervisor.cs:377-382`); the dead run's ownership record reads as stale, so there is no takeover (`OutputTakeover.cs:304-309`); and `App.axaml.cs:73` restores only on one of those two. `PendingRecovery` is read and nothing acts on it. The reproducer boots on a fresh live record and the outputs stay closed. QUALIFICATION §24 (round 83) and §25 (round 85) both pass only if the air is put back after each pull, and round 83's flush and backup were motivated by exactly that restore. Either a cold start offers or performs the restore, or the walks say a power cut comes back dark by design.

**PB-3, one desk clears another's record.** When a desk stands down because another took its screens, it stays the folder's primary. At its clean exit the recovery step checks only `_restartRequested` and `_primaryInstance` (`AppServices.cs:2434-2436`), not the hand-over, so it deletes the live desk's record and, since 83.3, its `.bak`. The reproducer stands a desk down, writes the incoming desk's record, shuts the old desk and finds nothing.

**PB-1 and PB-2, the newer-schema rule.** At boot the notice is sent before the main window exists, so it lands only in `patterns.log`; the reproducer finds it on no desk surface: not the status line, the health line, a Super Check row or STATE. Round 85.4 made promotion honour the rule. A node's saves, Load show and SAVE AS still do not read `NewerSchema` (by grep at the head); by the reviewer's reading, Load show copies a newer file's schema number into this build's own file.

**PB-6, `createdump.exe`.** The target places it on the lean publish and CI checks it there (`build.yml:130`), but the full bundle is assembled from `Patterns.exe` alone (`:157`) and the Release copies only the exe and the manifest (`:323-324`). The builds most likely to meet a native decoder crash, the ones with libVLC, leave no dump.

**PR-3, tags and Releases.** The remote still has zero tags and zero Releases. `tag-rounds.sh` makes every missing tag and pushes them in one `git push` (`tag-rounds.sh:147`). GitHub documents that no push event is created when more than three tags are pushed at once, which several independent reports confirm ([community discussion 56152](https://github.com/orgs/community/discussions/56152), [actions/runner issue 3644](https://github.com/actions/runner/issues/3644)); this review could not reach the docs page itself or test it. So the maintainer's one command would make about seventy tags and no Release. Push the newest tag on its own, or let CI create the Release from the papers commit, as the retrospective's rule 16 said.

### Frame rate

Round 80 made the master rate follow the slowest display (`OutputRate.Master`, `OutputRate.cs:85`), on by default for every show.

- **RT-2.** Windows reports whole hertz. The follow keeps 59 under 60 in one family but not 29 under 30 or 23 under 24, so a 29.97 Hz TV drops a 30 fps show to 29, and a display reporting Win32's "hardware default" value 1 drops every output, NDI and a following stream to 1 fps. This repository's own CI runner has reported 1 Hz (REVIEW round 65).
- **RT-3.** A screen given its own rate still leads the master: the per-screen fix an operator used for a slow TV before round 80 now drags every other output to that TV's rate.
- **RT-1.** Under EDIT SAFE the NDI sender reads the rate from the frozen programme's copy of the rig (`NdiSender.cs:233`). Switch the follow off and the desk, STATE and the Super Check say 60 while NDI stays at 50 until the next whole-wall take.
- **RT-4.** On a 50/60 Hz rig every 60 Hz output is asked for 50, which a 60 Hz clock presents with ten frames a second held twice, a visible judder on pans and tickers. Before round 80 the 60 Hz outputs ran at 60 and the TV already ran at 50 on its own. The trade is unmeasured and unstated, and QUALIFICATION §5 now calls the degraded state a pass.

### The Eye's replay

Round 84 built it from web research; round 85 fixed its two worst problems (the fault counts and the UI-thread read). What remains:
- **EY-5.** The replay reads the metrics file's rotated half but not the journal's (`EyeService.cs:346-351`); after the journal rotates at 4 MB it reports "no rows" for a period the journal recorded.
- **EY-2.** Its light table does not know the journal's own alarm outcomes, "Alert" (SCREEN UNPLUGGED) and "MISMATCH": they paint grey and the desk stays green.
- **EY-3.** It resolves a row's target by string, so `ApplyLookHotkey 3`, a lower third or a track numbered 2 lights screen 3 or screen 2.
- **EY-6 and TF-8** (low) are in Appendix B.

### The wire recorder

**WR-2.** The recorder writes a readable line for each action by splicing the thing's name after the verb, and the parser reads sub-verbs and digits first. A look named "Update Walk-in" is recorded as `LOOK Update Walk-in` and replays as LOOK UPDATE on "Walk-in", overwriting that look; a look named "2" replays as the F2 hotkey; "Delete me", a preset "Save the date", a lower third "Take" and a stinger "Stop" do the same. The round-79 oracle round-trips the module's sample lines, which use "Walk-in", so it cannot see this.

## The process

### The field loop

| Round | Opened by | Field files read | Walk row filled |
|---|---|---|---|
| 79 | the retrospective | none | none |
| 80 | "continue" | none | none |
| 81 | the maintainer's words from the rig | none | none |
| 82 | an outside review | none | none |
| 83 | "continue" | none | none |
| 84 | web research | none | none |
| 85 | a handed-in plan | none | none |

`docs/field/` holds only its README, which says "No report has been committed here yet". Every qualification table (now 25 sections) has an empty row. Round 81 was the one contact with the rig in the period, and its papers record no request for the files and no build version, although round 79 had just made every build name itself. The diagnosis was written as established and the item closed; its own words say "on the rig it is unread". Round 85 says the plan's Phase 1 gate, "representative rig rehearsal confirms affected paths", is "the maintainer's next build".

The retrospective predicted that a blocking gate would fail because it relies on a behaviour the record never shows. The non-blocking version failed the same way. I read that as a cost problem, not a discipline problem: gathering four files by hand, keeping the secrets out of them and walking eleven steps costs more than typing "continue".

### Walk scripts that encode the code

The walks were written by the agent from the code, so they expect what the code does (PR-5).
- **§20** (round 78) still passes the rule the operator rejected in round 81 ("with the editors back on PGM the tile's PVW is the programme's preview again and the take lands it") and quotes refusal words the desk no longer says.
- **§22 step 5** presses one tile three times with the preview unchanged and expects the second and third refused. That passes under both the round-78 rule the operator rejected and the round-81 rule, so it cannot tell them apart.
- **§23** re-encodes the second-press refusal and, by reading, fails at MAIN SCREENS on the current build (SW-1).
- **§24 and §25** expect a restore no start path performs (PB-4).

A walk is only evidence if its expected readings come from the operator.

### The round-81 regression, reconstructed

Read from git at each commit:
1. **Round 78.1 (`e175928`, 09-18).** The tile's own TAKE refuses a press that changes nothing ("already shows this picture - nothing to take. Change the preview, or SEND a picture to this tile first."), and a settled OWN tile's PVW follows the programme's preview when the editors are on PROGRAM.
2. **The retrospective (09-20).** Entry 4 asks for a fence test, "Done implies Effect is not Nothing" over the take family, and says "self-reporting, not prevented".
3. **Round 79.1 (`9410292`, 09-21).** It adds the refusal to the wall's TAKE through the predicted `WouldChange`, and narrows the tile's exemption from stingers to all automation. QUALIFICATION §22 step 5 then expects repeated presses to be refused.
4. **Through round 80 (read, not run).** The wall refusal also met FOCUSED on the main key, where the scope would land the tile's own picture while the round-78 PVW showed the programme's preview.
5. **09-23.** The operator reports "Cut/Take on each individual screen tile has stopped working", and asks that editing the programme leave an OWN tile alone "unless Send is clicked".
6. **Round 81.3.** An OWN tile's PVW becomes its own.

PLAN §99.1 blames round 79 for the tile refusal and says its words named neither SEND nor the edit. Both are wrong: the refusal and its words are round 78's (PR-7). Round 79's contribution was the wall refusal, which also caught FOCUSED takes, the operator's third complaint. The chain matters because each step was locally reasonable and tested headless, and each moved the desk further from what the operator expected. SW-1 shows the next step of the same chain.

### A false history in the ledger

`docs/OPEN.md:74` says the repository's root is `b746a8d` of 2026-09-06, "257 commits at round 79", and that the retrospective's 92 earlier commits from `227f628` "are not in this remote's history". GitHub serves `227f628` ("Initial commit", 2026-08-29) in this repository; `b746a8d` names a parent (`50da6f9`); the full clone reaches 414 commits from the head. The figures are what a 50-deep clone of `main` (stuck at round 28) produces: `b746a8d` is exactly 50 commits back from `main`'s tip, and `b746a8d` to round 79.5 is exactly 257 commits. The false sentence also stands in CHANGELOG, `ChangelogTests`, `tag-rounds.sh`, README, PLAN §79 and §97, and REVIEW round 79. `CLAUDE.md` has no warning about shallow clones. One line would have caught it: `git rev-parse --is-shallow-repository`.

### Papers, comments and cadence

- **Papers.** Markdown was 26% of all inserted lines in rounds 79 to 85 and 41% in round 85, against about 11% over rounds 16 to 78. PLAN grew from 10,386 to 11,692 lines.
- **Commits.** Subjects are shorter from round 79.8 on, with prose in the bodies, as advised; every commit carries the `Round NN.k:` prefix and one identity.
- **Comments.** The brief's rule against round numbers in code comments was broken 114 times in rounds 79 to 84, after it was written.
- **What the brief says.** `CLAUDE.md` says `DateTime.Now` is banned in `src/` (57 lines use it; `BannedSymbols.txt` has no entry) and that the module's CI job runs on Windows (it runs on Ubuntu).

## Where the retrospective was wrong or incomplete

- **It put the leak into the field instructions.** Rule 1 asked for the four files, the recovery record among them, to be committed under `docs/field/round-NN/`. It never said the record holds the whole show state with its secrets, or that the repository is public. PR-2 is the result.
- **Its fence invited a refusal.** Entry 4 asked for a fence "Done implies Effect is not Nothing" and said "self-reporting, not prevented", but it did not say plainly: never refuse on a prediction. If a refusal exists, compute it from the same measure the executor uses and publish it before the press. Round 79.1 turned the fence into a predicted refusal, which is the root of TF-1, TF-2 and TF-6 and one link in the round-81 chain.
- **Its stamp was too coarse.** Entry 4(a) specified Visibility "from `_s.OutputsLive`", which is TF-3's blind spot. It should have said per target: a window open, not black on its own, not frozen, NDI counted.
- **It missed a class of attack.** Entry 13 covered tokens, binds, listeners and lights, and never considered a browser used as a proxy: cross-origin requests, DNS rebinding, `Host` validation, loopback trust. The round-82 outside review found the first half; SEC-2 is the second.
- **It did not check the tag script against GitHub.** It recommended that CI make the tag and the Release, which still stands, but it cited `tag-rounds.sh` without noticing that the script's one multi-tag push starts no workflow. The papers then kept the script as the plan, and nothing warned them.
- **It did not say who writes a walk's expected readings.** The walks that came from it encode the code's behaviour (PR-5).
- **Its evidence advice lacked a mechanism.** Rules 1 and 2 were adopted as text and not practised. Advice that costs the maintainer effort on the rig needs to be cheaper to follow than to skip.
- **Where it was right and overruled.** Its history claim was correct; the ledger rejected it on the basis of a shallow clone (PR-1).

## What I would do next

In order, with rough sizes. Only the first item is urgent; the rest are ranked by what a show would meet first.

1. **Before any field report is committed** (under an hour): rewrite `docs/field/README.md` and QUALIFICATION §22 to commit the support bundle's redacted files only. Add a test that the raw recovery record never lands in `docs/field/`, and consider redacting the record's `Air` at write time.
2. **Close the rebinding hole** (a few hours with tests): accept only a `Host` that names the desk (its addresses, its mDNS name, `localhost`); stop trusting loopback when a token is set, or trust it only when `Host` is a loopback literal. Gate `EYE AT` with RECORD.
3. **Walk the switcher with the operator before changing it again** (one session on the rig, then a round): decide with the operator what a second group take does, whether the highlight is always the edited tile, and whether LOCK holds against SEND TO TICKED and per-screen verbs. Then fix SW-1, SW-2, TF-4 and SW-3 (run SEND TO TICKED through the executor), and either derive the wall refusal from the executor's own measure or remove it (TF-1, TF-2, TF-6). Make Visibility per target (TF-3). Write the walk's expected readings from the operator's answers.
4. **Decide what a power cut does** (a decision, then an hour or two): offer or perform the restore on a cold start with a fresh live record, or say in §24 and §25 that it comes back dark. Skip the exit's clear after a hand-over (PB-3). Put the newer-schema words on the desk and in STATE, and hold the rule in the node, Load show and SAVE AS paths (PB-1, PB-2).
5. **Make one Release** (an hour): push the newest tag on its own or let CI create the Release; add `createdump.exe` to the full bundle and the Release (PB-6); correct L25's closing path and README's tag sentence.
6. **Fix the follow** (a few hours): read 23, 29, 47, 59 and 119 as fractional families and ignore anything under about 20 Hz (RT-2); skip screens with their own rate (RT-3); carry the rate in force on the snapshot for NDI (RT-1). Default the follow off until §5 is walked, or write the judder trade into the papers (RT-4).
7. **Replay and recorder** (a few hours): read the rotated journal, learn the alarm outcomes, resolve targets by verb, write a take's screens as a field (EY-5, EY-2, EY-3, TF-8); quote or refuse names that parse as sub-verbs (WR-2).
8. **Papers** (an hour): correct the false history in the seven files that carry it and add a shallow-clone check to `CLAUDE.md` (PR-1); retire §20 and give §22 step 5 a press that must land (PR-5); correct §99.1 (PR-7); migrate stored `GROUPS` to `CANVASES` and bump the descriptor (SW-4); commit handed-in critiques into `docs/` as round 72's was.
9. **Process.** A one-press field pack on the desk that writes the four files, redacted, with the build line; and a round that opens by naming the last walk row it read, or saying there is none, before any feature work. The gate is the maintainer's; the cheaper the evidence, the less the gate has to do.

## What held up

The reviewers checked the round-79 to round-85 work that matters for correctness and found much of it sound:
- **The result stamping.** Every result that reaches the executor is stamped once, for every origin; automation is never refused by the no-op rules; older journal rows still load.
- **The snapshot pair.** Programme and sandbox are published as one reference and read once per frame. `SectionsPublished` fires after the assignment, and the `WirePeer` fixes are right (L13 to L15).
- **`AtomicFile`.** Its ordering is correct for a process crash on any file system and for power loss on NTFS, and declining `File.Replace` was right.
- **The wire.** The fail-closed wire words, the recorder's nine arms and the module-lines oracle hold within their scope. The fuzz fence catches throws.
- **Scoped takes.** Each is one publish; locked, un-armed and repeater targets are held and named; a scoped CUT cuts; the programme's air and preview stay put on every scope.
- **Round 85's units.** Each is small, has its reason written and has a test: the worker read with its generation check, the bind parser, the recovery ladder's predicate, the promotion guard and the pages' token.
- **The ledger's bookkeeping.** Rows are contiguous, every closed row names a real commit, and the running counts add up.
- **The build and CI.** CI stamps the right identity and gates the Release on the Windows lane. Every suite passes at both heads.

## Method and coverage

- **Clone and range.** A full clone of `jammin808/Patterns`: 414 commits from `2746612`, `git rev-parse --is-shallow-repository` false. The range is `08dcaaa..2746612`, 50 commits.
- **Reviewers.** Eight read-only reviewers took one area each at `f4f9d3c` (round 84): truth as fields; the round-81 switcher; the wire, OSC and faults; security and inputs; rendering and timing; persistence, build and release; the Eye's replay; the process and papers. They produced 76 findings: 3 rated high at the time, 29 medium and 44 low. Round 85 landed during the review, and each finding was re-read against it. Round 85 fixed three outright: the phone page, the bind fall-back (found by two reviewers) and the replay's counts. It also fixed parts of two more: the replay's read cost, and one of the four newer-schema paths.
- **Build and suites.** .NET SDK 10.0.112 on Linux. The build was clean at both heads, with no warnings. All seven suites passed:

  | Suite | at `f4f9d3c` | at `2746612` |
  |---|---|---|
  | Core | 1,030 | 1,046 |
  | Rendering | 670 | 670 |
  | Devices | 8 | 8 |
  | Audio | 13 | 13 |
  | Assistant | 37 | 37 |
  | Audience | 2 | 2 |
  | App | 815 | 816 |
  | Total | 2,575 | 2,592 |

  The Companion module's own tests passed 34 of 34 at `f4f9d3c`; round 85 did not change the module.
- **Reproducers.** 42 test cases in the suites' own idiom, in a separate worktree, never in the repository's test projects. All 42 pass at `2746612`. Three more tests (six cases) confirmed defects at `f4f9d3c` that round 85 fixed, and were retired. The sources are in `docs/rereview-repro/` on this branch; Appendix A maps them to findings.
- **GitHub.** Read through the API: visibility public; zero tags; zero Releases; CI runs 267 and 268 green; `227f628` served as the initial commit. GitHub's documentation site was blocked from this environment; the three-tag limit is cited from independent reports.
- **Not done.** Nothing ran on Windows or on the rig. Findings marked Read or Reported were not run. The two-machine paths (L21) and the time a replay read takes on a show laptop are unmeasured. The low-severity findings in Appendix B are listed as the reviewers reported them unless marked otherwise.

## Appendix A: the reproducer tests

Each test asserts the defective behaviour, so a pass means the defect is present. To use one as a regression test, invert its marked assertions.

| Test | Finding | At `2746612` |
|---|---|---|
| `RereviewTakeRepro.SW1_ASecondGroupTakeOfANewPictureIsRefusedAndAllArmedThenSaysTheAirIsThePreview` | SW-1 | pass |
| `RereviewTakeRepro.TF4_SendToTickedChangesALockedTileOnAirAndWritesNoJournalRow` | TF-4 | pass |
| `RereviewTakeRepro.SW3b_APerScreenPatternStepFromACueChangesALockedScreenOnAir` | SW-3 | pass |
| `RereviewTakeRepro.TF1a_AWallTakeCarryingOnlyANewBrandKitIsRefusedForAHand` | TF-1 | pass |
| `RereviewTakeRepro.TF1b_AnAutomationTakeLandsTheNewBrandKitAndMeasuresNothing` | TF-1 | pass |
| `RereviewTakeRepro.TF1c_AWallTakeCarryingOnlyALowerThirdInThePreviewIsRefusedForAHand` | TF-1 | pass |
| `RereviewTakeRepro.TF2_AWallTakeWhoseOnlyEffectIsOwnershipIsRefusedWithAdviceToSend` | TF-2 | pass |
| `RereviewTakeRepro.SW2_AfterATilesTakeTheHighlightIsTheTileButTheEditorsAndThePaneAreNot` | SW-2 | pass |
| `RereviewTakeRepro.TF3a_ATakeUnderFreezeIsStampedOutputsLive` | TF-3 | pass |
| `RereviewTakeRepro.TF3b_ATakeOnAScreenFadedToBlackOnItsOwnIsStampedOutputsLive` | TF-3 | pass |
| `RereviewTakeRepro.TF3c_ATakeOnAScreenWithNoOutputWindowIsStampedOutputsLive` | TF-3 | pass |
| `RereviewTakeRepro.TF6_StatePublishesNoRefusalForAWallTakeThatWillBeRefused` | TF-6 | pass |
| `RereviewTakeRepro.TF7_FollowTheProgrammeOnAnOwnTileIsNotPendingAndIsSaidToKeepItsPictureWhileTakeMovesIt` | TF-7 | pass |
| `RereviewSecurityRepro.ARequestShapedLikeARebindingPagePassesTheCrossOriginGate` | SEC-2 | pass |
| `RereviewEyeRepro.EY1_AnUnpairedWireClientReadsTheOperatorsJournalRowsThroughEyeAtWhileRecordIsGated` | EY-1 | pass |
| `RereviewEyeRepro.EY5_AfterTheJournalRotatesTheReplaySaysNoRowsForAPeriodTheJournalRecorded` | EY-5 | pass |
| `RereviewEyeRepro.EY6_AnOpenReplayAnswersALaterInstantFromTheFirstPressesSnapshot` | EY-6 | pass |
| `RereviewEyeRepro.RT1_UnderEditSafeTheNdiLaneKeepsTheFrozenProgrammesRateAfterTheFollowIsSwitchedOff` | RT-1 | pass |
| `RereviewPersistenceRepro.PR2_TheRawRecoveryRecordCarriesThePairingTokenInClearAndOnlyTheBundleMasksIt` | PR-2 | pass |
| `RereviewPersistenceRepro.PB1_ANewerShowFileRunsUnsavedAndNoDeskSurfaceSaysSoAtBoot` | PB-1 | pass |
| `RereviewPersistenceRepro.PB3_ADeskThatStoodDownClearsTheIncomingDesksRecoveryRecordAtItsExit` | PB-3 | pass |
| `RereviewPersistenceRepro.PB4_APlainStartWithAFreshLiveRecordLeavesTheOutputsClosed` | PB-4 | pass |
| `RereviewCoreRepro.EY2_TheReplayPaintsMismatchAndAlertGreyAndLeavesTheDeskGreenAfterAnUnplug` | EY-2 | pass |
| `RereviewCoreRepro.EY3_ANumberedLookLowerThirdOrTrackLightsTheScreenWithThatNumber` (3 cases) | EY-3 | pass |
| `RereviewCoreRepro.EY1_EyeAtIsAQueryAndEyeReplayIsNot` | EY-1 | pass |
| `RereviewCoreRepro.WR2_ANamedThingIsRecordedAsALineThatReplaysAsAnotherAction` (6 cases) | WR-2 | pass |
| `RereviewRateRepro.RT2a_AFractionalModeReportedAsAnIntegerDragsTheWholeShowDown` (2 cases) | RT-2 | pass |
| `RereviewRateRepro.RT2b_TheHardwareDefaultValueOneIsFollowedToOneFps` | RT-2 | pass |
| `RereviewRateRepro.RT3_AScreenOnItsOwnRateStillLeadsTheMasterRate` | RT-3 | pass |
| `RereviewRateRepro.TF8_AFocusedTakeRowLightsNoScreenAndAnUnseenTakeReadsGreen` | TF-8 | pass |
| `RereviewPersistenceCoreRepro.PB5_ARecordWhoseReplacingMoveWasLostReadsAsNoCrash` | PB-5 | pass |
| `RereviewPersistenceCoreRepro.PB9_AFailedMoveLeavesTheTempFileWithTheSecretBehind` | PB-9 | pass |
| `RereviewPersistenceCoreRepro.PB10_TheRecoveryRecordsTwinKeyIsNotMaskedByPlaceWhileItsTokenIsMaskedByName` | PB-10 | pass |
| `RereviewPacerRepro.RT4_FiftyOnASixtyHertzClockHoldsAFrameForTwoRefreshesTenTimesASecond` | RT-4 | pass |

Retired because round 85 fixed the defect: the phone page's bare poll (SEC-1, 85.6), the replay's rise rule over rolling counts (EY-4, 85.1), and the bind fall-back (WR-1, 85.5).

## Appendix B: lower-severity findings

Confirmed by a reproducer:
- **TF-6.** No reader knows the wall refusal before the press: STATE's `take.refusal`, the plan's words, the deck and the Eye say the take will land.
- **TF-7.** "Follow the programme" on an OWN tile does not light PVW, and the plan says the tile keeps its picture while ALL ARMED moves it.
- **TF-8.** A scoped take's journal row names the scope word ("FOCUSED"), so the replay lights no screen, and an unseen take (OutputsOff) replays green.
- **EY-6.** An open replay never re-reads; a later instant is answered from the first press's snapshot.
- **PB-5.** A recovery record whose replacing move was lost reads as "no crash", though the `.bak` and the flushed `.tmp` hold whole records.
- **PB-9.** A failed move leaves the temp file behind; for the Spotify and assistant stores that is a copy of the credential, which DISCONNECT and FORGET do not remove.
- **PB-10.** The support bundle masks the twin key only at the top level; the recovery record's `Air.Twin.Key` is left in clear once the key has been rotated.

Read in the code at the head:
- **TF-5 and PR-7.** PLAN §99.1 misdates the tile refusal (round 78, not 79) and misquotes its words.
- **TF-11 and PR-9.** `CLAUDE.md` claims `DateTime.Now` is banned and that the module's CI job runs on Windows; neither is true. `patterns.log` is stamped in local time beside the UTC journal.
- **PR-10.** Ledger integrity: L22's and L28's "Where" name files that do not exist; L44, a feature idea, is weighted above L36 and L40; L21 still counts 21 matrices (there are 25).
- **PR-6.** The field loop is adopted in words only; see "The process".

Reported by the reviewers, not re-checked:
- **Switcher.**
  - SW-5: a preset chip always recalls into the programme's preview, whatever tile is selected.
  - SW-6: the plan's words and the result's words disagree with the take for OWN targets.
  - SW-7: feed screens show NDI or STREAM as their group but take as their role.
  - SW-8: stale help, tooltips and menu words after round 81; the refusals tell wire clients to SEND, which the wire lacks.
  - SW-9: every editing-target change republishes both states.
  - SW-10: no round-81 test drives the pointer.
- **Truth as fields.**
  - TF-9: a caller node answers OK for a take the desk then refuses, and nothing settles it.
  - TF-10: the round-79 fence is one ten-press scenario, not a hook over every take in every test.
- **Frame rate.**
  - RT-5: the stream renderer and the remote pictures read the programme and the sandbox separately.
  - RT-6: the clock-limit cause can blame the wrong thing.
  - RT-7: 59 under 60 stays amber with circular advice.
  - RT-8: the monitor rate ignores the render clock.
  - RT-9: the follow has no hold, and a following stream restarts on hot-plug (now L45).
  - RT-10: the papers claim a measurement that was not made.
  - RT-11: display enumeration on the tick, against ADR-014.
- **Wire.**
  - WR-3: the fault throttle sits where few faults arrive.
  - WR-4: an IOException from a route's own file work is read as a socket's end.
  - WR-5: the week ceiling meets consumers that hold a time of day.
  - WR-6: a few fail-open edges remain (two switch verbs, NaN on OSC).
  - WR-7: the module's newer-descriptor warning is lost at the first reconnect.
  - WR-8: the fuzz fence covers about a dozen parsers and asserts only that nothing throws.
- **Security.** SEC-3: the ticker moderates with the default word list, not the room's.
- **Build and release.**
  - PB-7: the build is not one string on every surface.
  - PB-8: the same finding as PR-3.
- **Replay.**
  - EY-7: times without dates over a record spanning days.
  - EY-8: during a replay, the menu and ASK mix then and now.
  - EY-9: the papers cite a precedent that does not exist.
  - EY-10: the synthetic fence asserts neither Visibility nor Effect.
  - EY-11: round 84 built operator surface from web research without a rig.
- **Process.**
  - PR-8: 114 round mentions added to comments after the brief banned them.
  - PR-11: PLAN §97.9's answer to the retrospective is incomplete in places.
  - PR-12: units were committed in batches, not one validated unit per commit.

## Appendix C: ledger verdicts from this review

| Row | Verdict |
|---|---|
| L01, L06, L07, L08, L11, L13, L17, L18, L19, L20, L23, L27, L34 | closed, with the caveats above (L23: TF-7; L34: PB-3, PB-4, PB-5) |
| L02, L03, L04, L05, L12, L14, L15, L16, L24, L39, L41 | verified closed (L03's bind fall-back was fixed by 85.5; L16 was closed by `0c6f048`, not `4f12381` as the row says) |
| L31, L32, L37 | verified closed with caveats: L31 WR-3 and WR-4; L32 SEC-2, with SEC-1 now fixed; L37 SEC-3 |
| L09, L10, L21, L22, L25, L26, L28, L29, L33, L35, L38, L40, L42, L43, L44, L45, L46 | open and accurate; L36 should be medium; L30 is inaccurate and misses GROUPS (SW-4) |
| "Checked and left" | false (PR-1) |
