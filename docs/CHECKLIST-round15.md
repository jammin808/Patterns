# Round 15 — Windows checklist

The headless suite proves the logic on every push. These are the things only a real rig shows:
one line per item, what to do, and what you should see. Tick them on a Windows machine with the
full build (libVLC bundled), two displays and an audio interface. Row 1 is the report's crash
between menus: do it on the machine it happened on, and after any restart read the health line
before touching anything — the sentence it shows is the evidence.

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 1 | The crash between menus, contained | Start Patterns through the watchdog; move through every page on the rail (SHOW, PLAN, BUILD, SETUP, ADMIN) and back, then RUN and EXIT RUN, at the laptop's window size and maximised, with OUTPUTS ON on two outputs and a clip playing; read STABILITY on the Machine page and the health line; then, to see the guard itself, type `HELP` on the console and press a key the page refuses — nothing should happen but the refusal; if the desk ever restarts, read the health line and STABILITY first, then `patterns.log` and the support bundle | Every switch is instant and the outputs never flinch; STABILITY reads *no faults*; if a page did throw, the status line reads *A fault was contained and the desk carried on — <type>: <message> — in <where>*, the health line counts it (*1 fault caught, show kept running (last hh:mm — UI fault contained …)*), `patterns.log` has the stack under *UI fault contained*, and the desk, the outputs, the clip and the remotes carry on; if the desk did restart, the health line and STABILITY read *The last run ended in an unhandled .NET exception (see patterns.log) — <type>: <message> — in <the app's frames> — at hh:mm:ss after n min* (a native exit code means §18.2 of the plan and the mini-dump instead) — send that sentence with the bundle |
