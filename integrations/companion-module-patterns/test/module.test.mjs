import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { InstanceStatus } from '@companion-module/base'
import { validateManifest } from '@companion-module/base/manifest'
import { sanitisePresetDefinitions } from '../node_modules/@companion-module/host/dist/internal/presets.js'
import { boot, pressAction, askFeedback, sampleState } from './harness.mjs'
import { allLines } from './emit-lines.mjs'
import { variableDefinitions } from '../src/variables.js'
import { variableValues, emptyState } from '../src/state.js'
import { connectionTarget, configFields, GROUPS } from '../src/config.js'
import { COLOURS, STATES, style } from '../src/palette.js'
import { MODULE_VERSION, pairingToken, trustProblem } from '../src/main.js'

const manifest = JSON.parse(readFileSync(new URL('../companion/manifest.json', import.meta.url), 'utf8'))
const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'))

test('the manifest is one Companion 5 accepts, and its bonjour query has its config field', () => {
	validateManifest(manifest, false)
	assert.equal(manifest.type, 'connection')
	assert.equal(manifest.version, pkg.version)
	assert.equal(manifest.version, MODULE_VERSION)
	assert.match(manifest.runtime.type, /^node2[26]$/)
	const bonjourFields = configFields().filter((f) => f.type === 'bonjour-device').map((f) => f.id)
	for (const id of Object.keys(manifest.bonjourQueries)) assert.ok(bonjourFields.includes(id), `bonjour query ${id} has no bonjour-device field`)
	assert.equal(manifest.bonjourQueries.desk.type, 'patterns')
})

test('the module boots on the real base, says HELLO with its label and version, and hands Companion its definitions', async () => {
	const b = await boot()
	assert.equal(b.lines()[0], `HELLO FOH deck module=${MODULE_VERSION}`)
	assert.ok(Object.keys(b.ctx.actions).length > 80, 'actions')
	assert.ok(Object.keys(b.ctx.feedbacks).length > 60, 'feedbacks')
	assert.ok(b.ctx.structure.length > 20, 'sections')
	assert.ok(b.ctx.statuses.some((s) => s.status === InstanceStatus.Ok))
	assert.ok(!Array.isArray(b.ctx.variableDefs), 'variable definitions are an object, as base 2 wants')
})

