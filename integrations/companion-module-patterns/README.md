# Patterns — Bitfocus Companion module

Stream Deck / Companion control for the Patterns show display suite: fast look recall,
presenter next/back, transport, blackout with live feedback, individual screens and
canvas groups, VOGs and stingers (one-press sounds and clips), break music (Spotify), the audio track,
and the caller's VT clock — what is left of the clip on air, red for its last ten seconds.

## Setup

1. In Patterns: **Remote** page (SETUP) → make sure remote control is on (Companion TCP port,
   default **9697**).
2. In Companion (v3.x): `Connections → Add`, import this module as a dev module
   (`Settings → Developer modules path` pointing at this folder after `yarn && yarn package`,
   or copy the folder into your dev modules directory), then add the **Patterns** connection
   with the machine's IP and port.
3. Drag presets onto keys: the **banks** first (Look bank, Cue bank — keys that label themselves
   from the show and dim while empty), then Cue stack (GO, standby ▲ ▼, HOLD, ARM, STOP ALL),
   Transport, Presenter, Looks (F1–F12), Screens, VOG, Stingers, Audio, Break music, and the
   *… — this show* categories with a preset per item of the show that is loaded.

## The VT clock: what is left, the last ten seconds, the top (module 2.4.0)

While a clip is on air — the program's video, a playlist's video, a stinger's clip, an audio file —
Patterns now reads the caller's VT clock into STATE (`video{file,tag,position,length,remaining,
positionText,lengthText,remainingText,text,chip,playing,ended,loops,out,call}`) and pushes it every
second while the clip runs (only then, and only while a controller is connected). The variables:
`$(patterns:video_chip)` reads `VT 2:28` (what is left; `VT LOOP 1:02` for a loop that never comes
out, `VT ENDED` after the end), `video_remaining`, `video_remaining_seconds`, `video_position`,
`video_length`, `video_file`, `video_tag` (VT, AUDIO, STINGER CLIP, PLAYLIST), `video_text` (the whole
line), and `video_call` — the caller's word in the clip's last ten seconds (`OUT IN 7`). The
feedback `video_on_air` lights a key while a clip is on air, or — with *only in its last ten seconds*
ticked — red for the out. Two actions: `video_end` sends `VIDEO END <seconds>` (the rehearsal's
skip: the clip jumps to its last ten seconds, its end still plays, the out is still heard, and
whatever follows it — the playlist's next item, a stinger's ending — happens for real) and
`video_restart` sends `VIDEO RESTART`. Presets under *Presenter*: the VT clock key (reads what
is left, goes red for the last ten seconds, and pressing it is the skip), LAST 10 s and TOP.

## The cue stack (module 1.1.0)

The **GO** preset fires the cue on standby and shows its number and name; it is green while
the stack is armed, amber on HOLD or while a cue waits for confirmation (press GO again
within four seconds), red when the last cue failed or was refused. Every GO sends the
standby id the module last saw, so a GO that races a standby move is refused with
`ERR standby moved` rather than firing the wrong cue; `$(patterns:last_error)` carries the
last ERR line. **ARM** needs "Remotes may ARM the cue stack" on the Remote page. **STOP ALL**
stops the audio track, break music, any VOG or stinger (a clip or a held frame reverts, and a
stinger's ending is cancelled) and the tone — never the outputs, blackout or the stream. Variables: `cue_standby_number/name`, `cue_next_number/name`,
`cue_previous_number/name`, `cue_last_outcome`, `cue_confirm`, `cue_armed`, `cue_hold`,
`cue_seq`, and `program` (what is on air, by name). The module says `HELLO <label>` on
connect, so the caller's history reads "GO from FOH deck".

## VOG and stingers (module 1.2.0)

One library, one numbering. The **Stingers** presets (`stinger_1..8`, `STINGER n`) fire whatever
item *n* is; the **VOG** category (`vog_1..8`, `VOG n`) and the kind-checked stinger keys
(`STING n`, `sting`, `sting_name`) refuse the other kind — Patterns answers `ERR … is a VOG, not a
stinger` — so a key that says VOG never fires a transition. Feedbacks: `vog_playing` (blue — a VOG
clip, or a VOG sound, including one playing over a stinger it ducks),
`sting_playing` (amber) and `sting_hold` (hold amber) — a stinger that landed and is holding the
screens lights every sting key until the caller TAKEs, GOes, or presses the *Held stinger — put
it back* key (`stinger_stop`). `$(patterns:sting_hold)` names the held stinger. Note that the
`program` variable's prefix while something plays is now `VOG:`, `STING:` or `STING HOLD:` —
a trigger matching `STING:` should match `VOG:` too.

## A look on one screen alone (module 2.3.0)

Patterns' Show panel now runs the show from one place — the cue strip (standby, GO, HOLD, ARM),
the looks grid and a row per screen with its own look picker — and the two per-screen sends it
gained are here as actions. `screen_look` sends `SCREEN <n> LOOK <name or id>`: the look's
picture for that screen (its own picture in the look if it had one, else the look's program)
lands on screen `n` alone as the screen's own pattern; every other screen stays as it was, a
whole-look recall or a cue later replaces it, TAKE leaves it, a lock keeps it. `screen_program`
sends `SCREEN <n> PROGRAM`: the screen drops its own picture and follows the program again.
Presets under *Screens*: PGM per screen (the key labels itself from the screen's label). A key
for "sponsor logo on the side screen" is `screen_look` with the screen's number and the look's
name typed once; the same line works from a cue (*Screen — its own look*) and from OSC
(`/patterns/screen/<n>/look "<name>"`, `/patterns/screen/<n>/program`).

## Installs: announcements, adverts, the schedule (module 2.2.0)

A permanent install runs from Patterns' Install page (PLAN) — programmes on a rota, adverts at
their times, announcements by the clock or by hand — and a Stream Deck at the reception desk
or the shop's till gets the by-hand part: `announce` sends `ANNOUNCE <name or words>` (an
announcement of the Install page by its name, else the words themselves on the message overlay
for the site's announcement seconds), `announce_off` ends it; `advert` sends `ADVERT <name or
number>` (its look for its seconds, on its screens, the programme back after) and `advert_off`
ends it early; `schedule` sends `SCHEDULE ON` / `OFF`. Feedbacks `schedule_on` (green),
`announcement_on` and `advert_on` (amber and blue; blank = any, or a named one) light the keys
from the `install` block every STATE carries; variables `install` (ON/off), `install_programme`,
`install_over` (the announcement or advert on), `install_next` ("12:30 advert Lunch offer") and
`install_status` (the Install page's line). Presets under *Install*: SCHEDULE (a latch: press
on, press off), ANNOUNCE Closing time (edit the name), ANNOUNCE OFF, ADVERT 1–4 (the advert at
that place on the Install page), ADVERT OFF, and a status key. RESTART and UPDATE APPLY are
deliberately not here — they take the admin passcode, from the phone's ADMIN page or a
Generic TCP line of your own.

## A line to an Arduino or an IP device (module 2.1.0)

`device_send` writes a line to a device of Patterns' Interactive page — `DEVICE Arduino RELAY 1`
on the wire — so a Stream Deck key can fire a relay, a lamp or a script in the room through the
same board a cue would use; `*` names the first device. What a device sends back becomes show
commands on the Patterns side (its triggers, or the protocol as it is), and every key here lights
from the same STATE the device hears.

## Keys that fill themselves from the show (module 2.0.0)

Companion cannot place keys on a page by itself, so the module does the next best thing: a
**bank** is a row of keys you drag once, each of which labels itself from the show and fires
whatever sits at its place. Patterns pushes the show's lists in every `STATE`, and the module
turns them into variables that stay fresh — `$(patterns:look_1)`…`look_16` (the looks in the
order of the Looks page), `look_f1`…`f12` (by F-key), `lt_1`…`8`, `person_1`…`8`,
`stinger_1`…`8` (VOGs and stingers, one library), `music_1`…`6`, `section_1`…`6`,
`screen_1`…`8` (labels) and `cue_1`…`7` (the standby cue and the six after it, number and
name; `cue_k_number` / `cue_k_name` on their own) — plus `air_look`, `preview_look` and
`pattern` (what kind of picture is on air). The presets under **Look bank (labels itself)**,
**Cue bank (labels itself)**, **Looks** (F-keys), **Lower thirds**, **Stingers**, **VOG**,
**Break music**, **Playlist parts** and **Screens** carry the variable as their text, the
matching action (`look_bank` sends `LOOK #n`, the *n*th look in the show's order whatever its
name or F-key; `cue_bank` puts the cue at place *k* on standby — or on standby and GO — using
the number and id it last saw; `lower_third n`, `lower_third_person n`, `stinger n`,
`music_item n`, `section n`, `screen n`, `screen_lock n`), a lit feedback (`look_bank_on_air`,
`look_f_on_air`, `look_on_air` by name — green; `look_preview` — amber while a look is loaded
in the preview; `screen_enabled`, `screen_locked`, and the new `screen_armed` / `screen_own`)
and the `slot_empty` feedback, which dims a key while nothing sits behind it. Save a look in
Patterns and the next dim key on the bank lights up with its name; rename or reorder and the
keys follow; delete and the key dims again.

