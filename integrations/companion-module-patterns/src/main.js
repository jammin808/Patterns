// Bitfocus Companion module for Patterns — the desk's wire (one line per press, STATE pushed on
// every change) as Companion 5 keys: actions, feedbacks in the palette's colours, variables that
// label the banks, presets in sections, and the desk found on the network by itself. Round 74:
// every line sent is matched to its reply (the wire answers every line, in order), so a key can
// ask the desk a question — NAV, MENU PAGE Looks, MENU LOOK Walk-in — and the Navigator lays the
// answer on the slot keys; the desk's actions come back as ACTION lines while Companion records
// a button; a key can learn its line from what the desk does next.
import { InstanceBase, InstanceStatus, TCPHelper } from '@companion-module/base'
import { buildActions } from './actions.js'
import { buildFeedbacks } from './feedbacks.js'
import { buildNavPresets, buildPresets, buildShowPresets, structure } from './presets.js'
import { variableDefinitions } from './variables.js'
import { configFields, connectionTarget, groupEnabled, groupOf } from './config.js'
import { emptyState, showSignature, upcoming, variableValues } from './state.js'
import { KNOWN_PROTOCOL, Navigator, payload } from './nav.js'

/** The module's own version, said on HELLO so the desk's Remote page can show which module a deck runs. */
export const MODULE_VERSION = '3.16.0'

/** How long a question to the desk waits for its answer before the key gives up. */
export const ASK_TIMEOUT_MS = 5000

/** What the state carries that a feedback reads: every boolean feedback is rechecked on every STATE. */
class PatternsInstance extends InstanceBase {
	constructor(internal) {
		super(internal)
		this.socket = null
		this.state = emptyState()
		this.buffer = ''
		this.standbyId = '' // the standby id this instance last saw — every cue_go sends it
		this.showSignature = '' // the show's lists as last turned into presets — rebuilt only when they change
		this.fixed = null // the fixed presets, built once
		this.navInfo = null // the reply to NAV: the rails and the pages, for the Navigator's own presets
		this.pending = [] // round 74: one entry per line sent, in order — a resolver for a question, null for a press
		this.recording = false // round 74: Companion's Action Recorder is open on a button of this connection
		this.lastAction = '' // round 74: the last ACTION line the desk fed
		this.actionWaiters = [] // round 74: a key learning its line waits here for the next ACTION
		/** How the wire is opened — a test hands in a fake; the desk gets a TCPHelper. */
		this.socketFactory = (host, port) => new TCPHelper(host, port)
		this.nav = new Navigator({
			ask: (line) => this.ask(line),
			say: (line) => this.send(line),
			log: (level, text) => this.log(level, text),
			changed: (nav) => this.onNavChanged(nav),
		})
		this.nav.onProtocol = (version) => this.onProtocol(version)
	}

	/**
	 * Round 80: the desk's descriptor version, read from its NAV and MENU replies. A newer desk is said on the
	 * connection (a warning, not a fault: the keys keep working on what this module understands) and in
	 * `desk_protocol`; the same or an older version is a fact in the variable alone.
	 */
	onProtocol(version) {
		this.setVariableValues({ desk_protocol: String(version) })
		if (version > KNOWN_PROTOCOL) {
			this.log('warn', `The desk speaks descriptor protocol ${version}; this module knows ${KNOWN_PROTOCOL} — update the module for what is new`)
			this.updateStatus(InstanceStatus.UnknownWarning, `Desk protocol ${version}, this module knows ${KNOWN_PROTOCOL} — update the module`)
		}
	}

	async init(config) {
		this.config = config
		this.updateStatus(InstanceStatus.Connecting)
		const ctx = {
			send: (line) => this.send(line),
			ask: (line) => this.ask(line),
			log: (level, text) => this.log(level, text),
			state: () => this.state,
			standbyId: () => this.standbyId,
			upcoming: () => upcoming(this.state),
			nav: () => this.nav,
			recording: () => this.recording,
			nextAction: (signal) => this.nextAction(signal),
		}
		this.setActionDefinitions(buildActions(ctx))
		this.setFeedbackDefinitions(buildFeedbacks(ctx))
		this.setVariableDefinitions(variableDefinitions())
		this.setVariableValues({ ...variableValues(this.state), ...this.nav.variables() })
		this.refreshPresets()
		this.initSocket()
	}

	async destroy() {
		if (this.socket) {
			this.socket.destroy()
			this.socket = null
		}
		this.dropPending('the connection closed')
	}

	async configUpdated(config) {
		this.config = config
		this.initSocket()
		this.refreshPresets() // the ticked groups may have changed
	}

	getConfigFields() {
		return configFields()
	}

	// ---- presets: the fixed ones, the show's own and the Navigator's, through the ticked groups --------------

