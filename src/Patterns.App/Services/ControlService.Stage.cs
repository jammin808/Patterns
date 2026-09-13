namespace Patterns.App.Services;

/// <summary>
/// The stage pages: <c>/stage</c> — the display in front of the speaker (or, with <c>?view=crew</c>,
/// the stage manager): the timer in the colour of what is left, the message with its ACK, a flash;
/// and <c>/timer</c> — the controller: start, pause, resume, seconds either way, stop, flash, the
/// presets, a message to either display, the receipts. Both long-poll <c>/api/stage</c> and tick
/// their own clock between polls, so a tablet on a stand or a phone taped to the lectern is a
/// confidence monitor with no output card at all.
/// </summary>
public sealed partial class ControlService
{
    private const string StagePage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<title>Patterns Stage</title>
<style>
  :root { --bg:#000; --text:#F2F4F8; --mut:#8A93A6; --green:#2EE68A; --amber:#FFC24D; --red:#FF3B3B; }
  * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
  html, body { height:100%; }
  body { margin:0; background:var(--bg); color:var(--text); font:20px/1.3 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; display:flex; flex-direction:column; overflow:hidden; }
  body.flash { animation: flash .5s steps(2) infinite; }
  @keyframes flash { 0% { background:#000; } 100% { background:#3A3000; } }
  .top { display:flex; justify-content:space-between; align-items:center; padding:2vh 3vw; color:var(--mut); font-size:3.2vh; letter-spacing:.06em; }
  .time { flex:1; display:flex; align-items:center; justify-content:center; font-weight:800; font-variant-numeric:tabular-nums; line-height:1; font-size:28vw; transition:color .3s; }
  .time.green { color:var(--green); } .time.amber { color:var(--amber); } .time.red { color:var(--red); } .time.off { color:#333; }
  .time.paused { animation: dim 1.2s ease-in-out infinite alternate; }
  @keyframes dim { from { opacity:1; } to { opacity:.35; } }
  .bar { height:1.2vh; background:#1A1D24; }
  .bar div { height:100%; background:var(--green); transition:width 1s linear; }
  .bar.amber div { background:var(--amber); } .bar.red div { background:var(--red); }
  .seg { padding:1.5vh 3vw; color:var(--mut); font-size:3vh; display:flex; justify-content:space-between; gap:2vw; }
  .msg { display:none; padding:3vh 3vw; background:#0F1522; border-top:0.6vh solid var(--amber); }
  .msg.on { display:flex; align-items:center; gap:3vw; }
  .msg .text { flex:1; font-size:7vh; font-weight:800; line-height:1.1; }
  .msg button { font:inherit; font-weight:800; font-size:4vh; padding:2vh 4vw; border-radius:2vh; border:0; background:var(--green); color:#062; cursor:pointer; }
  .label { font-size:4vh; color:var(--mut); text-align:center; letter-spacing:.2em; font-weight:700; }
  #err { position:fixed; bottom:1vh; right:2vw; color:var(--red); font-size:2vh; }
</style>
</head>
<body>
  <div class="top"><span id="show"></span><span id="clock"></span></div>
  <div class="label" id="label"></div>
  <div class="time off" id="time">--:--</div>
  <div class="bar" id="bar"><div id="fill" style="width:0%"></div></div>
  <div class="seg" id="seg"></div>
  <div class="msg" id="msg"><div class="text" id="msgtext"></div><button id="ack" onclick="ack()">ACK</button></div>
  <div id="err"></div>
<script>
  const view = new URLSearchParams(location.search).get('view') === 'crew' ? 'crew' : 'speaker';
  let rev = -1, state = null, skew = 0, pendingId = null;
  const el = id => document.getElementById(id);
  const fmt = s => { const neg = s < 0; s = Math.abs(Math.round(s)); const h = Math.floor(s/3600), m = Math.floor((s%3600)/60), x = s%60;
    return (neg?'-':'') + (h > 0 ? h + ':' + String(m).padStart(2,'0') : m) + ':' + String(x).padStart(2,'0'); };
  function render() {
    if (!state) return;
    const t = state.timer, now = Date.now() + skew;
    let remaining = t.remaining;
    if (t.phase === 'running' || t.phase === 'over') remaining = t.remaining - (now - Date.parse(state.serverUtc)) / 1000;
    const time = el('time');
    time.className = 'time ' + (t.phase === 'idle' ? 'off' : (remaining <= t.red ? 'red' : remaining <= t.amber ? 'amber' : 'green')) + (t.phase === 'paused' ? ' paused' : '');
    time.textContent = t.phase === 'idle' ? '--:--' : fmt(remaining);
    el('label').textContent = t.phase === 'idle' ? '' : (t.paused ? 'PAUSED' : (t.label || ''));
    const bar = el('bar'); bar.className = 'bar ' + time.className.replace('time ', '').split(' ')[0];
    el('fill').style.width = (t.phase === 'idle' ? 0 : Math.min(100, Math.max(0, t.progress * 100))) + '%';
    el('show').textContent = state.show || '';
    el('clock').textContent = (view === 'crew' || state.sees.speakerClock) ? state.clock : '';
    const s = state.segment;
    if ((view === 'crew' && state.sees.crewSegment) || (view === 'speaker' && state.sees.speakerSegment)) {
      const parts = [];
      if (s.name) parts.push('NOW ' + (s.number ? s.number + ' ' : '') + s.name + (s.remaining !== null ? ' · ' + (s.overran ? 'over' : fmt(s.remaining) + ' left') : ''));
      if (view === 'crew' && s.next) parts.push('NEXT ' + (s.nextNumber ? s.nextNumber + ' ' : '') + s.next + (s.nextAt ? ' at ' + s.nextAt : ''));
      if (view === 'crew' && s.offset) parts.push(s.offset);
      el('seg').innerHTML = parts.map(p => '<span>' + p.replace(/</g,'&lt;') + '</span>').join('');
    } else el('seg').innerHTML = '';
    const pending = view === 'crew' ? state.pendingCrew : state.pendingSpeaker;
    pendingId = pending ? pending.id : null;
    el('msg').className = 'msg' + (pending ? ' on' : '');
    el('msgtext').textContent = pending ? pending.text : '';
    const flashing = (pending && pending.flash) || (state.flashUntilUtc && Date.parse(state.flashUntilUtc) > now);
    document.body.className = flashing ? 'flash' : '';
  }
  async function poll() {
    try {
      const r = await fetch('/api/stage?since=' + rev, { cache: 'no-store' });
      const s = await r.json();
      skew = Date.parse(s.serverUtc) - Date.now();
      rev = s.rev; state = s; render(); el('err').textContent = '';
    } catch (e) { el('err').textContent = 'reconnecting…'; await new Promise(r => setTimeout(r, 1500)); }
    poll();
  }
  async function ack() {
    if (!pendingId) return;
    try { await fetch('/api/stage/ack', { method: 'POST', body: pendingId }); } catch (e) {}
  }
  setInterval(render, 250);
  poll();
</script>
</body>
</html>
""";

    private const string TimerPage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<title>Patterns Timer</title>
<style>
  :root { --bg:#0D0F14; --panel:#151A22; --line:#2A313E; --text:#E8ECF2; --mut:#98A1B1; --acc:#3EC1F3; --green:#2EE68A; --amber:#FFC24D; --red:#FF3B3B; }
  * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
  body { margin:0; background:var(--bg); color:var(--text); font:16px/1.35 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; padding:12px; }
  .time { text-align:center; font-size:72px; font-weight:800; font-variant-numeric:tabular-nums; line-height:1; margin:8px 0 4px; }
  .time.green { color:var(--green); } .time.amber { color:var(--amber); } .time.red { color:var(--red); } .time.off { color:#444; }
  .line { text-align:center; color:var(--mut); font-size:14px; min-height:18px; }
  .sec { font-size:12px; letter-spacing:.14em; color:var(--mut); font-weight:700; margin:14px 0 6px; }
  .grid { display:grid; gap:8px; } .row4 { grid-template-columns:repeat(4,1fr); } .row3 { grid-template-columns:repeat(3,1fr); } .row2 { grid-template-columns:repeat(2,1fr); }
  button { border:1px solid var(--line); border-radius:10px; background:var(--panel); color:var(--text); font:inherit; font-weight:800; padding:14px 8px; cursor:pointer; }
  button.go { background:#1E9E5A; border-color:#1E9E5A; color:#fff; } button.warn { background:#3A2E10; color:var(--amber); border-color:var(--amber); } button.stop { background:#3A1414; color:#FF9A9A; border-color:#7A2A2A; }
  input { width:100%; border:1px solid var(--line); border-radius:10px; background:var(--panel); color:var(--text); font:inherit; padding:12px; }
  .card { background:var(--panel); border:1px solid var(--line); border-radius:12px; padding:12px; margin-top:8px; }
  .msgs div { display:flex; gap:10px; padding:6px 0; border-top:1px solid var(--line); font-size:14px; }
  .msgs div:first-child { border-top:none; }
  .msgs .seen { color:var(--green); } .msgs .wait { color:var(--amber); }
  .link { display:block; text-align:center; padding:12px; border:1px solid var(--line); border-radius:10px; color:var(--acc); text-decoration:none; font-weight:700; }
  #err { color:var(--red); font-size:13px; min-height:16px; margin-top:8px; text-align:center; }
</style>
</head>
<body>
  <div class="time off" id="time">--:--</div>
  <div class="line" id="tline"></div>
  <div class="line" id="seg"></div>
  <div class="sec">START</div>
  <div class="grid row4">
    <button onclick="cmd('TIMER START 5')">5 min</button><button onclick="cmd('TIMER START 10')">10 min</button>
    <button onclick="cmd('TIMER START 15')">15 min</button><button onclick="cmd('TIMER START 20')">20 min</button>
  </div>
  <div class="grid row3" style="margin-top:8px">
    <input id="mins" placeholder="minutes, or HH:mm" inputmode="numeric">
    <button class="go" onclick="startCustom()">START</button>
    <button class="stop" onclick="cmd('TIMER STOP')">STOP</button>
  </div>
  <div class="sec">WHILE IT RUNS</div>
  <div class="grid row4">
    <button onclick="cmd('TIMER -60')">−1 min</button><button onclick="cmd('TIMER -10')">−10 s</button>
    <button onclick="cmd('TIMER +10')">+10 s</button><button onclick="cmd('TIMER +60')">+1 min</button>
  </div>
  <div class="grid row3" style="margin-top:8px">
    <button class="warn" id="pause" onclick="pauseResume()">PAUSE</button>
    <button onclick="cmd('TIMER FLASH')">FLASH</button>
    <button onclick="cmd('STAGE CLEAR')">CLEAR MESSAGE</button>
  </div>
  <div class="sec">TO THE SPEAKER</div>
  <div class="grid row3" id="presets"></div>
  <div class="grid row3" style="margin-top:8px">
    <input id="text" placeholder="a message to the speaker…" style="grid-column:span 2">
    <button class="go" onclick="send('speaker')">SEND</button>
  </div>
  <div class="grid row2" style="margin-top:8px">
    <button onclick="send('crew')">SEND TO CREW</button>
    <a class="link" href="/stage?view=speaker" target="_blank">Open the speaker's display ⟩</a>
  </div>
  <div class="grid row2" style="margin-top:8px">
    <a class="link" href="/stage?view=crew" target="_blank">Open the crew's display ⟩</a>
    <a class="link" href="/run">The caller's page ⟩</a>
  </div>
  <div class="sec">RECEIPTS</div>
  <div class="card msgs" id="msgs"></div>
  <div id="err"></div>
<script>
  let rev = -1, state = null, skew = 0;
  const el = id => document.getElementById(id);
  const fmt = s => { const neg = s < 0; s = Math.abs(Math.round(s)); const h = Math.floor(s/3600), m = Math.floor((s%3600)/60), x = s%60;
    return (neg?'-':'') + (h > 0 ? h + ':' + String(m).padStart(2,'0') : m) + ':' + String(x).padStart(2,'0'); };
  async function cmd(line) {
    try {
      const r = await fetch('/api/cmd', { method: 'POST', headers: { 'X-Patterns-Client': 'timer' }, body: line });
      const j = await r.json();
      el('err').textContent = j.ok ? '' : j.msg;
    } catch (e) { el('err').textContent = 'the desk did not answer'; }
  }
  function startCustom() {
    const v = el('mins').value.trim();
    if (!v) return;
    cmd(v.includes(':') ? 'TIMER TO ' + v : 'TIMER START ' + v);
  }
  function pauseResume() { cmd(state && state.timer.paused ? 'TIMER RESUME' : 'TIMER PAUSE'); }
  function send(channel) {
    const v = el('text').value.trim();
    if (!v) return;
    cmd((channel === 'crew' ? 'STAGE CREW ' : 'STAGE MESSAGE ') + v);
    el('text').value = '';
  }
  function render() {
    if (!state) return;
    const t = state.timer, now = Date.now() + skew;
    let remaining = t.remaining;
    if (t.phase === 'running' || t.phase === 'over') remaining = t.remaining - (now - Date.parse(state.serverUtc)) / 1000;
    const time = el('time');
    time.className = 'time ' + (t.phase === 'idle' ? 'off' : (remaining <= t.red ? 'red' : remaining <= t.amber ? 'amber' : 'green'));
    time.textContent = t.phase === 'idle' ? '--:--' : fmt(remaining);
    el('tline').textContent = t.phase === 'idle' ? 'no timer running' : t.paused ? 'PAUSED — ' + (t.label || '') : (t.label || '') + ' · amber at ' + fmt(t.amber) + ', red at ' + fmt(t.red);
    el('pause').textContent = t.paused ? 'RESUME' : 'PAUSE';
    const s = state.segment;
    el('seg').textContent = s.name ? ('NOW ' + (s.number ? s.number + ' ' : '') + s.name + (s.remaining !== null ? ' · ' + (s.overran ? 'over' : fmt(s.remaining) + ' left') : '') + (s.next ? ' · NEXT ' + s.next + (s.nextAt ? ' at ' + s.nextAt : '') : '') + (s.offset ? ' · ' + s.offset : '')) : '';
    el('presets').innerHTML = state.presets.map(p => '<button onclick="cmd(\'STAGE MESSAGE ' + p.replace(/'/g, '') + '\')">' + p.replace(/</g,'&lt;') + '</button>').join('');
    el('msgs').innerHTML = state.messages.slice().reverse().map(m => '<div><span>' + (m.channel === 'crew' ? 'CREW · ' : '') + m.text.replace(/</g,'&lt;') + '</span><span style="flex:1"></span><span class="' + (m.seen ? 'seen' : 'wait') + '">' + (m.seen ? 'seen ' + new Date(m.ackUtc).toLocaleTimeString() : 'waiting…') + '</span></div>').join('') || '<div><span style="color:var(--mut)">nothing sent yet</span></div>';
  }
  async function poll() {
    try {
      const r = await fetch('/api/stage?since=' + rev, { cache: 'no-store' });
      const s = await r.json();
      skew = Date.parse(s.serverUtc) - Date.now();
      rev = s.rev; state = s; render(); el('err').textContent = '';
    } catch (e) { el('err').textContent = 'reconnecting…'; await new Promise(r => setTimeout(r, 1500)); }
    poll();
  }
  setInterval(render, 250);
  poll();
</script>
</body>
</html>
""";
}