The module also builds a preset **per item** — *Looks — this show*, *Lower thirds — this
show*, *People — this show*, *Stingers — this show*, *VOGs — this show*, *Break music — this
show*, *Playlist parts — this show*, *Screens — this show* and *Upcoming cues — this show* —
named for the item and rebuilt the moment the show's lists change, so a key with a fixed name
is one drag away. A `stream` action (STREAM ON / OFF) joins the transport.

## Decks: a PDF presentation from a key (module 1.11.0)

A deck — a PDF on the pattern — is the click-through while it is on air: the **Presenter**
NEXT / BACK keys turn its pages before they step the clicker list, and past the last page the
caller's stack resumes (GO on the standby cue) when the deck asks for it. `deck_page` turns it
on its own — NEXT, PREV, FIRST, LAST or a page number (`DECK NEXT`, `DECK PAGE 5` on the wire).
The `deck_on_air` feedback lights a key while a deck is on air, or amber only on its last page
(the *only on its last page* option) so the caller sees the GO coming; `$(patterns:deck_page)`,
`$(patterns:deck_count)` and `$(patterns:deck_file)` read the page. Presets: DECK ▶ (with the
page count), DECK ◀, FIRST, LAST.

## Web pages: next slide, present, play — YouTube, Google Slides, PowerPoint (module 1.10.0)