	/** The fixed presets, the show's own and the Navigator's rails and pages, in their sections, through the ticked groups. */
	refreshPresets() {
		this.fixed ??= buildPresets()
		const show = buildShowPresets(this.state)
		const nav = buildNavPresets(this.navInfo)
		const presets = { ...this.fixed.presets, ...show.presets, ...nav.presets }
		const categories = { ...this.fixed.categories, ...show.categories, ...nav.categories }
		const sections = structure(categories, (category) => groupEnabled(this.config, groupOf(category)))
		const kept = {}
		for (const section of sections) for (const id of section.definitions) kept[id] = presets[id]
		this.setPresetDefinitions(sections, kept)
	}

	/** Presets built from the show itself, rebuilt when the lists change. */
	refreshShowPresets() {
		const signature = showSignature(this.state)
		if (signature === this.showSignature) return
		this.showSignature = signature
		this.refreshPresets()
	}

	// ---- the wire -----------------------------------------------------------------------------------------------

	initSocket() {
		if (this.socket) {
			this.socket.destroy()
			this.socket = null
		}
		const target = connectionTarget(this.config)
		if (!target) {
			this.updateStatus(InstanceStatus.BadConfig, 'Pick the desk on the network, or type its address')
			return
		}
		this.buffer = ''
		this.dropPending('the connection was reopened')
		this.socket = this.socketFactory(target.host, target.port)
		this.socket.on('status_change', (status, message) => {
			this.updateStatus(status, message)
			if (status === InstanceStatus.Ok) {
				// The connection names itself and its module, so the caller's history reads "GO from FOH deck"
				// and the desk's Remote page can say which module version each deck runs.
				this.send(`HELLO ${this.label ?? 'Companion'} module=${MODULE_VERSION}`)
				// A desk with a pairing token (Remote page, TRUST) runs a verb only from a connection that
				// presented it: AUTH follows HELLO with the token from the config, and nothing is sent without one.
				const token = pairingToken(this.config)
				if (token) this.send(`AUTH ${token}`)
				// Round 74: the rails and the pages, for the Navigator and its presets; the recorder re-armed after a reconnect.
				this.ask('NAV').then((reply) => this.onNav(reply)).catch(() => {})
				if (this.recording) this.send('RECORD ON')
			} else {
				// Feedbacks reset on disconnect: a dead key must not stay green.
				this.state = emptyState()
				this.standbyId = ''
				this.dropPending('the connection dropped')
				this.setVariableValues(variableValues(this.state))
				this.checkAllFeedbacks()
			}
		})
		this.socket.on('error', (err) => this.log('error', `Connection error: ${err.message}`))
		this.socket.on('data', (data) => this.onData(data.toString('utf8')))
	}

	/** Bytes from the desk: whole lines, STATE and ERR read, OK and ERR matched to the line they answer, ACTION fed to the recorder. */
	onData(text) {
		this.buffer += text
		let idx
		while ((idx = this.buffer.indexOf('\n')) >= 0) {
			const line = this.buffer.slice(0, idx).trim()
			this.buffer = this.buffer.slice(idx + 1)
			this.onLine(line)
		}
		if (this.buffer.length > 4_000_000) this.buffer = '' // a line that never ends is not the desk's
	}

	onLine(line) {
		if (line.startsWith('STATE ')) return this.onState(line.slice(6))
		if (line.startsWith('ACTION ')) return this.onAction(line.slice(7).trim())
		if (line.startsWith('ERR')) {
			this.log('warn', line)
			this.setVariableValues({ last_error: line.slice(4).trim() })
			const trust = trustProblem(line)
			if (trust) this.updateStatus(InstanceStatus.BadConfig, trust) // the connection is up, the desk will not run a verb from it: the config is what needs a hand
		}
		if (line.startsWith('OK') || line.startsWith('ERR')) this.answer(line)
	}

	onState(json) {
		let parsed
		try {
			parsed = JSON.parse(json)
		} catch (e) {
			this.log('warn', `Bad STATE payload: ${e.message}`)
			return
		}
		this.state = parsed && typeof parsed === 'object' ? parsed : emptyState()
		this.standbyId = this.state.cuestack?.standby?.id ?? ''
		this.setVariableValues({ ...variableValues(this.state), ...this.nav.variables() })
		this.checkAllFeedbacks()
		this.refreshShowPresets()
		// Round 74: the desk moved — with FOLLOW on, the Navigator turns with it.
		const nav = this.state.nav
		if (nav && typeof nav === 'object') this.nav.deskMoved(nav.page ?? '', nav.run === true).catch(() => {})
	}

	/** The reply to NAV: the Navigator learns the desk's rails and pages, and gets its presets for them. */
	onNav(reply) {
		const info = payload(reply)
		if (!info) return
		this.navInfo = info
		this.nav.learnDesk(info)
		this.refreshPresets()
	}

