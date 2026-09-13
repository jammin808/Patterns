# The redundancy drill

*A release gate, not a good test. Nothing below is claimed until it has been done in a real room
with two real machines, real displays, the real switcher or matrix the redundancy depends on,
actual network links, the actual authority files and actual output windows — and filmed. The
acceptance criterion for every scenario is what the audience saw, never what the log said.*

## The rig

- Two machines with Patterns on each: MAIN and STANDBY, the same build, the same show file, the
  same twin key (Machine page, TWIN). STANDBY as a standby of MAIN with "take over by itself"
  ticked; a wall-switch cue on each side that routes the room's wall through the real switcher.
- The switcher's device on the Interactive page with its Confirm level set as high as it can
  answer (Accepted for HTTP or PJLink; Observed with a query and an expected answer where the
  box can be asked what it did).
- A caller node and a stage timer node linked to MAIN; a phone on the stage page.
- Two cameras: one on the wall, one on the operator screens (MAIN and STANDBY side by side), the
  clocks in shot. The log folders of both machines kept after the drill.
- The show at a known picture on air before every scenario, so a change is unmistakable.

## The record

One row per scenario, filled in during the drill:

```
scenario
failure injected (how, by whom, at what clock time)
expected behaviour
actual audience picture (from the wall film, with the times)
operator state (both screens, the status lines, the health lines)
recovery path (what was pressed, what happened by itself)
time to a stable picture
unexpected side effects
```

## The scenarios

Each names the failure, what the room should see, and the lines that should be in the log.

1. **Kill MAIN's process** (Task Manager, End task). Expected: the wall keeps MAIN's last frames
   while STANDBY hears the silence (five seconds), fires the wall switch, waits for the
   switcher's answer, and opens its outputs; the room sees STANDBY's picture within seconds and
   never two pictures. Log: `Twin: the main … has been silent`, `Wall switch: cue … fired`,
   `Wall switch confirmed: …`, `TOOK OVER from … — take over across machines: route requested →
   route confirmed → authority committed → target ready → complete`.
2. **Freeze MAIN's UI thread** (a debugger break, or a deliberate hang). Expected: as 1 — and
   MAIN's own output windows keep playing until the switcher moves; the ownership record on
   MAIN's folder stops beating.
3. **Pull the twin network cable** (between the machines, the switcher still reachable from
   STANDBY). Expected: STANDBY takes over by itself only because the switcher is its fence; the
   room follows the switcher. If the switcher is behind the same cable, STANDBY must refuse
   ("was not confirmed"), keep its outputs held, and try again after the pause.
4. **Restore the network during a takeover.** Expected: no second takeover, no double picture;
   MAIN, still running, holds its outputs when it reads STANDBY's join that says it has the
   show; the line says `HAS THE SHOW`.
5. **Fail the wall-switch HTTP** (unplug the switcher's control port). Expected: an automatic
   takeover is refused with `was not confirmed (… no answer in … s)`; the hold stays; a pressed
   TAKE OVER goes ahead and says the switch was not confirmed, so the wall is switched by hand.
6. **Return HTTP 500** (a mock in front of the switcher, or the switcher's own error). Expected:
   as 5, with `rejected: 500 …` in the words.
7. **Return HTTP 200 without changing the route.** Expected: with Confirm at Accepted, the
   takeover proceeds and the wall does not move — the film shows it; with Confirm at Observed
   and a route query, the takeover is refused (`accepted, not observed`). This is the case that
   decides what level the switcher must be set to.
8. **Make the authority folder read-only** on MAIN, then restart MAIN. Expected: MAIN's line says
   the ownership record could not be written; a restart reads `The record of who has the screens
   could not be read` if the file is locked, or the stale record if only writes fail; nothing
   opens by itself; OUTPUTS ON opens by hand.
9. **Lock the ownership sidecar** (`patterns.outputs.json` held open by another program) and
   restart MAIN. Expected: `OutputClaim.Unknown` — nothing asked, ended or opened; the words say
   OUTPUTS ON.
10. **Corrupt the ownership sidecar** (write junk into it) and restart. Expected: as 9.
11. **Restart MAIN during a takeover** (the watchdog, or by hand, while STANDBY is between the
    wall switch and its outputs). Expected: MAIN comes back, reads STANDBY's marker or its join,
    holds its outputs; one picture throughout.
12. **Kill MAIN between the marker and the process termination** (a same-machine standby: end
    MAIN the instant STANDBY's marker appears). Expected: STANDBY's takeover completes; MAIN's
    restart reads the marker and holds.
13. **TAKE BACK with the switch NAKed** (the switcher answers an error). Expected: `TAKE BACK
    stopped at the wall switch … was not confirmed`; STANDBY's picture stays up and the room stays
    on it; MAIN's picture is up on its own displays; the line says TAKE BACK again finishes it.
14. **TAKE BACK with the route unchanged** (the switcher accepts and does nothing). Expected: at
    Accepted the hand-back completes and the room is dark or stale — the film shows it, and that
    is the finding; at Observed with a query it stops. Record which level the switcher earns.
15. **Unplug an output during failover.** Expected: the lost display's window is marked, the
    others carry on, the takeover completes on the rest.
16. **Reconnect it after failover.** Expected: the returned display gets its window back on the
    desk that owns the show now.
17. **ACK a stage message during the same run.** Expected: the receipt reaches whichever desk is
    the main at that moment; the timer node's clock never runs on its own while linked.
18. **Run the caller and the timer through the transition.** Expected: the caller's GO lands on
    the desk that has the show; the timer follows the same desk; both say which.
19. **Lose the hand-back** (TAKE BACK on MAIN with the twin link cut the instant HANDBACK is
    written — a firewall rule, or the cable). Expected: MAIN says the standby was told to let go
    and its answer is awaited, then, past five seconds, that it did not say it let go; on one
    machine MAIN's outputs stay held. When the link is back STANDBY dials in claiming the show
    under the takeover MAIN took back: MAIN's log says it was told the hand-back again, MAIN's
    outputs are never closed for the claim, STANDBY closes its outputs and answers RELEASED, and
    MAIN's trail ends `old owner released → complete`.
20. **Kill STANDBY between HANDBACK and its answer** (a same-machine standby). Expected: MAIN's
    trail says `its process is gone`, the marker is cleared, and MAIN's picture goes up; the wall
    is dark for the instant and no longer.
21. **TAKE BACK with no take-back cue** (across machines). Expected: MAIN's picture goes up on
    the inputs the room is not watching, the line says to switch the room by hand and press again,
    STANDBY stays up and the holder; after the switch, the second press releases it. Nothing
    releases STANDBY between the presses.
22. **Set the switcher's Confirm to Delivered and TAKE BACK.** Expected: the switch is asked, the
    receipt says delivered, MAIN says `no box said yes — delivered is not switched` and stops with
    STANDBY up; at Accepted the same press releases it. A takeover by itself on that cue is
    refused before the cue fires, with the reason on the line.

## What decides

- No scenario may show two pictures on the wall.
- No scenario may leave the wall dark for longer than the switcher's own switching time plus the
  first frame, except 7 and 14, whose finding is the Confirm level the switcher needs.
- Every refusal must be on the health line of the desk that refused, with the reason.
- The log of each machine must carry the handover trail for every takeover and take-back, with
  its id, and every RELEASED must name the hand-back it answers.
- No scenario may count a standby released on a line written: only on its answer, its marker
  gone, its process gone, or its fresh join that claims nothing.

Until this has been done, the redundancy claims in `docs/PLAN.md` §48–§63 are claims about the
simulated room the tests build, not about a real one.
