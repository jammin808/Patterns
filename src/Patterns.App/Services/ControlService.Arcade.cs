namespace Patterns.App.Services;

/// <summary>The phone pad: a d-pad, A, B and START for one of four players, each press a line to the arcade's own verb.</summary>
public sealed partial class ControlService
{
    private const string PadPage = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no">
<title>Patterns Arcade pad</title>
<style>
  html,body{margin:0;height:100%;background:#0b0c10;color:#e6e9ef;font:16px/1.3 system-ui,sans-serif;-webkit-user-select:none;user-select:none;touch-action:none;overscroll-behavior:none}
  .wrap{display:flex;flex-direction:column;height:100%;padding:12px;box-sizing:border-box;gap:10px}
  .top{display:flex;gap:8px;align-items:center}
  .top button{flex:1;padding:10px 0;border:1px solid #2a3140;border-radius:10px;background:#141720;color:#c0cbdb;font-weight:600}
  .top button.on{background:#e0ff5f;color:#111;border-color:#e0ff5f}
  .words{color:#9aa4b5;font-size:14px;min-height:1.3em}
  .board{flex:1;display:grid;grid-template-columns:1fr 1fr 1fr;grid-template-rows:1fr 1fr 1fr;gap:10px;min-height:0}
  .board button{border:none;border-radius:18px;background:#1c2130;color:#e6e9ef;font-size:34px;font-weight:700;touch-action:none}
  .board button:active,.board button.down{background:#e0ff5f;color:#111}
  .btns{display:grid;grid-template-columns:1fr 1fr 1fr;gap:10px;height:96px}
  .btns button{border:none;border-radius:18px;font-size:26px;font-weight:800;touch-action:none;color:#111}
  #a{background:#7cf5c8}#b{background:#ff9e58}#start{background:#5fd0ff}
  .btns button:active,.btns button.down{filter:brightness(1.3)}
</style></head>
<body><div class="wrap">
  <div class="top">
    <button data-p="1" class="on">P1</button><button data-p="2">P2</button><button data-p="3">P3</button><button data-p="4">P4</button>
  </div>
  <div class="words" id="words">Patterns Arcade — the pad</div>
  <div class="board">
    <div></div><button data-b="UP">▲</button><div></div>
    <button data-b="LEFT">◀</button><div></div><button data-b="RIGHT">▶</button>
    <div></div><button data-b="DOWN">▼</button><div></div>
  </div>
  <div class="btns"><button id="a" data-b="A">A</button><button id="b" data-b="B">B</button><button id="start" data-b="START">START</button></div>
</div>
<script>
  let player = 1;
  document.querySelectorAll('.top button').forEach(b => b.addEventListener('click', () => {
    player = +b.dataset.p;
    document.querySelectorAll('.top button').forEach(o => o.classList.toggle('on', o === b));
  }));
  function send(button, how) {
    fetch('/api/arcade/key', { method: 'POST', body: player + ' ' + button + ' ' + how, keepalive: true }).catch(() => {});
  }
  document.querySelectorAll('button[data-b]').forEach(b => {
    const down = e => { e.preventDefault(); if (!b.classList.contains('down')) { b.classList.add('down'); send(b.dataset.b, 'DOWN'); } };
    const up = e => { e.preventDefault(); if (b.classList.contains('down')) { b.classList.remove('down'); send(b.dataset.b, 'UP'); } };
    b.addEventListener('pointerdown', down);
    b.addEventListener('pointerup', up);
    b.addEventListener('pointercancel', up);
    b.addEventListener('pointerleave', up);
    b.addEventListener('contextmenu', e => e.preventDefault());
  });
  async function poll() {
    try {
      const r = await fetch('/api/arcade', { cache: 'no-store' });
      const j = await r.json();
      const w = Array.isArray(j) ? j.map(n => n.node + ': ' + (n.status ? n.status.words : n.reply)).join(' · ') : j.words;
      document.getElementById('words').textContent = w || 'No arcade heard.';
    } catch (e) { document.getElementById('words').textContent = 'No answer from the arcade.'; }
  }
  poll(); setInterval(poll, 2000);
</script></body></html>
""";
}
