# The field: the machine's files, per report

A rig report comes as files, not words. Every report from the show laptop lands here as
`docs/field/round-NN/` — NN the round that reads it — and the next round opens with "what the files
said" (the shape of `docs/PLAN.md` §96.1: each row or line, what it means, what it asks) before any
feature ask. The first time the desk's own files arrived (round 71) they found a fault every review
since the first week had missed; the first full set (round 77) produced eleven fixes in two days.

## The four files

All beside `Patterns.exe`:

| File | What it is | Why it is read first |
|---|---|---|
| `patterns.supercheck.txt` | The last Super Check: every row with its light, value and note | The desk's own verdict on the machine, the displays, the audio, the wire, the render clock |
| `patterns.log` | The log (`patterns.log.old` is the one before) | The build's version banner, the start-up lines, every warning with its minute |
| `patterns.showlog.jsonl` | The journal: one row per action with who caused it, its status, and from round 79 its visibility and effect | Whether what was pressed reached the room, and what the room saw |
| `patterns.recovery.json` | The recovery record: what a restart puts back on the screens | What the desk believed was on air |

Welcome beside them: `patterns.metrics.csv` (a row a minute), `patterns.playhead.json`,
`patterns.quality.json`, `patterns.watchdog.log`, and the manifest of the build. The simplest way to
gather them all is the **SUPPORT BUNDLE** button (PLAN → Install): it writes
`patterns-support-<date>.zip` beside the settings with the logs, the journal, the settings with
every secret blanked, the last Super Check, the metrics and the sidecars — and it never carries
the Spotify credentials or an update's manifest. Do not commit `patterns.settings.json` raw: it
holds the pairing token.

## The report

`docs/field/round-NN/REPORT.md`, a few lines:

```
Date:      2026-09-21
Machine:   (the Machine page's first line — CPU, GPU, RAM, the displays and their refresh)
Build:     (the Admin page's version, or STATE's `version`: 0.79.312+round-79.4f12381)
Walk:      (the QUALIFICATION §22 rows ticked, or "no walk")
Seen:      (what happened, in the operator's words, with the clock time)
Files:     patterns.supercheck.txt, patterns.log, patterns.showlog.jsonl, patterns.recovery.json
```

The round that reads it writes "what the files said" at the top of its PLAN section and, for each
fault, the row of `docs/OPEN.md` it opened or closed. A step the operator could not complete becomes
a headless test driven through the input pipeline, not a footnote.

## Status

No report has been committed here yet; the files that shaped rounds 71 to 78 were read from the
maintainer's desk and not kept in the tree. From round 79 on they are kept.
