# Permanent installs — the clock runs the site

A shop window, a hotel lobby, a museum wall, a reception screen, a stadium concourse: a machine
nobody sits at, that must show the right thing at the right time for months, take an advert or an
announcement in its stride, be looked after from somewhere else, and take a new build without
anyone driving to it. The **Install** page (PLAN) is that machine's home. Everything on it goes
through the same action layer as the desk, the cues, the phone, Companion and OSC, so the journal
reads *ApplyLook from schedule* beside *GO from tcp FOH deck*, and every rule the show already has
(the caller's stack owns the screens while armed, a stinger holds them, a locked screen keeps its
picture) holds for the clock too.

Off by default. On a show machine the clock must never move the picture by itself.

## The rota: programmes, adverts, announcements

Every row of the schedule is one of three kinds.

| Kind | What it is | When |
| --- | --- | --- |
| **Programme** | A look on air from a start to an end | its days, between its dates, `Start`–`End` |
| **Advert** | A look for some seconds, then the programme back underneath — on every screen, or only the screens it names (the others keep their picture, locked for the advert and freed after) | at `Start`, then every `Every` minutes until `End` |
| **Announcement** | Words over the programme (the message overlay), a VOG from the Audio page's library, a look of its own — for some seconds | at `Start`, then every `Every` minutes until `End`; or by hand |

- **Days** read like a rota: blank = every day; `Mon–Fri`, `weekdays`, `weekends`, `Sat Sun`,
  `Mon, Wed, Fri`, `Fri-Sun`, `Sat-Mon` (a range past the week's end), day names whole or by their
  first letters.
- **From / Until** fence a seasonal row: `2026-12-01` to `2026-12-31` (the safe form; `24/12/2026`,
  `24.12.2026` and `24 Dec 2026` read too). Both ends inclusive; blank = open.
- **Start / End** are `HH:mm`. A window that ends at or before it starts runs past midnight
  (`22:00`–`02:00`): Tuesday 01:30 belongs to Monday's window. The end is exclusive — an advert
  every 30 min from 10:00 to 12:00 fires at 10:00, 10:30, 11:00 and 11:30.
- **Which programme is on** when two overlap: a dated row beats an undated one (the Christmas rota
  over the daily one), then the later start wins (a *Lunch* row inside *Daytime*), then the first in
  the list. Outside every programme the screens show the **idle look**, or black when none is set;
  the next programme lifts the black after its look has landed, so the audience never sees the old
  picture.

The page's **TODAY** block lists the day as the clock will run it — every programme window and
every firing in time order, with NOW and done — and says in words what a row cannot do (a day that
does not read, a look the show lacks, an advert with no picture) before the clock trips over it.

### How the clock decides (the rules the tests pin)

- A programme's look is applied once when its window begins (and again after an advert or an
  announcement with a look has moved the picture) — never every second, never over an operator's
  edit while it is still the same programme.
- An advert or announcement fires at its minute. **Announcements beat adverts**: one due while an
  advert runs cuts the advert short; an advert due while an announcement runs waits and fires when
  the words end, if that is within five minutes; later than that it is missed rather than fired
  into the wrong moment. A firing that lands while the desk owns the screens — the caller's stack
  armed, a stinger holding — is skipped and said in the journal.
- A firing before the clock started is not owed: switching the schedule on at 15:03 does not fire
  the 15:00 advert.
- Switching the schedule off ends an override in progress and leaves the picture where it is;
  nothing fires until it is on again, when the programme is applied afresh.

### By hand

The by-hand part works with the schedule on or off: an announcement is often a person's call, not
the clock's.

| Where | How |
| --- | --- |
| The page | **PLAY NOW** on an advert or announcement row; **END WHAT IS ON** |
| A cue | *Announcement on* (choose one, or leave it blank and type the words in the value), *Announcement off*, *Advert — play now*, *Advert — end now*, *Install schedule on / off*; the checks refuse a name that is not on the page |
| The wire (TCP, `/api/cmd`) | `ANNOUNCE <name or words>`, `ANNOUNCE OFF`, `ADVERT <name or number>`, `ADVERT OFF`, `SCHEDULE ON / OFF` |
| OSC | `/patterns/announce "words"` (also `/patterns/announce/<name>`, `/patterns/announce/off`), `/patterns/advert "name"` (also `/patterns/advert/<n>`, `/patterns/advert/off`), `/patterns/schedule 1|0` |
| Companion 2.2.0 | `announce`, `announce_off`, `advert`, `advert_off`, `schedule`; feedbacks `schedule_on`, `announcement_on`, `advert_on`; the *Install* presets |
| The phone's ADMIN page | every announcement and advert as a key, a free-text box, the schedule's switch, END NOW |

`ANNOUNCE Closing time` fires the announcement called *Closing time*; `ANNOUNCE The car with the
lights on` — words that are not a row's name — puts those words on the message overlay for the
site's **Words stay up** seconds. `ANNOUNCE OFF` ends an announcement and refuses while an advert is
on (`ADVERT OFF` ends that): a key that says ANNOUNCE OFF never skips an advert. STATE carries an
`install` block (below) so every remote lights from the same facts.

## Remote administration

