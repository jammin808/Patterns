namespace Patterns.App.Services;

/// <summary>The audience's page and the host's: joined by a nickname, a token kept in the browser, the room polled as it moves.</summary>
public sealed partial class ControlService
{
    private const string PlayPage = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1">
<title>Patterns Play</title>
<style>
  html,body{margin:0;background:#0b0c10;color:#e6e9ef;font:17px/1.35 system-ui,sans-serif;-webkit-user-select:none;user-select:none}
  .wrap{max-width:560px;margin:0 auto;padding:14px;box-sizing:border-box;display:flex;flex-direction:column;gap:12px}
  .card{background:#141720;border:1px solid #232a38;border-radius:14px;padding:14px}
  h1{font-size:22px;margin:0 0 6px}
  h2{font-size:15px;margin:0 0 8px;color:#9aa4b5;letter-spacing:.06em;text-transform:uppercase}
  input,textarea{width:100%;box-sizing:border-box;padding:12px;border-radius:10px;border:1px solid #2a3140;background:#0f1118;color:#e6e9ef;font-size:17px}
  button{padding:12px 14px;border:none;border-radius:12px;background:#1c2130;color:#e6e9ef;font-size:17px;font-weight:600}
  button.big{width:100%;text-align:left;margin-top:8px;padding:16px;font-size:19px}
  button.on{background:#e0ff5f;color:#111}
  button.right{background:#7cf5c8;color:#111}
  button.wrong{background:#ff5c7a;color:#111}
  .row{display:flex;gap:8px;flex-wrap:wrap}
  .muted{color:#9aa4b5;font-size:14px}
  .toast{background:#e0ff5f;color:#111;border-radius:12px;padding:12px;font-weight:600}
  .bar{height:10px;border-radius:5px;background:#5fd0ff;margin:4px 0 10px}
  .grid8{display:grid;grid-template-columns:repeat(8,1fr);gap:2px;margin-top:8px}
  .grid8 button{padding:0;aspect-ratio:1;border-radius:4px;font-size:20px}
  .dark{background:#2a313e}.light{background:#c0cbdb}
  .sel{outline:3px solid #e0ff5f}
  .hidden{display:none}
</style></head>
<body><div class="wrap">
  <div class="card" id="join">
    <h1 id="title">Join in</h1>
    <div class="muted" id="roomline"></div>
    <input id="nick" placeholder="Your name" maxlength="20" autocomplete="off">
    <input id="group" placeholder="Your table or team (optional)" maxlength="20" autocomplete="off" style="margin-top:8px">
    <button class="big on" id="joinbtn">JOIN</button>
    <div class="muted" id="joinmsg"></div>
  </div>
  <div id="live" class="hidden">
    <div class="card"><div class="row" style="justify-content:space-between;align-items:center"><div><b id="me"></b> <span class="muted" id="score"></span></div><div class="muted" id="players"></div></div></div>
    <div id="toasts"></div>
    <div class="card" id="qcard"><h2 id="qkind">Question</h2><div id="qtext" style="font-size:20px;font-weight:600"></div><div id="qbody"></div><div class="muted" id="qfoot"></div></div>
    <div class="card hidden" id="pathcard"><h2>The path</h2><div id="pathtext"></div><div id="pathopts"></div><div class="muted" id="pathfoot"></div></div>
    <div class="card hidden" id="drcard"><h2>Draughts</h2><div class="muted" id="drwords"></div><div class="row" id="drseats"></div><div class="grid8" id="drboard"></div></div>
    <div class="card"><h2>Say something</h2><div class="row"><input id="say" placeholder="A word for the wall — the host sees it first" maxlength="60"><button id="saybtn">SEND</button></div><div class="muted" id="saymsg"></div></div>
    <div class="card"><h2>Leaders</h2><div id="lead" class="muted">no points yet</div></div>
  </div>
</div>
<script>
  const qs = new URLSearchParams(location.search);
  const roomWanted = (qs.get('room') || '').toUpperCase();
  let token = localStorage.getItem('play.token') || '';
  let since = 0, rev = -1, multi = new Set(), drFrom = -1, myVote = -1, stopped = false;
  const $ = id => document.getElementById(id);
  const post = (url, body) => fetch(url, { method: 'POST', body: JSON.stringify(body) }).then(r => r.json());
  $('roomline').textContent = roomWanted ? 'Room ' + roomWanted : '';
  $('nick').value = localStorage.getItem('play.nick') || '';
  $('group').value = localStorage.getItem('play.group') || '';
  $('joinbtn').onclick = async () => {
    const j = await post('/api/play/join', { nick: $('nick').value, group: $('group').value, token, room: roomWanted });
    if (!j.ok) { $('joinmsg').textContent = j.msg; return; }
    token = j.token; localStorage.setItem('play.token', token); localStorage.setItem('play.nick', j.nick); localStorage.setItem('play.group', j.group);
    $('join').classList.add('hidden'); $('live').classList.remove('hidden'); document.title = j.show || 'Patterns Play';
    poll();
  };
  if (token) $('joinbtn').textContent = 'JOIN AGAIN';
  function toast(text) { const t = document.createElement('div'); t.className = 'toast'; t.textContent = text; $('toasts').prepend(t); setTimeout(() => t.remove(), 12000); }
  function render(s) {
    $('me').textContent = s.nick + (s.group ? ' · ' + s.group : '');
    $('score').textContent = s.score ? s.score + ' pts' : '';
    $('players').textContent = s.players + ' in the room';
    for (const m of s.messages) { toast(m.text); since = Math.max(since, m.seq); }
    const q = s.question, body = $('qbody');
    if (!q) { $('qkind').textContent = 'Waiting'; $('qtext').textContent = 'The host has not opened a question yet.'; body.innerHTML = ''; $('qfoot').textContent = ''; }
    else {
      $('qkind').textContent = q.kind === 'quiz' ? 'Quiz' : q.kind === 'words' ? 'One word' : q.kind === 'scale' ? 'Rate it' : 'Poll';
      $('qtext').textContent = q.text;
      body.innerHTML = '';
      const open = q.state === 'open', mine = q.mine;
      if (q.kind === 'choice' || q.kind === 'quiz' || q.kind === 'multi') {
        q.options.forEach((o, i) => {
          const b = document.createElement('button'); b.className = 'big';
          const pct = q.results ? ' — ' + q.results.percent[i] + '%' : '';
          b.textContent = String.fromCharCode(65 + i) + '. ' + o + pct;
          if (mine && mine.choices.includes(i)) b.classList.add(q.correct >= 0 ? (i === q.correct ? 'right' : 'wrong') : 'on');
          if (q.correct === i) b.classList.add('right');
          if (q.kind === 'multi' && multi.has(i)) b.classList.add('on');
          b.disabled = !open || (q.kind === 'quiz' && !!mine);
          b.onclick = () => { if (q.kind === 'multi') { multi.has(i) ? multi.delete(i) : multi.add(i); render(s); } else answer({ choices: [i] }); };
          body.appendChild(b);
        });
        if (q.kind === 'multi' && open) { const b = document.createElement('button'); b.className = 'big on'; b.textContent = 'SEND'; b.onclick = () => answer({ choices: [...multi] }); body.appendChild(b); }
      } else if (q.kind === 'scale') {
        const row = document.createElement('div'); row.className = 'row';
        for (let v = q.scaleMin; v <= q.scaleMax; v++) { const b = document.createElement('button'); b.textContent = v; if (mine && mine.scale === v) b.classList.add('on'); b.disabled = !open; b.onclick = () => answer({ scale: v }); row.appendChild(b); }
        body.appendChild(row);
        if (q.results && q.results.average) { const d = document.createElement('div'); d.className = 'muted'; d.textContent = 'average ' + q.results.average; body.appendChild(d); }
      } else if (q.kind === 'words') {
        const inp = document.createElement('input'); inp.placeholder = 'a word or two'; inp.maxLength = 60; inp.disabled = !open; inp.value = mine ? mine.words : '';
        const b = document.createElement('button'); b.className = 'big on'; b.textContent = 'SEND'; b.disabled = !open; b.onclick = () => answer({ words: inp.value });
        body.appendChild(inp); body.appendChild(b);
        if (q.results) { const d = document.createElement('div'); d.className = 'muted'; d.textContent = q.results.words.map(w => w.word + ' ×' + w.count).join(' · '); body.appendChild(d); }
      }
      let foot = q.answers + ' answered';
      if (q.kind === 'quiz' && open) foot += ' · ' + Math.ceil(q.secondsLeft) + ' s';
      if (mine && mine.points) foot += ' · you scored ' + mine.points;
      if (q.state === 'closed') foot += ' · closed'; if (q.state === 'revealed') foot += ' · revealed';
      $('qfoot').textContent = foot;
    }
    const p = s.path; $('pathcard').classList.toggle('hidden', !(s.wall === 'path' || p.open));
    if (p) { $('pathtext').textContent = p.text; const po = $('pathopts'); po.innerHTML = ''; p.options.forEach((o, i) => { const b = document.createElement('button'); b.className = 'big'; b.textContent = (i + 1) + '. ' + o; if (myVote === i) b.classList.add('on'); b.disabled = !p.open; b.onclick = async () => { myVote = i; const r = await post('/api/play/vote', { token, option: i }); $('pathfoot').textContent = r.msg; render(s); }; po.appendChild(b); }); $('pathfoot').textContent = p.end ? 'The end.' : p.open ? 'Vote now' : (p.chose ? 'The room chose: ' + p.chose : 'The host opens the vote'); }
    const d = s.draughts; $('drcard').classList.toggle('hidden', s.wall !== 'draughts' && d.mine === 'none');
    if (d) {
      $('drwords').textContent = d.words + (d.mine !== 'none' ? ' · you are ' + d.mine : '');
      const seats = $('drseats'); seats.innerHTML = '';
      for (const side of ['black', 'white']) { const b = document.createElement('button'); b.textContent = side.toUpperCase() + (side === 'black' && d.black ? ' · ' + d.black : side === 'white' && d.white ? ' · ' + d.white : ''); if (d.mine === side) b.classList.add('on'); b.onclick = () => post('/api/play/draughts', { token, action: 'seat', side }).then(poll); seats.appendChild(b); }
      const g = $('drboard'); g.innerHTML = '';
      for (let i = 0; i < 64; i++) { const c = d.board[i]; const b = document.createElement('button'); const dark = (Math.floor(i / 8) + i % 8) % 2 === 1; b.className = dark ? 'dark' : 'light'; b.textContent = c === 'b' ? '●' : c === 'B' ? '♚' : c === 'w' ? '○' : c === 'W' ? '♔' : ''; b.style.color = (c === 'b' || c === 'B') ? '#ff5c7a' : '#fff'; if (i === drFrom) b.classList.add('sel');
        b.onclick = async () => { if (drFrom < 0) { drFrom = i; render(s); } else { const from = drFrom; drFrom = -1; const r = await post('/api/play/draughts', { token, action: 'move', from, to: i }); $('drwords').textContent = r.msg; poll(); } };
        g.appendChild(b); }
    }
    $('lead').textContent = s.leaderboard.length ? s.leaderboard.map((l, i) => (i + 1) + '. ' + l.nick + ' ' + l.score).join(' · ') : 'no points yet';
  }
  async function answer(a) {
    const s = window.__state; if (!s || !s.question) return;
    const r = await post('/api/play/answer', Object.assign({ token, question: s.question.id }, a));
    $('qfoot').textContent = r.msg === 'ok' ? 'Answered' : r.msg === 'queued' ? 'Sent — the host sees it first' : r.msg;
    poll();
  }
  $('saybtn').onclick = async () => { const r = await post('/api/play/say', { token, text: $('say').value }); $('saymsg').textContent = r.msg; if (r.ok) $('say').value = ''; };
  let polling = false;
  async function poll() {
    if (polling) return; polling = true;
    try {
      const r = await fetch('/api/play/state?token=' + encodeURIComponent(token) + '&since=' + since + '&rev=' + rev, { cache: 'no-store' });
      const s = await r.json(); window.__state = s; rev = s.rev;
      if (!s.known) { $('join').classList.remove('hidden'); $('live').classList.add('hidden'); $('joinmsg').textContent = 'Join the room ' + s.room; polling = false; return; }
      render(s);
    } catch (e) { await new Promise(r => setTimeout(r, 2000)); }
    polling = false; if (!stopped) poll();
  }
  if (token && !roomWanted) $('joinbtn').click();
</script></body></html>
""";

    private const string HostPage = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Patterns Play — host</title>
<style>
  html,body{margin:0;background:#0b0c10;color:#e6e9ef;font:15px/1.4 system-ui,sans-serif}
  .wrap{max-width:900px;margin:0 auto;padding:14px;display:flex;flex-direction:column;gap:12px}
  .card{background:#141720;border:1px solid #232a38;border-radius:12px;padding:12px}
  h2{font-size:13px;margin:0 0 8px;color:#9aa4b5;letter-spacing:.06em;text-transform:uppercase}
  input,textarea{width:100%;box-sizing:border-box;padding:9px;border-radius:8px;border:1px solid #2a3140;background:#0f1118;color:#e6e9ef;font-size:15px}
  button{padding:8px 12px;border:none;border-radius:8px;background:#1c2130;color:#e6e9ef;font-weight:600;cursor:pointer}
  button.on{background:#e0ff5f;color:#111}button.no{background:#ff5c7a;color:#111}button.ok{background:#7cf5c8;color:#111}
  .row{display:flex;gap:6px;flex-wrap:wrap;align-items:center}
  .muted{color:#9aa4b5;font-size:13px}
  .q{border-top:1px solid #232a38;padding:8px 0}
  .code{font-size:34px;font-weight:800;color:#e0ff5f;letter-spacing:.1em}
  .hidden{display:none}
</style></head>
<body><div class="wrap">
  <div class="card" id="gate"><h2>Host</h2><div class="row"><input id="pass" type="password" placeholder="Admin passcode (Install page)" style="max-width:320px"><button class="on" id="unlock">OPEN</button><span class="muted" id="gatemsg"></span></div></div>
  <div id="host" class="hidden">
    <div class="card"><div class="row" style="justify-content:space-between"><div><span class="code" id="code"></span> <span class="muted" id="url"></span></div><div class="muted" id="people"></div></div>
      <div class="row" style="margin-top:8px"><span class="muted">Wall:</span>
        <button data-v="PLAY SHOW join">JOIN</button><button data-v="PLAY SHOW results">RESULTS</button><button data-v="PLAY SHOW leaderboard">LEADERBOARD</button><button data-v="PLAY SHOW message">MESSAGE</button><button data-v="PLAY SHOW draughts">DRAUGHTS</button><button data-v="PLAY SHOW path">PATH</button><button data-v="PLAY HIDE">OFF</button><span class="muted" id="wall"></span></div></div>
    <div class="card"><h2>Add a question</h2><input id="qline" placeholder="quiz Which hall is the keynote in? | Hall A | Hall B | Hall C | correct=2 time=15"><div class="row" style="margin-top:6px"><button class="on" id="add">ADD</button><span class="muted">choice · multi · scale (scale=1-10) · words · quiz (correct=N time=S)</span></div></div>
    <div class="card"><h2>Questions</h2><div class="row"><button class="on" data-v="PLAY NEXT">OPEN NEXT</button><button data-v="PLAY CLOSE">CLOSE</button><button data-v="PLAY REVEAL">REVEAL</button></div><div id="questions"></div></div>
    <div class="card"><h2>Queue</h2><div class="row"><button class="ok" data-v="PLAY APPROVE all">APPROVE ALL</button><button id="auto">AUTO: ?</button></div><div id="queue"></div></div>
    <div class="card"><h2>Message back</h2><div class="row"><input id="msgto" placeholder="room · group:Table 4 · phone:Sam" style="max-width:220px" value="room"><input id="msg" placeholder="The words" style="flex:1"><button class="on" id="send">SEND</button></div><div class="muted" id="sent"></div></div>
    <div class="card"><h2>The path and the board</h2><div class="row"><button data-v="PLAY PATH OPEN">PATH: OPEN VOTE</button><button data-v="PLAY PATH CLOSE">CLOSE VOTE</button><button data-v="PLAY PATH RESET">RESET STORY</button><button data-v="PLAY PATH RELOAD">RELOAD FILE</button><button data-v="PLAY DRAUGHTS RESET">NEW DRAUGHTS BOARD</button></div><div class="muted" id="path"></div></div>
    <div class="card"><h2>The room</h2><div class="row"><button data-v="PLAY RESET">RESET (keep code)</button><button class="no" data-v="PLAY NEW">NEW CODE</button><button data-v="PLAY EXPORT">EXPORT</button><span class="muted" id="last"></span></div><div class="muted" id="playersList"></div></div>
  </div>
</div>
<script>
  const $ = id => document.getElementById(id);
  let pass = sessionStorage.getItem('play.pass') || '';
  async function verb(line) { const r = await fetch('/api/admin', { method: 'POST', body: pass + '\n' + line }); const t = await r.text(); $('last').textContent = t; refresh(); return t; }
  async function refresh() {
    const r = await fetch('/api/play/host', { method: 'POST', body: pass }); if (r.status === 403) { $('gate').classList.remove('hidden'); $('host').classList.add('hidden'); $('gatemsg').textContent = 'No.'; return; }
    const h = await r.json();
    $('gate').classList.add('hidden'); $('host').classList.remove('hidden');
    $('code').textContent = h.room; $('url').textContent = h.joinUrl; $('people').textContent = h.players.length + ' joined · ' + h.players.filter(p => p.here).length + ' here'; $('wall').textContent = 'showing ' + h.wall;
    $('auto').textContent = 'AUTO WORDS: ' + (h.auto ? 'ON' : 'OFF'); $('auto').onclick = () => verb('PLAY AUTO ' + (h.auto ? 'OFF' : 'ON'));
    $('questions').innerHTML = h.questions.map(q => `<div class="q"><b>${q.kind}</b> ${esc(q.text)} <span class="muted">${q.state} · ${q.answers} answers${q.state === 'open' && q.kind === 'quiz' ? ' · ' + q.secondsLeft + ' s' : ''}</span><br><span class="muted">${q.options.map((o, i) => (i === q.correct ? '✓ ' : '') + esc(o) + ' ' + q.percent[i] + '%').join(' · ')}${q.words.length ? esc(q.words.join(', ')) : ''}${q.average ? 'average ' + q.average : ''}</span><br><button data-v="PLAY OPEN ${q.id}">OPEN</button></div>`).join('') || '<div class="muted">none yet</div>';
    $('queue').innerHTML = h.queue.filter(m => m.state === 'waiting').map(m => `<div class="q">${esc(m.nick)}: <b>${esc(m.text)}</b> <span class="muted">${m.forCloud ? 'for the cloud' : 'a shout'} ${esc(m.note)}</span> <button class="ok" data-v="PLAY APPROVE ${m.id}">APPROVE</button> <button class="no" data-v="PLAY REJECT ${m.id}">REJECT</button></div>`).join('') || '<div class="muted">nothing waiting</div>';
    $('path').textContent = h.path.title + ' — scene ' + h.path.scene + (h.path.open ? ' · vote open ' + h.path.votes.join('/') : h.path.end ? ' · the end' : '') + ' · draughts: ' + h.draughts;
    $('playersList').textContent = h.players.map(p => p.nick + (p.group ? ' (' + p.group + ')' : '') + (p.score ? ' ' + p.score : '')).join(' · ');
    document.querySelectorAll('button[data-v]').forEach(b => b.onclick = () => verb(b.dataset.v));
  }
  function esc(s) { return String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]); }
  $('unlock').onclick = () => { pass = $('pass').value; sessionStorage.setItem('play.pass', pass); refresh(); };
  $('add').onclick = () => verb('PLAY ADD ' + $('qline').value).then(() => $('qline').value = '');
  $('send').onclick = () => verb('PLAY MESSAGE ' + $('msgto').value + ' ' + $('msg').value).then(t => { $('sent').textContent = t; $('msg').value = ''; });
  if (pass) refresh();
  setInterval(() => { if (!$('host').classList.contains('hidden')) refresh(); }, 3000);
</script></body></html>
""";
}
