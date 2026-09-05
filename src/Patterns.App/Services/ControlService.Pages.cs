namespace Patterns.App.Services;

/// <summary>
/// The phone / tablet remote — one page, embedded so there is no file to lose: a sticky header
/// that names what is on air with its chips and a connection dot, a menu of seven tabs (SHOW,
/// CUES, LOOKS, SCREENS, AUDIO, LOWER THIRDS, SETUP) that every phone remembers, and controls
/// big enough for a thumb at the tech table. Every button sends the same one-line command the
/// TCP port takes; the state comes back on the long-poll the caller's page uses, so the page
/// changes within the push throttle instead of polling.
/// </summary>
public sealed partial class ControlService
{
    private const string RemotePage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<title>Patterns Remote</title>
<style>
  :root { --bg:#0D0F14; --panel:#151A22; --line:#2A313E; --text:#E8ECF2; --mut:#98A1B1;
          --acc:#3EC1F3; --good:#2EE68A; --bad:#F0524D; --hold:#FFC24D; --pgm:#E0342E; }
  * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
  html, body { margin:0; background:var(--bg); color:var(--text); font:17px/1.35 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; }
  header { position:sticky; top:0; z-index:5; background:var(--bg); border-bottom:1px solid var(--line); }
  .top { display:flex; align-items:center; gap:10px; padding:10px 12px 6px; }
  .top .brand { font-size:13px; letter-spacing:.14em; color:var(--mut); font-weight:800; }
  .top .air { flex:1; font-size:20px; font-weight:800; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .dot { width:10px; height:10px; border-radius:50%; background:var(--bad); flex:none; }
  .dot.ok { background:var(--good); }
  .chips { display:flex; gap:6px; flex-wrap:wrap; padding:0 12px 8px; min-height:8px; }
  .chip { display:none; font-size:12px; font-weight:800; letter-spacing:.08em; border-radius:6px; padding:3px 7px; }
  .chip.on { display:inline-block; }
  .c-bo { background:#000; color:var(--pgm); border:1px solid var(--pgm); }
  .c-hold { background:var(--hold); color:#0E0F13; }
  .c-armed { background:#3A2E10; color:var(--hold); border:1px solid var(--hold); }
  .c-music { background:#10303A; color:var(--acc); border:1px solid var(--acc); }
  .c-off { background:#2A313E; color:var(--mut); }
  nav { display:flex; overflow-x:auto; border-top:1px solid var(--line); scrollbar-width:none; }
  nav::-webkit-scrollbar { display:none; }
  nav button { flex:1 0 auto; min-width:84px; min-height:0; border:0; border-bottom:3px solid transparent; border-radius:0; background:transparent; color:var(--mut); font:inherit; font-size:12px; letter-spacing:.12em; font-weight:800; padding:12px 10px; }
  nav button.on { color:var(--text); border-bottom-color:var(--acc); }
  main { padding:12px 12px 48px; }
  section { display:none; }
  section.on { display:block; }
  .sec { margin:16px 0 8px; font-size:11px; letter-spacing:.16em; color:var(--mut); font-weight:800; }
  section > .sec:first-child { margin-top:4px; }
  .grid { display:grid; gap:10px; }
  .row2 { grid-template-columns:1fr 1fr; }
  .row3 { grid-template-columns:1fr 1fr 1fr; }
  button { border:1px solid var(--line); border-radius:12px; background:var(--panel); color:var(--text); font:inherit; font-weight:700; padding:16px 10px; min-height:56px; cursor:pointer; }
  button:active { background:#20242E; }
  button:disabled { opacity:.35; }
  .big { font-size:22px; padding:24px 10px; min-height:72px; }
  .go { background:#0F3D2A; border-color:#1E6A4A; }
  .stop { background:#3A2020; border-color:#6A3A3A; }
  .bo { background:#3A0F0F; border-color:var(--bad); color:#FFB0B0; }
  .bo.on { background:var(--bad); color:#fff; }
  .lit { border-color:var(--good); color:var(--good); }
  .warm { border-color:var(--hold); color:var(--hold); }
  #duck.on, #hold.on { background:var(--hold); color:#0E0F13; }
  .look { padding:18px 6px; }
  .k { display:block; font-size:11px; color:var(--acc); font-weight:800; }
  .scr { display:grid; grid-template-columns:1fr 56px; gap:6px; }
  .scr button:last-child { padding:6px; font-size:20px; }
  .scr.off > button:first-child { opacity:.45; }
  .scr.locked > button:last-child { border-color:var(--hold); color:var(--hold); }
  .card { background:var(--panel); border:1px solid var(--line); border-radius:12px; padding:12px; }
  .card.standby { border-color:var(--good); border-width:2px; }
  .card .n { font-size:26px; font-weight:800; }
  .num { color:var(--mut); font-family:ui-monospace,Menlo,Consolas,monospace; margin-right:8px; }
  .notes { color:var(--hold); margin-top:4px; }
  .rows div { display:flex; gap:10px; padding:6px 0; border-top:1px solid var(--line); font-size:15px; }
  .rows div:first-child { border-top:none; }
  .rows .bad { color:var(--pgm); font-weight:700; }
  #gobtn { background:#1E9E5A; border-color:#1E9E5A; color:#fff; font-size:24px; }
  #gobtn.confirm { background:var(--hold); color:#0E0F13; }
  .cuerow { display:grid; grid-template-columns:1fr 1fr 2fr 1fr; gap:8px; margin-top:10px; }
  .line { margin-top:6px; font-size:14px; color:var(--mut); }
  .center { text-align:center; }
  #err { position:fixed; left:0; right:0; bottom:0; padding:6px; background:var(--bg); color:var(--bad); font-size:13px; min-height:18px; text-align:center; }
  a.link { display:flex; align-items:center; justify-content:center; min-height:56px; border:1px solid var(--line); border-radius:12px; color:var(--acc); text-decoration:none; font-weight:700; background:var(--panel); }
  .card + .card { margin-top:10px; }
</style>
</head>
<body>
<header>
  <div class="top"><span class="brand">PATTERNS</span><span class="air" id="air">—</span><span class="dot" id="dot"></span></div>
  <div class="chips">
    <span class="chip c-off" id="clive">OUTPUTS OFF</span>
    <span class="chip c-bo" id="cbo">BLACKOUT</span>
    <span class="chip c-hold" id="chold">HOLD</span>
    <span class="chip c-armed" id="carmed">ARMED</span>
    <span class="chip c-music" id="cmusic">♪ MUSIC</span>
    <span class="chip c-hold" id="csting">STING HOLD</span>
    <span class="chip c-hold" id="cduck">DUCK</span>
  </div>
  <nav id="nav">
    <button data-tab="show">SHOW</button>
    <button data-tab="cues">CUES</button>
    <button data-tab="looks">LOOKS</button>
    <button data-tab="screens">SCREENS</button>
    <button data-tab="audio">AUDIO</button>
    <button data-tab="lower">LOWER THIRDS</button>
    <button data-tab="setup">SETUP</button>
  </nav>
</header>
<main>

<section id="tab-show">
  <div class="sec">PRESENTER</div>
  <div class="grid row2">
    <button class="big" onclick="cmd('PREV')">⟨ Back</button>
    <button class="big go" onclick="cmd('NEXT')">Next ⟩</button>
  </div>
  <div id="step" class="line center"></div>
  <div class="sec">TRANSPORT</div>
  <div class="grid row3">
    <button class="go" onclick="cmd('OUTPUTS ON')">OUTPUTS ON</button>
    <button class="stop" onclick="cmd('OUTPUTS OFF')">OUTPUTS OFF</button>
    <button onclick="cmd('IDENTIFY')">IDENTIFY</button>
  </div>
  <div class="grid" style="margin-top:10px"><button id="bo" class="bo big" onclick="cmd('BLACKOUT TOGGLE')">BLACKOUT</button></div>
  <div class="grid row2" style="margin-top:10px">
    <button id="duck" class="big" onclick="cmd('DUCK TOGGLE')" title="Everything but a VOG makes way for an announcement from the room — press again to lift it">DUCK</button>
    <button id="stopall" class="big stop" onclick="stopAll()">STOP ALL</button>
  </div>
  <div id="nowrow" class="grid" style="margin-top:10px"></div>
  <div class="line">STOP ALL stops the audio track, break music, VOGs, stingers and the tone — never the outputs, blackout or the stream. Press it twice.</div>
  <div class="sec">FREEZE · FADE</div>
  <div class="grid row3">
    <button id="freeze" class="big" onclick="cmd('FREEZE TOGGLE')" title="Every output holds its frame until you press again">FREEZE</button>
    <button class="big bo" onclick="cmd('FADE 2')" title="Blackout, faded over two seconds">FADE ↓ 2 S</button>
    <button class="big" onclick="cmd('FADEUP 2')" title="The blackout lifted over two seconds">FADE ↑ 2 S</button>
  </div>
  <div class="sec">REVIEW</div>
  <div class="grid"><button id="review" onclick="cmd('REVIEW TOGGLE')">PREVIEW ON THE MULTIVIEW</button></div>
  <div class="line">Every multiview shows what the desk is building until you switch it off — the audience's screens do not change.</div>
</section>

<section id="tab-cues">
  <div class="card standby">
    <div class="sec" style="margin-top:0">STANDBY</div>
    <div class="n" id="sb">No cue on standby</div>
    <div class="notes" id="sbnotes"></div>
    <div class="line" id="sbplan"></div>
  </div>
  <div class="cuerow">
    <button id="up" onclick="cmd('CUE STANDBY PREV')">▲</button>
    <button id="down" onclick="cmd('CUE STANDBY NEXT')">▼</button>
    <button id="gobtn" onclick="go()">GO</button>
    <button id="hold" onclick="hold()">HOLD</button>
  </div>
  <div class="line" id="timing"></div>
  <div class="grid row2" style="margin-top:10px">
    <button id="arm" onclick="arm()">ARM</button>
    <a class="link" href="/run">The caller's page ⟩</a>
  </div>
  <div class="sec">NEXT</div>
  <div class="card rows" id="next"></div>
  <div class="sec">LAST</div>
  <div class="card rows" id="hist"></div>
</section>

<section id="tab-looks">
  <div class="sec">LOOKS — straight to air</div>
  <div class="grid"><button id="lookback" onclick="cmd('LOOKBACK')" title="The look that was on air before the current one, back on air">◀ PREVIOUS LOOK</button></div>
  <div id="looks" class="grid row3"></div>
</section>

<section id="tab-screens">
  <div class="sec">SCREENS — tap to switch, the padlock to lock</div>
  <div id="screens" class="grid"></div>
  <div class="sec" id="secsec" hidden>SHOW PARTS (PLAYLIST)</div>
  <div id="sections" class="grid row3"></div>
</section>

<section id="tab-audio">
  <div class="sec">AUDIO TRACK</div>
  <div class="grid row2">
    <button class="go" onclick="cmd('AUDIO PLAY')">▶ Play</button>
    <button class="stop" onclick="cmd('AUDIO STOP')">■ Stop</button>
  </div>
  <div class="sec" id="musicsec" hidden>BREAK MUSIC</div>
  <div id="musicctl" class="grid row3" hidden>
    <button class="go" onclick="cmd('MUSIC PLAY')">▶ Play</button>
    <button class="stop" onclick="cmd('MUSIC PAUSE')">❚❚ Pause</button>
    <button onclick="cmd('MUSIC NEXT')">⏭ Skip</button>
  </div>
  <div id="music" class="grid row2" style="margin-top:10px"></div>
  <div id="musicnow" class="line"></div>
  <div class="sec">VOG</div>
  <div id="vogs" class="grid row2"></div>
  <div class="sec">STINGERS</div>
  <div id="stings" class="grid row2"></div>
  <div id="stingnow" class="grid" style="margin-top:10px"></div>
  <div class="sec">SOUNDCHECK</div>
  <div class="grid row2">
    <button onclick="cmd('TONE ON')">TONE ON</button>
    <button onclick="cmd('TONE OFF')">TONE OFF</button>
  </div>
</section>

<section id="tab-lower">
  <div class="sec">DESIGNS</div>
  <div id="lts" class="grid row2"></div>
  <div id="ltnow" class="grid" style="margin-top:10px"></div>
  <div class="sec" id="peoplesec" hidden>PEOPLE — into the lower third on air</div>
  <div id="people" class="grid row2"></div>
</section>

<section id="tab-setup">
  <div class="card">
    <div class="sec" style="margin-top:0">THIS SHOW</div>
    <div id="showname" style="font-size:20px;font-weight:800"></div>
    <div id="health" class="line"></div>
    <div id="machine" class="line"></div>
    <div id="stream" class="line"></div>
    <div id="main" class="line"></div>
  </div>
  <div class="card">
    <div class="sec" style="margin-top:0">PAGES</div>
    <div class="grid row2">
      <a class="link" href="/run">Caller's page ⟩</a>
      <a class="link" href="/multiview">Multiview ⟩</a>
    </div>
  </div>
  <div class="card">
    <div class="sec" style="margin-top:0">THIS DEVICE</div>
    <div class="line" id="where"></div>
    <div class="line">Everything here goes straight to what the audience sees. A Stream Deck uses the Companion module; QLab and TouchOSC use OSC — both on the Remote page of the desk. No password: anyone on this network can drive the show while remote control is on.</div>
  </div>
</section>

</main>
<div id="err"></div>
<script>
var st = null, rev = 0, standbyId = '', stopArmedUntil = 0;
var TABS = ['show', 'cues', 'looks', 'screens', 'audio', 'lower', 'setup'];
function esc(s){ var d=document.createElement('div'); d.textContent=s==null?'':s; return d.innerHTML; }
function err(t){ document.getElementById('err').textContent = t || ''; }
function cmd(c) {
  return fetch('/api/cmd', { method:'POST', body:c, headers:{'X-Patterns-Client':'phone'} })
    .then(function(r){ return r.json(); })
    .then(function(j){ err(j.ok ? '' : j.msg); })
    .catch(function(){ err('Connection lost'); });
}
function go(){ if (standbyId) cmd('CUE GO ' + standbyId); }
function hold(){ var h = st && st.cuestack && st.cuestack.hold; cmd('CUE HOLD ' + (h ? 'OFF' : 'ON')); }
function arm(){ var a = st && st.cuestack && st.cuestack.armed; cmd('CUE ARM ' + (a ? 'OFF' : 'ON')); }
function stopAll() {
  var b = document.getElementById('stopall'), now = Date.now();
  if (now < stopArmedUntil) { stopArmedUntil = 0; b.textContent = 'STOP ALL'; cmd('STOPALL'); return; }
  stopArmedUntil = now + 3000; b.textContent = 'PRESS AGAIN';
  setTimeout(function(){ if (Date.now() >= stopArmedUntil) b.textContent = 'STOP ALL'; }, 3100);
}
function each(list, f){ Array.prototype.forEach.call(list, f); }
function show(tab) {
  each(document.querySelectorAll('nav button'), function(b){ b.classList.toggle('on', b.getAttribute('data-tab') === tab); });
  each(document.querySelectorAll('main section'), function(s){ s.classList.toggle('on', s.id === 'tab-' + tab); });
  try { localStorage.setItem('patterns.tab', tab); } catch (e) {}
  if (location.hash !== '#' + tab) { try { history.replaceState(null, '', '#' + tab); } catch (e) {} }
}
each(document.querySelectorAll('nav button'), function(b){ b.onclick = function(){ show(b.getAttribute('data-tab')); }; });
(function(){
  var t = (location.hash || '').replace('#', '');
  if (!t) { try { t = localStorage.getItem('patterns.tab') || ''; } catch (e) {} }
  show(TABS.indexOf(t) >= 0 ? t : 'show');
})();
function btn(html, cls, on){ var b = document.createElement('button'); b.innerHTML = html; if (cls) b.className = cls; b.onclick = on; return b; }
function fill(id, html){ document.getElementById(id).innerHTML = html; }
function render(s) {
  st = s; rev = s.rev || 0;
  var c = s.cuestack || {};
  // The header: what is on air, and the chips.
  document.getElementById('air').textContent = s.airLabel || '—';
  document.getElementById('clive').classList.toggle('on', !s.live);
  document.getElementById('cbo').classList.toggle('on', !!s.blackout);
  document.getElementById('chold').classList.toggle('on', !!c.hold);
  document.getElementById('carmed').classList.toggle('on', !!c.armed);
  document.getElementById('cmusic').classList.toggle('on', !!(s.music && s.music.playing));
  var cs = document.getElementById('csting');
  cs.classList.toggle('on', !!s.stingHold); cs.textContent = s.stingHold ? 'STING HOLD: ' + s.stingHold : 'STING HOLD';
  document.getElementById('cduck').classList.toggle('on', !!s.duck);

  // SHOW
  var p = s.presenter || { count: 0, index: -1, steps: [] };
  document.getElementById('step').textContent =
    p.count === 0 ? 'No presenter steps' :
    (p.index < 0 ? p.count + ' steps ready' : 'Step ' + (p.index + 1) + ' / ' + p.count + (p.steps[p.index] ? ' — ' + p.steps[p.index] : ''));
  var bo = document.getElementById('bo');
  bo.classList.toggle('on', !!s.blackout); bo.textContent = s.blackout ? 'BLACKOUT — ON' : 'BLACKOUT';
  var duck = document.getElementById('duck');
  duck.classList.toggle('on', !!s.duck); duck.textContent = s.duck ? 'DUCK — ON (lift)' : 'DUCK';
  var now = document.getElementById('nowrow'); now.innerHTML = '';
  if (s.stingHold) now.appendChild(btn('■ Holding: ' + esc(s.stingHold) + ' — put it back', 'stop', function(){ cmd('STINGER STOP'); }));
  else if (s.stingerPlaying) now.appendChild(btn('■ Stop: ' + esc(s.stingerPlaying), 'stop', function(){ cmd('STINGER STOP'); }));
  if (s.lowerThird) now.appendChild(btn('■ Hide lower third: ' + esc(s.lowerThird) + (s.lowerThirdPerson ? ' — ' + esc(s.lowerThirdPerson) : ''), 'stop', function(){ cmd('LT OFF'); }));
  var rv = document.getElementById('review');
  rv.classList.toggle('lit', !!s.review); rv.textContent = s.review ? 'REVIEW — ON: the preview fills every multiview' : 'PREVIEW ON THE MULTIVIEW';
  var fz = document.getElementById('freeze');
  fz.classList.toggle('on', !!s.frozen); fz.textContent = s.frozen ? 'FROZEN — release' : 'FREEZE';
  var lb = document.getElementById('lookback');
  lb.textContent = s.previousLook ? '◀ BACK TO: ' + s.previousLook : '◀ PREVIOUS LOOK';
  lb.disabled = !s.previousLook;

  // CUES
  var sb = c.standby; standbyId = sb ? sb.id : '';
  fill('sb', sb ? '<span class="num">' + esc(sb.number) + '</span>' + esc(sb.name) : 'No cue on standby');
  document.getElementById('sbnotes').textContent = sb ? (sb.notes || '') : '';
  var plan = [];
  if (sb && sb.plannedStart) plan.push('planned ' + sb.plannedStart);
  if (sb && sb.followSeconds != null) plan.push(sb.followSeconds === 0 ? 'follows at once' : 'follows after ' + sb.followSeconds + ' s');
  if (sb && sb.requireConfirm) plan.push('asks for a second GO');
  document.getElementById('sbplan').textContent = plan.join(' · ');
  var gob = document.getElementById('gobtn');
  gob.disabled = !(c.armed && sb);
  gob.classList.toggle('confirm', !!c.confirm);
  gob.textContent = c.confirm ? c.confirm : (sb ? 'GO ' + sb.number : 'GO');
  var hb = document.getElementById('hold');
  hb.disabled = !c.armed; hb.classList.toggle('on', !!c.hold);
  var ab = document.getElementById('arm');
  ab.textContent = c.armed ? 'DISARM' : 'ARM'; ab.classList.toggle('warm', !!c.armed);
  var t = c.timing || {}, tl = [];
  if (t.offset) tl.push(t.offset);
  if (t.nextBreak && t.nextBreak.text) tl.push(t.nextBreak.text);
  if (t.lunch && t.lunch.text) tl.push(t.lunch.text);
  if (t.end && t.end.text) tl.push(t.end.text);
  if (t.follow) tl.push(t.follow);
  document.getElementById('timing').textContent = tl.join(' · ');
  var nx = document.getElementById('next'); nx.innerHTML = '';
  (c.next || []).forEach(function(x){ var d = document.createElement('div'); d.innerHTML = '<span class="num">' + esc(x.number) + '</span>' + esc(x.name); nx.appendChild(d); });
  if (!c.next || c.next.length === 0) nx.innerHTML = '<div style="color:var(--mut)">end of the list</div>';
  var h = document.getElementById('hist'); h.innerHTML = '';
  (c.history || []).forEach(function(r){
    var d = document.createElement('div'), bad = /Failed|Refused/.test(r.outcome);
    d.innerHTML = '<span class="num">' + esc((r.at || '').slice(11, 19)) + '</span><span style="flex:1">' + esc(r.number + ' ' + r.name) + '</span><span class="' + (bad ? 'bad' : '') + '">' + esc(r.outcome) + '</span>';
    h.appendChild(d);
  });
  if (!c.history || c.history.length === 0) h.innerHTML = '<div style="color:var(--mut)">nothing yet</div>';

  // LOOKS
  var lk = document.getElementById('looks'); lk.innerHTML = '';
  (s.looks || []).forEach(function(l){
    lk.appendChild(btn((l.slot > 0 ? '<span class="k">F' + l.slot + '</span>' : '') + esc(l.name), 'look', function(){ cmd('LOOK ' + (l.slot > 0 ? l.slot : l.name)); }));
  });
  if (!s.looks || s.looks.length === 0) lk.innerHTML = '<button disabled>No looks saved</button>';

  // SCREENS — the main button switches, the padlock locks (a locked screen keeps its picture through looks and cues).
  var sc = document.getElementById('screens'); sc.innerHTML = '';
  (s.screens || []).forEach(function(x){
    var row = document.createElement('div');
    row.className = 'scr' + (x.enabled ? '' : ' off') + (x.locked ? ' locked' : '');
    var tag = (x.role && x.role !== 'main' ? ' <span class="k">' + esc(x.role.toUpperCase()) + '</span>' : '') + (x.group ? ' <span class="k">[' + esc(x.group) + ']</span>' : '');
    row.appendChild(btn(esc(x.n + ' · ' + x.label) + tag + (x.enabled ? '' : ' <span class="k">OFF</span>'), '', function(){ cmd('SCREEN ' + x.n + ' TOGGLE'); }));
    row.appendChild(btn(x.locked ? '🔒' : '🔓', '', function(){ cmd('LOCK ' + x.n + ' TOGGLE'); }));
    sc.appendChild(row);
  });
  var se = document.getElementById('sections'); se.innerHTML = '';
  var hasSections = s.sections && s.sections.length > 0;
  document.getElementById('secsec').hidden = !hasSections;
  (s.sections || []).forEach(function(x){ se.appendChild(btn(esc(x.name), x.active ? 'lit' : '', function(){ cmd('SECTION ' + x.n); })); });

  // AUDIO — one library, one numbering: both grids fire STINGER n.
  var m = s.music || {};
  document.getElementById('musicsec').hidden = !m.on;
  document.getElementById('musicctl').hidden = !m.on;
  var mu = document.getElementById('music'); mu.innerHTML = '';
  if (m.on) (m.items || []).forEach(function(x){ mu.appendChild(btn(esc(x.name), '', function(){ cmd('MUSIC PLAY ' + x.n); })); });
  document.getElementById('musicnow').textContent = !m.on ? '' : m.playing ? (m.now || 'Starting…') + (m.device ? ' · ' + m.device : '') : (m.status || 'Paused');
  var vg = document.getElementById('vogs'); vg.innerHTML = '';
  var sg = document.getElementById('stings'); sg.innerHTML = '';
  (s.stingers || []).forEach(function(x){
    var lit = s.stingerPlaying === x.name ? 'lit' : '';
    (x.kind === 'sting' ? sg : vg).appendChild(btn((x.kind === 'sting' ? '⚡ ' : '🔊 ') + esc(x.name), lit, function(){ cmd('STINGER ' + x.n); }));
  });
  if (!vg.children.length) vg.innerHTML = '<button disabled>No VOGs set up</button>';
  if (!sg.children.length) sg.innerHTML = '<button disabled>No stingers set up</button>';
  var sn = document.getElementById('stingnow'); sn.innerHTML = '';
  if (s.stingHold) sn.appendChild(btn('■ Holding: ' + esc(s.stingHold) + ' — put it back', 'stop', function(){ cmd('STINGER STOP'); }));
  else if (s.stingerPlaying) sn.appendChild(btn('■ Stop: ' + esc(s.stingerPlaying), 'stop', function(){ cmd('STINGER STOP'); }));

  // LOWER THIRDS — a design by number (page order); a person into the one on air.
  var lts = document.getElementById('lts'); lts.innerHTML = '';
  (s.lowerThirds || []).forEach(function(x){ lts.appendChild(btn(esc(x.name), s.lowerThird === x.name ? 'lit' : '', function(){ cmd('LT ' + x.n); })); });
  if (!s.lowerThirds || s.lowerThirds.length === 0) lts.innerHTML = '<button disabled>No designs yet — build one on the Lower thirds page</button>';
  var ln = document.getElementById('ltnow'); ln.innerHTML = '';
  if (s.lowerThird) ln.appendChild(btn('■ Hide: ' + esc(s.lowerThird) + (s.lowerThirdPerson ? ' — ' + esc(s.lowerThirdPerson) : ''), 'stop', function(){ cmd('LT OFF'); }));
  var pe = document.getElementById('people'); pe.innerHTML = '';
  var peopleList = s.people || [];
  document.getElementById('peoplesec').hidden = peopleList.length === 0;
  peopleList.forEach(function(x){
    pe.appendChild(btn(esc(x.name) + (x.role ? '<br><span class="k">' + esc(x.role) + '</span>' : ''), s.lowerThirdPerson === x.name ? 'lit' : '', function(){ cmd('PERSON ' + x.n); }));
  });

  // SETUP
  document.getElementById('showname').textContent = s.show || 'Patterns';
  document.getElementById('health').textContent = s.health || '';
  var mc = s.machine || {};
  document.getElementById('machine').textContent = 'CPU ' + (mc.cpu >= 0 ? mc.cpu + '%' : 'n/a') + ' · RAM ' + (mc.ram >= 0 ? mc.ram + '%' : 'n/a') + ' · ' + (mc.fps || 0) + ' fps · ' + (mc.battery ? 'ON BATTERY' : 'mains') + (mc.advice ? ' · ' + mc.advice + ' advice' : '');
  var stream = s.stream || {};
  document.getElementById('stream').textContent = 'Stream: ' + (stream.active ? 'ON — ' : 'off — ') + (stream.status || '');
  var bc = s.beacon || {};
  document.getElementById('main').textContent = bc.main ? bc.main : (bc.sending ? 'Beacon: sending' : '');
  document.getElementById('where').textContent = 'Connected to ' + location.host + ' · state ' + rev;
}
function poll() {
  fetch('/api/state?since=' + rev).then(function(r){ return r.json(); })
    .then(function(s){ render(s); document.getElementById('dot').classList.add('ok'); err(''); poll(); })
    .catch(function(){ document.getElementById('dot').classList.remove('ok'); err('Connection lost — retrying…'); setTimeout(poll, 1500); });
}
fetch('/api/state').then(function(r){ return r.json(); })
  .then(function(s){ render(s); document.getElementById('dot').classList.add('ok'); poll(); })
  .catch(function(){ err('Connection lost — retrying…'); setTimeout(poll, 1500); });
</script>
</body>
</html>
""";
}
