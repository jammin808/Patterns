# The show machine: what Patterns holds off, and what the administrator sets once

A show has been interrupted by every one of these: a toast sliding over the desk, a new-mail
chime through the PA, a Teams call ringing out of the show's speakers, Sticky Keys popping up
with a beep when the caller hammers Shift, the display going to sleep in a long hold, the
Windows key opening Start over the switcher, and Windows Update restarting the machine at 21:03.

## What Patterns does by itself — the show lock

Machine page → **SHOW LOCK**. On with the outputs (by default) and off with them, or by hand,
or `SHOWLOCK ON` / `SHOWLOCK OFF` on the wire. Each item is its own switch, every one is put
back on unlock and on a clean exit, and a crash leaves a receipt (`showlock.receipt.json` beside
the settings) the next start puts back from.

| Item | What is done | How |
|---|---|---|
| Notifications | Toasts and banners off for this user | the user's toast switch, and the per-user policy Explorer honours |
| System sounds | Every event's sound silenced, the default beep off | the sound scheme's events, one by one, with the originals kept |
| Other apps' audio | Every audio session that is not Patterns' muted — Teams' ring, Outlook's alert, a browser — and any that starts later, within two seconds | the same per-app session control the volume mixer uses; the break-music player is let through by name (*Allowed audio*) |
| Sticky, Filter and Toggle Keys shortcuts | The five-Shift, eight-second-Shift and five-second-NumLock shortcuts off (a feature the user has on is left on) | the way games do it |
| Sleep and the screensaver | The machine kept awake, the display on, the screensaver off | the execution state and the screensaver switch |
| The Windows key | Swallowed, so nothing opens Start | a low-level keyboard hook, removed on unlock |
| Windows Update | **Read only**: a pending restart is said on the Machine page, the health line, the super-check and the brief | needs an administrator to prevent — below |

The foreground is watched too: if another app takes it while the lock is on (a Teams window
popping up), the log and the journal say which and when, because the caller's keys go there
until the desk is clicked.

## What needs an administrator — once, before the tour

Windows Update can restart the machine and no user-level setting stops it. Run
`tools/show-machine.ps1` **as an administrator** once on the show machine; it sets the policies
below and prints what it did. Undo with `-Undo`.

- Windows Update: no automatic restart while a user is signed in
  (`NoAutoRebootWithLoggedOnUsers`), updates deferred and paused to the date you give
  (`PauseUpdatesExpiryTime`, up to 35 days), active hours set to the widest Windows allows.
- Toasts off for every user on the machine (the machine policy, in case the show runs under
  another account).
- The Windows Error Reporting dialog off (a crash in some other program must not put a dialog on
  a screen).

The script does not touch Teams or Outlook: quit them before doors, or leave them and let the
lock mute them — a Teams call still pops a window on the desk's screen (the lock logs it), so
quitting is the safer choice. Set Teams to *Do not disturb* if it must stay.

## Before doors, on the Machine page

1. RUN SUPER-CHECK: the *Show lock* row is green when held, amber when the outputs are live and
   the machine is not held, red when an item could not be held or a Windows Update restart is
   pending.
2. Check *Allowed audio*: the break-music player (Spotify) is let through by default; add
   anything else the show itself plays through another program.
3. Quit Teams and Outlook, or set Teams to *Do not disturb*.
