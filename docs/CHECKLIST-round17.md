# Round 17 — Windows checklist

The headless suite proves the logic on every push. These are the things only a real rig shows:
one line per item, what to do, and what you should see. Tick them on a Windows machine with the
full build (libVLC bundled), two displays and an audio interface. The round came from a rig day —
a crash between pages, the switcher's tiles, a send that lost the preview, the grid on the tiles,
the assistant's first question — so the rows are the desk in use.

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 2 | The assistant answers | ADMIN → Assistant with a saved key: ask *What can you help me build?*; then *Two screens and a walk-in look with the clock*; APPLY a proposal; ask a third question | Every question is answered in words with proposals or questions, never *The assistant failed: … The compiled grammar is too large …*; if the status line carries *The service declined the reply's schema; the answer came as plain JSON and was read all the same* once, the answer still reads and the questions after it carry no such note; APPLY still lands the look with its overlays |
| 1 | No crash between pages | BUILD → Lower thirds: pick a design with a Fractal element (the *Fractal* preset, or add a Fractal element to any design) and let the designer preview play; with no output connected and then with one, switch BUILD → Pattern and back to Lower thirds as fast as the mouse allows, twenty times; do the same between SETUP → Screens and any other page; leave the desk running ten minutes with the fractal design playing in the preview | No exit, no *App crashed (exit -1073741819 …)* on the next start's health line; the Lower thirds preview draws again every time the page is re-entered (a fresh stage, the fractal moving); the Screens page's tiles draw again every time; the log carries no *Lower-third preview failed to draw* |