A **Web page** category drives the web page on air — the one the program shows — from a key.
`web_action` sends a page action Patterns maps to the page's service: **NEXT** / **PREV** /
**FIRST** / **LAST** are the slide keys of a Google Slides deck or a PowerPoint for the web,
**PRESENT** starts the deck (Ctrl+Shift+F5 on Slides, F5 on PowerPoint), **BLACK** / **WHITE**
blank it, **EXIT** leaves; on YouTube **PLAY**, **PAUSE**, **MUTE**, **RESTART**, +10 s and −10 s
go through the player itself, so they work whether or not the page has focus; on any other page
the arrows and page keys. *A key of your own* sends a chord — `ArrowRight`, `Space`, `k`,
`Ctrl+Shift+F5`. A page other than the one on air is named by its nickname or a word of its
address (`WEB KEY next ON slides` on the wire). `web_click` clicks a spot in percent of the page,
`web_type` types into the field that has the page's focus, `web_open` sends the page's browser to
another address. The `web_on_air` feedback lights a key while a page is on air (any, or one whose
address carries a word); `$(patterns:web_page)`, `$(patterns:web_title)` and
`$(patterns:web_service)` name it. Presets: PAGE NEXT, PREV, FIRST, LAST, PRESENT, EXIT, PLAY,
MUTE, BLACK, RELOAD. On the wire: `WEB KEY <key|action> [ON <page>]`, `WEB NEXT` (any action
word), `WEB CLICK x y`, `WEB TYPE text`, `WEB RELOAD [page]`, `WEB OPEN address`; `PAGE` is an
alias of `WEB`.

## Lower thirds: preview, take, update, the show's default (module 1.9.0)