A passcode on the Install page (**Admin passcode**) opens the web remote's **ADMIN** page
(`http://<machine>:9696/admin`, linked from the remote's SETUP tab): the health line, the schedule's
switch and its status, every announcement and advert as a key with its state, a free-text
announcement, RESTART, the staged update and APPLY, a support bundle to download, the log's last
eighty lines, and a console for any line of the protocol. The same passcode rides the wire:
`RESTART <passcode>` (the app restarts under the watchdog with the show put back) and
`UPDATE APPLY <passcode>`.

The gate compares in constant time and locks for a minute after five wrong tries. The passcode
travels on the LAN in the clear like every remote command: it is a fence around a site, not a
secret across the internet — for that, put the machine on a VPN or behind the management server
below and keep the remote's ports off the public side.

**Assistance** starts with the **SUPPORT BUNDLE**: a zip beside the settings (or downloaded from
the ADMIN page) with `patterns.log` (and its `.old`), the watchdog's log, the show journal, the
settings with every secret blanked (`AdminPasscode`, `ManagementToken`, tokens and keys become
`•••`), the last super-check, the metrics CSV, the recovery sidecar, the last update note and a
`bundle-info.txt` with the site, the build, the machine, the health line and what the install, the
update folder and the check-in are doing. Send it with the question.

## The management server: check-in

A site behind a shop's router needs no port opened. With a **check-in URL** set, Patterns POSTs
every *N* minutes (and on CHECK IN NOW):

```json
{ "site": "Store 12 window", "version": "1.0.0", "machine": "SIGN-PC", "health": "Up 3h 12m · no faults",
  "utc": "2026-09-07T10:00:00Z", "state": { …the same STATE every remote reads… } }
```

with the header `X-Patterns-Token: <token>` when a token is set. The reply may carry:

```json
{ "token": "<the same token>",
  "commands": ["ANNOUNCE Sale on now", "SCHEDULE ON", "LOOK Weekend"],
  "update": { "url": "https://…/patterns-update-1.2.0.zip", "version": "1.2.0", "sha256": "<64 hex>" },
  "applyUpdate": false, "restart": false, "note": "seen" }
```

- With a token configured the reply must echo it, or it is ignored and the page says so.
- `commands` are protocol lines (at most 20, 200 characters each), run through the router with the
  server as their origin — the journal reads *from management Store 12 window* — and fenced like any
  remote's: a GO needs the stack armed, a passcode verb needs the passcode.
- `update` is downloaded into the updates folder and kept only when its SHA-256 is the one
  promised; `applyUpdate: true` applies it now, otherwise the update window or APPLY does.
- `restart: true` restarts the app under the watchdog (with the site's own passcode, so the gate's
  rules hold).

HTTPS anywhere; plain `http://` only to this machine or a private network (`10.`, `192.168.`,
`172.16–31.`, `.local`). The status line on the page reads *Checked in at 10:05 — 2 commands*, the
answer to each command that was refused, or why the URL cannot be used.

Anything that speaks HTTP can be the server — a few lines of Node, a Python script, a signage CMS
with a webhook. It sees every site's health and state at every check-in, and speaks back only
through the fence above.

## Updates the watchdog applies

An update is a zip in `updates/` beside the settings:

```
patterns-update-1.2.0.zip
├── Patterns.exe              ← at the root, required
├── patterns.update.json      ← { "version": "1.2.0", "notes": "…" } — required
└── libvlc/…                  ← anything else the build ships beside the exe (optional)
```

Drop it in over the network share, or let the management server deliver it. The page reads it
(*Staged: 1.2.0 (patterns-update-1.2.0.zip, 2 files); running 1.0.0*) and refuses a package with
no exe, no version, a path that climbs out of the folder, or one that does not open.

**APPLY** (the page, `UPDATE APPLY <passcode>`, the ADMIN page, the management server's
`applyUpdate`, or the **update window** with *Apply by itself at* on — once a day at that minute)
asks the watchdog to do the swap:

1. The app saves the show, writes the recovery sidecar as it would for a restart, leaves an
   `updates/apply.json` request and exits with the update code (83).
2. The watchdog reads the request, moves every file the package replaces into
   `updates/backup-<date>-<time>/` (a rename, which Windows allows even for the exe the watchdog
   itself runs from), puts the package's files in place, deletes the package and starts the app —
   with `--recover`, so the show comes back live as it was.
3. The new build has **two minutes to prove itself**. If it exits with an error or hangs before
   then, the watchdog moves the backup back, starts the old build, writes the reason to
   `updates/last-update.txt`, the watchdog log and the health line (*Update to 1.2.0 rolled back at
   03:00: the new build exited with 1 after 12 s*). If it stays up (or is closed cleanly), the update
   is kept and the note says so; the backup folder stays for a roll-back by hand.

The watchdog process itself keeps running the old code until the next full close — it is the small
supervisor, not the show. Without the watchdog (a `--no-watchdog` start, or the Stability switch
off) APPLY refuses and says so; copy the files by hand instead.

## STATE

Every STATE push (and `STATUS`) carries:

```
install{on, site, programme, idle, over, overKind, overUntil, next, status,
        slots[{n,name,kind,enabled,status}], problems,
        update{staged,version,ok,running,supervised,status,last}, management}
```

`programme` is the programme on (by name), `over` the announcement or advert on (its name, or the
words of a free-text announcement) with `overKind` and `overUntil` (`HH:mm:ss`), `next` the next
change (*12:30 advert Lunch offer*), `status` the page's line, `problems` how many rows cannot do
what they say. OSC feedback carries `/patterns/state/install/on`, `/programme`, `/over`, `/until`
and `/next`.

## Where things live

| Path | What |
| --- | --- |
| `patterns.settings.json` → `Install` | the rows and the site's settings (the passcode and the token in the clear — the folder is the trust boundary) |
| `updates/*.zip` | staged packages (the newest counts) |
| `updates/apply.json` | the request the app leaves for the watchdog |
| `updates/backup-<date>-<time>/` | the files an update replaced |
| `updates/last-update.txt` | what the last update did |
| `patterns-support-<date>-<time>.zip` | a support bundle |
