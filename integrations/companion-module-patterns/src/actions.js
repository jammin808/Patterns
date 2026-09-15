// The actions: every key's press as one line of the desk's wire. The ids are the ones saved pages
// carry since 1.0 — 'go' and 'stop' are still the outputs' transport, GO is the cue stack's — and
// the lines are the ones docs/REMOTE.md lists, which a test on the desk's side parses one by one.
//
// ctx: { send(line), log(level, text), state(), standbyId(), upcoming() }

const onOff = (id = 'mode', def = 'TOGGLE') => ({
	type: 'dropdown', id, label: 'Mode', default: def,
	choices: [{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' }],
})
const onOrOff = (def = 'ON') => ({ type: 'dropdown', id: 'mode', label: 'Mode', default: def, choices: [{ id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' }] })
const screenN = { type: 'number', id: 'n', label: 'Screen number (overview order)', default: 1, min: 1, max: 32 }
const text = (id, label, def = '') => ({ type: 'textinput', id, label, default: def })
const clean = (v) => String(v ?? '').trim()
const stagedWhat = {
	type: 'dropdown', id: 'what', label: 'What', default: 'LOOK',
	choices: [
		{ id: 'LOOK', label: 'A look' }, { id: 'PRESET', label: 'A preset' }, { id: 'PATTERN', label: 'A kind of picture' },
		{ id: 'PROGRAM', label: 'The programme' }, { id: 'RESET', label: 'The look on air, as it was (RESET)' },
	],
}
/** "PREFIX LOOK x" and the other staged words; null when a name is needed and none was typed. */
const stagedLine = (prefix, what, name) => {
	if (what === 'PROGRAM' || what === 'RESET') return `${prefix} ${what}`
	const x = clean(name)
	return x ? `${prefix} ${what} ${x}` : null
}

export function buildActions(ctx) {
	const send = (cmd) => ctx.send(cmd)
	return {
		// Action ids stay 'go' / 'stop' so saved pages keep working; the verbs are the
		// outputs transport. GO is reserved for the cue stack.
		go: { name: 'Outputs on (open output windows)', options: [], callback: () => send('OUTPUTS ON') },
		stop: { name: 'Outputs off (close output windows)', options: [], callback: () => send('OUTPUTS OFF') },
		identify: { name: 'Identify screens', options: [], callback: () => send('IDENTIFY') },
		blackout: { name: 'Blackout', options: [onOff()], callback: (a) => send(`BLACKOUT ${a.options.mode}`) },
		look_slot: {
			name: 'Apply look by F-key slot',
			options: [{ type: 'number', id: 'slot', label: 'Slot (1–12)', default: 1, min: 1, max: 12 }],
			callback: (a) => send(`LOOK ${a.options.slot}`),
		},
		look_name: {
			name: 'Apply look by name',
			options: [text('name', 'Look name')],
			callback: (a) => { const n = clean(a.options.name); if (n) send(`LOOK ${n}`) },
		},
		// A bank key: the look at a place in the show's list — LOOK #n — so a row of keys labels itself
		// ($(patterns:look_n)) as looks are made, renamed or reordered, with no key to edit.
		look_bank: {
			name: "Look — by its place in the show's list (bank key n; the name is $(patterns:look_n))",
			options: [{ type: 'number', id: 'n', label: 'Place in the list (1–16)', default: 1, min: 1, max: 16 }],
			callback: (a) => send(`LOOK #${a.options.n}`),
		},
		// The cue bank: place 1 is the standby cue, 2… the cues after it — a key per upcoming cue that
		// relabels itself on every GO. STANDBY moves the standby there; GO moves it and fires it.
		cue_bank: {
			name: 'Cue bank — the cue at a place (1 = the standby cue, 2… = the cues after it): standby, or standby and GO',
			options: [
				{ type: 'number', id: 'k', label: 'Place (1–7)', default: 1, min: 1, max: 7 },
				{ type: 'dropdown', id: 'mode', label: 'Do', default: 'STANDBY', choices: [{ id: 'STANDBY', label: 'Put it on standby' }, { id: 'GO', label: 'Standby and GO' }] },
			],
			callback: (a) => {
				const row = ctx.upcoming()[a.options.k - 1]
				if (!row) return ctx.log('warn', `Cue bank ${a.options.k}: no cue at that place`)
				if (a.options.k !== 1) send(`CUE STANDBY ${row.number || row.name}`)
				if (a.options.mode === 'GO') send(`CUE GO ${row.id ?? ''}`.trim())
			},
		},
		// The Interactive area: a line to a device — an Arduino's relay, a Pi's script, a projector, Companion itself.
		device_send: {
			name: 'Device — send a line to a device of the Interactive page (a board, a projector, Disguise, Pixera, an OSC box, an HTTP endpoint, a Companion)',
			options: [
				text('device', 'Device name (blank or * = the first)', '*'),
				text('text', "The device's words: RELAY 1 · POWER ON · PLAY · CUE 1.2 · TIMELINE Main PLAY · /cue/1/start · PAGE 3", 'RELAY 1'),
			],
			callback: (a) => {
				const t = clean(a.options.text)
				if (!t) return
				send(`DEVICE ${clean(a.options.device) || '*'} ${t}`)
			},
		},
		// The Install page: an announcement by name or as words, an advert by name, the schedule's switch.
		announce: {
			name: 'Announcement — by name from the Install page, or the words themselves',
			options: [text('what', 'Announcement name, or the words on screen', 'Closing time')],
			callback: (a) => { const w = clean(a.options.what); if (w) send(`ANNOUNCE ${w}`) },
		},
		announce_off: { name: 'Announcement — end now', options: [], callback: () => send('ANNOUNCE OFF') },
		advert: {
			name: 'Advert — play now (by name or number, from the Install page)',
			options: [text('name', 'Advert name or number', '1')],
			callback: (a) => { const n = clean(a.options.name); if (n) send(`ADVERT ${n}`) },
		},
		advert_off: { name: 'Advert — end now (the programme comes back)', options: [], callback: () => send('ADVERT OFF') },
		schedule: { name: 'Install schedule on / off (the clock runs the site, or stops)', options: [onOrOff('ON')], callback: (a) => send(`SCHEDULE ${a.options.mode}`) },
		stream: { name: 'Stream on / off', options: [onOrOff('ON')], callback: (a) => send(`STREAM ${a.options.mode}`) },
		presenter_next: { name: 'Presenter — next step', options: [], callback: () => send('NEXT') },
		presenter_prev: { name: 'Presenter — previous step', options: [], callback: () => send('PREV') },
		// The cue stack: GO always sends the standby id this instance last saw, so a GO that
		// races a standby move is refused ("ERR standby moved") instead of firing the wrong cue.
		cue_go: { name: 'Cue stack — GO (the standby cue)', options: [], callback: () => send(`CUE GO ${ctx.standbyId()}`.trim()) },
		cue_standby: {
			name: 'Cue stack — standby',
			options: [
				{ type: 'dropdown', id: 'mode', label: 'Move', default: 'NEXT', choices: [{ id: 'NEXT', label: 'Next cue' }, { id: 'PREV', label: 'Previous cue' }, { id: 'NUMBER', label: 'A cue number or name' }] },
				text('cue', 'Cue number or name (when chosen above)'),
			],
			callback: (a) => {
				if (a.options.mode !== 'NUMBER') return send(`CUE STANDBY ${a.options.mode}`)
				const c = clean(a.options.cue)
				if (c) send(`CUE STANDBY ${c}`)
			},
		},
		cue_hold: {
			name: 'Cue stack — HOLD',
			options: [onOff()],
			callback: (a) => {
				const mode = a.options.mode === 'TOGGLE' ? (ctx.state().cuestack?.hold ? 'OFF' : 'ON') : a.options.mode
				send(`CUE HOLD ${mode}`)
			},
		},
		cue_arm: {
			name: 'Cue stack — ARM (needs "remotes may arm" in the Remote tab)',
			options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'ON', choices: [{ id: 'ON', label: 'Arm' }, { id: 'OFF', label: 'Disarm' }] }],
			callback: (a) => send(`CUE ARM ${a.options.mode}`),
		},
		stop_all: { name: 'STOP ALL (audio, break music, VOGs, stingers, tone — never outputs, blackout or the stream)', options: [], callback: () => send('STOPALL') },
		// The running order's clock: the day slipped, resumed, or caught up.
		plan: {
			name: 'Plan — the day slips by a delta, resumes now, or catches up before the next break',
			options: [
				{ type: 'dropdown', id: 'mode', label: 'Do', default: 'SHIFT', choices: [{ id: 'SHIFT', label: 'Shift every planned start by the delta below' }, { id: 'RESUME', label: 'Resume now (the standby cue starts the clock)' }, { id: 'CATCHUP', label: 'Catch up before the next break' }] },
				text('delta', 'Delta — +2:00, -0:30, +90, -2m', '+1:00'),
			],
			callback: (a) => {
				if (a.options.mode === 'SHIFT') { const d = clean(a.options.delta); if (d) send(`PLAN SHIFT ${d}`); return }
				send(`PLAN ${a.options.mode}`)
			},
		},
		countdown_follow: { name: 'Countdown — follow the running order (its target is the standby cue\'s planned start)', options: [onOrOff('ON')], callback: (a) => send(`COUNTDOWN FOLLOW ${a.options.mode}`) },
		screen: { name: 'Screen on/off/toggle', options: [screenN, onOff()], callback: (a) => send(`SCREEN ${a.options.n} ${a.options.mode}`) },
		// A look on one screen alone: its picture for that screen lands there as the screen's own pattern; every other screen stays.
		screen_look: {
			name: 'Screen — a look on this screen alone (every other screen stays)',
			options: [screenN, text('look', 'Look (name or id)')],
			callback: (a) => { const look = clean(a.options.look); if (look) send(`SCREEN ${a.options.n} LOOK ${look}`) },
		},
		screen_program: { name: 'Screen — back to the program (drops its own picture)', options: [screenN], callback: (a) => send(`SCREEN ${a.options.n} PROGRAM`) },
		// Round 63 — the tile's own CUT / TAKE: the desk's preview to this one screen, as its own picture (OWN lights up); the programme and every other screen stay. EDIT SAFE must be open on the desk.
		screen_take: {
			name: 'Screen — CUT / TAKE the desk\'s preview to this screen alone (it becomes the screen\'s own picture; every other screen stays)',
			options: [screenN, { type: 'dropdown', id: 'mode', label: 'How', default: 'TAKE', choices: [{ id: 'TAKE', label: 'TAKE — with the transition' }, { id: 'CUT', label: 'CUT — instantly' }] }],
			callback: (a) => send(`SCREEN ${a.options.n} ${a.options.mode === 'CUT' ? 'CUT' : 'TAKE'}`),
		},
		review: { name: 'Review — the preview on every multiview (toggle / on / off)', options: [onOff()], callback: (a) => send(`REVIEW ${a.options.mode}`) },
		weather: {
			name: 'Weather — the chip on air, off, or its view',
			options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'TOGGLE', choices: [
				{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' },
				{ id: 'NOW', label: 'View: now (this hour)' }, { id: 'DAY', label: 'View: the rest of today' }, { id: 'TOMORROW', label: 'View: tomorrow' },
			] }],
			callback: (a) => send(`WEATHER ${a.options.mode}`),
		},
		freeze: {
			name: 'Freeze — every output holds its frame (toggle / on / off)',
			options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'TOGGLE', choices: [{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'Freeze' }, { id: 'OFF', label: 'Release' }] }],
			callback: (a) => send(`FREEZE ${a.options.mode}`),
		},
		// A fade to black — or up again — over the seconds given (0 = the show's transition time), where the target says:
		// empty = every screen (a blackout with a fade of its own); SCREEN 2, GROUP A, FOCUSED, TICKED or GROUPS fade that part alone.
		fade: {
			name: 'Fade to black / fade up over a time — every screen, or one screen, a group, the focused or ticked tiles',
			options: [
				{ type: 'dropdown', id: 'dir', label: 'Direction', default: 'DOWN', choices: [{ id: 'DOWN', label: 'To black' }, { id: 'UP', label: 'Up (lift it)' }] },
				{ type: 'number', id: 'secs', label: "Seconds (0 = the show's transition time)", default: 2, min: 0, max: 600, step: 0.5 },
				text('target', 'Where (empty = every screen; SCREEN 2, GROUP A, FOCUSED, TICKED, GROUPS)'),
			],
			callback: (a) => {
				const where = clean(a.options.target)
				send(`${a.options.dir === 'UP' ? 'FADEUP' : 'FADE'}${a.options.secs > 0 ? ' ' + a.options.secs : ''}${where ? ' ' + where : ''}`)
			},
		},
		look_back: { name: 'Previous look — back on air', options: [], callback: () => send('LOOKBACK') },
		screen_lock: {
			name: 'Screen lock / unlock (locked, it keeps its picture through looks, cues and TAKE)',
			options: [screenN, { type: 'dropdown', id: 'mode', label: 'Mode', default: 'TOGGLE', choices: [{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'Lock' }, { id: 'OFF', label: 'Unlock' }] }],
			callback: (a) => send(`LOCK ${a.options.n} ${a.options.mode}`),
		},
		group: {
			name: 'Canvas group on/off',
			options: [text('letter', 'Canvas letter (A, B…)', 'A'), onOrOff('ON')],
			callback: (a) => { const l = clean(a.options.letter); if (l) send(`GROUP ${l} ${a.options.mode}`) },
		},
		// The audio playlist: play / resume the list, stop, step it, a track by its place or its name, the level.
		audio: {
			name: 'Audio playlist',
			options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'PLAY', choices: [{ id: 'PLAY', label: 'Play / resume the list' }, { id: 'STOP', label: 'Stop' }, { id: 'NEXT', label: 'Next track' }, { id: 'PREV', label: 'Previous track' }] }],
			callback: (a) => send(`AUDIO ${a.options.mode}`),
		},
		audio_item: {
			name: 'Audio playlist — play track (by number, its place in the list)',
			options: [{ type: 'number', id: 'n', label: "Track number (Audio page order, the folders' files after the rows)", default: 1, min: 1, max: 999 }],
			callback: (a) => send(`AUDIO PLAY ${a.options.n}`),
		},
		audio_name: { name: 'Audio playlist — play track (by name)', options: [text('name', "Track name (a row's name, or the file's)")], callback: (a) => { const n = clean(a.options.name); if (n) send(`AUDIO PLAY ${n}`) } },
		audio_level: { name: 'Audio playlist level', options: [{ type: 'number', id: 'n', label: 'Level (0–125 %)', default: 100, min: 0, max: 125 }], callback: (a) => send(`AUDIO VOL ${a.options.n}`) },
		tone: { name: 'Soundcheck tone', options: [onOrOff('ON')], callback: (a) => send(`TONE ${a.options.mode}`) },
		// The live duck: music, stinger sounds and clip audio make way for an announcement from the room (a VOG never ducks). A latch — STOP ALL leaves it.
		duck: { name: 'Live duck (make way for an announcement from the room)', options: [onOff()], callback: (a) => send(`DUCK ${a.options.mode}`) },
		stinger: { name: 'Fire stinger (by number)', options: [{ type: 'number', id: 'n', label: 'Stinger number (Audio tab order)', default: 1, min: 1, max: 32 }], callback: (a) => send(`STINGER ${a.options.n}`) },
		stinger_name: { name: 'Fire stinger (by name)', options: [text('name', 'Stinger name')], callback: (a) => { const n = clean(a.options.name); if (n) send(`STINGER ${n}`) } },
		stinger_stop: { name: 'Stop VOG / stinger (a held frame reverts; the ending is cancelled)', options: [], callback: () => send('STINGER STOP') },
		// Lower thirds: a design by number (Lower thirds page order) or name goes on air over the show; OFF takes it off.
		lower_third: { name: 'Lower third on (by number, Lower thirds page order)', options: [{ type: 'number', id: 'n', label: 'Design number', default: 1, min: 1, max: 64 }], callback: (a) => send(`LT ${a.options.n}`) },
		lower_third_name: { name: 'Lower third on (by name)', options: [text('name', 'Design name')], callback: (a) => { const n = clean(a.options.name); if (n) send(`LT ${n}`) } },
		lower_third_off: { name: 'Lower third off (leaves the way it was designed to)', options: [], callback: () => send('LT OFF') },
		// The library: a person by number (Lower thirds page order) or name into a design — the one on air when
		// blank (else the first) — and on air. A name that is not in the library is refused: it never reaches the screen.
		lower_third_person: {
			name: 'Lower third — a person from the library (by number)',
			options: [
				{ type: 'number', id: 'n', label: 'Person number (Lower thirds page order)', default: 1, min: 1, max: 200 },
				text('design', 'Design (number or name; blank = the one on air, else the first)'),
			],
			callback: (a) => { const d = clean(a.options.design); send(d ? `LT ${d} WITH ${a.options.n}` : `PERSON ${a.options.n}`) },
		},
		lower_third_person_name: {
			name: 'Lower third — a person from the library (by name)',
			options: [text('name', 'Person name'), text('design', 'Design (number or name; blank = the one on air, else the first)')],
			callback: (a) => {
				const n = clean(a.options.name)
				if (!n) return
				const d = clean(a.options.design)
				send(d ? `LT ${d} WITH ${n}` : `PERSON ${n}`)
			},
		},
		// The sign-off flow (needs EDIT SAFE on the desk): a design — with a person — into the preview, where the
		// PREVIEW pane, the multiview's Preview tile and REVIEW show it; TAKE puts it on air and clears the preview;
		// UPDATE pushes an edit made while a design is on air across in place.
		lower_third_preview: {
			name: 'Lower third to preview (by number; blank person = as designed)',
			options: [
				{ type: 'number', id: 'n', label: 'Design number (Lower thirds page order; 0 = the one in the preview, on air, or the default)', default: 1, min: 0, max: 64 },
				text('person', 'Person (number or name; blank = as designed)'),
			],
			callback: (a) => {
				const design = a.options.n > 0 ? String(a.options.n) : ''
				const person = clean(a.options.person)
				if (!design && !person) return
				send(design ? (person ? `LT PREVIEW ${design} WITH ${person}` : `LT PREVIEW ${design}`) : `LT PREVIEW WITH ${person}`)
			},
		},
		lower_third_preview_name: {
			name: 'Lower third to preview (by name)',
			options: [text('name', 'Design name'), text('person', 'Person (number or name; blank = as designed)')],
			callback: (a) => {
				const n = clean(a.options.name)
				if (!n) return
				const p = clean(a.options.person)
				send(p ? `LT PREVIEW ${n} WITH ${p}` : `LT PREVIEW ${n}`)
			},
		},
		lower_third_take: { name: 'Lower third TAKE — the one in the preview to air (the preview clears)', options: [], callback: () => send('LT TAKE') },
		lower_third_update: { name: 'Lower third UPDATE — the design on air replaced by the design as it is now, in place', options: [], callback: () => send('LT UPDATE') },
		lower_third_preview_off: { name: 'Lower third preview clear', options: [], callback: () => send('LT PREVIEW OFF') },
		// The web page on air (or one named by its nickname or a word of its address): a page action the
		// page's service maps to its key or its player — next / present on a deck, play / mute on YouTube —
		// or a key of your own; a click in percent; typed text; a reload; another address.
		web_action: {
			name: 'Web page — an action on the page on air (next slide, present, play, black…)',
			options: [
				{
					type: 'dropdown', id: 'action', label: 'Action', default: 'next',
					choices: [
						{ id: 'next', label: 'Next (slide / video)' }, { id: 'prev', label: 'Previous' }, { id: 'first', label: 'First slide' }, { id: 'last', label: 'Last slide' },
						{ id: 'present', label: 'Present — start the deck' }, { id: 'exit', label: 'Exit (Escape)' }, { id: 'play', label: 'Play / pause' }, { id: 'pause', label: 'Pause' },
						{ id: 'mute', label: 'Mute / unmute' }, { id: 'restart', label: 'Restart the video' }, { id: 'forward', label: '+10 s' }, { id: 'rewind', label: '−10 s' },
						{ id: 'black', label: 'Black slide' }, { id: 'white', label: 'White slide' }, { id: 'captions', label: 'Captions' }, { id: 'fullscreen', label: 'Full screen' },
						{ id: 'reload', label: 'Reload the page' }, { id: 'key', label: 'A key of your own (below)' },
					],
				},
				text('key', 'Key, when the action is "a key of your own": ArrowRight, Space, k, Ctrl+Shift+F5'),
				text('page', 'Page (blank = the page on air; else its nickname or a word of its address)'),
			],
			callback: (a) => {
				const on = clean(a.options.page) ? ` ON ${clean(a.options.page)}` : ''
				if (a.options.action === 'reload') return send(`WEB RELOAD${on}`)
				const key = a.options.action === 'key' ? clean(a.options.key) : a.options.action
				if (key) send(`WEB KEY ${key}${on}`)
			},
		},
		web_click: {
			name: 'Web page — click at a spot (percent of the page)',
			options: [
				{ type: 'number', id: 'x', label: 'X (% from the left)', default: 50, min: 0, max: 100 },
				{ type: 'number', id: 'y', label: 'Y (% from the top)', default: 50, min: 0, max: 100 },
				text('page', 'Page (blank = the page on air)'),
			],
			callback: (a) => send(`WEB CLICK ${a.options.x} ${a.options.y}${clean(a.options.page) ? ` ON ${clean(a.options.page)}` : ''}`),
		},
		web_type: { name: "Web page — type text into the field that has the page's focus", options: [text('text', 'Text')], callback: (a) => { if (clean(a.options.text)) send(`WEB TYPE ${a.options.text}`) } },
		web_open: {
			name: "Web page — send the page's browser to another address",
			options: [text('address', 'Address'), text('page', 'Page (blank = the page on air)')],
			callback: (a) => { const addr = clean(a.options.address); if (addr) send(`WEB OPEN ${addr}${clean(a.options.page) ? ` ON ${clean(a.options.page)}` : ''}`) },
		},
		// The routing matrix: which soundtrack goes where — on or off, a source on a destination at a level, and what a VOG does there.
		audio_routing: {
			name: 'Audio routing — the matrix on, off or toggled',
			options: [{ type: 'dropdown', id: 'mode', label: 'Routing', default: 'toggle', choices: [{ id: 'on', label: 'On (seeds audio-follows-video when empty)' }, { id: 'off', label: 'Off — the two wires as before' }, { id: 'toggle', label: 'Toggle' }] }],
			callback: (a) => send(`AUDIO ROUTING ${String(a.options.mode).toUpperCase()}`),
		},
		audio_route: {
			name: 'Audio routing — put a source on a destination (or take it off)',
			options: [
				{
					type: 'dropdown', id: 'source', label: 'Source', default: 'programme',
					choices: [
						{ id: 'programme', label: 'Programme (the picture on air)' }, { id: 'preview', label: 'Preview' }, { id: 'music', label: 'Music (playlist)' },
						{ id: 'vog', label: 'VOG' }, { id: 'sting', label: 'Stingers' }, { id: 'tone', label: 'Tone' }, { id: 'screen', label: "A screen's own picture (name below)" },
					],
				},
				text('screen', 'Screen, when the source is a screen: its name or id'),
				text('destination', "Destination: an output's name (or its label on the Audio page), NDI <send>, computer"),
				{ type: 'number', id: 'db', label: 'Level dB (0 = unity, −60 = off)', default: 0, min: -60, max: 12 },
				{ type: 'checkbox', id: 'off', label: 'Take it off instead', default: false },
			],
			callback: (a) => {
				const source = a.options.source === 'screen' ? `screen ${clean(a.options.screen)}` : a.options.source
				const to = clean(a.options.destination)
				if (!to || (a.options.source === 'screen' && !clean(a.options.screen))) return
				if (a.options.off) return send(`AUDIO UNROUTE ${source} FROM ${to}`)
				const db = Number(a.options.db) || 0
				send(db === 0 ? `AUDIO ROUTE ${source} TO ${to}` : `AUDIO ROUTE ${source} TO ${to} AT ${db}`)
			},
		},
		audio_vog: {
			name: 'Audio routing — what a VOG does on a destination',
			options: [
				text('destination', "Destination: an output's name, NDI <send>, computer"),
				{ type: 'dropdown', id: 'mode', label: 'A VOG', default: 'duck', choices: [{ id: 'duck', label: 'Ducks the rest' }, { id: 'replace', label: 'Replaces the rest' }, { id: 'leave', label: 'Stays off it' }] },
			],
			callback: (a) => { const to = clean(a.options.destination); if (to) send(`AUDIO VOG ${to} ${String(a.options.mode).toUpperCase()}`) },
		},
		// The armed web VT: the page's video held at a point and played from it the moment the page goes to air.
		web_vt: {
			name: 'Web page — the armed VT (arm, mark, disarm)',
			options: [
				{
					type: 'dropdown', id: 'mode', label: 'Do', default: 'arm',
					choices: [
						{ id: 'arm', label: 'ARM at the mark (else where the player is now)' }, { id: 'arm_at', label: 'ARM at a time (below)' },
						{ id: 'mark', label: 'MARK where the player is now' }, { id: 'mark_at', label: 'MARK at a time (below)' }, { id: 'disarm', label: 'DISARM' },
					],
				},
				text('time', 'Time, for "at a time": 1:23, 83, 1m23s'),
				text('page', 'Page (blank = the page on air, else the page in the preview)'),
			],
			callback: (a) => {
				const on = clean(a.options.page) ? ` ON ${clean(a.options.page)}` : ''
				const time = clean(a.options.time)
				switch (a.options.mode) {
					case 'arm_at': return time ? send(`WEB ARM ${time}${on}`) : undefined
					case 'mark': return send(`WEB MARK${on}`)
					case 'mark_at': return time ? send(`WEB MARK ${time}${on}`) : undefined
					case 'disarm': return send(`WEB DISARM${on}`)
					default: return send(`WEB ARM${on}`)
				}
			},
		},
		// The deck (a PDF presentation) on air: its pages from a key. Presenter NEXT / BACK turn it too, and past the last page the caller's stack resumes.
		deck_page: {
			name: 'Deck — turn the PDF on air',
			options: [
				{ type: 'dropdown', id: 'mode', label: 'Turn', default: 'NEXT', choices: [{ id: 'NEXT', label: 'Next page' }, { id: 'PREV', label: 'Previous page' }, { id: 'FIRST', label: 'First page' }, { id: 'LAST', label: 'Last page' }, { id: 'PAGE', label: 'A page number (below)' }] },
				{ type: 'number', id: 'n', label: 'Page (when Turn is a page number)', default: 1, min: 1, max: 9999 },
			],
			callback: (a) => send(a.options.mode === 'PAGE' ? `DECK PAGE ${a.options.n}` : `DECK ${a.options.mode}`),
		},
		// The clip on air — the caller's VT clock: the rehearsal's skip to its last seconds, and the top again.
		video_end: { name: "Video — jump to the clip's last seconds (rehearsal)", options: [{ type: 'number', id: 'seconds', label: 'Seconds before the end', default: 10, min: 0, max: 3600 }], callback: (a) => send(`VIDEO END ${a.options.seconds}`) },
		video_restart: { name: 'Video — the clip on air from the top', options: [], callback: () => send('VIDEO RESTART') },
		// VOG / STING name the same library by the same number and only assert the kind: a key that says VOG never fires a stinger.
		vog: { name: 'Fire VOG (by number, Audio-page order)', options: [{ type: 'number', id: 'n', label: 'Library number (Audio page order)', default: 1, min: 1, max: 32 }], callback: (a) => send(`VOG ${a.options.n}`) },
		vog_name: { name: 'Fire VOG (by name)', options: [text('name', 'VOG name')], callback: (a) => { const n = clean(a.options.name); if (n) send(`VOG ${n}`) } },
		sting: { name: 'Fire stinger (by number, Audio-page order)', options: [{ type: 'number', id: 'n', label: 'Library number (Audio page order)', default: 1, min: 1, max: 32 }], callback: (a) => send(`STING ${a.options.n}`) },
		sting_name: { name: 'Fire stinger (by name)', options: [text('name', 'Stinger name')], callback: (a) => { const n = clean(a.options.name); if (n) send(`STING ${n}`) } },
		// Break music (Spotify): the MUSIC verbs. With the feature off in Patterns these answer OK and do nothing.
		music: {
			name: 'Break music',
			options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'PLAY', choices: [{ id: 'PLAY', label: 'Play / resume' }, { id: 'PAUSE', label: 'Pause' }, { id: 'NEXT', label: 'Skip track' }] }],
			callback: (a) => send(`MUSIC ${a.options.mode}`),
		},
		music_item: { name: 'Break music — play entry (by number)', options: [{ type: 'number', id: 'n', label: 'Break-music number (Audio page order)', default: 1, min: 1, max: 32 }], callback: (a) => send(`MUSIC PLAY ${a.options.n}`) },
		music_name: { name: 'Break music — play entry (by name)', options: [text('name', 'Break-music name')], callback: (a) => { const n = clean(a.options.name); if (n) send(`MUSIC PLAY ${n}`) } },
		music_level: { name: 'Break music level', options: [{ type: 'number', id: 'n', label: "Level (0–100, the Spotify device's own volume)", default: 60, min: 0, max: 100 }], callback: (a) => send(`MUSIC VOL ${a.options.n}`) },
		// The clock overlay: on / off / toggle, its hours, the seconds and the date line — CLOCK on the wire.
		clock: {
			name: 'Clock — the clock overlay: on, off, toggle, 12 / 24 h, seconds, date',
			options: [{ type: 'dropdown', id: 'mode', label: 'Do', default: 'TOGGLE', choices: [
				{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' }, { id: '12', label: '12-hour' }, { id: '24', label: '24-hour' },
				{ id: 'SECONDS TOGGLE', label: 'Seconds: toggle' }, { id: 'SECONDS ON', label: 'Seconds: on' }, { id: 'SECONDS OFF', label: 'Seconds: off' },
				{ id: 'DATE TOGGLE', label: 'Date: toggle' }, { id: 'DATE ON', label: 'Date: on' }, { id: 'DATE OFF', label: 'Date: off' },
			] }],
			callback: (a) => send(`CLOCK ${a.options.mode}`),
		},
		// The message overlay: these words and on, on / off / toggle with the words kept, a ticker or a still line — MESSAGE on the wire.
		message: {
			name: 'Message — the words on screen, on / off / toggle, scrolling',
			options: [
				{ type: 'dropdown', id: 'mode', label: 'Do', default: 'SAY', choices: [
					{ id: 'SAY', label: 'Show these words (below)' }, { id: 'ON', label: 'On (the current words)' }, { id: 'OFF', label: 'Off (the words kept)' }, { id: 'TOGGLE', label: 'Toggle' },
					{ id: 'SCROLL TOGGLE', label: 'Scroll: toggle' }, { id: 'SCROLL ON', label: 'Scroll: on (a ticker)' }, { id: 'SCROLL OFF', label: 'Scroll: off (a still line)' },
				] },
				text('text', 'The words (when "Show these words")', 'Doors open at 7'),
			],
			callback: (a) => {
				if (a.options.mode !== 'SAY') return send(`MESSAGE ${a.options.mode}`)
				const t = clean(a.options.text)
				send(t ? `MESSAGE ${t}` : 'MESSAGE ON')
			},
		},
		// The countdown: a duration from now, a time of day, the label, stop — COUNTDOWN on the wire.
		countdown: {
			name: 'Countdown — toggle, start (minutes), count down to a time, the label, stop',
			options: [
				{ type: 'dropdown', id: 'mode', label: 'Do', default: 'START', choices: [
					{ id: 'TOGGLE', label: 'Toggle — on as set up on the desk, off again' }, { id: 'START', label: 'Start — the minutes below (blank = as set up on the desk)' },
					{ id: 'TO', label: 'Count down to the time below' }, { id: 'LABEL', label: 'Set the label below' }, { id: 'STOP', label: 'Stop' },
				] },
				text('minutes', 'Minutes — 5, 2.5, 2:30, 90s', '5'),
				text('time', 'Time of day — 19:30', '19:30'),
				text('label', 'The words over the digits', 'SHOW STARTS IN'),
			],
			callback: (a) => {
				const mode = a.options.mode
				if (mode === 'TOGGLE') return send('COUNTDOWN TOGGLE')
				if (mode === 'STOP') return send('COUNTDOWN STOP')
				if (mode === 'TO') { const t = clean(a.options.time); if (t) send(`COUNTDOWN TO ${t}`); return }
				if (mode === 'LABEL') { const l = clean(a.options.label); if (l) send(`COUNTDOWN LABEL ${l}`); return }
				const m = clean(a.options.minutes)
				send(m ? `COUNTDOWN START ${m}` : 'COUNTDOWN START')
			},
		},
		logo: { name: 'Logo — the brand logo overlay (toggle / on / off)', options: [onOff()], callback: (a) => send(`LOGO ${a.options.mode}`) },
		pip: { name: 'PiP — the picture-in-picture inset (toggle / on / off)', options: [onOff()], callback: (a) => send(`PIP ${a.options.mode}`) },
		overlays_off: { name: 'Overlays — every overlay off (the clock, the message, the countdown, the logo, the PiP, the weather chip)', options: [], callback: () => send('OVERLAYS OFF') },
		pattern: { name: 'Pattern — the kind of picture on air (Grid, ColorBars, LedWall, Particles, Fractal…)', options: [text('kind', 'Kind', 'Grid')], callback: (a) => { const k = clean(a.options.kind); if (k) send(`PATTERN ${k}`) } },
		screen_pattern: {
			name: 'Screen — a kind of picture on this screen alone, live (every other screen stays)',
			options: [screenN, text('kind', 'Kind', 'Grid')],
			callback: (a) => { const k = clean(a.options.kind); if (k) send(`SCREEN ${a.options.n} PATTERN ${k}`) },
		},
		// Round 60 — the staged verbs: a picture on a screen's PVW in the desk's preview and nowhere else. EDIT SAFE opens by itself,
		// the audience sees nothing until the desk's CUT or TAKE, so a key here can build the next picture without ever going live.
		screen_stage: {
			name: 'Screen PVW — stage a look, a preset, a kind of picture, the programme, or the look on air back (RESET) on this screen\'s preview; nothing changes on air until CUT or TAKE',
			options: [screenN, stagedWhat, text('name', 'Look / preset / kind (not for PROGRAM or RESET)')],
			callback: (a) => { const line = stagedLine(`SCREEN ${a.options.n} PVW`, a.options.what, a.options.name); if (line) send(line) },
		},
		pvw: {
			name: 'Preview — the programme\'s picture in the desk\'s preview: a look (whole), a preset, a kind of picture, the look on air back (RESET), or what is on air to edit (PROGRAM); nothing changes on air until CUT or TAKE',
			options: [stagedWhat, text('name', 'Look / preset / kind (not for PROGRAM or RESET)')],
			callback: (a) => { const line = stagedLine('PVW', a.options.what, a.options.name); if (line) send(line) },
		},
		section: { name: 'Playlist — show part on air', options: [{ type: 'number', id: 'n', label: 'Part number (Media tab order)', default: 1, min: 1, max: 32 }], callback: (a) => send(`SECTION ${a.options.n}`) },
		// ---- the stage: the speaker's timer and the messages to the stage ------------------------------------
		stage_message: {
			name: "Stage — a message to the speaker's stage page (or the crew's), kept until its ACK",
			options: [
				{ type: 'dropdown', id: 'channel', label: 'To', default: 'speaker', choices: [{ id: 'speaker', label: 'The speaker' }, { id: 'crew', label: 'The crew' }] },
				text('text', 'The words', 'Wrap up'),
			],
			callback: (a) => { const t = clean(a.options.text); if (t) send(a.options.channel === 'crew' ? `STAGE CREW ${t}` : `STAGE MESSAGE ${t}`) },
		},
		stage_clear: { name: 'Stage — every pending message marked seen', options: [], callback: () => send('STAGE CLEAR') },
		timer: {
			name: 'Stage timer — pause, resume, pause / resume, add or take seconds, flash the stage pages',
			options: [
				{ type: 'dropdown', id: 'mode', label: 'Do', default: 'TOGGLE', choices: [
					{ id: 'TOGGLE', label: 'Pause, or resume when paused' }, { id: 'PAUSE', label: 'Pause' }, { id: 'RESUME', label: 'Resume' },
					{ id: 'ADD', label: 'Add the seconds below' }, { id: 'MINUS', label: 'Take the seconds below' }, { id: 'FLASH', label: 'Flash the stage pages' },
				] },
				{ type: 'number', id: 'seconds', label: 'Seconds (for add / take)', default: 60, min: 1, max: 3600 },
			],
			callback: (a) => {
				const mode = a.options.mode
				if (mode === 'TOGGLE') return send(ctx.state().stage?.timer?.phase === 'paused' ? 'TIMER RESUME' : 'TIMER PAUSE')
				if (mode === 'ADD' || mode === 'MINUS') return send(`TIMER ${mode} ${a.options.seconds}`)
				send(`TIMER ${mode}`)
			},
		},
		// ---- the twin, the nodes, the arcade, the audience ------------------------------------------------------
		twin: {
			name: 'Twin — take over (on a standby), stand by again, take back (on the main); the wire\'s own fences apply',
			options: [{ type: 'dropdown', id: 'mode', label: 'Do', default: 'TAKEOVER', choices: [
				{ id: 'TAKEOVER', label: 'TAKE OVER — the standby runs the show' }, { id: 'TAKEOVER FORCE', label: 'TAKE OVER anyway, over a refusal' },
				{ id: 'STANDBY', label: 'STAND BY again — follow the main' }, { id: 'TAKEBACK', label: 'TAKE BACK — the main takes the show back' },
			] }],
			callback: (a) => send(`TWIN ${a.options.mode}`),
		},
		showlock: { name: 'Show lock — the machine held for the show, or released', options: [onOrOff('ON')], callback: (a) => send(`SHOWLOCK ${a.options.mode}`) },
		arcade: {
			name: 'Arcade — start a game, stop, pause, resume, the attract screen, the picture on NDI',
			options: [
				{ type: 'dropdown', id: 'mode', label: 'Do', default: 'START', choices: [
					{ id: 'START', label: 'Start the game below' }, { id: 'STOP', label: 'Stop (the title card)' }, { id: 'PAUSE', label: 'Pause' }, { id: 'RESUME', label: 'Resume' },
					{ id: 'ATTRACT', label: 'Attract — the house plays itself' }, { id: 'NDI ON', label: 'NDI on' }, { id: 'NDI OFF', label: 'NDI off' },
				] },
				text('game', 'Game — pong, snake, breakout', 'pong'),
				{ type: 'number', id: 'players', label: 'Players (for start)', default: 2, min: 1, max: 4 },
			],
			callback: (a) => {
				if (a.options.mode === 'START') { const g = clean(a.options.game); if (g) send(`ARCADE START ${g} ${a.options.players}`); return }
				send(`ARCADE ${a.options.mode}`)
			},
		},
		arcade_key: {
			name: "Arcade — a pad's key (UP DOWN LEFT RIGHT A B START), tapped, held down or let go",
			options: [
				{ type: 'number', id: 'pad', label: 'Pad (1–4)', default: 1, min: 1, max: 4 },
				{ type: 'dropdown', id: 'button', label: 'Key', default: 'A', choices: ['UP', 'DOWN', 'LEFT', 'RIGHT', 'A', 'B', 'START'].map((k) => ({ id: k, label: k })) },
				{ type: 'dropdown', id: 'how', label: 'How', default: 'TAP', choices: [{ id: 'TAP', label: 'Tap' }, { id: 'DOWN', label: 'Down (hold)' }, { id: 'UP', label: 'Up (let go)' }] },
			],
			callback: (a) => send(`ARCADE KEY ${a.options.pad} ${a.options.button} ${a.options.how}`),
		},
		play: {
			name: 'Audience play — the next question, open, close, reveal, the wall\'s picture, the queue on auto',
			options: [
				{ type: 'dropdown', id: 'mode', label: 'Do', default: 'NEXT', choices: [
					{ id: 'NEXT', label: 'Open the next question' }, { id: 'OPEN', label: 'Open (the next draft)' }, { id: 'CLOSE', label: 'Close the open question' }, { id: 'REVEAL', label: 'Reveal the answer and results' },
					{ id: 'SHOW', label: 'The wall shows what is named below' }, { id: 'AUTO ON', label: 'Words straight to the wall' }, { id: 'AUTO OFF', label: 'Words wait for the host' },
				] },
				{ type: 'dropdown', id: 'show', label: 'The wall (for show)', default: 'results', choices: ['join', 'results', 'leaderboard', 'message', 'draughts', 'path', 'off'].map((k) => ({ id: k, label: k })) },
			],
			callback: (a) => send(a.options.mode === 'SHOW' ? `PLAY SHOW ${a.options.show}` : `PLAY ${a.options.mode}`),
		},
		// The escape hatch: a line as docs/REMOTE.md spells it, for a verb the module has no key for yet.
		raw: {
			name: 'A line of your own on the wire (docs/REMOTE.md)',
			options: [text('line', 'The line — SCREEN 3 ROLE confidence, CALIBRATE STATUS, ALIGN NUDGE 2 0', 'PING')],
			callback: (a) => { const l = clean(a.options.line); if (l) send(l) },
		},
	}
}
