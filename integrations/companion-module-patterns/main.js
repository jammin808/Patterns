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
		this.socket.on('status_change', (status, message) => this.updateStatus(status, message))
		this.socket.on('error', (err) => this.log('error', `Connection error: ${err.message}`))
		this.socket.on('data', (data) => {
			this.buffer += data.toString('utf8')
			let idx
			while ((idx = this.buffer.indexOf('\n')) >= 0) {
				const line = this.buffer.slice(0, idx).trim()
				this.buffer = this.buffer.slice(idx + 1)
				if (line.startsWith('STATE ')) this.onState(line.slice(6))
				else if (line.startsWith('ERR')) this.log('warn', line)
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
		this.setVariableValues({
			blackout: this.state.blackout ? 'ON' : 'off',
			presenter_step: p.index >= 0 ? String(p.index + 1) : '-',
			presenter_count: String(p.count ?? 0),
			playlist: this.state.playlist ?? '',
			next_cue: this.state.nextCue ?? '',
			stinger: this.state.stingerPlaying ?? '',
			health: this.state.health ?? '',
			machine_cpu: this.state.machine?.cpu >= 0 ? `${this.state.machine.cpu}%` : 'n/a',
			machine_fps: String(this.state.machine?.fps ?? 0),
			machine_power: this.state.machine?.battery ? 'BATTERY' : 'mains',
			machine_advice: String(this.state.machine?.advice ?? 0),
		})
		this.checkFeedbacks('blackout', 'screen_enabled', 'audio_playing', 'stinger_playing')
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
			presenter_next: { name: 'Presenter — next step', options: [], callback: () => send('NEXT') },
			presenter_prev: { name: 'Presenter — previous step', options: [], callback: () => send('PREV') },
			screen: {
				name: 'Screen on/off/toggle',
				options: [
					{ type: 'number', id: 'n', label: 'Screen number (overview order)', default: 1, min: 1, max: 32 },
					{ type: 'dropdown', id: 'mode', label: 'Mode', default: 'TOGGLE',
						choices: [{ id: 'TOGGLE', label: 'Toggle' }, { id: 'ON', label: 'On' }, { id: 'OFF', label: 'Off' }] },
				],
				callback: (a) => send(`SCREEN ${a.options.n} ${a.options.mode}`),
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
			stinger_stop: { name: 'Stop stinger', options: [], callback: () => send('STINGER STOP') },
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
		}
	}

	buildVariables() {
		return [
			{ variableId: 'blackout', name: 'Blackout state' },
			{ variableId: 'presenter_step', name: 'Presenter step number' },
			{ variableId: 'presenter_count', name: 'Presenter step count' },
			{ variableId: 'playlist', name: 'Playlist status' },
			{ variableId: 'next_cue', name: 'Next scheduled cue' },
			{ variableId: 'stinger', name: 'Stinger on air (name)' },
			{ variableId: 'health', name: 'App health line' },
			{ variableId: 'machine_cpu', name: 'Computer CPU load' },
			{ variableId: 'machine_fps', name: 'Output frame rate' },
			{ variableId: 'machine_power', name: 'Power source (mains/BATTERY)' },
			{ variableId: 'machine_advice', name: 'Health suggestions needing attention' },
		]
	}

	buildPresets() {
		const presets = {}
		const white = combineRgb(255, 255, 255)
		const dark = combineRgb(20, 22, 28)

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
				type: 'button', category: 'Looks', name: `Look F${slot}`,
				style: { text: `LOOK\\nF${slot}`, size: '14', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'look_slot', options: { slot } }], up: [] }], feedbacks: [],
			}
		}
		for (let n = 1; n <= 8; n++) {
			presets[`screen_${n}`] = {
				type: 'button', category: 'Screens', name: `Screen ${n} toggle`,
				style: { text: `SCR\\n${n}`, size: '14', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'screen', options: { n, mode: 'TOGGLE' } }], up: [] }],
				feedbacks: [{ feedbackId: 'screen_enabled', options: { n }, style: { bgcolor: combineRgb(0, 120, 60) } }],
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
				type: 'button', category: 'Stingers', name: `Stinger ${n}`,
				style: { text: `STING\\n${n}`, size: '14', color: white, bgcolor: dark },
				steps: [{ down: [{ actionId: 'stinger', options: { n } }], up: [] }],
				feedbacks: [{ feedbackId: 'stinger_playing', options: {}, style: { bgcolor: combineRgb(190, 120, 0) } }],
			}
		}
		presets.stinger_stop = {
			type: 'button', category: 'Stingers', name: 'Stop stinger',
			style: { text: 'STING\\nSTOP', size: '14', color: white, bgcolor: combineRgb(90, 30, 30) },
			steps: [{ down: [{ actionId: 'stinger_stop', options: {} }], up: [] }], feedbacks: [],
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