	/** The Navigator changed: its variables, its feedbacks, and the deck's whereabouts told to the desk. */
	onNavChanged(nav) {
		this.setVariableValues(nav.variables())
		this.checkFeedbacks('nav_slot_on', 'nav_slot_empty', 'nav_slot_tone_is', 'nav_slot_drawer', 'nav_slot_disabled', 'nav_level_is', 'nav_mode_run', 'nav_follow_on', 'nav_rail_is', 'nav_page_is')
		const where = nav.where
		if (where !== this.told && this.socket?.isConnected) {
			this.told = where
			this.send(`NAV DECK ${where}`)
		}
	}

	send(cmd) {
		if (this.socket?.isConnected) {
			this.pending.push(null) // every line the desk hears is answered once, in order: a press takes its place in the queue
			this.socket.send(cmd + '\n')
		} else this.log('warn', `Not connected — dropped: ${cmd}`)
	}

	/** A line the caller wants the answer to: resolved with the desk's OK … or ERR … reply, in order; rejected on a timeout or a drop. */
	ask(cmd) {
		if (!this.socket?.isConnected) return Promise.reject(new Error(`Not connected — dropped: ${cmd}`))
		return new Promise((resolve, reject) => {
			const entry = { resolve, reject, timer: null }
			entry.timer = setTimeout(() => {
				const at = this.pending.indexOf(entry)
				if (at >= 0) this.pending[at] = null // its reply, when it comes, keeps the order
				reject(new Error(`No answer to ${cmd} in ${ASK_TIMEOUT_MS} ms`))
			}, ASK_TIMEOUT_MS)
			entry.timer.unref?.()
			this.pending.push(entry)
			this.socket.send(cmd + '\n')
		})
	}

	/** The desk's reply: the oldest line waiting gets it. */
	answer(line) {
		const entry = this.pending.shift()
		if (!entry) return
		clearTimeout(entry.timer)
		entry.resolve(line)
	}

	dropPending(why) {
		const waiting = this.pending
		this.pending = []
		for (const entry of waiting) {
			if (!entry) continue
			clearTimeout(entry.timer)
			entry.reject(new Error(why))
		}
		for (const w of this.actionWaiters.splice(0)) w.reject(new Error(why))
		this.told = ''
	}

	// ---- round 74: the recorder and learn ---------------------------------------------------------------------

	/** Companion's Action Recorder opened or closed on a button of this connection: the desk feeds its actions as ACTION lines meanwhile. */
	handleStartStopRecordActions(isRecording) {
		this.recording = !!isRecording
		this.send(this.recording ? 'RECORD ON' : 'RECORD OFF')
		this.checkFeedbacks('nav_recording')
	}

	/** An ACTION line: the desk did something, written as the line that reproduces it — into the button being recorded, and to any key learning its line. */
	onAction(line) {
		if (line.length === 0) return
		this.lastAction = line
		this.setVariableValues({ last_action: line })
		if (this.recording) this.recordAction({ actionId: 'raw', options: { line } }, line)
		for (const w of this.actionWaiters.splice(0)) w.resolve(line)
	}

	/** The next ACTION line the desk feeds — a key learning its line asks the desk to record meanwhile; null when the learn is abandoned. */
	nextAction(signal) {
		const wasRecording = this.recording
		if (!wasRecording) this.send('RECORD ON')
		return new Promise((resolve) => {
			const waiter = {
				resolve: (line) => { finish(); resolve(line) },
				reject: () => { finish(); resolve(null) },
			}
			const finish = () => {
				const at = this.actionWaiters.indexOf(waiter)
				if (at >= 0) this.actionWaiters.splice(at, 1)
				if (!wasRecording && this.actionWaiters.length === 0 && !this.recording) this.send('RECORD OFF')
			}
			this.actionWaiters.push(waiter)
			signal?.addEventListener?.('abort', () => waiter.reject(), { once: true })
		})
	}
}

export default PatternsInstance

/** The pairing token as the wire carries it: trimmed, or '' when the config has none. */
export function pairingToken(config) {
	return String(config?.token ?? '').trim()
}

/**
 * The words for Companion's status when the desk refuses the connection's verbs over trust: the
 * token is wrong, or the desk asks for one and the config has none. Null for every other ERR.
 */
export function trustProblem(errLine) {
	const text = String(errLine ?? '')
	if (/^ERR\s+wrong token/i.test(text)) return "The pairing token is not this desk's — Remote page, TRUST, on the desk"
	if (/^ERR\s+not paired/i.test(text)) return 'This desk asks for its pairing token — type it into this connection (Remote page, TRUST, on the desk)'
	return null
}

/** Upgrade scripts, oldest first; none yet — every id of 1.x, 2.x and 3.x is kept as it was. */
export const UpgradeScripts = []