The sign-off flow from a key. **LT PVW n** (`lower_third_preview`) puts design *n* — with a
person from the library when the action names one — into the preview: the desk's PREVIEW pane,
the multiview's Preview tile and REVIEW show it while the audience sees nothing new, and a show
caller or director checks the spelling. **PVW PERSON n** puts library entry *n* into the design in
the preview (else the one on air, else the show's ★ default) and into the preview. **LT TAKE**
(`lower_third_take`) puts the signed-off lower third on air, arriving the way it was designed to, and
clears the preview for the next name; the key reads `$(patterns:lower_third_preview)` and lights
amber (`lower_third_preview` feedback, a design name optional) while something is in the preview.
**LT UPDATE** (`lower_third_update`) pushes an edit made while a design is on air across in place —
the words too — with no leaving and arriving again; the key lights amber (`lower_third_edited`,
`$(patterns:lower_third_edited)` reads EDITED) while the design on air differs from the edited one.
`$(patterns:lower_third_default)` names the show's ★ design, where PERSON n goes when nothing is on
air. The preview needs EDIT SAFE on the desk; without it Patterns answers `ERR` and the AIR keys work
as before. `LT PREVIEW n [WITH person]`, `LT PREVIEW WITH person`, `LT TAKE`, `LT UPDATE`,
`LT PREVIEW OFF` on the wire.

## Freeze, the timed fade and the previous look (module 1.8.0)

**FREEZE** (Transport) holds every output's frame — the windows, the NDI sends, the stream —
until it is pressed again; the key lights cyan while the rig is frozen (`frozen` feedback,
`$(patterns:freeze)` reads FROZEN/off) and follows a freeze made anywhere else. The `freeze`
action takes a mode (toggle / freeze / release). **FADE TO BLACK 2 s** and **FADE UP 2 s** are
the `fade` action: a direction and the seconds (0 = the show's transition time) — a blackout with
a fade of its own, so the key that fades the room to black over two seconds needs no change to
the transition setting; the blackout feedback lights the down key. **PREVIOUS LOOK** (Looks)
puts the look that was on air before the current one back on air (`look_back`; the key reads
`$(patterns:previous_look)`), and pressing it again swaps the two.

## Review on the multiview (module 1.7.0)

The **Transport** category gains **REVIEW** (`review` action: toggle / on / off, `REVIEW …` on the
wire): every multiview — a screen's own multiview pattern, an NDI send of it, `/multiview` — draws
the desk's sandboxed preview full-frame with a REVIEW chip until it is switched off, so the next
look is checked on the monitor wall before the TAKE; the audience's screens do not change.
Feedback `review_on` (green) and `$(patterns:review)` (`ON` / `off`). Every existing action,
feedback and variable id is unchanged.

## The people library (module 1.6.0)

The **Lower thirds** category gains the library from the Lower thirds page: `lower_third_person`
(by number, page order: `PERSON n` on the wire, or `LT <design> WITH n` when a design is named)
and `lower_third_person_name` (by name) put a person — name, role, company and photo — into the
lower third on air (else the last shown, else the first) and show it again: the next speaker in
one press. A name that is not in the library is refused (`ERR … not in the lower-thirds library`),
so a wrong name never reaches the screen. Feedback `lower_third_person_is` (red while that name is
on screen), `$(patterns:lower_third_person)` (the name the lower third on screen carries, or empty),
and the state's `people[{n,name,role}]`. Presets: PERSON 1–6. Every existing action, feedback and
variable id is unchanged.

## Screen locks (module 1.5.0)

The **Screens** category gains `screen_lock` (toggle / lock / unlock, `LOCK n …` on the wire): a
locked screen keeps its picture through every look, cue, TAKE ALL and stinger — a confidence
monitor or an info screen that must not change on a cue — and follows again when unlocked. The
`screen_locked` feedback lights amber while it is locked, and the state's `screens[]` rows carry
`locked` and `role`. Every existing action, feedback and variable id is unchanged.

## Lower thirds (module 1.4.0)

The **Lower thirds** category puts a design from the Lower thirds page on air over whatever is
showing — on every screen, NDI send and the stream at once — and takes it off the way it was
designed to. Actions `lower_third` (by number, Lower thirds page order: `LT n` on the wire),
`lower_third_name` (by name) and `lower_third_off` (`LT OFF`); pressing a design's key again
restarts its way in. Feedback `lower_third_on` (red; a blank name means any design, a name means
that one) and `$(patterns:lower_third)` (the design on screen, or empty). Presets: keys 1–6 and
OFF. Every existing action, feedback and variable id is unchanged.

## Live duck (module 1.3.0)

The **DUCK** key (`duck` action: toggle / on / off, `DUCK …` on the wire) makes way for an
announcement from the room: the music track, break music, a playing stinger's sound and a clip's
soundtrack drop to the Audio page's live-duck level, ramping, and come back when the key is
pressed again — a VOG never ducks. It is a latch, not a programme source: STOP ALL and look
recalls leave it. Feedback `duck_on` (hold amber) and `$(patterns:duck)` (`DUCK` / `off`). Every
existing action, feedback and variable id is unchanged.

## Break music (module 1.2.0)

The **Break music** category drives Spotify through Patterns: play / resume, pause, skip and
keys for entries 1–6 of the Audio page's break-music library (actions `music`, `music_item`,
`music_name` and `music_level` for anything else). Every key lights green while music plays
(`music_playing` feedback); `$(patterns:music)` is the track Spotify reports,
`$(patterns:music_state)` PLAYING / paused, `$(patterns:music_level)` the device level and
`$(patterns:music_device)` the Spotify device. With break music switched off in Patterns the
keys answer OK and do nothing, so a saved page never breaks a cue.

No module? The same protocol works with Companion's built-in **Generic TCP** connection —
send the commands listed in `docs/REMOTE.md` (e.g. `LOOK 2`, `NEXT`, `BLACKOUT TOGGLE`),
one per line. You lose feedback; keys still fire.

## Feedback

Patterns pushes `STATE {json}` lines whenever anything changes, so blackout, per-screen
enable, stinger-on-air and audio-playing states light keys live, and
`$(patterns:presenter_step)` / `$(patterns:presenter_count)` variables put the click
position on the NEXT key (`$(patterns:stinger)` and `$(patterns:health)` exist too).
Machine health lives in `$(patterns:machine_cpu)`, `$(patterns:machine_fps)`,
`$(patterns:machine_power)` (mains/BATTERY) and `$(patterns:machine_advice)` — put CPU
and fps in a key corner and you have a confidence monitor on the Stream Deck.
