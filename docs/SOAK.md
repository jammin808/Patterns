# The soak

*The day before the drill. `docs/DRILL.md` films a night of failures; this runs a day of ordinary
load first, with a few faults at the hours, and reads the numbers. Nothing below is a claim until
the record has been filled in on the real rig. Seven steps, four hours, one page.*

## The rig

The drill's rig, less the cameras: MAIN and STANDBY (the same build, show file and twin key,
STANDBY following with "take over by itself" off — the soak is not the drill), a caller node and a
stage timer node linked to MAIN, the arcade node as the hub with its audience port on the audience
Wi-Fi, Companion on MAIN's wire with feedback on, twenty real phones on the audience Wi-Fi (or the
load test's two hundred from one laptop, with the venue NAT profile on). The metrics CSV on
(Machine page, STABILITY). The clocks measured on the link: `TWIN STATUS` on MAIN reads each
peer's `clocks` entry, and the health line says CLOCKS APART past two seconds — set both machines
to one time server before the soak starts, and note the offsets in the record.

## The record

One row per step, filled in as it runs:

```
step
clock time started / ended
p95 frame ms (worst minute) · dropped frames (total) · desk tick worst ms
memory MB at start / end (the Machine page's MEMORY tile, the CSV's column)
WARN lines in patterns.log, by kind
what was refused, and the line that said so
unexpected
```

## The seven steps

1. **Build the room (0:00).** OUTPUTS ON on MAIN and a look the room recognises; the cue stack
   armed with a loop of ten cues on follows of thirty seconds — a media cue, a look, a lower
   third, a stinger and an overlay among them; the countdown following the plan; the phones joined
   (`PLAY SHOW join`); Companion connected. Record the glance line under the LIVE strip and the
   CSV's first row: this is the baseline.
2. **Run (0:00–4:00).** Leave it. Every thirty minutes read the glance line on MAIN and on the
   caller — the outputs' frame rate, p95 and drops, the twin's word, the screens the room is
   short, the last box that said no, the plan, the lock — and note anything that moved.
3. **Cut the twin link (1:00, for sixty seconds).** STANDBY's cable, or a firewall rule. Expected:
   MAIN's line names the standby's silence and STANDBY's the main's; nothing takes over (the
   switch is off); within five seconds of the cable the link is back and the mirror complete; the
   caller and the timer unmoved. Log: `Twin: … silent`, then the join.
4. **APPLY while armed (2:00).** On the caller, change three cues' notes and one planned start and
   OFFER PLAN; on MAIN press APPLY with the stack armed. Expected: the offer waits on the Nodes
   page (WAITS), the running order untouched; DISARM lands it and the countdown re-aims. A desk
   edit meanwhile sets the waiting offer aside and says so.
5. **Flood the doors (3:00, for five minutes).** From a laptop on the show LAN, a hundred TCP
   connections to MAIN's Companion port, fifty of them holding half a line; from the audience
   Wi-Fi, three hundred joins a minute from one address. Expected: `ERR busy` after the sixteenth
   connection from the laptop and the door closed on the rest, the half-lines cut at ten seconds,
   one WARN line a minute per door in the log; Companion's own connection answering throughout;
   the phones already seated unmoved; the audience refusals named on the room's line with the
   profile as the fix — and, with the venue NAT profile on, the room seated instead. The glance
   line's p95 does not move.

   ```powershell
   # the wire: a hundred connections, fifty of them holding half a line
   $cs = 1..100 | % { $c = [Net.Sockets.TcpClient]::new('MAIN', 9697); if ($_ -le 50) { $s = $c.GetStream(); $s.Write([Text.Encoding]::ASCII.GetBytes('PIN'), 0, 3) }; $c }
   Start-Sleep 300; $cs | % { $_.Dispose() }
   # the audience port: three hundred joins a minute from this address
   1..300 | % { Invoke-RestMethod -Method Post -Uri 'http://HUB:9701/api/play/join' -Body ('{"nick":"soak ' + $_ + '"}') | Out-Null; Start-Sleep -Milliseconds 200 }
   ```

6. **Stop the flood and read (3:05–4:00).** The counts back to their baseline within a minute
   (`AUDIENCE STATUS`: `connections` and `joinsRefused` to zero; the wire's open count in the
   log's next line). At 4:00: the CSV's worst minute for p95 and drops, memory at 0:00 against
   4:00, the WARN lines by kind, the journal's count of GOs against the loop's arithmetic (480 in
   four hours at thirty seconds).
7. **Decide.** Pass when: p95 under 25 ms in every minute after the first; drops zero after the
   first minute; the desk tick under 16 ms; memory within ten per cent of the start; no restart by
   the watchdog; nothing refused on the health line but what step 5 injected; every GO in the
   journal. Anything else is a row with the hour and the line, and a fix before the drill.

## What decides

- The room saw one picture throughout, and the loop never missed a GO.
- Memory is flat, not merely small: a leak is a slope, and four hours is enough to see one.
- Every refusal has a line that names it, and no refusal was silent.
- The flood cost the room nothing it could see.

Until this has been run, the numbers in `docs/PLAN.md` §65 and §66 are the tests' numbers, not
the room's.

## The hardware qualification (round 58)

The critique's last items (P1.25–P1.28) need a rig, not a test host. Each is a row in the record
above; the numbers named here are read from the Machine page, `patterns.metrics.csv` and STATE.

1. **Companion 5 imports the module.** Companion 5.0.5 or later on a machine on the desk's
   network: unzip the CI artifact, remove any `jammin808-patterns` already installed, import the
   tgz (`jammin808-patterns-3.2.0.tgz`), connect to the desk and read the Remote page's decks
   line — *FOH deck (module 3.2.0, …)*. Then every new word: `machine_memory_pressure` reads
   *none* on an idle desk; `inputs_pending` fills when a capture device on air has its Low
   latency box ticked and empties when the outputs go off; the two feedbacks follow; `web_vt`
   ends with *PLAYING (observed)* after a take.
2. **IMAG on a capture card, the profile off and on.** A camera into a capture card (a Cam Link
   4K or an HDMI card) on a screen the room sees beside the speaker, a clap at the lens. Record
   the glance line's *live* number and SYNC CHECK's whole-chain figure with Low latency off, then
   on. Expect the whole chain to fall by about the decoder's 80 ms confidence buffer while the
   *live* number — the app's share, decoder to frame — barely moves; the difference between the
   two is the card's and the screen's. Note the frames dropped on a hitch with the profile on,
   and that ticking the box while the camera is on air stages the reopen rather than blanking the
   room. The super-check's *Live input* row stays green (to 40 ms) or amber (to 80).
3. **The thirty-minute and sixty-minute soaks** with a capture card on air and a clip in the
   preview, looks recalled every minute: the CSV's `retiringMB` returns to its floor between
   edits; `poolStarved` stays 0; STATE's `forcedFrees` reads 0 throughout (a number there is a
   finding — a sink the fence never saw advance); `fenceOldestMs` stays under a second; the
   pressure rung never leaves *none* on a Standard machine with one source.
4. **The four-hour soak** (the seven steps above, unchanged) with the new columns read at the
   end: `retiringMB` flat, `poolStarved` 0, `forcedFrees` 0, the memory line's *media* figure
   flat, and every ladder transition, if any, in `patterns.log` with the reading that caused it.
