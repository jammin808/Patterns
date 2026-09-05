// Bitfocus Companion module for Patterns — drives the TCP line protocol on the app's
// Companion port (default 9697) and consumes pushed STATE lines for live feedback.
import {
	InstanceBase,
	InstanceStatus,
	TCPHelper,
	Regex,
	runEntrypoint,
	combineRgb,
} from '@companion-module/base'

class PatternsInstance extends InstanceBase {
	constructor(internal) {
		super(internal)
		this.socket = null
		this.state = { blackout: false, looks: [], screens: [], presenter: { index: -1, count: 0 } }
		this.buffer = ''
		this.standbyId = '' // the standby id this instance last saw — every cue_go sends it
		this.showSignature = '' // the show's lists as last turned into presets — rebuilt only when they change
	}

	// ---- the show's lists, by place: what a bank key reads to label itself -------------------

	/** The name at a place in one of the show's lists, or '' when nothing is there. */
	bankName(kind, n) {
		const s = this.state
		switch (kind) {
			case 'look': return s.looks?.[n - 1]?.name ?? ''
			case 'look_f': return s.looks?.find((l) => l.slot === n)?.name ?? ''
			case 'lt': return s.lowerThirds?.[n - 1]?.name ?? ''
			case 'person': return s.people?.[n - 1]?.name ?? ''
			case 'stinger': return s.stingers?.[n - 1]?.name ?? ''
			case 'music': return s.music?.items?.[n - 1]?.name ?? ''
			case 'section': return s.sections?.[n - 1]?.name ?? ''
			case 'screen': return s.screens?.[n - 1]?.label ?? ''
			case 'cue': return this.upcoming()[n - 1]?.number ?? ''
			default: return ''
		}
	}

	/** The standby cue and the cues after it, in order — the cue bank's places 1… */
	upcoming() {
		const c = this.state.cuestack ?? {}
		return [c.standby, ...(c.next ?? [])].filter(Boolean)
	}

	/** Every bank variable from the state: look_1…16, look_f1…12, lt_1…8, person_1…8, stinger_1…8, music_1…6, section_1…6, screen_1…8, cue_1…7. */
	bankVariables() {
		const vars = {}
		for (let n = 1; n <= 16; n++) vars[`look_${n}`] = this.bankName('look', n)
		for (let f = 1; f <= 12; f++) vars[`look_f${f}`] = this.bankName('look_f', f)
		for (let n = 1; n <= 8; n++) {
			vars[`lt_${n}`] = this.bankName('lt', n)
			vars[`person_${n}`] = this.bankName('person', n)
			vars[`stinger_${n}`] = this.bankName('stinger', n)
			vars[`screen_${n}`] = this.bankName('screen', n)
		}
		for (let n = 1; n <= 6; n++) {
			vars[`music_${n}`] = this.bankName('music', n)
			vars[`section_${n}`] = this.bankName('section', n)
		}
		const up = this.upcoming()
		for (let k = 1; k <= 7; k++) {
			const row = up[k - 1]
			vars[`cue_${k}`] = row ? `${row.number ?? ''}\n${row.name ?? ''}`.trim() : ''
			vars[`cue_${k}_number`] = row?.number ?? ''
			vars[`cue_${k}_name`] = row?.name ?? ''
		}
		return vars
	}

	/** The show's lists as one string: when it changes, the per-show presets are built again. */
	showSignatureNow() {
		const s = this.state
		const names = (list) => (list ?? []).map((x) => x.name ?? x.label ?? '').join('|')
		return [names(s.looks), names(s.lowerThirds), names(s.people), (s.stingers ?? []).map((x) => `${x.kind}:${x.name}`).join('|'),
			names(s.music?.items), names(s.sections), names(s.screens), this.upcoming().map((c) => `${c.number} ${c.name}`).join('|')].join('#')
	}

	/** Presets built from the show itself — one key per look, design, person, stinger, track, part, screen and upcoming cue — rebuilt when the lists change. */
	refreshShowPresets() {
		const signature = this.showSignatureNow()
		if (signature === this.showSignature) return
		this.showSignature = signature
		this.setPresetDefinitions({ ...this.buildPresets(), ...this.buildShowPresets() })
	}

	async init(config) {
		this.config = config
		this.updateStatus(InstanceStatus.Connecting)
		this.setActionDefinitions(this.buildActions())
		this.setFeedbackDefinitions(this.buildFeedbacks())
		this.setVariableDefinitions(this.buildVariables())
		this.setPresetDefinitions(this.buildPresets())
		this.initSocket()
	}

	async destroy() {
		if (this.socket) {
			this.socket.destroy()
			this.socket = null
		}
	}

	async configUpdated(config) {
		this.config = config
		this.initSocket()
	}

	getConfigFields() {
		return [
			{ type: 'textinput', id: 'host', label: 'Patterns machine IP', width: 8, regex: Regex.IP, default: '127.0.0.1' },
			{ type: 'number', id: 'port', label: 'Companion (TCP) port — Remote tab in Patterns', width: 4, min: 1024, max: 65535, default: 9697 },
		]
	}

	initSocket() {
		if (this.socket) {
			this.socket.destroy()
			this.socket = null
		}
		if (!this.config?.host) {
			this.updateStatus(InstanceStatus.BadConfig, 'No host set')
			return
		}
		this.socket = new TCPHelper(this.config.host, this.config.port ?? 9697)
		this.socket.on('status_change', (status, message) => {
			this.updateStatus(status, message)
			if (status === InstanceStatus.Ok) {
				// The connection names itself, so the caller's history reads "GO from FOH deck".
				this.socket.send(`HELLO ${this.label ?? 'Companion'}\n`)
			} else {
				// Feedbacks reset on disconnect: a dead key must not stay green.
				this.state = { blackout: false, looks: [], screens: [], presenter: { index: -1, count: 0 } }
				this.standbyId = ''
				this.checkFeedbacks()
			}
		})
		this.socket.on('error', (err) => this.log('error', `Connection error: ${err.message}`))
		this.socket.on('data', (data) => {
			this.buffer += data.toString('utf8')
			let idx
			while ((idx = this.buffer.indexOf('\n')) >= 0) {
				const line = this.buffer.slice(0, idx).trim()
				this.buffer = this.buffer.slice(idx + 1)
				if (line.startsWith('STATE ')) this.onState(line.slice(6))
				else if (line.startsWith('ERR')) {
					this.log('warn', line)
					this.setVariableValues({ last_error: line.slice(4) })
				}
			}
		})
	}

	onState(json) {
		try {
			this.state = JSON.parse(json)
		} catch (e) {
			this.log('warn', `Bad STATE payload: ${e.message}`)
			return
		}
		const p = this.state.presenter ?? { index: -1, count: 0 }
		const c = this.state.cuestack ?? {}
		this.standbyId = c.standby?.id ?? ''
		this.setVariableValues({
			program: this.state.airLabel ?? '',
			cue_armed: c.armed ? 'ARMED' : 'off',
			cue_hold: c.hold ? 'HOLD' : 'off',
			cue_seq: String(c.seq ?? 0),
			cue_standby_number: c.standby?.number ?? '-',
			cue_standby_name: c.standby?.name ?? '',
			cue_next_number: c.next?.[0]?.number ?? '-',
			cue_next_name: c.next?.[0]?.name ?? '',
			cue_previous_number: c.previous?.number ?? '-',
			cue_previous_name: c.previous?.name ?? '',
			cue_last_outcome: c.last?.outcome ?? '',
			cue_confirm: c.confirm ?? '',
			blackout: this.state.blackout ? 'ON' : 'off',
			presenter_step: p.index >= 0 ? String(p.index + 1) : '-',
			presenter_count: String(p.count ?? 0),
			playlist: this.state.playlist ?? '',
			next_cue: this.state.nextCue ?? '',
			stinger: this.state.stingerPlaying ?? '',
			sting_hold: this.state.stingHold ?? '',
			duck: this.state.duck ? 'DUCK' : 'off',
			lower_third: this.state.lowerThird ?? '',
			lower_third_person: this.state.lowerThirdPerson ?? '',
			lower_third_preview: this.state.lowerThirdPreview ?? '',
			lower_third_preview_person: this.state.lowerThirdPreviewPerson ?? '',
			lower_third_default: this.state.lowerThirdDefault ?? '',
			lower_third_edited: this.state.lowerThirdEdited ? 'EDITED' : 'off',
			deck_page: this.state.deck?.count ? String(this.state.deck.page) : '',
			deck_count: this.state.deck?.count ? String(this.state.deck.count) : '',
			deck_file: this.state.deck?.file ?? '',
			web_page: this.state.web?.page ?? '',
			web_title: this.state.web?.title ?? '',
			web_service: this.state.web?.service ?? '',
			review: this.state.review ? 'ON' : 'off',
			freeze: this.state.frozen ? 'FROZEN' : 'off',
			previous_look: this.state.previousLook ?? '',
			music: this.state.music?.now ?? '',
			music_state: this.state.music?.playing ? 'PLAYING' : 'paused',
			music_level: String(this.state.music?.level ?? 0),
			music_device: this.state.music?.device ?? '',
			health: this.state.health ?? '',
			machine_cpu: this.state.machine?.cpu >= 0 ? `${this.state.machine.cpu}%` : 'n/a',
			machine_fps: String(this.state.machine?.fps ?? 0),
			machine_power: this.state.machine?.battery ? 'BATTERY' : 'mains',
			machine_advice: String(this.state.machine?.advice ?? 0),
			air_look: this.state.airLook ?? '',
			preview_look: this.state.previewLook ?? '',
			pattern: this.state.pattern ?? '',
			...this.bankVariables(),
		})
		this.checkFeedbacks('blackout', 'screen_enabled', 'screen_locked', 'screen_armed', 'screen_own', 'audio_playing', 'stinger_playing', 'music_playing',
			'vog_playing', 'sting_playing', 'sting_hold', 'duck_on', 'lower_third_on', 'lower_third_person_is', 'lower_third_preview', 'lower_third_edited',
			'review_on', 'frozen', 'cue_armed', 'cue_hold', 'cue_standby_is', 'cue_confirm_required', 'cue_last_failed',
			'web_on_air', 'deck_on_air', 'look_on_air', 'look_bank_on_air', 'look_f_on_air', 'look_preview', 'slot_empty')
		this.refreshShowPresets()
	}

