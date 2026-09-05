namespace Patterns.App.Services;

/// <summary>
/// The ADMIN page of the web remote — behind the Install page's passcode: the health line, the
/// schedule's switch and what it is doing, every announcement and advert as a key, a free-text
/// announcement, RESTART, the staged update and APPLY, the support bundle to download, the log's
/// tail, and a console for any line of the protocol. Every button posts "passcode\ncommand" to
/// /api/admin; the passcode stays in this browser's session storage and goes no further.
/// </summary>
public sealed partial class ControlService
{
    private const string AdminPage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Patterns Admin</title>
<style>
  :root { --bg:#0D0F14; --panel:#151A22; --line:#2A313E; --text:#E8ECF2; --mut:#98A1B1; --acc:#3EC1F3; --good:#2EE68A; --bad:#F0524D; --hold:#FFC24D; }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--text); font:16px/1.4 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; padding:12px; max-width:900px; margin:0 auto; }
  h1 { font-size:14px; letter-spacing:.16em; color:var(--mut); margin:8px 0 12px; }
  .sec { margin:18px 0 8px; font-size:11px; letter-spacing:.16em; color:var(--mut); font-weight:800; }
  .card { background:var(--panel); border:1px solid var(--line); border-radius:12px; padding:12px; }
  .card + .card { margin-top:10px; }
  .line { margin-top:6px; font-size:14px; color:var(--mut); white-space:pre-wrap; }
  .grid { display:grid; gap:10px; grid-template-columns:1fr 1fr; }
  .grid3 { grid-template-columns:1fr 1fr 1fr; }
  button, input { border:1px solid var(--line); border-radius:10px; background:var(--panel); color:var(--text); font:inherit; padding:12px 10px; min-height:48px; }
  button { font-weight:700; cursor:pointer; }
  button:active { background:#20242E; }
  button.go { background:#0F3D2A; border-color:#1E6A4A; }
  button.stop { background:#3A2020; border-color:#6A3A3A; }
  button.lit { border-color:var(--good); color:var(--good); }
  input { width:100%; }
  .row { display:flex; gap:8px; margin-top:8px; }
  .row input { flex:1; }
  pre { background:#0A0C10; border:1px solid var(--line); border-radius:10px; padding:10px; font:12px/1.4 ui-monospace,Menlo,Consolas,monospace; max-height:320px; overflow:auto; white-space:pre-wrap; }
  a { color:var(--acc); }
  #err { color:var(--bad); min-height:18px; margin-top:8px; }
  #gate { display:none; }
  #gate.on { display:block; }
  #main { display:none; }
  #main.on { display:block; }
</style>
</head>
<body>
<h1>PATTERNS · ADMIN</h1>
<div id="gate" class="card">
  <div>The passcode from the Install page (PLAN → Install → Remote admin):</div>
  <div class="row"><input id="pass" type="password" placeholder="passcode" autocomplete="current-password"><button class="go" onclick="unlock()">UNLOCK</button></div>
  <div id="gateerr" style="color:var(--bad);margin-top:8px"></div>
  <div class="line"><a href="/">⟨ The remote</a></div>
</div>
<div id="main">
  <div class="card">
    <div id="site" style="font-size:20px;font-weight:800"></div>
    <div id="health" class="line"></div>
    <div id="machine" class="line"></div>
    <div id="install" class="line"></div>
  </div>
  <div class="sec">THE SCHEDULE</div>
  <div class="grid">
    <button id="schon" onclick="cmd('SCHEDULE ON')">SCHEDULE ON</button>
    <button onclick="cmd('SCHEDULE OFF')">SCHEDULE OFF</button>
  </div>
  <div id="over" class="grid" style="margin-top:10px"></div>
  <div class="sec">ANNOUNCEMENTS</div>
  <div id="announcements" class="grid"></div>
  <div class="row"><input id="words" placeholder="The store closes in 15 minutes"><button onclick="announce()">ANNOUNCE</button></div>
  <div class="sec">ADVERTS</div>
  <div id="adverts" class="grid"></div>
  <div class="sec">THE APP</div>
  <div class="card">
    <div id="update" class="line"></div>
    <div id="updatelast" class="line"></div>
    <div id="mgmt" class="line"></div>
    <div class="grid grid3" style="margin-top:10px">
      <button id="apply" onclick="apply()">APPLY THE STAGED UPDATE</button>
      <button class="stop" onclick="restart()">RESTART THE APP</button>
      <button onclick="bundle()">SUPPORT BUNDLE ⇩</button>
    </div>
  </div>
  <div class="sec">CONSOLE — any line of the protocol (docs/REMOTE.md)</div>
  <div class="row"><input id="console" placeholder="LOOK Walk-in · BLACKOUT ON · STATUS"><button onclick="consoleSend()">SEND</button></div>
  <div id="answer" class="line"></div>
  <div class="sec">THE LOG</div>
  <pre id="log">…</pre>
  <div class="row"><button onclick="loadLog()">REFRESH THE LOG</button><button onclick="lock()">LOCK</button></div>
  <div class="line"><a href="/">⟨ The remote</a> · <a href="/run">the caller's page</a> · <a href="/multiview">the multiview</a></div>
</div>
<div id="err"></div>
<script>
var pass = '', st = null, rev = 0, polling = false;
try { pass = sessionStorage.getItem('patterns.admin') || ''; } catch (e) {}
function esc(s){ var d=document.createElement('div'); d.textContent=s==null?'':s; return d.innerHTML; }
function err(t){ document.getElementById('err').textContent = t || ''; }
function post(line) {
  return fetch('/api/admin', { method:'POST', body: pass + '\n' + line, headers:{'X-Patterns-Client':'admin'} })
    .then(function(r){ return r.json(); });
}
function cmd(line) {
  return post(line).then(function(j){ err(j.ok ? '' : j.msg); document.getElementById('answer').textContent = line + ' → ' + j.msg; if (/passcode|locked/.test(j.msg) && !j.ok) lock(); })
    .catch(function(){ err('Connection lost'); });
}
function unlock() {
  pass = document.getElementById('pass').value;
  post('').then(function(j){
    if (!j.ok) { document.getElementById('gateerr').textContent = j.msg; return; }
    try { sessionStorage.setItem('patterns.admin', pass); } catch (e) {}
    showMain(true); loadLog(); if (!polling) { polling = true; poll(); }
  }).catch(function(){ document.getElementById('gateerr').textContent = 'Connection lost'; });
}
function lock(){ pass = ''; try { sessionStorage.removeItem('patterns.admin'); } catch (e) {} showMain(false); }
function showMain(on){ document.getElementById('main').classList.toggle('on', on); document.getElementById('gate').classList.toggle('on', !on); }
function announce(){ var w = document.getElementById('words').value.trim(); if (w) cmd('ANNOUNCE ' + w); }
function apply(){ if (confirm('Apply the staged update? The screens go black for a few seconds while the watchdog swaps the files.')) cmd('UPDATE APPLY ' + pass); }
function restart(){ if (confirm('Restart the app? The show comes straight back.')) cmd('RESTART ' + pass); }
function bundle(){ window.location = '/support-bundle.zip?pass=' + encodeURIComponent(pass); }
function consoleSend(){ var l = document.getElementById('console').value.trim(); if (l) cmd(l); }
function loadLog(){ fetch('/api/admin/log?pass=' + encodeURIComponent(pass)).then(function(r){ return r.text(); }).then(function(t){ document.getElementById('log').textContent = t; }).catch(function(){}); }
function btn(html, cls, on){ var b = document.createElement('button'); b.innerHTML = html; if (cls) b.className = cls; b.onclick = on; return b; }
function render(s) {
  st = s; rev = s.rev || 0;
  var i = s.install || {};
  document.getElementById('site').textContent = (i.site || s.show || 'Patterns') + ' — ' + (s.airLabel || '—');
  document.getElementById('health').textContent = s.health || '';
  var mc = s.machine || {};
  document.getElementById('machine').textContent = 'CPU ' + (mc.cpu >= 0 ? mc.cpu + '%' : 'n/a') + ' · RAM ' + (mc.ram >= 0 ? mc.ram + '%' : 'n/a') + ' · ' + (mc.fps || 0) + ' fps · ' + (mc.battery ? 'ON BATTERY' : 'mains') + (mc.advice ? ' · ' + mc.advice + ' advice' : '');
  document.getElementById('install').textContent = i.status || '';
  document.getElementById('schon').classList.toggle('lit', !!i.on);
  var ov = document.getElementById('over'); ov.innerHTML = '';
  if (i.over) ov.appendChild(btn('■ END NOW: ' + esc(i.overKind + ' ' + i.over) + ' (until ' + esc(i.overUntil) + ')', 'stop', function(){ cmd(i.overKind === 'advert' ? 'ADVERT OFF' : 'ANNOUNCE OFF'); }));
  var an = document.getElementById('announcements'); an.innerHTML = '';
  var ad = document.getElementById('adverts'); ad.innerHTML = '';
  (i.slots || []).forEach(function(x){
    if (x.kind === 'announcement') an.appendChild(btn(esc(x.name) + (x.status ? '<br><span style="font-size:11px;color:var(--mut)">' + esc(x.status) + '</span>' : ''), '', function(){ cmd('ANNOUNCE ' + x.name); }));
    if (x.kind === 'advert') ad.appendChild(btn(esc(x.name) + (x.status ? '<br><span style="font-size:11px;color:var(--mut)">' + esc(x.status) + '</span>' : ''), '', function(){ cmd('ADVERT ' + x.name); }));
  });
  if (!an.children.length) an.innerHTML = '<button disabled>No announcements on the Install page</button>';
  if (!ad.children.length) ad.innerHTML = '<button disabled>No adverts on the Install page</button>';
  var u = i.update || {};
  document.getElementById('update').textContent = u.status || '';
  document.getElementById('updatelast').textContent = u.last || '';
  document.getElementById('apply').disabled = !(u.ok && u.supervised);
  document.getElementById('mgmt').textContent = i.management || '';
}
function poll() {
  fetch('/api/state?since=' + rev).then(function(r){ return r.json(); })
    .then(function(s){ render(s); err(''); poll(); })
    .catch(function(){ err('Connection lost — retrying…'); setTimeout(poll, 1500); });
}
fetch('/api/state').then(function(r){ return r.json(); }).then(function(s){ render(s); });
if (pass) { unlock(); } else { showMain(false); }
</script>
</body>
</html>
""";
}