test('a pairing token in the config is presented with AUTH straight after HELLO; without one nothing more is said; a refusal over trust is a bad-config status with the words', async () => {
	const paired = await boot({ config: { host: '10.0.0.5', port: 9697, token: ' k7qm-3xwd-p9ra ' } })
	assert.equal(paired.lines()[0], `HELLO FOH deck module=${MODULE_VERSION}`)
	assert.equal(paired.lines()[1], 'AUTH k7qm-3xwd-p9ra')
	const open = await boot()
	assert.ok(!open.lines().some((l) => l.startsWith('AUTH')), 'no token, no AUTH')
	assert.equal(pairingToken({}), '')
	assert.equal(pairingToken({ token: null }), '')
	assert.ok(configFields().find((f) => f.id === 'token' && f.type === 'textinput'), 'the token field')
	assert.equal(trustProblem('ERR unknown command'), null)
	assert.match(trustProblem('ERR wrong token — the pairing token is on the desk'), /not this desk's/)
	assert.match(trustProblem('ERR not paired — this desk asks for its pairing token first'), /asks for its pairing token/)
	open.socket.receive('ERR not paired — this desk asks for its pairing token first: AUTH <token> on the wire\n')
	const last = open.ctx.statuses[open.ctx.statuses.length - 1]
	assert.equal(last.status, InstanceStatus.BadConfig)
	assert.match(last.message, /pairing token/)
	assert.equal(open.ctx.variables.last_error.startsWith('not paired'), true)
})

test('every preset is one the host keeps: its actions, feedbacks and option keys exist, and its layered elements pass the schema', async () => {
	const warnings = []
	global.COMPANION_LOGGER = (source, level, message) => { if (level === 'warn' || level === 'error') warnings.push(message) }
	try {
		const b = await boot()
		const managers = (defs) => ({ getDefinitionIds: () => Object.keys(defs), getDefinition: (id) => defs[id] })
		const result = sanitisePresetDefinitions(managers(b.ctx.actions), managers(b.ctx.feedbacks), b.ctx.structure, b.ctx.presets, manifest.runtime.apiVersion)
		assert.deepEqual(warnings, [])
		assert.equal(Object.keys(result.presets).length, Object.keys(b.ctx.presets).length)
		const timer = result.presets.stage_timer
		assert.equal(timer.type, 'alternatives')
		assert.equal(timer.variants.length, 2)
		assert.equal(timer.variants[0].type, 'layered')
		assert.equal(timer.variants[1].type, 'simple')
		// Every section names presets that exist, and every kept preset sits in one section.
		const inSections = new Set(result.structure.flatMap((s) => s.definitions))
		for (const id of inSections) assert.ok(result.presets[id], `section names ${id}`)
		for (const id of Object.keys(result.presets)) assert.ok(inSections.has(id), `${id} is in a section`)
	} finally {
		delete global.COMPANION_LOGGER
	}
})

test('every variable a key can read is declared, and every declared variable is written from a STATE', () => {
	const declared = new Set(Object.keys(variableDefinitions()))
	const written = new Set(Object.keys(variableValues(sampleState())))
	for (const id of written) assert.ok(declared.has(id), `${id} is written but not declared`)
	for (const id of declared) assert.ok(written.has(id) || id === 'last_error', `${id} is declared but never written`)
	const empty = Object.keys(variableValues(emptyState()))
	assert.deepEqual(empty.sort(), [...written].sort(), 'an empty state writes the same ids as a full one')
})

test('every $(patterns:…) on a preset names a declared variable', async () => {
	const b = await boot()
	const declared = new Set(Object.keys(variableDefinitions()))
	const texts = []
	const walk = (v) => {
		if (typeof v === 'string') texts.push(v)
		else if (Array.isArray(v)) v.forEach(walk)
		else if (v && typeof v === 'object') Object.values(v).forEach(walk)
	}
	walk(b.ctx.presets)
	const used = new Set(texts.flatMap((t) => [...t.matchAll(/\$\(patterns:([a-z0-9_]+)\)/g)].map((m) => m[1])))
	for (const id of used) assert.ok(declared.has(id), `$(patterns:${id}) is not a variable`)
	assert.ok(used.size > 60)
})

test('a STATE becomes the variables a key reads, the new blocks included', async () => {
	const b = await boot()
	const v = b.ctx.variables
	assert.equal(v.cue_standby_number, '01.020')
	assert.equal(v.cue_1, '01.020\nKeynote')
	assert.equal(v.cue_2_name, 'Q&A')
	assert.equal(v.look_state, 'EDITED')
	assert.equal(v.look_2, 'Awards')
	assert.equal(v.look_f1, 'Walk-in')
	assert.equal(v.screen_2_pattern, 'TestCard')
	assert.equal(v.timing_offset, '7 MIN LATE')
	assert.equal(v.countdown_text, '1:49 · DOORS IN')
	assert.equal(v.desk_version, '1.9.0')
	assert.equal(v.show, 'Gala')
	assert.equal(v.node_1, 'Caller\nFOH-CALL')
	assert.equal(v.machine_memory_held, '3 held (212 MB): 2 on air, 1 idle — the first lets go in 43 s')
	assert.equal(v.machine_gpu_cache, 'GPU cache 48 MB of 128 MB (212 resources)')
	assert.equal(v.node_2_kind, 'timer')
	assert.equal(v.nodes_count, '1')
	assert.equal(v.nodes_text, 'Caller FOH-CALL')
	assert.equal(v.twin_role, 'main')
	assert.equal(v.twin_phase, 'inStep')
	assert.equal(v.stage_timer, '1:35')
	assert.equal(v.stage_colour, 'amber')
	assert.equal(v.stage_progress, '83')
	assert.equal(v.stage_pending, 'Wrap up')
	assert.equal(v.stage_segment, '01.020 Keynote')
	assert.equal(v.machine_render_faults, '0')
	assert.equal(v.cue_last_pending, '0')
	assert.equal(v.machine_faulting, 'ok')
	assert.equal(v.machine_live_age, '33 ms')
	assert.equal(v.machine_memory_pressure, 'elevated')
	assert.match(v.inputs_pending, /^Low latency change pending/)
	assert.equal(v.devices_failing, '1')
	assert.equal(v.device_last_reply, 'Projector: POWR: OK — accepted')
	assert.equal(v.device_last_failure, 'Projector: INPUT HDMI 2 — no answer in 2 s (delivered, not accepted)')
	assert.equal(b.inst.standbyId, 'c2')
	assert.ok(b.ctx.checks >= 1, 'every feedback rechecked')
})

test('the feedbacks read the state: looks, screens, cues, nodes, the twin and the stage', async () => {
	const b = await boot()
	assert.equal(askFeedback(b, 'look_on_air', { name: '' }), true)
	assert.equal(askFeedback(b, 'look_on_air', { name: 'Walk-in' }), false)
	assert.equal(askFeedback(b, 'look_edited', { name: '' }), true)
	assert.equal(askFeedback(b, 'look_bank_edited', { n: 2 }), true)
	assert.equal(askFeedback(b, 'look_bank_edited', { n: 1 }), false)
	assert.equal(askFeedback(b, 'look_preview', { name: 'Walk-in' }), true)
	assert.equal(askFeedback(b, 'screen_off_look', { n: 2 }), true)
	assert.equal(askFeedback(b, 'screen_black', { n: 2 }), true)
	assert.equal(askFeedback(b, 'screen_pattern_is', { n: 2, kind: 'test card' }), true)
	assert.equal(askFeedback(b, 'cue_standby_is', { cue: '01.020' }), true)
	assert.equal(askFeedback(b, 'cue_armed', {}), true)
	assert.equal(askFeedback(b, 'running_late', { minutes: 5 }), true)
	assert.equal(askFeedback(b, 'running_late', { minutes: 10 }), false)
	assert.equal(askFeedback(b, 'countdown_running', { phase: 'running' }), true)
	assert.equal(askFeedback(b, 'deck_on_air', { ended: true }), false)
	assert.equal(askFeedback(b, 'device_open', { name: 'Projector' }), true)
	assert.equal(askFeedback(b, 'device_failing', { name: '' }), true)
	assert.equal(askFeedback(b, 'device_failing', { name: 'Projector' }), true)
	assert.equal(askFeedback(b, 'device_failing', { name: 'Lights' }), false)
	assert.equal(askFeedback(b, 'render_faulting', {}), false)
	assert.equal(askFeedback(b, 'live_age_over', { ms: 80 }), false)
	assert.equal(askFeedback(b, 'live_age_over', { ms: 20 }), true, 'the fixture\'s live picture is 33 ms old')
	assert.equal(askFeedback(b, 'memory_pressure_at_least', { level: 'elevated' }), true)
	assert.equal(askFeedback(b, 'inputs_change_pending', {}), true)
	assert.equal(askFeedback(b, 'memory_pressure_at_least', { level: 'high' }), false)
	assert.equal(askFeedback(b, 'slot_empty', { kind: 'look', n: 3 }), true)
	assert.equal(askFeedback(b, 'slot_empty', { kind: 'node', n: 2 }), false)
	assert.equal(askFeedback(b, 'slot_empty', { kind: 'node', n: 3 }), true)
	assert.equal(askFeedback(b, 'node_is', { n: 1, kind: 'caller' }), true)
	assert.equal(askFeedback(b, 'node_is', { n: 1, kind: 'any' }), true)
	assert.equal(askFeedback(b, 'node_is', { n: 2, kind: 'timer' }), false, 'a node not heard lately is not lit as its kind')
	assert.equal(askFeedback(b, 'node_gone', { n: 2 }), true)
	assert.equal(askFeedback(b, 'node_linked', { min: 1 }), true)
	assert.equal(askFeedback(b, 'twin_is', { phase: 'inStep' }), true)
	assert.equal(askFeedback(b, 'twin_role_is', { role: 'main' }), true)
	assert.equal(askFeedback(b, 'stage_colour_is', { colour: 'amber' }), true)
	assert.equal(askFeedback(b, 'stage_colour_is', { colour: 'green' }), false)
	assert.equal(askFeedback(b, 'stage_is', { phase: 'running' }), true)
	assert.equal(askFeedback(b, 'stage_pending', { channel: 'speaker' }), true)
	assert.equal(askFeedback(b, 'stage_pending', { channel: 'crew' }), false)
	assert.equal(askFeedback(b, 'stage_pending', { channel: 'any' }), true)
})

test('a press is one line of the wire, spelt as docs/REMOTE.md has it', async () => {
	const b = await boot()
	const cases = [
		['go', {}, ['OUTPUTS ON']], ['blackout', { mode: 'TOGGLE' }, ['BLACKOUT TOGGLE']],
		['look_slot', { slot: 3 }, ['LOOK 3']], ['look_name', { name: 'Awards' }, ['LOOK Awards']], ['look_name', { name: '  ' }, []], ['look_bank', { n: 2 }, ['LOOK #2']],
		['cue_go', {}, ['CUE GO c2']], ['cue_bank', { k: 1, mode: 'GO' }, ['CUE GO c2']], ['cue_bank', { k: 2, mode: 'STANDBY' }, ['CUE STANDBY 01.030']], ['cue_bank', { k: 3, mode: 'GO' }, ['CUE STANDBY 01.040', 'CUE GO c4']], ['cue_bank', { k: 7, mode: 'GO' }, []],
		['cue_standby', { mode: 'NEXT', cue: '' }, ['CUE STANDBY NEXT']], ['cue_standby', { mode: 'NUMBER', cue: '01.040' }, ['CUE STANDBY 01.040']],
		['cue_hold', { mode: 'TOGGLE' }, ['CUE HOLD ON']], ['cue_arm', { mode: 'OFF' }, ['CUE ARM OFF']], ['stop_all', {}, ['STOPALL']],
		['plan', { mode: 'SHIFT', delta: '+2:00' }, ['PLAN SHIFT +2:00']], ['plan', { mode: 'RESUME', delta: '' }, ['PLAN RESUME']], ['plan', { mode: 'CATCHUP', delta: '' }, ['PLAN CATCHUP']],
		['countdown_follow', { mode: 'ON' }, ['COUNTDOWN FOLLOW ON']],
		['device_send', { device: '*', text: 'RELAY 1' }, ['DEVICE * RELAY 1']], ['device_send', { device: 'Companion', text: 'PAGE 3' }, ['DEVICE Companion PAGE 3']],
		['screen', { n: 2, mode: 'OFF' }, ['SCREEN 2 OFF']], ['screen_look', { n: 2, look: 'Sponsor' }, ['SCREEN 2 LOOK Sponsor']], ['screen_program', { n: 2 }, ['SCREEN 2 PROGRAM']], ['screen_take', { n: 2, mode: 'TAKE' }, ['SCREEN 2 TAKE']], ['screen_take', { n: 3, mode: 'CUT' }, ['SCREEN 3 CUT']],
		['screen_lock', { n: 1, mode: 'ON' }, ['LOCK 1 ON']], ['group', { letter: 'A', mode: 'ON' }, ['GROUP A ON']],
		['fade', { dir: 'DOWN', secs: 2, target: '' }, ['FADE 2']], ['fade', { dir: 'UP', secs: 0, target: 'SCREEN 2' }, ['FADEUP SCREEN 2']], ['fade', { dir: 'DOWN', secs: 1.5, target: 'GROUP A' }, ['FADE 1.5 GROUP A']],
		['freeze', { mode: 'ON' }, ['FREEZE ON']], ['review', { mode: 'TOGGLE' }, ['REVIEW TOGGLE']], ['look_back', {}, ['LOOKBACK']],
		['audio', { mode: 'NEXT' }, ['AUDIO NEXT']], ['audio_item', { n: 2 }, ['AUDIO PLAY 2']], ['audio_name', { name: 'Intro' }, ['AUDIO PLAY Intro']], ['audio_level', { n: 80 }, ['AUDIO VOL 80']],
		['music', { mode: 'PAUSE' }, ['MUSIC PAUSE']], ['music_item', { n: 1 }, ['MUSIC PLAY 1']], ['music_level', { n: 50 }, ['MUSIC VOL 50']], ['tone', { mode: 'ON' }, ['TONE ON']], ['duck', { mode: 'TOGGLE' }, ['DUCK TOGGLE']],
		['stinger', { n: 1 }, ['STINGER 1']], ['vog', { n: 2 }, ['VOG 2']], ['sting_name', { name: 'Whoosh' }, ['STING Whoosh']], ['stinger_stop', {}, ['STINGER STOP']],
		['lower_third', { n: 1 }, ['LT 1']], ['lower_third_name', { name: 'Name strap' }, ['LT Name strap']], ['lower_third_off', {}, ['LT OFF']],
		['lower_third_person', { n: 2, design: '' }, ['PERSON 2']], ['lower_third_person', { n: 2, design: '1' }, ['LT 1 WITH 2']], ['lower_third_person_name', { name: 'Jane Doe', design: '' }, ['PERSON Jane Doe']],
		['lower_third_preview', { n: 1, person: '' }, ['LT PREVIEW 1']], ['lower_third_preview', { n: 0, person: '2' }, ['LT PREVIEW WITH 2']], ['lower_third_preview', { n: 1, person: 'Jane Doe' }, ['LT PREVIEW 1 WITH Jane Doe']],
		['lower_third_take', {}, ['LT TAKE']], ['lower_third_update', {}, ['LT UPDATE']], ['lower_third_preview_off', {}, ['LT PREVIEW OFF']],
		['web_action', { action: 'next', key: '', page: '' }, ['WEB KEY next']], ['web_action', { action: 'key', key: 'Ctrl+Shift+F5', page: 'slides' }, ['WEB KEY Ctrl+Shift+F5 ON slides']], ['web_action', { action: 'reload', key: '', page: '' }, ['WEB RELOAD']],
		['web_click', { x: 50, y: 50, page: '' }, ['WEB CLICK 50 50']], ['web_type', { text: 'hello' }, ['WEB TYPE hello']], ['web_open', { address: 'https://x.y', page: '' }, ['WEB OPEN https://x.y']],
		['deck_page', { mode: 'NEXT', n: 1 }, ['DECK NEXT']], ['deck_page', { mode: 'PAGE', n: 5 }, ['DECK PAGE 5']], ['video_end', { seconds: 10 }, ['VIDEO END 10']], ['video_restart', {}, ['VIDEO RESTART']],
		['clock', { mode: 'SECONDS ON' }, ['CLOCK SECONDS ON']], ['message', { mode: 'SAY', text: 'Doors open at 7' }, ['MESSAGE Doors open at 7']], ['message', { mode: 'SAY', text: '' }, ['MESSAGE ON']], ['message', { mode: 'SCROLL ON', text: '' }, ['MESSAGE SCROLL ON']],
		['countdown', { mode: 'START', minutes: '5', time: '', label: '' }, ['COUNTDOWN START 5']], ['countdown', { mode: 'START', minutes: '', time: '', label: '' }, ['COUNTDOWN START']], ['countdown', { mode: 'TO', minutes: '', time: '19:30', label: '' }, ['COUNTDOWN TO 19:30']],
		['countdown', { mode: 'LABEL', minutes: '', time: '', label: 'DOORS IN' }, ['COUNTDOWN LABEL DOORS IN']], ['countdown', { mode: 'TOGGLE', minutes: '', time: '', label: '' }, ['COUNTDOWN TOGGLE']], ['countdown', { mode: 'STOP', minutes: '', time: '', label: '' }, ['COUNTDOWN STOP']],
		['logo', { mode: 'ON' }, ['LOGO ON']], ['pip', { mode: 'OFF' }, ['PIP OFF']], ['overlays_off', {}, ['OVERLAYS OFF']], ['pattern', { kind: 'LED wall' }, ['PATTERN LED wall']], ['section', { n: 2 }, ['SECTION 2']],
		['weather', { mode: 'TOMORROW' }, ['WEATHER TOMORROW']], ['stream', { mode: 'OFF' }, ['STREAM OFF']], ['schedule', { mode: 'ON' }, ['SCHEDULE ON']],
		['announce', { what: 'Closing time' }, ['ANNOUNCE Closing time']], ['announce_off', {}, ['ANNOUNCE OFF']], ['advert', { name: '2' }, ['ADVERT 2']], ['advert_off', {}, ['ADVERT OFF']],
		['presenter_next', {}, ['NEXT']], ['presenter_prev', {}, ['PREV']], ['identify', {}, ['IDENTIFY']],
		['stage_message', { channel: 'speaker', text: 'Wrap up' }, ['STAGE MESSAGE Wrap up']], ['stage_message', { channel: 'crew', text: 'Stand by' }, ['STAGE CREW Stand by']], ['stage_clear', {}, ['STAGE CLEAR']],
		['timer', { mode: 'TOGGLE', seconds: 60 }, ['TIMER PAUSE']], ['timer', { mode: 'ADD', seconds: 90 }, ['TIMER ADD 90']], ['timer', { mode: 'MINUS', seconds: 30 }, ['TIMER MINUS 30']], ['timer', { mode: 'FLASH', seconds: 60 }, ['TIMER FLASH']],
		['twin', { mode: 'TAKEOVER' }, ['TWIN TAKEOVER']], ['twin', { mode: 'TAKEOVER FORCE' }, ['TWIN TAKEOVER FORCE']], ['twin', { mode: 'STANDBY' }, ['TWIN STANDBY']], ['twin', { mode: 'TAKEBACK' }, ['TWIN TAKEBACK']],
		['showlock', { mode: 'ON' }, ['SHOWLOCK ON']],
		['arcade', { mode: 'START', game: 'pong', players: 2 }, ['ARCADE START pong 2']], ['arcade', { mode: 'NDI ON', game: '', players: 1 }, ['ARCADE NDI ON']], ['arcade_key', { pad: 1, button: 'A', how: 'TAP' }, ['ARCADE KEY 1 A TAP']],
		['play', { mode: 'NEXT', show: 'results' }, ['PLAY NEXT']], ['play', { mode: 'SHOW', show: 'leaderboard' }, ['PLAY SHOW leaderboard']], ['play', { mode: 'AUTO OFF', show: '' }, ['PLAY AUTO OFF']],
		['raw', { line: 'CALIBRATE STATUS' }, ['CALIBRATE STATUS']],
	]
	for (const [id, options, expected] of cases) {
		assert.deepEqual(pressAction(b, id, options), expected, `${id} ${JSON.stringify(options)}`)
	}
	// A paused timer resumes on the same key.
	b.socket.receive('STATE ' + JSON.stringify({ ...sampleState(), stage: { timer: { phase: 'paused', text: '1:35', colour: 'amber' } } }) + '\n')
	assert.deepEqual(pressAction(b, 'timer', { mode: 'TOGGLE', seconds: 60 }), ['TIMER RESUME'])
})

test('every line the module can send is in test/lines.txt — the file the desk parses', async () => {
	const lines = await allLines()
	const kept = readFileSync(new URL('./lines.txt', import.meta.url), 'utf8').split('\n').filter(Boolean)
	assert.deepEqual(lines, kept, 'run `npm run lines` and commit test/lines.txt')
	for (const line of lines) assert.ok(!/undefined|null|\[object/.test(line), line)
})

test('a dropped link clears every key, and an ERR line is kept as the last error', async () => {
	const b = await boot()
	assert.equal(b.ctx.variables.cue_standby_number, '01.020')
	b.socket.receive('ERR standby moved\n')
	assert.equal(b.ctx.variables.last_error, 'standby moved')
	const checks = b.ctx.checks
	b.socket.emit('status_change', InstanceStatus.Disconnected, 'gone')
	assert.equal(b.ctx.variables.cue_standby_number, '-')
	assert.equal(b.ctx.variables.node_1, '')
	assert.equal(b.inst.standbyId, '')
	assert.ok(b.ctx.checks > checks)
	// Half a line waits for the rest; a bad payload is let by.
	b.socket.receive('STATE {"cuestack":{"standby":{"nu')
	b.socket.receive('mber":"02.010"}}}\n')
	assert.equal(b.ctx.variables.cue_standby_number, '02.010')
	b.socket.receive('STATE not json\n')
	assert.equal(b.ctx.variables.cue_standby_number, '02.010')
})

test('the desk picked on the network wins over a typed address; nothing set is bad config', async () => {
	assert.deepEqual(connectionTarget({ desk: '10.0.0.12:9697', host: '1.2.3.4', port: 9700 }), { host: '10.0.0.12', port: 9697, discovered: true })
	assert.deepEqual(connectionTarget({ desk: null, host: '1.2.3.4', port: 9700 }), { host: '1.2.3.4', port: 9700, discovered: false })
	assert.deepEqual(connectionTarget({ desk: '', host: '1.2.3.4' }), { host: '1.2.3.4', port: 9697, discovered: false })
	assert.equal(connectionTarget({ desk: '', host: '' }), null)
	const b = await boot({ config: { desk: null, host: '' }, state: null })
	assert.ok(b.ctx.statuses.some((s) => s.status === InstanceStatus.BadConfig))
	const fields = configFields()
	assert.ok(fields.find((f) => f.id === 'host').isVisibleExpression.includes('$(options:desk)'))
})

test('the ticked groups shape the sections; the defaults leave out patterns and the install', async () => {
	const names = (b) => b.ctx.structure.map((s) => s.name)
	const d = await boot()
	assert.ok(names(d).includes('Stage'))
	assert.ok(names(d).includes('Nodes'))
	assert.ok(names(d).includes('Looks — this show'))
	assert.ok(!names(d).includes('Install'))
	assert.ok(!names(d).includes('Patterns — every kind'))
	const all = {}
	for (const g of GROUPS) all[`g_${g.id}`] = true
	const a = await boot({ config: { host: '10.0.0.5', port: 9697, ...all, g_stage: false } })
	assert.ok(names(a).includes('Install'))
	assert.ok(names(a).includes('Patterns — every kind'))
	assert.ok(!names(a).includes('Stage'))
	// The sections keep reading order, and an id is never in two.
	const ids = d.ctx.structure.flatMap((s) => s.definitions)
	assert.equal(new Set(ids).size, ids.length)
	assert.equal(names(d)[0], 'Cue stack')
})

test('the palette is one language: every state names a colour, and the light grounds take dark text', () => {
	for (const [kind, row] of Object.entries(STATES)) for (const state of Object.keys(row)) {
		const s = style(kind, state)
		assert.equal(typeof s.bgcolor, 'number')
		assert.ok(COLOURS[row[state]], `${kind}.${state} names ${row[state]}`)
	}
	assert.equal(style('look', 'preview').color, style('cue', 'hold').color, 'amber keys read in ink everywhere')
	assert.equal(style('look', 'air').bgcolor, (30 << 16) | (158 << 8) | 90)
})

test("round 66 — the God's Eye on the deck: the verbs spelt as the desk parses them, the worst light and the problems as feedbacks, the headline as variables, an Eye page", async () => {
	const b = await boot()
	assert.deepEqual(pressAction(b, 'eye_focus', { words: ' screen 2 ' }), ['EYE FOCUS screen 2'])
	assert.deepEqual(pressAction(b, 'eye_focus', { words: '' }), [])
	assert.deepEqual(pressAction(b, 'eye_next', {}), ['EYE NEXT'])
	assert.deepEqual(pressAction(b, 'eye_prev', {}), ['EYE PREV'])
	assert.deepEqual(pressAction(b, 'eye_lens', { lens: 'audio' }), ['EYE LENS audio'])
	assert.deepEqual(pressAction(b, 'eye_reset', {}), ['EYE RESET'])
	assert.equal(askFeedback(b, 'eye_worst', { light: 'red' }), true)
	assert.equal(askFeedback(b, 'eye_worst', { light: 'green' }), false)
	assert.equal(askFeedback(b, 'eye_problems', {}), true)
	const v = variableValues(sampleState())
	assert.match(v.eye_headline, /MISMATCH/)
	assert.equal(v.eye_worst, v.eye_headline)
	assert.equal(v.eye_problems, '2')
	assert.equal(v.eye_focus, '')
	const none = variableValues(emptyState())
	assert.equal(none.eye_headline, '')
	assert.equal(none.eye_problems, '0')
	const quiet = await boot({ state: emptyState() })
	assert.equal(askFeedback(quiet, 'eye_worst', { light: 'red' }), false)
	assert.equal(askFeedback(quiet, 'eye_problems', {}), false)
	assert.ok(b.ctx.presets.eye_headline, 'the Eye page has its headline key')
	assert.ok(Object.keys(b.ctx.presets).some((id) => id === 'eye_lens_problems'), 'a lens key per lens')
	assert.deepEqual(style('eye', 'red'), style('signal', 'mismatch'))
})

test('round 67 — the next take and the group on the deck: TAKE NEXT and SCREEN n GROUP spelt as the desk parses them, the one-shot and the group as feedbacks and variables, a Take page', async () => {
	const b = await boot()
	assert.deepEqual(pressAction(b, 'take_next', { kind: 'wipe left', ms: 800, sting: '' }), ['TAKE NEXT wipe left 800'])
	assert.deepEqual(pressAction(b, 'take_next', { kind: 'cut', ms: 800, sting: '' }), ['TAKE NEXT cut'])
	assert.deepEqual(pressAction(b, 'take_next', { kind: 'dissolve', ms: 0, sting: '' }), ['TAKE NEXT dissolve'])
	assert.deepEqual(pressAction(b, 'take_next', { kind: 'CLEAR', ms: 500, sting: 'x' }), ['TAKE NEXT CLEAR'])
	assert.deepEqual(pressAction(b, 'take_next', { kind: 'STING', ms: 0, sting: ' Whoosh ' }), ['TAKE NEXT STING Whoosh'])
	assert.deepEqual(pressAction(b, 'take_next', { kind: 'STING', ms: 0, sting: '' }), [])
	assert.deepEqual(pressAction(b, 'screen_group', { n: 2, group: 'confidence' }), ['SCREEN 2 GROUP confidence'])
	assert.equal(askFeedback(b, 'screen_group_is', { n: 2, group: 'info' }), true)
	assert.equal(askFeedback(b, 'screen_group_is', { n: 2, group: 'main' }), false)
	assert.equal(askFeedback(b, 'screen_ticked', { n: 1 }), true)
	assert.equal(askFeedback(b, 'screen_ticked', { n: 2 }), false)
	assert.equal(askFeedback(b, 'take_next_set', {}), true)
	assert.equal(askFeedback(b, 'take_next_sting', {}), true)
	const v = variableValues(sampleState())
	assert.equal(v.take_next, 'STING Whoosh')
	assert.equal(v.take_scope, 'every screen')
	assert.equal(v.take_words, '→ 1 · Main wall · 1 held (locked)')
	assert.equal(v.screen_2_group, 'info')
	const none = variableValues(emptyState())
	assert.equal(none.take_next, '')
	assert.equal(none.take_words, '')
	const quiet = await boot({ state: emptyState() })
	assert.equal(askFeedback(quiet, 'take_next_set', {}), false)
	assert.equal(askFeedback(quiet, 'take_next_sting', {}), false)
	assert.ok(b.ctx.presets.take_plan, 'the Take page has the plan key')
	assert.ok(b.ctx.presets.take_next_cut && b.ctx.presets.take_next_sting_1, 'a key per way to arrive')
	assert.ok(b.ctx.presets.screen_2_group_conf, 'a group key per screen')
	assert.deepEqual(style('take', 'sting'), style('stinger', 'playing'))
})

test("round 68 — the web page's picture on the deck: the frame path as variables, buffered and stalled as feedbacks, nothing for a page without one", async () => {
	const path = { words: 'smooth 2 (67 ms) · decode 6.2 ms · 30 → 30 fps', smoothing: 'smooth 2 (67 ms)', depth: 2, latencyMs: 66.7, jitterMs: 4.1, decodeMs: 6.2, deliveredFps: 30, presentedFps: 30, underruns: 1, dropped: 0, duplicates: 12, held: 2, poolStarved: 0, poolBytes: 58060800, capture: 'captured at 1280×720 · q60' }
	const smooth = { ...sampleState(), web: { page: 'Sponsor', url: 'https://www.youtube.com/embed/abc', title: 'Sponsor', service: 'YouTube', fps: 30, actions: [], player: null, arm: null, path } }
	const v = variableValues(smooth)
	assert.equal(v.web_path, path.words)
	assert.equal(v.web_smoothing, 'smooth 2 (67 ms)')
	assert.equal(v.web_latency, '67 ms')
	assert.equal(v.web_underruns, '1')
	assert.equal(v.web_capture, 'captured at 1280×720 · q60')
	const b = await boot({ state: smooth })
	assert.equal(askFeedback(b, 'web_smoothed', {}), true)
	assert.equal(askFeedback(b, 'web_stalled', {}), true)
	const live = { ...smooth, web: { ...smooth.web, path: { ...path, smoothing: 'low latency', depth: 0, latencyMs: 0, underruns: 0 } } }
	const q = await boot({ state: live })
	assert.equal(askFeedback(q, 'web_smoothed', {}), false)
	assert.equal(askFeedback(q, 'web_stalled', {}), false)
	assert.equal(variableValues(live).web_latency, '0 ms')
	const none = variableValues(emptyState())
	assert.equal(none.web_path, '')
	assert.equal(none.web_smoothing, '')
	assert.equal(none.web_latency, '')
	assert.equal(none.web_underruns, '0')
	assert.equal(none.web_capture, '')
	const quiet = await boot({ state: emptyState() })
	assert.equal(askFeedback(quiet, 'web_smoothed', {}), false)
	assert.equal(askFeedback(quiet, 'web_stalled', {}), false)
})

test("round 69 — the sound follows the picture on the deck: SCREEN n AUDIO and AUDIO FOLLOW spelt as the desk parses them, the follow and each screen's output as feedbacks and variables, a FOLLOW key", async () => {
	const b = await boot()
	assert.deepEqual(pressAction(b, 'screen_audio', { n: 2, output: ' Info HDMI ' }), ['SCREEN 2 AUDIO Info HDMI'])
	assert.deepEqual(pressAction(b, 'screen_audio', { n: 2, output: '' }), ['SCREEN 2 AUDIO OFF'])
	assert.deepEqual(pressAction(b, 'audio_follow', { mode: 'off' }), ['AUDIO FOLLOW OFF'])
	assert.deepEqual(pressAction(b, 'audio_follow', { mode: 'toggle' }), ['AUDIO FOLLOW TOGGLE'])
	assert.equal(askFeedback(b, 'audio_follow_on', {}), true)
	assert.equal(askFeedback(b, 'screen_sound_out', { n: 1 }), true)
	assert.equal(askFeedback(b, 'screen_sound_out', { n: 2 }), false)
	const v = variableValues(sampleState())
	assert.equal(v.audio_follow, 'ON')
	assert.equal(v.screen_1_audio, 'Main HDMI')
	assert.equal(v.screen_2_audio, '')
	assert.ok(v.audio_follow_words.startsWith('Sound follows the picture on 1 screen'))
	const none = variableValues(emptyState())
	assert.equal(none.audio_follow, 'off')
	assert.equal(none.audio_follow_words, '')
	assert.equal(none.screen_1_audio, '')
	const quiet = await boot({ state: emptyState() })
	assert.equal(askFeedback(quiet, 'audio_follow_on', {}), false)
	assert.equal(askFeedback(quiet, 'screen_sound_out', { n: 1 }), false)
	assert.ok(b.ctx.presets.audio_follow, 'the Audio page has the FOLLOW key')
	assert.deepEqual(style('audio', 'follow'), style('overlay', 'on'))
	assert.deepEqual(style('screen', 'sound'), style('audio', 'playing'))
})