	send(cmd) {
		if (this.socket?.isConnected) this.socket.send(cmd + '\n')
		else this.log('warn', `Not connected — dropped: ${cmd}`)
	}

	buildActions() {
		const send = (cmd) => this.send(cmd)
		return {
			// Action ids stay 'go' / 'stop' so saved pages keep working; the verbs are the
			// outputs transport. GO is reserved for the cue stack.
			go: { name: 'Outputs on (open output windows)', options: [], callback: () => send('OUTPUTS ON') },
			stop: { name: 'Outputs off (close output windows)', options: [], callback: () => send('OUTPUTS OFF') },
			identify: { name: 'Identify screens', options: [], callback: () => send('IDENTIFY') },
			blackout: {
				name: 'Blackout',
				options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'TOGGLE',
					choices: [{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' }] }],
				callback: (a) => send(`BLACKOUT ${a.options.mode}`),
			},
			look_slot: {
				name: 'Apply look by F-key slot',
				options: [{ type: 'number', id: 'slot', label: 'Slot (1–12)', default: 1, min: 1, max: 12 }],
				callback: (a) => send(`LOOK ${a.options.slot}`),
			},
			look_name: {
				name: 'Apply look by name',
				options: [{ type: 'textinput', id: 'name', label: 'Look name', default: '' }],
				callback: (a) => send(`LOOK ${a.options.name}`),
			},
			// A bank key: the look at a place in the show's list — LOOK #n — so a row of keys labels itself
			// ($(patterns:look_n)) as looks are made, renamed or reordered, with no key to edit.
			look_bank: {
				name: 'Look — by its place in the show\'s list (bank key n; the name is $(patterns:look_n))',
				options: [{ type: 'number', id: 'n', label: 'Place in the list (1–16)', default: 1, min: 1, max: 16 }],
				callback: (a) => send(`LOOK #${a.options.n}`),
			},
			// The cue bank: place 1 is the standby cue, 2… the cues after it — a key per upcoming cue that
			// relabels itself on every GO. STANDBY moves the standby there; GO moves it and fires it.
			cue_bank: {
				name: 'Cue bank — the cue at a place (1 = the standby cue, 2… = the cues after it): standby, or standby and GO',
				options: [
					{ type: 'number', id: 'k', label: 'Place (1–7)', default: 1, min: 1, max: 7 },
					{ type: 'dropdown', id: 'mode', label: 'Do', default: 'STANDBY',
						choices: [{ id: 'STANDBY', label: 'Put it on standby' }, { id: 'GO', label: 'Standby and GO' }] },
				],
				callback: (a) => {
					const row = this.upcoming()[a.options.k - 1]
					if (!row) return this.log('warn', `Cue bank ${a.options.k}: no cue at that place`)
					if (a.options.k !== 1) send(`CUE STANDBY ${row.number || row.name}`)
					if (a.options.mode === 'GO') send(`CUE GO ${row.id ?? ''}`.trim())
				},
			},
			stream: {
				name: 'Stream on / off',
				options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'ON',
					choices: [{ id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' }] }],
				callback: (a) => send(`STREAM ${a.options.mode}`),
			},
			presenter_next: { name: 'Presenter — next step', options: [], callback: () => send('NEXT') },
			presenter_prev: { name: 'Presenter — previous step', options: [], callback: () => send('PREV') },
			// The cue stack: GO always sends the standby id this instance last saw, so a GO that
			// races a standby move is refused ("ERR standby moved") instead of firing the wrong cue.
			cue_go: { name: 'Cue stack — GO (the standby cue)', options: [], callback: () => send(`CUE GO ${this.standbyId}`.trim()) },
			cue_standby: {
				name: 'Cue stack — standby',
				options: [
					{ type: 'dropdown', id: 'mode', label: 'Move', default: 'NEXT',
						choices: [{ id: 'NEXT', label: 'Next cue' }, { id: 'PREV', label: 'Previous cue' }, { id: 'NUMBER', label: 'A cue number or name' }] },
					{ type: 'textinput', id: 'cue', label: 'Cue number or name (when chosen above)', default: '' },
				],
				callback: (a) => send(a.options.mode === 'NUMBER' ? `CUE STANDBY ${a.options.cue}` : `CUE STANDBY ${a.options.mode}`),
			},
			cue_hold: {
				name: 'Cue stack — HOLD',
				options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'TOGGLE',
					choices: [{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' }] }],
				callback: (a) => {
					const mode = a.options.mode === 'TOGGLE' ? (this.state.cuestack?.hold ? 'OFF' : 'ON') : a.options.mode
					send(`CUE HOLD ${mode}`)
				},
			},
			cue_arm: {
				name: 'Cue stack — ARM (needs "remotes may arm" in the Remote tab)',
				options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'ON',
					choices: [{ id: 'ON', label: 'Arm' }, { id: 'OFF', label: 'Disarm' }] }],
				callback: (a) => send(`CUE ARM ${a.options.mode}`),
			},
			stop_all: { name: 'STOP ALL (audio, break music, VOGs, stingers, tone — never outputs, blackout or the stream)', options: [], callback: () => send('STOPALL') },
			screen: {
				name: 'Screen on/off/toggle',
				options: [
					{ type: 'number', id: 'n', label: 'Screen number (overview order)', default: 1, min: 1, max: 32 },
					{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'TOGGLE',
						choices: [{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' }] },
				],
				callback: (a) => send(`SCREEN ${a.options.n} ${a.options.mode}`),
			},
			// The review latch: the sandboxed preview full-frame on every multiview until it is switched off.
			review: {
				name: 'Review — the preview on every multiview (toggle / on / off)',
				options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'TOGGLE', choices: [{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' }] }],
				callback: (a) => send(`REVIEW ${a.options.mode}`),
			},
			// FREEZE: every output holds its frame until released; the desk keeps moving.
			freeze: {
				name: 'Freeze — every output holds its frame (toggle / on / off)',
				options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'TOGGLE', choices: [{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'Freeze' }, { id: 'OFF', label: 'Release' }] }],
				callback: (a) => send(`FREEZE ${a.options.mode}`),
			},
			// A blackout with a fade of its own: down to black or up again, over the seconds given (0 = the show's transition time).
			fade: {
				name: 'Fade to black / fade up over a time',
				options: [
					{ type: 'dropdown', id: 'dir', label: 'Direction', default: 'DOWN', choices: [{ id: 'DOWN', label: 'To black' }, { id: 'UP', label: 'Up (lift the blackout)' }] },
					{ type: 'number', id: 'secs', label: 'Seconds (0 = the show\'s transition time)', default: 2, min: 0, max: 600, step: 0.5 },
				],
				callback: (a) => send(`${a.options.dir === 'UP' ? 'FADEUP' : 'FADE'}${a.options.secs > 0 ? ' ' + a.options.secs : ''}`),
			},
			// The look that was on air before the current one, back on air (press again to swap back).
			look_back: {
				name: 'Previous look — back on air',
				options: [],
				callback: () => send('LOOKBACK'),
			},
			screen_lock: {
				name: 'Screen lock / unlock (locked, it keeps its picture through looks, cues and TAKE)',
				options: [
					{ type: 'number', id: 'n', label: 'Screen number (overview order)', default: 1, min: 1, max: 32 },
					{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'TOGGLE',
						choices: [{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'Lock' }, { id: 'OFF', label: 'Unlock' }] },
				],
				callback: (a) => send(`LOCK ${a.options.n} ${a.options.mode}`),
			},
			group: {
				name: 'Canvas group on/off',
				options: [
					{ type: 'textinput', id: 'letter', label: 'Canvas letter (A, B…)', default: 'A' },
					{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'ON',
						choices: [{ id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' }] },
				],
				callback: (a) => send(`GROUP ${a.options.letter} ${a.options.mode}`),
			},
			audio: {
				name: 'Audio track',
				options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'PLAY',
					choices: [{ id: 'PLAY', label: 'Play' }, { id: 'STOP', label: 'Stop' }] }],
				callback: (a) => send(`AUDIO ${a.options.mode}`),
			},
			tone: {
				name: 'Soundcheck tone',
				options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'ON',
					choices: [{ id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' }] }],
				callback: (a) => send(`TONE ${a.options.mode}`),
			},
			// The live duck: music, stinger sounds and clip audio make way for an announcement
			// from the room (a VOG never ducks). A latch — STOP ALL leaves it.
			duck: {
				name: 'Live duck (make way for an announcement from the room)',
				options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'TOGGLE',
					choices: [{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' }] }],
				callback: (a) => send(`DUCK ${a.options.mode}`),
			},
			stinger: {
				name: 'Fire stinger (by number)',
				options: [{ type: 'number', id: 'n', label: 'Stinger number (Audio tab order)', default: 1, min: 1, max: 32 }],
				callback: (a) => send(`STINGER ${a.options.n}`),
			},
			stinger_name: {
				name: 'Fire stinger (by name)',
				options: [{ type: 'textinput', id: 'name', label: 'Stinger name', default: '' }],
				callback: (a) => send(`STINGER ${a.options.name}`),
			},
			stinger_stop: { name: 'Stop VOG / stinger (a held frame reverts; the ending is cancelled)', options: [], callback: () => send('STINGER STOP') },
			// Lower thirds: a design by number (Lower thirds page order) or name goes on air over the show; OFF takes it off.
			lower_third: {
				name: 'Lower third on (by number, Lower thirds page order)',
				options: [{ type: 'number', id: 'n', label: 'Design number', default: 1, min: 1, max: 64 }],
				callback: (a) => send(`LT ${a.options.n}`),
			},
			lower_third_name: {
				name: 'Lower third on (by name)',
				options: [{ type: 'textinput', id: 'name', label: 'Design name', default: '' }],
				callback: (a) => send(`LT ${a.options.name}`),
			},
			lower_third_off: { name: 'Lower third off (leaves the way it was designed to)', options: [], callback: () => send('LT OFF') },
			// The library: a person by number (Lower thirds page order) or name into a design — the one on air when
			// blank (else the first) — and on air. A name that is not in the library is refused: it never reaches the screen.
			lower_third_person: {
				name: 'Lower third — a person from the library (by number)',
				options: [
					{ type: 'number', id: 'n', label: 'Person number (Lower thirds page order)', default: 1, min: 1, max: 200 },
					{ type: 'textinput', id: 'design', label: 'Design (number or name; blank = the one on air, else the first)', default: '' },
				],
				callback: (a) => send(a.options.design ? `LT ${a.options.design} WITH ${a.options.n}` : `PERSON ${a.options.n}`),
			},
			lower_third_person_name: {
				name: 'Lower third — a person from the library (by name)',
				options: [
					{ type: 'textinput', id: 'name', label: 'Person name', default: '' },
					{ type: 'textinput', id: 'design', label: 'Design (number or name; blank = the one on air, else the first)', default: '' },
				],
				callback: (a) => send(a.options.design ? `LT ${a.options.design} WITH ${a.options.name}` : `PERSON ${a.options.name}`),
			},
			// The sign-off flow (needs EDIT SAFE on the desk): a design — with a person — into the preview, where the
			// PREVIEW pane, the multiview's Preview tile and REVIEW show it; TAKE puts it on air and clears the preview;
			// UPDATE pushes an edit made while a design is on air across in place.
			lower_third_preview: {
				name: 'Lower third to preview (by number; blank person = as designed)',
				options: [
					{ type: 'number', id: 'n', label: 'Design number (Lower thirds page order; 0 = the one in the preview, on air, or the default)', default: 1, min: 0, max: 64 },
					{ type: 'textinput', id: 'person', label: 'Person (number or name; blank = as designed)', default: '' },
				],
				callback: (a) => {
					const design = a.options.n > 0 ? String(a.options.n) : ''
					const person = a.options.person ? String(a.options.person).trim() : ''
					if (!design && !person) return
					send(design ? (person ? `LT PREVIEW ${design} WITH ${person}` : `LT PREVIEW ${design}`) : `LT PREVIEW WITH ${person}`)
				},
			},
			lower_third_preview_name: {
				name: 'Lower third to preview (by name)',
				options: [
					{ type: 'textinput', id: 'name', label: 'Design name', default: '' },
					{ type: 'textinput', id: 'person', label: 'Person (number or name; blank = as designed)', default: '' },
				],
				callback: (a) => send(a.options.person ? `LT PREVIEW ${a.options.name} WITH ${a.options.person}` : `LT PREVIEW ${a.options.name}`),
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
							{ id: 'next', label: 'Next (slide / video)' },
							{ id: 'prev', label: 'Previous' },
							{ id: 'first', label: 'First slide' },
							{ id: 'last', label: 'Last slide' },
							{ id: 'present', label: 'Present — start the deck' },
							{ id: 'exit', label: 'Exit (Escape)' },
							{ id: 'play', label: 'Play / pause' },
							{ id: 'pause', label: 'Pause' },
							{ id: 'mute', label: 'Mute / unmute' },
							{ id: 'restart', label: 'Restart the video' },
							{ id: 'forward', label: '+10 s' },
							{ id: 'rewind', label: '−10 s' },
							{ id: 'black', label: 'Black slide' },
							{ id: 'white', label: 'White slide' },
							{ id: 'captions', label: 'Captions' },
							{ id: 'fullscreen', label: 'Full screen' },
							{ id: 'reload', label: 'Reload the page' },
							{ id: 'key', label: 'A key of your own (below)' },
						],
					},
					{ type: 'textinput', id: 'key', label: 'Key, when the action is "a key of your own": ArrowRight, Space, k, Ctrl+Shift+F5', default: '' },
					{ type: 'textinput', id: 'page', label: 'Page (blank = the page on air; else its nickname or a word of its address)', default: '' },
				],
				callback: (a) => {
					const on = a.options.page ? ` ON ${String(a.options.page).trim()}` : ''
					if (a.options.action === 'reload') return send(`WEB RELOAD${on}`)
					const key = a.options.action === 'key' ? String(a.options.key || '').trim() : a.options.action
					if (key) send(`WEB KEY ${key}${on}`)
				},
			},
			web_click: {
				name: 'Web page — click at a spot (percent of the page)',
				options: [
					{ type: 'number', id: 'x', label: 'X (% from the left)', default: 50, min: 0, max: 100 },
					{ type: 'number', id: 'y', label: 'Y (% from the top)', default: 50, min: 0, max: 100 },
					{ type: 'textinput', id: 'page', label: 'Page (blank = the page on air)', default: '' },
				],
				callback: (a) => send(`WEB CLICK ${a.options.x} ${a.options.y}${a.options.page ? ` ON ${String(a.options.page).trim()}` : ''}`),
			},
			web_type: {
				name: 'Web page — type text into the field that has the page\'s focus',
				options: [{ type: 'textinput', id: 'text', label: 'Text', default: '' }],
				callback: (a) => { if (a.options.text) send(`WEB TYPE ${a.options.text}`) },
			},
			web_open: {
				name: 'Web page — send the page\'s browser to another address',
				options: [
					{ type: 'textinput', id: 'address', label: 'Address', default: '' },
					{ type: 'textinput', id: 'page', label: 'Page (blank = the page on air)', default: '' },
				],
				callback: (a) => { if (a.options.address) send(`WEB OPEN ${String(a.options.address).trim()}${a.options.page ? ` ON ${String(a.options.page).trim()}` : ''}`) },
			},
			// The deck (a PDF presentation) on air: its pages from a key. Presenter NEXT / BACK turn it too,
			// and past the last page the caller's stack resumes.
			deck_page: {
				name: 'Deck — turn the PDF on air',
				options: [
					{
						type: 'dropdown', id: 'mode', label: 'Turn', default: 'NEXT',
						choices: [
							{ id: 'NEXT', label: 'Next page' },
							{ id: 'PREV', label: 'Previous page' },
							{ id: 'FIRST', label: 'First page' },
							{ id: 'LAST', label: 'Last page' },
							{ id: 'PAGE', label: 'A page number (below)' },
						],
					},
					{ type: 'number', id: 'n', label: 'Page (when Turn is a page number)', default: 1, min: 1, max: 9999 },
				],
				callback: (a) => send(a.options.mode === 'PAGE' ? `DECK PAGE ${a.options.n}` : `DECK ${a.options.mode}`),
			},
			// VOG / STING name the same library by the same number and only assert the kind: a key
			// that says VOG never fires a stinger — Patterns refuses and names the item.
			vog: {
				name: 'Fire VOG (by number, Audio-page order)',
				options: [{ type: 'number', id: 'n', label: 'Library number (Audio page order)', default: 1, min: 1, max: 32 }],
				callback: (a) => send(`VOG ${a.options.n}`),
			},
			vog_name: {
				name: 'Fire VOG (by name)',
				options: [{ type: 'textinput', id: 'name', label: 'VOG name', default: '' }],
				callback: (a) => send(`VOG ${a.options.name}`),
			},
			sting: {
				name: 'Fire stinger (by number, Audio-page order)',
				options: [{ type: 'number', id: 'n', label: 'Library number (Audio page order)', default: 1, min: 1, max: 32 }],
				callback: (a) => send(`STING ${a.options.n}`),
			},
			sting_name: {
				name: 'Fire stinger (by name)',
				options: [{ type: 'textinput', id: 'name', label: 'Stinger name', default: '' }],
				callback: (a) => send(`STING ${a.options.name}`),
			},
			// Break music (Spotify): the MUSIC verbs. With the feature off in Patterns these answer OK and do nothing.
			music: {
				name: 'Break music',
				options: [{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'PLAY',
					choices: [{ id: 'PLAY', label: 'Play / resume' }, { id: 'PAUSE', label: 'Pause' }, { id: 'NEXT', label: 'Skip track' }] }],
				callback: (a) => send(`MUSIC ${a.options.mode}`),
			},
			music_item: {
				name: 'Break music — play entry (by number)',
				options: [{ type: 'number', id: 'n', label: 'Break-music number (Audio page order)', default: 1, min: 1, max: 32 }],
				callback: (a) => send(`MUSIC PLAY ${a.options.n}`),
			},
			music_name: {
				name: 'Break music — play entry (by name)',
				options: [{ type: 'textinput', id: 'name', label: 'Break-music name', default: '' }],
				callback: (a) => send(`MUSIC PLAY ${a.options.name}`),
			},
			music_level: {
				name: 'Break music level',
				options: [{ type: 'number', id: 'n', label: 'Level (0–100, the Spotify device\'s own volume)', default: 60, min: 0, max: 100 }],
				callback: (a) => send(`MUSIC VOL ${a.options.n}`),
			},
			section: {
				name: 'Playlist — show part on air',
				options: [{ type: 'number', id: 'n', label: 'Part number (Media tab order)', default: 1, min: 1, max: 32 }],
				callback: (a) => send(`SECTION ${a.options.n}`),
			},
		}
	}

	buildFeedbacks() {
		return {
			blackout: {
				type: 'boolean',
				name: 'Blackout is on',
				defaultStyle: { bgcolor: combineRgb(200, 0, 0), color: combineRgb(255, 255, 255) },
				options: [],
				callback: () => this.state.blackout === true,
			},
			screen_enabled: {
				type: 'boolean',
				name: 'Screen is enabled',
				defaultStyle: { bgcolor: combineRgb(0, 120, 60), color: combineRgb(255, 255, 255) },
				options: [{ type: 'number', id: 'n', label: 'Screen number', default: 1, min: 1, max: 32 }],
				callback: (fb) => this.state.screens?.some((s) => s.n === fb.options.n && s.enabled) === true,
			},
			screen_locked: {
				type: 'boolean',
				name: 'Screen is locked (keeps its picture through looks, cues and TAKE)',
				defaultStyle: { bgcolor: combineRgb(160, 110, 0), color: combineRgb(255, 255, 255) },
				options: [{ type: 'number', id: 'n', label: 'Screen number', default: 1, min: 1, max: 32 }],
				callback: (fb) => this.state.screens?.some((s) => s.n === fb.options.n && s.locked) === true,
			},
			screen_armed: {
				type: 'boolean',
				name: 'Screen is armed — the next CUT / TAKE changes it',
				defaultStyle: { bgcolor: combineRgb(30, 158, 90), color: combineRgb(255, 255, 255) },
				options: [{ type: 'number', id: 'n', label: 'Screen number', default: 1, min: 1, max: 32 }],
				callback: (fb) => this.state.screens?.some((s) => s.n === fb.options.n && s.armed) === true,
			},
			screen_own: {
				type: 'boolean',
				name: 'Screen shows a picture of its own, not the program\'s',
				defaultStyle: { bgcolor: combineRgb(0, 90, 130), color: combineRgb(255, 255, 255) },
				options: [{ type: 'number', id: 'n', label: 'Screen number', default: 1, min: 1, max: 32 }],
				callback: (fb) => this.state.screens?.some((s) => s.n === fb.options.n && s.own) === true,
			},
			// Looks: which one is on air (by name, by its place in the list, or by its F-key) and which is in the preview.
			look_on_air: {
				type: 'boolean',
				name: 'A look is on air (a named one, or any)',
				defaultStyle: { bgcolor: combineRgb(30, 158, 90), color: combineRgb(255, 255, 255) },
				options: [{ type: 'textinput', id: 'name', label: 'Look name (blank = any look)', default: '' }],
				callback: (fb) => {
					const on = this.state.airLook ?? ''
					return on !== '' && (!fb.options.name || on === fb.options.name)
				},
			},
			look_bank_on_air: {
				type: 'boolean',
				name: 'The look at a place in the show\'s list is on air (bank key n)',
				defaultStyle: { bgcolor: combineRgb(30, 158, 90), color: combineRgb(255, 255, 255) },
				options: [{ type: 'number', id: 'n', label: 'Place in the list (1–16)', default: 1, min: 1, max: 16 }],
				callback: (fb) => !!this.state.looks?.[fb.options.n - 1]?.air,
			},
			look_f_on_air: {
				type: 'boolean',
				name: 'The look on an F-key is on air',
				defaultStyle: { bgcolor: combineRgb(30, 158, 90), color: combineRgb(255, 255, 255) },
				options: [{ type: 'number', id: 'slot', label: 'F-key (1–12)', default: 1, min: 1, max: 12 }],
				callback: (fb) => !!this.state.looks?.find((l) => l.slot === fb.options.slot)?.air,
			},
			look_preview: {
				type: 'boolean',
				name: 'A look is loaded in the preview (a named one, or any)',
				defaultStyle: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) },
				options: [{ type: 'textinput', id: 'name', label: 'Look name (blank = any look)', default: '' }],
				callback: (fb) => {
					const on = this.state.previewLook ?? ''
					return on !== '' && (!fb.options.name || on === fb.options.name)
				},
			},
			// A bank key with nothing behind it dims, so a page of sixteen look keys shows only the looks the show has.
			slot_empty: {
				type: 'boolean',
				name: 'Bank key has nothing behind it (dim the key)',
				defaultStyle: { bgcolor: combineRgb(12, 13, 16), color: combineRgb(80, 84, 94) },
				options: [
					{ type: 'dropdown', id: 'kind', label: 'Bank', default: 'look',
						choices: [
							{ id: 'look', label: 'Looks by place' }, { id: 'look_f', label: 'Looks by F-key' }, { id: 'lt', label: 'Lower thirds' },
							{ id: 'person', label: 'People' }, { id: 'stinger', label: 'VOGs and stingers' }, { id: 'music', label: 'Break music' },
							{ id: 'section', label: 'Playlist parts' }, { id: 'screen', label: 'Screens' }, { id: 'cue', label: 'Upcoming cues' },
						] },
					{ type: 'number', id: 'n', label: 'Place', default: 1, min: 1, max: 32 },
				],
				callback: (fb) => this.bankName(fb.options.kind, fb.options.n) === '',
			},
			audio_playing: {
				type: 'boolean',
				name: 'Audio track is playing',
				defaultStyle: { bgcolor: combineRgb(0, 100, 160), color: combineRgb(255, 255, 255) },
				options: [],
				callback: () => this.state.audio?.playing === true,
			},
			stinger_playing: {
				type: 'boolean',
				name: 'A stinger is on air',
				defaultStyle: { bgcolor: combineRgb(190, 120, 0), color: combineRgb(255, 255, 255) },
				options: [],
				callback: () => (this.state.stingerPlaying ?? '') !== '',
			},
			vog_playing: {
				type: 'boolean',
				name: 'A VOG is on air',
				defaultStyle: { bgcolor: combineRgb(0, 100, 160), color: combineRgb(255, 255, 255) },
				options: [],
				callback: () => this.state.stingerKind === 'vog' || (this.state.vogSound ?? '') !== '',
			},
			sting_playing: {
				type: 'boolean',
				name: 'A stinger is on air',
				defaultStyle: { bgcolor: combineRgb(190, 120, 0), color: combineRgb(255, 255, 255) },
				options: [],
				callback: () => this.state.stingerKind === 'sting',
			},
			sting_hold: {
				type: 'boolean',
				name: 'A stinger is holding the screens',
				defaultStyle: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) },
				options: [],
				callback: () => (this.state.stingHold ?? '') !== '',
			},
			duck_on: {
				type: 'boolean',
				name: 'The live duck is on',
				defaultStyle: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) },
				options: [],
				callback: () => !!this.state.duck,
			},
			lower_third_on: {
				type: 'boolean',
				name: 'A lower third is on screen (any, or a named one)',
				defaultStyle: { bgcolor: combineRgb(224, 52, 46), color: combineRgb(255, 255, 255) },
				options: [{ type: 'textinput', id: 'name', label: 'Design name (blank = any)', default: '' }],
				callback: (fb) => {
					const on = this.state.lowerThird ?? ''
					return on !== '' && (!fb.options.name || on === fb.options.name)
				},
			},
			review_on: {
				type: 'boolean',
				name: 'Review is on (the preview fills every multiview)',
				defaultStyle: { bgcolor: combineRgb(46, 230, 138), color: combineRgb(14, 15, 19) },
				options: [],
				callback: () => !!this.state.review,
			},
			frozen: {
				type: 'boolean',
				name: 'Frozen (every output holds its frame)',
				defaultStyle: { bgcolor: combineRgb(53, 224, 208), color: combineRgb(14, 15, 19) },
				options: [],
				callback: () => !!this.state.frozen,
			},
			lower_third_person_is: {
				type: 'boolean',
				name: 'A given person is on screen (the name the lower third on air carries)',
				defaultStyle: { bgcolor: combineRgb(224, 52, 46), color: combineRgb(255, 255, 255) },
				options: [{ type: 'textinput', id: 'name', label: 'Person name', default: '' }],
				callback: (fb) => {
					const on = this.state.lowerThirdPerson ?? ''
					return on !== '' && on === fb.options.name
				},
			},
			lower_third_preview: {
				type: 'boolean',
				name: 'A lower third is in the preview for a sign-off (any, or a named one)',
				defaultStyle: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) },
				options: [{ type: 'textinput', id: 'name', label: 'Design name (blank = any)', default: '' }],
				callback: (fb) => {
					const on = this.state.lowerThirdPreview ?? ''
					return on !== '' && (!fb.options.name || on === fb.options.name)
				},
			},
			lower_third_edited: {
				type: 'boolean',
				name: 'The lower third on air was edited after it went there (UPDATE pushes the edit)',
				defaultStyle: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) },
				options: [],
				callback: () => !!this.state.lowerThirdEdited,
			},
			web_on_air: {
				type: 'boolean',
				name: 'A web page is on air (any, or one whose address carries a word)',
				defaultStyle: { bgcolor: combineRgb(0, 90, 130), color: combineRgb(255, 255, 255) },
				options: [{ type: 'textinput', id: 'word', label: 'A word of the address (blank = any page)', default: '' }],
				callback: (fb) => {
					const w = this.state.web
					if (!w || !w.page) return false
					const word = String(fb.options.word || '').trim().toLowerCase()
					return !word || String(w.url || '').toLowerCase().includes(word) || String(w.page || '').toLowerCase().includes(word)
				},
			},
			deck_on_air: {
				type: 'boolean',
				name: 'A deck (PDF) is on air — or on its last page',
				defaultStyle: { bgcolor: combineRgb(0, 90, 130), color: combineRgb(255, 255, 255) },
				options: [{ type: 'checkbox', id: 'ended', label: 'Only on its last page (the next click GOes the standby cue)', default: false }],
				callback: (fb) => {
					const d = this.state.deck
					if (!d || !d.count) return false
					return fb.options.ended ? !!d.ended : true
				},
			},
			music_playing: {
				type: 'boolean',
				name: 'Break music is playing',
				defaultStyle: { bgcolor: combineRgb(20, 120, 90), color: combineRgb(255, 255, 255) },
				options: [],
				callback: () => this.state.music?.playing === true,
			},
			cue_armed: {
				type: 'boolean',
				name: 'Cue stack is armed',
				defaultStyle: { bgcolor: combineRgb(30, 158, 90), color: combineRgb(255, 255, 255) },
				options: [],
				callback: () => this.state.cuestack?.armed === true,
			},
			cue_hold: {
				type: 'boolean',
				name: 'Cue stack is on HOLD',
				defaultStyle: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) },
				options: [],
				callback: () => this.state.cuestack?.hold === true,
			},
			cue_standby_is: {
				type: 'boolean',
				name: 'A given cue is on standby',
				defaultStyle: { bgcolor: combineRgb(46, 230, 138), color: combineRgb(14, 15, 19) },
				options: [{ type: 'textinput', id: 'cue', label: 'Cue number', default: '01.010' }],
				callback: (fb) => (this.state.cuestack?.standby?.number ?? '') === fb.options.cue,
			},
			cue_confirm_required: {
				type: 'boolean',
				name: 'GO is waiting for confirmation',
				defaultStyle: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) },
				options: [],
				callback: () => !!this.state.cuestack?.confirm,
			},
			cue_last_failed: {
				type: 'boolean',
				name: 'The last cue failed or was refused',
				defaultStyle: { bgcolor: combineRgb(224, 52, 46), color: combineRgb(255, 255, 255) },
				options: [],
				callback: () => /Failed|Refused/.test(this.state.cuestack?.last?.outcome ?? ''),
			},
		}
	}

	buildVariables() {
		const banks = [
			{ variableId: 'air_look', name: 'The look on air, by name (or empty)' },
			{ variableId: 'preview_look', name: 'The look loaded in the preview, by name (or empty)' },
			{ variableId: 'pattern', name: 'What kind of picture is on air (Media, LedWall, ProjectionBlend…)' },
		]
		for (let n = 1; n <= 16; n++) banks.push({ variableId: `look_${n}`, name: `Look at place ${n} in the show's list (name, or empty)` })
		for (let f = 1; f <= 12; f++) banks.push({ variableId: `look_f${f}`, name: `Look on F${f} (name, or empty)` })
		for (let n = 1; n <= 8; n++) {
			banks.push({ variableId: `lt_${n}`, name: `Lower third design ${n} (name, or empty)` })
			banks.push({ variableId: `person_${n}`, name: `Person ${n} in the library (name, or empty)` })
			banks.push({ variableId: `stinger_${n}`, name: `VOG / stinger ${n} (name, or empty)` })
			banks.push({ variableId: `screen_${n}`, name: `Screen ${n} (its label, or empty)` })
		}
		for (let n = 1; n <= 6; n++) {
			banks.push({ variableId: `music_${n}`, name: `Break music entry ${n} (name, or empty)` })
			banks.push({ variableId: `section_${n}`, name: `Playlist part ${n} (name, or empty)` })
		}
		for (let k = 1; k <= 7; k++) {
			banks.push({ variableId: `cue_${k}`, name: `Cue bank ${k} — number and name (1 = the standby cue, 2… the cues after it)` })
			banks.push({ variableId: `cue_${k}_number`, name: `Cue bank ${k} — number` })
			banks.push({ variableId: `cue_${k}_name`, name: `Cue bank ${k} — name` })
		}
		return [
			...banks,
			{ variableId: 'blackout', name: 'Blackout state' },
			{ variableId: 'presenter_step', name: 'Presenter step number' },
			{ variableId: 'presenter_count', name: 'Presenter step count' },
			{ variableId: 'playlist', name: 'Playlist status' },
			{ variableId: 'next_cue', name: 'Next scheduled cue' },
			{ variableId: 'stinger', name: 'VOG or stinger on air (name)' },
			{ variableId: 'sting_hold', name: 'Stinger holding the screens (name)' },
			{ variableId: 'duck', name: 'Live duck (DUCK/off)' },
			{ variableId: 'lower_third', name: 'Lower third on screen (name, or empty)' },
			{ variableId: 'lower_third_person', name: 'The name the lower third on screen carries (or empty)' },
			{ variableId: 'lower_third_preview', name: 'Lower third in the preview for a sign-off (name, or empty)' },
			{ variableId: 'lower_third_preview_person', name: 'The name the lower third in the preview carries (or empty)' },
			{ variableId: 'lower_third_default', name: 'The show\'s default lower third design (★)' },
			{ variableId: 'lower_third_edited', name: 'EDITED when the design on air differs from the edited one, else off' },
			{ variableId: 'review', name: 'Review on the multiview (ON/off)' },
			{ variableId: 'freeze', name: 'Freeze (FROZEN/off)' },
			{ variableId: 'previous_look', name: 'The look LOOK BACK returns to (name, or empty)' },
			{ variableId: 'deck_page', name: 'Deck on air — the page on show (or empty)' },
			{ variableId: 'deck_count', name: 'Deck on air — its page count (or empty)' },
			{ variableId: 'deck_file', name: 'Deck on air — its file name (or empty)' },
			{ variableId: 'web_page', name: 'Web page on air (its nickname or host, or empty)' },
			{ variableId: 'web_title', name: 'Web page on air — its title' },
			{ variableId: 'web_service', name: 'Web page on air — its service (YouTube, Google Slides…), or empty' },
			{ variableId: 'music', name: 'Break music — now playing' },
			{ variableId: 'music_state', name: 'Break music state (PLAYING/paused)' },
			{ variableId: 'music_level', name: 'Break music level (0–100)' },
			{ variableId: 'music_device', name: 'Break music — Spotify device' },
			{ variableId: 'health', name: 'App health line' },
			{ variableId: 'machine_cpu', name: 'Computer CPU load' },
			{ variableId: 'machine_fps', name: 'Output frame rate' },
			{ variableId: 'machine_power', name: 'Power source (mains/BATTERY)' },
			{ variableId: 'machine_advice', name: 'Health suggestions needing attention' },
			{ variableId: 'program', name: 'What is on air, by name' },
			{ variableId: 'cue_armed', name: 'Cue stack armed (ARMED/off)' },
			{ variableId: 'cue_hold', name: 'Cue stack hold (HOLD/off)' },
			{ variableId: 'cue_seq', name: 'Cue stack runtime sequence' },
			{ variableId: 'cue_standby_number', name: 'Standby cue number' },
			{ variableId: 'cue_standby_name', name: 'Standby cue name' },
			{ variableId: 'cue_next_number', name: 'Next cue number' },
			{ variableId: 'cue_next_name', name: 'Next cue name' },
			{ variableId: 'cue_previous_number', name: 'Previous (last run) cue number' },
			{ variableId: 'cue_previous_name', name: 'Previous (last run) cue name' },
			{ variableId: 'cue_last_outcome', name: 'Last GO outcome' },
			{ variableId: 'cue_confirm', name: 'Pending confirm (CONFIRM 03.020) or empty' },
			{ variableId: 'last_error', name: 'Last ERR line from Patterns' },
		]
	}

	/** The dim style for a bank key with nothing behind it. */
	static empty(kind, n) {
		return { feedbackId: 'slot_empty', options: { kind, n }, style: { bgcolor: combineRgb(12, 13, 16), color: combineRgb(80, 84, 94) } }
	}

	/** Presets built from the show's own lists — one key per item, named for it — under "… — this show" categories. */
	buildShowPresets() {
		const presets = {}
		const white = combineRgb(255, 255, 255)
		const dark = combineRgb(20, 22, 28)
		const green = combineRgb(30, 158, 90)
		const amber = { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) }
		const s = this.state
		const key = (text) => String(text ?? '').replace(/[^A-Za-z0-9]+/g, '_').slice(0, 40)
		;(s.looks ?? []).forEach((l, i) => {
			presets[`show_look_${i + 1}_${key(l.name)}`] = {
				type: 'button', category: 'Looks — this show', name: `Look: ${l.name}${l.slot ? ` (F${l.slot})` : ''}`,
				style: { text: l.name, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'look_name', options: { name: l.name } }], up: [] }],
				feedbacks: [
					{ feedbackId: 'look_on_air', options: { name: l.name }, style: { bgcolor: green } },
					{ feedbackId: 'look_preview', options: { name: l.name }, style: amber },
				],
			}
		})
		;(s.lowerThirds ?? []).forEach((d) => {
			presets[`show_lt_${d.n}_${key(d.name)}`] = {
				type: 'button', category: 'Lower thirds — this show', name: `Lower third: ${d.name}`,
				style: { text: `LT\\n${d.name}`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'lower_third', options: { n: d.n } }], up: [] }],
				feedbacks: [{ feedbackId: 'lower_third_on', options: { name: d.name }, style: { bgcolor: combineRgb(224, 52, 46) } }],
			}
		})
		;(s.people ?? []).forEach((p) => {
			presets[`show_person_${p.n}_${key(p.name)}`] = {
				type: 'button', category: 'People — this show', name: `Person: ${p.name}${p.role ? ` — ${p.role}` : ''}`,
				style: { text: p.name, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'lower_third_person', options: { n: p.n, design: '' } }], up: [] }],
				feedbacks: [{ feedbackId: 'lower_third_person_is', options: { name: p.name }, style: { bgcolor: combineRgb(224, 52, 46) } }],
			}
		})
		;(s.stingers ?? []).forEach((it) => {
			const vog = it.kind === 'vog'
			presets[`show_stinger_${it.n}_${key(it.name)}`] = {
				type: 'button', category: vog ? 'VOGs — this show' : 'Stingers — this show', name: `${vog ? 'VOG' : 'Stinger'}: ${it.name}`,
				style: { text: `${vog ? 'VOG' : 'STING'}\\n${it.name}`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: vog ? 'vog' : 'sting', options: { n: it.n } }], up: [] }],
				feedbacks: vog
					? [{ feedbackId: 'vog_playing', options: {}, style: { bgcolor: combineRgb(0, 100, 160) } }]
					: [{ feedbackId: 'sting_playing', options: {}, style: { bgcolor: combineRgb(190, 120, 0) } }, { feedbackId: 'sting_hold', options: {}, style: amber }],
			}
		})
		;(s.music?.items ?? []).forEach((m) => {
			presets[`show_music_${m.n}_${key(m.name)}`] = {
				type: 'button', category: 'Break music — this show', name: `Break music: ${m.name}`,
				style: { text: `♫\\n${m.name}`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'music_item', options: { n: m.n } }], up: [] }],
				feedbacks: [{ feedbackId: 'music_playing', options: {}, style: { bgcolor: combineRgb(20, 120, 90) } }],
			}
		})
		;(s.sections ?? []).forEach((p) => {
			presets[`show_section_${p.n}_${key(p.name)}`] = {
				type: 'button', category: 'Playlist parts — this show', name: `Part: ${p.name}`,
				style: { text: `PART\\n${p.name}`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'section', options: { n: p.n } }], up: [] }], feedbacks: [],
			}
		})
		;(s.screens ?? []).forEach((sc) => {
			presets[`show_screen_${sc.n}_${key(sc.label)}`] = {
				type: 'button', category: 'Screens — this show', name: `Screen ${sc.n}: ${sc.label}`,
				style: { text: sc.label, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'screen', options: { n: sc.n, mode: 'TOGGLE' } }], up: [] }],
				feedbacks: [
					{ feedbackId: 'screen_enabled', options: { n: sc.n }, style: { bgcolor: combineRgb(0, 120, 60) } },
					{ feedbackId: 'screen_locked', options: { n: sc.n }, style: { bgcolor: combineRgb(160, 110, 0) } },
				],
			}
		})
		this.upcoming().forEach((c, i) => {
			presets[`show_cue_${i + 1}_${key(c.number)}`] = {
				type: 'button', category: 'Upcoming cues — this show', name: `${i === 0 ? 'Standby' : 'Cue'}: ${c.number} ${c.name}`,
				style: { text: `${c.number}\\n${c.name}`, size: 'auto', color: white, bgcolor: i === 0 ? green : dark },
				steps: [{ down: [{ actionId: 'cue_bank', options: { k: i + 1, mode: 'STANDBY' } }], up: [] }],
				feedbacks: [{ feedbackId: 'cue_standby_is', options: { cue: c.number }, style: { bgcolor: combineRgb(46, 230, 138), color: combineRgb(14, 15, 19) } }],
			}
		})
		return presets
	}

	buildPresets() {
		const presets = {}
		const white = combineRgb(255, 255, 255)
		const dark = combineRgb(20, 22, 28)
		const green = combineRgb(30, 158, 90)
		const empty = PatternsInstance.empty

		// Banks: keys that label themselves from the show — drag a row once and every look, design,
		// person, stinger, track, part, screen or upcoming cue made later appears on the next key.
		for (let n = 1; n <= 16; n++) {
			presets[`look_bank_${n}`] = {
				type: 'button', category: 'Look bank (labels itself)', name: `Look bank ${n} — the look at place ${n} in the show's list`,
				style: { text: `$(patterns:look_${n})`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'look_bank', options: { n } }], up: [] }],
				feedbacks: [
					{ feedbackId: 'look_bank_on_air', options: { n }, style: { bgcolor: green } },
					empty('look', n),
				],
			}
		}
		for (let k = 1; k <= 7; k++) {
			presets[`cue_bank_${k}`] = {
				type: 'button', category: 'Cue bank (labels itself)', name: k === 1 ? 'Cue bank 1 — the standby cue' : `Cue bank ${k} — the cue ${k - 1} after the standby: press to put it on standby`,
				style: { text: `$(patterns:cue_${k})`, size: 'auto', color: white, bgcolor: k === 1 ? green : dark },
				steps: [{ down: [{ actionId: 'cue_bank', options: { k, mode: 'STANDBY' } }], up: [] }],
				feedbacks: [empty('cue', k)],
			}
		}
		for (let n = 1; n <= 6; n++) {
			presets[`section_${n}`] = {
				type: 'button', category: 'Playlist parts', name: `Playlist part ${n} on air`,
				style: { text: `PART ${n}\\n$(patterns:section_${n})`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'section', options: { n } }], up: [] }], feedbacks: [empty('section', n)],
			}
		}

		presets.go = {
			type: 'button', category: 'Transport', name: 'Outputs on',
			style: { text: 'OUTPUTS\\nON', size: '14', color: white, bgcolor: combineRgb(0, 100, 50) },
			steps: [{ down: [{ actionId: 'go', options: {} }], up: [] }], feedbacks: [],
		}
		presets.stop = {
			type: 'button', category: 'Transport', name: 'Outputs off',
			style: { text: 'OUTPUTS\\nOFF', size: '14', color: white, bgcolor: combineRgb(90, 30, 30) },
			steps: [{ down: [{ actionId: 'stop', options: {} }], up: [] }], feedbacks: [],
		}
		presets.blackout = {
			type: 'button', category: 'Transport', name: 'Blackout toggle',
			style: { text: 'BLACK\\nOUT', size: '18', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'blackout', options: { mode: 'TOGGLE' } }], up: [] }],
			feedbacks: [{ feedbackId: 'blackout', options: {}, style: { bgcolor: combineRgb(200, 0, 0) } }],
		}
		presets.cue_go = {
			type: 'button', category: 'Cue stack', name: 'GO',
			style: { text: 'GO\n$(patterns:cue_standby_number)\n$(patterns:cue_standby_name)', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'cue_go', options: {} }], up: [] }],
			feedbacks: [
				{ feedbackId: 'cue_armed', options: {}, style: { bgcolor: combineRgb(30, 158, 90) } },
				{ feedbackId: 'cue_hold', options: {}, style: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) } },
				{ feedbackId: 'cue_confirm_required', options: {}, style: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19), text: '$(patterns:cue_confirm)' } },
				{ feedbackId: 'cue_last_failed', options: {}, style: { bgcolor: combineRgb(224, 52, 46) } },
			],
		}
		presets.cue_standby_next = {
			type: 'button', category: 'Cue stack', name: 'Standby next',
			style: { text: 'STANDBY\n▼', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'cue_standby', options: { mode: 'NEXT', cue: '' } }], up: [] }], feedbacks: [],
		}
		presets.cue_standby_prev = {
			type: 'button', category: 'Cue stack', name: 'Standby previous',
			style: { text: 'STANDBY\n▲', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'cue_standby', options: { mode: 'PREV', cue: '' } }], up: [] }], feedbacks: [],
		}
		presets.cue_hold = {
			type: 'button', category: 'Cue stack', name: 'HOLD',
			style: { text: 'HOLD', size: '18', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'cue_hold', options: { mode: 'TOGGLE' } }], up: [] }],
			feedbacks: [{ feedbackId: 'cue_hold', options: {}, style: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) } }],
		}
		presets.cue_arm = {
			type: 'button', category: 'Cue stack', name: 'ARM',
			style: { text: 'ARM\n$(patterns:cue_armed)', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'cue_arm', options: { mode: 'ON' } }], up: [] }],
			feedbacks: [{ feedbackId: 'cue_armed', options: {}, style: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) } }],
		}
		presets.stop_all = {
			type: 'button', category: 'Cue stack', name: 'STOP ALL',
			style: { text: 'STOP\nALL', size: '14', color: combineRgb(224, 52, 46), bgcolor: dark },
			steps: [{ down: [{ actionId: 'stop_all', options: {} }], up: [] }], feedbacks: [],
		}
		presets.duck = {
			type: 'button', category: 'Cue stack', name: 'DUCK (live announcement)',
			style: { text: 'DUCK\n$(patterns:duck)', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'duck', options: { mode: 'TOGGLE' } }], up: [] }],
			feedbacks: [{ feedbackId: 'duck_on', options: {}, style: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) } }],
		}
		const lowerOn = { feedbackId: 'lower_third_on', options: { name: '' }, style: { bgcolor: combineRgb(224, 52, 46) } }
		presets.review = {
			type: 'button', category: 'Transport', name: 'REVIEW — the preview on every multiview',
			style: { text: 'REVIEW\\n$(patterns:review)', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'review', options: { mode: 'TOGGLE' } }], up: [] }],
			feedbacks: [{ feedbackId: 'review_on', options: {}, style: { bgcolor: combineRgb(46, 230, 138), color: combineRgb(14, 15, 19) } }],
		}
		presets.freeze = {
			type: 'button', category: 'Transport', name: 'FREEZE — every output holds its frame',
			style: { text: 'FREEZE\\n$(patterns:freeze)', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'freeze', options: { mode: 'TOGGLE' } }], up: [] }],
			feedbacks: [{ feedbackId: 'frozen', options: {}, style: { bgcolor: combineRgb(53, 224, 208), color: combineRgb(14, 15, 19) } }],
		}
		presets.fade_down = {
			type: 'button', category: 'Transport', name: 'FADE TO BLACK — 2 s',
			style: { text: 'FADE\\nTO BLACK\\n2 s', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'fade', options: { dir: 'DOWN', secs: 2 } }], up: [] }],
			feedbacks: [{ feedbackId: 'blackout', options: {}, style: { bgcolor: combineRgb(224, 52, 46), color: white } }],
		}
		presets.fade_up = {
			type: 'button', category: 'Transport', name: 'FADE UP — 2 s',
			style: { text: 'FADE\\nUP\\n2 s', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'fade', options: { dir: 'UP', secs: 2 } }], up: [] }],
			feedbacks: [],
		}
		presets.look_back = {
			type: 'button', category: 'Looks', name: 'PREVIOUS LOOK — back on air',
			style: { text: 'BACK TO\\n$(patterns:previous_look)', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'look_back', options: {} }], up: [] }],
			feedbacks: [],
		}
		presets.lower_third_off = {
			type: 'button', category: 'Lower thirds', name: 'Lower third off',
			style: { text: 'LT\\nOFF\\n$(patterns:lower_third)', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'lower_third_off', options: {} }], up: [] }], feedbacks: [lowerOn],
		}
		for (let n = 1; n <= 6; n++) {
			presets[`lower_third_${n}`] = {
				type: 'button', category: 'Lower thirds', name: `Lower third ${n} (labels itself)`,
				style: { text: `LT ${n}\\n$(patterns:lt_${n})`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'lower_third', options: { n } }], up: [] }], feedbacks: [lowerOn, empty('lt', n)],
			}
		}
		for (let n = 1; n <= 6; n++) {
			presets[`person_${n}`] = {
				type: 'button', category: 'Lower thirds', name: `Person ${n} (library) into the lower third on air (labels itself)`,
				style: { text: `$(patterns:person_${n})`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'lower_third_person', options: { n, design: '' } }], up: [] }], feedbacks: [lowerOn, empty('person', n)],
			}
		}
		const lowerPvw = { feedbackId: 'lower_third_preview', options: { name: '' }, style: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) } }
		presets.lower_third_take = {
			type: 'button', category: 'Lower thirds', name: 'Lower third TAKE — the one in the preview to air',
			style: { text: 'LT TAKE\\n$(patterns:lower_third_preview)', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'lower_third_take', options: {} }], up: [] }], feedbacks: [lowerPvw],
		}
		presets.lower_third_update = {
			type: 'button', category: 'Lower thirds', name: 'Lower third UPDATE — push an edit to the design on air',
			style: { text: 'LT\\nUPDATE', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'lower_third_update', options: {} }], up: [] }],
			feedbacks: [{ feedbackId: 'lower_third_edited', options: {}, style: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) } }],
		}
		presets.lower_third_preview_off = {
			type: 'button', category: 'Lower thirds', name: 'Lower third preview clear',
			style: { text: 'LT PVW\\nCLEAR', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'lower_third_preview_off', options: {} }], up: [] }], feedbacks: [lowerPvw],
		}
		for (let n = 1; n <= 6; n++) {
			presets[`lower_third_preview_${n}`] = {
				type: 'button', category: 'Lower thirds', name: `Lower third ${n} to preview (sign-off)`,
				style: { text: `LT PVW\\n${n}`, size: '14', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'lower_third_preview', options: { n, person: '' } }], up: [] }], feedbacks: [lowerPvw],
			}
			presets[`person_preview_${n}`] = {
				type: 'button', category: 'Lower thirds', name: `Person ${n} (library) to preview, into the design in the preview, on air, or the default`,
				style: { text: `PVW\\nPERSON ${n}`, size: '14', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'lower_third_preview', options: { n: 0, person: String(n) } }], up: [] }], feedbacks: [lowerPvw],
			}
		}
		const deckOn = { feedbackId: 'deck_on_air', options: { ended: false }, style: { bgcolor: combineRgb(0, 90, 130) } }
		const deckEnded = { feedbackId: 'deck_on_air', options: { ended: true }, style: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) } }
		for (const [id, text, mode] of [['next', 'DECK\\n▶ $(patterns:deck_page)/$(patterns:deck_count)', 'NEXT'], ['prev', 'DECK\\n◀', 'PREV'], ['first', 'DECK\\nFIRST', 'FIRST'], ['last', 'DECK\\nLAST', 'LAST']]) {
			presets[`deck_${id}`] = {
				type: 'button', category: 'Presenter', name: `Deck — ${mode.toLowerCase()} page`,
				style: { text, size: '14', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'deck_page', options: { mode, n: 1 } }], up: [] }],
				feedbacks: id === 'next' ? [deckOn, deckEnded] : [deckOn],
			}
		}
		const webOn = { feedbackId: 'web_on_air', options: { word: '' }, style: { bgcolor: combineRgb(0, 90, 130) } }
		for (const [id, text, action] of [
			['next', 'PAGE\\nNEXT ▶', 'next'], ['prev', 'PAGE\\n◀ PREV', 'prev'], ['first', 'PAGE\\nFIRST', 'first'], ['last', 'PAGE\\nLAST', 'last'],
			['present', 'PAGE\\nPRESENT', 'present'], ['exit', 'PAGE\\nEXIT', 'exit'], ['play', 'PAGE\\nPLAY ❚❚', 'play'], ['mute', 'PAGE\\nMUTE', 'mute'],
			['black', 'PAGE\\nBLACK', 'black'], ['reload', 'PAGE\\nRELOAD', 'reload'],
		]) {
			presets[`web_${id}`] = {
				type: 'button', category: 'Web page', name: `Web page — ${action} (the page on air: $(patterns:web_page))`,
				style: { text, size: '14', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'web_action', options: { action, key: '', page: '' } }], up: [] }], feedbacks: [webOn],
			}
		}
		const musicOn = { feedbackId: 'music_playing', options: {}, style: { bgcolor: combineRgb(20, 120, 90) } }
		presets.music_play = {
			type: 'button', category: 'Break music', name: 'Break music — play / resume',
			style: { text: 'BREAK\\n▶', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'music', options: { mode: 'PLAY' } }], up: [] }], feedbacks: [musicOn],
		}
		presets.music_pause = {
			type: 'button', category: 'Break music', name: 'Break music — pause',
			style: { text: 'BREAK\\n❚❚', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'music', options: { mode: 'PAUSE' } }], up: [] }], feedbacks: [musicOn],
		}
		presets.music_skip = {
			type: 'button', category: 'Break music', name: 'Break music — skip track',
			style: { text: 'BREAK\\n⏭', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'music', options: { mode: 'NEXT' } }], up: [] }], feedbacks: [musicOn],
		}
		for (let n = 1; n <= 6; n++) {
			presets[`music_${n}`] = {
				type: 'button', category: 'Break music', name: `Break music ${n} (labels itself)`,
				style: { text: `♫ ${n}\\n$(patterns:music_${n})`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'music_item', options: { n } }], up: [] }], feedbacks: [musicOn, empty('music', n)],
			}
		}
		presets.next = {
			type: 'button', category: 'Presenter', name: 'Next step',
			style: { text: 'NEXT\\n$(patterns:presenter_step)/$(patterns:presenter_count)', size: '14', color: white, bgcolor: combineRgb(0, 90, 130) },
			steps: [{ down: [{ actionId: 'presenter_next', options: {} }], up: [] }], feedbacks: [],
		}
		presets.prev = {
			type: 'button', category: 'Presenter', name: 'Previous step',
			style: { text: 'BACK', size: '18', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'presenter_prev', options: {} }], up: [] }], feedbacks: [],
		}
		for (let slot = 1; slot <= 12; slot++) {
			presets[`look_${slot}`] = {
				type: 'button', category: 'Looks', name: `Look F${slot} (labels itself)`,
				style: { text: `F${slot}\\n$(patterns:look_f${slot})`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'look_slot', options: { slot } }], up: [] }],
				feedbacks: [{ feedbackId: 'look_f_on_air', options: { slot }, style: { bgcolor: green } }, empty('look_f', slot)],
			}
		}
		for (let n = 1; n <= 8; n++) {
			presets[`screen_${n}`] = {
				type: 'button', category: 'Screens', name: `Screen ${n} toggle (labels itself)`,
				style: { text: `$(patterns:screen_${n})`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'screen', options: { n, mode: 'TOGGLE' } }], up: [] }],
				feedbacks: [
					{ feedbackId: 'screen_enabled', options: { n }, style: { bgcolor: combineRgb(0, 120, 60) } },
					{ feedbackId: 'screen_locked', options: { n }, style: { bgcolor: combineRgb(160, 110, 0) } },
					empty('screen', n),
				],
			}
			presets[`screen_${n}_lock`] = {
				type: 'button', category: 'Screens', name: `Screen ${n} lock / unlock`,
				style: { text: `LOCK\\n$(patterns:screen_${n})`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'screen_lock', options: { n, mode: 'TOGGLE' } }], up: [] }],
				feedbacks: [{ feedbackId: 'screen_locked', options: { n }, style: { bgcolor: combineRgb(160, 110, 0) } }, empty('screen', n)],
			}
		}
		for (const letter of ['A', 'B', 'C', 'D']) {
			presets[`group_${letter}_on`] = {
				type: 'button', category: 'Screens', name: `Canvas ${letter} on`,
				style: { text: `${letter}\\nON`, size: '14', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'group', options: { letter, mode: 'ON' } }], up: [] }], feedbacks: [],
			}
			presets[`group_${letter}_off`] = {
				type: 'button', category: 'Screens', name: `Canvas ${letter} off`,
				style: { text: `${letter}\\nOFF`, size: '14', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'group', options: { letter, mode: 'OFF' } }], up: [] }], feedbacks: [],
			}
		}
		for (let n = 1; n <= 8; n++) {
			presets[`stinger_${n}`] = {
				type: 'button', category: 'Stingers', name: `Stinger ${n} (labels itself)`,
				style: { text: `$(patterns:stinger_${n})`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'stinger', options: { n } }], up: [] }],
				feedbacks: [{ feedbackId: 'stinger_playing', options: {}, style: { bgcolor: combineRgb(190, 120, 0) } }, empty('stinger', n)],
			}
		}
		presets.stinger_stop = {
			type: 'button', category: 'Stingers', name: 'Stop stinger',
			style: { text: 'STING\\nSTOP', size: '14', color: white, bgcolor: combineRgb(90, 30, 30) },
			steps: [{ down: [{ actionId: 'stinger_stop', options: {} }], up: [] }], feedbacks: [],
		}
		for (let n = 1; n <= 8; n++) {
			presets[`vog_${n}`] = {
				type: 'button', category: 'VOG', name: `VOG ${n} (labels itself)`,
				style: { text: `VOG\\n$(patterns:stinger_${n})`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'vog', options: { n } }], up: [] }],
				feedbacks: [{ feedbackId: 'vog_playing', options: {}, style: { bgcolor: combineRgb(0, 100, 160) } }, empty('stinger', n)],
			}
			presets[`sting_${n}`] = {
				type: 'button', category: 'Stingers', name: `Stinger ${n} (kind-checked, labels itself)`,
				style: { text: `STING\\n$(patterns:stinger_${n})`, size: 'auto', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'sting', options: { n } }], up: [] }],
				feedbacks: [
					{ feedbackId: 'sting_playing', options: {}, style: { bgcolor: combineRgb(190, 120, 0) } },
					{ feedbackId: 'sting_hold', options: {}, style: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) } },
				],
			}
		}
		presets.sting_hold_release = {
			type: 'button', category: 'Stingers', name: 'Held stinger — put it back',
			style: { text: 'HOLD\\nBACK', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'stinger_stop', options: {} }], up: [] }],
			feedbacks: [{ feedbackId: 'sting_hold', options: {}, style: { bgcolor: combineRgb(255, 194, 77), color: combineRgb(14, 15, 19) } }],
		}
		presets.audio_play = {
			type: 'button', category: 'Audio', name: 'Audio play',
			style: { text: '♪ PLAY', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'audio', options: { mode: 'PLAY' } }], up: [] }],
			feedbacks: [{ feedbackId: 'audio_playing', options: {}, style: { bgcolor: combineRgb(0, 100, 160) } }],
		}
		presets.audio_stop = {
			type: 'button', category: 'Audio', name: 'Audio stop',
			style: { text: '♪ STOP', size: '14', color: white, bgcolor: dark },
			steps: [{ down: [{ actionId: 'audio', options: { mode: 'STOP' } }], up: [] }], feedbacks: [],
		}
		return presets
	}
}

runEntrypoint(PatternsInstance, [])
