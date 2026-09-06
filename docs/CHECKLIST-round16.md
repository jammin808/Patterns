# Round 16 — Windows checklist

The headless suite proves the logic on every push. These are the things only a real rig shows:
one line per item, what to do, and what you should see. Tick them on a Windows machine with the
full build (libVLC bundled), two displays and an audio interface; row 1 also wants a Stream Deck
with Companion. The round is a refactor of the action path — one vocabulary, one executor — so
the rows are about the same verbs reaching the screens from every source, and about an older
show file loading as it did.

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 1 | One action vocabulary | PLAN → Cues: + CUE, add actions *Clock — 12 or 24 hours* 24, *Clock — seconds on / off* on, *Logo on*, *Picture-in-picture on*, *Countdown to a time of day* 14:00, *Countdown label* Back at, *Pattern — change its kind* Grid, then a second cue with *Overlays off (a clean picture)*; ARM and GO each; read every cue's summary line; add a third cue and look for TAKE, CUT, GO or Restart in the picker; IMPORT a CSV whose Action column reads `logo`, `back at`, `clean picture` and `Take`; open ADMIN → Assistant and ask for a cue that puts the logo up; on the Show panel FADE TO BLACK 1.5, from Companion FADE 1.5, and a cue *Fade to black* 1.5; load a show saved by the previous build with fade cues | The first cue's GO puts the clock (24-hour, seconds), the logo, the PiP, a countdown to 14:00 labelled *Back at* and a Grid on every screen at once; the second clears every overlay; the summaries read *Clock 24-hour + Clock seconds on + Logo on …* and *Overlays off*, never a bare kind name; the picker has no TAKE, CUT, GO or Restart; the import makes Logo on, Countdown to a time of day and Overlays off cues and notes that `Take` is not a cue action; the assistant's proposal carries *Logo on [LogoOn]*; the three fades take the same one and a half seconds; the older show's fade cues fade for the seconds they always said |
